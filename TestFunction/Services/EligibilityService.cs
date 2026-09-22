using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class EligibilityService(AppDbContext database, TimeProvider clock)
{
    public static bool IsCurrent(DocumentEntry document, DateTimeOffset now) => document.IsValid
        && document.Status == DocumentStatus.Validated && document.ScanPassed && !document.IsArchived
        && (document.StartDate is null || document.StartDate.Value.UtcDateTime.Date <= now.UtcDateTime.Date)
        && (document.ExpiryDate is null || document.ExpiryDate.Value.UtcDateTime.Date >= now.UtcDateTime.Date);

    public async Task<Dictionary<int, ReadinessResponse>> GetAsync(List<Staff> staff, CancellationToken cancellationToken)
    {
        var ids = staff.Select(worker => worker.Id).ToList();
        var documents = await database.Documents.AsNoTracking().Where(document => document.StaffId != null && ids.Contains(document.StaffId.Value)).ToListAsync(cancellationToken);
        var requirements = await database.StaffRoleDocumentTypes.AsNoTracking().Include(requirement => requirement.DocumentType).ToListAsync(cancellationToken);
        var assignments = await database.StaffTermsAssignments.AsNoTracking().Include(assignment => assignment.TermsDocument)
            .Where(assignment => ids.Contains(assignment.StaffId)).ToListAsync(cancellationToken);
        var acceptances = await database.StaffTermsAcceptances.AsNoTracking().Include(acceptance => acceptance.TermsDocumentVersion)
            .Where(acceptance => ids.Contains(acceptance.StaffId)).ToListAsync(cancellationToken);
        return staff.ToDictionary(worker => worker.Id, worker =>
        {
            var reasons = new List<string>();
            if (worker.StaffRoleId is null) reasons.Add("No job role assigned.");
            foreach (var requirement in requirements.Where(requirement => requirement.StaffRoleId == worker.StaffRoleId))
                if (!documents.Any(document => document.StaffId == worker.Id && document.DocumentTypeId == requirement.DocumentTypeId && IsCurrent(document, clock.GetUtcNow())))
                    reasons.Add($"Current validated document required: {requirement.DocumentType.Name}.");
            foreach (var assignment in assignments.Where(assignment => assignment.StaffId == worker.Id))
                if (!acceptances.Any(acceptance => acceptance.StaffId == worker.Id
                    && acceptance.TermsDocumentVersion.TermsDocumentId == assignment.TermsDocumentId && acceptance.TermsDocumentVersion.Version == assignment.Version))
                    reasons.Add($"Terms outstanding: {assignment.TermsDocument.Title}, version {assignment.Version}.");
            return new ReadinessResponse(reasons.Count == 0, reasons);
        });
    }
}