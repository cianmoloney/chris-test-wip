using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public interface IStaffDataService
{
    Task<StaffListResponse> GetStaffAsync(string? filter, string? sortBy, CancellationToken cancellationToken);
    Task<LookupsResponse> GetLookupsAsync(CancellationToken cancellationToken);
    Task<StaffResponse> CreateStaffAsync(CreateStaffRequest request, CancellationToken cancellationToken);
    Task<StaffMemberResponse> GetStaffMemberAsync(int id, CancellationToken cancellationToken);
    Task UpdateStaffAsync(int id, UpdateStaffRequest request, CancellationToken cancellationToken);
    Task<DocumentListResponse> GetDocumentsAsync(string? status, CancellationToken cancellationToken, string? filter = null, string? sortBy = null);
    Task SetDocumentStatusAsync(int id, SetDocumentStatusRequest request, CancellationToken cancellationToken);
    Task UpdateDocumentAsync(int id, UpdateDocumentRequest request, CancellationToken cancellationToken);
    Task ReassignDocumentAsync(int id, ReassignDocumentRequest request, CancellationToken cancellationToken);
    Task UpdateStaffDocumentAsync(int staffId, int documentId, UpdateStaffDocumentRequest request, CancellationToken cancellationToken);
    Task<DocumentResponse> GetStaffDocumentAsync(int staffId, int documentId, CancellationToken cancellationToken);
    Task<StaffTermsResponse> GetTermsAsync(int staffId, string? language, CancellationToken cancellationToken);
    Task<TermsAcceptanceResponse> AcceptTermsAsync(int staffId, AcceptTermsRequest request, CancellationToken cancellationToken);
}

public sealed class ApiException(int statusCode, string message, string? field = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Field { get; } = field;
}

