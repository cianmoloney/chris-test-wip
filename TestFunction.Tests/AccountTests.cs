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