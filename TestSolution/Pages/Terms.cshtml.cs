using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;
using TestSolution.Services;

namespace TestSolution.Pages
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
        private readonly AppDbContext _dbContext;
        private readonly IShareLinkService _shareLinkService;
        private readonly ILogger<TermsModel> _logger;

        public TermsModel(AppDbContext dbContext, IShareLinkService shareLinkService, ILogger<TermsModel> logger)
        {
            _dbContext = dbContext;
            _shareLinkService = shareLinkService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Lang { get; set; }

        [BindProperty]
        public bool Agree { get; set; }

        public bool IsValid { get; private set; }

        public Staff? Staff { get; private set; }

        public TermsDocumentVersion? Terms { get; private set; }

        public bool AlreadyAccepted { get; private set; }

        public DateTimeOffset? AcceptedAt { get; private set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);
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
                _dbContext.StaffTermsAcceptances.Add(new StaffTermsAcceptance
                {
                    StaffId = Staff.Id,
                    TermsDocumentVersionId = Terms.Id,
                    AcceptedAt = DateTimeOffset.UtcNow,
                });
                await _dbContext.SaveChangesAsync(cancellationToken);

                AlreadyAccepted = true;
                AcceptedAt = DateTimeOffset.UtcNow;
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
            // Accept tokens issued specifically for terms, or registration
            // tokens carrying the staff id (post-registration flow).
            int? staffId;
            IsValid = _shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Terms, out _, out staffId)
                || _shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Register, out _, out staffId);

            if (!IsValid || staffId is null)
            {
                IsValid = false;
                return false;
            }

            Staff = await _dbContext.Staff.FindAsync([staffId.Value], cancellationToken);
            if (Staff is null)
            {
                return false;
            }

            var language = Lang is "pl" or "uk" ? Lang : "en";
            Terms = await _dbContext.TermsDocumentVersions.AsNoTracking()
                .Include(v => v.TermsDocument)
                .Where(v => v.IsActive && v.Language == language)
                .OrderByDescending(v => v.Version)
                .FirstOrDefaultAsync(cancellationToken)
                ?? await _dbContext.TermsDocumentVersions.AsNoTracking()
                    .Include(v => v.TermsDocument)
                    .Where(v => v.IsActive && v.Language == "en")
                    .OrderByDescending(v => v.Version)
                    .FirstOrDefaultAsync(cancellationToken);

            if (Terms is not null)
            {
                var acceptance = await _dbContext.StaffTermsAcceptances.AsNoTracking()
                    .Where(a => a.StaffId == Staff.Id && a.TermsDocumentVersionId == Terms.Id)
                    .OrderByDescending(a => a.AcceptedAt)
                    .FirstOrDefaultAsync(cancellationToken);
                AlreadyAccepted = acceptance is not null;
                AcceptedAt = acceptance?.AcceptedAt;
            }

            return true;
        }
    }
}
