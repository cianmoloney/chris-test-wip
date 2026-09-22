using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class FileAccessTests
{
    [Theory]
    [InlineData("uploads", "2026/09/certificate.pdf")]
    [InlineData("staff-uploads", "09/Safe Pass + #1%.pdf")]
    public async Task ExistingBlobCanBeDownloadedWithoutDocumentRowOrScan(string container, string blobName)
    {
        await using var database = Database();
        var storage = new TestStorage();
        using var cancellation = new CancellationTokenSource();

        var response = await Service(database, storage, container).AuthorizeAsync(container, blobName, cancellation.Token);

        Assert.Equal(new UploadResponse(container, blobName), response);
        Assert.Equal(container, storage.ContainerName);
        Assert.Equal(blobName, storage.Container.BlobName);
        Assert.Equal(cancellation.Token, storage.Container.Blob.Token);
        Assert.Empty(await database.Documents.ToListAsync());
    }

    [Fact]
    public async Task MissingUntrackedBlobIsNotFound()
    {
        await using var database = Database();
        var storage = new TestStorage();
        storage.Container.Blob.BlobExists = false;

        var exception = await Assert.ThrowsAsync<ApiException>(() => Service(database, storage)
            .AuthorizeAsync("uploads", "2026/09/certificate.pdf", default));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("no longer exists", exception.Message);
    }

    [Theory]
    [InlineData(404, 404, "no longer exists")]
    [InlineData(403, 503, "could not be accessed")]
    public async Task StorageFailuresAreDistinguishedFromMissingMetadata(int storageStatus, int expectedStatus, string message)
    {
        await using var database = Database();
        var storage = new TestStorage();
        storage.Container.Blob.ErrorStatus = storageStatus;

        var exception = await Assert.ThrowsAsync<ApiException>(() => Service(database, storage)
            .AuthorizeAsync("uploads", "missing.pdf", default));

        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Contains(message, exception.Message);
    }

    [Theory]
    [InlineData(true, true, DocumentStatus.Validated, "archived")]
    [InlineData(false, false, DocumentStatus.Unsafe, "quarantined")]
    [InlineData(false, true, DocumentStatus.Unsafe, "quarantined")]
    public async Task TrackedRestrictionsCannotFallBackToStorage(bool archived, bool scanned, DocumentStatus status, string message)
    {
        await using var database = Database();
        database.Documents.Add(new() { ContainerName = "uploads", BlobName = "certificate.pdf", IsArchived = archived, ScanPassed = scanned, Status = status });
        await database.SaveChangesAsync();
        var storage = new TestStorage();

        var exception = await Assert.ThrowsAsync<ApiException>(() => Service(database, storage)
            .AuthorizeAsync("uploads", "certificate.pdf", default));

        Assert.Equal(409, exception.StatusCode);
        Assert.Contains(message, exception.Message);
        Assert.Null(storage.ContainerName);
    }

    [Theory]
    [InlineData("legacy-uploads", "legacy-uploads")]
    [InlineData("uploads", "uploads")]
    [InlineData(null, "uploads")]
    public async Task UnscannedDocumentLocationsIncludingLegacyRowsStillWork(string? storedContainer, string requestedContainer)
    {
        await using var database = Database();
        database.Documents.Add(new() { ContainerName = storedContainer, BlobName = "09/certificate.pdf", ScanPassed = false, Status = DocumentStatus.AwaitingScan });
        await database.SaveChangesAsync();
        var storage = new TestStorage();

        var response = await Service(database, storage).AuthorizeAsync(requestedContainer, "09/certificate.pdf", default);

        Assert.Equal(new UploadResponse(requestedContainer, "09/certificate.pdf"), response);
        Assert.Null(storage.ContainerName);
        Assert.False((await database.Documents.SingleAsync()).ScanPassed);
    }

    [Fact]
    public async Task UntrackedFilesCannotReadOtherContainers()
    {
        await using var database = Database();
        var storage = new TestStorage();

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(database, storage)
            .AuthorizeAsync("private-data", "file.pdf", default));

        Assert.Equal(403, error.StatusCode);
        Assert.Null(storage.ContainerName);
    }

    [Theory]
    [InlineData("", "file.pdf")]
    [InlineData("uploads", "")]
    public async Task MissingLocatorIsRejected(string container, string blob)
    {
        await using var database = Database();
        var storage = new TestStorage();

        var error = await Assert.ThrowsAsync<ApiException>(() => Service(database, storage).AuthorizeAsync(container, blob, default));

        Assert.Equal(400, error.StatusCode);
        Assert.Null(storage.ContainerName);
    }

    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static FileAccessService Service(AppDbContext database, TestStorage storage, string container = "uploads") => new(database, storage,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UploadsContainer"] = container }).Build(),
        NullLogger<FileAccessService>.Instance);

    private sealed class TestStorage : BlobServiceClient
    {
        public string? ContainerName { get; private set; }
        public TestContainer Container { get; } = new();
        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            ContainerName = blobContainerName;
            return Container;
        }
    }

    private sealed class TestContainer : BlobContainerClient
    {
        public string? BlobName { get; private set; }
        public TestBlob Blob { get; } = new();
        public override BlobClient GetBlobClient(string blobName)
        {
            BlobName = blobName;
            return Blob;
        }
    }

    private sealed class TestBlob : BlobClient
    {
        public bool BlobExists { get; set; } = true;
        public int? ErrorStatus { get; set; }
        public CancellationToken Token { get; private set; }
        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (ErrorStatus is not null) throw new RequestFailedException(ErrorStatus.Value, "Storage failure.");
            return Task.FromResult(Response.FromValue(BlobExists, null!));
        }

        public override Task<Response<GetBlobTagResult>> GetTagsAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Downloads must not require external scan tags.");
    }
}