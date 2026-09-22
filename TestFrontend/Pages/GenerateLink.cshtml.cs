using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages
{
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = Permissions.LinksWrite)]
    public class GenerateLinkModel : PageModel
    {
        private readonly FunctionApiClient _api;

        public GenerateLinkModel(FunctionApiClient api)
        {
            _api = api;
        }

        [BindProperty(SupportsGet = true)]
        public string LinkType { get; set; } = ShareLinkPurposes.Register;

        [BindProperty(SupportsGet = true)]
        public int? StaffId { get; set; }
        [BindProperty] public int? TermsDocumentId { get; set; }
        public List<TermsDocumentResponse> TermsDocuments { get; private set; } = [];

        [BindProperty]
        public int ValidHours { get; set; } = 24;

        public List<StaffResponse> StaffList { get; } = new();

        public string? ShareLink { get; set; }
        public DateTimeOffset? ShareLinkExpiresAt { get; set; }
        [BindProperty] public Guid? IssuedLinkId { get; set; }
        public string? ErrorMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadStaffAsync(cancellationToken);
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            await LoadStaffAsync(cancellationToken);

            var purpose = LinkType switch
            {
                ShareLinkPurposes.Upload => ShareLinkPurposes.Upload,
                ShareLinkPurposes.Terms => ShareLinkPurposes.Terms,
                _ => ShareLinkPurposes.Register,
            };

            if (purpose == ShareLinkPurposes.Terms)
            {
                if (StaffId is null || !StaffList.Any(s => s.Id == StaffId))
                {
                    ErrorMessage = "Please select the staff member this link is for.";
                    return Page();
                }
            }
            else if (purpose == ShareLinkPurposes.Register)
            {
                StaffId = null; // registration links are anonymous
            }

            var hours = Math.Clamp(ValidHours, 1, 24 * 14);
            LinkResponse issued;
            try
            {
                issued = await _api.PostAsync<CreateLinkRequest, LinkResponse>("links",
                    new(purpose, StaffId, TermsDocumentId, hours), cancellationToken);
            }
            catch (FunctionApiException exception)
            {
                ErrorMessage = exception.Message;
                return Page();
            }

            var page = purpose switch
            {
                ShareLinkPurposes.Upload => "/Share",
                ShareLinkPurposes.Terms => "/Terms",
                _ => "/Register",
            };
            ShareLink = Url.Page(page, pageHandler: null, values: new { token = issued.Token }, protocol: Request.Scheme);
            ShareLinkExpiresAt = issued.ExpiresAt;
            IssuedLinkId = issued.Id;

            return Page();
        }

        private async Task LoadStaffAsync(CancellationToken cancellationToken)
        {
            var response = await _api.GetAsync<StaffListResponse>("staff", cancellationToken);
            StaffList.AddRange(response.Staff);
            TermsDocuments = await _api.GetAsync<List<TermsDocumentResponse>>("terms", cancellationToken);
        }

        public async Task<IActionResult> OnPostRevokeAsync(CancellationToken cancellationToken)
        {
            if (IssuedLinkId is null) return BadRequest();
            await _api.DeleteAsync($"links/{IssuedLinkId}", cancellationToken);
            return RedirectToPage();
        }
    }
}
