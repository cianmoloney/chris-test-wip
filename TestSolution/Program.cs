using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using TestSolution.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromHours(1);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// Register BlobServiceClient. Prefers a connection string if configured,
// otherwise falls back to ServiceUri with Managed Identity / DefaultAzureCredential.
builder.Services.AddSingleton(_ =>
{
    var connectionString = builder.Configuration["AzureStorage:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        return new BlobServiceClient(connectionString);
    }

    var accountUri = builder.Configuration["AzureStorage:ServiceUri"]
        ?? throw new InvalidOperationException("Either AzureStorage:ConnectionString or AzureStorage:ServiceUri must be configured.");
    return new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential());
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured."),
        sqlOptions => sqlOptions.EnableRetryOnFailure()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TestSolution.Services.IBlobStorageService, TestSolution.Services.BlobStorageService>();
builder.Services.AddSingleton<TestSolution.Services.IShareLinkService, TestSolution.Services.ShareLinkService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
