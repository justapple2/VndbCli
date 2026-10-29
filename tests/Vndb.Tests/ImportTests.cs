using System.Net;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class ImportTests
{
    internal const string Novel = """
        {"more":false,"results":[{"id":"v17","title":"Example","titles":[{"lang":"ja","title":"作品名","main":true}],
        "developers":[{"id":"p1","name":"Studio"}],"image":{"id":"cv1","url":"https://t.vndb.org/cv/01/1.jpg"}}]}
        """;
    internal static StubHandler Upstream() => new((call, _) => Task.FromResult(StubHandler.Json(call % 2 == 1 ? Novel :
        """{"more":false,"results":[{"id":"p1","name":"Studio","original":"会社","type":"co"}]}""")));
    internal static HttpResponseMessage Image() => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZ1EAAAAASUVORK5CYII="))
    };

    [Fact]
    public async Task ImportIsIdempotentAndPreservesDownloadedCoverAcrossRefresh()
    {
        using var directory = new TestDirectory();
        using var apiHandler = Upstream();
        using var imageHandler = new StubHandler((_, _) => Task.FromResult(Image()));
        using var apiHttp = new HttpClient(apiHandler);
        using var imageHttp = new HttpClient(imageHandler);
        using var client = new VndbClient(apiHttp, VndbClientTests.FastOptions);
        var store = new SqliteLibraryStore(directory.Path);
        using var importer = new LibraryImporter(client, new(imageHttp, directory.Path), store);
        var first = await importer.ImportAsync("v17", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.Equal("ready", first.CoverStatus);
        Assert.Equal("image/png", first.Cover!.ContentType);
        Assert.True(File.Exists(LibraryRules.CoverPath(directory.Path, first.Cover)));
        Assert.Equal("会社", first.Producers[0].Original);
        await importer.ImportAsync("v17", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, apiHandler.Requests.Count);
        var refreshed = await importer.ImportAsync("v17", refresh: true, TestContext.Current.CancellationToken);
        Assert.Equal(4, apiHandler.Requests.Count);
        Assert.Single(imageHandler.Requests);
        Assert.Equal(first.CreatedAt, refreshed!.CreatedAt);
        Assert.Equal(1, store.Statistics().Novels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenCoverStillPersistsMetadataAndCanBeRetried(bool invalidContent)
    {
        using var directory = new TestDirectory();
        using var apiHandler = Upstream();
        using var imageHandler = new StubHandler((_, _) => Task.FromResult(invalidContent ? StubHandler.Json("<html>not an image</html>") : StubHandler.Json("failed", HttpStatusCode.BadGateway)));
        using var apiHttp = new HttpClient(apiHandler);
        using var imageHttp = new HttpClient(imageHandler);
        using var client = new VndbClient(apiHttp, VndbClientTests.FastOptions);
        var store = new JsonLibraryStore(directory.Path);
        using var importer = new LibraryImporter(client, new(imageHttp, directory.Path), store);
        var result = await importer.ImportAsync("v17", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("failed", result!.CoverStatus);
        Assert.Equal("Example", store.Get("v17")!.Novel.Title);
        Assert.Null(result.Cover);
        await importer.ImportAsync("v17", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, imageHandler.Requests.Count);
    }

    [Fact]
    public async Task CoverAddressCannotAccessNasOrArbitraryHosts()
    {
        using var directory = new TestDirectory();
        using var handler = new StubHandler("{}");
        using var http = new HttpClient(handler);
        var downloader = new CoverDownloader(http, directory.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(new NovelImage { Id = "cv1", Url = "http://192.0.2.10/private" }, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }
}
