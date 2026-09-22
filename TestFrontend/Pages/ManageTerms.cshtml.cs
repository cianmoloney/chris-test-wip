using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.TermsWrite)]
public class ManageTermsModel(FunctionApiClient api) : PageModel
{
    [BindProperty(Name = "Input")] public PublishTermsRequest Input { get; set; } = new();
    [BindProperty(Name = "RoleInput"), ValidateNever] public SaveTermsRoleRequest RoleInput { get; set; } = new();
    [BindProperty(SupportsGet = true)] public int? DocumentId { get; set; }
    [BindProperty(SupportsGet = true)] public int? VersionId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; } = "view";
    [TempData] public string? StatusMessage { get; set; }
    public List<TermsDocumentResponse> Terms { get; private set; } = [];
    public List<TermsVersionResponse> Versions { get; private set; } = [];
    public List<LookupResponse> StaffRoles { get; private set; } = [];
    public TermsDocumentResponse? SelectedDocument => Terms.SingleOrDefault(terms => terms.Id == DocumentId);
    public TermsVersionResponse? SelectedVersion => Versions.SingleOrDefault(version => version.Id == VersionId);
    public bool IsEditing => Mode is "version" or "document";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Mode is not ("view" or "version" or "document")) return BadRequest();
        var error = await LoadAsync(cancellationToken);
        if (error is not null) return error;
        if (SelectedDocument is { } selected)
            RoleInput = new() { StaffRoleIds = selected.StaffRoleIds, ExpectedRevision = selected.RoleRevision };
        if (Mode == "version")
        {
            if (SelectedDocument is null) return NotFound();
            var latest = Versions.Select(version => version.Version).DefaultIfEmpty(0).Max();
            string Translation(string language) => Versions.FirstOrDefault(version => version.Version == latest && version.Language == language)?.Content ?? "";
            Input = new()
            {
                TermsDocumentId = SelectedDocument.Id, Title = SelectedDocument.Title,
                English = Translation("en"), Polish = Translation("pl"), Ukrainian = Translation("uk")
            };
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!User.HasClaim("permission", Permissions.TermsWrite)) return Forbid();
        if (!IsEditing || (Mode == "version" && DocumentId is null)) return BadRequest();
        var error = await LoadAsync(cancellationToken);
        if (error is not null) return error;
        if (Mode == "version")
        {
            Input = Input with { TermsDocumentId = SelectedDocument!.Id, Title = SelectedDocument.Title };
            ModelState.Remove("Input.Title");
        }
        else Input = Input with { TermsDocumentId = null };
        ModelState.Remove("Input.TermsDocumentId");
        if (ModelState.IsValid)
        {
            try
            {
                var published = await api.PostAsync<PublishTermsRequest, TermsDocumentResponse>("terms", Input, cancellationToken);
                StatusMessage = "New terms version published.";
                return RedirectToPage(new { DocumentId = published.Id });
            }
            catch (FunctionApiException exception)
            {
                if (exception.StatusCode == HttpStatusCode.Forbidden) return Forbid();
                ModelState.AddModelError("", exception.Message);
                foreach (var field in exception.Errors)
                    foreach (var message in field.Value)
                        ModelState.AddModelError($"Input.{field.Key}", message);
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostRoleAsync(CancellationToken cancellationToken)
    {
        if (!User.HasClaim("permission", Permissions.TermsWrite)) return Forbid();
        if (Mode != "view" || DocumentId is null) return BadRequest();
        foreach (var key in ModelState.Keys.Where(key => key == "Input" || key.StartsWith("Input.", StringComparison.Ordinal)).ToList())
            ModelState.Remove(key);
        var error = await LoadAsync(cancellationToken);
        if (error is not null) return error;
        var validation = new List<ValidationResult>();
        Validator.TryValidateObject(RoleInput, new ValidationContext(RoleInput), validation, true);
        foreach (var result in validation)
            ModelState.AddModelError($"RoleInput.{result.MemberNames.FirstOrDefault()}", result.ErrorMessage!);
        if (ModelState.IsValid)
        {
            try
            {
                await api.PutAsync($"terms/{DocumentId}/staff-roles", RoleInput, cancellationToken);
                StatusMessage = "Required staff roles saved.";
                return RedirectToPage(new { DocumentId, VersionId });
            }
            catch (FunctionApiException exception)
            {
                if (exception.StatusCode == HttpStatusCode.Forbidden) return Forbid();
                ModelState.AddModelError("", exception.Message);
            }
            catch (HttpRequestException)
            {
                ModelState.AddModelError("", "Could not save the required staff roles. Please try again.");
            }
        }
        return Page();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        Terms = await api.GetAsync<List<TermsDocumentResponse>>("terms", cancellationToken);
        if (Mode == "document")
        {
            DocumentId = null;
            VersionId = null;
            return null;
        }
        DocumentId ??= Terms.FirstOrDefault()?.Id;
        if (DocumentId is null) return null;
        if (SelectedDocument is null) return NotFound();
        if (!IsEditing)
            StaffRoles = (await api.GetAsync<LookupsResponse>("lookups", cancellationToken)).StaffRoles;
        try
        {
            Versions = await api.GetAsync<List<TermsVersionResponse>>($"terms/{DocumentId}/versions", cancellationToken);
        }
        catch (FunctionApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound();
        }
        VersionId ??= Versions.FirstOrDefault()?.Id;
        return VersionId is not null && SelectedVersion is null ? NotFound() : null;
    }
}