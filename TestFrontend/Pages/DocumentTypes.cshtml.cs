using System.Net;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.DocumentsWrite)]
public class DocumentTypesModel(FunctionApiClient api, ILogger<DocumentTypesModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty(SupportsGet = true)] public int? RoleId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Mode { get; set; } = "requirements";
    [BindProperty(Name = "Input"), ValidateNever] public SaveDocumentTypeRequest Input { get; set; } = new();
    [BindProperty(Name = "RoleInput"), ValidateNever] public CreateStaffRoleRequest RoleInput { get; set; } = new();
    [BindProperty(Name = "Requirements"), ValidateNever] public SaveStaffRoleDocumentsRequest Requirements { get; set; } = new();
    [TempData] public string? StatusMessage { get; set; }
    public DocumentTypeManagementResponse Data { get; private set; } = new([], []);
    public LookupResponse? SelectedRole => Data.StaffRoles.SingleOrDefault(role => role.Id == RoleId);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Mode is not ("requirements" or "role" or "type")) return BadRequest();
        await LoadAsync(cancellationToken);
        if (RoleId is not null && SelectedRole is null) return NotFound();
        if (Id is not null && Mode != "role")
        {
            Mode = "type";
            var type = Data.DocumentTypes.SingleOrDefault(type => type.Id == Id);
            if (type is null) return NotFound();
            Input = new() { Name = type.Name, TextIdentifier = type.TextIdentifier ?? "", StaffRoleIds = type.StaffRoleIds };
        }
        if (Mode == "requirements")
            Requirements = new() { DocumentTypeIds = Data.DocumentTypes.Where(type => type.StaffRoleIds.Contains(RoleId ?? 0)).Select(type => type.Id).ToList() };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!User.HasClaim("permission", Permissions.DocumentsWrite)) return Forbid();
        if (Mode is not ("requirements" or "role" or "type")) return BadRequest();
        if (Mode == "requirements" && RoleId is null) return BadRequest();
        await LoadAsync(cancellationToken);
        if (RoleId is not null && SelectedRole is null) return NotFound();
        var activeInput = Mode switch { "role" => "RoleInput", "type" => "Input", _ => "Requirements" };
        foreach (var key in ModelState.Keys.Where(key => new[] { "Input", "RoleInput", "Requirements" }
            .Any(prefix => prefix != activeInput && (key == prefix || key.StartsWith(prefix + ".", StringComparison.Ordinal)))).ToList())
            ModelState.Remove(key);
        if (Mode == "type")
        {
            var existing = Id is null ? null : Data.DocumentTypes.SingleOrDefault(type => type.Id == Id);
            if (Id is not null && existing is null) return NotFound();
            Input = Input with { StaffRoleIds = existing?.StaffRoleIds ?? [] };
            foreach (var key in ModelState.Keys.Where(key => key.StartsWith("Input.StaffRoleIds", StringComparison.Ordinal)).ToList())
                ModelState.Remove(key);
        }
        object activeRequest = Mode switch { "role" => RoleInput, "type" => Input, _ => Requirements };
        var validationErrors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(activeRequest, new ValidationContext(activeRequest), validationErrors, true))
            foreach (var error in validationErrors)
                foreach (var member in error.MemberNames.DefaultIfEmpty(""))
                    ModelState.AddModelError($"{activeInput}.{member}", error.ErrorMessage ?? "Check the entered details.");
        if (ModelState.IsValid)
        {
            try
            {
                if (Mode == "role")
                {
                    var created = await api.PostAsync<CreateStaffRoleRequest, LookupResponse>("staff-roles", RoleInput, cancellationToken);
                    RoleId = created.Id;
                    StatusMessage = "Staff role added.";
                }
                else if (Mode == "requirements")
                {
                    await api.PutAsync($"staff-roles/{RoleId}/document-types", Requirements, cancellationToken);
                    StatusMessage = "Required documents saved.";
                }
                else if (Id is null)
                {
                    await api.PostAsync<SaveDocumentTypeRequest, DocumentTypeResponse>("document-types", Input, cancellationToken);
                    StatusMessage = "Document type added.";
                }
                else
                {
                    await api.PutAsync($"document-types/{Id}", Input, cancellationToken);
                    StatusMessage = "Document type saved.";
                }
                return RedirectToPage(new { RoleId, Id = (int?)null, Mode = "requirements" });
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
                if (exception.Errors.Count == 0) ModelState.AddModelError("", exception.Message);
                foreach (var error in exception.Errors)
                    foreach (var message in error.Value)
                        ModelState.AddModelError($"{activeInput}.{error.Key}", message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to save role manager changes for {Mode}.", Mode);
                ModelState.AddModelError("", "Could not save changes. Please try again.");
            }
        }
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Data = await api.GetAsync<DocumentTypeManagementResponse>("document-types", cancellationToken);
        RoleId ??= Data.StaffRoles.FirstOrDefault()?.Id;
    }
}