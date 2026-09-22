using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;
using Microsoft.EntityFrameworkCore;

namespace TestFunction.Tests;

public sealed class EligibilityTests
{
    [Fact]
    public async Task EveryAssignedEditionMustBeAcceptedAndEditsInvalidateReview()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        var worker = new Staff { Id = 10, FirstName = "Test", LastName = "Worker", Email = "worker@example.test", StaffRoleId = 1 };
        database.Staff.Add(worker);
        database.StaffRoleDocumentTypes.Add(new() { StaffRoleId = 1, DocumentTypeId = 1 });
        database.Documents.Add(new() { Id = 10, Name = "certificate", StaffId = 10, DocumentTypeId = 1,
            IsValid = true, ScanPassed = true, Status = DocumentStatus.Validated, ProcessingCompletedAt = DateTimeOffset.UtcNow });
        database.TermsDocumentVersions.Add(new() { Id = 10, TermsDocument = new() { Id = 10, Title = "Safety terms" }, Version = 2, Language = "pl", Content = "Approved text" });
        database.StaffTermsAssignments.Add(new() { StaffId = 10, TermsDocumentId = 10, Version = 2 });
        await database.SaveChangesAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.False((await eligibility.GetAsync([worker], default))[10].IsReady);
        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 10 });
        await database.SaveChangesAsync();
        Assert.True((await eligibility.GetAsync([worker], default))[10].IsReady);
        await new StaffDataService(database).UpdateStaffDocumentAsync(10, 10, new() { DocumentTypeId = 1, DocumentNumber = "changed" }, default);
        Assert.False((await eligibility.GetAsync([worker], default))[10].IsReady);
        Assert.False((await database.Documents.FindAsync(10))!.IsValid);
    }

    [Fact]
    public async Task UnscannedDocumentsCannotBeValidatedOrDownloaded()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        database.Documents.Add(new() { Id = 10, StaffId = 7, Name = "unscanned.pdf" });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(DocumentStatus.Validated), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.GetStaffDocumentAsync(7, 10, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(DocumentStatus.AwaitingScan), default))).StatusCode);
        await service.SetDocumentStatusAsync(10, new(DocumentStatus.Rejected), default);
        Assert.NotNull((await database.Documents.FindAsync(10))!.ProcessingCompletedAt);
    }
    [Theory]
    [InlineData(DocumentStatus.Validated, true, -10, 0, true)]
    [InlineData(DocumentStatus.PendingReview, false, -10, 10, false)]
    [InlineData(DocumentStatus.Validated, true, -10, -1, false)]
    [InlineData(DocumentStatus.Validated, true, 1, 10, false)]
    public void OnlyValidatedCurrentDocumentsQualify(DocumentStatus status, bool valid, int startDays, int endDays, bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var document = new DocumentEntry { Status = status, IsValid = valid, ScanPassed = true,
            StartDate = now.AddDays(startDays), ExpiryDate = new DateTimeOffset(now.UtcDateTime.Date.AddDays(endDays), TimeSpan.Zero) };
        Assert.Equal(expected, EligibilityService.IsCurrent(document, now));
        document.ScanPassed = false;
        Assert.False(EligibilityService.IsCurrent(document, now));
    }
}