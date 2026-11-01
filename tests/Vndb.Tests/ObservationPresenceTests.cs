using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ObservationPresenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", root).UseSetting("Library:WriteKey", "observation-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Gate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool DropReply { get; set; }
        public List<string> Bodies { get; } = [];
        public List<string> Reads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var presence = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/presence", StringComparison.Ordinal);
            if (request.Method == HttpMethod.Get) Reads.Add(request.RequestUri!.AbsolutePath);
            if (presence) Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            var reply = await base.SendAsync(request, ct);
            if (presence && DropReply) { DropReply = false; reply.Dispose(); throw new HttpRequestException("Synthetic lost reply"); }
            return reply;
        }
    }
    [Fact]
    public async Task ObservedOwnershipCoalescesCopiesKeepsUnknownAndReplaysExactOfflineBatchWithoutPaths()
    {
        using var f = new ObservationTests.Fixture(); using var db = new TestDirectory(); await f.InitializeAsync(); var package = await f.AddPackageAsync();
        await using var app = new Factory(db.Path); using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = new("http://localhost/") };
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry()); var item = store.SaveItem(new(new("Synthetic", "v17")), null, "create-observed");
        await f.Store.UpdateAsync(v => v with { Items = [store.ReadItem(item.Id)!], Installations = v.Installations.Select(i => i with { Components = i.Components.Select(c => c with { Details = c.Details with { ItemId = item.Id } }).ToArray() }).ToArray(), Packages = v.Packages!.Select(p => p with { ItemId = item.Id }).ToArray() }, Ct);
        var api = new WebLibraryClient(http, http.BaseAddress!, "observation-test-key", TimeSpan.FromSeconds(5)); var info = await api.OrganizationInfoAsync(Ct);
        var other = Guid.NewGuid().ToString("D"); store.SavePresence(new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, other, 0, [new(item.Id, true)]), info.ServiceInstanceId, "other-device");
        var sync = new PresenceSync(f.Store); await f.CheckAsync(); Assert.Equal(1, await sync.SubmitAsync(api, http.BaseAddress!.AbsoluteUri, f.Device, Ct));
        f.Files.RemoveTree(f.Game); await f.CheckAsync(); f.Clock.Advance(11); await f.CheckAsync();
        Assert.Equal(0, await sync.SubmitAsync(api, http.BaseAddress!.AbsoluteUri, f.Device, Ct)); // Package is still owned.
        var packageRoot = f.Files.Inspect(package.ScanRoot); f.Files.Set(package.ScanRoot, new("unknown")); await f.CheckAsync();
        Assert.Equal(0, await sync.SubmitAsync(api, http.BaseAddress!.AbsoluteUri, f.Device, Ct)); Assert.True(Assert.Single(store.PresenceHead(f.Device).Items).IsLocal);
        f.Files.Set(package.ScanRoot, packageRoot); f.Files.RemoveTree(Path.Combine(package.ScanRoot, package.RelativePath)); await f.CheckAsync(); f.Clock.Advance(11); await f.CheckAsync();
        gate.DropReply = true; await Assert.ThrowsAsync<WebUnavailableException>(() => sync.SubmitAsync(api, http.BaseAddress!.AbsoluteUri, f.Device, Ct));
        Assert.Equal("uncertain", (await sync.StateAsync(http.BaseAddress!.AbsoluteUri, f.Device, Ct)).Status);
        Assert.Equal(1, await sync.SubmitAsync(api, http.BaseAddress!.AbsoluteUri, f.Device, Ct)); Assert.Equal(gate.Bodies[^1], gate.Bodies[^2]);
        Assert.False(Assert.Single(store.PresenceHead(f.Device).Items).IsLocal); Assert.True(store.ReadItem(item.Id)!.State!.IsLocal); // Other device remains.
        Assert.DoesNotContain("/api/v1/items", gate.Reads); Assert.Contains("/api/v1/item-heads", gate.Reads);
        foreach (var body in gate.Bodies)
        {
            Assert.DoesNotContain(f.Root, body); Assert.DoesNotContain(package.RelativePath, body); Assert.DoesNotContain("executable", body);
            using var json = JsonDocument.Parse(body); Assert.All(json.RootElement.GetProperty("items").EnumerateArray(), r => Assert.Equal(new[] { "item_id", "is_local" }.Order(), r.EnumerateObject().Select(p => p.Name).Order()));
        }
    }
}