public sealed class StaffDataService(AppDbContext database) : IStaffDataService
{
    public async Task<StaffListResponse> GetStaffAsync(string? filter, string? sortBy, CancellationToken cancellationToken)
    {
        var query = database.Staff.AsNoTracking().Include(staff => staff.StaffType)
            .Include(staff => staff.StaffRole).AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filter = filter.Trim();
            query = query.Where(staff => staff.FirstName.Contains(filter) || staff.LastName.Contains(filter)
                || staff.Email.Contains(filter) || (staff.StaffRole != null && staff.StaffRole.Name.Contains(filter)));
        }
        query = sortBy switch
        {
            "email" => query.OrderBy(staff => staff.Email),
            "role" => query.OrderBy(staff => staff.StaffRole!.Name),
            "registered" => query.OrderByDescending(staff => staff.RegisteredAt),
            _ => query.OrderBy(staff => staff.LastName).ThenBy(staff => staff.FirstName)
        };
        var staffList = await query.ToListAsync(cancellationToken);
        var staffIds = staffList.Select(staff => staff.Id).ToList();
        var acceptances = await database.StaffTermsAcceptances.AsNoTracking()
            .Where(acceptance => staffIds.Contains(acceptance.StaffId))
            .GroupBy(acceptance => acceptance.StaffId)
            .Select(group => new { StaffId = group.Key, AcceptedAt = group.Max(acceptance => acceptance.AcceptedAt) })
            .ToDictionaryAsync(acceptance => acceptance.StaffId, acceptance => acceptance.AcceptedAt, cancellationToken);
        return new(staffList.Select(MapStaff).ToList(), acceptances,
            await new EligibilityService(database, TimeProvider.System).GetAsync(staffList, cancellationToken));
    }

    public async Task<LookupsResponse> GetLookupsAsync(CancellationToken cancellationToken)
    {
        var types = await database.StaffTypes.AsNoTracking()
            .Select(type => new LookupResponse(type.Id, type.Name, type.Prefix)).ToListAsync(cancellationToken);
        var roles = await GetRolesAsync(cancellationToken);
        var documentTypes = await GetDocumentTypesAsync(cancellationToken);
        var requirements = await database.StaffRoleDocumentTypes.AsNoTracking()
            .Select(requirement => new { requirement.StaffRoleId, requirement.DocumentType.Name }).ToListAsync(cancellationToken);
        return new(types, roles, documentTypes, requirements.GroupBy(requirement => requirement.StaffRoleId)
            .ToDictionary(group => group.Key, group => group.Select(requirement => requirement.Name).ToList()));
    }

    public async Task<StaffResponse> CreateStaffAsync(CreateStaffRequest request, CancellationToken cancellationToken)
    {
        if (await database.Staff.AnyAsync(staff => staff.Email == request.Email, cancellationToken))
            throw new ApiException(409, "A staff member with this email address is already registered.", "Email");
        var staffType = await database.StaffTypes.FindAsync([request.StaffTypeId], cancellationToken)
            ?? throw new ApiException(400, "Please select a staff type.", "StaffTypeId");
        if (request.StaffRoleId is null)
            throw new ApiException(400, "Please select a role.", "StaffRoleId");
        await ValidateRoleAsync(request.StaffRoleId, cancellationToken);

        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = database.Database.CurrentTransaction is null
                ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
            var staff = new Staff
            {
                FirstName = request.FirstName, LastName = request.LastName, Email = request.Email,
                PhoneNumber = request.PhoneNumber, StaffTypeId = request.StaffTypeId,
                StaffRoleId = request.StaffRoleId, RegisteredAt = DateTimeOffset.UtcNow
            };
            database.Staff.Add(staff);
            await database.SaveChangesAsync(cancellationToken);
            staff.StaffId = $"{staffType.Prefix}{staff.StaffNumber}";
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return MapStaff(staff);
        });
    }

    public async Task<StaffMemberResponse> GetStaffMemberAsync(int id, CancellationToken cancellationToken)
    {
        var staff = await database.Staff.AsNoTracking().Include(staff => staff.StaffType).Include(staff => staff.StaffRole)
            .FirstOrDefaultAsync(staff => staff.Id == id, cancellationToken) ?? throw NotFound();
        var documents = await database.Documents.AsNoTracking().Include(document => document.Type)
            .Where(document => document.StaffId == id).OrderByDescending(document => document.Timestamp).ToListAsync(cancellationToken);
        var requirements = await database.StaffRoleDocumentTypes.AsNoTracking()
            .Where(requirement => requirement.StaffRoleId == staff.StaffRoleId)
            .Select(requirement => new LookupResponse(requirement.DocumentType.Id, requirement.DocumentType.Name, null))
            .ToListAsync(cancellationToken);
        var uploadedTypes = documents.Where(document => EligibilityService.IsCurrent(document, DateTimeOffset.UtcNow))
            .Select(document => document.DocumentTypeId).ToHashSet();
        var agreements = await database.StaffTermsAcceptances.AsNoTracking().Where(acceptance => acceptance.StaffId == id)
            .Include(acceptance => acceptance.TermsDocumentVersion).ThenInclude(version => version.TermsDocument)
            .OrderByDescending(acceptance => acceptance.AcceptedAt).ToListAsync(cancellationToken);
        return new(MapStaff(staff), documents.Select(MapDocument).ToList(), agreements.Select(MapAcceptance).ToList(),
            await GetRolesAsync(cancellationToken), await GetDocumentTypesAsync(cancellationToken),
            requirements.Where(type => !uploadedTypes.Contains(type.Id)).ToList(),
            (await new EligibilityService(database, TimeProvider.System).GetAsync([staff], cancellationToken))[id],
            await database.StaffTypes.Select(type => new LookupResponse(type.Id, type.Name, type.Prefix)).ToListAsync(cancellationToken));
    }

    public async Task UpdateStaffAsync(int id, UpdateStaffRequest request, CancellationToken cancellationToken)
    {
        var staff = await database.Staff.FindAsync([id], cancellationToken) ?? throw NotFound();
        await ValidateRoleAsync(request.StaffRoleId, cancellationToken);
        StaffType? staffType = null;
        if (request.StaffTypeId is not null)
            staffType = await database.StaffTypes.FindAsync([request.StaffTypeId.Value], cancellationToken)
                ?? throw new ApiException(400, "Select a valid staff type.");
        if (await database.Staff.AnyAsync(other => other.Id != id && other.Email == request.Email, cancellationToken))
            throw new ApiException(409, "A staff member with this email address is already registered.", "Email");
        staff.FirstName = request.FirstName;
        staff.LastName = request.LastName;
        staff.Email = request.Email;
        staff.PhoneNumber = request.PhoneNumber;
        staff.StaffRoleId = request.StaffRoleId;
        if (staffType is not null)
        {
            staff.StaffTypeId = staffType.Id;
            staff.StaffId = $"{staffType.Prefix}{staff.StaffNumber}";
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentListResponse> GetDocumentsAsync(string? status, CancellationToken cancellationToken, string? filter = null, string? sortBy = null)
    {
        var query = database.Documents.AsNoTracking().Include(document => document.Staff).Include(document => document.Type).AsQueryable();
        query = status switch
        {
            "pending" => query.Where(document => document.Status == DocumentStatus.PendingReview),
            "failed" => query.Where(document => document.Status == DocumentStatus.ParseFailed),
            "validated" => query.Where(document => document.Status == DocumentStatus.Validated),
            "rejected" => query.Where(document => document.Status == DocumentStatus.Rejected),
            "scanning" => query.Where(document => document.Status == DocumentStatus.AwaitingScan),
            "unsafe" => query.Where(document => document.Status == DocumentStatus.Unsafe),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(filter))
            query = query.Where(document => document.Name.Contains(filter) || (document.Type != null && document.Type.Name.Contains(filter))
                || (document.Staff != null && (document.Staff.FirstName.Contains(filter) || document.Staff.LastName.Contains(filter)))
                || (document.DocumentNumber != null && document.DocumentNumber.Contains(filter)));
        query = sortBy switch
        {
            "name" => query.OrderBy(document => document.Name),
            "expiry" => query.OrderBy(document => document.ExpiryDate),
            "staff" => query.OrderBy(document => document.Staff!.LastName).ThenBy(document => document.Staff!.FirstName),
            _ => query.OrderByDescending(document => document.Timestamp)
        };
        var documents = await query.ToListAsync(cancellationToken);
        var staff = await database.Staff.AsNoTracking().OrderBy(staff => staff.LastName).ThenBy(staff => staff.FirstName).ToListAsync(cancellationToken);
        return new(documents.Select(MapDocument).ToList(), staff.Select(MapStaff).ToList(), await GetDocumentTypesAsync(cancellationToken));
    }

    public async Task SetDocumentStatusAsync(int id, SetDocumentStatusRequest request, CancellationToken cancellationToken)
    {
        if (request.Status is not (DocumentStatus.Validated or DocumentStatus.Rejected))
            throw new ApiException(400, "Invalid document status.", "Status");
        var document = await database.Documents.FindAsync([id], cancellationToken) ?? throw NotFound();
        if (request.Status == DocumentStatus.Validated && (!document.ScanPassed || document.ProcessingCompletedAt is null))
            throw new ApiException(409, "File safety processing must complete before validation.");
        document.Status = request.Status;
        document.IsValid = request.Status == DocumentStatus.Validated;
        if (request.Status == DocumentStatus.Rejected) document.ProcessingCompletedAt ??= DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDocumentAsync(int id, UpdateDocumentRequest request, CancellationToken cancellationToken)
    {
        ValidateDates(request.StartDate, request.ExpiryDate);
        var document = await database.Documents.FindAsync([id], cancellationToken) ?? throw NotFound();
        await ValidateDocumentTypeAsync(request.DocumentTypeId, cancellationToken);
        if (request.StaffId is not null && !await database.Staff.AnyAsync(staff => staff.Id == request.StaffId, cancellationToken))
            throw new ApiException(400, "Please select a valid staff member.", "StaffId");
        document.Name = request.Name?.Trim() ?? document.Name;
        document.DocumentTypeId = request.DocumentTypeId;
        document.DocumentNumber = request.DocumentNumber?.Trim();
        document.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        document.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        document.StartDate = request.StartDate;
        document.ExpiryDate = request.ExpiryDate;
        document.StaffId = request.StaffId;
        ResetReview(document);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task ReassignDocumentAsync(int id, ReassignDocumentRequest request, CancellationToken cancellationToken)
    {
        var document = await database.Documents.FirstOrDefaultAsync(document => document.Id == id, cancellationToken)
            ?? throw NotFound();
        if (request.StaffId is not null && !await database.Staff.AnyAsync(staff => staff.Id == request.StaffId, cancellationToken))
            throw new ApiException(400, "Please select a valid staff member.", "StaffId");
        if (document.StaffId != request.StaffId)
        {
            document.StaffId = request.StaffId;
            ResetReview(document);
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateStaffDocumentAsync(int staffId, int documentId, UpdateStaffDocumentRequest request, CancellationToken cancellationToken)
    {
        ValidateDates(request.StartDate, request.ExpiryDate);
        var document = await database.Documents.FirstOrDefaultAsync(document => document.Id == documentId && document.StaffId == staffId,
            cancellationToken) ?? throw NotFound();
        await ValidateDocumentTypeAsync(request.DocumentTypeId, cancellationToken);
        document.DocumentTypeId = request.DocumentTypeId;
        document.DocumentNumber = request.DocumentNumber?.Trim();
        document.StartDate = request.StartDate;
        document.ExpiryDate = request.ExpiryDate;
        ResetReview(document);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentResponse> GetStaffDocumentAsync(int staffId, int documentId, CancellationToken cancellationToken)
    {
        var document = await database.Documents.AsNoTracking()
            .FirstOrDefaultAsync(document => document.Id == documentId && document.StaffId == staffId, cancellationToken) ?? throw NotFound();
        if (!document.ScanPassed) throw new ApiException(409, "The file is not available until safety checks pass.");
        return MapDocument(document);
    }

    public async Task<StaffTermsResponse> GetTermsAsync(int staffId, string? language, CancellationToken cancellationToken)
    {
        var staff = await database.Staff.AsNoTracking().FirstOrDefaultAsync(staff => staff.Id == staffId, cancellationToken) ?? throw NotFound();
        language = language is "pl" or "uk" ? language : "en";
        var versions = database.TermsDocumentVersions.AsNoTracking().Include(version => version.TermsDocument)
            .Where(version => database.StaffTermsAssignments.Any(assignment => assignment.StaffId == staffId
                && assignment.TermsDocumentId == version.TermsDocumentId && assignment.Version == version.Version));
        var terms = await versions.Where(version => version.IsActive && version.Language == language)
            .OrderByDescending(version => version.Version).FirstOrDefaultAsync(cancellationToken)
            ?? await versions.Where(version => version.IsActive && version.Language == "en")
                .OrderByDescending(version => version.Version).FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset? acceptedAt = terms is null ? null : await database.StaffTermsAcceptances.AsNoTracking()
            .Where(acceptance => acceptance.StaffId == staffId && acceptance.TermsDocumentVersionId == terms.Id)
            .OrderByDescending(acceptance => acceptance.AcceptedAt).Select(acceptance => (DateTimeOffset?)acceptance.AcceptedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new(MapStaff(staff), terms is null ? null : MapTerms(terms), acceptedAt);
    }

    public async Task<TermsAcceptanceResponse> AcceptTermsAsync(int staffId, AcceptTermsRequest request, CancellationToken cancellationToken)
    {
        if (!request.Agree)
            throw new ApiException(400, "You must agree to the terms to continue.", "Agree");
        if (!await database.Staff.AnyAsync(staff => staff.Id == staffId, cancellationToken))
            throw NotFound();
        var version = await database.TermsDocumentVersions.Include(version => version.TermsDocument)
            .FirstOrDefaultAsync(version => version.Id == request.TermsDocumentVersionId, cancellationToken) ?? throw NotFound();
        if (!version.IsActive)
            throw new ApiException(409, "The terms have changed. Please reload and review the current version.");
        if (!await database.StaffTermsAssignments.AnyAsync(assignment => assignment.StaffId == staffId
            && assignment.TermsDocumentId == version.TermsDocumentId && assignment.Version == version.Version, cancellationToken))
            throw new ApiException(403, "These terms are not currently assigned to this staff member.");
        var existing = await database.StaffTermsAcceptances.Include(acceptance => acceptance.TermsDocumentVersion).ThenInclude(terms => terms.TermsDocument)
            .FirstOrDefaultAsync(acceptance => acceptance.StaffId == staffId && acceptance.TermsDocumentVersion.TermsDocumentId == version.TermsDocumentId
                && acceptance.TermsDocumentVersion.Version == version.Version, cancellationToken);
        if (existing is not null) return MapAcceptance(existing);
        var acceptance = new StaffTermsAcceptance
        {
            StaffId = staffId, TermsDocumentVersionId = version.Id, AcceptedAt = DateTimeOffset.UtcNow
        };
        database.StaffTermsAcceptances.Add(acceptance);
        await database.SaveChangesAsync(cancellationToken);
        return new(acceptance.Id, MapTerms(version), acceptance.AcceptedAt);
    }

    private Task<List<LookupResponse>> GetRolesAsync(CancellationToken cancellationToken) =>
        database.StaffRoles.AsNoTracking().OrderBy(role => role.Name)
            .Select(role => new LookupResponse(role.Id, role.Name, null)).ToListAsync(cancellationToken);

    private Task<List<LookupResponse>> GetDocumentTypesAsync(CancellationToken cancellationToken) =>
        database.DocumentTypes.AsNoTracking().OrderBy(type => type.Name)
            .Select(type => new LookupResponse(type.Id, type.Name, null)).ToListAsync(cancellationToken);

    private async Task ValidateRoleAsync(int? roleId, CancellationToken cancellationToken)
    {
        if (roleId is not null && !await database.StaffRoles.AnyAsync(role => role.Id == roleId, cancellationToken))
            throw new ApiException(400, "Please select a valid role.", "StaffRoleId");
    }

    private async Task ValidateDocumentTypeAsync(int? typeId, CancellationToken cancellationToken)
    {
        if (typeId is not null && !await database.DocumentTypes.AnyAsync(type => type.Id == typeId, cancellationToken))
            throw new ApiException(400, "Please select a valid document type.", "DocumentTypeId");
    }

    private static ApiException NotFound() => new(404, "The requested record was not found.");

    private static void ValidateDates(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start > end) throw new ApiException(400, "End date cannot precede start date.");
    }

    private static void ResetReview(DocumentEntry document)
    {
        document.IsValid = false;
        if (document.ScanPassed) document.Status = DocumentStatus.PendingReview;
    }

    private static StaffResponse MapStaff(Staff staff) => new(staff.Id, staff.StaffId, staff.StaffNumber,
        staff.FirstName, staff.LastName, staff.Email, staff.PhoneNumber, staff.StaffTypeId,
        staff.StaffType is null ? null : new(staff.StaffType.Id, staff.StaffType.Name, staff.StaffType.Prefix),
        staff.StaffRoleId, staff.StaffRole is null ? null : new(staff.StaffRole.Id, staff.StaffRole.Name), staff.RegisteredAt);

    private static DocumentResponse MapDocument(DocumentEntry document) => new(document.Id, document.Name,
        document.BlobName, document.ExtractedName, document.Email, document.Phone, document.DocumentType, document.DocumentTypeId,
        document.Type is null ? null : new(document.Type.Id, document.Type.Name), document.DocumentNumber,
        document.StartDate, document.ExpiryDate, document.IsValid, document.Status, document.Timestamp, document.StaffId,
        document.Staff is null ? null : MapStaff(document.Staff), document.ContainerName, document.ScanPassed, document.Issue);

    private static TermsVersionResponse MapTerms(TermsDocumentVersion version) => new(version.Id,
        new(version.TermsDocument.Id, version.TermsDocument.Title), version.Content, version.Language, version.Version,
        version.IsActive, version.CreatedAt);

    private static TermsAcceptanceResponse MapAcceptance(StaffTermsAcceptance acceptance) =>
        new(acceptance.Id, MapTerms(acceptance.TermsDocumentVersion), acceptance.AcceptedAt);
}