using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestSolution.Services;

namespace TestSolution.Pages
{
    public class FilesModel : PageModel
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly IBlobStorageService _blobStorageService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FilesModel> _logger;

        public FilesModel(BlobServiceClient blobServiceClient, IBlobStorageService blobStorageService, IConfiguration configuration, ILogger<FilesModel> logger)
        {
            _blobServiceClient = blobServiceClient;
            _blobStorageService = blobStorageService;
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

        public string[] PathSegments => Prefix.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        public List<FolderItem> Folders { get; } = new();
        public List<FileItem> Files { get; } = new();

        public string? StatusMessage { get; set; }

        private async Task<BlobContainerClient> GetContainerClient()
        {
            var containerName = _configuration["AzureStorage:ContainerName"] ?? "uploads";
            var client = _blobServiceClient.GetBlobContainerClient(containerName);
            var accountInfo = await client.GetAccountInfoAsync();
            return client;
        }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            // Normalize prefix so it always ends with '/' when non-empty.
            if (!string.IsNullOrEmpty(Prefix) && !Prefix.EndsWith('/'))
            {
                Prefix += "/";
            }

            try
            {
                var containerClient = await GetContainerClient();

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
            if (upload is null || upload.Length == 0)
            {
                StatusMessage = "Please choose a file to upload.";
                return RedirectToPage(new { prefix = Prefix });
            }

            try
            {
                var containerName = _configuration["AzureStorage:ContainerName"] ?? "uploads";
                await using var stream = upload.OpenReadStream();
                await _blobStorageService.UploadAsync(containerName, upload.FileName, stream,
                    upload.ContentType ?? "application/octet-stream", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file {FileName}", upload.FileName);
                StatusMessage = "Upload failed. Please try again.";
            }

            return RedirectToPage(new { prefix = Prefix });
        }

        public async Task<IActionResult> OnGetDownloadAsync(string blobName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(blobName))
            {
                return BadRequest();
            }

            try
            {
                var containerClient = await GetContainerClient();
                var blobClient  = containerClient.GetBlobClient(blobName);
                if (!await blobClient.ExistsAsync(cancellationToken))
                {
                    return NotFound();
                }

                var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
                var fileName = Path.GetFileName(blobName);
                return File(stream, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName}", blobName);
                StatusMessage = "Download failed. Please try again.";
                return RedirectToPage();
            }
        }
    }
}
