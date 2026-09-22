using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data;

public sealed class ShareLink
{
    [Key, MaxLength(64)] public string TokenHash { get; set; } = "";
    public Guid UploadId { get; set; } = Guid.NewGuid();
    [MaxLength(16)] public string Purpose { get; set; } = "";
    public int? StaffId { get; set; }
    public int? TermsDocumentId { get; set; }
    public int? TermsVersion { get; set; }
    public int CreatedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUploadCheck { get; set; }
    [MaxLength(63)] public string? ContainerName { get; set; }
    [MaxLength(512)] public string? BlobName { get; set; }
    [MaxLength(64)] public string? ContentHash { get; set; }
    public int? DocumentTypeId { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}

public sealed class StaffTermsAssignment
{
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public int TermsDocumentId { get; set; }
    public TermsDocument TermsDocument { get; set; } = null!;
    public int Version { get; set; }
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}