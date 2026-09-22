using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace TestFunction.Tests;

public sealed class DocumentTypeTests
{
    [Fact]
    public async Task FieldLabelsRoundTripThroughCreateUpdateAndManagement()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var request = new SaveDocumentTypeRequest { Name = "Training", TextIdentifier = "Training Certificate", StaffRoleIds = [1],
            StartDateLabel = " From ", ExpiryDateLabel = " To ", DocumentNumberLabel = " ID ",
            ExtractedNameLabel = " Holder ", EmailLabel = " Personal email ", PhoneLabel = " Mobile " };

        var created = await service.CreateDocumentTypeAsync(request, default);

        Assert.Equal("From", created.StartDateLabel);
        Assert.Equal("To", created.ExpiryDateLabel);
        Assert.Equal("ID", created.DocumentNumberLabel);
        Assert.Equal("Holder", created.ExtractedNameLabel);
        Assert.Equal("Personal email", created.EmailLabel);
        Assert.Equal("Mobile", created.PhoneLabel);
        database.ChangeTracker.Clear();
        var loaded = (await service.GetDocumentTypeManagementAsync(default)).DocumentTypes.Single(type => type.Id == created.Id);
        Assert.Equal(created with { StaffRoleIds = loaded.StaffRoleIds }, loaded);

        await service.UpdateDocumentTypeAsync(created.Id, request with { StartDateLabel = "Start", ExpiryDateLabel = "End",
            DocumentNumberLabel = "Certificate ID", ExtractedNameLabel = "Participant", EmailLabel = "Contact email", PhoneLabel = "Contact phone" }, default);
        var updated = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("Start", updated.StartDateLabel);
        Assert.Equal("End", updated.ExpiryDateLabel);
        Assert.Equal("Certificate ID", updated.DocumentNumberLabel);
        Assert.Equal("Participant", updated.ExtractedNameLabel);
        Assert.Equal("Contact email", updated.EmailLabel);
        Assert.Equal("Contact phone", updated.PhoneLabel);

