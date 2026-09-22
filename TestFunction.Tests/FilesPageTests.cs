using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
using TestShared;
using Xunit;

namespace TestFunction.Tests;

public sealed class FilesPageTests
{
    [Theory]
    [InlineData("uploads", "", "")]
    [InlineData("staff-uploads", "2026", "2026/")]
    [InlineData("uploads", "2026/09/", "2026/09/")]
    [InlineData("uploads", "09", "09/")]
    public async Task ListsConfiguredContainerAndNormalizesFolderPrefix(string containerName, string prefix, string expectedPrefix)
    {
        var storage = new RecordingBlobServiceClient();
        using var http = new HttpClient();
        var model = CreateModel(storage, http, containerName);
        model.Prefix = prefix;
        using var cancellation = new CancellationTokenSource();

        await model.OnGetAsync(cancellation.Token);

        Assert.Null(model.StatusMessage);
        Assert.Equal(containerName, storage.RequestedContainer);
        Assert.Equal(expectedPrefix, storage.Container.RequestedPrefix);
        Assert.Equal(cancellation.Token, storage.Container.CancellationToken);
        Assert.Equal(expectedPrefix, model.Prefix);
        var folder = Assert.Single(model.Folders);
        Assert.Equal("child", folder.Name);
        Assert.Equal(expectedPrefix + "child/", folder.Prefix);
        var file = Assert.Single(model.Files);
        Assert.Equal("document.pdf", file.Name);
        Assert.Equal(expectedPrefix + "document.pdf", file.BlobName);
    }

    [Theory]
    [InlineData("uploads", null, "uploads")]
    [InlineData("staff-uploads", null, "staff-uploads")]
    [InlineData("uploads", "legacy-uploads", "legacy-uploads")]
    public async Task DownloadChecksAccessInCorrectContainerBeforeReadingStorage(
        string configuredContainer, string? explicitContainer, string expectedContainer)
    {
        var storage = new RecordingBlobServiceClient();
        using var handler = new DownloadHandler(HttpStatusCode.Forbidden, "This action is not permitted.");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var model = CreateModel(storage, http, configuredContainer);
        model.Prefix = "2026/09/";
        const string blobName = "2026/09/document.pdf";

        var result = await model.OnGetDownloadAsync(blobName, CancellationToken.None, explicitContainer);

        Assert.Equal($"https://api.example.test/files/access?container={expectedContainer}&blob={Uri.EscapeDataString(blobName)}",
            handler.RequestUri?.AbsoluteUri);
        Assert.Null(storage.RequestedContainer);
        Assert.Equal("2026/09/", Assert.IsType<RedirectToPageResult>(result).RouteValues!["prefix"]);
        Assert.Equal("This action is not permitted.", model.StatusMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "The file no longer exists in storage.")]
    [InlineData(HttpStatusCode.Conflict, "Download blocked: file quarantined.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "File storage could not be accessed.")]
    public async Task DownloadPreservesActionableApiFailureAndCurrentFolder(HttpStatusCode status, string detail)
    {
        var storage = new RecordingBlobServiceClient();
        using var handler = new DownloadHandler(status, detail);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var model = CreateModel(storage, http, "uploads");
        model.Prefix = "2026/09/";

        var result = await model.OnGetDownloadAsync("2026/09/file.pdf", default);

        Assert.Null(storage.RequestedContainer);
        Assert.Equal(detail, model.StatusMessage);
        Assert.Equal(model.Prefix, Assert.IsType<RedirectToPageResult>(result).RouteValues!["prefix"]);
    }

    [Fact]
    public async Task SuccessfulDownloadUsesAuthorizedLocatorAndPreservesFileName()
    {
        var storage = new RecordingBlobServiceClient();
        const string blobName = "2026/09/Safe Pass + #1%.pdf";
        using var handler = new DownloadHandler(HttpStatusCode.OK, access: new("approved-container", blobName));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var model = CreateModel(storage, http, "uploads");

        var result = Assert.IsType<FileStreamResult>(await model.OnGetDownloadAsync(blobName, default));

        Assert.Equal($"https://api.example.test/files/access?container=uploads&blob={Uri.EscapeDataString(blobName)}", handler.RequestUri!.AbsoluteUri);
        Assert.Equal("approved-container", storage.RequestedContainer);
        Assert.Equal(blobName, storage.Container.RequestedBlob);
        Assert.Equal("Safe Pass + #1%.pdf", result.FileDownloadName);
        Assert.Equal("application/octet-stream", result.ContentType);
        using var reader = new StreamReader(result.FileStream);
        Assert.Equal("test file content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task BlobDeletedAfterAuthorizationReturnsToCurrentFolder()
    {
        var storage = new RecordingBlobServiceClient();
        storage.Container.Blob.BlobExists = false;
        using var handler = new DownloadHandler(HttpStatusCode.OK, access: new("uploads", "2026/09/file.pdf"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var model = CreateModel(storage, http, "uploads");
        model.Prefix = "2026/09/";

        var result = await model.OnGetDownloadAsync("2026/09/file.pdf", default);

        Assert.Equal("The file no longer exists in storage.", model.StatusMessage);
        Assert.Equal(model.Prefix, Assert.IsType<RedirectToPageResult>(result).RouteValues!["prefix"]);
    }

    private static FilesModel CreateModel(BlobServiceClient storage, HttpClient http, string containerName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureStorage:ContainerName"] = containerName
        }).Build();
        return new FilesModel(storage, new FunctionApiClient(http), configuration, NullLogger<FilesModel>.Instance);
    }

    private sealed class RecordingBlobServiceClient : BlobServiceClient
    {
        public string? RequestedContainer { get; private set; }
        public RecordingBlobContainerClient Container { get; } = new();

        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            RequestedContainer = blobContainerName;
            return Container;
        }
    }

    private sealed class RecordingBlobContainerClient : BlobContainerClient
    {
        public string? RequestedBlob { get; private set; }
        public DownloadBlobClient Blob { get; } = new();
        public override BlobClient GetBlobClient(string blobName)
        {
            RequestedBlob = blobName;
            return Blob;
        }

        public string? RequestedPrefix { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public override AsyncPageable<BlobHierarchyItem> GetBlobsByHierarchyAsync(
            BlobTraits traits = BlobTraits.None, BlobStates states = BlobStates.None,
            string? delimiter = null, string? prefix = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("/", delimiter);
            RequestedPrefix = prefix;
            CancellationToken = cancellationToken;
            var items = new[]
            {
                BlobsModelFactory.BlobHierarchyItem(prefix + "child/", null),
                BlobsModelFactory.BlobHierarchyItem(null, BlobsModelFactory.BlobItem(
                    name: prefix + "document.pdf", properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, contentLength: 128)))
            };
            return AsyncPageable<BlobHierarchyItem>.FromPages([Page<BlobHierarchyItem>.FromValues(items, null, null!)]);
        }
    }

    private sealed class DownloadBlobClient : BlobClient
    {
        public bool BlobExists { get; set; } = true;
        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobExists, null!));
        public override Task<Stream> OpenReadAsync(long position = 0, int? bufferSize = null,
            BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("test file content")));
    }

    private sealed class DownloadHandler(HttpStatusCode status, string? detail = null, UploadResponse? access = null) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = status == HttpStatusCode.OK ? JsonContent.Create(access) : JsonContent.Create(
                    new ValidationProblemDetails { Status = (int)status, Detail = detail },
                    mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json"))
            });
        }
    }
}