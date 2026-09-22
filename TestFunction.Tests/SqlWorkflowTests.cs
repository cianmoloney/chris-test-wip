using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        var connection = Environment.GetEnvironmentVariable("HR_TEST_SQL");
        if (string.IsNullOrWhiteSpace(connection) || !new SqlConnectionStringBuilder(connection).InitialCatalog.StartsWith("HrImplementationVerification_", StringComparison.Ordinal))
            Skip = "Set HR_TEST_SQL to a published, disposable HrImplementationVerification_ database.";
    }
}

public sealed class SqlWorkflowTests
{
    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("HR_TEST_SQL"), options => options.EnableRetryOnFailure()).Options);
    private static LinkService Links(AppDbContext database) => new(database, new StaffDataService(database), TimeProvider.System);

    [SqlFact]
    public async Task RoleResponsibilityChangesPersistAndAffectExistingSqlSessions()
    {
        await using var database = Database();
        await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync();
            var adminRole = await database.Roles.SingleAsync(role => role.Name == "Admin");
            var foremanRole = await database.Roles.SingleAsync(role => role.Name == "Foreman");
            var admin = new User { Email = $"{Guid.NewGuid():N}@example.test", RoleId = adminRole.Id, PasswordHash = "unused" };
            var foreman = new User { Email = $"{Guid.NewGuid():N}@example.test", RoleId = foremanRole.Id, PasswordHash = "unused" };
            database.Users.AddRange(admin, foreman);
            var token = AccountService.NewToken();
            database.UserSessions.Add(new() { TokenHash = AccountService.HashToken(token), User = foreman, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
            await database.SaveChangesAsync();
            var actor = new UserResponse(admin.Id, admin.Email, adminRole.Id, "Admin", true, false, []);
            var service = new AccountService(database, new NoRoleTestEmail(), TimeProvider.System);
            var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
            request.Headers["X-User-Session"] = token;

            await service.SaveRoleResponsibilitiesAsync(actor, foremanRole.Id,
                new() { Responsibilities = [Permissions.StaffRead, Permissions.DocumentsWrite] }, default);
            database.ChangeTracker.Clear();
            Assert.Contains(Permissions.DocumentsWrite, (await service.RequireAsync(request, Permissions.DocumentsWrite, default)).Permissions);
            await service.SaveRoleResponsibilitiesAsync(actor, foremanRole.Id, new() { Responsibilities = [] }, default);
            database.ChangeTracker.Clear();

            Assert.Empty((await service.RequireAsync(request, null, default)).Permissions);
            Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(request, Permissions.DocumentsWrite, default))).StatusCode);
            var responsibilities = await database.Responsibilities.Where(responsibility => responsibility.RoleId == foremanRole.Id).ToListAsync();
            Assert.Equal(Permissions.All.Length, responsibilities.Count(responsibility => Permissions.All.Contains(responsibility.Name)));
            Assert.All(responsibilities, responsibility => Assert.False(responsibility.IsEnabled));
            Assert.Equal(2, await database.AuditEntries.CountAsync(entry => entry.UserId == actor.Id && entry.Action == "Role.Responsibilities"));
            await transaction.RollbackAsync();
        });
    }

    private sealed class NoRoleTestEmail : IEmailService
    {
        public Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [SqlFact]
    public async Task ConcurrentRegistrationCommitsOnlyOneWorker()
    {
        await using var database = Database();
        var link = await Links(database).CreateAsync(1, new("register", null, null), default);
        var prefix = Guid.NewGuid().ToString("N");
        async Task<bool> RegisterAsync(string suffix)
        {
            await using var attempt = Database();
            try
            {
                await Links(attempt).RegisterAsync(new(link.Token, new()
                {
                    FirstName = "Concurrency", LastName = "Test", Email = $"{prefix}{suffix}@example.test", StaffTypeId = 1, StaffRoleId = 1
                }), default);
                return true;
            }
            catch (ApiException exception) when (exception.StatusCode is 403 or 409) { return false; }
        }
        var results = await Task.WhenAll(RegisterAsync("one"), RegisterAsync("two"));
        Assert.Single(results, result => result);
        Assert.Equal(1, await database.Staff.CountAsync(staff => staff.Email.StartsWith(prefix)));
        var hash = AccountService.HashToken(link.Token);
        Assert.NotNull((await database.ShareLinks.AsNoTracking().SingleAsync(stored => stored.TokenHash == hash)).UsedAt);
    }

    [SqlFact]
    public async Task MultipleRegistrationRollsBackOnErrorAndLinkCanBeRetriedOnce()
    {
        await using var database = Database();
        var prefix = Guid.NewGuid().ToString("N");
        CreateStaffRequest Staff(string suffix) => new()
        {
            FirstName = "Batch", LastName = suffix, Email = $"{prefix}{suffix}@example.test", StaffTypeId = 1, StaffRoleId = 1
        };
        var duplicate = Staff("existing");
        await new StaffDataService(database).CreateStaffAsync(duplicate, default);
        var issued = await Links(database).CreateAsync(1, new(ShareLinkPurposes.RegisterMultiple, null, null), default);
        var error = await Assert.ThrowsAsync<ApiException>(() => Links(database).RegisterMultipleAsync(
            new(issued.Token, [Staff("first"), duplicate]), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Staff[1].Email", error.Field);
        Assert.Equal(1, await database.Staff.AsNoTracking().CountAsync(staff => staff.Email.StartsWith(prefix)));
        var hash = AccountService.HashToken(issued.Token);
        Assert.Null((await database.ShareLinks.AsNoTracking().SingleAsync(link => link.TokenHash == hash)).UsedAt);

        var request = new LinkMultipleRegistrationRequest(issued.Token, [Staff("first"), Staff("second")]);
        var result = await Links(database).RegisterMultipleAsync(request, default);
        Assert.Equal(2, result.Staff.Count);
        Assert.All(result.Staff, staff => Assert.False(string.IsNullOrWhiteSpace(staff.StaffId)));
        Assert.Equal(2, result.Staff.Select(staff => staff.StaffNumber).Distinct().Count());
        Assert.Equal(3, await database.Staff.AsNoTracking().CountAsync(staff => staff.Email.StartsWith(prefix)));
        Assert.NotNull((await database.ShareLinks.AsNoTracking().SingleAsync(link => link.TokenHash == hash)).UsedAt);
        var reused = await Assert.ThrowsAsync<ApiException>(() => Links(database).RegisterMultipleAsync(request, default));
        Assert.Equal(403, reused.StatusCode);
    }

    [SqlFact]
    public async Task ConcurrentMultipleRegistrationCommitsOnlyOneBatch()
    {
        await using var database = Database();
        var link = await Links(database).CreateAsync(1, new(ShareLinkPurposes.RegisterMultiple, null, null), default);
        var prefix = Guid.NewGuid().ToString("N");
        async Task<bool> RegisterAsync(string suffix)
        {
            await using var attempt = Database();
            try
            {
                var staff = Enumerable.Range(1, 2).Select(number => new CreateStaffRequest
                {
                    FirstName = "Batch", LastName = suffix, Email = $"{prefix}{suffix}{number}@example.test", StaffTypeId = 1, StaffRoleId = 1
                }).ToList();
                await Links(attempt).RegisterMultipleAsync(new(link.Token, staff), default);
                return true;
            }
            catch (ApiException exception) when (exception.StatusCode is 403 or 409) { return false; }
        }

        var results = await Task.WhenAll(RegisterAsync("one"), RegisterAsync("two"));

        Assert.Single(results, result => result);
        Assert.Equal(2, await database.Staff.CountAsync(staff => staff.Email.StartsWith(prefix)));
    }

    [SqlFact]
    public async Task MultipleRegistrationRejectsExpiredRevokedAndWrongPurposeLinks()
    {
        await using var database = Database();
        foreach (var scenario in new[] { "expired", "revoked", "purpose" })
        {
            var issued = await Links(database).CreateAsync(1, new(scenario == "purpose" ? ShareLinkPurposes.Register : ShareLinkPurposes.RegisterMultiple, null, null), default);
            var stored = await database.ShareLinks.SingleAsync(link => link.UploadId == issued.Id);
            if (scenario == "expired") stored.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (scenario == "revoked") stored.RevokedAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync();
            var email = $"{Guid.NewGuid():N}@example.test";

            var error = await Assert.ThrowsAsync<ApiException>(() => Links(database).RegisterMultipleAsync(new(issued.Token,
                [new() { FirstName = "Batch", LastName = "Invalid", Email = email, StaffTypeId = 1, StaffRoleId = 1 }]), default));

            Assert.Equal(403, error.StatusCode);
            Assert.False(await database.Staff.AnyAsync(staff => staff.Email == email));
        }
    }

    [SqlFact]
    public async Task DocumentTypeManagementPersistsIdentifierAndRoleChangesInSql()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var name = "SQL document type " + Guid.NewGuid().ToString("N");
        var created = await service.CreateDocumentTypeAsync(new()
        {
            Name = name, TextIdentifier = "INITIAL CERTIFICATE", StaffRoleIds = [1, 2, 1]
        }, default);
        database.ChangeTracker.Clear();
        var stored = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("INITIAL CERTIFICATE", stored.TextIdentifier);
        Assert.Equal(new[] { 1, 2 }, stored.StaffRoleIds);

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => service.CreateDocumentTypeAsync(new()
        {
            Name = name.ToUpperInvariant(), TextIdentifier = "DUPLICATE", StaffRoleIds = [3]
        }, default));
        Assert.Equal(409, duplicate.StatusCode);
        var invalidRole = await Assert.ThrowsAsync<ApiException>(() => service.UpdateDocumentTypeAsync(created.Id, new()
        {
            Name = name, TextIdentifier = "INVALID UPDATE", StaffRoleIds = [int.MaxValue]
        }, default));
        Assert.Equal(400, invalidRole.StatusCode);
        Assert.Equal("INITIAL CERTIFICATE", (await service.GetDocumentTypeAsync(created.Id, default)).TextIdentifier);

        await service.UpdateDocumentTypeAsync(created.Id, new()
        {
            Name = name, TextIdentifier = "UPDATED CERTIFICATE", StaffRoleIds = [2, 3]
        }, default);
        database.ChangeTracker.Clear();
        stored = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("UPDATED CERTIFICATE", stored.TextIdentifier);
        Assert.Equal(new[] { 2, 3 }, stored.StaffRoleIds);
        await service.UpdateDocumentTypeAsync(created.Id, new()
        {
            Name = name, TextIdentifier = "UPDATED CERTIFICATE", StaffRoleIds = []
        }, default);
        Assert.Empty((await service.GetDocumentTypeAsync(created.Id, default)).StaffRoleIds);
    }

    [SqlFact]
    public async Task UploadReservationUsesSqlRowVersion()
    {
        await using var creator = Database();
        var issued = await Links(creator).CreateAsync(1, new("upload", null, null), default);
        await using var first = Database();
        await using var second = Database();
        var firstLink = await Links(first).RequireAsync(issued.Token, "upload", default);
        var secondLink = await Links(second).RequireAsync(issued.Token, "upload", default);
        firstLink.BlobName = "09/first.pdf";
        secondLink.BlobName = "09/second.pdf";
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [SqlFact]
    public async Task StaffRoleAndRequirementsPersistAcrossReloads()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var suffix = Guid.NewGuid().ToString("N");
        var role = await service.CreateStaffRoleAsync(new() { Name = "Role " + suffix }, default);
        var otherRole = await service.CreateStaffRoleAsync(new() { Name = "Other role " + suffix }, default);
        var type = await service.CreateDocumentTypeAsync(new() { Name = "Type " + suffix, TextIdentifier = "CERTIFICATE", StaffRoleIds = [otherRole.Id] }, default);

        await service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [type.Id, type.Id] }, default);
        database.ChangeTracker.Clear();

        var data = await service.GetDocumentTypeManagementAsync(default);
        Assert.Contains(role, data.StaffRoles);
        Assert.Equal(new[] { role.Id, otherRole.Id }.Order(), data.DocumentTypes.Single(candidate => candidate.Id == type.Id).StaffRoleIds);
        Assert.Contains(role, (await service.GetLookupsAsync(default)).StaffRoles);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.CreateStaffRoleAsync(new() { Name = role.Name.ToUpperInvariant() }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [int.MaxValue] }, default))).StatusCode);
        database.ChangeTracker.Clear();
        Assert.True(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == role.Id && requirement.DocumentTypeId == type.Id));

        await service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [] }, default);
        database.ChangeTracker.Clear();

        Assert.False(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == role.Id));
        Assert.True(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == otherRole.Id && requirement.DocumentTypeId == type.Id));
    }

    [SqlFact]
    public async Task PublishingPreservesAcceptedTextAndRequiresNewEdition()
    {
        await using var database = Database();
        var publisher = new TermsService(database);
        var terms = await publisher.PublishAsync(1, new() { Title = "Terms " + Guid.NewGuid(), English = "Edition one", Polish = "Polish one", Ukrainian = "Ukrainian one" }, default);
        var staff = await new StaffDataService(database).CreateStaffAsync(new()
        {
            FirstName = "Terms", LastName = "Test", Email = $"{Guid.NewGuid():N}@example.test", StaffTypeId = 1, StaffRoleId = 1
        }, default);
        var link = await Links(database).CreateAsync(1, new("terms", staff.Id, terms.Id), default);
        var offered = await Links(database).ResolveAsync(new(link.Token, "terms", "pl"), default);
        var accepted = await Links(database).AcceptAsync(new(link.Token, offered.Terms!.Id, true), default);
        Assert.Equal("pl", accepted.TermsDocumentVersion.Language);
        await publisher.PublishAsync(1, new() { TermsDocumentId = terms.Id, Title = terms.Title, English = "Edition two", Polish = "Polish two", Ukrainian = "Ukrainian two" }, default);
        var error = await Assert.ThrowsAsync<ApiException>(() => Links(database).AcceptAsync(new(link.Token, offered.Terms.Id, true), default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("Polish one", (await database.TermsDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == offered.Terms.Id)).Content);
        Assert.Equal(2, (await database.StaffTermsAssignments.AsNoTracking().SingleAsync(assignment => assignment.StaffId == staff.Id)).Version);
        var history = await publisher.GetVersionsAsync(terms.Id, default);
        Assert.Equal(6, history.Count);
        Assert.Equal(new[] { "en", "pl", "uk", "en", "pl", "uk" }, history.Select(version => version.Language));
        Assert.Equal("Edition two", history.First().Content);
        Assert.True(history.First().IsActive);
        var acceptedHistory = Assert.Single(history, version => version.Id == offered.Terms.Id);
        Assert.Equal("Polish one", acceptedHistory.Content);
        Assert.False(acceptedHistory.IsActive);
    }
}