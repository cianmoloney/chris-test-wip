using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class LinkTests
{
    [Fact]
    public async Task MultipleRegistrationLinksAreGenericAndPurposeBound()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        var service = new LinkService(database, new StaffDataService(database), TimeProvider.System);
        var issued = await service.CreateAsync(1, new(ShareLinkPurposes.RegisterMultiple, null, null), default);

        var link = await service.RequireAsync(issued.Token, ShareLinkPurposes.RegisterMultiple, default);
        Assert.Null(link.StaffId);
        var resolved = await service.ResolveAsync(new(issued.Token, ShareLinkPurposes.RegisterMultiple), default);
        Assert.NotNull(resolved.Lookups);
        Assert.Null(resolved.Staff);
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(issued.Token, ShareLinkPurposes.Register, default));
        var assigned = await Assert.ThrowsAsync<ApiException>(() => service.CreateAsync(1, new(ShareLinkPurposes.RegisterMultiple, 7, null), default));
        Assert.Equal(400, assigned.StatusCode);
    }

    [Theory]
    [InlineData("empty", "Staff")]
    [InlineData("too-many", "Staff")]
    [InlineData("invalid", "Staff[1].Email")]
    [InlineData("duplicate", "Staff[1].Email")]
    [InlineData("null", "Staff[1]")]
    public async Task InvalidRegistrationBatchIsRejectedBeforeSaving(string scenario, string field)
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        var service = new LinkService(database, new StaffDataService(database), TimeProvider.System);
        var issued = await service.CreateAsync(1, new(ShareLinkPurposes.RegisterMultiple, null, null), default);
        var first = new CreateStaffRequest { FirstName = "Alex", LastName = "Smith", Email = "alex@example.test", StaffTypeId = 1, StaffRoleId = 1 };
        List<CreateStaffRequest> staff = scenario switch
        {
            "empty" => [],
            "too-many" => Enumerable.Repeat(first, LinkMultipleRegistrationRequest.MaximumStaff + 1).ToList(),
            "invalid" => [first, first with { Email = "invalid" }],
            "duplicate" => [first, first with { Email = "ALEX@example.test" }],
            _ => [first, null!]
        };

        var exception = await Assert.ThrowsAsync<ApiException>(() => service.RegisterMultipleAsync(new(issued.Token, staff), default));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(field, exception.Field);
        Assert.Empty(await database.Staff.ToListAsync());
        Assert.Null((await database.ShareLinks.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task GenericLinksAreUniquePurposeBoundAndSingleUse()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        var service = new LinkService(database, new StaffDataService(database), TimeProvider.System);
        var first = await service.CreateAsync(1, new("upload", null, null), default);
        var second = await service.CreateAsync(1, new("upload", null, null), default);
        Assert.NotEqual(first.Token, second.Token);
        var link = await service.RequireAsync(first.Token, "upload", default);
        Assert.Null(link.StaffId);
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(first.Token, "register", default));
        link.UsedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(first.Token, "upload", default));
        var unused = await service.RequireAsync(second.Token, "upload", default);
        unused.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await database.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(second.Token, "upload", default));
        var revoked = await service.CreateAsync(1, new("upload", null, null), default);
        await service.RevokeAsync(revoked.Id, default);
        await Assert.ThrowsAsync<ApiException>(() => service.RequireAsync(revoked.Token, "upload", default));
    }

    [Theory]
    [InlineData("uploads")]
    [InlineData("staff-uploads")]
    public async Task UploadReservationUsesConfiguredContainer(string containerName)
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        var links = new LinkService(database, new StaffDataService(database), TimeProvider.System);
        var issued = await links.CreateAsync(1, new("upload", null, null), default);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UploadsContainer"] = containerName
        }).Build();
        var uploads = new UploadService(database, links, new UploadStorage(configuration), TimeProvider.System);
        using var content = new MemoryStream([1, 2, 3]);
        var file = new FormFile(content, 0, content.Length, "file", "certificate.pdf");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => uploads.UploadAsync(issued.Token, file, null, default));

        Assert.Equal("AzureStorage:ServiceUri is required.", exception.Message);
        var reservation = await database.ShareLinks.AsNoTracking().SingleAsync();
        Assert.Equal(containerName, reservation.ContainerName);
        Assert.Matches(@"^\d{4}/\d{2}/[a-f0-9]{32}_certificate\.pdf$", reservation.BlobName!);
        Assert.Null(reservation.UsedAt);
    }
}