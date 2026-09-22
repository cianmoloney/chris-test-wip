using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class LinkService(AppDbContext database, IStaffDataService staffData, TimeProvider clock)
{
    public Task<LinkResponse> CreateAsync(int actorId, CreateLinkRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        database.ChangeTracker.Clear();
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        if (request.Purpose is not (ShareLinkPurposes.Register or ShareLinkPurposes.RegisterMultiple or ShareLinkPurposes.Upload or ShareLinkPurposes.Terms))
            throw new ApiException(400, "Invalid link purpose.");
        if (request.ValidHours is < 1 or > 336) throw new ApiException(400, "Link duration must be 1 to 336 hours.");
        if (request.Purpose is ShareLinkPurposes.Register or ShareLinkPurposes.RegisterMultiple && request.StaffId is not null)
            throw new ApiException(400, "Registration links cannot identify existing staff.");
        if (request.StaffId is not null && !await database.Staff.AnyAsync(staff => staff.Id == request.StaffId, cancellationToken))
            throw new ApiException(400, "Staff record not found.");
        var token = AccountService.NewToken();
        var link = new ShareLink
        {
            TokenHash = AccountService.HashToken(token), Purpose = request.Purpose,
            StaffId = request.StaffId, CreatedBy = actorId, ExpiresAt = clock.GetUtcNow().AddHours(request.ValidHours)
        };
        if (request.Purpose == "terms")
        {
            if (request.StaffId is null || request.TermsDocumentId is null) throw new ApiException(400, "Select both staff and terms.");
            var version = await database.TermsDocumentVersions.Where(version => version.TermsDocumentId == request.TermsDocumentId && version.IsActive)
                .OrderByDescending(version => version.Version).FirstOrDefaultAsync(cancellationToken) ?? throw new ApiException(400, "No published terms available.");
            link.TermsDocumentId = version.TermsDocumentId;
            link.TermsVersion = version.Version;
            var assignment = await database.StaffTermsAssignments.FindAsync([request.StaffId.Value, version.TermsDocumentId], cancellationToken);
            if (assignment is null) database.StaffTermsAssignments.Add(new() { StaffId = request.StaffId.Value, TermsDocumentId = version.TermsDocumentId, Version = version.Version });
            else { assignment.Version = version.Version; assignment.AssignedAt = clock.GetUtcNow(); }
        }
        database.ShareLinks.Add(link);
        database.AuditEntries.Add(new() { UserId = actorId, Action = "Link.Issue", Subject = request.Purpose });
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new LinkResponse(token, link.Purpose, link.ExpiresAt, link.UploadId);
    });

    public async Task<ShareLink> RequireAsync(string token, string purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) throw new ApiException(403, "Invalid link.");
        var hash = AccountService.HashToken(token);
        var link = await database.ShareLinks.SingleOrDefaultAsync(link => link.TokenHash == hash, cancellationToken);
        if (link is null || link.Purpose != purpose || link.ExpiresAt <= clock.GetUtcNow() || link.RevokedAt is not null
            || (link.UsedAt is not null && purpose != "terms")) throw new ApiException(403, "This link is invalid, expired or already used.");
        if (link.StaffId is not null && !await database.Staff.AnyAsync(staff => staff.Id == link.StaffId, cancellationToken))
            throw new ApiException(403, "This staff link is no longer available.");
        return link;
    }

    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var link = await database.ShareLinks.SingleOrDefaultAsync(link => link.UploadId == id, cancellationToken)
            ?? throw new ApiException(404, "Link not found.");
        link.RevokedAt ??= clock.GetUtcNow();
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicLinkResponse> ResolveAsync(ResolveLinkRequest request, CancellationToken cancellationToken)
    {
        var link = await RequireAsync(request.Token, request.Purpose, cancellationToken);
        if (link.Purpose != "terms") return new(link.Purpose, null, await staffData.GetLookupsAsync(cancellationToken), null, null);
        var language = request.Language is "pl" or "uk" ? request.Language : "en";
        var versions = database.TermsDocumentVersions.Include(version => version.TermsDocument)
            .Where(version => version.TermsDocumentId == link.TermsDocumentId && version.Version == link.TermsVersion);
        var terms = await versions.FirstOrDefaultAsync(version => version.Language == language, cancellationToken)
            ?? await versions.FirstOrDefaultAsync(version => version.Language == "en", cancellationToken)
            ?? throw new ApiException(404, "Terms translation unavailable.");
        var staff = (await staffData.GetStaffMemberAsync(link.StaffId!.Value, cancellationToken)).Staff;
        var accepted = await database.StaffTermsAcceptances.Where(acceptance => acceptance.StaffId == link.StaffId
            && acceptance.TermsDocumentVersion.TermsDocumentId == link.TermsDocumentId && acceptance.TermsDocumentVersion.Version == link.TermsVersion)
            .Select(acceptance => (DateTimeOffset?)acceptance.AcceptedAt).FirstOrDefaultAsync(cancellationToken);
        return new(link.Purpose, staff, null, new(terms.Id, new(terms.TermsDocumentId, terms.TermsDocument.Title), terms.Content,
            terms.Language, terms.Version, terms.IsActive, terms.CreatedAt), accepted);
    }

    public Task<StaffResponse> RegisterAsync(LinkRegistrationRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            if (request.Staff is null) throw new ApiException(400, "Staff details are required.");
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(request.Staff, new ValidationContext(request.Staff), errors, true))
                throw new ApiException(400, errors[0].ErrorMessage ?? "Invalid staff details.");
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await RequireAsync(request.Token, "register", cancellationToken);
            var staff = await staffData.CreateStaffAsync(request.Staff, cancellationToken);
            var link = await RequireAsync(request.Token, "register", cancellationToken);
            link.UsedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return staff;
        });

    public Task<MultipleRegistrationResponse> RegisterMultipleAsync(LinkMultipleRegistrationRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            if (request.Staff is null || request.Staff.Count is < 1 or > LinkMultipleRegistrationRequest.MaximumStaff)
                throw new ApiException(400, $"Register between 1 and {LinkMultipleRegistrationRequest.MaximumStaff} staff members.", "Staff");
            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < request.Staff.Count; index++)
            {
                var staff = request.Staff[index];
                if (staff is null) throw new ApiException(400, "Staff details are required.", $"Staff[{index}]");
                var errors = new List<ValidationResult>();
                if (!Validator.TryValidateObject(staff, new ValidationContext(staff), errors, true))
                {
                    var field = errors[0].MemberNames.FirstOrDefault();
                    throw new ApiException(400, errors[0].ErrorMessage ?? "Invalid staff details.",
                        field is null ? $"Staff[{index}]" : $"Staff[{index}].{field}");
                }
                if (!emails.Add(staff.Email.Trim()))
                    throw new ApiException(400, "Each staff member must have a different email address.", $"Staff[{index}].Email");
            }

            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await RequireAsync(request.Token, ShareLinkPurposes.RegisterMultiple, cancellationToken);
            var registered = new List<StaffResponse>();
            for (var index = 0; index < request.Staff.Count; index++)
            {
                try
                {
                    var staff = request.Staff[index];
                    registered.Add(await staffData.CreateStaffAsync(staff with { Email = staff.Email.Trim() }, cancellationToken));
                }
                catch (ApiException exception)
                {
                    throw new ApiException(exception.StatusCode, exception.Message,
                        exception.Field is null ? $"Staff[{index}]" : $"Staff[{index}].{exception.Field}");
                }
            }
            var link = await RequireAsync(request.Token, ShareLinkPurposes.RegisterMultiple, cancellationToken);
            link.UsedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MultipleRegistrationResponse(registered);
        });

    public Task<TermsAcceptanceResponse> AcceptAsync(LinkAcceptanceRequest request, CancellationToken cancellationToken) =>
        database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var link = await RequireAsync(request.Token, "terms", cancellationToken);
            var version = await database.TermsDocumentVersions.FindAsync([request.TermsDocumentVersionId], cancellationToken);
            if (version is null || version.TermsDocumentId != link.TermsDocumentId || version.Version != link.TermsVersion)
                throw new ApiException(403, "This version does not belong to the issued terms link.");
            var result = await staffData.AcceptTermsAsync(link.StaffId!.Value, new(version.Id, request.Agree), cancellationToken);
            link.UsedAt = clock.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
}