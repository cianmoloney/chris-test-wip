using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;
using TestSolution.Services;

namespace TestSolution.Pages
{
    public class GenerateLinkModel : PageModel
    {
        private readonly IShareLinkService _shareLinkService;
        private readonly AppDbContext _dbContext;

        public GenerateLinkModel(IShareLinkService shareLinkService, AppDbContext dbContext)
        {
            _shareLinkService = shareLinkService;
            _dbContext = dbContext;
        }

        [BindProperty(SupportsGet = true)]
        public string LinkType { get; set; } = ShareLinkPurposes.Register;

        [BindProperty(SupportsGet = true)]
        public int? StaffId { get; set; }

        [BindProperty]
        public int ValidHours { get; set; } = 24;

        public List<Staff> StaffList { get; } = new();

        public string? ShareLink { get; set; }
        public DateTimeOffset? ShareLinkExpiresAt { get; set; }
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

            // Upload and terms links must be tied to a staff member so the resulting
            // upload/agreement is associated with their account.
            if (purpose is ShareLinkPurposes.Upload or ShareLinkPurposes.Terms)
            {
                if (StaffId is null || !StaffList.Any(s => s.Id == StaffId))
                {
                    ErrorMessage = "Please select the staff member this link is for.";
                    return Page();
                }
            }
            else
            {
                StaffId = null; // registration links are anonymous
            }

            // Staff-bound uploads land in a per-staff folder so the processing
            // function can associate the document deterministically.
            var prefix = purpose == ShareLinkPurposes.Upload ? $"staff/{StaffId}/" : "";

            var hours = Math.Clamp(ValidHours, 1, 24 * 14);
            var expiresAt = DateTimeOffset.UtcNow.AddHours(hours);
            var token = _shareLinkService.CreateToken(purpose, prefix, expiresAt, StaffId);

            var page = purpose switch
            {
                ShareLinkPurposes.Upload => "/Share",
                ShareLinkPurposes.Terms => "/Terms",
                _ => "/Register",
            };
            ShareLink = Url.Page(page, pageHandler: null, values: new { token }, protocol: Request.Scheme);
            ShareLinkExpiresAt = expiresAt;

            return Page();
        }

        private async Task LoadStaffAsync(CancellationToken cancellationToken)
        {
            StaffList.AddRange(await _dbContext.Staff.AsNoTracking()
                .OrderBy(s => s.LastName).ThenBy(s => s.FirstName)
                .ToListAsync(cancellationToken));
        }
    }
}
