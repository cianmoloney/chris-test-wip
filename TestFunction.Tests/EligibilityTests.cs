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
            IsValid = true, ScanPassed = false, Status = DocumentStatus.Validated, ProcessingCompletedAt = DateTimeOffset.UtcNow });
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
    public async Task UnprocessedDocumentsCanBeDownloadedButCannotBeValidated()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        database.Documents.Add(new() { Id = 10, StaffId = 7, Name = "unscanned.pdf" });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(DocumentStatus.Validated), default))).StatusCode);
        Assert.False((await service.GetStaffDocumentAsync(7, 10, default)).ScanPassed);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(DocumentStatus.AwaitingScan), default))).StatusCode);
        await service.SetDocumentStatusAsync(10, new(DocumentStatus.Rejected), default);
        Assert.Null((await database.Documents.FindAsync(10))!.ProcessingCompletedAt);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(DocumentStatus.Validated), default))).StatusCode);
    }

    [Theory]
    [InlineData(DocumentStatus.PendingReview)]
    [InlineData(DocumentStatus.ParseFailed)]
    public async Task ProcessedUnscannedDocumentsCanBeManuallyValidated(DocumentStatus status)
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Documents.Add(new() { Id = 10, StaffId = 7, Status = status, ProcessingCompletedAt = DateTimeOffset.UtcNow });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        await service.SetDocumentStatusAsync(10, new(DocumentStatus.Validated), default);

        var document = (await database.Documents.FindAsync(10))!;
        Assert.True(document.IsValid);
        Assert.False(document.ScanPassed);
        var response = await service.GetStaffDocumentAsync(7, 10, default);
        Assert.True(response.CanValidate);
        Assert.Equal(document.ProcessingCompletedAt, response.ProcessingCompletedAt);
        Assert.True(EligibilityService.IsCurrent(document, DateTimeOffset.UtcNow));
        await service.UpdateDocumentAsync(10, new() { Name = "Changed", StaffId = null }, default);
        Assert.False(document.IsValid);
        Assert.Equal(DocumentStatus.PendingReview, document.Status);
    }

    [Fact]
    public async Task QuarantineCannotBeClearedThroughReviewOrMetadataEdits()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Documents.Add(new() { Id = 10, StaffId = 7, Status = DocumentStatus.Unsafe, ScanPassed = true, ProcessingCompletedAt = DateTimeOffset.UtcNow });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        foreach (var status in new[] { DocumentStatus.Validated, DocumentStatus.Rejected })
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new(status), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.GetStaffDocumentAsync(7, 10, default))).StatusCode);
        await service.UpdateDocumentAsync(10, new() { Name = "Changed" }, default);
        Assert.Equal(DocumentStatus.Unsafe, (await database.Documents.FindAsync(10))!.Status);
    }

    [Theory]
    [InlineData("scanning")]
    [InlineData("processing")]
    public async Task ProcessingFilterIncludesLegacyScanQueueWithoutClaimingScan(string filter)
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Documents.AddRange(new DocumentEntry { Id = 10, Status = DocumentStatus.AwaitingScan, Issue = "Awaiting a successful malware scan." },
            new DocumentEntry { Id = 11, Status = DocumentStatus.AwaitingProcessing },
            new DocumentEntry { Id = 12, Status = DocumentStatus.Unsafe });
        await database.SaveChangesAsync();

        var response = await new StaffDataService(database).GetDocumentsAsync(filter, default);

        Assert.Equal(2, response.Documents.Count);
        Assert.All(response.Documents, document =>
        {
            Assert.Equal("Awaiting processing", document.StatusDisplay);
            Assert.False(document.CanValidate);
            Assert.False(document.ScanPassed);
            Assert.DoesNotContain("scan", document.Issue ?? "", StringComparison.OrdinalIgnoreCase);
        });
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
        Assert.Equal(expected, EligibilityService.IsCurrent(document, now));
        document.Status = DocumentStatus.Unsafe;
        Assert.False(EligibilityService.IsCurrent(document, now));
    }
}