using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.AspNetCore.Mvc;
using TestShared;

namespace TestFrontend.Services;

public sealed class FunctionApiException(HttpStatusCode statusCode, ValidationProblemDetails problem)
    : Exception(problem.Detail ?? "The API request failed.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public IDictionary<string, string[]> Errors { get; } = problem.Errors;
}

public sealed class FunctionApiClient(HttpClient client, IHttpContextAccessor? context = null)
{
    public string? SessionToken { get; set; }
    private void SetSession()
    {
        client.DefaultRequestHeaders.Remove("X-User-Session");
        var session = SessionToken ?? context?.HttpContext?.User.FindFirst("session")?.Value;
        if (!string.IsNullOrWhiteSpace(session)) client.DefaultRequestHeaders.Add("X-User-Session", session);
    }

    public async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        SetSession();
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(ApiJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("The Function API returned an empty response.");
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        SetSession();
        using var response = await client.PostAsJsonAsync(path, request, ApiJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(ApiJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("The Function API returned an empty response.");
    }

    public async Task PutAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        SetSession();
        using var response = await client.PutAsJsonAsync(path, request, ApiJson.Options, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiJson.Options, cancellationToken)
            : null;
        throw new FunctionApiException(response.StatusCode, problem ?? new ValidationProblemDetails
        {
            Status = (int)response.StatusCode,
            Detail = "The API request failed. Please try again."
        });
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        SetSession();
        using var response = await client.DeleteAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<UploadResponse> UploadAsync(string token, IFormFile file, int? documentTypeId, CancellationToken cancellationToken)
    {
        SetSession();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "token");
        if (documentTypeId is not null) form.Add(new StringContent(documentTypeId.Value.ToString()), "documentTypeId");
        form.Add(new StreamContent(file.OpenReadStream()), "file", Path.GetFileName(file.FileName));
        using var response = await client.PostAsync("public/upload", form, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(ApiJson.Options, cancellationToken))!;
    }
}

public sealed class FunctionApiAuthenticationHandler(
    TokenCredential credential, IConfiguration configuration, IHostEnvironment environment) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var local = environment.IsDevelopment() && request.RequestUri is { IsLoopback: true }
            && configuration.GetValue<bool>("FunctionApi:AllowUnauthenticatedLocalRequests");
        if (!local)
        {
            if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Authenticated Function API calls require HTTPS.");
            var scope = configuration["FunctionApi:Scope"];
            if (string.IsNullOrWhiteSpace(scope))
                throw new InvalidOperationException("FunctionApi:Scope must be configured.");
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}