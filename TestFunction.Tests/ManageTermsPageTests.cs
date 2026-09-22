using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class ManageTermsPageTests
{
    [Fact]
    public async Task DefaultViewShowsExistingTermsAndLatestEnglishVersion()
    {
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client);

        Assert.IsType<PageResult>(await page.OnGetAsync(default));

        Assert.False(page.IsEditing);
        Assert.Equal(20, page.DocumentId);
        Assert.Equal(23, page.VersionId);
        Assert.Equal("Current English\n<literal text>", page.SelectedVersion!.Content);
        Assert.Equal("/terms/20/versions", handler.HistoryPath);
        Assert.Null(handler.Published);
    }

    [Theory]
    [InlineData(21, "Original English", 1, "en")]
    [InlineData(24, "Current Polish", 2, "pl")]
    [InlineData(22, "Current Ukrainian", 2, "uk")]
    public async Task SelectedVersionAndLanguageAreDisplayedExactly(int versionId, string content, int version, string language)
    {
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.DocumentId = 20;
        page.VersionId = versionId;

        await page.OnGetAsync(default);

        Assert.Equal(content, page.SelectedVersion!.Content);
        Assert.Equal(version, page.SelectedVersion.Version);
        Assert.Equal(language, page.SelectedVersion.Language);
        Assert.False(page.IsEditing);
    }

    [Theory]
    [InlineData(999, null)]
    [InlineData(20, 31)]
    [InlineData(30, 23)]
    public async Task UnknownDocumentOrCrossDocumentVersionIsRejected(int documentId, int? versionId)
    {
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.DocumentId = documentId;
        page.VersionId = versionId;

        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));

        Assert.Null(handler.Published);
    }

    [Fact]
    public async Task NewVersionStartsFromLatestTranslationsWithoutChangingPublishedText()
    {
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.DocumentId = 20;
        page.VersionId = 21;
        page.Mode = "version";

        await page.OnGetAsync(default);

        Assert.True(page.IsEditing);
        Assert.Equal(20, page.Input.TermsDocumentId);
        Assert.Equal("Site terms", page.Input.Title);
        Assert.Equal("Current English\n<literal text>", page.Input.English);
        Assert.Equal("Current Polish", page.Input.Polish);
        Assert.Equal("Current Ukrainian", page.Input.Ukrainian);
        Assert.Equal("Original English", page.SelectedVersion!.Content);
        Assert.Null(handler.Published);
    }

    [Theory]
    [InlineData("version", 20)]
    [InlineData("document", null)]
    public async Task PublishUsesSelectedDocumentAndRedirectsBackToViewer(string mode, int? expectedDocumentId)
    {
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = mode;
        page.DocumentId = 20;
        page.Input = new() { TermsDocumentId = 999, Title = "New title", English = "New English", Polish = "New Polish", Ukrainian = "New Ukrainian" };

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));

        Assert.Equal(expectedDocumentId, handler.Published!.TermsDocumentId);
        Assert.Equal(mode == "version" ? "Site terms" : "New title", handler.Published.Title);
        Assert.Equal("New English", handler.Published.English);
        Assert.Equal(expectedDocumentId ?? 40, result.RouteValues!["DocumentId"]);
        Assert.False(result.RouteValues.ContainsKey("Mode"));
        Assert.False(result.RouteValues.ContainsKey("VersionId"));
    }

    [Fact]
    public async Task InvalidDraftAndPublishErrorsPreserveAllEnteredText()
    {
        using var handler = new TermsHandler { RejectPublish = true };
        using var client = Client(handler);
        var page = Page(client);
        page.Mode = "version";
        page.DocumentId = 20;
        page.Input = new() { Title = "Site terms", English = "Draft English", Polish = "Draft Polish", Ukrainian = "Draft Ukrainian" };
        page.ModelState.AddModelError("Input.English", "Invalid draft.");
        Assert.IsType<PageResult>(await page.OnPostAsync(default));
        Assert.Null(handler.Published);
        page.ModelState.Clear();

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.False(page.ModelState.IsValid);
        Assert.True(page.IsEditing);
        Assert.Equal("Draft English", page.Input.English);
        Assert.Equal("Draft Polish", page.Input.Polish);
        Assert.Equal("Draft Ukrainian", page.Input.Ukrainian);
        Assert.Equal(20, page.DocumentId);
    }

    [Fact]
    public async Task EmptyCatalogAndNewDocumentModeDoNotRequestVersionHistory()
    {
        using var handler = new TermsHandler { EmptyCatalog = true };
        using var client = Client(handler);
        var page = Page(client);
        Assert.IsType<PageResult>(await page.OnGetAsync(default));
        Assert.Null(page.SelectedDocument);
        Assert.Null(page.SelectedVersion);
        Assert.Null(handler.HistoryPath);
        page.Mode = "document";

        Assert.IsType<PageResult>(await page.OnGetAsync(default));

        Assert.True(page.IsEditing);
        Assert.Equal("", page.Input.Title);
        Assert.Null(handler.HistoryPath);
    }

    [Fact]
    public async Task PublishingRequiresPermissionAndExplicitCreateMode()
    {
        Assert.Equal(Permissions.TermsWrite, Assert.Single(typeof(ManageTermsModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
        using var handler = new TermsHandler();
        using var client = Client(handler);
        var page = Page(client, canPublish: false);
        page.Mode = "document";
        Assert.IsType<ForbidResult>(await page.OnPostAsync(default));
        page = Page(client);
        Assert.IsType<BadRequestResult>(await page.OnPostAsync(default));
        Assert.Null(handler.Published);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://api.example.test/") };
    private static ManageTermsModel Page(HttpClient client, bool canPublish = true) => new(new FunctionApiClient(client))
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(canPublish ? [new Claim("permission", Permissions.TermsWrite)] : [], "Test"))
            }
        }
    };

    private sealed class TermsHandler : HttpMessageHandler
    {
        public bool EmptyCatalog { get; init; }
        public bool RejectPublish { get; init; }
        public string? HistoryPath { get; private set; }
        public PublishTermsRequest? Published { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/terms", request.RequestUri!.AbsolutePath);
                Published = await request.Content!.ReadFromJsonAsync<PublishTermsRequest>(cancellationToken);
                return RejectPublish ? new(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new ValidationProblemDetails { Status = 409, Detail = "This version changed. Try again." },
                        mediaType: new("application/problem+json"))
                } : new(HttpStatusCode.Created) { Content = JsonContent.Create(new TermsDocumentResponse(Published!.TermsDocumentId ?? 40, Published.Title)) };
            }
            if (request.RequestUri!.AbsolutePath == "/terms")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(EmptyCatalog ? new List<TermsDocumentResponse>() : [new(20, "Site terms"), new(30, "Other terms")]) };
            HistoryPath = request.RequestUri.AbsolutePath;
            var now = DateTimeOffset.UtcNow;
            var document = new TermsDocumentResponse(20, "Site terms");
            var versions = HistoryPath == "/terms/20/versions" ? new List<TermsVersionResponse>
            {
                new(23, document, "Current English\n<literal text>", "en", 2, true, now),
                new(24, document, "Current Polish", "pl", 2, true, now),
                new(22, document, "Current Ukrainian", "uk", 2, true, now),
                new(21, document, "Original English", "en", 1, false, now.AddDays(-1))
            } : [new(31, new(30, "Other terms"), "Other document English", "en", 1, true, now)];
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(versions) };
        }
    }
}