using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestShared;

namespace TestFunction.Services;

public sealed class AccountService(AppDbContext database, IEmailService email, TimeProvider clock)
{
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword(new User(), Guid.NewGuid().ToString());
    public static string HashToken(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var normalized = request.Email.Trim().ToLowerInvariant();
        var user = await database.Users.Include(account => account.Role).ThenInclude(role => role!.Responsibilities)
            .SingleOrDefaultAsync(account => account.Email == normalized, cancellationToken);
        var now = clock.GetUtcNow();
        PasswordVerificationResult result;
        try { result = Hasher.VerifyHashedPassword(user ?? new User(), user?.PasswordHash ?? DummyHash, request.Password); }
        catch (FormatException) { result = PasswordVerificationResult.Failed; }
        if (user is null || !user.IsEnabled || user.LockedUntil > now)
            throw new ApiException(401, "Invalid credentials or account temporarily unavailable.");
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= 5) user.LockedUntil = now.AddMinutes(15);
            await database.SaveChangesAsync(cancellationToken);
            throw new ApiException(401, "Invalid credentials or account temporarily unavailable.");
        }
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, request.Password);
        var token = NewToken();
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        if (user.MfaEnabled && user.LastChallengeAt > now.AddMinutes(-1))
            throw new ApiException(429, "Please wait one minute before requesting another code.");
        var session = new UserSession
        {
            TokenHash = HashToken(token), User = user, MfaPending = user.MfaEnabled,
            CodeHash = user.MfaEnabled ? HashToken(token + code) : null,
            ExpiresAt = now.AddMinutes(user.MfaEnabled ? 5 : 60)
        };
        if (user.MfaEnabled) user.LastChallengeAt = now;
        database.UserSessions.Add(session);
        await database.SaveChangesAsync(cancellationToken);
        if (user.MfaEnabled)
            await email.SendAsync(user.Email, "Your sign-in code", $"Your code is {code}. It expires in five minutes.", cancellationToken);
        return new(token, session.MfaPending, session.ExpiresAt, session.MfaPending ? null : Map(user));
    }

    public async Task<LoginResponse> VerifyAsync(VerifyMfaRequest request, CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(request.Challenge, cancellationToken);
        if (!session.MfaPending || session.Attempts >= 5) throw new ApiException(401, "Invalid or expired challenge.");
        session.Attempts++;
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(session.CodeHash!),
                Convert.FromHexString(HashToken(request.Challenge + request.Code))))
        {
            await database.SaveChangesAsync(cancellationToken);
            throw new ApiException(401, "Invalid or expired challenge.");
        }
        database.UserSessions.Remove(session);
        var token = NewToken();
        var authenticated = new UserSession { TokenHash = HashToken(token), User = session.User, ExpiresAt = clock.GetUtcNow().AddHours(1) };
        database.UserSessions.Add(authenticated);
        await database.SaveChangesAsync(cancellationToken);
        return new(token, false, authenticated.ExpiresAt, Map(session.User));
    }

    private async Task<UserSession> FindSessionAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) throw new ApiException(401, "Sign in is required.");
        var hash = HashToken(token);
        var session = await database.UserSessions.Include(session => session.User).ThenInclude(user => user.Role)
            .ThenInclude(role => role!.Responsibilities).SingleOrDefaultAsync(session => session.TokenHash == hash, cancellationToken);
        if (session is null || session.ExpiresAt <= clock.GetUtcNow() || !session.User.IsEnabled)
            throw new ApiException(401, "Sign in is required.");
        return session;
    }

    public async Task<UserResponse> RequireAsync(HttpRequest request, string? permission, CancellationToken cancellationToken)
    {
        var session = await FindSessionAsync(request.Headers["X-User-Session"].ToString(), cancellationToken);
        if (session.MfaPending) throw new ApiException(401, "Complete MFA before continuing.");
        var user = Map(session.User);
        if (permission is not null && !user.Permissions.Contains(permission)) throw new ApiException(403, "This action is not permitted.");
        return user;
    }

    public async Task<UserResponse> RequireAdminAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(request, null, cancellationToken);
        if (actor.Role != "Admin") throw new ApiException(403, "Only Admin can manage role responsibilities.");
        return actor;
    }

    public async Task<RolesResponse> ListRolesAsync(UserResponse actor, CancellationToken cancellationToken)
    {
        if (actor.Role != "Admin") throw new ApiException(403, "Only Admin can manage role responsibilities.");
        var roles = await database.Roles.AsNoTracking().Include(role => role.Responsibilities)
            .OrderBy(role => role.Name).ToListAsync(cancellationToken);
        return new(roles.Select(role => new RoleResponsibilitiesResponse(role.Id, role.Name,
            role.Responsibilities.Where(responsibility => responsibility.IsEnabled && Permissions.All.Contains(responsibility.Name))
                .Select(responsibility => responsibility.Name).Order().ToList())).ToList(), Permissions.All.ToList());
    }

    public async Task SaveRoleResponsibilitiesAsync(UserResponse actor, int roleId, SaveRoleResponsibilitiesRequest request, CancellationToken cancellationToken)
    {
        if (actor.Role != "Admin") throw new ApiException(403, "Only Admin can manage role responsibilities.");
        if (request.Responsibilities is null || request.Responsibilities.Count > Permissions.All.Length
            || request.Responsibilities.Any(permission => !Permissions.All.Contains(permission)))
            throw new ApiException(400, "Select only supported responsibilities.", "Responsibilities");
        var enabled = request.Responsibilities.ToHashSet(StringComparer.Ordinal);
        if (!enabled.Contains(Permissions.StaffRead) && enabled.Overlaps(
            [Permissions.StaffWrite, Permissions.DocumentsWrite, Permissions.DocumentsValidate, Permissions.LinksWrite, Permissions.TermsWrite]))
            throw new ApiException(400, "Staff.Read is required for staff editing, document editing or validation, links, and terms.", "Responsibilities");
        var role = await database.Roles.Include(role => role.Responsibilities)
            .SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken) ?? throw new ApiException(404, "Role not found.");
        var previous = role.Responsibilities.Where(responsibility => responsibility.IsEnabled && Permissions.All.Contains(responsibility.Name))
            .Select(responsibility => responsibility.Name).Order().ToList();
        foreach (var permission in Permissions.All)
        {
            var responsibility = role.Responsibilities.SingleOrDefault(responsibility => responsibility.Name == permission);
            if (responsibility is null)
                role.Responsibilities.Add(new Responsibility { Name = permission, IsEnabled = enabled.Contains(permission) });
            else
                responsibility.IsEnabled = enabled.Contains(permission);
        }
        database.AuditEntries.Add(new()
        {
            UserId = actor.Id, Action = "Role.Responsibilities", Subject = $"Role {role.Id}: [{string.Join(",", previous)}] -> [{string.Join(",", enabled.Order())}]"
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task LogoutAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var hash = HashToken(request.Headers["X-User-Session"].ToString());
        var session = await database.UserSessions.FindAsync([hash], cancellationToken);
        if (session is not null) database.UserSessions.Remove(session);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<UsersResponse> ListAsync(CancellationToken cancellationToken) => new(
        (await database.Users.Include(user => user.Role).ThenInclude(role => role!.Responsibilities).OrderBy(user => user.Email).ToListAsync(cancellationToken)).Select(Map).ToList(),
        await database.Roles.Select(role => new LookupResponse(role.Id, role.Name, null)).ToListAsync(cancellationToken));

    public async Task<UserResponse> SaveAsync(UserResponse actor, int? id, SaveUserRequest request, CancellationToken cancellationToken)
    {
        var role = await database.Roles.Include(role => role.Responsibilities).SingleOrDefaultAsync(role => role.Id == request.RoleId, cancellationToken)
            ?? throw new ApiException(400, "Select a valid role.");
        var user = id is null ? new User() : await database.Users.Include(user => user.Role).SingleOrDefaultAsync(user => user.Id == id, cancellationToken)
            ?? throw new ApiException(404, "Account not found.");
        if (actor.Role != "Admin" && (role.Name == "Admin" || user.Role?.Name == "Admin"))
            throw new ApiException(403, "Only Admin can manage Admin accounts.");
        if (id == actor.Id && (!request.IsEnabled || request.RoleId != actor.RoleId))
            throw new ApiException(409, "You cannot disable or change your own role.");
        if (user.Role?.Name == "Admin" && (!request.IsEnabled || role.Name != "Admin")
            && !await database.Users.AnyAsync(other => other.Id != id && other.IsEnabled && other.Role!.Name == "Admin", cancellationToken))
            throw new ApiException(409, "At least one enabled Admin is required.");
        user.Email = request.Email.Trim().ToLowerInvariant();
        user.Role = role;
        user.RoleId = role.Id;
        user.IsEnabled = request.IsEnabled;
        user.MfaEnabled = request.MfaEnabled;
        if (!string.IsNullOrWhiteSpace(request.Password)) user.PasswordHash = Hasher.HashPassword(user, request.Password);
        if (string.IsNullOrEmpty(user.PasswordHash)) throw new ApiException(400, "A password of at least 12 characters is required.");
        if (id is null) database.Users.Add(user);
        else database.UserSessions.RemoveRange(await database.UserSessions.Where(session => session.UserId == id).ToListAsync(cancellationToken));
        database.AuditEntries.Add(new() { UserId = actor.Id, Action = "Account.Save", Subject = user.Email });
        await database.SaveChangesAsync(cancellationToken);
        return Map(user);
    }

    public async Task SetMfaAsync(UserResponse actor, bool enabled, CancellationToken cancellationToken)
    {
        var user = await database.Users.FindAsync([actor.Id], cancellationToken) ?? throw new ApiException(404, "Account not found.");
        user.MfaEnabled = enabled;
        database.UserSessions.RemoveRange(await database.UserSessions.Where(session => session.UserId == actor.Id).ToListAsync(cancellationToken));
        database.AuditEntries.Add(new() { UserId = actor.Id, Action = "Account.Mfa", Subject = enabled.ToString() });
        await database.SaveChangesAsync(cancellationToken);
    }

    private static UserResponse Map(User user) => new(user.Id, user.Email, user.RoleId, user.Role?.Name ?? "", user.IsEnabled, user.MfaEnabled,
        user.Role?.Responsibilities.Where(responsibility => responsibility.IsEnabled).Select(responsibility => responsibility.Name).ToList() ?? []);
}