using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[Authorize(Policy = Permissions.StaffRead)]
[Authorize(Policy = Permissions.DocumentsWrite)]
[Authorize(Policy = Permissions.LinksWrite)]
public class UploadFileModel(FunctionApiClient api, ILogger<UploadFileModel> logger) : PageModel
{
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty] public int? DocumentTypeId { get; set; }
    [TempData] public string? StatusMessage { get; set; }
    public List<DocumentTypeResponse> DocumentTypes { get; private set; } = [];
    public bool TypesLoaded { get; private set; }

    private bool CanUpload => User.HasClaim("permission", Permissions.StaffRead)
        && User.HasClaim("permission", Permissions.DocumentsWrite)
        && User.HasClaim("permission", Permissions.LinksWrite);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!CanUpload) return Forbid();
        await LoadTypesAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!CanUpload) return Forbid();
        await LoadTypesAsync(cancellationToken);
        if (Upload is null || Upload.Length == 0)
            ModelState.AddModelError(nameof(Upload), "Please choose a file to upload.");
        else if (Upload.Length > 20 * 1024 * 1024)
            ModelState.AddModelError(nameof(Upload), "Choose a file up to 20 MB.");
        else if (Path.GetExtension(Upload.FileName).ToLowerInvariant() is not
            (".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".bmp" or ".webp"))
            ModelState.AddModelError(nameof(Upload), "Upload a PDF or supported image.");
        if (TypesLoaded && DocumentTypeId is not null && !DocumentTypes.Any(type => type.Id == DocumentTypeId))
            ModelState.AddModelError(nameof(DocumentTypeId), "Select a valid document type.");
        if (!ModelState.IsValid) return Page();

        try
        {
            var link = await api.PostAsync<CreateLinkRequest, LinkResponse>("links",
                new(ShareLinkPurposes.Upload, null, null, 1), cancellationToken);
            await api.UploadAsync(link.Token, Upload!, DocumentTypeId, cancellationToken);
            StatusMessage = "File uploaded. Awaiting file checks and extraction.";
            return RedirectToPage();
        }
        catch (FunctionApiException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Signed-in file upload failed.");
            ModelState.AddModelError(string.Empty, "Upload failed. Please try again.");
        }
        return Page();
    }

    private async Task LoadTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await api.GetAsync<DocumentTypeManagementResponse>("document-types", cancellationToken);
            DocumentTypes = response.DocumentTypes;
            TypesLoaded = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not load upload document types.");
            ModelState.AddModelError(string.Empty, "Could not load document types. Please try again.");
        }
    }
}