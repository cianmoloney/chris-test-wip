using Azure;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public interface IFileAccessService
{
    Task<UploadResponse> AuthorizeAsync(string container, string blobName, CancellationToken cancellationToken);
}

public sealed class FileAccessService(AppDbContext database, BlobServiceClient storage,
    IConfiguration configuration, ILogger<FileAccessService> logger) : IFileAccessService
{
    public async Task<UploadResponse> AuthorizeAsync(string container, string blobName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(container) || string.IsNullOrWhiteSpace(blobName))
            throw new ApiException(400, "A container and file name are required.");
        var uploadsContainer = configuration["UploadsContainer"]
            ?? throw new InvalidOperationException("UploadsContainer is required.");
        var isUploadsContainer = string.Equals(container, uploadsContainer, StringComparison.Ordinal);
        var candidates = await database.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(document => document.BlobName == blobName
                && (document.ContainerName == container || (document.ContainerName == null && isUploadsContainer)))
            .ToListAsync(cancellationToken);
        var documents = candidates.Where(document => string.Equals(document.BlobName, blobName, StringComparison.Ordinal)
            && (string.Equals(document.ContainerName, container, StringComparison.Ordinal)
                || (document.ContainerName is null && isUploadsContainer))).ToList();

        if (documents.Count > 0)
        {
            if (documents.Any(document => document.IsArchived))
                throw new ApiException(409, "This file belongs to an archived document and is not available for download.");
            if (documents.Any(document => document.Status == DocumentStatus.Unsafe))
                throw new ApiException(409, "Download blocked: file quarantined.");
            return new(container, blobName);
        }

        if (!isUploadsContainer)
            throw new ApiException(403, "Untracked files can only be downloaded from the configured uploads container.");
        var blob = storage.GetBlobContainerClient(container).GetBlobClient(blobName);
        try
        {
            if (!await blob.ExistsAsync(cancellationToken))
                throw new ApiException(404, "The file no longer exists in storage.");
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            throw new ApiException(404, "The file no longer exists in storage.");
        }
        catch (RequestFailedException exception) when (exception.Status == 403)
        {
            logger.LogError(exception, "Cannot access uploaded file {Container}/{BlobName}.", container, blobName);
            throw new ApiException(503, "File storage could not be accessed. Please contact an administrator.");
        }
        return new(container, blobName);
    }
}