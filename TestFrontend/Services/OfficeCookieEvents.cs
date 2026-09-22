using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using TestShared;

namespace TestFrontend.Services;

public sealed class OfficeCookieEvents(FunctionApiClient api, ILogger<OfficeCookieEvents> logger) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var token = context.Principal?.FindFirst("session")?.Value;
        if (string.IsNullOrWhiteSpace(token)) { context.RejectPrincipal(); return; }
        try
        {
            api.SessionToken = token;
            var user = await api.GetAsync<UserResponse>("accounts/me", context.HttpContext.RequestAborted);
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Email),
                new(ClaimTypes.Role, user.Role), new("session", token)
            };
            claims.AddRange(user.Permissions.Select(permission => new Claim("permission", permission)));
            context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name)));
        }
        catch (Exception exception) when (exception is FunctionApiException or HttpRequestException)
        {
            logger.LogWarning("Session validation failed; refusing access.");
            context.RejectPrincipal();
        }
    }
}