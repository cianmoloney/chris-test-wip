using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

public class AccountModel(FunctionApiClient api) : PageModel
{
    [BindProperty] public bool MfaEnabled { get; set; }
    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        MfaEnabled = (await api.GetAsync<UserResponse>("accounts/me", cancellationToken)).MfaEnabled;
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await api.PutAsync("accounts/me/mfa", new MfaPreferenceRequest(MfaEnabled), cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}