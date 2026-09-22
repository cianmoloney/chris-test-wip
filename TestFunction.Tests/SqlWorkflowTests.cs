using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
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
    public async Task SharedTermsHaveOneHistoryAndRequireLatestAcceptanceInEveryRole()
    {
        await using var database = Database();
        var staffData = new StaffDataService(database);
        var firstRole = await staffData.CreateStaffRoleAsync(new() { Name = "Shared terms " + Guid.NewGuid().ToString("N") }, default);
        var secondRole = await staffData.CreateStaffRoleAsync(new() { Name = "Shared terms " + Guid.NewGuid().ToString("N") }, default);
        var first = await staffData.CreateStaffAsync(new() { FirstName = "Shared", LastName = "First", Email = $"{Guid.NewGuid():N}@example.test", StaffTypeId = 1, StaffRoleId = firstRole.Id }, default);
        var second = await staffData.CreateStaffAsync(new() { FirstName = "Shared", LastName = "Second", Email = $"{Guid.NewGuid():N}@example.test", StaffTypeId = 1, StaffRoleId = secondRole.Id }, default);
        var publisher = new TermsService(database);
        var terms = await publisher.PublishAsync(1, new() { Title = "Shared policy " + Guid.NewGuid(), English = "Edition one", Polish = "Polish one", Ukrainian = "Ukrainian one" }, default);
        await publisher.SaveRoleAsync(1, terms.Id, new() { StaffRoleIds = [firstRole.Id, secondRole.Id, firstRole.Id], ExpectedRevision = terms.RoleRevision }, default);
        Assert.Equal(2, await database.StaffRoleTermsDocuments.CountAsync(requirement => requirement.TermsDocumentId == terms.Id));
        var workers = await database.Staff.AsNoTracking().Where(worker => worker.Id == first.Id || worker.Id == second.Id).ToListAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.All((await eligibility.GetAsync(workers, default)).Values, result => Assert.False(result.IsReady));
        var acceptedVersionIds = new List<int>();
        foreach (var worker in workers)
        {
            var link = await Links(database).CreateAsync(1, new("terms", worker.Id, terms.Id), default);
            var offered = (await Links(database).ResolveAsync(new(link.Token, "terms", "pl"), default)).Terms!;
            acceptedVersionIds.Add(offered.Id);
            await Links(database).AcceptAsync(new(link.Token, offered.Id, true), default);
        }
        Assert.Single(acceptedVersionIds.Distinct());
        Assert.All((await eligibility.GetAsync(workers, default)).Values, result => Assert.True(result.IsReady));
        var published = await publisher.PublishAsync(1, new() { TermsDocumentId = terms.Id, Title = terms.Title, English = "Edition two", Polish = "Polish two", Ukrainian = "Ukrainian two" }, default);
        Assert.Equal(new[] { firstRole.Id, secondRole.Id }.Order(), published.StaffRoleIds);
        Assert.All((await eligibility.GetAsync(workers, default)).Values, result => Assert.False(result.IsReady));
        var latestLink = await Links(database).CreateAsync(1, new("terms", first.Id, terms.Id), default);
        var latest = (await Links(database).ResolveAsync(new(latestLink.Token, "terms", "uk"), default)).Terms!;
        await Links(database).AcceptAsync(new(latestLink.Token, latest.Id, true), default);
        var readiness = await eligibility.GetAsync(workers, default);
        Assert.True(readiness[first.Id].IsReady);
        Assert.False(readiness[second.Id].IsReady);
        await publisher.SaveRoleAsync(1, terms.Id, new() { StaffRoleIds = [secondRole.Id], ExpectedRevision = published.RoleRevision }, default);
        Assert.Equal(secondRole.Id, (await database.StaffRoleTermsDocuments.AsNoTracking().SingleAsync(requirement => requirement.TermsDocumentId == terms.Id)).StaffRoleId);
        Assert.Single(await database.TermsDocuments.Where(document => document.Title == terms.Title).ToListAsync());
        Assert.Equal(6, await database.TermsDocumentVersions.CountAsync(version => version.TermsDocumentId == terms.Id));
        Assert.Equal(3, await database.StaffTermsAcceptances.CountAsync(acceptance => acceptance.TermsDocumentVersion.TermsDocumentId == terms.Id));
        Assert.Equal("Polish one", (await database.TermsDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == acceptedVersionIds[0])).Content);
        await using var duplicate = Database();
        duplicate.StaffRoleTermsDocuments.Add(new() { StaffRoleId = secondRole.Id, TermsDocumentId = terms.Id });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.True(error.InnerException is SqlException { Number: 2601 or 2627 });
    }

    [SqlFact]
    public async Task RoleTermsRequireEveryLatestEditionForExistingAndNewStaff()
    {
        await using var database = Database();
        var staffData = new StaffDataService(database);
        var role = await staffData.CreateStaffRoleAsync(new() { Name = "Terms " + Guid.NewGuid().ToString("N") }, default);
        var existing = await staffData.CreateStaffAsync(new() { FirstName = "Terms", LastName = "Existing", Email = $"{Guid.NewGuid():N}@example.test", StaffTypeId = 1, StaffRoleId = role.Id }, default);
        var publisher = new TermsService(database);
        var safety = await publisher.PublishAsync(1, new() { Title = "Safety " + Guid.NewGuid(), English = "Safety one", Polish = "Safety PL", Ukrainian = "Safety UK" }, default);
        var conduct = await publisher.PublishAsync(1, new() { Title = "Conduct " + Guid.NewGuid(), English = "Conduct one", Polish = "Conduct PL", Ukrainian = "Conduct UK" }, default);
        foreach (var terms in new[] { safety, conduct })
            await publisher.SaveRoleAsync(1, terms.Id, new() { StaffRoleIds = [role.Id], ExpectedRevision = terms.RoleRevision }, default);
        var registration = await Links(database).CreateAsync(1, new("register", null, null), default);
        var created = await Links(database).RegisterAsync(new(registration.Token,
            new() { FirstName = "Terms", LastName = "New", Email = $"{Guid.NewGuid():N}@example.test", StaffTypeId = 1, StaffRoleId = role.Id }), default);
        var workers = await database.Staff.AsNoTracking().Where(worker => worker.Id == existing.Id || worker.Id == created.Id).ToListAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.All((await eligibility.GetAsync(workers, default)).Values, readiness => { Assert.False(readiness.IsReady); Assert.Equal(2, readiness.Reasons.Count); });
        Assert.False(await database.StaffTermsAssignments.AnyAsync(assignment => assignment.StaffId == existing.Id || assignment.StaffId == created.Id));

        var safetyLink = await Links(database).CreateAsync(1, new("terms", existing.Id, safety.Id), default);
        var safetyText = (await Links(database).ResolveAsync(new(safetyLink.Token, "terms", "pl"), default)).Terms!;
        await Links(database).AcceptAsync(new(safetyLink.Token, safetyText.Id, true), default);
        Assert.False((await eligibility.GetAsync(workers, default))[existing.Id].IsReady);
        var conductLink = await Links(database).CreateAsync(1, new("terms", existing.Id, conduct.Id), default);
        var conductText = (await Links(database).ResolveAsync(new(conductLink.Token, "terms", "uk"), default)).Terms!;
        await Links(database).AcceptAsync(new(conductLink.Token, conductText.Id, true), default);
        Assert.True((await eligibility.GetAsync(workers, default))[existing.Id].IsReady);
        Assert.False((await eligibility.GetAsync(workers, default))[created.Id].IsReady);

        await publisher.PublishAsync(1, new() { TermsDocumentId = safety.Id, Title = safety.Title, English = "Safety two", Polish = "Safety two PL", Ukrainian = "Safety two UK" }, default);
        Assert.Equal(role.Id, (await database.StaffRoleTermsDocuments.AsNoTracking().SingleAsync(requirement => requirement.TermsDocumentId == safety.Id)).StaffRoleId);
        Assert.False((await eligibility.GetAsync(workers, default))[existing.Id].IsReady);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Links(database).AcceptAsync(new(safetyLink.Token, safetyText.Id, true), default))).StatusCode);
        var latestLink = await Links(database).CreateAsync(1, new("terms", existing.Id, safety.Id), default);
        var latest = (await Links(database).ResolveAsync(new(latestLink.Token, "terms", "en"), default)).Terms!;
        Assert.Equal(2, latest.Version);
        await Links(database).AcceptAsync(new(latestLink.Token, latest.Id, true), default);
        Assert.True((await eligibility.GetAsync(workers, default))[existing.Id].IsReady);
        Assert.Equal("Safety PL", (await database.TermsDocumentVersions.AsNoTracking().SingleAsync(version => version.Id == safetyText.Id)).Content);
        Assert.Equal(3, await database.StaffTermsAcceptances.CountAsync(acceptance => acceptance.StaffId == existing.Id));
        foreach (var terms in new[] { safety, conduct })
            await publisher.SaveRoleAsync(1, terms.Id, new() { StaffRoleIds = [], ExpectedRevision = AssignmentRevision.TermsRole([role.Id]) }, default);
        Assert.True((await eligibility.GetAsync(workers, default))[created.Id].IsReady);
    }

    [SqlFact]
    public async Task ConcurrentTermsRoleSavesRejectOneStaleWriter()
    {
        await using var database = Database();
        var staffData = new StaffDataService(database);
        var firstRole = await staffData.CreateStaffRoleAsync(new() { Name = "Terms concurrency " + Guid.NewGuid().ToString("N") }, default);
        var secondRole = await staffData.CreateStaffRoleAsync(new() { Name = "Terms concurrency " + Guid.NewGuid().ToString("N") }, default);
        var terms = new TermsDocument { Title = "Concurrent terms " + Guid.NewGuid() };
        database.TermsDocuments.Add(terms);
        await database.SaveChangesAsync();
        async Task<int> SaveAsync(int roleId)
        {
            await using var attempt = Database();
            try
            {
                await new TermsService(attempt).SaveRoleAsync(1, terms.Id,
                    new() { StaffRoleIds = [roleId], ExpectedRevision = AssignmentRevision.TermsRole([]) }, default);
                return roleId;
            }
            catch (ApiException exception) when (exception.StatusCode == 409) { return 0; }
        }
        var results = await Task.WhenAll(SaveAsync(firstRole.Id), SaveAsync(secondRole.Id));
        var winner = Assert.Single(results, result => result != 0);
        Assert.Single(results, result => result == 0);
        Assert.Equal(winner, (await database.StaffRoleTermsDocuments.AsNoTracking().SingleAsync(requirement => requirement.TermsDocumentId == terms.Id)).StaffRoleId);
        Assert.Single(await database.AuditEntries.Where(entry => entry.Action == "Terms.Role" && entry.Subject.StartsWith($"Terms {terms.Id}:")).ToListAsync());
    }

    [SqlFact]
    public async Task CurrentAccountQueriesSessionOncePerHttpRequestAndRejectsDisabledAccountOnNextRequest()
    {
        await using var creator = Database();
        var roleId = await creator.Roles.Where(role => role.Name == "Admin").Select(role => role.Id).SingleAsync();
        var user = new User { Email = $"{Guid.NewGuid():N}@example.test", RoleId = roleId, PasswordHash = "unused" };
        var token = AccountService.NewToken();
        creator.UserSessions.Add(new() { TokenHash = AccountService.HashToken(token), User = user, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        await creator.SaveChangesAsync();
        var queries = new SessionQueryCounter();
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("HR_TEST_SQL"), options => options.EnableRetryOnFailure())
            .AddInterceptors(queries).Options);
        var accounts = new AccountService(database, new NoRoleTestEmail(), TimeProvider.System);
        using var services = new ServiceCollection().AddSingleton(accounts)
            .AddSingleton<IStaffDataService>(new StaffDataService(database)).BuildServiceProvider();
        var authorization = new SessionTestAuthorization();
        var api = new TestFunction.API(authorization, services, NullLogger<TestFunction.API>.Instance);
        HttpRequest Request()
        {
            var request = new DefaultHttpContext().Request;
            request.Headers["X-User-Session"] = token;
            return request;
        }

        var first = Request();
        var result = Assert.IsType<UserResponse>(Assert.IsType<OkObjectResult>(await api.CurrentAccount(first, default)).Value);
        Assert.Equal(user.Id, result.Id);
        Assert.Equal(1, queries.Count);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => accounts.RequireAsync(first, "Not.Granted", default))).StatusCode);
        await accounts.RequireAdminAsync(first, default);
        Assert.Equal(1, queries.Count);

        Assert.IsType<OkObjectResult>(await api.CurrentAccount(Request(), default));
        Assert.Equal(2, queries.Count);
        user.IsEnabled = false;
        await creator.SaveChangesAsync();
        Assert.Equal(401, Assert.IsType<ObjectResult>(await api.CurrentAccount(Request(), default)).StatusCode);
        Assert.Equal(3, queries.Count);
        Assert.Equal(3, authorization.Calls);
    }

    private sealed class SessionQueryCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("[UserSessions]", StringComparison.Ordinal)) Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SessionTestAuthorization : IApiAuthorization
    {
        public int Calls { get; private set; }
        public Task<int> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(0);
        }
    }

    [SqlFact]
    public async Task RoleResponsibilityChangesPersistAndAffectExistingSqlSessions()
    {
        await using var database = Database();
        await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
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
                new() { Responsibilities = [Permissions.StaffRead, Permissions.DocumentsWrite], ExpectedRevision = (await service.ListRolesAsync(actor, default)).Roles.Single(role => role.Id == foremanRole.Id).Revision }, default);
            database.ChangeTracker.Clear();
            Assert.Contains(Permissions.DocumentsWrite, (await service.RequireAsync(request, Permissions.DocumentsWrite, default)).Permissions);
            await service.SaveRoleResponsibilitiesAsync(actor, foremanRole.Id, new() { Responsibilities = [], ExpectedRevision = AssignmentRevision.Responsibilities([Permissions.StaffRead, Permissions.DocumentsWrite]) }, default);
            database.ChangeTracker.Clear();

            request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
            request.Headers["X-User-Session"] = token;
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
    public async Task ConcurrentStaffRoleSavesRejectOneStaleWriter()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var role = await service.CreateStaffRoleAsync(new() { Name = "Concurrency " + Guid.NewGuid().ToString("N") }, default);
        var typeIds = await database.DocumentTypes.OrderBy(type => type.Id).Take(2).Select(type => type.Id).ToArrayAsync();
        Assert.Equal(2, typeIds.Length);
        var revision = AssignmentRevision.Documents([]);
        async Task<int> SaveAsync(int typeId)
        {
            await using var attempt = Database();
            try
            {
                await new StaffDataService(attempt).SaveStaffRoleDocumentsAsync(role.Id,
                    new() { DocumentTypeIds = [typeId], ExpectedRevision = revision }, default);
                return typeId;
            }
            catch (ApiException exception) when (exception.StatusCode == 409) { return 0; }
        }

        var results = await Task.WhenAll(SaveAsync(typeIds[0]), SaveAsync(typeIds[1]));

        var winner = Assert.Single(results, result => result != 0);
        Assert.Single(results, result => result == 0);
        Assert.Equal(winner, await database.StaffRoleDocumentTypes.Where(requirement => requirement.StaffRoleId == role.Id)
            .Select(requirement => requirement.DocumentTypeId).SingleAsync());
    }

    [SqlFact]
    public async Task ConcurrentOfficeRoleSavesRejectOneStaleWriterWithoutDuplicateAudit()
    {
        await using var database = Database();
        var role = new Role { Name = "Concurrency " + Guid.NewGuid().ToString("N") };
        var adminRoleId = await database.Roles.Where(candidate => candidate.Name == "Admin").Select(candidate => candidate.Id).SingleAsync();
        var admin = new User { Email = $"{Guid.NewGuid():N}@example.test", RoleId = adminRoleId, PasswordHash = "unused" };
        database.Roles.Add(role);
        database.Users.Add(admin);
        await database.SaveChangesAsync();
        var actor = new UserResponse(admin.Id, admin.Email, adminRoleId, "Admin", true, false, []);
        var revision = AssignmentRevision.Responsibilities([]);
        async Task<bool> SaveAsync(List<string> permissions)
        {
            await using var attempt = Database();
            try
            {
                await new AccountService(attempt, new NoRoleTestEmail(), TimeProvider.System).SaveRoleResponsibilitiesAsync(actor, role.Id,
                    new() { Responsibilities = permissions, ExpectedRevision = revision }, default);
                return true;
            }
            catch (ApiException exception) when (exception.StatusCode == 409) { return false; }
        }

        var results = await Task.WhenAll(SaveAsync([Permissions.StaffRead]), SaveAsync([Permissions.UsersWrite]));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Single(await database.Responsibilities.Where(permission => permission.RoleId == role.Id && permission.IsEnabled).ToListAsync());
        Assert.Single(await database.AuditEntries.Where(entry => entry.UserId == admin.Id && entry.Action == "Role.Responsibilities").ToListAsync());
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
            Name = name, TextIdentifier = "INITIAL CERTIFICATE", StaffRoleIds = [1, 2, 1],
            StartDateLabel = " From ", ExpiryDateLabel = " To ", DocumentNumberLabel = " ID ",
            ExtractedNameLabel = " Holder ", EmailLabel = " Personal email ", PhoneLabel = " Mobile "
        }, default);
        database.ChangeTracker.Clear();
        var stored = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("INITIAL CERTIFICATE", stored.TextIdentifier);
        Assert.Equal("From", stored.StartDateLabel);
        Assert.Equal("To", stored.ExpiryDateLabel);
        Assert.Equal("ID", stored.DocumentNumberLabel);
        Assert.Equal("Holder", stored.ExtractedNameLabel);
        Assert.Equal("Personal email", stored.EmailLabel);
        Assert.Equal("Mobile", stored.PhoneLabel);
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
            Name = name, TextIdentifier = "UPDATED CERTIFICATE", StaffRoleIds = [2, 3],
            StartDateLabel = "Start", ExpiryDateLabel = "End", DocumentNumberLabel = "Registration ID",
            ExtractedNameLabel = "Participant", EmailLabel = "Contact email", PhoneLabel = "Contact phone"
        }, default);
        database.ChangeTracker.Clear();
        stored = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("UPDATED CERTIFICATE", stored.TextIdentifier);
        Assert.Equal("Start", stored.StartDateLabel);
        Assert.Equal("End", stored.ExpiryDateLabel);
        Assert.Equal("Registration ID", stored.DocumentNumberLabel);
        Assert.Equal("Participant", stored.ExtractedNameLabel);
        Assert.Equal("Contact email", stored.EmailLabel);
        Assert.Equal("Contact phone", stored.PhoneLabel);
        Assert.Equal(new[] { 2, 3 }, stored.StaffRoleIds);
        await service.UpdateDocumentTypeAsync(created.Id, new()
        {
            Name = name, TextIdentifier = "UPDATED CERTIFICATE", StaffRoleIds = []
        }, default);
        database.ChangeTracker.Clear();
        stored = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Empty(stored.StaffRoleIds);
        Assert.Null(stored.StartDateLabel);
        Assert.Null(stored.ExpiryDateLabel);
        Assert.Null(stored.DocumentNumberLabel);
        Assert.Null(stored.ExtractedNameLabel);
        Assert.Null(stored.EmailLabel);
        Assert.Null(stored.PhoneLabel);
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

        await service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [type.Id, type.Id], ExpectedRevision = AssignmentRevision.Documents([]) }, default);
        database.ChangeTracker.Clear();

        var data = await service.GetDocumentTypeManagementAsync(default);
        Assert.Contains(role, data.StaffRoles);
        Assert.Equal(new[] { role.Id, otherRole.Id }.Order(), data.DocumentTypes.Single(candidate => candidate.Id == type.Id).StaffRoleIds);
        Assert.Contains(role, (await service.GetLookupsAsync(default)).StaffRoles);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.CreateStaffRoleAsync(new() { Name = role.Name.ToUpperInvariant() }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [int.MaxValue] }, default))).StatusCode);
        database.ChangeTracker.Clear();
        Assert.True(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == role.Id && requirement.DocumentTypeId == type.Id));

        await service.SaveStaffRoleDocumentsAsync(role.Id, new() { DocumentTypeIds = [], ExpectedRevision = AssignmentRevision.Documents([type.Id]) }, default);
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