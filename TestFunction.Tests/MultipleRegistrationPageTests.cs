using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class MultipleRegistrationPageTests
{
    [Fact]
    public async Task AddAndRemovePreserveDetailsAndDoNotRegisterAnyone()
    {
        using var handler = new RegistrationHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Staff = [Input("first"), Input("second")];
        page.ModelState.AddModelError("Staff[0].Email", "Old error");

        await page.OnPostAddAsync(default);

        Assert.Equal(3, page.Staff.Count);
        Assert.Equal("first@example.test", page.Staff[0].Email);
        Assert.Empty(page.ModelState);
        await page.OnPostRemoveAsync(0, default);
        Assert.Equal(2, page.Staff.Count);
        Assert.Equal("second@example.test", page.Staff[0].Email);
        Assert.Null(handler.Submitted);
        Assert.Equal(ShareLinkPurposes.RegisterMultiple, handler.ResolvedPurpose);
    }

    [Fact]
    public async Task AddIsCappedAndLastEntryCannotBeRemoved()
    {
        using var handler = new RegistrationHandler();
        using var client = Client(handler);
        var page = Page(client);
        await page.OnPostRemoveAsync(0, default);
        Assert.Single(page.Staff);
        page.Staff = Enumerable.Repeat(Input("first"), LinkMultipleRegistrationRequest.MaximumStaff).ToList();

        await page.OnPostAddAsync(default);

        Assert.Equal(LinkMultipleRegistrationRequest.MaximumStaff, page.Staff.Count);
        Assert.Equal("BatchLimit", page.StatusMessage);
        Assert.Null(handler.Submitted);
    }

    [Fact]
    public async Task InvalidFormDoesNotSubmitBatch()
    {
        using var handler = new RegistrationHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Staff = [Input("first")];
        page.ModelState.AddModelError("Staff[0].Email", "Invalid email.");

        Assert.IsType<PageResult>(await page.OnPostAsync(default));

        Assert.Null(handler.Submitted);
        Assert.Single(page.Staff);
    }

    [Fact]
    public async Task IndexedApiErrorPreservesAllEntriesForCorrection()
    {
        using var handler = new RegistrationHandler { SubmitStatus = HttpStatusCode.Conflict };
        using var client = Client(handler);
        var page = Page(client);
        page.Staff = [Input("first"), Input("second")];

        await page.OnPostAsync(default);

        Assert.Equal(2, page.Staff.Count);
        Assert.Equal("second@example.test", page.Staff[1].Email);
        Assert.Contains("Staff[1].Email", page.ModelState.Keys);
        Assert.Equal("BatchNotRegistered", page.StatusMessage);
        Assert.False(page.Completed);
        Assert.True(page.IsValid);
    }

    [Fact]
    public async Task SuccessfulRegistrationSubmitsWholeBatchAndHidesForm()
    {
        using var handler = new RegistrationHandler();
        using var client = Client(handler);
        var page = Page(client);
        page.Staff = [Input("first"), Input("second")];

        await page.OnPostAsync(default);

        Assert.Equal("test-capability", handler.Submitted!.Token);
        Assert.Equal(2, handler.Submitted.Staff.Count);
        Assert.True(page.Completed);
        Assert.Equal(2, page.RegisteredCount);
        Assert.Empty(page.Staff);
    }

    [Fact]
    public async Task InvalidLinkCannotAddEntriesOrSubmit()
    {
        using var handler = new RegistrationHandler { ResolveStatus = HttpStatusCode.Forbidden };
        using var client = Client(handler);
        var page = Page(client);

        await page.OnPostAddAsync(default);
        await page.OnPostAsync(default);

        Assert.False(page.IsValid);
        Assert.Equal("InvalidLink", page.StatusMessage);
        Assert.Single(page.Staff);
        Assert.Null(handler.Submitted);
    }

    [Fact]
    public async Task GenerateMultipleLinkClearsExistingStaffAndUsesNewPage()
    {
        using var handler = new RegistrationHandler();
        using var client = Client(handler);
        var url = new RecordingUrlHelper();
        var page = new GenerateLinkModel(new FunctionApiClient(client))
        {
            LinkType = ShareLinkPurposes.RegisterMultiple, StaffId = 7, TermsDocumentId = 2,
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }, Url = url
        };
        page.Request.Scheme = "https";

        await page.OnPostAsync(default);

        Assert.Equal(ShareLinkPurposes.RegisterMultiple, handler.IssuedRequest!.Purpose);
        Assert.Null(handler.IssuedRequest.StaffId);
        Assert.Null(handler.IssuedRequest.TermsDocumentId);
        Assert.Equal("/RegisterMultiple", url.Values!["page"]);
        Assert.Equal("test-capability", url.Values["token"]);
    }

    private static CreateStaffRequest Input(string name) => new()
    {
        FirstName = name, LastName = "Test", Email = $"{name}@example.test", StaffTypeId = 1, StaffRoleId = 1
    };
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://api.example.test/") };
    private static RegisterMultipleModel Page(HttpClient client) => new(new FunctionApiClient(client), NullLogger<RegisterMultipleModel>.Instance)
    {
        Token = "test-capability", PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
    };

    private sealed class RegistrationHandler : HttpMessageHandler
    {
        public HttpStatusCode ResolveStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode SubmitStatus { get; init; } = HttpStatusCode.Created;
        public LinkMultipleRegistrationRequest? Submitted { get; private set; }
        public CreateLinkRequest? IssuedRequest { get; private set; }
        public string? ResolvedPurpose { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/public/resolve":
                    ResolvedPurpose = (await request.Content!.ReadFromJsonAsync<ResolveLinkRequest>(ApiJson.Options, cancellationToken))!.Purpose;
                    return new(ResolveStatus)
                    {
                        Content = JsonContent.Create(new PublicLinkResponse(ResolvedPurpose, null, new([], [], [], []), null, null), options: ApiJson.Options)
                    };
                case "/public/register-many":
                    Submitted = await request.Content!.ReadFromJsonAsync<LinkMultipleRegistrationRequest>(ApiJson.Options, cancellationToken);
                    if (SubmitStatus == HttpStatusCode.Created)
                        return new(SubmitStatus)
                        {
                            Content = JsonContent.Create(new MultipleRegistrationResponse(Submitted!.Staff.Select((staff, index) => new StaffResponse(
                                index + 1, $"EMP{index + 1}", index + 1, staff.FirstName, staff.LastName, staff.Email, staff.PhoneNumber,
                                staff.StaffTypeId, null, staff.StaffRoleId, null, DateTimeOffset.UtcNow)).ToList()), options: ApiJson.Options)
                        };
                    return new(SubmitStatus)
                    {
                        Content = JsonContent.Create(new ValidationProblemDetails(new Dictionary<string, string[]>
                        {
                            ["Staff[1].Email"] = ["Email already registered."]
                        }) { Status = (int)SubmitStatus, Detail = "Invalid staff details." },
                            mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json"))
                    };
                case "/staff":
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new StaffListResponse([], [])) };
                case "/terms":
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new List<TermsDocumentResponse>()) };
                case "/links":
                    IssuedRequest = await request.Content!.ReadFromJsonAsync<CreateLinkRequest>(ApiJson.Options, cancellationToken);
                    return new(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new LinkResponse("test-capability", IssuedRequest!.Purpose, DateTimeOffset.UtcNow.AddHours(1)))
                    };
                default: throw new InvalidOperationException("Unexpected API request.");
            }
        }
    }

    private sealed class RecordingUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        public RouteValueDictionary? Values { get; private set; }
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext)
        {
            Values = new RouteValueDictionary(routeContext.Values);
            return "https://frontend.example.test/RegisterMultiple?token=test-capability";
        }
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext) => throw new NotSupportedException();
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => throw new NotSupportedException();
    }
}