using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestFunction.Services;
using TestShared;

namespace TestFunction;

public sealed partial class API(IApiAuthorization authorization, IServiceProvider services, ILogger<API> logger)
{
    [Function(nameof(GetStaff))]
    public Task<IActionResult> GetStaff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetStaffAsync(request.Query["filter"], request.Query["sortBy"], cancellationToken)), cancellationToken);

    [Function(nameof(GetLookups))]
    public Task<IActionResult> GetLookups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lookups")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetLookupsAsync(cancellationToken)), cancellationToken);

    [Function(nameof(CreateStaff))]
    public Task<IActionResult> CreateStaff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "staff")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            var staff = await service.CreateStaffAsync(await ReadAsync<CreateStaffRequest>(request, cancellationToken), cancellationToken);
            return new CreatedResult($"/api/staff/{staff.Id}", staff);
        }, cancellationToken);

    [Function(nameof(GetStaffMember))]
    public Task<IActionResult> GetStaffMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetStaffMemberAsync(id, cancellationToken)), cancellationToken);

    [Function(nameof(UpdateStaff))]
    public Task<IActionResult> UpdateStaff(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "staff/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.UpdateStaffAsync(id, await ReadAsync<UpdateStaffRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetDocuments))]
    public Task<IActionResult> GetDocuments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents")] HttpRequest request,
        CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetDocumentsAsync(request.Query["status"], cancellationToken, request.Query["filter"], request.Query["sortBy"])), cancellationToken);

    [Function(nameof(SetDocumentStatus))]
    public Task<IActionResult> SetDocumentStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "documents/{id:int}/status")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.SetDocumentStatusAsync(id, await ReadAsync<SetDocumentStatusRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(UpdateDocument))]
    public Task<IActionResult> UpdateDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "documents/{id:int}")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.UpdateDocumentAsync(id, await ReadAsync<UpdateDocumentRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(ReassignDocument))]
    public Task<IActionResult> ReassignDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "documents/{id:int}/staff")] HttpRequest request,
        int id, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.ReassignDocumentAsync(id, await ReadAsync<ReassignDocumentRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetStaffDocument))]
    public Task<IActionResult> GetStaffDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff/{staffId:int}/documents/{documentId:int}")] HttpRequest request,
        int staffId, int documentId, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetStaffDocumentAsync(staffId, documentId, cancellationToken)), cancellationToken);

    [Function(nameof(UpdateStaffDocument))]
    public Task<IActionResult> UpdateStaffDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "staff/{staffId:int}/documents/{documentId:int}")] HttpRequest request,
        int staffId, int documentId, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await service.UpdateStaffDocumentAsync(staffId, documentId,
                await ReadAsync<UpdateStaffDocumentRequest>(request, cancellationToken), cancellationToken);
            return new NoContentResult();
        }, cancellationToken);

    [Function(nameof(GetTerms))]
    public Task<IActionResult> GetTerms(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "staff/{staffId:int}/terms")] HttpRequest request,
        int staffId, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
            new OkObjectResult(await service.GetTermsAsync(staffId, request.Query["language"], cancellationToken)), cancellationToken);

    [Function(nameof(AcceptTerms))]
    public Task<IActionResult> AcceptTerms(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "staff/{staffId:int}/terms/acceptances")] HttpRequest request,
        int staffId, CancellationToken cancellationToken) => ExecuteAsync(request, async service =>
        {
            await Task.CompletedTask;
            throw new ApiException(403, "Terms acceptance requires a staff terms link.");
        }, cancellationToken);

    private async Task<IActionResult> ExecuteAsync(HttpRequest request, Func<IStaffDataService, Task<IActionResult>> action,
        CancellationToken cancellationToken, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        try
        {
            var status = await authorization.AuthorizeAsync(request, cancellationToken);
            if (status != 0)
            {
                if (status == 401)
                    request.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return Problem(status, status == 401 ? "A valid application bearer token is required." : "The calling application is not permitted.");
            }
            if (operation is not (nameof(Login) or nameof(VerifyMfa) or nameof(ResolveLink) or nameof(RegisterByLink) or nameof(RegisterMultipleByLink) or nameof(AcceptByLink) or nameof(UploadByLink)))
            {
                var permission = operation switch
                {
                    nameof(CurrentAccount) or nameof(Logout) or nameof(SetMfaPreference) or nameof(GetRoles) or nameof(UpdateRoleResponsibilities) => null,
                    nameof(CreateStaff) or nameof(UpdateStaff) => Permissions.StaffWrite,
                    nameof(UpdateDocument) or nameof(UpdateStaffDocument) or nameof(ReassignDocument) => Permissions.DocumentsWrite,
                    nameof(GetDocumentTypes) or nameof(GetDocumentType) or nameof(CreateDocumentType) or nameof(UpdateDocumentType) => Permissions.DocumentsWrite,
                    nameof(GetStaffRole) or nameof(CreateStaffRole) or nameof(SaveStaffRoleDocuments) => Permissions.DocumentsWrite,
                    nameof(SetDocumentStatus) => Permissions.DocumentsValidate,
                    nameof(GetUsers) or nameof(CreateUser) or nameof(UpdateUser) => Permissions.UsersWrite,
                    nameof(CreateLink) or nameof(RevokeLink) => Permissions.LinksWrite,
                    nameof(PublishTerms) or nameof(GetTermsVersions) or nameof(SaveTermsRole) => Permissions.TermsWrite,
                    nameof(ArchiveStaff) => Permissions.StaffWrite,
                    nameof(ArchiveDocument) => Permissions.DocumentsWrite,
                    _ => Permissions.StaffRead
                };
                var actor = await services.GetRequiredService<AccountService>().RequireAsync(request, permission, cancellationToken);
                if (request.Method is "POST" or "PUT" or "DELETE")
                    services.GetRequiredService<TestFunction.Data.AppDbContext>().AuditEntries.Add(new()
                    { UserId = actor.Id, Action = operation, Subject = request.Path.ToString() });
            }
            return await action(services.GetRequiredService<IStaffDataService>());
        }
        catch (ApiException exception)
        {
            return Problem(exception.StatusCode, exception.Message, exception.Field);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(409, "This record changed. Reload and try again.");
        }
        catch (JsonException)
        {
            return Problem(400, "The request body is not valid JSON.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Problem(409, "A record with these details already exists.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 547 })
        {
            return Problem(400, "A referenced record no longer exists.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Function API request failed: {Method} {Path}", request.Method, request.Path);
            return Problem(500, "The request could not be completed.");
        }
    }

    private static async Task<TRequest> ReadAsync<TRequest>(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
            throw new ApiException(415, "Content-Type must be application/json.");
        var body = await request.ReadFromJsonAsync<TRequest>(ApiJson.Options, cancellationToken)
            ?? throw new ApiException(400, "A request body is required.");
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(body, new ValidationContext(body), errors, validateAllProperties: true))
            throw new ApiException(400, errors[0].ErrorMessage ?? "Invalid request.", errors[0].MemberNames.FirstOrDefault());
        return body;
    }

    private static ObjectResult Problem(int status, string detail, string? field = null)
    {
        ProblemDetails problem = field is null ? new ProblemDetails() : new ValidationProblemDetails(
            new Dictionary<string, string[]> { [field] = [detail] });
        problem.Status = status;
        problem.Title = status switch
        {
            400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
            409 => "Conflict", 415 => "Unsupported Media Type", _ => "Internal Server Error"
        };
        problem.Detail = detail;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}