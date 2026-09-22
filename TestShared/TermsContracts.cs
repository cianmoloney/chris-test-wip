using System.ComponentModel.DataAnnotations;

namespace TestShared;

/// <summary>The complete set of staff roles required to accept this shared terms document.</summary>
public sealed record SaveTermsRoleRequest
{
    [System.Text.Json.Serialization.JsonRequired, Required] public List<int> StaffRoleIds { get; init; } = [];
    [Required, RegularExpression("^[A-F0-9]{64}$")] public string ExpectedRevision { get; init; } = "";
}

/// <summary>A new immutable edition with approved translations.</summary>
public sealed record PublishTermsRequest
{
    public int? TermsDocumentId { get; init; }
    [Required, MaxLength(256)] public string Title { get; init; } = "";
    [Required, MaxLength(100000)] public string English { get; init; } = "";
    [Required, MaxLength(100000)] public string Polish { get; init; } = "";
    [Required, MaxLength(100000)] public string Ukrainian { get; init; } = "";
}