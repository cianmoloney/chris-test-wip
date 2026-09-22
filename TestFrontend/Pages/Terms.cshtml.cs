using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;
using System.Net;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Anonymous terms page reached through a signed, expiring terms link.
    /// The staff identity is carried inside the signed token (never a query
    /// parameter), the terms text comes from the database in the requested
    /// language, and each acceptance is stored as a history record.
    /// </summary>
    [AllowAnonymous]
    public class TermsModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<TermsModel> _logger;

        public TermsModel(FunctionApiClient api, ILogger<TermsModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Lang { get; set; }

        [BindProperty]
        public bool Agree { get; set; }
        [BindProperty] public int TermsDocumentVersionId { get; set; }

        public bool IsValid { get; private set; }

        public StaffResponse? Staff { get; private set; }

        public TermsVersionResponse? Terms { get; private set; }

        public bool AlreadyAccepted { get; private set; }

        public DateTimeOffset? AcceptedAt { get; private set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);
            TermsDocumentVersionId = Terms?.Id ?? 0;
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            if (!await LoadAsync(cancellationToken))
            {
                return Page();
            }

            if (Staff is null || Terms is null)
            {
                return NotFound();
            }

            if (!Agree)
            {
                StatusMessage = "You must agree to the terms to continue.";
                return Page();
            }

            try
            {
                var acceptance = await _api.PostAsync<LinkAcceptanceRequest, TermsAcceptanceResponse>(
                    "public/accept", new(Token!, TermsDocumentVersionId, Agree), cancellationToken);

                AlreadyAccepted = true;
                AcceptedAt = acceptance.AcceptedAt;
                StatusMessage = "Thank you. Your agreement has been recorded.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record terms agreement for staff member {StaffId}.", Staff.Id);
                StatusMessage = "Could not record your agreement. Please try again.";
            }

            return Page();
        }

        private async Task<bool> LoadAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await _api.PostAsync<ResolveLinkRequest, PublicLinkResponse>(
                    "public/resolve", new(Token ?? "", "terms", Lang ?? "en"), cancellationToken);
                IsValid = true;
                Staff = response.Staff;
                Terms = response.Terms;
                AcceptedAt = response.AcceptedAt;
                AlreadyAccepted = AcceptedAt is not null;
                return true;
            }
            catch (FunctionApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                IsValid = false;
                return false;
            }
            catch (HttpRequestException) { IsValid = false; StatusMessage = "Unavailable"; return false; }
        }
    }
}
