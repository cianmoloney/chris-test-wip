using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages
{
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = Permissions.StaffRead)]
    public class FilesModel : PageModel
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly FunctionApiClient _api;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FilesModel> _logger;

        public FilesModel(BlobServiceClient blobServiceClient, FunctionApiClient api, IConfiguration configuration, ILogger<FilesModel> logger)
        {
            _blobServiceClient = blobServiceClient;
            _api = api;
            _configuration = configuration;
            _logger = logger;
        }

        public record FolderItem(string Name, string Prefix);
        public record FileItem(string Name, string BlobName, long? Size, DateTimeOffset? LastModified)
        {
            public string SizeDisplay => Size switch
            {
                null => "—",
                < 1024 => $"{Size} B",
                < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024):F1} MB",
                _ => $"{Size / (1024.0 * 1024 * 1024):F1} GB"
            };
        }

        [BindProperty(SupportsGet = true)]
        public string Prefix { get; set; } = "";

        public string ContainerName => _configuration["AzureStorage:ContainerName"]
            ?? throw new InvalidOperationException("AzureStorage:ContainerName is required.");

        public string[] PathSegments => Prefix.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        public List<FolderItem> Folders { get; } = new();
        public List<FileItem> Files { get; } = new();

        [TempData] public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            // Normalize prefix so it always ends with '/' when non-empty.
            if (!string.IsNullOrEmpty(Prefix) && !Prefix.EndsWith('/'))
            {
                Prefix += "/";
            }

            try
            {
                var containerClient = _blobServiceClient.GetBlobContainerClient(ContainerName);

                await foreach (var item in containerClient.GetBlobsByHierarchyAsync(
                    BlobTraits.None, BlobStates.None, delimiter: "/", prefix: Prefix, cancellationToken: cancellationToken))
                {
                    if (item.IsPrefix)
                    {
                        var name = item.Prefix[Prefix.Length..].TrimEnd('/');
                        Folders.Add(new FolderItem(name, item.Prefix));
                    }
                    else
                    {
                        var blob = item.Blob;
                        var name = blob.Name[Prefix.Length..];
                        Files.Add(new FileItem(name, blob.Name, blob.Properties.ContentLength, blob.Properties.LastModified));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list blobs with prefix {Prefix}", Prefix);
                StatusMessage = "Could not load files. Please try again.";
            }
        }

        public async Task<IActionResult> OnPostUploadAsync(IFormFile upload, CancellationToken cancellationToken)
        {
            if (!User.HasClaim("permission", Permissions.DocumentsWrite) || !User.HasClaim("permission", Permissions.LinksWrite)) return Forbid();
            if (upload is null || upload.Length == 0)
            {
                StatusMessage = "Please choose a file to upload.";
                return RedirectToPage(new { prefix = Prefix });
            }

            try
            {
                var link = await _api.PostAsync<CreateLinkRequest, LinkResponse>("links", new("upload", null, null, 1), cancellationToken);
                await _api.UploadAsync(link.Token, upload, null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file {FileName}", upload.FileName);
                StatusMessage = "Upload failed. Please try again.";
            }

            return RedirectToPage(new { prefix = Prefix });
        }

        public async Task<IActionResult> OnGetDownloadAsync(string blobName, CancellationToken cancellationToken, string? containerName = null)
        {
            if (string.IsNullOrWhiteSpace(blobName))
            {
                return BadRequest();
            }

            try
            {
                var container = containerName ?? ContainerName;
                var access = await _api.GetAsync<UploadResponse>($"files/access?container={Uri.EscapeDataString(container)}&blob={Uri.EscapeDataString(blobName)}", cancellationToken);
                var blobClient = _blobServiceClient.GetBlobContainerClient(access.ContainerName).GetBlobClient(access.BlobName);
                if (!await blobClient.ExistsAsync(cancellationToken))
                {
                    StatusMessage = "The file no longer exists in storage.";
                    return RedirectToPage(new { prefix = Prefix });
                }

                var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
                var fileName = Path.GetFileName(access.BlobName);
                return File(stream, "application/octet-stream", fileName);
            }
            catch (FunctionApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning(ex, "Download not authorized for {Container}/{BlobName}.", containerName ?? ContainerName, blobName);
                StatusMessage = ex.Message;
                return RedirectToPage(new { prefix = Prefix });
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                StatusMessage = "The file no longer exists in storage.";
                return RedirectToPage(new { prefix = Prefix });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName}", blobName);
                StatusMessage = "Download failed. Please try again.";
                return RedirectToPage(new { prefix = Prefix });
            }
        }
    }
}
