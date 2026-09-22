using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TestFunction.Services;

public interface IApiAuthorization
{
    Task<int> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken);
}

public sealed class ApiAuthorization : IApiAuthorization
{
    private readonly IConfiguration _configuration;
    private readonly Lazy<IConfigurationManager<OpenIdConnectConfiguration>> _metadata;

    public ApiAuthorization(IConfiguration configuration, IConfigurationManager<OpenIdConnectConfiguration>? metadata = null)
    {
        _configuration = configuration;
        _metadata = new(() =>
        {
            if (metadata is not null)
                return metadata;
            var tenantId = RequiredGuid("ApiAuth:TenantId");
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever());
        });
    }

    public async Task<int> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (_configuration.GetValue<bool>("ApiAuth:AllowUnauthenticatedLocalRequests")
            && string.Equals(_configuration["AZURE_FUNCTIONS_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME")))
            return 0;

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization.Parameter))
            return StatusCodes.Status401Unauthorized;

        var tenantId = RequiredGuid("ApiAuth:TenantId");
        var allowedClientId = RequiredGuid("ApiAuth:AllowedClientId");
        var audience = _configuration["ApiAuth:Audience"];
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("ApiAuth:Audience must be configured.");

        var metadata = await _metadata.Value.GetConfigurationAsync(cancellationToken);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [metadata.Issuer, $"https://sts.windows.net/{tenantId}/"],
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(authorization.Parameter, parameters, out _);
            var clientId = principal.FindFirst("azp")?.Value ?? principal.FindFirst("appid")?.Value;
            if (!string.Equals(principal.FindFirst("tid")?.Value, tenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(clientId, allowedClientId, StringComparison.OrdinalIgnoreCase)
                || principal.HasClaim(claim => claim.Type == "scp"))
                return StatusCodes.Status403Forbidden;
            return 0;
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            _metadata.Value.RequestRefresh();
            return StatusCodes.Status401Unauthorized;
        }
        catch (SecurityTokenException)
        {
            return StatusCodes.Status401Unauthorized;
        }
        catch (ArgumentException)
        {
            return StatusCodes.Status401Unauthorized;
        }
    }

    private string RequiredGuid(string key) => Guid.TryParse(_configuration[key], out var value)
        ? value.ToString() : throw new InvalidOperationException($"{key} must be a GUID.");
}