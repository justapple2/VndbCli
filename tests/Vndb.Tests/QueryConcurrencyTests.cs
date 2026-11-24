using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class QueryConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path, HttpMessageHandler handler) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            b.UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "test-key").UseSetting("CatalogJobs:Enabled", "false");
            b.ConfigureServices(s => { s.RemoveAll<VndbClient>(); s.AddSingleton(_ => new VndbClient(new HttpClient(handler, false), VndbClientTests.FastOptions)); });
        }
    }
    [Fact]
    public async Task StalledUpstreamDoesNotHoldLocalReadsAndConcurrentMissesShareTheCompletedResult()
    {
        using var dir = new TestDirectory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var handler = new StubHandler(async (_, token) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.WaitAsync(token); return new(HttpStatusCode.OK) { Content = new StringContent("{\"results\":[],\"more\":false}") }; });
        await using var app = new Factory(dir.Path, handler); using var http = app.CreateClient();
        http.DefaultRequestHeaders.Add("X-Library-Key", "test-key"); http.DefaultRequestHeaders.Add("X-Library-Request", "1");
        app.Services.GetRequiredService<SqliteLibraryStore>().Save(StorageTests.Entry());
        var pending = http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("synthetic absent"), VndbJson.Options, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            using var get = await http.GetAsync("/api/v1/vns/v17", Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct); get.EnsureSuccessStatusCode();
            foreach (var mode in new[] { "local_only", "local_first" })
            {
                using var local = await http.PostAsJsonAsync("/api/v1/queries/vns", new MetadataQuery("作品名", Mode: mode), VndbJson.Options, Ct).WaitAsync(TimeSpan.FromSeconds(3), Ct);
                local.EnsureSuccessStatusCode(); Assert.Equal("v17", Assert.Single((await local.Content.ReadFromJsonAsync<MetadataResponse>(VndbJson.Options, Ct))!.Items).VndbId);
            }
            var queries = app.Services.GetRequiredService<CatalogQueries>();
            using var cancel = new CancellationTokenSource();
            var cancelled = queries.QueryAsync("vn_search", new("other absent"), null, cancel.Token); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            var duplicate = queries.QueryAsync("vn_search", new("synthetic absent"), null, Ct);
            Assert.False(duplicate.IsCompleted); Assert.Equal(1, calls);
            release.SetResult(); using var response = await pending; response.EnsureSuccessStatusCode();
            Assert.Empty((await duplicate).Items); Assert.Equal(1, calls);
            Assert.Empty((await queries.QueryAsync("vn_search", new("synthetic absent"), null, Ct)).Items); Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
    }
}
