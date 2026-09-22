using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.TermsWrite)]
public class ManageTermsModel(FunctionApiClient api) : PageModel
{
    [BindProperty] public PublishTermsRequest Input { get; set; } = new();
    public List<TermsDocumentResponse> Terms { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken cancellationToken) => Terms = await api.GetAsync<List<TermsDocumentResponse>>("terms", cancellationToken);
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            try
            {
                await api.PostAsync<PublishTermsRequest, TermsDocumentResponse>("terms", Input, cancellationToken);
                return RedirectToPage();
            }
            catch (FunctionApiException exception) { ModelState.AddModelError("", exception.Message); }
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}