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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;

namespace TestFunction;

public class BackgroundWorker(AppDbContext database, BlobServiceClient storage, IEmailService email,
    IConfiguration configuration, TimeProvider clock, ILogger<BackgroundWorker> logger)
{
    private readonly ILogger _logger = logger;

    [Function("ExpiryCheck")]
    [FixedDelayRetry(3, "00:05:00")]
    public async Task ExpiryCheck([TimerTrigger("0 0 5 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var documents = await database.Documents.AsNoTracking().Include(document => document.Staff).Include(document => document.Type)
            .Where(document => document.Staff != null && !document.Staff.IsArchived && document.IsValid && document.Status == DocumentStatus.Validated)
            .ToListAsync(cancellationToken);
        var expiring = documents.Where(document => document.ExpiryDate is not null
            && document.ExpiryDate.Value.UtcDateTime.Date >= now.UtcDateTime.Date
            && document.ExpiryDate.Value.UtcDateTime.Date <= now.UtcDateTime.Date.AddDays(7)
            && !documents.Any(replacement => IsReplacement(document, replacement))).ToList();
        if (expiring.Count == 0) return;
        var recipients = await database.Users.Where(user => user.IsEnabled && user.Role!.Name == "HR").ToListAsync(cancellationToken);
        var text = string.Join("\n", expiring.Select(document =>
            $"{document.Staff!.FirstName} {document.Staff.LastName}: {document.Type?.Name ?? document.DocumentType ?? "Unknown"}, expires {document.ExpiryDate:yyyy-MM-dd}"));
        foreach (var recipient in recipients)
        {
            var key = $"expiry:{now:yyyyMMdd}:{recipient.Id}";
            var dispatch = await database.EmailDispatches.FindAsync([key], cancellationToken);
            if (dispatch is { SentAt: not null } || dispatch?.LeaseUntil > now) continue;
            if (dispatch is null) { dispatch = new EmailDispatch { Id = key }; database.EmailDispatches.Add(dispatch); }
            dispatch.LeaseUntil = now.AddMinutes(15);
            await database.SaveChangesAsync(cancellationToken);
            try { await email.SendAsync(recipient.Email, "Certificates expiring within seven days", text, cancellationToken); }
            catch
            {
                dispatch.LeaseUntil = clock.GetUtcNow();
                await database.SaveChangesAsync(cancellationToken);
                throw;
            }
            dispatch.SentAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    public static bool IsReplacement(DocumentEntry expiring, DocumentEntry candidate) => expiring.Id != candidate.Id
        && expiring.StaffId != null && expiring.DocumentTypeId != null && candidate.StaffId == expiring.StaffId
        && candidate.DocumentTypeId == expiring.DocumentTypeId && candidate.IsValid
        && candidate.Status == DocumentStatus.Validated && !candidate.IsArchived && expiring.ExpiryDate is not null
        && (candidate.StartDate is null || candidate.StartDate.Value.UtcDateTime.Date <= expiring.ExpiryDate.Value.UtcDateTime.Date.AddDays(1))
        && (candidate.ExpiryDate is null || candidate.ExpiryDate.Value.UtcDateTime.Date > expiring.ExpiryDate.Value.UtcDateTime.Date);

    [Function("ProcessUploadedBlob")]
    public async Task ProcessUploadedBlob([BlobTrigger("%UploadsContainer%/{name}", Connection = "ProcessingStorage")] Stream content,
        string name, CancellationToken cancellationToken)
    {
        var container = configuration["UploadsContainer"]!;
        var document = await database.Documents.IgnoreQueryFilters().SingleOrDefaultAsync(document => document.ContainerName == container && document.BlobName == name, cancellationToken);
        if (document is { IsArchived: true } or { Status: DocumentStatus.Unsafe }) return;
        if (document is null)
        {
            var reservation = await database.ShareLinks.SingleOrDefaultAsync(link => link.ContainerName == container && link.BlobName == name, cancellationToken);
            if (reservation is null)
            {
                var identifiers = UploadNaming.Parse(name);
                var staffId = identifiers.StaffId is not null && await database.Staff.AnyAsync(staff => staff.Id == identifiers.StaffId, cancellationToken) ? identifiers.StaffId : null;
                var typeId = identifiers.DocumentTypeId is not null && await database.DocumentTypes.AnyAsync(type => type.Id == identifiers.DocumentTypeId, cancellationToken) ? identifiers.DocumentTypeId : null;
                document = new DocumentEntry { Name = Path.GetFileName(name), BlobName = name, ContainerName = container,
                    StaffId = staffId, DocumentTypeId = typeId, Status = DocumentStatus.AwaitingProcessing, Issue = "Awaiting file checks and extraction." };
                database.Documents.Add(document);
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        if (document is not null) await ProcessDocumentAsync(document, cancellationToken);
    }

    [Function("RetryPendingUploads")]
    public async Task RetryPendingUploads([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var reservations = await database.ShareLinks.Where(link => link.Purpose == "upload" && link.BlobName != null && link.UsedAt == null && link.RevokedAt == null)
            .OrderBy(link => link.LastUploadCheck).Take(100).ToListAsync(cancellationToken);
        foreach (var link in reservations)
        {
            link.LastUploadCheck = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            var blob = storage.GetBlobContainerClient(link.ContainerName).GetBlobClient(link.BlobName);
            if (!await blob.ExistsAsync(cancellationToken)) continue;
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (!properties.Value.Metadata.TryGetValue("sha256", out var hash) || hash != link.ContentHash) continue;
            if (!await database.Documents.IgnoreQueryFilters().AnyAsync(document => document.ContainerName == link.ContainerName && document.BlobName == link.BlobName, cancellationToken))
                database.Documents.Add(new() { ContainerName = link.ContainerName, BlobName = link.BlobName, Name = Path.GetFileName(link.BlobName!),
                    StaffId = link.StaffId, DocumentTypeId = link.DocumentTypeId, Status = DocumentStatus.AwaitingProcessing });
            link.UsedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
        }
        var pending = await database.Documents.Where(document => document.ProcessingCompletedAt == null && document.ContainerName != null && document.Status != DocumentStatus.Unsafe)
            .OrderBy(document => document.LastProcessingAttempt).Select(document => document.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var documentId in pending)
        {
            var document = await database.Documents.SingleOrDefaultAsync(document => document.Id == documentId, cancellationToken);
            if (document is null) continue;
            try { await ProcessDocumentAsync(document, cancellationToken); }
            catch (DbUpdateConcurrencyException) { database.ChangeTracker.Clear(); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { _logger.LogError(exception, "Processing failed for document {DocumentId}.", document.Id); }
        }
    }

    private async Task ProcessDocumentAsync(DocumentEntry document, CancellationToken cancellationToken)
    {
        if (document.ProcessingCompletedAt is not null || document.IsArchived || document.Status == DocumentStatus.Unsafe) return;
        var wasRejected = document.Status == DocumentStatus.Rejected;
        document.LastProcessingAttempt = clock.GetUtcNow();
        if (!wasRejected) document.Status = DocumentStatus.AwaitingProcessing;
        document.Issue = "Awaiting file checks and extraction.";
        await database.SaveChangesAsync(cancellationToken);
        var blob = storage.GetBlobContainerClient(document.ContainerName).GetBlobClient(document.BlobName);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.ContentLength > UploadService.MaximumBytes)
        {
            document.Status = DocumentStatus.Unsafe;
            document.IsValid = false;
            document.Issue = "File exceeds the processing limit.";
            document.ProcessingCompletedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        using var memoryStream = new MemoryStream();
        await blob.DownloadToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        var extension = Path.GetExtension(document.BlobName!);
        var isPdf = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var isImage = SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        if ((!isPdf && !isImage) || !IsFileSafe(memoryStream, isPdf, document.BlobName!))
        {
            document.Status = DocumentStatus.Unsafe;
            document.IsValid = false;
            document.Issue = "Unsupported file or unsafe content.";
            document.ProcessingCompletedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            return;
        }
        memoryStream.Position = 0;
        try
        {
            var text = isPdf
                ? ExtractTextFromPdf(memoryStream)
                : await ExtractTextFromImageAsync(memoryStream);
            var info = ExtractDocumentInfo(text);
            if (isPdf && (string.IsNullOrWhiteSpace(text) || info == new DocumentInfo(null, null, null, null, null)))
            {
                memoryStream.Position = 0;
                text = await ExtractTextFromPdfViaOcrAsync(memoryStream);
                info = ExtractDocumentInfo(text);
            }
            document.ExtractedName = Limit(info.Name, 256);
            document.Email = Limit(info.Email, 256);
            document.Phone = Limit(info.Phone, 64);
            document.DocumentNumber = Limit(info.DocumentNumber, 128);
            document.DocumentType = Limit(info.DocumentType, 128);
            document.StartDate = info.StartDate is null ? null : new DateTimeOffset(DateTime.SpecifyKind(info.StartDate.Value, DateTimeKind.Utc));
            document.ExpiryDate = info.EndDate is null ? null : new DateTimeOffset(DateTime.SpecifyKind(info.EndDate.Value, DateTimeKind.Utc));
            var types = await database.DocumentTypes.AsNoTracking().ToListAsync(cancellationToken);
            var typeMatch = DocumentTypeMatcher.Match(text, info.DocumentType, document.DocumentTypeId, types);
            document.DocumentTypeId = typeMatch.TypeId;
            if (document.StaffId is null)
            {
                var matches = info.Email is null ? [] : await database.Staff.Where(staff => staff.Email == info.Email).Select(staff => staff.Id).Take(2).ToListAsync(cancellationToken);
                if (matches.Count == 0 && info.Phone is not null)
                    matches = await database.Staff.Where(staff => staff.PhoneNumber != null
                        && staff.PhoneNumber.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "") == info.Phone)
                        .Select(staff => staff.Id).Take(2).ToListAsync(cancellationToken);
                if (matches.Count == 0 && info.Name is not null)
                    matches = await database.Staff.Where(staff => staff.FirstName + " " + staff.LastName == info.Name).Select(staff => staff.Id).Take(2).ToListAsync(cancellationToken);
                if (matches.Count == 1) document.StaffId = matches[0];
            }
            if (string.IsNullOrWhiteSpace(text) || document.StartDate > document.ExpiryDate)
                throw new InvalidDataException("No readable text or inconsistent dates.");
            document.Status = DocumentStatus.PendingReview;
            var issues = new List<string>();
            if (typeMatch.Issue is not null) issues.Add(typeMatch.Issue);
            if (document.StaffId is null) issues.Add("Staff could not be matched; manual association required.");
            document.Issue = issues.Count == 0 ? null : string.Join(" ", issues);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Extraction failed for document {DocumentId}.", document.Id);
            document.Status = DocumentStatus.ParseFailed;
            document.Issue = "Extraction failed. Review the file and enter its details manually.";
        }
        if (wasRejected) document.Status = DocumentStatus.Rejected;
        document.IsValid = false;
        document.ProcessingCompletedAt = clock.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
    }

    private static string? Limit(string? text, int maximum) => text?.Length > maximum ? text[..maximum] : text;

    // Rejects files containing common malicious-content markers (embedded
    // JavaScript / launch actions in PDFs, script tags or executable headers
    // masquerading as documents).
    private bool IsFileSafe(MemoryStream stream, bool isPdf, string name)
    {
        var bytes = stream.ToArray();
        if (isPdf && (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")) return false;
        if (!isPdf)
        {
            using var imageData = SkiaSharp.SKData.CreateCopy(bytes);
            using var codec = SkiaSharp.SKCodec.Create(imageData);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > 40000000) return false;
            stream.Position = 0;
        }

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

    private static readonly string[] SupportedImageExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"];

    private static string ExtractTextFromPdf(Stream pdfStream)
    {
        var textBuilder = new StringBuilder();
        using (var document = PdfDocument.Open(pdfStream))
        {
            if (document.NumberOfPages > 50) throw new InvalidDataException("PDF exceeds 50 pages.");
            foreach (var page in document.GetPages())
            {
                if (page.Width > 1440 || page.Height > 1440) throw new InvalidDataException("PDF page dimensions exceed the processing limit.");
                textBuilder.AppendLine(page.Text);
            }
        }

        return textBuilder.ToString();
    }

    // Renders each PDF page to a PNG and runs it through Azure AI Vision OCR,
    // since the Image Analysis API does not accept PDF input directly.
    private async Task<string> ExtractTextFromPdfViaOcrAsync(Stream pdfStream)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("PDF rendering is supported on Windows and Linux hosts.");
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
    private async Task<string> ExtractTextFromImageAsync(Stream imageStream)
    {
        var client = new ImageAnalysisClient(
            new Uri(configuration["Vision:Endpoint"] ?? throw new InvalidOperationException("Vision:Endpoint is required.")),
            AzureCredentials.Create(configuration), new ImageAnalysisClientOptions
            { Retry = { NetworkTimeout = TimeSpan.FromSeconds(30), MaxRetries = 2 } });

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