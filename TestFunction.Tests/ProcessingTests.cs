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

    private static byte[] Pdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Name: Alex Smith", 12, new PdfPoint(50, 700), font);
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