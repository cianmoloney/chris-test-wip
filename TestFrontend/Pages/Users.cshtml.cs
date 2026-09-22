using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.UsersWrite)]
public class UsersModel(FunctionApiClient api) : PageModel
{
    public UsersResponse Data { get; private set; } = new([], []);
    [BindProperty] public SaveUserRequest Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Data = await api.GetAsync<UsersResponse>("users", cancellationToken);
        if (Id is not null && Data.Users.Find(user => user.Id == Id) is { } user)
            Input = new() { Email = user.Email, RoleId = user.RoleId, IsEnabled = user.IsEnabled, MfaEnabled = user.MfaEnabled };
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (Id is null) await api.PostAsync<SaveUserRequest, UserResponse>("users", Input, cancellationToken);
                else await api.PutAsync($"users/{Id}", Input, cancellationToken);
                return RedirectToPage(new { id = (int?)null });
            }
            catch (FunctionApiException exception) { ModelState.AddModelError("", exception.Message); }
        }
        Data = await api.GetAsync<UsersResponse>("users", cancellationToken);
        return Page();
    }
}