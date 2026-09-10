using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestSolution.Services;

namespace TestSolution.Pages
{
    /// <summary>
    /// Anonymous, upload-only page reached through a signed, expiring share link.
    /// Access is authorized by validating the token, not the login cookie.
    /// </summary>
    [AllowAnonymous]
    public class ShareModel : PageModel
    {
        private readonly IBlobStorageService _blobStorageService;
        private readonly IShareLinkService _shareLinkService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ShareModel> _logger;

        public ShareModel(IBlobStorageService blobStorageService, IShareLinkService shareLinkService, IConfiguration configuration, ILogger<ShareModel> logger)
        {
            _blobStorageService = blobStorageService;
            _shareLinkService = shareLinkService;
            _configuration = configuration;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        public bool IsValid { get; private set; }

        public string? StatusMessage { get; set; }

        public void OnGet()
        {
            IsValid = _shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Upload, out _, out _);
        }

        public async Task<IActionResult> OnPostUploadAsync(IFormFile upload, CancellationToken cancellationToken)
        {
            if (!_shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Upload, out var prefix, out var staffId))
            {
                IsValid = false;
                return Page();
            }

            IsValid = true;

            if (upload is null || upload.Length == 0)
            {
                StatusMessage = "Please choose a file to upload.";
                return Page();
            }

            try
            {
                var containerName = _configuration["AzureStorage:ContainerName"] ?? "uploads";

                // Staff-bound links always upload into the staff member's folder so the
                // processing function can associate the document deterministically.
                if (staffId is not null && string.IsNullOrEmpty(prefix))
                {
                    prefix = $"staff/{staffId}/";
                }

                var targetName = string.IsNullOrEmpty(prefix)
                    ? upload.FileName
                    : $"{prefix}{Path.GetFileName(upload.FileName)}";

                await using var stream = upload.OpenReadStream();
                await _blobStorageService.UploadAsync(containerName, targetName, stream,
                    upload.ContentType ?? "application/octet-stream", cancellationToken);

                StatusMessage = "File uploaded successfully.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Share upload failed for prefix {Prefix}", prefix);
                StatusMessage = "Upload failed. Please try again.";
            }

            return Page();
        }
    }
}
