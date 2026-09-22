using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class UploadStorage(IConfiguration configuration)
{
    public string ContainerName => configuration["UploadsContainer"]
        ?? throw new InvalidOperationException("UploadsContainer is required.");

    public BlobServiceClient Client => new(new Uri(configuration["AzureStorage:ServiceUri"]
        ?? throw new InvalidOperationException("AzureStorage:ServiceUri is required.")), AzureCredentials.Create(configuration));
}

public sealed class UploadService(AppDbContext database, LinkService links, UploadStorage storage, TimeProvider clock)
{
    public const long MaximumBytes = 20 * 1024 * 1024;
    public async Task<UploadResponse> UploadAsync(string token, IFormFile file, int? documentTypeId, CancellationToken cancellationToken)
    {
        var link = await links.RequireAsync(token, "upload", cancellationToken);
        if (file.Length is <= 0 or > MaximumBytes) throw new ApiException(400, "Choose a file up to 20 MB.");
        if (documentTypeId is not null && !await database.DocumentTypes.AnyAsync(type => type.Id == documentTypeId, cancellationToken))
            throw new ApiException(400, "Select a valid document type.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".webp"))
            throw new ApiException(400, "Upload a PDF or supported image.");
        await using var content = new MemoryStream();
        await file.CopyToAsync(content, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(content.GetBuffer().AsSpan(0, checked((int)content.Length))));
        if (link.BlobName is null)
        {
            var location = UploadNaming.Create(clock.GetUtcNow(), link.UploadId, file.FileName, link.StaffId, documentTypeId, storage.ContainerName);
            link.ContainerName = location.ContainerName;
            link.BlobName = location.BlobName;
            link.ContentHash = hash;
            link.DocumentTypeId = documentTypeId;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (link.ContentHash != hash || link.DocumentTypeId != documentTypeId)
            throw new ApiException(409, "An upload is already reserved for this link. Retry the original file.");

        var container = storage.Client.GetBlobContainerClient(link.ContainerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(link.BlobName);
        content.Position = 0;
        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" },
                Metadata = new Dictionary<string, string> { ["sha256"] = hash }
            }, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (!properties.Value.Metadata.TryGetValue("sha256", out var existing) || existing != hash)
                throw new ApiException(409, "The reserved upload already contains a different file.");
        }
        link.UsedAt = clock.GetUtcNow();
        if (!await database.Documents.IgnoreQueryFilters().AnyAsync(document => document.ContainerName == link.ContainerName && document.BlobName == link.BlobName, cancellationToken))
            database.Documents.Add(new DocumentEntry
            {
                ContainerName = link.ContainerName, BlobName = link.BlobName, Name = Path.GetFileName(file.FileName),
                StaffId = link.StaffId, DocumentTypeId = link.DocumentTypeId, Status = DocumentStatus.AwaitingScan,
                Issue = "Awaiting file safety scan and extraction."
            });
        await database.SaveChangesAsync(cancellationToken);
        return new(link.ContainerName!, link.BlobName);
    }
}