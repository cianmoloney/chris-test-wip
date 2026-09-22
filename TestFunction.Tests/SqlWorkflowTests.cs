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
    }
}