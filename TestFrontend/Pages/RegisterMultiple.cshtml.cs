using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages;

[AllowAnonymous]
public class RegisterMultipleModel(FunctionApiClient api, ILogger<RegisterMultipleModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty, MinLength(1), MaxLength(LinkMultipleRegistrationRequest.MaximumStaff)]
    public List<CreateStaffRequest> Staff { get; set; } = [new()];
    public bool IsValid { get; private set; }
    public bool Completed { get; private set; }
    public int RegisteredCount { get; private set; }
    public string? StatusMessage { get; private set; }
    public List<LookupResponse> StaffTypes { get; private set; } = [];
    public List<LookupResponse> StaffRoles { get; private set; } = [];
    public Dictionary<int, List<string>> RoleRequiredDocuments { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAddAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!IsValid) return Page();
        if (Staff.Count < LinkMultipleRegistrationRequest.MaximumStaff)
        {
            Staff.Add(new());
            ModelState.Clear();
        }
        else StatusMessage = "BatchLimit";
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int index, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!IsValid) return Page();
        if (Staff.Count > 1 && index >= 0 && index < Staff.Count)
        {
            Staff.RemoveAt(index);
            ModelState.Clear();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        if (!IsValid) return Page();
        if (Staff.Count is < 1 or > LinkMultipleRegistrationRequest.MaximumStaff)
        {
            StatusMessage = "BatchLimit";
            return Page();
        }
        if (!ModelState.IsValid) return Page();

        try
        {
            var result = await api.PostAsync<LinkMultipleRegistrationRequest, MultipleRegistrationResponse>(
                "public/register-many", new(Token!, Staff), cancellationToken);
            Completed = true;
            RegisteredCount = result.Staff.Count;
            Staff.Clear();
            ModelState.Clear();
            return Page();
        }
        catch (FunctionApiException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            IsValid = false;
        }
        catch (FunctionApiException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            StatusMessage = "BatchNotRegistered";
            foreach (var error in exception.Errors)
                foreach (var message in error.Value)
                    ModelState.AddModelError(error.Key, message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to register staff batch.");
            StatusMessage = "Failure";
        }
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsValid = false;
        try
        {
            var response = await api.PostAsync<ResolveLinkRequest, PublicLinkResponse>("public/resolve",
                new(Token ?? "", ShareLinkPurposes.RegisterMultiple), cancellationToken);
            var lookups = response.Lookups ?? throw new InvalidOperationException("Registration lookups are missing.");
            StaffTypes = lookups.StaffTypes;
            StaffRoles = lookups.StaffRoles;
            RoleRequiredDocuments = lookups.RoleRequiredDocuments;
            IsValid = true;
        }
        catch (FunctionApiException exception) when (exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            StatusMessage = "InvalidLink";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to resolve multiple registration link.");
            StatusMessage = "Failure";
        }
    }
}