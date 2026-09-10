using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace TestSolution.Pages
{
    public class IndexModel : PageModel
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(BlobServiceClient blobServiceClient, IConfiguration configuration, ILogger<IndexModel> logger)
        {
            _blobServiceClient = blobServiceClient;
            _configuration = configuration;
            _logger = logger;
        }

        [BindProperty]
        public IFormFile? Upload { get; set; }

        [TempData]
        public string? StatusMessage { get; set; }

        public void OnGet()
        {

        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            if (Upload is null || Upload.Length == 0)
            {
                StatusMessage = "Please select a file to upload.";
                return RedirectToPage();
            }

            try
            {
                var containerName = _configuration["AzureStorage:ContainerName"] ?? "uploads";
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

                var blobClient = containerClient.GetBlobClient(Path.GetFileName(Upload.FileName));
                await using var stream = Upload.OpenReadStream();
                await blobClient.UploadAsync(stream, overwrite: true, cancellationToken);

                StatusMessage = $"Uploaded '{Upload.FileName}' successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload file {FileName}", Upload.FileName);
                StatusMessage = "Upload failed. Please try again.";
            }

            return RedirectToPage();
        }
    }
}
