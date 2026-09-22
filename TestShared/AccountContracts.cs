using System.ComponentModel.DataAnnotations;

namespace TestShared;

public static class Permissions
{
    public const string StaffRead = "Staff.Read";
    public const string StaffWrite = "Staff.Write";
    public const string DocumentsWrite = "Documents.Write";
    public const string DocumentsValidate = "Documents.Validate";
    public const string LinksWrite = "Links.Write";
    public const string UsersWrite = "Users.Write";
    public const string TermsWrite = "Terms.Write";
    public static readonly string[] All = [StaffRead, StaffWrite, DocumentsWrite, DocumentsValidate, LinksWrite, UsersWrite, TermsWrite];
}

/// <summary>Credentials for an office account.</summary>
public sealed record LoginRequest([property: Required, EmailAddress, MaxLength(256)] string Email,
    [property: Required, MaxLength(256)] string Password);
/// <summary>A challenge response; the challenge token is not a signed-in session.</summary>
public sealed record VerifyMfaRequest([property: Required] string Challenge,
    [property: Required, RegularExpression("^[0-9]{6}$")] string Code);
/// <summary>An office account without credentials.</summary>
public sealed record UserResponse(int Id, string Email, int RoleId, string Role, bool IsEnabled, bool MfaEnabled,
    List<string> Permissions);
/// <summary>A short-lived session or MFA challenge.</summary>
public sealed record LoginResponse(string Token, bool RequiresMfa, DateTimeOffset ExpiresAt, UserResponse? User);
/// <summary>Account details editable by HR or Admin.</summary>
public sealed record SaveUserRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = "";
    [MaxLength(256), MinLength(12)] public string? Password { get; init; }
    [Range(1, int.MaxValue)] public int RoleId { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool MfaEnabled { get; init; }
}
/// <summary>Accounts and available application roles.</summary>
public sealed record UsersResponse(List<UserResponse> Users, List<LookupResponse> Roles);
/// <summary>The authenticated user's optional email MFA preference.</summary>
public sealed record MfaPreferenceRequest(bool Enabled);