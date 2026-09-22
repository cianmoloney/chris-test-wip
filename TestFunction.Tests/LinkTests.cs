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