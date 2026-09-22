using Azure;
using Azure.Communication.Email;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace TestFunction.Services;

public interface IEmailService
{
    Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken);
}

public sealed class EmailService(IConfiguration configuration) : IEmailService
{
    public async Task SendAsync(string recipient, string subject, string text, CancellationToken cancellationToken)
    {
        var endpoint = configuration["Email:Endpoint"] ?? throw new InvalidOperationException("Email:Endpoint is required.");
        var sender = configuration["Email:Sender"] ?? throw new InvalidOperationException("Email:Sender is required.");
        var client = new EmailClient(new Uri(endpoint), AzureCredentials.Create(configuration));
        await client.SendAsync(WaitUntil.Completed, new EmailMessage(sender, recipient,
            new EmailContent(subject) { PlainText = text }), cancellationToken);
    }
}

public static class AzureCredentials
{
    public static Azure.Core.TokenCredential Create(IConfiguration configuration)
    {
        var clientId = configuration["Azure:ManagedIdentityClientId"];
        if (configuration["AZURE_FUNCTIONS_ENVIRONMENT"] == "Development")
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
        return string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}