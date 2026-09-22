using System.Data;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class TermsService(AppDbContext database)
{
    public Task SaveRoleAsync(int actorId, int termsDocumentId, SaveTermsRoleRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = database.Database.IsRelational()
                ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            var terms = await database.TermsDocuments.SingleOrDefaultAsync(terms => terms.Id == termsDocumentId, cancellationToken)
                ?? throw new ApiException(404, "Terms not found.");
            var existing = await database.StaffRoleTermsDocuments.Where(requirement => requirement.TermsDocumentId == termsDocumentId)
                .ToListAsync(cancellationToken);
            var revision = AssignmentRevision.TermsRole(existing.Select(requirement => requirement.StaffRoleId));
            if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
                throw new ApiException(400, "Reload the terms document before saving.", nameof(request.ExpectedRevision));
            if (revision != request.ExpectedRevision)
                throw new ApiException(409, "The required staff roles changed. Reload before saving.");
            if (request.StaffRoleIds is null)
                throw new ApiException(400, "Select valid staff roles.", nameof(request.StaffRoleIds));
            var roleIds = request.StaffRoleIds.Distinct().Order().ToList();
            if (await database.StaffRoles.CountAsync(role => roleIds.Contains(role.Id), cancellationToken) != roleIds.Count)
                throw new ApiException(400, "Select valid staff roles.", nameof(request.StaffRoleIds));
            database.StaffRoleTermsDocuments.RemoveRange(existing.Where(requirement => !roleIds.Contains(requirement.StaffRoleId)));
            foreach (var roleId in roleIds.Except(existing.Select(requirement => requirement.StaffRoleId)))
                database.StaffRoleTermsDocuments.Add(new() { StaffRoleId = roleId, TermsDocumentId = termsDocumentId });
            var subject = $"Terms {terms.Id}: roles [{string.Join(",", existing.Select(requirement => requirement.StaffRoleId).Order())}] -> [{string.Join(",", roleIds)}]";
            database.AuditEntries.Add(new() { UserId = actorId, Action = "Terms.Role", Subject = subject.Length <= 256 ? subject : subject[..256] });
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        });

    public async Task<List<TermsVersionResponse>> GetVersionsAsync(int termsDocumentId, CancellationToken cancellationToken)
    {
        var terms = await database.TermsDocuments.AsNoTracking().Include(terms => terms.RequiredByRoles)
            .SingleOrDefaultAsync(terms => terms.Id == termsDocumentId, cancellationToken)
            ?? throw new ApiException(404, "Terms not found.");
        var versions = await database.TermsDocumentVersions.AsNoTracking()
            .Where(version => version.TermsDocumentId == termsDocumentId)
            .OrderByDescending(version => version.Version).ThenBy(version => version.Language)
            .ToListAsync(cancellationToken);
        return versions.Select(version => new TermsVersionResponse(version.Id, new(terms.Id, terms.Title)
            { StaffRoleIds = terms.RequiredByRoles.Select(requirement => requirement.StaffRoleId).Order().ToList() },
            version.Content, version.Language, version.Version, version.IsActive, version.CreatedAt)).ToList();
    }

    public Task<TermsDocumentResponse> PublishAsync(int actorId, PublishTermsRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var terms = request.TermsDocumentId is null ? new TermsDocument { Title = request.Title.Trim() }
                : await database.TermsDocuments.FindAsync([request.TermsDocumentId.Value], cancellationToken) ?? throw new ApiException(404, "Terms not found.");
            if (request.TermsDocumentId is null) database.TermsDocuments.Add(terms);
            var previous = await database.TermsDocumentVersions.Where(version => version.TermsDocumentId == terms.Id).ToListAsync(cancellationToken);
            var edition = previous.Select(version => version.Version).DefaultIfEmpty(0).Max() + 1;
            foreach (var version in previous) version.IsActive = false;
            foreach (var translation in new[] { ("en", request.English), ("pl", request.Polish), ("uk", request.Ukrainian) })
                database.TermsDocumentVersions.Add(new() { TermsDocument = terms, Language = translation.Item1, Content = translation.Item2,
                    Version = edition, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            foreach (var assignment in await database.StaffTermsAssignments.Where(assignment => assignment.TermsDocumentId == terms.Id).ToListAsync(cancellationToken))
                assignment.Version = edition;
            database.AuditEntries.Add(new() { UserId = actorId, Action = "Terms.Publish", Subject = terms.Title });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TermsDocumentResponse(terms.Id, terms.Title)
            {
                StaffRoleIds = await database.StaffRoleTermsDocuments.Where(requirement => requirement.TermsDocumentId == terms.Id)
                    .OrderBy(requirement => requirement.StaffRoleId).Select(requirement => requirement.StaffRoleId).ToListAsync(cancellationToken)
            };
        });
}