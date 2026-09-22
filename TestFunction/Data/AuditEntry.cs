using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data;

public sealed class AuditEntry
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    [MaxLength(128)] public string Action { get; set; } = "";
    [MaxLength(256)] public string Subject { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}