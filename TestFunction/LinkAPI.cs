using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TestFunction.Data;
using TestFunction.Services;
using TestShared;

namespace TestFunction;

public sealed partial class API
{
    private LinkService Links => services.GetRequiredService<LinkService>();

    [Function(nameof(RevokeLink))]
    public Task<IActionResult> RevokeLink([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "links/{id:guid}")] HttpRequest request,
        Guid id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            await Links.RevokeAsync(id, cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(PublishTerms))]
    public Task<IActionResult> PublishTerms([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "terms")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, Permissions.TermsWrite, cancellationToken);
            var terms = await services.GetRequiredService<TermsService>().PublishAsync(actor.Id,
                await ReadAsync<PublishTermsRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/terms/{terms.Id}", terms);
        }, cancellationToken);

    [Function(nameof(UploadByLink))]
    public Task<IActionResult> UploadByLink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/upload")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            if (!request.HasFormContentType || request.ContentLength > UploadService.MaximumBytes + 65536)
                throw new ApiException(413, "Upload a file up to 20 MB.");
            var form = await request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions
                { MultipartBodyLengthLimit = UploadService.MaximumBytes + 65536 }, cancellationToken);
            var file = form.Files.GetFile("file") ?? throw new ApiException(400, "Choose a file.");
            int? typeId = int.TryParse(form["documentTypeId"], out var parsed) ? parsed : null;
            return new OkObjectResult(await services.GetRequiredService<UploadService>().UploadAsync(form["token"].ToString(), file, typeId, cancellationToken));
        }, cancellationToken);

    [Function(nameof(CreateLink))]
    public Task<IActionResult> CreateLink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "links")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, Permissions.LinksWrite, cancellationToken);
            return new OkObjectResult(await Links.CreateAsync(actor.Id, await ReadAsync<CreateLinkRequest>(request, cancellationToken), cancellationToken));
        }, cancellationToken);

    [Function(nameof(ListTerms))]
    public Task<IActionResult> ListTerms([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "terms")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await services.GetRequiredService<AppDbContext>().TermsDocuments.AsNoTracking().OrderBy(terms => terms.Title)
                .Select(terms => new TermsDocumentResponse(terms.Id, terms.Title)
                { StaffRoleIds = terms.RequiredByRoles.OrderBy(requirement => requirement.StaffRoleId).Select(requirement => requirement.StaffRoleId).ToList() })
                .ToListAsync(cancellationToken)), cancellationToken);

    [Function(nameof(SaveTermsRole))]
    public Task<IActionResult> SaveTermsRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "terms/{id:int}/staff-roles")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var actor = await Accounts.RequireAsync(request, Permissions.TermsWrite, cancellationToken);
            await services.GetRequiredService<TermsService>().SaveRoleAsync(actor.Id, id,
                await ReadAsync<SaveTermsRoleRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetTermsVersions))]
    public Task<IActionResult> GetTermsVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "terms/{id:int}/versions")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await services.GetRequiredService<TermsService>().GetVersionsAsync(id, cancellationToken)), cancellationToken);

    [Function(nameof(ResolveLink))]
    public Task<IActionResult> ResolveLink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/resolve")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await Links.ResolveAsync(await ReadAsync<ResolveLinkRequest>(request, cancellationToken), cancellationToken)), cancellationToken);

    [Function(nameof(RegisterByLink))]
    public Task<IActionResult> RegisterByLink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/register")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var staff = await Links.RegisterAsync(await ReadAsync<LinkRegistrationRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/staff/{staff.Id}", staff);
        }, cancellationToken);

    [Function(nameof(RegisterMultipleByLink))]
    public Task<IActionResult> RegisterMultipleByLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/register-many")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var result = await Links.RegisterMultipleAsync(await ReadAsync<LinkMultipleRegistrationRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult("/api/staff", result);
        }, cancellationToken);

    [Function(nameof(AcceptByLink))]
    public Task<IActionResult> AcceptByLink([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "public/accept")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
            new OkObjectResult(await Links.AcceptAsync(await ReadAsync<LinkAcceptanceRequest>(request, cancellationToken), cancellationToken)), cancellationToken);
}