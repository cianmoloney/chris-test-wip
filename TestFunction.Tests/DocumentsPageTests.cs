using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class DocumentsPageTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(null)]
    public async Task ReassignmentSendsOnlyStaffAssociationAndPreservesFilters(int? staffId)
    {
        using var handler = new DocumentHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var page = CreatePage(client);
        page.StatusFilter = "pending";
        page.Filter = "certificate";
        page.SortBy = "staff";

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostReassignAsync(10, staffId, CancellationToken.None));

        Assert.Equal("https://api.example.test/documents/10/staff", handler.WriteUri?.AbsoluteUri);
        Assert.Equal(staffId is null ? "{\"staffId\":null}" : "{\"staffId\":8}", handler.WriteBody);
        Assert.Equal("pending", result.RouteValues!["StatusFilter"]);
        Assert.Equal("certificate", result.RouteValues["Filter"]);
        Assert.Equal("staff", result.RouteValues["SortBy"]);
    }

    [Fact]
    public async Task ReassignmentWithoutPermissionDoesNotCallApi()
    {
        using var handler = new DocumentHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var page = CreatePage(client, canWrite: false);

        Assert.IsType<ForbidResult>(await page.OnPostReassignAsync(10, 8, CancellationToken.None));

        Assert.Null(handler.WriteUri);
        Assert.Equal(0, handler.Reads);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ReassignmentShowsApiRejectionAndReloadsChoices(HttpStatusCode status)
    {
        using var handler = new DocumentHandler(status);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var page = CreatePage(client);

        Assert.IsType<PageResult>(await page.OnPostReassignAsync(10, 8, CancellationToken.None));

        Assert.Equal("Staff assignment was rejected.", page.StatusMessage);
        Assert.Equal(1, handler.Reads);
    }

    [Fact]
    public async Task InvalidStaffInputCannotSilentlyUnlinkDocument()
    {
        using var handler = new DocumentHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var page = CreatePage(client);
        page.ModelState.AddModelError("staffId", "Invalid number.");

        Assert.IsType<PageResult>(await page.OnPostReassignAsync(10, null, CancellationToken.None));

        Assert.Null(handler.WriteUri);
        Assert.Equal("Please select a valid staff member.", page.StatusMessage);
        Assert.Equal(1, handler.Reads);
    }

    private static DocumentsModel CreatePage(HttpClient client, bool canWrite = true) => new(
        new FunctionApiClient(client), NullLogger<DocumentsModel>.Instance)
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(canWrite ? [new Claim("permission", Permissions.DocumentsWrite)] : [], "Test"))
            }
        }
    };

    private sealed class DocumentHandler(HttpStatusCode status = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public Uri? WriteUri { get; private set; }
        public string? WriteBody { get; private set; }
        public int Reads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new DocumentListResponse([], [], []), options: ApiJson.Options)
                };
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            WriteUri = request.RequestUri;
            WriteBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new ValidationProblemDetails { Detail = "Staff assignment was rejected.", Status = (int)status },
                    mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json"))
            };
        }
    }
}