using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure;
using Azure.AI.Vision.ImageAnalysis;
using Azure.Communication.Email;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace TestFunction;

public class BackgroundWorker
{
    private readonly ILogger _logger;

    public BackgroundWorker(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<BackgroundWorker>();
    }

    // Same connection strings as the website (appsettings.json).
    private static string SqlConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is not configured.");

    private static string StorageConnectionString =>
        Environment.GetEnvironmentVariable("AzureStorage__ConnectionString")
            ?? throw new InvalidOperationException("AzureStorage__ConnectionString is not configured.");

    private static string StorageContainerName =>
        Environment.GetEnvironmentVariable("AzureStorage__ContainerName") ?? "uploads";

    // Runs every morning at 5:00 AM. Checks the ExpiryDate column in the
    // Documents table and sends a collated email for entries expiring within a week.
   // [Function("ExpiryCheck")]
    public async Task ExpiryCheck([TimerTrigger("0 0 5 * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("Expiry check executed at: {executionTime}", DateTime.UtcNow);

        var expiring = new StringBuilder();
        var count = 0;

        await using var connection = new SqlConnection(SqlConnectionString);
        await connection.OpenAsync();

        const string query = """
            SELECT Name, DocumentType, ExpiryDate
            FROM dbo.Documents
            WHERE ExpiryDate >= SYSDATETIMEOFFSET()
              AND ExpiryDate <= DATEADD(day, 7, SYSDATETIMEOFFSET())
            ORDER BY ExpiryDate;
            """;

        await using (var command = new SqlCommand(query, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var docType = reader.IsDBNull(1) ? "Unknown" : reader.GetString(1);
                var expiryDate = reader.GetDateTimeOffset(2);

                expiring.AppendLine($"- {name} ({docType}) expires on {expiryDate:yyyy-MM-dd}");
                count++;
            }
        }

        if (count == 0)
        {
            _logger.LogInformation("No certificates expiring in the next 7 days.");
            return;
        }

        await SendExpiryEmailAsync(count, expiring.ToString());
    }

    // Runs every 5 minutes. Scans the uploads container for blobs that have not
    // yet been recorded in SQL, validates each file, extracts certificate
    // information, inserts it into SQL, and flags it for admin review (IsValid = 0).
    [Function("ProcessUploadedBlob")]
    public async Task ProcessUploadedBlob([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer)
    {
        _logger.LogInformation("Blob processing sweep executed at: {executionTime}", DateTime.UtcNow);

        var containerClient = new BlobContainerClient(StorageConnectionString, StorageContainerName);
        if (!await containerClient.ExistsAsync())
        {
            _logger.LogWarning("Container {container} does not exist; nothing to process.", StorageContainerName);
            return;
        }

        var processed = await GetProcessedBlobNamesAsync();

        await foreach (var blobItem in containerClient.GetBlobsAsync())
        {
            if (processed.Contains(blobItem.Name))
            {
                continue;
            }

            try
            {
                using var blobStream = new MemoryStream();
                await containerClient.GetBlobClient(blobItem.Name).DownloadToAsync(blobStream);
                blobStream.Position = 0;

                await ProcessBlobAsync(blobStream, blobItem.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process blob {blobName}.", blobItem.Name);
            }
        }
    }

    private async Task<HashSet<string>> GetProcessedBlobNamesAsync()
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(SqlConnectionString);
        await connection.OpenAsync();

        const string query = "SELECT BlobName FROM dbo.Documents WHERE BlobName IS NOT NULL;";
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            processed.Add(reader.GetString(0));
        }

        return processed;
    }

    // Validates the file, extracts certificate information, inserts it into
    // SQL, and flags it for admin review (IsValid = 0).
    private async Task ProcessBlobAsync(Stream blobStream, string name)
    {
        _logger.LogInformation("Processing uploaded blob: {blobName}", name);

        var extension = Path.GetExtension(name);
        var isPdf = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var isImage = SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

        if (!isPdf && !isImage)
        {
            _logger.LogWarning("Blob {blobName} is not a supported document type, skipping.", name);
            return;
        }

        // Both PdfPig and the Vision SDK need a seekable stream, so buffer the blob into memory.
        using var memoryStream = new MemoryStream();
        await blobStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        // Basic malicious-content validation before parsing.
        if (!IsFileSafe(memoryStream, isPdf, name))
        {
            _logger.LogWarning("Blob {blobName} failed the safety validation and will not be processed.", name);
            return;
        }
        memoryStream.Position = 0;

        // Staff-bound upload links place blobs under "staff/{id}/..." so the
        // document can be associated with the staff member deterministically.
        var pathStaffId = TryGetStaffIdFromPath(name);

        DocumentInfo info;
        try
        {
            var text = isPdf
                ? ExtractTextFromPdf(memoryStream)
                : await ExtractTextFromImageAsync(memoryStream);

            // Scanned PDFs often contain no extractable text; render the pages
            // to images and fall back to OCR (the Vision API rejects raw PDFs).
            if (isPdf && string.IsNullOrWhiteSpace(text))
            {
                _logger.LogInformation("No text extracted from PDF {blobName}; falling back to OCR.", name);
                memoryStream.Position = 0;
                text = await ExtractTextFromPdfViaOcrAsync(memoryStream);
            }

            info = ExtractDocumentInfo(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from {blobName}.", name);
            // Still record the document, flagged as ParseFailed, so the admin
            // sees it and can review the issue.
            await InsertDocumentAsync(
                new DocumentInfo(null, null, null, null, null, null, null), name, pathStaffId, DocumentStatusParseFailed);
            return;
        }

        _logger.LogInformation(
            "Extracted from {blobName} - Name: {docName}, StartDate: {startDate}, EndDate: {endDate}, DocumentType: {docType}",
            name, info.Name, info.StartDate, info.EndDate, info.DocumentType);

        // Insert into SQL flagged for admin review (IsValid = 0).
        await InsertDocumentAsync(info, name, pathStaffId, DocumentStatusPendingReview);
    }

    private const int DocumentStatusPendingReview = 0;
    private const int DocumentStatusRejected = 2;
    private const int DocumentStatusParseFailed = 3;

    private static int? TryGetStaffIdFromPath(string blobName)
    {
        // Accept both the new "staff/{id}/" and legacy "workers/{id}/" prefixes.
        var match = Regex.Match(blobName, @"^(?:staff|workers)/(\d+)/", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private async Task InsertDocumentAsync(DocumentInfo info, string blobName, int? pathStaffId, int status)
    {
        await using var connection = new SqlConnection(SqlConnectionString);
        await connection.OpenAsync();

        // Prefer the staff id carried in the blob path (deterministic, from the
        // signed upload link). Otherwise match identifiers extracted from the
        // document, most reliable first: email -> phone -> full name.
        // NULL if no unambiguous match exists.
        var staffId = pathStaffId
            ?? await MatchStaffAsync(connection, blobName,
                    "Email = @Value", info.Email)
            ?? await MatchStaffAsync(connection, blobName,
                    "REPLACE(REPLACE(REPLACE(REPLACE(PhoneNumber, ' ', ''), '-', ''), '(', ''), ')', '') = @Value", info.Phone)
            ?? await MatchStaffAsync(connection, blobName,
                    "CONCAT(FirstName, ' ', LastName) = @Value", info.Name?.Trim());

        const string insert = """
            INSERT INTO dbo.Documents (Name, BlobName, ExtractedName, Email, Phone, DocumentType, DocumentNumber, StartDate, ExpiryDate, IsValid, Status, StaffId)
            VALUES (@Name, @BlobName, @ExtractedName, @Email, @Phone, @DocumentType, @DocumentNumber, @StartDate, @ExpiryDate, 0, @Status, @StaffId);
            """;

        await using var command = new SqlCommand(insert, connection);
        command.Parameters.AddWithValue("@Name", blobName);
        command.Parameters.AddWithValue("@BlobName", blobName);
        command.Parameters.AddWithValue("@ExtractedName", (object?)info.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("@Email", (object?)info.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("@Phone", (object?)info.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("@DocumentType", (object?)info.DocumentType ?? DBNull.Value);
        command.Parameters.AddWithValue("@DocumentNumber", (object?)info.DocumentNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@StartDate", (object?)info.StartDate ?? DBNull.Value);
        command.Parameters.AddWithValue("@ExpiryDate", (object?)info.EndDate ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", status);
        command.Parameters.AddWithValue("@StaffId", (object?)staffId ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Inserted document record for {blobName}, flagged for admin review.", blobName);
    }

    // Returns the staff id when exactly one staff member matches the predicate;
    // null when there is no match or the match is ambiguous.
    private async Task<int?> MatchStaffAsync(SqlConnection connection, string blobName, string predicate, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        await using var command = new SqlCommand($"SELECT Id FROM dbo.Staff WHERE {predicate};", connection);
        command.Parameters.AddWithValue("@Value", value);

        var ids = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt32(0));
            }
        }

        if (ids.Count > 1)
        {
            _logger.LogWarning("Multiple staff members match {value}; skipping this identifier for {blobName}.", value, blobName);
            return null;
        }

        return ids.Count == 1 ? ids[0] : null;
    }

    // Rejects files containing common malicious-content markers (embedded
    // JavaScript / launch actions in PDFs, script tags or executable headers
    // masquerading as documents).
    private bool IsFileSafe(MemoryStream stream, bool isPdf, string name)
    {
        var bytes = stream.ToArray();

        // Executable masquerading as a document ("MZ" header).
        if (bytes.Length >= 2 && bytes[0] == 0x4D && bytes[1] == 0x5A)
        {
            _logger.LogWarning("Blob {blobName} has an executable header.", name);
            return false;
        }

        if (isPdf)
        {
            var content = Encoding.Latin1.GetString(bytes);
            string[] dangerousMarkers = ["/JavaScript", "/JS", "/Launch", "/EmbeddedFile", "/OpenAction", "/AA"];
            foreach (var marker in dangerousMarkers)
            {
                if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Blob {blobName} contains suspicious PDF marker {marker}.", name, marker);
                    return false;
                }
            }
        }

        return true;
    }

    private async Task SendExpiryEmailAsync(int count, string details)
    {
        var emailClient = new EmailClient(Environment.GetEnvironmentVariable("EmailConnection"));

        var message = new EmailMessage(
            senderAddress: Environment.GetEnvironmentVariable("EmailSender"),
            recipientAddress: Environment.GetEnvironmentVariable("EmailRecipient"),
            content: new EmailContent($"{count} certificate(s) expiring within 7 days")
            {
                PlainText = $"The following certificates expire within the next week:\n\n{details}\nPlease take action."
            });

        await emailClient.SendAsync(Azure.WaitUntil.Started, message);
        _logger.LogInformation("Collated expiry notification email sent for {count} certificate(s).", count);
    }

    private static readonly string[] SupportedImageExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"];

    private static string ExtractTextFromPdf(Stream pdfStream)
    {
        var textBuilder = new StringBuilder();
        using (var document = PdfDocument.Open(pdfStream))
        {
            foreach (var page in document.GetPages())
            {
                textBuilder.AppendLine(page.Text);
            }
        }

        return textBuilder.ToString();
    }

    // Renders each PDF page to a PNG and runs it through Azure AI Vision OCR,
    // since the Image Analysis API does not accept PDF input directly.
    private static async Task<string> ExtractTextFromPdfViaOcrAsync(Stream pdfStream)
    {
        var textBuilder = new StringBuilder();

        foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdfStream, leaveOpen: true))
        {
            using (bitmap)
            using (var pageImage = new MemoryStream())
            {
                bitmap.Encode(pageImage, SkiaSharp.SKEncodedImageFormat.Png, 100);
                pageImage.Position = 0;
                textBuilder.AppendLine(await ExtractTextFromImageAsync(pageImage));
            }
        }

        return textBuilder.ToString();
    }

    // Uses Azure AI Vision OCR (Read) to extract text from image documents.
    private static async Task<string> ExtractTextFromImageAsync(Stream imageStream)
    {
        var client = new ImageAnalysisClient(
            new Uri(Environment.GetEnvironmentVariable("VisionEndpoint")
                ?? throw new InvalidOperationException("VisionEndpoint is not configured.")),
            new AzureKeyCredential(Environment.GetEnvironmentVariable("VisionKey")
                ?? throw new InvalidOperationException("VisionKey is not configured.")));

        var result = await client.AnalyzeAsync(BinaryData.FromStream(imageStream), VisualFeatures.Read);

        var textBuilder = new StringBuilder();
        if (result.Value.Read is not null)
        {
            foreach (var block in result.Value.Read.Blocks)
            {
                foreach (var line in block.Lines)
                {
                    textBuilder.AppendLine(line.Text);
                }
            }
        }

        return textBuilder.ToString();
    }

    private static DocumentInfo ExtractDocumentInfo(string text)
    {
        return new DocumentInfo(
            Name: ExtractField(text, "Name"),
            StartDate: ExtractDateField(text, "Start Date"),
            EndDate: ExtractDateField(text, "End Date"),
            DocumentType: ExtractField(text, "Document Type"),
            DocumentNumber: ExtractField(text, "Document Number") ?? ExtractField(text, "Certificate Number"),
            Email: ExtractEmail(text),
            Phone: ExtractPhone(text));
    }

    private static string? ExtractEmail(string text)
    {
        var match = Regex.Match(text, @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}");
        return match.Success ? match.Value : null;
    }

    private static string? ExtractPhone(string text)
    {
        // Prefer a labelled phone field; fall back to the first phone-shaped token.
        var value = ExtractField(text, "Phone") ?? ExtractField(text, "Phone Number") ?? ExtractField(text, "Tel");
        var candidate = value ?? text;
        var match = Regex.Match(candidate, @"\+?\d[\d\s\-\(\)]{7,}\d");
        return match.Success ? NormalizePhone(match.Value) : null;
    }

    // Strips separators so phone comparison is format-insensitive.
    private static string NormalizePhone(string phone) =>
        Regex.Replace(phone, @"[^\d+]", "");

    // Matches labelled fields such as "Name: John Smith" in the extracted PDF text.
    private static string? ExtractField(string text, string label)
    {
        var match = Regex.Match(
            text,
            $@"{Regex.Escape(label)}\s*[:\-]\s*(.+?)(?=(?:\r?\n)|(?:\s{{2,}})|$)",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static DateTime? ExtractDateField(string text, string label)
    {
        var value = ExtractField(text, label);
        if (value is null)
        {
            return null;
        }

        // Take only the leading date portion in case other text follows on the same line.
        var dateMatch = Regex.Match(value, @"\d{1,4}[/\-\.]\d{1,2}[/\-\.]\d{1,4}|\d{1,2}\s+\w+\s+\d{4}|\w+\s+\d{1,2},?\s+\d{4}");
        var candidate = dateMatch.Success ? dateMatch.Value : value;

        return DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private record DocumentInfo(
        string? Name, DateTime? StartDate, DateTime? EndDate, string? DocumentType,
        string? DocumentNumber, string? Email = null, string? Phone = null);
}