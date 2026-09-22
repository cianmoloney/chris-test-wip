using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using TestFunction.Services;
using TestShared;

namespace TestFunction;

public sealed partial class API
{
    private AccountService Accounts => services.GetRequiredService<AccountService>();

    [Function(nameof(GetRoles))]
    public Task<IActionResult> GetRoles([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "roles")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAdminAsync(request, cancellationToken);
            return new OkObjectResult(await Accounts.ListRolesAsync(actor, cancellationToken));
        }, cancellationToken);

    [Function(nameof(UpdateRoleResponsibilities))]
    public Task<IActionResult> UpdateRoleResponsibilities(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "roles/{id:int}/responsibilities")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAdminAsync(request, cancellationToken);
            await Accounts.SaveRoleResponsibilitiesAsync(actor, id,
                await ReadAsync<SaveRoleResponsibilitiesRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(Login))]
    public Task<IActionResult> Login([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accounts/login")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await Accounts.LoginAsync(await ReadAsync<LoginRequest>(request, cancellationToken), cancellationToken)), cancellationToken);

    [Function(nameof(VerifyMfa))]
    public Task<IActionResult> VerifyMfa([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accounts/mfa")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await Accounts.VerifyAsync(await ReadAsync<VerifyMfaRequest>(request, cancellationToken), cancellationToken)), cancellationToken);

    [Function(nameof(CurrentAccount))]
    public Task<IActionResult> CurrentAccount([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "accounts/me")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await Accounts.RequireAsync(request, null, cancellationToken)), cancellationToken);

    [Function(nameof(Logout))]
    public Task<IActionResult> Logout([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "accounts/logout")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            await Accounts.LogoutAsync(request, cancellationToken);
            return new OkObjectResult(new { signedOut = true });
        }, cancellationToken);

    [Function(nameof(SetMfaPreference))]
    public Task<IActionResult> SetMfaPreference([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "accounts/me/mfa")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, null, cancellationToken);
            await Accounts.SetMfaAsync(actor, (await ReadAsync<MfaPreferenceRequest>(request, cancellationToken)).Enabled, cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetUsers))]
    public Task<IActionResult> GetUsers([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ => new OkObjectResult(await Accounts.ListAsync(cancellationToken)), cancellationToken);

    [Function(nameof(CreateUser))]
    public Task<IActionResult> CreateUser([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "users")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, Permissions.UsersWrite, cancellationToken);
            var user = await Accounts.SaveAsync(actor, null, await ReadAsync<SaveUserRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/users/{user.Id}", user);
        }, cancellationToken);

    [Function(nameof(UpdateUser))]
    public Task<IActionResult> UpdateUser([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, Permissions.UsersWrite, cancellationToken);
            await Accounts.SaveAsync(actor, id, await ReadAsync<SaveUserRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);
}