using System.ComponentModel.DataAnnotations;

namespace TestFunction.Data;

public sealed class UserSession
{
    [Key, MaxLength(64)] public string TokenHash { get; set; } = "";
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool MfaPending { get; set; }
    [MaxLength(64)] public string? CodeHash { get; set; }
    public int Attempts { get; set; }
    [Timestamp] public byte[] RowVersion { get; set; } = [];
}