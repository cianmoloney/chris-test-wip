using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class AccountTests
{
    [Fact]
    public async Task AdminCanGrantAndRevokeResponsibilitiesForExistingSessions()
    {
        await using var database = CreateDatabase();
        database.Users.Add(new User { Id = 20, Email = "admin@example.test", RoleId = 2, PasswordHash = "unused" });
        var user = (await database.Users.FindAsync(10))!;
        user.RoleId = 3;
        database.UserSessions.Add(new() { TokenHash = AccountService.HashToken("live-session"), UserId = 10, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await database.SaveChangesAsync();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var actor = new UserResponse(20, "admin@example.test", 2, "Admin", true, false, []);
        var request = new DefaultHttpContext().Request;
        request.Headers["X-User-Session"] = "live-session";
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.StaffWrite, default))).StatusCode);

        await service.SaveRoleResponsibilitiesAsync(actor, 3, new() { Responsibilities = [Permissions.StaffRead, Permissions.StaffWrite] }, default);
        database.ChangeTracker.Clear();
        Assert.Contains(Permissions.StaffWrite, (await service.RequireAsync(request, Permissions.StaffWrite, default)).Permissions);
        await service.SaveRoleResponsibilitiesAsync(actor, 3, new() { Responsibilities = [] }, default);
        database.ChangeTracker.Clear();

        Assert.Empty((await service.RequireAsync(request, null, default)).Permissions);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.StaffWrite, default))).StatusCode);
        var rows = await database.Responsibilities.Where(responsibility => responsibility.RoleId == 3).ToListAsync();
        Assert.Equal(Permissions.All.Length, rows.Count);
        Assert.All(rows, responsibility => Assert.False(responsibility.IsEnabled));
        Assert.Equal(2, await database.AuditEntries.CountAsync(entry => entry.Action == "Role.Responsibilities"));
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Foreman")]
    public async Task NonAdminCannotManageRolesEvenWithAllResponsibilities(string role)
    {
        await using var database = CreateDatabase();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var actor = new UserResponse(10, "user@example.test", role == "HR" ? 1 : 3, role, true, false, Permissions.All.ToList());
        var before = await database.Responsibilities.CountAsync(responsibility => responsibility.IsEnabled);

        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.ListRolesAsync(actor, default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleResponsibilitiesAsync(actor, 1, new() { Responsibilities = [] }, default))).StatusCode);

        Assert.Equal(before, await database.Responsibilities.CountAsync(responsibility => responsibility.IsEnabled));
        Assert.Empty(await database.AuditEntries.ToListAsync());
    }

    [Theory]
    [InlineData("Roles.Write")]
    [InlineData("Staff.Write")]
    [InlineData("Documents.Write")]
    [InlineData("Documents.Validate")]
    [InlineData("Links.Write")]
    [InlineData("Terms.Write")]
    public async Task UnknownOrDependentResponsibilitiesAreRejectedWithoutChanges(string permission)
    {
        await using var database = CreateDatabase();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var actor = new UserResponse(10, "admin@example.test", 2, "Admin", true, false, []);
        var before = await database.Responsibilities.CountAsync(responsibility => responsibility.IsEnabled);

        var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleResponsibilitiesAsync(actor, 3, new() { Responsibilities = [permission] }, default));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("Responsibilities", error.Field);
        Assert.Equal(before, await database.Responsibilities.CountAsync(responsibility => responsibility.IsEnabled));
    }

    [Fact]
    public async Task AdminWithoutOptionalPermissionsCanStillManageRoles()
    {
        await using var database = CreateDatabase();
        (await database.Users.FindAsync(10))!.RoleId = 2;
        database.UserSessions.Add(new() { TokenHash = AccountService.HashToken("admin-session"), UserId = 10, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await database.SaveChangesAsync();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var request = new DefaultHttpContext().Request;
        request.Headers["X-User-Session"] = "admin-session";
        var actor = await service.RequireAdminAsync(request, default);

        await service.SaveRoleResponsibilitiesAsync(actor, 2, new() { Responsibilities = [] }, default);
        database.ChangeTracker.Clear();
        var refreshed = await service.RequireAdminAsync(request, default);

        Assert.Empty(refreshed.Permissions);
        Assert.Equal(3, (await service.ListRolesAsync(refreshed, default)).Roles.Count);
        await service.SaveRoleResponsibilitiesAsync(refreshed, 2, new() { Responsibilities = Permissions.All.ToList() }, default);
        Assert.Equal(Permissions.All.Length, (await service.RequireAdminAsync(request, default)).Permissions.Count);
    }

    [Fact]
    public async Task ForemanCanValidateAndIssueLinksButCannotEditStaffOrManageAccounts()
    {
        await using var database = CreateDatabase();
        var user = await database.Users.FindAsync(10);
        user!.RoleId = 3;
        database.UserSessions.Add(new() { TokenHash = AccountService.HashToken("foreman-session"), UserId = user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await database.SaveChangesAsync();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var request = new DefaultHttpContext().Request;
        request.Headers["X-User-Session"] = "foreman-session";
        await service.RequireAsync(request, Permissions.DocumentsValidate, default);
        await service.RequireAsync(request, Permissions.LinksWrite, default);
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.StaffWrite, default));
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.UsersWrite, default));
        user.IsEnabled = false;
        await database.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.StaffRead, default));
    }
    [Fact]
    public async Task MfaChallengeCannotAuthorizeAndCannotBeReplayed()
    {
        await using var database = CreateDatabase();
        var sender = new RecordingEmail();
        var service = new AccountService(database, sender, TimeProvider.System);
        var login = await service.LoginAsync(new("hr@example.test", "Long test password!"), default);
        Assert.True(login.RequiresMfa);
        var request = new DefaultHttpContext().Request;
        request.Headers["X-User-Session"] = login.Token;
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, null, default))).StatusCode);
        var code = System.Text.RegularExpressions.Regex.Match(sender.Text!, @"\b\d{6}\b").Value;
        var authenticated = await service.VerifyAsync(new(login.Token, code), default);
        Assert.False(authenticated.RequiresMfa);
        Assert.NotEqual(login.Token, authenticated.Token);
        await Assert.ThrowsAsync<ApiException>(() => service.VerifyAsync(new(login.Token, code), default));
        request.Headers["X-User-Session"] = authenticated.Token;
        Assert.Equal("HR", (await service.RequireAsync(request, null, default)).Role);
        await service.LogoutAsync(request, default);
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, null, default));
    }

    [Fact]
    public async Task HrCannotGrantAdmin()
    {
        await using var database = CreateDatabase();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        var actor = new UserResponse(10, "hr@example.test", 1, "HR", true, false, [Permissions.UsersWrite]);
        var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(actor, null,
            new() { Email = "admin@example.test", RoleId = 2, Password = "Long test password!" }, default));
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task FiveWrongPasswordsLockAccount()
    {
        await using var database = CreateDatabase();
        var service = new AccountService(database, new RecordingEmail(), TimeProvider.System);
        for (var attempt = 0; attempt < 5; attempt++)
            await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync(new("hr@example.test", "wrong"), default));
        await Assert.ThrowsAsync<ApiException>(() => service.LoginAsync(new("hr@example.test", "Long test password!"), default));
    }

    private static AppDbContext CreateDatabase()
    {
        var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        var user = new User { Id = 10, Email = "hr@example.test", RoleId = 1, MfaEnabled = true };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "Long test password!");
        database.Users.Add(user);
        database.SaveChanges();
        return database;
    }

    private sealed class RecordingEmail : IEmailService
    {
        public string? Text { get; private set; }
        public Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }
}