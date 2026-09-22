using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.StaffWrite)]
public class StaffNewModel(FunctionApiClient api) : PageModel
{
    [BindProperty] public CreateStaffRequest Input { get; set; } = new();
    public LookupsResponse Lookups { get; private set; } = new([], [], [], []);
    public async Task OnGetAsync(CancellationToken cancellationToken) => Lookups = await api.GetAsync<LookupsResponse>("lookups", cancellationToken);
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var staff = await api.PostAsync<CreateStaffRequest, StaffResponse>("staff", Input, cancellationToken);
                return RedirectToPage("/StaffMember", new { id = staff.Id });
            }
            catch (FunctionApiException exception) { ModelState.AddModelError("", exception.Message); }
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}