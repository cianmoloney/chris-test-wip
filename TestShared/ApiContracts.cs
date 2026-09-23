using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestShared;

/// <summary>A named staff type, role, or document type.</summary>
public sealed record LookupResponse(int Id, string Name, string? Prefix = null);

/// <summary>A document type and the staff roles that require it.</summary>
public sealed record DocumentTypeResponse(int Id, string Name, string? TextIdentifier, List<int> StaffRoleIds)
{
    public string? StartDateLabel { get; init; }
    public string? ExpiryDateLabel { get; init; }
    public string? DocumentNumberLabel { get; init; }
    public string? ExtractedNameLabel { get; init; }
    public string? EmailLabel { get; init; }
    public string? PhoneLabel { get; init; }
}

/// <summary>Document types and available staff roles for HR management.</summary>
public sealed record DocumentTypeManagementResponse(List<DocumentTypeResponse> DocumentTypes, List<LookupResponse> StaffRoles)
{
    public Dictionary<int, string> RoleRevisions => StaffRoles.ToDictionary(role => role.Id,
        role => AssignmentRevision.Documents(DocumentTypes.Where(type => type.StaffRoleIds.Contains(role.Id)).Select(type => type.Id)));
}

/// <summary>A new staff job role, separate from office-user access roles.</summary>
public sealed record CreateStaffRoleRequest
{
    [Required, MaxLength(128)] public string Name { get; init; } = "";
}

/// <summary>The complete document-type requirements for a single staff role.</summary>
public sealed record SaveStaffRoleDocumentsRequest
{
    [JsonRequired, Required, MaxLength(256)] public List<int> DocumentTypeIds { get; init; } = [];
    [Required, RegularExpression("^[A-F0-9]{64}$")] public string ExpectedRevision { get; init; } = "";
}

/// <summary>Editable document identification text and required-role associations.</summary>
public sealed record SaveDocumentTypeRequest
{
    [Required, MaxLength(128)] public string Name { get; init; } = "";
    [Required, MaxLength(256)] public string TextIdentifier { get; init; } = "";
    [MaxLength(128)] public string? StartDateLabel { get; init; }
    [MaxLength(128)] public string? ExpiryDateLabel { get; init; }
    [MaxLength(128)] public string? DocumentNumberLabel { get; init; }
    [MaxLength(128)] public string? ExtractedNameLabel { get; init; }
    [MaxLength(128)] public string? EmailLabel { get; init; }
    [MaxLength(128)] public string? PhoneLabel { get; init; }
    [Required, MaxLength(256)] public List<int> StaffRoleIds { get; init; } = [];
}

/// <summary>Staff details exposed to the website without persistence navigation properties.</summary>
public sealed record StaffResponse(
    int Id, string? StaffId, int StaffNumber, string FirstName, string LastName,
    string Email, string? PhoneNumber, int? StaffTypeId, LookupResponse? StaffType,
    int? StaffRoleId, LookupResponse? StaffRole, DateTimeOffset RegisteredAt);

/// <summary>Filtered staff and their latest terms acceptance timestamps.</summary>
public sealed record StaffListResponse(
    List<StaffResponse> Staff, Dictionary<int, DateTimeOffset> LastTermsAcceptedAt,
    Dictionary<int, ReadinessResponse>? Readiness = null);

/// <summary>Current work eligibility and the requirements preventing it.</summary>
public sealed record ReadinessResponse(bool IsReady, List<string> Reasons)
{
    public List<MissingTermsResponse> MissingTerms { get; init; } = [];
}

public sealed record MissingTermsResponse(int TermsDocumentId, string Title, int? RequiredVersion);

public enum DocumentStatus
{
    PendingReview = 0,
    Validated = 1,
    Rejected = 2,
    ParseFailed = 3,
    AwaitingScan = 4,
    Unsafe = 5,
    AwaitingProcessing = 6,
}

public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>The lookup values needed for registration and staff management.</summary>
public sealed record LookupsResponse(List<LookupResponse> StaffTypes, List<LookupResponse> StaffRoles,
    List<LookupResponse> DocumentTypes, Dictionary<int, List<string>> RoleRequiredDocuments);

/// <summary>Information supplied when registering a staff member.</summary>
public sealed record CreateStaffRequest
{
    [Required, MaxLength(128)]
    public string FirstName { get; init; } = string.Empty;
    [Required, MaxLength(128)]
    public string LastName { get; init; } = string.Empty;
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;
    [Phone, MaxLength(32)]
    public string? PhoneNumber { get; init; }
    [Required, Range(1, int.MaxValue)]
    public int? StaffTypeId { get; init; }
    [Required, Range(1, int.MaxValue)]
    public int? StaffRoleId { get; init; }
}

