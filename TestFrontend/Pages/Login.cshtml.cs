using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages
{
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("login")]
    public class LoginModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<LoginModel> _logger;

        public LoginModel(FunctionApiClient api, ILogger<LoginModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        [BindProperty]
        public string? Password { get; set; }
        [BindProperty] public string Email { get; set; } = "";
        [BindProperty] public string? ChallengeToken { get; set; }
        [BindProperty] public string? Code { get; set; }

        public string? ErrorMessage { get; set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            var apiPath = string.IsNullOrEmpty(ChallengeToken) ? "accounts/login" : "accounts/mfa";
            LoginResponse result;
            try
            {
                result = string.IsNullOrEmpty(ChallengeToken)
                    ? await _api.PostAsync<LoginRequest, LoginResponse>("accounts/login", new(Email, Password ?? ""), HttpContext.RequestAborted)
                    : await _api.PostAsync<VerifyMfaRequest, LoginResponse>("accounts/mfa", new(ChallengeToken, Code ?? ""), HttpContext.RequestAborted);
            }
            catch (FunctionApiException exception)
            {
                _logger.LogError(exception, "Sign-in API request {ApiPath} failed with HTTP {StatusCode}.", apiPath, (int)exception.StatusCode);
                ErrorMessage = exception.Message;
                return Page();
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "Sign-in API request {ApiPath} could not be completed.", apiPath);
                ErrorMessage = "Sign-in is temporarily unavailable.";
                return Page();
            }
            Password = null;
            ModelState.Remove(nameof(Password));
            if (result.RequiresMfa)
            {
                ChallengeToken = result.Token;
                return Page();
            }

            var user = result.User!;
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Email), new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Role, user.Role), new("session", result.Token)
            };
            claims.AddRange(user.Permissions.Select(permission => new Claim("permission", permission)));
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = result.ExpiresAt, AllowRefresh = false });

            if (returnUrl is not null && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToPage("/Index");
        }

        public async Task<IActionResult> OnPostLogoutAsync()
        {
            try { await _api.PostAsync<object, object>("accounts/logout", new { }, HttpContext.RequestAborted); }
            catch (FunctionApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
            finally { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
            return RedirectToPage("/Login");
        }
    }
}
