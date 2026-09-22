using System.ComponentModel.DataAnnotations;

namespace TestShared;

/// <summary>A new immutable edition with approved translations.</summary>
public sealed record PublishTermsRequest
{
    public int? TermsDocumentId { get; init; }
    [Required, MaxLength(256)] public string Title { get; init; } = "";
    [Required, MaxLength(100000)] public string English { get; init; } = "";
    [Required, MaxLength(100000)] public string Polish { get; init; } = "";
    [Required, MaxLength(100000)] public string Ukrainian { get; init; } = "";
}