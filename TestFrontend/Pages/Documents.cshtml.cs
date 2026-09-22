using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;
using System.Net;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Admin page to review, validate, reject, edit, and associate uploaded
    /// documents with staff.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = Permissions.StaffRead)]
    public class DocumentsModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<DocumentsModel> _logger;

        public DocumentsModel(FunctionApiClient api, ILogger<DocumentsModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        public List<DocumentResponse> Documents { get; } = new();
        public List<StaffResponse> StaffList { get; } = new();
        public List<LookupResponse> DocumentTypesList { get; } = new();

        [BindProperty(SupportsGet = true)]
        public string? StatusFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? Filter { get; set; }
        [BindProperty(SupportsGet = true)] public string? SortBy { get; set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);
        }

        public async Task<IActionResult> OnPostSetStatusAsync(int id, int status, CancellationToken cancellationToken)
        {
            try
            {
                await _api.PutAsync($"documents/{id}/status", new SetDocumentStatusRequest((DocumentStatus)status), cancellationToken);
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (FunctionApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden)
            {
                StatusMessage = ex.Message;
                await LoadAsync(cancellationToken);
                return Page();
            }
            return RedirectToPage(new { StatusFilter });
        }

        public async Task<IActionResult> OnPostUpdateAsync(
            int id, string? name, int? documentTypeId, string? documentNumber,
            string? email, string? phone,
            DateTimeOffset? startDate, DateTimeOffset? expiryDate, int? staffId,
            CancellationToken cancellationToken)
        {
            try
            {
                await _api.PutAsync($"documents/{id}", new UpdateDocumentRequest
                {
                    Name = name, DocumentTypeId = documentTypeId, DocumentNumber = documentNumber,
                    Email = email, Phone = phone, StartDate = startDate, ExpiryDate = expiryDate, StaffId = staffId
                }, cancellationToken);
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update document {DocumentId}.", id);
                StatusMessage = "Could not save changes. Please try again.";
                await LoadAsync(cancellationToken);
                return Page();
            }

            return RedirectToPage(new { StatusFilter });
        }

        public async Task<IActionResult> OnPostReassignAsync(int id, int? staffId, CancellationToken cancellationToken)
        {
            if (!User.HasClaim("permission", Permissions.DocumentsWrite)) return Forbid();
            if (!ModelState.IsValid)
            {
                StatusMessage = "Please select a valid staff member.";
                await LoadAsync(cancellationToken);
                return Page();
            }

            try
            {
                await _api.PutAsync($"documents/{id}/staff", new ReassignDocumentRequest(staffId), cancellationToken);
                return RedirectToPage(new { StatusFilter, Filter, SortBy });
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (FunctionApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden)
            {
                StatusMessage = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to reassign document {DocumentId}.", id);
                StatusMessage = "Could not reassign the document. Please try again.";
            }

            await LoadAsync(cancellationToken);
            return Page();
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            var response = await _api.GetAsync<DocumentListResponse>(
                $"documents?status={Uri.EscapeDataString(StatusFilter ?? "")}&filter={Uri.EscapeDataString(Filter ?? "")}&sortBy={Uri.EscapeDataString(SortBy ?? "")}", cancellationToken);
            Documents.AddRange(response.Documents);
            StaffList.AddRange(response.Staff);
            DocumentTypesList.AddRange(response.DocumentTypes);
        }

        public async Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken cancellationToken)
        {
            if (!User.HasClaim("permission", Permissions.DocumentsWrite)) return Forbid();
            await _api.DeleteAsync($"documents/{id}", cancellationToken);
            return RedirectToPage();
        }
    }
}
