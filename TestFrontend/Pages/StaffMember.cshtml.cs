using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;
using System.Net;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Admin page to view and edit a staff member's details and manage the
    /// documents associated with them, including downloading files from
    /// blob storage.
    /// </summary>
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = Permissions.StaffRead)]
    public class StaffMemberModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StaffMemberModel> _logger;

        public StaffMemberModel(
            FunctionApiClient api,
            BlobServiceClient blobServiceClient,
            IConfiguration configuration,
            ILogger<StaffMemberModel> logger)
        {
            _api = api;
            _blobServiceClient = blobServiceClient;
            _configuration = configuration;
            _logger = logger;
        }

        [BindProperty]
        public UpdateStaffRequest Input { get; set; } = new();

        public StaffResponse? Staff { get; private set; }
        public ReadinessResponse? Readiness { get; private set; }

        public List<DocumentResponse> Documents { get; } = new();

        public List<TermsAcceptanceResponse> Agreements { get; } = new();

        public List<LookupResponse> StaffRoles { get; private set; } = new();
        public List<LookupResponse> StaffTypes { get; private set; } = [];

        public List<LookupResponse> DocumentTypesList { get; private set; } = new();

        /// <summary>Document types required by the staff member's role that have
        /// not yet been uploaded.</summary>
        public List<LookupResponse> MissingDocumentTypes { get; } = new();

        public string? StatusMessage { get; set; }

        public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
        {
            if (!await ReloadAsync(id, cancellationToken) || Staff is null)
            {
                return NotFound();
            }

            Input = new UpdateStaffRequest
            {
                FirstName = Staff.FirstName, LastName = Staff.LastName, Email = Staff.Email,
                PhoneNumber = Staff.PhoneNumber, StaffRoleId = Staff.StaffRoleId, StaffTypeId = Staff.StaffTypeId
            };
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                await ReloadAsync(id, cancellationToken);
                return Page();
            }

            try
            {
                await _api.PutAsync($"staff/{id}", Input, cancellationToken);
                return RedirectToPage(new { id });
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update staff member {StaffId}.", id);
                StatusMessage = "Could not save changes. Please try again.";
                await ReloadAsync(id, cancellationToken);
                return Page();
            }
        }

        public async Task<IActionResult> OnPostUpdateDocumentAsync(
            int id, int documentId, int? documentTypeId, string? documentNumber,
            DateTimeOffset? startDate, DateTimeOffset? expiryDate,
            CancellationToken cancellationToken)
        {
            try
            {
                await _api.PutAsync($"staff/{id}/documents/{documentId}", new UpdateStaffDocumentRequest
                {
                    DocumentTypeId = documentTypeId, DocumentNumber = documentNumber,
                    StartDate = startDate, ExpiryDate = expiryDate
                }, cancellationToken);
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update document {DocumentId} for staff member {StaffId}.", documentId, id);
                StatusMessage = "Could not save changes. Please try again.";
                return await OnGetAsync(id, cancellationToken);
            }

            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnGetDownloadAsync(int id, int documentId, CancellationToken cancellationToken)
        {
            DocumentResponse document;
            try
            {
                document = await _api.GetAsync<DocumentResponse>($"staff/{id}/documents/{documentId}", cancellationToken);
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound();
            }
            var blobName = document?.BlobName ?? document?.Name;
            if (document is null || string.IsNullOrWhiteSpace(blobName))
            {
                return NotFound();
            }

            try
            {
                var containerName = document.ContainerName ?? _configuration["AzureStorage:ContainerName"] ?? "uploads";
                var blobClient = _blobServiceClient
                    .GetBlobContainerClient(containerName)
                    .GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync(cancellationToken))
                {
                    return NotFound();
                }

                var stream = await blobClient.OpenReadAsync(cancellationToken: cancellationToken);
                var fileName = Path.GetFileName(blobName);
                return File(stream, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download blob {BlobName} for staff member {StaffId}.", blobName, id);
                return RedirectToPage(new { id });
            }
        }

        private async Task<bool> ReloadAsync(int id, CancellationToken cancellationToken)
        {
            try
            {
                var response = await _api.GetAsync<StaffMemberResponse>($"staff/{id}", cancellationToken);
                Staff = response.Staff;
                Readiness = response.Readiness;
                StaffRoles = response.StaffRoles;
                StaffTypes = response.StaffTypes ?? [];
                DocumentTypesList = response.DocumentTypes;
                Documents.Clear();
                Documents.AddRange(response.Documents);
                Agreements.Clear();
                Agreements.AddRange(response.Agreements);
                MissingDocumentTypes.Clear();
                MissingDocumentTypes.AddRange(response.MissingDocumentTypes);
                return true;
            }
            catch (FunctionApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public async Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken cancellationToken)
        {
            if (!User.HasClaim("permission", Permissions.StaffWrite)) return Forbid();
            await _api.DeleteAsync($"staff/{id}", cancellationToken);
            return RedirectToPage("/Staff");
        }
    }
}
