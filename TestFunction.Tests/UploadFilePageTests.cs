using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class UploadFilePageTests
{
    private static readonly string[] RequiredPermissions = [Permissions.StaffRead, Permissions.DocumentsWrite, Permissions.LinksWrite];

    [Fact]
    public void PageRequiresAllUploadPermissions()
    {
        var policies = typeof(UploadFileModel).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(attribute => attribute.Policy);
        Assert.Equal(RequiredPermissions.Order(), policies.Order());
    }

    [Theory]
    [InlineData(Permissions.StaffRead)]
    [InlineData(Permissions.DocumentsWrite)]
    [InlineData(Permissions.LinksWrite)]
    public async Task MissingPermissionBlocksGetAndPostWithoutApiCalls(string missing)
    {
        using var handler = new UploadHandler();
        using var client = CreateClient(handler);
        var model = CreateModel(client, RequiredPermissions.Where(permission => permission != missing));

        Assert.IsType<ForbidResult>(await model.OnGetAsync(default));
        Assert.IsType<ForbidResult>(await model.OnPostAsync(default));
        Assert.Empty(handler.Routes);
    }

    [Fact]
    public async Task GetLoadsDocumentTypesWithoutIssuingLink()
    {
        using var handler = new UploadHandler();
        using var client = CreateClient(handler);
        var model = CreateModel(client);

        Assert.IsType<PageResult>(await model.OnGetAsync(default));

        Assert.True(model.TypesLoaded);
        Assert.Equal("Safety", Assert.Single(model.DocumentTypes).Name);
        Assert.Equal(["document-types"], handler.Routes);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(null)]
    public async Task UploadUsesUnboundShortLivedLinkAndSelectedType(int? typeId)
    {
        using var handler = new UploadHandler();
        using var client = CreateClient(handler);
        var model = CreateModel(client);
        model.DocumentTypeId = typeId;
        model.Upload = File("certificate.PDF", 7);

        Assert.IsType<RedirectToPageResult>(await model.OnPostAsync(default));

        Assert.Equal(["document-types", "links", "public/upload"], handler.Routes);
        Assert.Equal(new CreateLinkRequest(ShareLinkPurposes.Upload, null, null, 1), handler.LinkRequest);
        Assert.Equal("test-upload-token", handler.Parts["token"]);
        Assert.Equal("fixture", handler.Parts["file"]);
        Assert.Equal(typeId?.ToString(), handler.Parts.GetValueOrDefault("documentTypeId"));
        Assert.Contains("File uploaded", model.StatusMessage);
    }

    [Theory]
    [InlineData(null, 0, null, "Upload")]
    [InlineData("empty.pdf", 0, null, "Upload")]
    [InlineData("large.pdf", 20971521, null, "Upload")]
    [InlineData("bad.exe", 7, null, "Upload")]
    [InlineData("certificate.pdf", 7, 999, "DocumentTypeId")]
    public async Task InvalidInputNeverIssuesAnUploadLink(string? filename, long length, int? typeId, string field)
    {
        using var handler = new UploadHandler();
        using var client = CreateClient(handler);
        var model = CreateModel(client);
        model.Upload = filename is null ? null : File(filename, length);
        model.DocumentTypeId = typeId;

        Assert.IsType<PageResult>(await model.OnPostAsync(default));

        Assert.NotEmpty(model.ModelState[field]!.Errors);
        Assert.Equal(["document-types"], handler.Routes);
        Assert.Null(model.StatusMessage);
    }

    [Fact]
    public async Task ApiRejectionPreservesSelectedTypeAndShowsError()
    {
        using var handler = new UploadHandler { RejectUpload = true };
        using var client = CreateClient(handler);
        var model = CreateModel(client);
        model.DocumentTypeId = 4;
        model.Upload = File("certificate.pdf", 7);

        Assert.IsType<PageResult>(await model.OnPostAsync(default));

        Assert.Equal(4, model.DocumentTypeId);
        Assert.Single(model.DocumentTypes);
        Assert.Contains(model.ModelState[string.Empty]!.Errors, error => error.ErrorMessage == "Upload rejected.");
        Assert.Null(model.StatusMessage);
    }

    [Fact]
    public async Task UnavailableTypesBlockUploadWithRetryMessage()
    {
        using var handler = new UploadHandler { FailTypes = true };
        using var client = CreateClient(handler);
        var model = CreateModel(client);
        model.Upload = File("certificate.pdf", 7);

        Assert.IsType<PageResult>(await model.OnPostAsync(default));

        Assert.False(model.TypesLoaded);
        Assert.Equal(["document-types"], handler.Routes);
        Assert.Contains(model.ModelState[string.Empty]!.Errors, error => error.ErrorMessage.Contains("Could not load document types"));
    }

    [Fact]
    public async Task MalformedModelBindingCannotIssueLink()
    {
        using var handler = new UploadHandler();
        using var client = CreateClient(handler);
        var model = CreateModel(client);
        model.Upload = File("certificate.pdf", 7);
        model.ModelState.AddModelError("DocumentTypeId", "Invalid number.");

        Assert.IsType<PageResult>(await model.OnPostAsync(default));
        Assert.Equal(["document-types"], handler.Routes);
    }

    private static IFormFile File(string filename, long length) =>
        new FormFile(new MemoryStream("fixture"u8.ToArray()), 0, length, "Upload", filename);

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://api.example.test/") };

    private static UploadFileModel CreateModel(HttpClient client, IEnumerable<string>? permissions = null) =>
        new(new FunctionApiClient(client), NullLogger<UploadFileModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity((permissions ?? RequiredPermissions)
                        .Select(permission => new Claim("permission", permission)), "test"))
                }
            }
        };

    private sealed class UploadHandler : HttpMessageHandler
    {
        public List<string> Routes { get; } = [];
        public Dictionary<string, string> Parts { get; } = [];
        public CreateLinkRequest? LinkRequest { get; private set; }
        public bool RejectUpload { get; init; }
        public bool FailTypes { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath.TrimStart('/');
            Routes.Add(route);
            if (route == "document-types")
            {
                if (FailTypes) throw new HttpRequestException("Offline.");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DocumentTypeManagementResponse([new(4, "Safety", "SAFE", [])], [])) };
            }
            if (route == "links")
            {
                LinkRequest = await request.Content!.ReadFromJsonAsync<CreateLinkRequest>(cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new LinkResponse("test-upload-token", "upload", DateTimeOffset.UtcNow.AddHours(1))) };
            }
            Assert.Equal("public/upload", route);
            foreach (var part in Assert.IsType<MultipartFormDataContent>(request.Content))
                Parts[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync(cancellationToken);
            if (RejectUpload)
                return new(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(new ValidationProblemDetails { Status = 400, Detail = "Upload rejected." },
                        mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json"))
                };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new UploadResponse("uploads", "2026/09/file.pdf")) };
        }
    }
}