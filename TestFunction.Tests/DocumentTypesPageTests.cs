using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class DocumentTypesPageTests
{
    [Fact]
    public void StaffRoleRequestSupportsRazorModelValidation()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddMvc();
        using var services = registrations.BuildServiceProvider();
        var page = new PageContext { HttpContext = new DefaultHttpContext { RequestServices = services } };

        services.GetRequiredService<IObjectModelValidator>().Validate(page, null, "RoleInput", new CreateStaffRoleRequest());

        Assert.Contains("RoleInput.Name", page.ModelState.Keys);
        Assert.False(page.ModelState.IsValid);
    }

    [Fact]
    public void DefaultModeIsOptionalAndFormsHaveExplicitBindingPrefixes()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddMvc();
        using var services = registrations.BuildServiceProvider();
        var metadata = services.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.IModelMetadataProvider>();
        var properties = metadata.GetMetadataForProperties(typeof(DocumentTypesModel)).ToList();
        Assert.False(properties.Single(property => property.PropertyName == nameof(DocumentTypesModel.Mode)).IsRequired);
        foreach (var property in new[] { nameof(DocumentTypesModel.Input), nameof(DocumentTypesModel.RoleInput), nameof(DocumentTypesModel.Requirements) })
            Assert.Equal(property, properties.Single(candidate => candidate.PropertyName == property).BinderModelName);
    }

    [Theory]
    [InlineData("role", "RoleInput.Name")]
    [InlineData("type", "Input.Name")]
    [InlineData("requirements", "Requirements.DocumentTypeIds")]
    public async Task SubmittedFormStillValidatesItsOwnFields(string mode, string expectedError)
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = mode;
        page.RoleId = 1;
        page.Requirements = new() { DocumentTypeIds = null! };

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.Contains(expectedError, page.ModelState.Keys);
        Assert.Null(handler.Method);
    }

    [Fact]
    public async Task RoleSelectionShowsAllTypesAndChecksOnlyItsRequirements()
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);

        await page.OnGetAsync(default);

        Assert.Equal("requirements", page.Mode);
        Assert.Equal(1, page.RoleId);
        Assert.Equal("Driver", page.SelectedRole!.Name);
        Assert.Equal(new[] { 4 }, page.Requirements.DocumentTypeIds);
        page.RoleId = 2;
        await page.OnGetAsync(default);
        Assert.Single(page.Data.DocumentTypes);
        Assert.Empty(page.Requirements.DocumentTypeIds);
        page.RoleId = 999;
        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavingRequirementsSendsOnlySelectedRoleAndExactSelection(bool selected)
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.RoleId = 2;
        page.Requirements = new() { DocumentTypeIds = selected ? [4] : [], ExpectedRevision = AssignmentRevision.Documents([]) };
        page.ModelState.AddModelError("Input.Name", "Unrelated type form is empty.");
        page.ModelState.AddModelError("RoleInput.Name", "Unrelated role form is empty.");

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));

        Assert.Equal("/staff-roles/2/document-types", handler.Path);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal(page.Requirements.DocumentTypeIds, handler.SavedRequirements!.DocumentTypeIds);
        Assert.Equal(2, result.RouteValues!["RoleId"]);
        Assert.Equal("requirements", result.RouteValues["Mode"]);
        Assert.Null(handler.Saved);
    }

    [Fact]
    public async Task AddingStaffRoleReturnsToChecklistWithNewRoleSelected()
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = "role";
        page.RoleInput = new() { Name = "Brick Layer" };
        page.ModelState.AddModelError("Input.Name", "Unrelated required field.");

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));

        Assert.Equal("/staff-roles", handler.Path);
        Assert.Equal("Brick Layer", handler.SavedRole!.Name);
        Assert.Equal(5, result.RouteValues!["RoleId"]);
        Assert.Equal("Staff role added.", page.StatusMessage);
    }

    [Fact]
    public async Task InvalidRequirementSelectionIsRetainedForCorrection()
    {
        using var handler = new ManagementHandler { Conflict = true };
        using var client = Client(handler);
        var page = Page(client);
        page.RoleId = 2;
        page.Requirements = new() { DocumentTypeIds = [4], ExpectedRevision = AssignmentRevision.Documents([]) };

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.Equal(new[] { 4 }, page.Requirements.DocumentTypeIds);
        Assert.Contains("Requirements.DocumentTypeIds", page.ModelState.Keys);
        Assert.Equal(2, page.RoleId);
    }

    [Fact]
    public async Task EmptyCatalogStillAllowsCreatingFirstRoleAndType()
    {
        using var handler = new ManagementHandler { EmptyCatalog = true };
        using var client = Client(handler);
        var page = Page(client);
        Assert.IsType<PageResult>(await page.OnGetAsync(default));
        Assert.Null(page.SelectedRole);
        Assert.Empty(page.Data.DocumentTypes);
        page.Mode = "role";
        page.RoleInput = new() { Name = "First role" };
        Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));
        page = Page(client);
        page.Mode = "type";
        page.Input = new() { Name = "First type", TextIdentifier = "CERTIFICATE" };
        Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));
    }

    [Fact]
    public async Task EditLoadsIdentifierAndSelectedRoles()
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Id = 4;

        Assert.IsType<PageResult>(await page.OnGetAsync(default));

        Assert.Equal("Induction", page.Input.Name);
        Assert.Equal("INDUCTED", page.Input.TextIdentifier);
        Assert.Equal("From", page.Input.StartDateLabel);
        Assert.Equal("To", page.Input.ExpiryDateLabel);
        Assert.Equal("ID", page.Input.DocumentNumberLabel);
        Assert.Equal("Holder", page.Input.ExtractedNameLabel);
        Assert.Equal("Personal email", page.Input.EmailLabel);
        Assert.Equal("Mobile", page.Input.PhoneLabel);
        Assert.Equal(new[] { 1 }, page.Input.StaffRoleIds);
        page.Id = 999;
        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));
    }

    [Theory]
    [InlineData(null, "POST", "/document-types")]
    [InlineData(4, "PUT", "/document-types/4")]
    public async Task TypeEditingPreservesAssignmentsAndNewTypesAreInitiallyUnassigned(int? id, string method, string path)
    {
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = "type";
        page.RoleId = 2;
        page.Id = id;
        page.Input = new() { Name = "Safety", TextIdentifier = "SAFEPASS", StaffRoleIds = [1, 2],
            StartDateLabel = "Start", ExpiryDateLabel = "End", DocumentNumberLabel = "Reference",
            ExtractedNameLabel = "Participant", EmailLabel = "Contact email", PhoneLabel = "Contact phone" };

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));

        Assert.Equal(method, handler.Method?.Method);
        Assert.Equal(path, handler.Path);
        Assert.Equal("SAFEPASS", handler.Saved!.TextIdentifier);
        Assert.Equal("Start", handler.Saved.StartDateLabel);
        Assert.Equal("End", handler.Saved.ExpiryDateLabel);
        Assert.Equal("Reference", handler.Saved.DocumentNumberLabel);
        Assert.Equal("Participant", handler.Saved.ExtractedNameLabel);
        Assert.Equal("Contact email", handler.Saved.EmailLabel);
        Assert.Equal("Contact phone", handler.Saved.PhoneLabel);
        Assert.Equal(id is null ? Array.Empty<int>() : new[] { 1 }, handler.Saved.StaffRoleIds);
        Assert.Null(result.RouteValues!["Id"]);
        Assert.Equal(2, result.RouteValues["RoleId"]);
        Assert.Equal("requirements", result.RouteValues["Mode"]);
        Assert.Equal(id is null ? "Document type added." : "Document type saved.", page.StatusMessage);
    }

    [Fact]
    public async Task RejectedSaveKeepsInputAndShowsFieldError()
    {
        using var handler = new ManagementHandler { Conflict = true };
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = "type";
        page.Input = new() { Name = "Duplicate", TextIdentifier = "TEXT", StaffRoleIds = [2],
            StartDateLabel = "From", ExpiryDateLabel = "To", DocumentNumberLabel = "ID",
            ExtractedNameLabel = "Holder", EmailLabel = "Personal email", PhoneLabel = "Mobile" };
        var draft = page.Input;

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.Equal("Duplicate", page.Input.Name);
        Assert.Equal("TEXT", page.Input.TextIdentifier);
        Assert.Equal(draft with { StaffRoleIds = page.Input.StaffRoleIds }, page.Input);
        Assert.Contains("Input.Name", page.ModelState.Keys);
        Assert.Single(page.Data.DocumentTypes);
    }

    [Fact]
    public async Task WritePermissionIsRequiredAndInvalidFormsDoNotSave()
    {
        var policy = Assert.Single(typeof(DocumentTypesModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(Permissions.DocumentsWrite, policy.Policy);
        using var handler = new ManagementHandler();
        using var client = Client(handler);
        var page = Page(client, canWrite: false);
        Assert.IsType<ForbidResult>(await page.OnPostAsync(default));
        Assert.Null(handler.Saved);
        page = Page(client);
        page.Mode = "type";
        page.ModelState.AddModelError("Input.Name", "Required.");
        Assert.IsType<PageResult>(await page.OnPostAsync(default));
        Assert.Null(handler.Saved);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://api.example.test/") };
    private static DocumentTypesModel Page(HttpClient client, bool canWrite = true) => new(new FunctionApiClient(client), NullLogger<DocumentTypesModel>.Instance)
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(canWrite ? [new Claim("permission", Permissions.DocumentsWrite)] : [], "Test"))
            }
        }
    };

    private sealed class ManagementHandler : HttpMessageHandler
    {
        public bool Conflict { get; init; }
        public bool EmptyCatalog { get; init; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public SaveDocumentTypeRequest? Saved { get; private set; }
        public CreateStaffRoleRequest? SavedRole { get; private set; }
        public SaveStaffRoleDocumentsRequest? SavedRequirements { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(EmptyCatalog ? new DocumentTypeManagementResponse([], []) : new DocumentTypeManagementResponse([new(4, "Induction", "INDUCTED", [1])
                        { StartDateLabel = "From", ExpiryDateLabel = "To", DocumentNumberLabel = "ID", ExtractedNameLabel = "Holder", EmailLabel = "Personal email", PhoneLabel = "Mobile" }],
                        [new(1, "Driver"), new(2, "Carpenter")]), options: ApiJson.Options)
                };
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            if (Path == "/staff-roles")
                SavedRole = await request.Content!.ReadFromJsonAsync<CreateStaffRoleRequest>(ApiJson.Options, cancellationToken);
            else if (Path.StartsWith("/staff-roles/", StringComparison.Ordinal))
                SavedRequirements = await request.Content!.ReadFromJsonAsync<SaveStaffRoleDocumentsRequest>(ApiJson.Options, cancellationToken);
            else
                Saved = await request.Content!.ReadFromJsonAsync<SaveDocumentTypeRequest>(ApiJson.Options, cancellationToken);
            if (Conflict)
                return new(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new ValidationProblemDetails(new Dictionary<string, string[]>
                    {
                        [SavedRequirements is null ? "Name" : "DocumentTypeIds"] = ["The selection is not valid."]
                    }) { Status = 409, Detail = "Duplicate name." }, mediaType: new("application/problem+json"))
                };
            if (SavedRole is not null)
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(new LookupResponse(5, SavedRole.Name)) };
            if (SavedRequirements is not null) return new(HttpStatusCode.NoContent);
            return new(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.NoContent)
            {
                Content = JsonContent.Create(new DocumentTypeResponse(4, Saved!.Name, Saved.TextIdentifier, Saved.StaffRoleIds))
            };
        }
    }
}