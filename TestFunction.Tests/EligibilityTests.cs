using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;
using Microsoft.EntityFrameworkCore;

namespace TestFunction.Tests;

public sealed class EligibilityTests
{
    [Fact]
    public async Task MultipleRolesShareOneTermsDocumentAndVersionHistory()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        var terms = new TermsDocument { Title = "Shared safety" };
        database.TermsDocumentVersions.Add(new() { TermsDocument = terms, Content = "One shared edition" });
        database.StaffRoleTermsDocuments.AddRange(
            new StaffRoleTermsDocument { StaffRoleId = 1, TermsDocument = terms },
            new StaffRoleTermsDocument { StaffRoleId = 2, TermsDocument = terms });
        await database.SaveChangesAsync();

        Assert.Single(await database.TermsDocuments.ToListAsync());
        Assert.Single(await database.TermsDocumentVersions.ToListAsync());
        Assert.Equal(2, await database.StaffRoleTermsDocuments.CountAsync());
        Assert.Equal(new[] { "StaffRoleId", "TermsDocumentId" }, database.Model.FindEntityType(typeof(StaffRoleTermsDocument))!
            .FindPrimaryKey()!.Properties.Select(property => property.Name));
        var first = new Staff { Id = 10, StaffRoleId = 1 };
        var second = new Staff { Id = 11, StaffRoleId = 2 };
        database.Staff.AddRange(first, second);
        await database.SaveChangesAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.All((await eligibility.GetAsync([first, second], default)).Values, result => Assert.False(result.IsReady));
        var originalVersion = await database.TermsDocumentVersions.SingleAsync();
        database.StaffTermsAcceptances.AddRange(new StaffTermsAcceptance { StaffId = 10, TermsDocumentVersionId = originalVersion.Id },
            new StaffTermsAcceptance { StaffId = 11, TermsDocumentVersionId = originalVersion.Id });
        await database.SaveChangesAsync();
        Assert.All((await eligibility.GetAsync([first, second], default)).Values, result => Assert.True(result.IsReady));
        database.TermsDocumentVersions.Add(new() { TermsDocumentId = terms.Id, Version = 2, Content = "One shared update" });
        await database.SaveChangesAsync();
        Assert.All((await eligibility.GetAsync([first, second], default)).Values, result => Assert.False(result.IsReady));
        var firstMapping = await database.StaffRoleTermsDocuments.SingleAsync(requirement => requirement.StaffRoleId == 1);
        database.StaffRoleTermsDocuments.Remove(firstMapping);
        await database.SaveChangesAsync();
        var resultAfterRemoval = await eligibility.GetAsync([first, second], default);
        Assert.True(resultAfterRemoval[10].IsReady);
        Assert.False(resultAfterRemoval[11].IsReady);
        Assert.Single(await database.TermsDocuments.ToListAsync());
        Assert.Equal(2, await database.StaffTermsAcceptances.CountAsync());
    }

    [Fact]
    public async Task TermsRoleCanBeAssignedMovedAndClearedWithoutChangingHistory()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        database.TermsDocumentVersions.Add(new() { Id = 10, TermsDocument = new() { Id = 10, Title = "Safety" }, Content = "Immutable" });
        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 10 });
        await database.SaveChangesAsync();
        var service = new TermsService(database);
        var emptyRevision = AssignmentRevision.TermsRole([]);
        await service.SaveRoleAsync(1, 10, new() { StaffRoleIds = [1, 2, 1], ExpectedRevision = emptyRevision }, default);
        Assert.Equal(new[] { 1, 2 }, await database.StaffRoleTermsDocuments.OrderBy(requirement => requirement.StaffRoleId).Select(requirement => requirement.StaffRoleId).ToListAsync());
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleAsync(1, 10,
            new() { StaffRoleIds = [2], ExpectedRevision = emptyRevision }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleAsync(1, 10,
            new() { StaffRoleIds = [1, 999], ExpectedRevision = AssignmentRevision.TermsRole([1, 2]) }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleAsync(1, 10,
            new() { StaffRoleIds = [2] }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveRoleAsync(1, 10,
            new() { StaffRoleIds = null!, ExpectedRevision = AssignmentRevision.TermsRole([1, 2]) }, default))).StatusCode);
        Assert.Equal(2, await database.StaffRoleTermsDocuments.CountAsync());
        await service.SaveRoleAsync(1, 10, new() { StaffRoleIds = [2], ExpectedRevision = AssignmentRevision.TermsRole([1, 2]) }, default);
        Assert.Equal(2, (await database.StaffRoleTermsDocuments.AsNoTracking().SingleAsync()).StaffRoleId);
        await service.SaveRoleAsync(1, 10, new() { StaffRoleIds = [], ExpectedRevision = AssignmentRevision.TermsRole([2]) }, default);
        Assert.Empty(await database.StaffRoleTermsDocuments.ToListAsync());
        Assert.Single(await database.StaffTermsAcceptances.ToListAsync());
        Assert.Equal("Immutable", (await database.TermsDocumentVersions.SingleAsync()).Content);
        Assert.Equal(3, await database.AuditEntries.CountAsync(entry => entry.Action == "Terms.Role"));
    }

    [Fact]
    public async Task RoleRequiresEveryLatestTermsEditionWithoutIndividualAssignments()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await database.Database.EnsureCreatedAsync();
        var worker = new Staff { Id = 10, FirstName = "Test", LastName = "Worker", Email = "worker@example.test", StaffRoleId = 1 };
        var other = new Staff { Id = 11, FirstName = "Other", LastName = "Worker", Email = "other@example.test", StaffRoleId = 2 };
        database.Staff.AddRange(worker, other);
        database.StaffRoleDocumentTypes.Add(new() { StaffRoleId = 1, DocumentTypeId = 1 });
        database.Documents.Add(new() { Id = 10, StaffId = 10, DocumentTypeId = 1, IsValid = true, Status = DocumentStatus.Validated });
        database.TermsDocumentVersions.AddRange(
            new TermsDocumentVersion { Id = 10, TermsDocument = new() { Id = 10, Title = "Safety", RequiredByRoles = [new() { StaffRoleId = 1 }] }, Version = 1, Language = "en", Content = "Safety" },
            new TermsDocumentVersion { Id = 11, TermsDocument = new() { Id = 11, Title = "Conduct", RequiredByRoles = [new() { StaffRoleId = 1 }] }, Version = 1, Language = "pl", Content = "Conduct" });
        await database.SaveChangesAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        var initial = await eligibility.GetAsync([worker, other], default);
        Assert.False(initial[10].IsReady);
        Assert.Equal(2, initial[10].Reasons.Count);
        Assert.True(initial[11].IsReady);
        Assert.Empty(await database.StaffTermsAssignments.ToListAsync());

        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 10 });
        await database.SaveChangesAsync();
        Assert.False((await eligibility.GetAsync([worker], default))[10].IsReady);
        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 11 });
        await database.SaveChangesAsync();
        Assert.True((await eligibility.GetAsync([worker], default))[10].IsReady);

        database.TermsDocumentVersions.Add(new() { Id = 12, TermsDocumentId = 10, Version = 2, Language = "uk", Content = "Updated safety" });
        await database.SaveChangesAsync();
        var outdated = (await eligibility.GetAsync([worker], default))[10];
        Assert.False(outdated.IsReady);
        Assert.Contains("Terms outstanding: Safety, version 2.", outdated.Reasons);
        database.StaffTermsAcceptances.Add(new() { StaffId = 11, TermsDocumentVersionId = 12 });
        await database.SaveChangesAsync();
        Assert.False((await eligibility.GetAsync([worker], default))[10].IsReady);
        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 12 });
        await database.SaveChangesAsync();
        Assert.True((await eligibility.GetAsync([worker], default))[10].IsReady);

        database.TermsDocuments.Add(new() { Id = 12, Title = "Unpublished", RequiredByRoles = [new() { StaffRoleId = 1 }] });
        await database.SaveChangesAsync();
        Assert.False((await eligibility.GetAsync([worker], default))[10].IsReady);
        worker.StaffRoleId = 2;
        await database.SaveChangesAsync();
        Assert.True((await eligibility.GetAsync([worker], default))[10].IsReady);
    }

    [Fact]
    public async Task StaleIndividualAssignmentDoesNotAllowOldAcceptance()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var worker = new Staff { Id = 10, StaffRoleId = 1 };
        database.Staff.Add(worker);
        database.TermsDocumentVersions.Add(new() { Id = 10, TermsDocument = new() { Id = 10, Title = "Safety" }, Version = 1 });
        database.TermsDocumentVersions.Add(new() { Id = 11, TermsDocumentId = 10, Version = 2 });
        database.StaffTermsAssignments.Add(new() { StaffId = 10, TermsDocumentId = 10, Version = 1 });
        database.StaffTermsAcceptances.Add(new() { StaffId = 10, TermsDocumentVersionId = 10 });
        await database.SaveChangesAsync();

        var result = (await new EligibilityService(database, TimeProvider.System).GetAsync([worker], default))[10];

        Assert.False(result.IsReady);
        Assert.Contains("Terms outstanding: Safety, version 2.", result.Reasons);
    }

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