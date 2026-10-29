using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vndb.Core;
using Xunit;

namespace Vndb.Tests;

public sealed class VndbClientTests
{
    internal static VndbClientOptions FastOptions { get; } = new()
    {
        RequestInterval = TimeSpan.Zero,
        ThrottleBackoff = TimeSpan.Zero,
        TransientBackoff = TimeSpan.Zero
    };
    private const string Empty = """{"results":[],"more":false}""";

    [Fact]
    public async Task SearchPreservesUnicodeAndSendsNestedDeveloperFilter()
    {
        using var handler = new StubHandler(Empty);
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        var result = await client.SearchNovelsAsync("  千恋＊万花 \"test\"  ", 20, 2, "https://vndb.org/p98", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.vndb.org/kana/vn", request.Uri);
        Assert.Equal("application/json", request.ContentType);
        Assert.Null(request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        var filter = root.GetProperty("filters");
        Assert.Equal("and", filter[0].GetString());
        Assert.Equal("千恋＊万花 \"test\"", filter[1][2].GetString());
        Assert.Equal("developer", filter[2][0].GetString());
        Assert.Equal("p98", filter[2][2][2].GetString());
        Assert.Equal("searchrank", root.GetProperty("sort").GetString());
        Assert.Equal(20, root.GetProperty("results").GetInt32());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.False(root.TryGetProperty("count", out _));
        Assert.Contains("developers{id,name,original}", root.GetProperty("fields").GetString());
        Assert.Equal(2, result.Page);
        Assert.Equal(20, result.PageSize);
    }

    [Fact]
    public async Task DetailPreservesAllDevelopersPartialDatesAndLocalizedTitles()
    {
        using var handler = new StubHandler("""
            {"more":false,"results":[{
              "id":"v17","title":"Example","alttitle":null,"olang":"ja","released":"2026-10",
              "description":null,"rating":null,"votecount":0,"length_minutes":null,
              "titles":[{"lang":"ja","title":"作品名","latin":null,"official":true,"main":true},
                        {"lang":"zh-Hans","title":"中文名","latin":null,"official":false,"main":false}],
              "developers":[{"id":"p1","name":"First","original":"会社一","type":"co"},
                            {"id":"p2","name":"Second","original":null,"type":"ng"}],
              "languages":["ja","zh-Hans"],"platforms":["win"],"aliases":["别名"]
            }]}
            """);
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        var novel = await client.GetNovelAsync("v17", TestContext.Current.CancellationToken);
        Assert.NotNull(novel);
        Assert.Equal("2026-10", novel.Released);
        Assert.Null(novel.AltTitle);
        Assert.Null(novel.Rating);
        Assert.Equal(2, novel.Developers.Count);
        Assert.Equal("p2", novel.Developers[1].Id);
        Assert.Equal("中文名", novel.Titles[1].Title);
        Assert.Equal("ja", novel.OriginalLanguage);
        Assert.Equal(0, novel.VoteCount);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Contains("titles{lang,title,latin,official,main}", body.RootElement.GetProperty("fields").GetString());
    }

    [Fact]
    public async Task DeveloperListingUsesDeveloperRoleAndStableIdOrder()
    {
        using var handler = new StubHandler("""{"more":true,"results":[{"id":"v1","title":"One","developers":[]}]}""");
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        var result = await client.GetDeveloperNovelsAsync("p98", 100, 3, TestContext.Current.CancellationToken);
        Assert.True(result.More);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("developer", body.RootElement.GetProperty("filters")[0].GetString());
        Assert.Equal("p98", body.RootElement.GetProperty("filters")[2][2].GetString());
        Assert.Equal("id", body.RootElement.GetProperty("sort").GetString());
    }

    [Fact]
    public async Task ProducerSearchUsesProducerEndpointAndSearchRank()
    {
        using var handler = new StubHandler("""{"more":false,"results":[{"id":"p98","name":"Yuzusoft","original":"ゆずソフト","aliases":["柚子社"],"lang":"ja","type":"co"}]}""");
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        var page = await client.SearchProducersAsync("柚子社", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("柚子社", Assert.Single(page.Results).Aliases[0]);
        Assert.EndsWith("/producer", Assert.Single(handler.Requests).Uri);
        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("searchrank", body.RootElement.GetProperty("sort").GetString());
    }

    [Fact]
    public async Task MissingIdReturnsNull()
    {
        using var handler = new StubHandler(Empty);
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        Assert.Null(await client.GetProducerAsync("p999999", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(101, 1)]
    [InlineData(10, 0)]
    public async Task InvalidPaginationIsRejectedBeforeNetwork(int limit, int page)
    {
        using var handler = new StubHandler(Empty);
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchNovelsAsync("test", limit, page, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AuthTokenIsOnlySentToAuthinfoAndErrorsDoNotEchoIt()
    {
        const string secret = "example-test-token";
        using var handler = new StubHandler((call, _) => Task.FromResult(call == 1
            ? StubHandler.Json(secret, HttpStatusCode.Unauthorized) : StubHandler.Json("{}")));
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        var exception = await Assert.ThrowsAsync<VndbApiException>(() => client.GetAuthInfoAsync(secret, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain(secret, exception.ToString());
        await client.GetStatsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.EndsWith("/authinfo", handler.Requests[0].Uri);
        Assert.Equal("Token " + secret, handler.Requests[0].Authorization);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RetryRecreatesPostRequestAndIsBounded(HttpStatusCode status)
    {
        using var handler = new StubHandler((call, _) => Task.FromResult(call < 3
            ? StubHandler.Json("busy", status) : StubHandler.Json(Empty)));
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await client.SearchNovelsAsync("test", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(handler.Requests[0].Body, r.Body));
    }

    [Fact]
    public async Task PersistentThrottleStopsAfterThreeAttempts()
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("busy", HttpStatusCode.TooManyRequests)));
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await Assert.ThrowsAsync<VndbApiException>(() => client.GetStatsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RetryAfterWaitCanBeCancelledWithoutSendingAnotherRequest()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new StubHandler((_, _) =>
        {
            var response = StubHandler.Json("busy", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            cancel.CancelAfter(TimeSpan.FromMilliseconds(40));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatsAsync(cancel.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RequestTimeoutIsDifferentFromCallerCancellation()
    {
        using var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return StubHandler.Json("{}");
        });
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(40) });
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetStatsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new StubHandler(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return StubHandler.Json("{}");
        });
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatsAsync(cancel.Token));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"results\":null,\"more\":false}")]
    [InlineData("{\"results\":[{\"id\":\"v17\",\"title\":null}],\"more\":false}")]
    public async Task InvalidResponseIsNotTreatedAsAnEmptySearch(string response)
    {
        using var handler = new StubHandler(response);
        using var http = new HttpClient(handler);
        using var client = new VndbClient(http, FastOptions);
        await Assert.ThrowsAsync<JsonException>(() => client.SearchNovelsAsync("test", cancellationToken: TestContext.Current.CancellationToken));
    }
}
