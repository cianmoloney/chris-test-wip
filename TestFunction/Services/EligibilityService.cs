using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class EligibilityService(AppDbContext database, TimeProvider clock)
{
    public static bool IsCurrent(DocumentEntry document, DateTimeOffset now) => document.IsValid
        && document.Status == DocumentStatus.Validated && !document.IsArchived
        && (document.StartDate is null || document.StartDate.Value.UtcDateTime.Date <= now.UtcDateTime.Date)
        && (document.ExpiryDate is null || document.ExpiryDate.Value.UtcDateTime.Date >= now.UtcDateTime.Date);

    public async Task<Dictionary<int, ReadinessResponse>> GetAsync(List<Staff> staff, CancellationToken cancellationToken)
    {
        var ids = staff.Select(worker => worker.Id).ToList();
        var documents = await database.Documents.AsNoTracking().Where(document => document.StaffId != null && ids.Contains(document.StaffId.Value)).ToListAsync(cancellationToken);
        var requirements = await database.StaffRoleDocumentTypes.AsNoTracking().Include(requirement => requirement.DocumentType).ToListAsync(cancellationToken);
        var assignments = await database.StaffTermsAssignments.AsNoTracking().Include(assignment => assignment.TermsDocument)
            .Where(assignment => ids.Contains(assignment.StaffId)).ToListAsync(cancellationToken);
        var roleIds = staff.Where(worker => worker.StaffRoleId != null).Select(worker => worker.StaffRoleId!.Value).Distinct().ToList();
        var assignedTermsIds = assignments.Select(assignment => assignment.TermsDocumentId).Distinct().ToList();
        var roleTerms = await database.StaffRoleTermsDocuments.AsNoTracking()
            .Where(requirement => roleIds.Contains(requirement.StaffRoleId)).ToListAsync(cancellationToken);
        var roleTermsIds = roleTerms.Select(requirement => requirement.TermsDocumentId).Distinct().ToList();
        var terms = await database.TermsDocuments.AsNoTracking()
            .Where(document => roleTermsIds.Contains(document.Id) || assignedTermsIds.Contains(document.Id))
            .Select(document => new { document.Id, document.Title,
                LatestVersion = document.Versions.Max(version => (int?)version.Version) })
            .ToListAsync(cancellationToken);
        var acceptances = await database.StaffTermsAcceptances.AsNoTracking().Include(acceptance => acceptance.TermsDocumentVersion)
            .Where(acceptance => ids.Contains(acceptance.StaffId)).ToListAsync(cancellationToken);
        return staff.ToDictionary(worker => worker.Id, worker =>
        {
            var reasons = new List<string>();
            var missingTerms = new List<MissingTermsResponse>();
            if (worker.StaffRoleId is null) reasons.Add("No job role assigned.");
            foreach (var requirement in requirements.Where(requirement => requirement.StaffRoleId == worker.StaffRoleId))
                if (!documents.Any(document => document.StaffId == worker.Id && document.DocumentTypeId == requirement.DocumentTypeId && IsCurrent(document, clock.GetUtcNow())))
                    reasons.Add($"Current validated document required: {requirement.DocumentType.Name}.");
            var workerAssignments = assignments.Where(assignment => assignment.StaffId == worker.Id).ToDictionary(assignment => assignment.TermsDocumentId);
            foreach (var document in terms.Where(document => roleTerms.Any(requirement => requirement.StaffRoleId == worker.StaffRoleId && requirement.TermsDocumentId == document.Id)
                || workerAssignments.ContainsKey(document.Id)))
            {
                if (document.LatestVersion is null)
                {
                    reasons.Add($"Required terms have no published version: {document.Title}.");
                    missingTerms.Add(new(document.Id, document.Title, null));
                    continue;
                }
                var requiredVersion = Math.Max(document.LatestVersion.Value, workerAssignments.GetValueOrDefault(document.Id)?.Version ?? 0);
                if (!acceptances.Any(acceptance => acceptance.StaffId == worker.Id
                    && acceptance.TermsDocumentVersion.TermsDocumentId == document.Id && acceptance.TermsDocumentVersion.Version == requiredVersion))
                {
                    reasons.Add($"Terms outstanding: {document.Title}, version {requiredVersion}.");
                    missingTerms.Add(new(document.Id, document.Title, requiredVersion));
                }
            }
            return new ReadinessResponse(reasons.Count == 0, reasons) { MissingTerms = missingTerms };
        });
    }
}