        await service.UpdateDocumentTypeAsync(created.Id, request with { StartDateLabel = " ", ExpiryDateLabel = "",
            DocumentNumberLabel = null, ExtractedNameLabel = " ", EmailLabel = "", PhoneLabel = null }, default);
        var cleared = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Null(cleared.StartDateLabel);
        Assert.Null(cleared.ExpiryDateLabel);
        Assert.Null(cleared.DocumentNumberLabel);
        Assert.Null(cleared.ExtractedNameLabel);
        Assert.Null(cleared.EmailLabel);
        Assert.Null(cleared.PhoneLabel);
        Assert.Equal(new[] { 1 }, cleared.StaffRoleIds);
    }

    [Theory]
    [InlineData(nameof(SaveDocumentTypeRequest.StartDateLabel))]
    [InlineData(nameof(SaveDocumentTypeRequest.ExpiryDateLabel))]
    [InlineData(nameof(SaveDocumentTypeRequest.DocumentNumberLabel))]
    [InlineData(nameof(SaveDocumentTypeRequest.ExtractedNameLabel))]
    [InlineData(nameof(SaveDocumentTypeRequest.EmailLabel))]
    [InlineData(nameof(SaveDocumentTypeRequest.PhoneLabel))]
    public async Task OverlongFieldLabelsAreRejectedBeforeMutation(string field)
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var request = new SaveDocumentTypeRequest { Name = "Training", TextIdentifier = "Training Certificate", StaffRoleIds = [1] };
        var created = await service.CreateDocumentTypeAsync(request, default);
        var invalid = request with { Name = "Changed", StaffRoleIds = [2] };
        typeof(SaveDocumentTypeRequest).GetProperty(field)!.SetValue(invalid, new string('x', 129));

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.UpdateDocumentTypeAsync(created.Id, invalid, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.CreateDocumentTypeAsync(invalid, default))).StatusCode);
        var unchanged = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal(created with { StaffRoleIds = unchanged.StaffRoleIds }, unchanged);
    }

    [Fact]
    public async Task NewStaffRoleAppearsInManagementAndRegistrationLookups()
    {
        await using var database = Database();
        var service = new StaffDataService(database);

        var created = await service.CreateStaffRoleAsync(new() { Name = " Brick Layer " }, default);

        Assert.Equal("Brick Layer", created.Name);
        Assert.Equal(created, await service.GetStaffRoleAsync(created.Id, default));
        Assert.Contains(created, (await service.GetDocumentTypeManagementAsync(default)).StaffRoles);
        Assert.Contains(created, (await service.GetLookupsAsync(default)).StaffRoles);
        Assert.False(await database.Roles.AnyAsync(role => role.Name == "Brick Layer"));
    }

    [Theory]
    [InlineData(" driver ", 409)]
    [InlineData(" ", 400)]
    public async Task InvalidStaffRoleNamesDoNotCreateRecords(string name, int status)
    {
        await using var database = Database();
        var count = await database.StaffRoles.CountAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() => new StaffDataService(database).CreateStaffRoleAsync(new() { Name = name }, default));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(count, await database.StaffRoles.CountAsync());
    }

    [Fact]
    public async Task RoleRequirementsReplaceOnlySelectedRoleAndUpdateReadiness()
    {
        await using var database = Database();
        var worker = new Staff { Id = 20, FirstName = "Alex", LastName = "Smith", Email = "alex@example.test", StaffRoleId = 1 };
        database.Staff.Add(worker);
        database.StaffRoleDocumentTypes.AddRange(new() { StaffRoleId = 1, DocumentTypeId = 1 }, new() { StaffRoleId = 2, DocumentTypeId = 1 });
        database.Documents.Add(new() { Id = 20, Name = "certificate", StaffId = 20, DocumentTypeId = 1, IsValid = true, Status = DocumentStatus.Validated });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.True((await eligibility.GetAsync([worker], default))[20].IsReady);

        await service.SaveStaffRoleDocumentsAsync(1, new() { DocumentTypeIds = [2, 2], ExpectedRevision = AssignmentRevision.Documents([1]) }, default);

        Assert.Equal(new[] { 2 }, await database.StaffRoleDocumentTypes.Where(requirement => requirement.StaffRoleId == 1).Select(requirement => requirement.DocumentTypeId).ToArrayAsync());
        Assert.True(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == 2 && requirement.DocumentTypeId == 1));
        Assert.False((await eligibility.GetAsync([worker], default))[20].IsReady);
        Assert.Equal(new[] { "Forklift" }, (await service.GetLookupsAsync(default)).RoleRequiredDocuments[1]);
        Assert.True((await database.Documents.FindAsync(20))!.IsValid);

        await service.SaveStaffRoleDocumentsAsync(1, new() { DocumentTypeIds = [], ExpectedRevision = AssignmentRevision.Documents([2]) }, default);

        Assert.False(await database.StaffRoleDocumentTypes.AnyAsync(requirement => requirement.StaffRoleId == 1));
        Assert.True((await eligibility.GetAsync([worker], default))[20].IsReady);
    }

    [Fact]
    public async Task StaleOrMissingRoleRevisionCannotOverwriteRequirements()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var revision = (await service.GetDocumentTypeManagementAsync(default)).RoleRevisions[1];
        await service.SaveStaffRoleDocumentsAsync(1, new() { DocumentTypeIds = [2], ExpectedRevision = revision }, default);
        foreach (var stale in new[] { revision, "" })
        {
            var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(1,
                new() { DocumentTypeIds = [], ExpectedRevision = stale }, default));
            Assert.Equal(409, error.StatusCode);
        }
        Assert.Equal(new[] { 2 }, await database.StaffRoleDocumentTypes.Where(requirement => requirement.StaffRoleId == 1).Select(requirement => requirement.DocumentTypeId).ToArrayAsync());
    }

    [Fact]
    public async Task UnknownRoleAndDocumentTypesAreRejectedBeforeChangingRequirements()
    {
        await using var database = Database();
        database.StaffRoleDocumentTypes.Add(new() { StaffRoleId = 1, DocumentTypeId = 1 });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(999, new() { DocumentTypeIds = [2] }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(1, new() { DocumentTypeIds = [2, 999] }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => service.SaveStaffRoleDocumentsAsync(1, new() { DocumentTypeIds = null! }, default))).StatusCode);
        Assert.Equal(1, (await database.StaffRoleDocumentTypes.SingleAsync()).DocumentTypeId);
    }

    [Fact]
    public async Task CreatingTypePersistsIdentifierAndDistinctRoleRequirements()
    {
        await using var database = Database();
        var service = new StaffDataService(database);

        var created = await service.CreateDocumentTypeAsync(new()
        {
            Name = " Site induction ", TextIdentifier = " INDUCTION COMPLETE ", StaffRoleIds = [1, 2, 1]
        }, default);

        Assert.Equal("Site induction", created.Name);
        Assert.Equal("INDUCTION COMPLETE", created.TextIdentifier);
        Assert.Equal(new[] { 1, 2 }, created.StaffRoleIds);
        Assert.Equal(2, await database.StaffRoleDocumentTypes.CountAsync(role => role.DocumentTypeId == created.Id));
        var lookups = await service.GetLookupsAsync(default);
        Assert.Contains(lookups.DocumentTypes, type => type.Id == created.Id);
        Assert.Contains("Site induction", lookups.RoleRequiredDocuments[1]);
    }

    [Fact]
    public async Task UpdatingTypeChangesOnlyItsRequirementsAndPreservesDocuments()
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var created = await service.CreateDocumentTypeAsync(new() { Name = "Induction", TextIdentifier = "INDUCTED", StaffRoleIds = [1, 2] }, default);
        database.StaffRoleDocumentTypes.Add(new() { StaffRoleId = 1, DocumentTypeId = 1 });
        database.Documents.Add(new() { Id = 20, Name = "certificate", DocumentTypeId = created.Id, IsValid = true, Status = DocumentStatus.Validated });
        await database.SaveChangesAsync();

        await service.UpdateDocumentTypeAsync(created.Id, new() { Name = "Site induction", TextIdentifier = "SITE INDUCTION", StaffRoleIds = [2, 3] }, default);

        var updated = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("SITE INDUCTION", updated.TextIdentifier);
        Assert.Equal(new[] { 2, 3 }, updated.StaffRoleIds);
        Assert.True(await database.StaffRoleDocumentTypes.AnyAsync(role => role.DocumentTypeId == 1 && role.StaffRoleId == 1));
        Assert.True((await database.Documents.FindAsync(20))!.IsValid);
        Assert.Equal(created.Id, (await database.Documents.FindAsync(20))!.DocumentTypeId);
        await service.UpdateDocumentTypeAsync(created.Id, new() { Name = updated.Name, TextIdentifier = updated.TextIdentifier!, StaffRoleIds = [] }, default);
        Assert.Empty((await service.GetDocumentTypeAsync(created.Id, default)).StaffRoleIds);
    }

    [Theory]
    [InlineData("duplicate", 409)]
    [InlineData("name", 400)]
    [InlineData("identifier", 400)]
    [InlineData("roles", 400)]
    public async Task InvalidChangesDoNotMutateTypesOrRequirements(string scenario, int expectedStatus)
    {
        await using var database = Database();
        var service = new StaffDataService(database);
        var created = await service.CreateDocumentTypeAsync(new() { Name = "Induction", TextIdentifier = "INDUCTED", StaffRoleIds = [1] }, default);
        var request = new SaveDocumentTypeRequest
        {
            Name = scenario == "duplicate" ? " safe pass " : scenario == "name" ? " " : "Changed",
            TextIdentifier = scenario == "identifier" ? " " : "CHANGED",
            StaffRoleIds = scenario == "roles" ? [999] : [2]
        };

        var error = await Assert.ThrowsAsync<ApiException>(() => service.UpdateDocumentTypeAsync(created.Id, request, default));

        Assert.Equal(expectedStatus, error.StatusCode);
        var original = await service.GetDocumentTypeAsync(created.Id, default);
        Assert.Equal("Induction", original.Name);
        Assert.Equal("INDUCTED", original.TextIdentifier);
        Assert.Equal(new[] { 1 }, original.StaffRoleIds);
    }

    [Fact]
    public async Task NewRequirementsImmediatelyAffectStaffReadiness()
    {
        await using var database = Database();
        var worker = new Staff { Id = 20, FirstName = "Alex", LastName = "Smith", Email = "alex@example.test", StaffRoleId = 1 };
        database.Staff.Add(worker);
        await database.SaveChangesAsync();
        var eligibility = new EligibilityService(database, TimeProvider.System);
        Assert.True((await eligibility.GetAsync([worker], default))[20].IsReady);

        await new StaffDataService(database).CreateDocumentTypeAsync(new() { Name = "Induction", TextIdentifier = "INDUCTED", StaffRoleIds = [1] }, default);

        Assert.False((await eligibility.GetAsync([worker], default))[20].IsReady);
    }

    private static AppDbContext Database()
    {
        var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        return database;
    }

    [Theory]
    [InlineData("Issued by SAFEPASS", "Safepass")]
    [InlineData("SAFE\r\n  PASS certificate", "Safe Pass")]
    [InlineData("A+B (Level 1) certificate", "A+B (Level 1)")]
    public void IdentifiesTypeFromLiteralTextIgnoringCaseAndWhitespace(string text, string identifier)
    {
        var result = DocumentTypeMatcher.Match(text, null, null, [new() { Id = 1, Name = "Safety certificate", TextIdentifier = identifier }]);
        Assert.Equal(1, result.TypeId);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void SelectedTypeMismatchIsFlaggedWithoutSilentlyReassigning()
    {
        var result = DocumentTypeMatcher.Match("Forklift certificate", null, 1,
            [new() { Id = 1, TextIdentifier = "Safepass" }, new() { Id = 2, TextIdentifier = "Forklift" }]);
        Assert.Equal(1, result.TypeId);
        Assert.Contains("does not contain", result.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void AmbiguousTextRequiresManualReview(int? selectedType)
    {
        var result = DocumentTypeMatcher.Match("Safepass Forklift", null, selectedType,
            [new() { Id = 1, TextIdentifier = "Safepass" }, new() { Id = 2, TextIdentifier = "Forklift" }]);
        Assert.Equal(selectedType, result.TypeId);
        Assert.Contains("multiple", result.Issue);
    }

    [Fact]
    public void ConfiguredIdentifierCannotBeBypassedByExtractedTypeName()
    {
        var result = DocumentTypeMatcher.Match("Unrelated text", "Safety certificate", null,
            [new() { Id = 1, Name = "Safety certificate", TextIdentifier = "Safepass" }]);
        Assert.Null(result.TypeId);
        Assert.NotNull(result.Issue);
    }

    [Fact]
    public void LegacyTypesRetainNameMatchingAndRequireReview()
    {
        var result = DocumentTypeMatcher.Match("Document Type: Safety certificate", "Safety certificate", null,
            [new() { Id = 1, Name = "Safety certificate" }]);
        Assert.Equal(1, result.TypeId);
        Assert.Contains("name only", result.Issue);
    }
}