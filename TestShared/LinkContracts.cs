using System.ComponentModel.DataAnnotations;

namespace TestShared;

public static class ShareLinkPurposes
{
    public const string Register = "register";
    public const string RegisterMultiple = "register-many";
    public const string Upload = "upload";
    public const string Terms = "terms";
}

/// <summary>A new staff capability issued by an authorized office user.</summary>
public sealed record CreateLinkRequest([property: Required, RegularExpression("^(register|register-many|upload|terms)$")] string Purpose,
    int? StaffId, int? TermsDocumentId, [property: Range(1, 336)] int ValidHours = 24);
/// <summary>A bearer capability returned only at issuance.</summary>
public sealed record LinkResponse(string Token, string Purpose, DateTimeOffset ExpiresAt, Guid Id = default);
/// <summary>A public capability lookup.</summary>
public sealed record ResolveLinkRequest([property: Required, MaxLength(128)] string Token,
    [property: Required] string Purpose, string Language = "en");
/// <summary>Only the data needed by the named worker workflow.</summary>
public sealed record PublicLinkResponse(string Purpose, StaffResponse? Staff, LookupsResponse? Lookups,
    TermsVersionResponse? Terms, DateTimeOffset? AcceptedAt);
/// <summary>A registration bound to a one-use link.</summary>
public sealed record LinkRegistrationRequest([property: Required] string Token, CreateStaffRequest Staff);
/// <summary>A batch of staff registrations bound to one single-use link.</summary>
public sealed record LinkMultipleRegistrationRequest([property: Required, MaxLength(128)] string Token,
    [property: Required, MinLength(1), MaxLength(LinkMultipleRegistrationRequest.MaximumStaff)] List<CreateStaffRequest> Staff)
{
    public const int MaximumStaff = 25;
}
/// <summary>The staff created by a successful atomic batch registration.</summary>
public sealed record MultipleRegistrationResponse(List<StaffResponse> Staff);
/// <summary>Agreement to the edition actually displayed.</summary>
public sealed record LinkAcceptanceRequest([property: Required] string Token, int TermsDocumentVersionId, bool Agree);
/// <summary>A completed immutable blob upload.</summary>
public sealed record UploadResponse(string ContainerName, string BlobName);