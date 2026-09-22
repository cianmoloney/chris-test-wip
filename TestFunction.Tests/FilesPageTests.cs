using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TestFrontend.Pages;
using TestFrontend.Services;
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
        using var handler = new DeniedDownloadHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") };
        var model = CreateModel(storage, http, configuredContainer);
        const string blobName = "2026/09/document.pdf";

        var result = await model.OnGetDownloadAsync(blobName, CancellationToken.None, explicitContainer);

        Assert.Equal($"https://api.example.test/files/access?container={expectedContainer}&blob={Uri.EscapeDataString(blobName)}",
            handler.RequestUri?.AbsoluteUri);
        Assert.Null(storage.RequestedContainer);
        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Download failed. Please try again.", model.StatusMessage);
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

    private sealed class DeniedDownloadHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }
    }
}