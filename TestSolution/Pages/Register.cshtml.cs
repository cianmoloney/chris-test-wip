using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;
using TestSolution.Services;

namespace TestSolution.Pages
{
    /// <summary>
    /// Anonymous registration page reached through a signed, expiring share link.
    /// Access is authorized by validating the token, not the login cookie.
    /// </summary>
    [AllowAnonymous]
    public class RegisterModel : PageModel
    {
        private readonly AppDbContext _dbContext;
        private readonly IShareLinkService _shareLinkService;
        private readonly ILogger<RegisterModel> _logger;

        public RegisterModel(AppDbContext dbContext, IShareLinkService shareLinkService, ILogger<RegisterModel> logger)
        {
            _dbContext = dbContext;
            _shareLinkService = shareLinkService;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        [BindProperty]
        public Staff Input { get; set; } = new();

        public bool IsValid { get; private set; }

        public List<StaffType> StaffTypes { get; private set; } = new();

        public List<StaffRole> StaffRoles { get; private set; } = new();

        /// <summary>Document types required per role, so the registration page
        /// can tell the staff member what they must upload.</summary>
        public Dictionary<int, List<string>> RoleRequiredDocuments { get; private set; } = new();

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            IsValid = _shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Register, out _, out _);
            if (IsValid)
            {
                await LoadLookupsAsync(cancellationToken);
            }
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            if (!_shareLinkService.TryValidateToken(Token ?? "", ShareLinkPurposes.Register, out _, out _))
            {
                IsValid = false;
                return Page();
            }

            IsValid = true;

            if (!ModelState.IsValid)
            {
                await LoadLookupsAsync(cancellationToken);
                return Page();
            }

            try
            {
                var exists = await _dbContext.Staff
                    .AnyAsync(s => s.Email == Input.Email, cancellationToken);
                if (exists)
                {
                    StatusMessage = "A staff member with this email address is already registered.";
                    await LoadLookupsAsync(cancellationToken);
                    return Page();
                }

                var staffType = await _dbContext.StaffTypes
                    .FirstOrDefaultAsync(t => t.Id == Input.StaffTypeId, cancellationToken);
                if (staffType is null)
                {
                    ModelState.AddModelError("Input.StaffTypeId", "Please select a staff type.");
                    await LoadLookupsAsync(cancellationToken);
                    return Page();
                }

                // Staff are assigned a role at registration, which determines the
                // documents they must upload.
                var role = await _dbContext.StaffRoles
                    .FirstOrDefaultAsync(r => r.Id == Input.StaffRoleId, cancellationToken);
                if (role is null)
                {
                    ModelState.AddModelError("Input.StaffRoleId", "Please select a role.");
                    await LoadLookupsAsync(cancellationToken);
                    return Page();
                }

                Input.RegisteredAt = DateTimeOffset.UtcNow;
                _dbContext.Staff.Add(Input);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // The human-readable StaffId combines the type prefix with the
                // sequence-generated staff number (e.g. P42 or E42), which is
                // auto-incremented by one for every registered staff member.
                Input.StaffId = $"{staffType.Prefix}{Input.StaffNumber}";
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Send the staff member on to the terms page with a short-lived token
                // that carries their identity inside the signed payload.
                var termsToken = _shareLinkService.CreateToken(
                    ShareLinkPurposes.Terms, "", DateTimeOffset.UtcNow.AddHours(24), Input.Id);
                return RedirectToPage("/Terms", new { token = termsToken });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register staff member.");
                StatusMessage = "Registration failed. Please try again.";
                await LoadLookupsAsync(cancellationToken);
                return Page();
            }
        }

        private async Task LoadLookupsAsync(CancellationToken cancellationToken)
        {
            StaffTypes = await _dbContext.StaffTypes.AsNoTracking().ToListAsync(cancellationToken);
            StaffRoles = await _dbContext.StaffRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken);
            RoleRequiredDocuments = (await _dbContext.StaffRoleDocumentTypes.AsNoTracking()
                    .Select(r => new { r.StaffRoleId, TypeName = r.DocumentType.Name })
                    .ToListAsync(cancellationToken))
                .GroupBy(r => r.StaffRoleId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.TypeName).ToList());
        }
    }
}
