using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TestFunction.Data;
using TestFunction.Services;
using System.Text.Json.Serialization;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddMvc().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IApiAuthorization, ApiAuthorization>();
builder.Services.AddScoped<IStaffDataService, StaffDataService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LinkService>();
builder.Services.AddScoped<TermsService>();
builder.Services.AddSingleton<UploadStorage>();
builder.Services.AddScoped<UploadService>();
builder.Services.AddSingleton<IEmailService, EmailService>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured."),
        sqlOptions => sqlOptions.EnableRetryOnFailure()));

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

var host = builder.Build();
if (args.Contains("--create-admin"))
{
    using var scope = host.Services.CreateScope();
    await AdminSetup.RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    return;
}
host.Run();
