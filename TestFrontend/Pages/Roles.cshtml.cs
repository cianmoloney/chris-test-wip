using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Roles = "Admin")]
public class RolesModel(FunctionApiClient api, ILogger<RolesModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public SaveRoleResponsibilitiesRequest Input { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public RolesResponse Data { get; private set; } = new([], []);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        await LoadAsync(cancellationToken);
        Id ??= Data.Roles.FirstOrDefault()?.Id;
        var role = Data.Roles.SingleOrDefault(role => role.Id == Id);
        if (Id is not null && role is null) return NotFound();
        if (role is not null) Input = new() { Responsibilities = role.Responsibilities, ExpectedRevision = role.Revision };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (Id is null) return BadRequest();
        if (ModelState.IsValid)
        {
            try
            {
                await api.PutAsync($"roles/{Id}/responsibilities", Input, cancellationToken);
                StatusMessage = "Role responsibilities saved.";
                return RedirectToPage(new { Id });
            }
            catch (FunctionApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (FunctionApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
            {
                return Forbid();
            }
            catch (FunctionApiException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                ModelState.AddModelError("", exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to save responsibilities for role {RoleId}.", Id);
                ModelState.AddModelError("", "Could not save responsibilities. Please try again.");
            }
        }
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        Data = await api.GetAsync<RolesResponse>("roles", cancellationToken);
}