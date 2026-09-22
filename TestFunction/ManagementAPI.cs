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
            var document = await services.GetRequiredService<AppDbContext>().Documents.AsNoTracking()
                .SingleOrDefaultAsync(document => document.ContainerName == container && document.BlobName == blob, cancellationToken)
                ?? throw new ApiException(404, "File not found.");
            if (!document.ScanPassed) throw new ApiException(409, "The file is unavailable until safety checks pass.");
            return new OkObjectResult(new UploadResponse(container, blob));
        }, cancellationToken);
}