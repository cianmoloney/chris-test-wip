using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace TestFunction.Tests;

public sealed class ProcessingTests
{
    [Fact]
    public async Task MappedLabelsAndValuesCanAppearOnSeparatePdfLines()
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate",
            StartDateLabel = "Valid from", ExpiryDateLabel = "Valid to", DocumentNumberLabel = "ID",
            ExtractedNameLabel = "Holder", EmailLabel = "Personal email", PhoneLabel = "Mobile" });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate\nHolder\nAlex Smith\nID\nABC-123\nValid from\n2026-09-01\n" +
            "Valid to\n2027-09-01\nPersonal email\nalex@example.com\nMobile\n+353 87 123 4567"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(7, document.DocumentTypeId);
        Assert.Equal("Alex Smith", document.ExtractedName);
        Assert.Equal("ABC-123", document.DocumentNumber);
        Assert.Equal("alex@example.com", document.Email);
        Assert.Equal("+353871234567", document.Phone);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), document.StartDate);
        Assert.Equal(new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero), document.ExpiryDate);
        Assert.Equal(DocumentStatus.PendingReview, document.Status);
    }

    [Fact]
    public async Task BlankMappingsRetainExistingExtractionDefaults()
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate" });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate  Name: Alex Smith  Certificate Number: ABC-123  " +
            "Start Date: 2026-09-01  End Date: 2027-09-01  Email: alex@example.com  Phone: +353 87 123 4567"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal("Alex Smith", document.ExtractedName);
        Assert.Equal("ABC-123", document.DocumentNumber);
        Assert.Equal("alex@example.com", document.Email);
        Assert.Equal("+353871234567", document.Phone);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), document.StartDate);
        Assert.Equal(new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero), document.ExpiryDate);
    }

    [Theory]
    [InlineData("From: 2027-09-01  To: 2026-09-01", DocumentStatus.ParseFailed)]
    [InlineData("From: invalid  To: 2027-99-99", DocumentStatus.PendingReview)]
    public async Task InvalidMappedDatesStillRequireManualReview(string dates, DocumentStatus expectedStatus)
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate",
            StartDateLabel = "From", ExpiryDateLabel = "To" });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate  " + dates));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(expectedStatus, document.Status);
        Assert.False(document.IsValid);
        if (expectedStatus == DocumentStatus.PendingReview)
        {
            Assert.Null(document.StartDate);
            Assert.Null(document.ExpiryDate);
        }
    }

    [Fact]
    public async Task IdentifierSelectsConfiguredIdentityAndContactLabels()
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate",
            DocumentNumberLabel = "Registration ID", ExtractedNameLabel = "Participant", EmailLabel = "Personal email", PhoneLabel = "Mobile" });
        database.Staff.Add(new() { Id = 5, FirstName = "Alex", LastName = "Smith", Email = "alex@example.com" });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate  Name: Wrong Person  Document Number: WRONG  " +
            "Email: provider@example.com  Phone: +353 1 111 1111  Participant: Alex Smith  Registration ID: ABC-123  " +
            "Personal email: alex@example.com  Mobile: +353 87 123 4567"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(7, document.DocumentTypeId);
        Assert.Equal("ABC-123", document.DocumentNumber);
        Assert.Equal("Alex Smith", document.ExtractedName);
        Assert.Equal("alex@example.com", document.Email);
        Assert.Equal("+353871234567", document.Phone);
        Assert.Equal(5, document.StaffId);
        Assert.False(document.IsValid);
    }

    [Fact]
    public async Task MissingConfiguredFieldsDoNotFallBackToUnrelatedValues()
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate",
            StartDateLabel = "From", ExpiryDateLabel = "To", DocumentNumberLabel = "ID", ExtractedNameLabel = "Holder",
            EmailLabel = "Personal email", PhoneLabel = "Mobile" });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate  Name: Wrong Person  Document Number: WRONG  " +
            "Email: provider@example.com  Phone: +353 1 111 1111  Start Date: 2020-01-01  End Date: 2021-01-01  " +
            "NotFrom: 2026-01-01  NotTo: 2027-01-01  OtherID: 123  PolicyHolder: Wrong Person"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(7, document.DocumentTypeId);
        Assert.Null(document.StartDate);
        Assert.Null(document.ExpiryDate);
        Assert.Null(document.DocumentNumber);
        Assert.Null(document.ExtractedName);
        Assert.Null(document.Email);
        Assert.Null(document.Phone);
        Assert.Null(document.StaffId);
        Assert.Equal(DocumentStatus.PendingReview, document.Status);
    }

    [Theory]
    [InlineData("From", "To", "From: 2026-09-01  To: 2027-09-01")]
    [InlineData("Start", "End", "start 2026-09-01  END 2027-09-01")]
    [InlineData("Valid from (date)", "Valid to [date]", "Valid from (date): 2026-09-01  Valid to [date]: 2027-09-01")]
    public async Task IdentifierSelectsConfiguredDateLabels(string startLabel, string expiryLabel, string dates)
    {
        await using var database = Database();
        database.DocumentTypes.Add(new() { Id = 7, Name = "Training", TextIdentifier = "Training Certificate",
            StartDateLabel = startLabel, ExpiryDateLabel = expiryLabel });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf("Training Certificate  " + dates + "  Start Date: 2020-01-01  End Date: 2021-01-01"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(7, document.DocumentTypeId);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), document.StartDate);
        Assert.Equal(new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero), document.ExpiryDate);
        Assert.Equal(DocumentStatus.PendingReview, document.Status);
        Assert.False(document.IsValid);
    }

    [Theory]
    [InlineData(DocumentStatus.AwaitingScan)]
    [InlineData(DocumentStatus.AwaitingProcessing)]
    [InlineData(DocumentStatus.Rejected)]
    public async Task QueuedDocumentsAreProcessedWithoutScannerOrFalseCleanResult(DocumentStatus initialStatus)
    {
        await using var database = Database();
        database.Documents.Add(new() { Id = 10, ContainerName = "uploads", BlobName = "2026/09/certificate.pdf", Status = initialStatus,
            Issue = "Awaiting a successful malware scan." });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf());

        await Worker(database, storage).RetryPendingUploads(null!, default);

        var document = (await database.Documents.FindAsync(10))!;
        Assert.Equal(initialStatus == DocumentStatus.Rejected ? DocumentStatus.Rejected : DocumentStatus.PendingReview, document.Status);
        Assert.Equal("Alex Smith", document.ExtractedName);
        Assert.NotNull(document.ProcessingCompletedAt);
        Assert.False(document.ScanPassed);
        Assert.False(document.IsValid);
        Assert.DoesNotContain("scan", document.Issue ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, storage.Blob.Downloads);
    }

    [Fact]
    public async Task ExtractionFailureAllowsManualReviewWithoutClaimingScanPassed()
    {
        await using var database = Database();
        var storage = new TestStorage(Encoding.ASCII.GetBytes("%PDF-1.7\nnot a complete PDF"));

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/broken.pdf", default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(DocumentStatus.ParseFailed, document.Status);
        Assert.NotNull(document.ProcessingCompletedAt);
        Assert.False(document.ScanPassed);
        Assert.False(document.IsValid);
        await new StaffDataService(database).SetDocumentStatusAsync(document.Id, new(DocumentStatus.Validated), default);
        Assert.True(document.IsValid);
        Assert.False(document.ScanPassed);
    }

    [Theory]
    [InlineData("certificate.pdf", "%PDF-1.7 /JavaScript malicious", false)]
    [InlineData("certificate.pdf", "MZ not a pdf", false)]
    [InlineData("certificate.exe", "executable", false)]
    [InlineData("certificate.png", "not an image", false)]
    [InlineData("certificate.pdf", "%PDF-1.7", true)]
    public async Task LocalFileChecksStillQuarantineInvalidOrOversizedFiles(string name, string content, bool oversized)
    {
        await using var database = Database();
        var storage = new TestStorage(Encoding.ASCII.GetBytes(content));
        if (oversized) storage.Blob.ReportedLength = UploadService.MaximumBytes + 1;

        await Worker(database, storage).ProcessUploadedBlob(Stream.Null, "2026/09/" + name, default);

        var document = await database.Documents.SingleAsync();
        Assert.Equal(DocumentStatus.Unsafe, document.Status);
        Assert.NotNull(document.ProcessingCompletedAt);
        Assert.False(document.ScanPassed);
        Assert.False(document.IsValid);
        if (oversized) Assert.Equal(0, storage.Blob.Downloads);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => new StaffDataService(database)
            .SetDocumentStatusAsync(document.Id, new(DocumentStatus.Validated), default))).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviouslyArchivedOrQuarantinedFilesAreNotReprocessed(bool archived)
    {
        await using var database = Database();
        database.Documents.Add(new() { Id = 10, ContainerName = "uploads", BlobName = "2026/09/certificate.pdf",
            Status = DocumentStatus.Unsafe, IsArchived = archived, Issue = "Previously quarantined." });
        await database.SaveChangesAsync();
        var storage = new TestStorage(Pdf());
        var worker = Worker(database, storage);

        await worker.ProcessUploadedBlob(Stream.Null, "2026/09/certificate.pdf", default);
        await worker.RetryPendingUploads(null!, default);

        Assert.Equal(0, storage.Blob.Downloads);
        var document = await database.Documents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(DocumentStatus.Unsafe, document.Status);
        Assert.Equal("Previously quarantined.", document.Issue);
    }

    private static byte[] Pdf(string text = "Name: Alex Smith")
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        var position = 700;
        foreach (var line in text.Split('\n'))
        {
            page.AddText(line, 12, new PdfPoint(50, position), font);
            position -= 20;
        }
        return builder.Build();
    }

    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static BackgroundWorker Worker(AppDbContext database, TestStorage storage) => new(database, storage, new NoEmail(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UploadsContainer"] = "uploads" }).Build(),
        TimeProvider.System, NullLogger<BackgroundWorker>.Instance);

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestStorage(byte[] content) : BlobServiceClient
    {
        public TestBlob Blob { get; } = new(content);
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName) => new TestContainer(Blob);
    }

    private sealed class TestContainer(TestBlob blob) : BlobContainerClient
    {
        public override BlobClient GetBlobClient(string blobName) => blob;
    }

    private sealed class TestBlob(byte[] content) : BlobClient
    {
        public long ReportedLength { get; set; } = content.Length;
        public int Downloads { get; private set; }
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(contentLength: ReportedLength), null!));
        public override async Task<Response> DownloadToAsync(Stream destination, CancellationToken cancellationToken = default)
        {
            Downloads++;
            await destination.WriteAsync(content, cancellationToken);
            return null!;
        }
        public override Task<Response<GetBlobTagResult>> GetTagsAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Processing must not request scanner tags.");
    }
}