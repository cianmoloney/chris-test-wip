using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;

namespace TestSolution.Pages
{
    /// <summary>
    /// Admin page listing registered staff with filtering and sorting.
    /// </summary>
    public class StaffModel : PageModel
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<StaffModel> _logger;

        public StaffModel(AppDbContext dbContext, ILogger<StaffModel> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public List<Staff> StaffList { get; } = new();

        public Dictionary<int, DateTimeOffset> LastTermsAcceptedAt { get; } = new();

        [BindProperty(SupportsGet = true)]
        public string? Filter { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SortBy { get; set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            try
            {
                var query = _dbContext.Staff.AsNoTracking()
                    .Include(s => s.StaffType)
                    .Include(s => s.StaffRole)
                    .AsQueryable();

                if (!string.IsNullOrWhiteSpace(Filter))
                {
                    var filter = Filter.Trim();
                    query = query.Where(s =>
                        s.FirstName.Contains(filter) ||
                        s.LastName.Contains(filter) ||
                        s.Email.Contains(filter) ||
                        (s.StaffRole != null && s.StaffRole.Name.Contains(filter)));
                }

                query = SortBy switch
                {
                    "email" => query.OrderBy(s => s.Email),
                    "role" => query.OrderBy(s => s.StaffRole!.Name),
                    "registered" => query.OrderByDescending(s => s.RegisteredAt),
                    _ => query.OrderBy(s => s.LastName).ThenBy(s => s.FirstName),
                };

                StaffList.AddRange(await query.ToListAsync(cancellationToken));

                // Terms status is derived from the acceptance history table.
                var staffIds = StaffList.Select(s => s.Id).ToList();
                var latest = await _dbContext.StaffTermsAcceptances.AsNoTracking()
                    .Where(a => staffIds.Contains(a.StaffId))
                    .GroupBy(a => a.StaffId)
                    .Select(g => new { StaffId = g.Key, AcceptedAt = g.Max(a => a.AcceptedAt) })
                    .ToListAsync(cancellationToken);
                foreach (var entry in latest)
                {
                    LastTermsAcceptedAt[entry.StaffId] = entry.AcceptedAt;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load staff.");
                StatusMessage = "Could not load staff. Please try again.";
            }
        }
    }
}
