using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;
using System.Net;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Anonymous registration page reached through a signed, expiring share link.
    /// Access is authorized by validating the token, not the login cookie.
    /// </summary>
    [AllowAnonymous]
    public class RegisterModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<RegisterModel> _logger;

        public RegisterModel(FunctionApiClient api, ILogger<RegisterModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        [BindProperty]
        public CreateStaffRequest Input { get; set; } = new();

        public bool IsValid { get; private set; }

        public List<LookupResponse> StaffTypes { get; private set; } = new();

        public List<LookupResponse> StaffRoles { get; private set; } = new();

        /// <summary>Document types required per role, so the registration page
        /// can tell the staff member what they must upload.</summary>
        public Dictionary<int, List<string>> RoleRequiredDocuments { get; private set; } = new();

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadLookupsAsync(cancellationToken);
        }

        public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
        {
            await LoadLookupsAsync(cancellationToken);
            if (!IsValid)
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
                await _api.PostAsync<LinkRegistrationRequest, StaffResponse>("public/register", new(Token!, Input), cancellationToken);
                return RedirectToPage("/RegistrationComplete", new { lang = Request.Query["lang"].ToString() });
            }
            catch (FunctionApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                StatusMessage = ex.Message;
                foreach (var error in ex.Errors)
                    foreach (var message in error.Value)
                        ModelState.AddModelError($"Input.{error.Key}", message);
                await LoadLookupsAsync(cancellationToken);
                return Page();
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
            try
            {
                var response = await _api.PostAsync<ResolveLinkRequest, PublicLinkResponse>("public/resolve", new(Token ?? "", "register"), cancellationToken);
                var lookups = response.Lookups!;
                IsValid = true;
                StaffTypes = lookups.StaffTypes;
                StaffRoles = lookups.StaffRoles;
                RoleRequiredDocuments = lookups.RoleRequiredDocuments;
            }
            catch (FunctionApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            { IsValid = false; }
            catch (HttpRequestException) { IsValid = false; StatusMessage = "Unavailable"; }
        }
    }
}
