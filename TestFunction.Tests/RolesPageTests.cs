using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class RolesPageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CookieValidationRefreshesPermissionsWithoutSigningInAgain(bool grant)
    {
        using var handler = new AccountHandler(grant);
        using var client = Client(handler);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Foreman"), new Claim("session", "existing-session"), new Claim("permission", Permissions.LinksWrite)], "Cookies"));
        var context = new CookieValidatePrincipalContext(new DefaultHttpContext(),
            new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, "Cookies"));

        await new OfficeCookieEvents(new FunctionApiClient(client), NullLogger<OfficeCookieEvents>.Instance).ValidatePrincipal(context);

        Assert.True(context.Principal!.Identity!.IsAuthenticated);
        Assert.Equal("existing-session", context.Principal.FindFirst("session")!.Value);
        Assert.Equal(grant, context.Principal.HasClaim("permission", Permissions.DocumentsWrite));
        Assert.False(context.Principal.HasClaim("permission", Permissions.LinksWrite));
    }

    private sealed class AccountHandler(bool grant) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/accounts/me", request.RequestUri!.AbsolutePath);
            Assert.Equal("existing-session", Assert.Single(request.Headers.GetValues("X-User-Session")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new UserResponse(3, "foreman@example.test", 3, "Foreman", true, false,
                    grant ? [Permissions.StaffRead, Permissions.DocumentsWrite] : []))
            });
        }
    }

    [Theory]
    [InlineData(typeof(StaffModel))]
    [InlineData(typeof(StaffMemberModel))]
    [InlineData(typeof(DocumentsModel))]
    [InlineData(typeof(FilesModel))]
    public void StaffAndFilePagesRequireReadPermission(Type pageType)
    {
        var policy = Assert.Single(pageType.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(Permissions.StaffRead, policy.Policy);
    }

    [Fact]
    public async Task SelectedRoleLoadsCurrentResponsibilities()
    {
        using var handler = new RolesHandler();
        using var client = Client(handler);
        var page = Page(client);

        Assert.IsType<PageResult>(await page.OnGetAsync(default));

        Assert.Equal(3, page.Id);
        Assert.Equal(new[] { Permissions.StaffRead, Permissions.LinksWrite }, page.Input.Responsibilities);
        page.Id = 99;
        Assert.IsType<NotFoundResult>(await page.OnGetAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveSendsExactSelectionIncludingEmpty(bool grant)
    {
        using var handler = new RolesHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Id = 3;
        page.Input = new() { Responsibilities = grant ? [Permissions.StaffRead, Permissions.StaffWrite] : [] };

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync(default));

        Assert.Equal("/roles/3/responsibilities", handler.Path);
        Assert.Equal(page.Input.Responsibilities, handler.Saved!.Responsibilities);
        Assert.Equal(3, result.RouteValues!["Id"]);
        Assert.Equal("Role responsibilities saved.", page.StatusMessage);
    }

    [Theory]
    [InlineData("HR")]
    [InlineData("Foreman")]
    public async Task NonAdminCannotReadOrSaveEvenWithAllPermissions(string role)
    {
        Assert.Equal("Admin", Assert.Single(typeof(RolesModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Roles);
        using var handler = new RolesHandler();
        using var client = Client(handler);
        var page = Page(client, role);
        page.Id = 3;

        Assert.IsType<ForbidResult>(await page.OnGetAsync(default));
        Assert.IsType<ForbidResult>(await page.OnPostAsync(default));

        Assert.Null(handler.Saved);
        Assert.Equal(0, handler.Reads);
    }

    [Fact]
    public async Task RejectedSelectionIsPreservedForCorrection()
    {
        using var handler = new RolesHandler { Reject = true };
        using var client = Client(handler);
        var page = Page(client);
        page.Id = 3;
        page.Input = new() { Responsibilities = [Permissions.LinksWrite] };

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.Equal(new[] { Permissions.LinksWrite }, page.Input.Responsibilities);
        Assert.False(page.ModelState.IsValid);
        Assert.Single(page.Data.Roles);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://api.example.test/") };
    private static RolesModel Page(HttpClient client, string role = "Admin") => new(new FunctionApiClient(client), NullLogger<RolesModel>.Instance)
    {
        PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }
                    .Concat(Permissions.All.Select(permission => new Claim("permission", permission))), "Test"))
            }
        }
    };

    private sealed class RolesHandler : HttpMessageHandler
    {
        public bool Reject { get; init; }
        public int Reads { get; private set; }
        public string? Path { get; private set; }
        public SaveRoleResponsibilitiesRequest? Saved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Reads++;
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new RolesResponse([new(3, "Foreman", [Permissions.StaffRead, Permissions.LinksWrite])], Permissions.All.ToList()))
                };
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            Path = request.RequestUri!.AbsolutePath;
            Saved = await request.Content!.ReadFromJsonAsync<SaveRoleResponsibilitiesRequest>(cancellationToken);
            return new(Reject ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent)
            {
                Content = JsonContent.Create(new ValidationProblemDetails { Detail = "Staff.Read is required.", Status = 400 },
                    mediaType: new("application/problem+json"))
            };
        }
    }
}