using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Anonymous, upload-only page reached through a signed, expiring share link.
    /// Access is authorized by validating the token, not the login cookie.
    /// </summary>
    [AllowAnonymous]
    public class ShareModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<ShareModel> _logger;

        public ShareModel(FunctionApiClient api, ILogger<ShareModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        public bool IsValid { get; private set; }

        public string? StatusMessage { get; set; }
        [BindProperty] public int? DocumentTypeId { get; set; }
        public List<LookupResponse> DocumentTypes { get; private set; } = [];

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await _api.PostAsync<ResolveLinkRequest, PublicLinkResponse>("public/resolve", new(Token ?? "", "upload"), cancellationToken);
                DocumentTypes = response.Lookups!.DocumentTypes;
                IsValid = true;
            }
            catch (FunctionApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest)
            { IsValid = false; }
            catch (HttpRequestException) { IsValid = false; StatusMessage = "Unavailable"; }
        }

        public async Task<IActionResult> OnPostUploadAsync(IFormFile upload, CancellationToken cancellationToken)
        {
            await OnGetAsync(cancellationToken);
            if (!IsValid)
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
                await _api.UploadAsync(Token!, upload, DocumentTypeId, cancellationToken);
                return RedirectToPage("/UploadComplete", new { lang = Request.Query["lang"].ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Share upload failed.");
                StatusMessage = ex is FunctionApiException ? ex.Message : "Upload failed. Please try again.";
            }

            return Page();
        }
    }
}
