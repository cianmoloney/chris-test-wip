using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;

namespace TestSolution.Pages
{
    /// <summary>
    /// Admin page to view and edit a staff member's details and manage the
    /// documents associated with them, including downloading files from
    /// blob storage.
    /// </summary>
    public class StaffMemberModel : PageModel
    {
        private readonly AppDbContext _dbContext;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<StaffMemberModel> _logger;

        public StaffMemberModel(
            AppDbContext dbContext,
            BlobServiceClient blobServiceClient,
            IConfiguration configuration,
            ILogger<StaffMemberModel> logger)
        {
            _dbContext = dbContext;
            _blobServiceClient = blobServiceClient;
            _configuration = configuration;
            _logger = logger;
        }

        [BindProperty]
        public Staff Input { get; set; } = new();

        public List<DocumentEntry> Documents { get; } = new();

        public List<StaffTermsAcceptance> Agreements { get; } = new();

        public List<StaffRole> StaffRoles { get; private set; } = new();

        public List<DocumentType> DocumentTypesList { get; private set; } = new();

        /// <summary>Document types required by the staff member's role that have
        /// not yet been uploaded.</summary>
        public List<DocumentType> MissingDocumentTypes { get; } = new();

        public string? StatusMessage { get; set; }

        public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
        {
            var staff = await _dbContext.Staff.AsNoTracking()
                .Include(s => s.StaffType)
                .Include(s => s.StaffRole)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (staff is null)
            {
                return NotFound();
            }

            Input = staff;
            await LoadDocumentsAsync(staff, cancellationToken);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                await ReloadAsync(id, cancellationToken);
                return Page();
            }

            var staff = await _dbContext.Staff.FindAsync([Input.Id], cancellationToken);
            if (staff is null)
            {
                return NotFound();
            }

            try
            {
                staff.FirstName = Input.FirstName;
                staff.LastName = Input.LastName;
                staff.Email = Input.Email;
                staff.PhoneNumber = Input.PhoneNumber;
                staff.StaffRoleId = Input.StaffRoleId;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return RedirectToPage(new { id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update staff member {StaffId}.", Input.Id);
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
            var document = await _dbContext.Documents
                .FirstOrDefaultAsync(d => d.Id == documentId && d.StaffId == id, cancellationToken);
            if (document is null)
            {
                return NotFound();
            }

            try
            {
                document.DocumentTypeId = documentTypeId;
                document.DocumentNumber = documentNumber?.Trim();
                document.StartDate = startDate;
                document.ExpiryDate = expiryDate;
                await _dbContext.SaveChangesAsync(cancellationToken);
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
            var document = await _dbContext.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId && d.StaffId == id, cancellationToken);
            var blobName = document?.BlobName ?? document?.Name;
            if (document is null || string.IsNullOrWhiteSpace(blobName))
            {
                return NotFound();
            }

            try
            {
                var containerName = _configuration["AzureStorage:ContainerName"] ?? "uploads";
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

        private async Task ReloadAsync(int id, CancellationToken cancellationToken)
        {
            var staff = await _dbContext.Staff.AsNoTracking()
                .Include(s => s.StaffRole)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (staff is not null)
            {
                await LoadDocumentsAsync(staff, cancellationToken);
            }
        }

        private async Task LoadDocumentsAsync(Staff staff, CancellationToken cancellationToken)
        {
            try
            {
                StaffRoles = await _dbContext.StaffRoles.AsNoTracking()
                    .OrderBy(r => r.Name)
                    .ToListAsync(cancellationToken);

                DocumentTypesList = await _dbContext.DocumentTypes.AsNoTracking()
                    .OrderBy(t => t.Name)
                    .ToListAsync(cancellationToken);

                Documents.AddRange(await _dbContext.Documents.AsNoTracking()
                    .Include(d => d.Type)
                    .Where(d => d.StaffId == staff.Id)
                    .OrderByDescending(d => d.Timestamp)
                    .ToListAsync(cancellationToken));

                // A staff member is expected to upload ALL documents required by
                // their role; surface any that are still missing.
                if (staff.StaffRoleId is not null)
                {
                    var required = await _dbContext.StaffRoleDocumentTypes.AsNoTracking()
                        .Where(r => r.StaffRoleId == staff.StaffRoleId)
                        .Select(r => r.DocumentType)
                        .ToListAsync(cancellationToken);
                    var uploadedTypeIds = Documents
                        .Where(d => d.DocumentTypeId is not null)
                        .Select(d => d.DocumentTypeId!.Value)
                        .ToHashSet();
                    MissingDocumentTypes.AddRange(required.Where(t => !uploadedTypeIds.Contains(t.Id)));
                }

                Agreements.AddRange(await _dbContext.StaffTermsAcceptances.AsNoTracking()
                    .Where(a => a.StaffId == staff.Id)
                    .Include(a => a.TermsDocumentVersion)
                        .ThenInclude(v => v.TermsDocument)
                    .OrderByDescending(a => a.AcceptedAt)
                    .ToListAsync(cancellationToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load documents for staff member {StaffId}.", staff.Id);
                StatusMessage = "Could not load documents. Please try again.";
            }
        }
    }
}
