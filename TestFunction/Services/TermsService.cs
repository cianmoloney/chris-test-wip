using System.Data;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class TermsService(AppDbContext database)
{
    public async Task<List<TermsVersionResponse>> GetVersionsAsync(int termsDocumentId, CancellationToken cancellationToken)
    {
        var terms = await database.TermsDocuments.AsNoTracking().SingleOrDefaultAsync(terms => terms.Id == termsDocumentId, cancellationToken)
            ?? throw new ApiException(404, "Terms not found.");
        var versions = await database.TermsDocumentVersions.AsNoTracking()
            .Where(version => version.TermsDocumentId == termsDocumentId)
            .OrderByDescending(version => version.Version).ThenBy(version => version.Language)
            .ToListAsync(cancellationToken);
        return versions.Select(version => new TermsVersionResponse(version.Id, new(terms.Id, terms.Title),
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
            return new TermsDocumentResponse(terms.Id, terms.Title);
        });
}