/// <summary>Editable staff details; identifiers and registration dates are server-owned.</summary>
public sealed record UpdateStaffRequest
{
    [Range(1, int.MaxValue)]
    public int? StaffTypeId { get; init; }
    [Required, MaxLength(128)]
    public string FirstName { get; init; } = string.Empty;
    [Required, MaxLength(128)]
    public string LastName { get; init; } = string.Empty;
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;
    [Phone, MaxLength(32)]
    public string? PhoneNumber { get; init; }
    [Range(1, int.MaxValue)]
    public int? StaffRoleId { get; init; }
}

/// <summary>An uploaded document and its optional staff association.</summary>
public sealed record DocumentResponse(int Id, string Name, string? BlobName, string? ExtractedName,
    string? Email, string? Phone, string? DocumentType, int? DocumentTypeId, LookupResponse? Type,
    string? DocumentNumber, DateTimeOffset? StartDate, DateTimeOffset? ExpiryDate, bool IsValid,
    DocumentStatus Status, DateTimeOffset Timestamp, int? StaffId, StaffResponse? Staff,
    string? ContainerName = null, bool ScanPassed = false, string? Issue = null,
    DateTimeOffset? ProcessingCompletedAt = null)
{
    public string StatusDisplay => Status is DocumentStatus.AwaitingScan or DocumentStatus.AwaitingProcessing
        ? "Awaiting processing" : Status.ToString();
    public bool CanValidate => Status != DocumentStatus.Unsafe && ProcessingCompletedAt is not null;
}

/// <summary>Filtered documents with the choices used by the editing form.</summary>
public sealed record DocumentListResponse(List<DocumentResponse> Documents, List<StaffResponse> Staff,
    List<LookupResponse> DocumentTypes);

/// <summary>A document review decision.</summary>
public sealed record SetDocumentStatusRequest(DocumentStatus Status);

/// <summary>A replacement staff association; null explicitly unlinks the document.</summary>
public sealed record ReassignDocumentRequest([property: JsonRequired, Range(1, int.MaxValue)] int? StaffId);

/// <summary>Editable metadata and staff association of a document.</summary>
public sealed record UpdateDocumentRequest
{
    [MaxLength(512)]
    public string? Name { get; init; }
    [Range(1, int.MaxValue)]
    public int? DocumentTypeId { get; init; }
    [MaxLength(128)]
    public string? DocumentNumber { get; init; }
    [MaxLength(256)]
    public string? Email { get; init; }
    [MaxLength(64)]
    public string? Phone { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? ExpiryDate { get; init; }
    [Range(1, int.MaxValue)]
    public int? StaffId { get; init; }
}

/// <summary>Metadata editable within a staff member's document list.</summary>
public sealed record UpdateStaffDocumentRequest
{
    [Range(1, int.MaxValue)]
    public int? DocumentTypeId { get; init; }
    [MaxLength(128)]
    public string? DocumentNumber { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? ExpiryDate { get; init; }
}

/// <summary>The title of a terms document.</summary>
public sealed record TermsDocumentResponse(int Id, string Title)
{
    public List<int> StaffRoleIds { get; init; } = [];
    public string RoleRevision => AssignmentRevision.TermsRole(StaffRoleIds);
}

/// <summary>A specific language and version of the terms text.</summary>
public sealed record TermsVersionResponse(int Id, TermsDocumentResponse TermsDocument,
    string Content, string Language, int Version, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>A timestamped acceptance of a particular terms version.</summary>
public sealed record TermsAcceptanceResponse(int Id, TermsVersionResponse TermsDocumentVersion,
    DateTimeOffset AcceptedAt);

/// <summary>Staff details, related documents, and terms history.</summary>
public sealed record StaffMemberResponse(StaffResponse Staff, List<DocumentResponse> Documents,
    List<TermsAcceptanceResponse> Agreements, List<LookupResponse> StaffRoles,
    List<LookupResponse> DocumentTypes, List<LookupResponse> MissingDocumentTypes, ReadinessResponse? Readiness = null,
    List<LookupResponse>? StaffTypes = null);

/// <summary>The terms offered to a staff member and their latest acceptance.</summary>
public sealed record StaffTermsResponse(StaffResponse Staff, TermsVersionResponse? Terms,
    DateTimeOffset? AcceptedAt);

/// <summary>Explicit agreement to a specific active terms version.</summary>
public sealed record AcceptTermsRequest([property: Range(1, int.MaxValue)] int TermsDocumentVersionId, bool Agree);