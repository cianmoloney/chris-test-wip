using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TestFrontend.Services;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class ApiBoundaryTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "22222222-2222-2222-2222-222222222222";
    private const string Audience = "api://33333333-3333-3333-3333-333333333333";

    [Theory]
    [InlineData("valid-v1", 0)]
    [InlineData("valid-v2", 0)]
    [InlineData("audience", 401)]
    [InlineData("issuer", 401)]
    [InlineData("tenant", 403)]
    [InlineData("client", 403)]
    [InlineData("delegated", 403)]
    [InlineData("expired", 401)]
    [InlineData("signature", 401)]
    [InlineData("unsigned", 401)]
    [InlineData("malformed", 401)]
    public async Task AuthorizationValidatesTokenAndCallingApplication(string scenario, int expectedStatus)
    {
        using var signingRsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(signingRsa) { KeyId = "test-signing-key" };
        var issuer = $"https://login.microsoftonline.com/{TenantId}/v2.0";
        var metadata = new OpenIdConnectConfiguration { Issuer = issuer };
        metadata.SigningKeys.Add(signingKey);
        var configuration = Configuration(new()
        {
            ["ApiAuth:TenantId"] = TenantId, ["ApiAuth:Audience"] = Audience, ["ApiAuth:AllowedClientId"] = ClientId
        });
        var authorization = new ApiAuthorization(configuration, new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
        var claims = new List<Claim>
        {
            new("tid", scenario == "tenant" ? Guid.NewGuid().ToString() : TenantId),
            new(scenario == "valid-v1" ? "appid" : "azp", scenario == "client" ? Guid.NewGuid().ToString() : ClientId)
        };
        if (scenario == "delegated")
            claims.Add(new("scp", "user.read"));
        var credentials = scenario == "unsigned" ? null : new SigningCredentials(
            scenario == "signature" ? new RsaSecurityKey(otherRsa) { KeyId = signingKey.KeyId } : signingKey,
            SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: scenario == "issuer" ? "https://untrusted.example" : scenario == "valid-v1" ? $"https://sts.windows.net/{TenantId}/" : issuer,
            audience: scenario == "audience" ? "another-api" : Audience,
            claims: claims, notBefore: DateTime.UtcNow.AddHours(-2),
            expires: scenario == "expired" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        var request = Request();
        request.Headers.Authorization = "Bearer " + (scenario == "malformed" ? "not-a-jwt" : new JwtSecurityTokenHandler().WriteToken(token));

        Assert.Equal(expectedStatus, await authorization.AuthorizeAsync(request, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTokenIsDeniedInProductionEvenWithLocalBypassEnabled(bool bypass)
    {
        var authorization = new ApiAuthorization(Configuration(new()
        {
            ["AZURE_FUNCTIONS_ENVIRONMENT"] = "Production",
            ["ApiAuth:AllowUnauthenticatedLocalRequests"] = bypass.ToString()
        }));
        using var services = new ServiceCollection().BuildServiceProvider();
        var api = new API(authorization, services, NullLogger<API>.Instance);
        var request = Request();

        var result = Assert.IsType<ObjectResult>(await api.GetStaff(request, CancellationToken.None));

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("Bearer", request.HttpContext.Response.Headers.WWWAuthenticate.ToString());
        Assert.IsAssignableFrom<ProblemDetails>(result.Value);
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("{}", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    public async Task InvalidRegistrationBodiesReturnProblemDetails(string body, string contentType, int status)
    {
        await using var database = Database();
        using var services = Services(database);
        var api = new API(new AllowRequests(), services, NullLogger<API>.Instance);
        var request = Request(body, contentType);

        var result = Assert.IsType<ObjectResult>(await api.CreateStaff(request, CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.IsAssignableFrom<ProblemDetails>(result.Value);
        Assert.Equal(0, await database.Staff.CountAsync());
    }

    [Fact]
    public async Task StaffQueryPreservesFilteringSortingAndLatestAcceptance()
    {
        await using var database = Database();
        database.Staff.AddRange(
            new Staff { Id = 10, FirstName = "Alex", LastName = "Zulu", Email = "alex@example.test", StaffRoleId = 1 },
            new Staff { Id = 11, FirstName = "Sam", LastName = "Alpha", Email = "sam@example.test", StaffRoleId = 1 },
            new Staff { Id = 12, FirstName = "Jo", LastName = "Beta", Email = "jo@example.test", StaffRoleId = 2 });
        var latest = DateTimeOffset.UtcNow;
        database.StaffTermsAcceptances.AddRange(
            new StaffTermsAcceptance { StaffId = 10, TermsDocumentVersionId = 1, AcceptedAt = latest.AddDays(-1) },
            new StaffTermsAcceptance { StaffId = 10, TermsDocumentVersionId = 1, AcceptedAt = latest });
        await database.SaveChangesAsync();

        var response = await new StaffDataService(database).GetStaffAsync(" Driver ", null, CancellationToken.None);

        Assert.Equal(new[] { 11, 10 }, response.Staff.Select(staff => staff.Id));
        Assert.Equal(latest, response.LastTermsAcceptedAt[10]);
        Assert.Equal("Driver", response.Staff[0].StaffRole?.Name);
    }

    [Theory]
    [InlineData(DocumentStatus.Validated, true)]
    [InlineData(DocumentStatus.Rejected, false)]
    public async Task DocumentStatusKeepsValidityInSync(DocumentStatus status, bool valid)
    {
        await using var database = Database();
        database.Documents.Add(new DocumentEntry { Id = 10, Name = "certificate.pdf", ScanPassed = true, ProcessingCompletedAt = DateTimeOffset.UtcNow });
        await database.SaveChangesAsync();

        await new StaffDataService(database).SetDocumentStatusAsync(10, new(status), CancellationToken.None);

        Assert.Equal(valid, (await database.Documents.FindAsync(10))!.IsValid);
        Assert.Equal(status, (await database.Documents.FindAsync(10))!.Status);
    }

    [Fact]
    public async Task InvalidStatusAndWrongDocumentOwnerAreRejectedWithoutMutation()
    {
        await using var database = Database();
        database.Documents.Add(new DocumentEntry { Id = 10, Name = "certificate.pdf", StaffId = 7 });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        var statusError = await Assert.ThrowsAsync<ApiException>(() => service.SetDocumentStatusAsync(10, new((DocumentStatus)99), CancellationToken.None));
        var readError = await Assert.ThrowsAsync<ApiException>(() => service.GetStaffDocumentAsync(8, 10, CancellationToken.None));
        var writeError = await Assert.ThrowsAsync<ApiException>(() => service.UpdateStaffDocumentAsync(8, 10,
            new() { DocumentNumber = "changed" }, CancellationToken.None));

        Assert.Equal(400, statusError.StatusCode);
        Assert.Equal(404, readError.StatusCode);
        Assert.Equal(404, writeError.StatusCode);
        Assert.Null((await database.Documents.FindAsync(10))!.DocumentNumber);
    }

    [Fact]
    public async Task DocumentUpdatesCannotChangeBlobIdentity()
    {
        await using var database = Database();
        database.Documents.Add(new DocumentEntry { Id = 10, Name = "old", BlobName = "staff/7/original.pdf" });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        await service.UpdateDocumentAsync(10, new() { Name = " new ", Email = " ", DocumentNumber = " 123 " }, CancellationToken.None);

        var document = (await database.Documents.FindAsync(10))!;
        Assert.Equal("new", document.Name);
        Assert.Equal("staff/7/original.pdf", document.BlobName);
        Assert.Equal("123", document.DocumentNumber);
        Assert.Null(document.Email);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(null)]
    public async Task ReassigningDocumentPreservesMetadataAndResetsReview(int? staffId)
    {
        await using var database = Database();
        database.Staff.AddRange(
            new Staff { Id = 7, FirstName = "Alex", LastName = "Smith", Email = "alex@example.test" },
            new Staff { Id = 8, FirstName = "Sam", LastName = "Jones", Email = "sam@example.test" });
        var document = new DocumentEntry
        {
            Id = 10, StaffId = 7, Name = "certificate.pdf", BlobName = "2026/09/original.pdf", ContainerName = "uploads",
            DocumentNumber = "ABC123", DocumentTypeId = 1, Email = "holder@example.test", Phone = "12345",
            ExtractedName = "Alex Smith", StartDate = DateTimeOffset.UtcNow.AddDays(-1), ExpiryDate = DateTimeOffset.UtcNow.AddYears(1),
            ScanPassed = true, ProcessingCompletedAt = DateTimeOffset.UtcNow, Status = DocumentStatus.Validated, IsValid = true
        };
        database.Documents.Add(document);
        await database.SaveChangesAsync();
        var original = database.Entry(document).CurrentValues.Clone();

        await new StaffDataService(database).ReassignDocumentAsync(10, new(staffId), CancellationToken.None);

        Assert.Equal(staffId, document.StaffId);
        Assert.False(document.IsValid);
        Assert.Equal(DocumentStatus.PendingReview, document.Status);
        foreach (var property in original.Properties.Where(property => property.Name is not ("StaffId" or "IsValid" or "Status")))
            Assert.Equal(original[property.Name], database.Entry(document).CurrentValues[property.Name]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReassignmentRejectsMissingOrArchivedStaffWithoutMutation(bool archived)
    {
        await using var database = Database();
        if (archived)
            database.Staff.Add(new Staff { Id = 8, FirstName = "Sam", LastName = "Jones", Email = "sam@example.test", IsArchived = true });
        var document = new DocumentEntry { Id = 10, StaffId = 7, IsValid = true, Status = DocumentStatus.Validated };
        database.Documents.Add(document);
        await database.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ApiException>(() => new StaffDataService(database)
            .ReassignDocumentAsync(10, new(8), CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(7, document.StaffId);
        Assert.True(document.IsValid);
        Assert.Equal(DocumentStatus.Validated, document.Status);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("HR", true)]
    [InlineData("Foreman", false)]
    public async Task ReassignmentRequiresDocumentWritePermissionAndIsAudited(string role, bool allowed)
    {
        await using var database = Database();
        var user = await database.Users.SingleAsync(user => user.Id == 10001);
        user.Role = await database.Roles.SingleAsync(candidate => candidate.Name == role);
        database.Staff.Add(new Staff { Id = 8, FirstName = "Sam", LastName = "Jones", Email = "sam@example.test" });
        database.Documents.Add(new DocumentEntry { Id = 10, StaffId = 7 });
        await database.SaveChangesAsync();
        using var services = Services(database);
        var request = Request("{\"staffId\":8}");
        request.Method = "PUT";
        request.Path = "/api/documents/10/staff";

        var result = await new API(new AllowRequests(), services, NullLogger<API>.Instance)
            .ReassignDocument(request, 10, CancellationToken.None);

        if (allowed)
        {
            Assert.IsType<NoContentResult>(result);
            Assert.Equal(8, (await database.Documents.FindAsync(10))!.StaffId);
            var audit = Assert.Single(await database.AuditEntries.ToListAsync());
            Assert.Equal(nameof(API.ReassignDocument), audit.Action);
            Assert.Equal(request.Path.ToString(), audit.Subject);
        }
        else
        {
            Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(7, (await database.Documents.FindAsync(10))!.StaffId);
            Assert.Empty(await database.AuditEntries.ToListAsync());
        }
    }

    [Theory]
    [InlineData("{}", false, 400)]
    [InlineData("{\"staffId\":0}", false, 400)]
    [InlineData("{\"staffId\":null}", true, 404)]
    public async Task InvalidReassignmentRequestsDoNotMutateDocuments(string body, bool archived, int status)
    {
        await using var database = Database();
        database.Documents.Add(new DocumentEntry { Id = 10, StaffId = 7, IsArchived = archived });
        await database.SaveChangesAsync();
        using var services = Services(database);

        var result = await new API(new AllowRequests(), services, NullLogger<API>.Instance)
            .ReassignDocument(Request(body), 10, CancellationToken.None);

        Assert.Equal(status, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(7, (await database.Documents.IgnoreQueryFilters().SingleAsync()).StaffId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentDownloadRequiresSuccessfulSafetyScan(bool scanPassed)
    {
        await using var database = Database();
        database.Documents.Add(new DocumentEntry
        {
            Id = 10, ContainerName = "uploads", BlobName = "2026/09/certificate.pdf", ScanPassed = scanPassed
        });
        await database.SaveChangesAsync();
        using var services = Services(database);
        var request = Request();
        request.QueryString = new QueryString("?container=uploads&blob=2026%2F09%2Fcertificate.pdf");

        var result = await new API(new AllowRequests(), services, NullLogger<API>.Instance)
            .FileAccess(request, CancellationToken.None);

        if (scanPassed)
        {
            var response = Assert.IsType<UploadResponse>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal("uploads", response.ContainerName);
            Assert.Equal("2026/09/certificate.pdf", response.BlobName);
        }
        else
            Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task TermsFallbackAndAcceptanceUseExactVersionAndServerTimestamp()
    {
        await using var database = Database();
        database.Staff.Add(new Staff { Id = 7, FirstName = "Alex", LastName = "Smith", Email = "alex@example.test" });
        database.TermsDocumentVersions.Add(new TermsDocumentVersion
        {
            Id = 20, Language = "en", Version = 2, Content = "Current terms", IsActive = true,
            TermsDocument = new TermsDocument { Id = 20, Title = "Terms" }
        });
        database.StaffTermsAssignments.Add(new() { StaffId = 7, TermsDocumentId = 20, Version = 2 });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);
        var terms = await service.GetTermsAsync(7, "pl", CancellationToken.None);
        Assert.Equal("en", terms.Terms?.Language);
        Assert.Null(terms.AcceptedAt);

        var rejected = await Assert.ThrowsAsync<ApiException>(() => service.AcceptTermsAsync(7, new(20, false), CancellationToken.None));
        Assert.Equal(400, rejected.StatusCode);
        var accepted = await service.AcceptTermsAsync(7, new(20, true), CancellationToken.None);
        var reloaded = await service.GetTermsAsync(7, "en", CancellationToken.None);

        Assert.Equal(20, accepted.TermsDocumentVersion.Id);
        Assert.Equal(accepted.AcceptedAt, reloaded.AcceptedAt);
        Assert.Single(await database.StaffTermsAcceptances.ToListAsync());
    }

    [Fact]
    public async Task DuplicateRegistrationAndUnknownRoleAreRejected()
    {
        await using var database = Database();
        database.Staff.Add(new Staff { Id = 7, FirstName = "Alex", LastName = "Smith", Email = "alex@example.test" });
        await database.SaveChangesAsync();
        var service = new StaffDataService(database);

        var duplicate = await Assert.ThrowsAsync<ApiException>(() => service.CreateStaffAsync(new()
        {
            FirstName = "Alex", LastName = "Smith", Email = "alex@example.test", StaffTypeId = 1, StaffRoleId = 1
        }, CancellationToken.None));
        var invalidRole = await Assert.ThrowsAsync<ApiException>(() => service.UpdateStaffAsync(7, new()
        {
            FirstName = "Alex", LastName = "Smith", Email = "alex@example.test", StaffRoleId = 999
        }, CancellationToken.None));

        Assert.Equal(409, duplicate.StatusCode);
        Assert.Equal(400, invalidRole.StatusCode);
        Assert.Null((await database.Staff.FindAsync(7))!.StaffRoleId);
    }

    [Fact]
    public void FrontendAssemblyDoesNotReferencePersistence()
    {
        var references = typeof(TestFrontend.Pages.StaffModel).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name!.Contains("EntityFrameworkCore")
            || reference.Name.Contains("SqlClient") || reference.Name == "TestFunction");
        Assert.DoesNotContain(typeof(CreateStaffRequest).GetProperties(), property => property.Name is "Id" or "StaffNumber" or "RegisteredAt");
    }

    [Theory]
    [InlineData("Production", "https://api.example.test/api/staff", true, true)]
    [InlineData("Development", "https://api.example.test/api/staff", true, true)]
    [InlineData("Development", "http://localhost:7024/api/staff", true, false)]
    public async Task FrontendUsesConfiguredScopeAndRestrictsLocalBypass(string environment, string address, bool bypass, bool expectToken)
    {
        var credential = new TestCredential();
        var transport = new RecordingHandler();
        using var client = new HttpClient(new FunctionApiAuthenticationHandler(credential, Configuration(new()
        {
            ["FunctionApi:Scope"] = Audience + "/.default",
            ["FunctionApi:AllowUnauthenticatedLocalRequests"] = bypass.ToString()
        }), new TestEnvironment { EnvironmentName = environment }) { InnerHandler = transport });

        using var response = await client.GetAsync(address);

        Assert.Equal(expectToken ? "Bearer test-token" : null, transport.Authorization);
        Assert.Equal(expectToken ? Audience + "/.default" : null, credential.Scope);
    }

    [Fact]
    public async Task FrontendDoesNotSendTokensOverHttp()
    {
        var credential = new TestCredential();
        using var client = new HttpClient(new FunctionApiAuthenticationHandler(credential,
            Configuration(new()), new TestEnvironment()) { InnerHandler = new RecordingHandler() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://api.example.test/api/staff"));
        Assert.Null(credential.Scope);
    }

    [Fact]
    public async Task FrontendPreservesApiValidationErrors()
    {
        var content = JsonSerializer.Serialize(new ValidationProblemDetails(new Dictionary<string, string[]>
        {
            ["Email"] = ["Already registered."]
        }) { Detail = "Duplicate email.", Status = 409 });
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Conflict, content)) { BaseAddress = new("https://api.example.test/api/") };

        var exception = await Assert.ThrowsAsync<FunctionApiException>(() => new FunctionApiClient(client)
            .PostAsync<CreateStaffRequest, StaffResponse>("staff", new(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("Already registered.", exception.Errors["Email"][0]);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Theory]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(500, false)]
    [InlineData(503, true)]
    public async Task FrontendLogsHandledSignInApiFailures(int statusCode, bool mfa)
    {
        using var client = new HttpClient(new RecordingHandler((HttpStatusCode)statusCode, "<html>private-response-body</html>", "text/html"))
        {
            BaseAddress = new("https://api.example.test/api/")
        };
        var logger = new SignInLogger();
        var page = new TestFrontend.Pages.LoginModel(new FunctionApiClient(client), logger)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = new DefaultHttpContext() },
            Email = "private-email@example.test",
            Password = "private-password",
            ChallengeToken = mfa ? "private-challenge" : null,
            Code = "private-code"
        };

        var result = await page.OnPostAsync();

        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal((HttpStatusCode)statusCode, Assert.IsType<FunctionApiException>(entry.Exception).StatusCode);
        Assert.Contains(mfa ? "accounts/mfa" : "accounts/login", entry.Message);
        Assert.Contains($"HTTP {statusCode}", entry.Message);
        Assert.DoesNotContain("private-", entry.Message + entry.Exception);
        Assert.Equal("The API request failed. Please try again.", page.ErrorMessage);
    }

    private sealed class SignInLogger : ILogger<TestFrontend.Pages.LoginModel>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static HttpRequest Request(string? body = null, string contentType = "application/json")
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["X-User-Session"] = "boundary-test-session";
        request.ContentType = contentType;
        if (body is not null)
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return request;
    }

    private static AppDbContext Database()
    {
        var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        database.Database.EnsureCreated();
        database.UserSessions.Add(new UserSession
        {
            TokenHash = AccountService.HashToken("boundary-test-session"),
            User = new User { Id = 10001, Email = "admin@example.test", RoleId = 2, PasswordHash = "unused-test-value" },
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        database.SaveChanges();
        return database;
    }

    private static ServiceProvider Services(AppDbContext database) => new ServiceCollection()
        .AddSingleton(database)
        .AddSingleton(new AccountService(database, new NoEmail(), TimeProvider.System))
        .AddSingleton<IStaffDataService>(new StaffDataService(database)).BuildServiceProvider();

    private sealed class NoEmail : IEmailService
    {
        public Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AllowRequests : IApiAuthorization
    {
        public Task<int> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class TestCredential : TokenCredential
    {
        public string? Scope { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scope = Assert.Single(requestContext.Scopes);
            return new("test-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string content = "{}", string mediaType = "application/problem+json") : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            });
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}