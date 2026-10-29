using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Acceptance;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class M7Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "m7-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteClientFlowSurvivesOfflinePlayExportSyncMoveInterruptionAndFullRestore(bool legacyWindowsPlan)
    {
        using var dir = new TestDirectory(); await using var app = new Factory(Path.Combine(dir.Path, "database")); using var initial = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry()); store.ImportPlays(PlayTests.Batch());
        var oldPlays = JsonSerializer.Serialize(store.Plays().Results, VndbJson.Options);
        using var gate = new AvailabilityHandler(app.Server.CreateHandler()); using var http = new HttpClient(gate, false) { BaseAddress = initial.BaseAddress };
        var fake = Path.GetFullPath(Path.Combine(TestDirectory.ScratchRoot, "../build/Vndb.FakeGame/bin/Release/net10.0/Vndb.FakeGame.exe"));
        var run = Path.Combine(dir.Path, "run");
        var report = await AcceptanceWorkflow.RunAsync(run, http.BaseAddress!, "m7-test-key", "v17", fake, http, gate, Ct, legacyWindowsPlan);
        Assert.True(report.Checks.Count >= 18); Assert.True(report.SessionSeconds > 0);
        Assert.Equal(oldPlays, JsonSerializer.Serialize(store.Plays().Results, VndbJson.Options));
        Assert.Empty(store.ArchiveRuns());
        var local = new LocalOrganizationServer(new(Path.Combine(run, "client")), new(Path.Combine(run, "client")), report.DeviceId);
        Assert.Equal("restored", (await local.ArchiveRunAsync(report.ArchiveRunId, Ct)).Receipt.State);
        Assert.All(store.Installations().Where(i => i.Details.DeviceId == report.DeviceId), i => Assert.NotNull(i.RemovedAt));
        Assert.True(File.Exists(Path.Combine(run, "result.json")));
    }
    [Fact]
    public async Task ColdOfflineCoreResultPersistsDependenciesAndImportsWithoutChangingExistingPersonalFields()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(Path.Combine(dir.Path, "database")); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        using var upstream = new HttpClient(new Upstream()); using var core = new VndbClient(upstream, VndbClientTests.FastOptions);
        var snapshots = new JsonSnapshotStore(Path.Combine(dir.Path, "client")); var router = new QueryRouter(core, snapshots);
        var result = await router.QueryAsync(new("vn_get", new(), "v765432"), "vndb", Ct);
        Assert.True(result.PendingImport); Assert.Equal("client_vndb", result.Source);
        var outbox = new ActivityOutbox(Path.Combine(dir.Path, "activities"));
        var identity = new ClientDeviceIdentity(Guid.NewGuid().ToString("D"), "M7 synthetic", "windows", DateTimeOffset.UtcNow);
        var target = await outbox.CreateTargetAsync(identity, "Synthetic cold result", Assert.Single(result.Items), ct: Ct);
        await outbox.EditAsync(target, "played", "Offline personal review", Ct);
        var web = new WebLibraryClient(http, http.BaseAddress!, "m7-test-key", TimeSpan.FromSeconds(30));
        Assert.True(Assert.Single(await outbox.SyncAsync(web, http.BaseAddress!.AbsoluteUri, Ct)).Complete);
        Assert.Equal("v765432", Assert.Single(store.PersonalItems()).Details.NovelId);
        var personal = Assert.Single(store.Plays().Results); Assert.Equal("Offline personal review", personal.Details.Review);
        foreach (var batch in await outbox.BatchesAsync(true, Ct)) Assert.True((await web.SubmitActivityAsync(batch, Ct)).Complete);
        Assert.Equal(personal, store.GetPlay(personal.Id)); Assert.Single(store.PersonalItems());
    }
    private sealed class Upstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(StubHandler.Json(
            request.RequestUri!.AbsolutePath.EndsWith("producer", StringComparison.Ordinal) ? "{\"results\":[],\"more\":false}" :
            "{\"results\":[{\"id\":\"v765432\",\"title\":\"Synthetic cold result\",\"titles\":[{\"lang\":\"ja\",\"title\":\"合成作品\",\"main\":true}],\"developers\":[]}],\"more\":false}"));
    }
    [Fact]
    public void WireHashesHavePortableLfBytesWhilePersonalTextAndLegacyHashesStayExact()
    {
        var value = new { value = 7, label = "fixture" };
        var golden = Encoding.UTF8.GetBytes("{\n  \"value\": 7,\n  \"label\": \"fixture\"\n}");
        Assert.Equal(golden, LocalArchiveFormat.Serialize(value));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(golden)), LocalArchiveFormat.Hash(value));
        Assert.NotEqual(LocalArchiveFormat.Hash(value), LocalArchiveFormat.LegacyWindowsHash(value));
        Assert.True(LocalArchiveFormat.MatchesHash(value, LocalArchiveFormat.LegacyWindowsHash(value)));
        Assert.False(LocalArchiveFormat.MatchesHash(value with { label = "changed" }, LocalArchiveFormat.LegacyWindowsHash(value)));
        Assert.NotEqual(LocalArchiveFormat.Hash(new { review = "a\r\nb" }), LocalArchiveFormat.Hash(new { review = "a\nb" }));
    }
    [Fact]
    public void LegacyWindowsJournalRemainsUntouchedAndNewSegmentCanContinueWithoutWeakeningBinding()
    {
        using var dir = new TestDirectory(); var run = Path.Combine(dir.Path, "journal"); Directory.CreateDirectory(run);
        var payload = new { id = "fixture", reason = "retain evidence" };
        var data = new JournalData(1, "approved", DateTimeOffset.UnixEpoch, new string('0', 64), LocalArchiveFormat.LegacyWindowsHash(payload));
        var line = new JournalLine(data, LocalArchiveFormat.LegacyWindowsHash(data)); var compact = new JsonSerializerOptions(VndbJson.Options) { WriteIndented = false };
        var original = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, compact) + "\n"); var file = Path.Combine(run, "journal-000000.jsonl"); File.WriteAllBytes(file, original);
        using (var journal = new RunJournal(run))
        {
            journal.BindValue("approved", payload); Assert.Equal(original, File.ReadAllBytes(file));
            Assert.Throws<InvalidDataException>(() => journal.BindValue("approved", new { id = "other", reason = "retain evidence" }));
            journal.Append("verified", LocalArchiveFormat.Hash(payload));
        }
        using var reopened = new RunJournal(run); Assert.True(reopened.Has("verified"));
        Assert.Equal(original, File.ReadAllBytes(file)[..original.Length]);
    }
    [Fact]
    public async Task LegacyWindowsCreationMappingAndBatchReceiptDeduplicateWithoutRewritingOriginalKeys()
    {
        using var dir = new TestDirectory(); await using var app = new Factory(dir.Path); using var http = app.CreateClient();
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var importer = app.Services.GetRequiredService<ActivityImports>();
        var device = Guid.NewGuid().ToString("D"); store.SaveDevice(new(device, new("M7 synthetic", "windows")), "device");
        var seed = new ActivityItemSeed("legacy-item", new("Legacy synthetic item")); var item = store.SaveItem(new(seed.Details), null, "old-create");
        store.SaveActivityMapping(device, "items", seed.ClientId, LocalArchiveFormat.LegacyWindowsHash(seed), item.Id);
        var batch = new ActivityBatch { BatchId = Guid.NewGuid().ToString("D"), DeviceId = device, Device = new("M7 synthetic", "windows"), Items = [seed] };
        store.RegisterActivityBatch(batch, LocalArchiveFormat.LegacyWindowsHash(batch));
        var receipt = importer.Submit(batch, "instance"); Assert.True(receipt.Complete); Assert.Equal(item.Id, Assert.Single(receipt.Results).ServerId); Assert.Single(store.PersonalItems());
        Assert.Equal("conflict", Assert.Single(importer.Submit(batch with { BatchId = Guid.NewGuid().ToString("D"), Items = [seed with { Details = new("Changed") }] }, "instance").Results).Status);
    }
}
