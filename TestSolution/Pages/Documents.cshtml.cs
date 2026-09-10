using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;

namespace TestSolution.Pages
{
    /// <summary>
    /// Admin page to review, validate, reject, edit, and associate uploaded
    /// documents with staff.
    /// </summary>
    public class DocumentsModel : PageModel
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<DocumentsModel> _logger;

        public DocumentsModel(AppDbContext dbContext, ILogger<DocumentsModel> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public List<DocumentEntry> Documents { get; } = new();
        public List<Staff> StaffList { get; } = new();
        public List<DocumentType> DocumentTypesList { get; } = new();

        [BindProperty(SupportsGet = true)]
        public string? StatusFilter { get; set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            await LoadAsync(cancellationToken);
        }

        public async Task<IActionResult> OnPostSetStatusAsync(int id, int status, CancellationToken cancellationToken)
        {
            var document = await _dbContext.Documents.FindAsync([id], cancellationToken);
            if (document is null)
            {
                return NotFound();
            }

            document.Status = (DocumentStatus)status;
            document.IsValid = document.Status == DocumentStatus.Validated;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return RedirectToPage(new { StatusFilter });
        }

        public async Task<IActionResult> OnPostUpdateAsync(
            int id, string? name, int? documentTypeId, string? documentNumber,
            string? email, string? phone,
            DateTimeOffset? startDate, DateTimeOffset? expiryDate, int? staffId,
            CancellationToken cancellationToken)
        {
            var document = await _dbContext.Documents.FindAsync([id], cancellationToken);
            if (document is null)
            {
                return NotFound();
            }

            try
            {
                document.Name = name?.Trim() ?? document.Name;
                document.DocumentTypeId = documentTypeId;
                document.DocumentNumber = documentNumber?.Trim();
                document.Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
                document.Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
                document.StartDate = startDate;
                document.ExpiryDate = expiryDate;
                document.StaffId = staffId;
                await _dbContext.SaveChangesAsync(cancellationToken);
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

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            var query = _dbContext.Documents.AsNoTracking()
                .Include(d => d.Staff)
                .Include(d => d.Type)
                .AsQueryable();

            query = StatusFilter switch
            {
                "pending" => query.Where(d => d.Status == DocumentStatus.PendingReview),
                "failed" => query.Where(d => d.Status == DocumentStatus.ParseFailed),
                "validated" => query.Where(d => d.Status == DocumentStatus.Validated),
                "rejected" => query.Where(d => d.Status == DocumentStatus.Rejected),
                _ => query,
            };

            Documents.AddRange(await query.OrderByDescending(d => d.Timestamp).ToListAsync(cancellationToken));
            StaffList.AddRange(await _dbContext.Staff.AsNoTracking()
                .OrderBy(s => s.LastName).ThenBy(s => s.FirstName)
                .ToListAsync(cancellationToken));
            DocumentTypesList.AddRange(await _dbContext.DocumentTypes.AsNoTracking()
                .OrderBy(t => t.Name)
                .ToListAsync(cancellationToken));
        }
    }
}
