using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace TestSolution.Services
{
    public interface IBlobStorageService
    {
        Task<string> UploadAsync(string containerName, string fileName, Stream content, string contentType, CancellationToken cancellationToken = default);
    }

    public class BlobStorageService : IBlobStorageService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<BlobStorageService> _logger;

        public BlobStorageService(BlobServiceClient blobServiceClient, TimeProvider timeProvider, ILogger<BlobStorageService> logger)
        {
            _blobServiceClient = blobServiceClient;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<string> UploadAsync(string containerName, string fileName, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            // Get the current UTC time and build a monthly folder path, e.g. "2025/06/report.pdf".
            var utcNow = _timeProvider.GetUtcNow();
            var blobName = $"{utcNow:yyyy}/{utcNow:MM}/{Path.GetFileName(fileName)}";

            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.UploadAsync(content, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
            }, cancellationToken);

            _logger.LogInformation("Uploaded blob {BlobName} to container {Container}", blobName, containerName);
            return blobName;
        }
    }
}
