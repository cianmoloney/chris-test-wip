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
    [Function(nameof(GetStaffRole))]
    public Task<IActionResult> GetStaffRole([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff-roles/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetStaffRoleAsync(id, cancellationToken)), cancellationToken);

    [Function(nameof(CreateStaffRole))]
    public Task<IActionResult> CreateStaffRole([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "staff-roles")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            var role = await service.CreateStaffRoleAsync(await ReadAsync<CreateStaffRoleRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/staff-roles/{role.Id}", role);
        }, cancellationToken);

    [Function(nameof(SaveStaffRoleDocuments))]
    public Task<IActionResult> SaveStaffRoleDocuments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "staff-roles/{id:int}/document-types")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.SaveStaffRoleDocumentsAsync(id, await ReadAsync<SaveStaffRoleDocumentsRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetDocumentTypes))]
    public Task<IActionResult> GetDocumentTypes([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "document-types")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetDocumentTypeManagementAsync(cancellationToken)), cancellationToken);

    [Function(nameof(GetDocumentType))]
    public Task<IActionResult> GetDocumentType([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "document-types/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetDocumentTypeAsync(id, cancellationToken)), cancellationToken);

    [Function(nameof(CreateDocumentType))]
    public Task<IActionResult> CreateDocumentType([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "document-types")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            var type = await service.CreateDocumentTypeAsync(await ReadAsync<SaveDocumentTypeRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/document-types/{type.Id}", type);
        }, cancellationToken);

    [Function(nameof(UpdateDocumentType))]
    public Task<IActionResult> UpdateDocumentType([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "document-types/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.UpdateDocumentTypeAsync(id, await ReadAsync<SaveDocumentTypeRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(ArchiveStaff))]
    public Task<IActionResult> ArchiveStaff([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "staff/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var staff = await database.Staff.FindAsync([id], cancellationToken) ?? throw new ApiException(404, "Staff not found.");
            staff.IsArchived = true;
            await database.SaveChangesAsync(cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(ArchiveDocument))]
    public Task<IActionResult> ArchiveDocument([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "documents/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var database = services.GetRequiredService<AppDbContext>();
            var document = await database.Documents.FindAsync([id], cancellationToken) ?? throw new ApiException(404, "Document not found.");
            document.IsArchived = true;
            document.IsValid = false;
            await database.SaveChangesAsync(cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(FileAccess))]
    public Task<IActionResult> FileAccess([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "files/access")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async _ =>
        {
            var container = request.Query["container"].ToString();
            var blob = request.Query["blob"].ToString();
            return new OkObjectResult(await services.GetRequiredService<IFileAccessService>()
                .AuthorizeAsync(container, blob, cancellationToken));
        }, cancellationToken);
}