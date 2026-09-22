using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Services;
using TestShared;

namespace TestFrontend.Pages
{
    /// <summary>
    /// Admin page listing registered staff with filtering and sorting.
    /// </summary>
    public class StaffModel : PageModel
    {
        private readonly FunctionApiClient _api;
        private readonly ILogger<StaffModel> _logger;

        public StaffModel(FunctionApiClient api, ILogger<StaffModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        public List<StaffResponse> StaffList { get; } = new();

        public Dictionary<int, DateTimeOffset> LastTermsAcceptedAt { get; } = new();
        public Dictionary<int, ReadinessResponse> Readiness { get; private set; } = [];

        [BindProperty(SupportsGet = true)]
        public string? Filter { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? SortBy { get; set; }

        public string? StatusMessage { get; set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await _api.GetAsync<StaffListResponse>(
                    $"staff?filter={Uri.EscapeDataString(Filter ?? "")}&sortBy={Uri.EscapeDataString(SortBy ?? "")}", cancellationToken);
                StaffList.AddRange(response.Staff);
                Readiness = response.Readiness ?? [];
                foreach (var entry in response.LastTermsAcceptedAt)
                {
                    LastTermsAcceptedAt[entry.Key] = entry.Value;
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
