using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Extensions;
using Azure.Core;
using TestFrontend.Services;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

var applicationInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("TestFrontend"))
        .UseAzureMonitor(options => options.ConnectionString = applicationInsightsConnectionString);
    builder.Services.PostConfigure<AspNetCoreTraceInstrumentationOptions>(options =>
        options.EnrichWithHttpResponse = (activity, response) =>
        {
            var request = response.HttpContext.Request;
            var url = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path);
            activity.SetTag("url.query", null);
            activity.SetTag("url.full", url);
            activity.SetTag("http.url", url);
            activity.SetTag("http.target", null);
        });
}

// Add services to the container.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture("en").AddSupportedCultures("en", "pl", "uk").AddSupportedUICultures("en", "pl", "uk");
    options.RequestCultureProviders.Insert(0, new Microsoft.AspNetCore.Localization.QueryStringRequestCultureProvider
        { QueryStringKey = "lang", UIQueryStringKey = "lang" });
});
builder.Services.AddRazorPages(options =>
{
    // Require authentication everywhere except the Login page.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    // These pages authorize themselves via a signed, expiring token
    // instead of the login cookie, so they must allow anonymous access.
    options.Conventions.AllowAnonymousToPage("/Share");
    options.Conventions.AllowAnonymousToPage("/Register");
    options.Conventions.AllowAnonymousToPage("/Terms");
}).AddDataAnnotationsLocalization(options => options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(TestFrontend.WorkerText)));

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/AccessDenied";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
        options.SlidingExpiration = false;
        options.EventsType = typeof(OfficeCookieEvents);
    });
builder.Services.AddAuthorization(options =>
{
    foreach (var permission in TestShared.Permissions.All)
        options.AddPolicy(permission, policy => policy.RequireClaim("permission", permission));
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OfficeCookieEvents>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 21 * 1024 * 1024);

// Register BlobServiceClient. Prefers a connection string if configured,
// otherwise falls back to ServiceUri with Managed Identity / DefaultAzureCredential.
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration["AzureStorage:ConnectionString"];
    if (builder.Environment.IsDevelopment() && connectionString == "UseDevelopmentStorage=true")
    {
        return new BlobServiceClient(connectionString);
    }

    var accountUri = builder.Configuration["AzureStorage:ServiceUri"]
        ?? throw new InvalidOperationException("Either AzureStorage:ConnectionString or AzureStorage:ServiceUri must be configured.");
    var clientId = builder.Configuration["AzureStorage:ManagedIdentityClientId"];
    TokenCredential credential = builder.Environment.IsDevelopment()
        ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId })
        : string.IsNullOrWhiteSpace(clientId) ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    return new BlobServiceClient(new Uri(accountUri), credential);
});

builder.Services.AddSingleton<TokenCredential>(_ =>
{
    var clientId = builder.Configuration["FunctionApi:ManagedIdentityClientId"];
    if (builder.Environment.IsDevelopment())
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
    return string.IsNullOrWhiteSpace(clientId)
        ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
});
builder.Services.AddTransient<FunctionApiAuthenticationHandler>();
builder.Services.AddHttpClient<FunctionApiClient>(client =>
{
    var address = builder.Configuration["FunctionApi:BaseUrl"]
        ?? throw new InvalidOperationException("FunctionApi:BaseUrl must be configured.");
    var uri = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
    if (uri.Scheme != Uri.UriSchemeHttps && !(builder.Environment.IsDevelopment() && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
        throw new InvalidOperationException("FunctionApi:BaseUrl must use HTTPS outside local development.");
    client.BaseAddress = uri;
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler<FunctionApiAuthenticationHandler>();

builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});

app.UseRouting();
app.UseRequestLocalization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().ShortCircuit();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
