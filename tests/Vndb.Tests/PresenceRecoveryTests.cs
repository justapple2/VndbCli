using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Cli;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class PresenceRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://localhost/";
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", root)
            .UseSetting("Library:WriteKey", "presence-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Gate(HttpMessageHandler handler) : DelegatingHandler(handler)
    {
        public bool LoseReply { get; set; }
        public int Writes { get; private set; }
        public List<string> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var presence = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/presence";
            if (presence) { Writes++; Payloads.Add(await request.Content!.ReadAsStringAsync(ct)); }
            var response = await base.SendAsync(request, ct);
            if (presence && LoseReply) { LoseReply = false; response.Dispose(); throw new HttpRequestException("Synthetic lost reply"); }
            return response;
        }
    }
    private static LocalInstallationView Install(string device, string item, string status, string suffix = "")
    {
        var now = DateTimeOffset.UtcNow; var id = "installation-" + item + suffix;
        return new(new(id, new(device, @"E:\Synthetic\" + id, "Synthetic"), status, now, 1, now, now),
            [new("component-" + item + suffix, id, new(item, "bonus"), 1, now, now)]);
    }
    private static async Task<PresenceBatch> Pending(LocalLibraryStore local, ServiceInfo info, string device, params PresenceFlag[] flags)
    {
        var batch = new PresenceBatch(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, device, 0, flags);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(local.DirectoryPath, "presence-pending.json"), batch, false, Ct); return batch;
    }
    private static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), VndbJson.Options)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRejectionCanBeRecheckedWithoutRevivingRemovedItemsOrLosingOriginalBytes(bool crashAfterArchive)
    {
        using var db = new TestDirectory(); using var dir = new TestDirectory(); await using var app = new Factory(db.Path);
        using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "presence-test-key", TimeSpan.FromSeconds(10));
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var a = store.SaveItem(new(new("Synthetic A")), null, "a"); var b = store.SaveItem(new(new("Synthetic B")), null, "b");
        var device = Guid.NewGuid().ToString("D"); var local = new LocalLibraryStore(dir.Path); var sync = new PresenceSync(local);
        await local.SaveAsync(new([Install(device, a.Id, "present"), Install(device, b.Id, "present")], []), Ct);
        var batch = await Pending(local, await api.OrganizationInfoAsync(Ct), device, new(a.Id, true), new(b.Id, true));
        var original = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "presence-pending.json"), Ct);
        store.SetItemRemoved(a.Id, new(a.Revision), true, "remove-a");
        await Assert.ThrowsAsync<PresenceConflictException>(() => sync.SubmitAsync(api, Origin, device, Ct));
        await Assert.ThrowsAsync<PresenceConflictException>(() => sync.SubmitAsync(api, Origin, device, Ct));
        Assert.Equal(1, gate.Writes); Assert.Equal("conflict", (await sync.StateAsync(Origin, device, Ct)).Status); Assert.Equal(0, store.PresenceHead(device).Revision);
        var preview = await sync.PreviewRecheckAsync(api, Origin, device, Ct); Assert.Equal(1, preview.ExcludedItems);
        Assert.Equal(b.Id, Assert.Single(preview.Replacement!.Items).ItemId); Assert.NotEqual(batch.Id, preview.Replacement.Id);
        if (crashAfterArchive)
        {
            await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(dir.Path, "presence-recovery", batch.Id + ".json"), new PresenceRecoveryIntent(preview, LocalArchiveFormat.Hash(preview)), false, Ct);
            Directory.CreateDirectory(Path.Combine(dir.Path, "presence-rejected"));
            File.Move(Path.Combine(dir.Path, "presence-pending.json"), Path.Combine(dir.Path, "presence-rejected", batch.Id + ".json"));
        }
        else await sync.ApplyRecheckAsync(api, preview, LocalArchiveFormat.Hash(preview), Ct);
        Assert.Equal(1, await new PresenceSync(new(dir.Path)).SubmitAsync(api, Origin, device, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "presence-rejected", batch.Id + ".json"), Ct));
        Assert.Equal(2, (await local.ReadAsync(Ct)).Installations.Count); Assert.Equal(1, store.PresenceHead(device).Revision);
        Assert.Equal(b.Id, Assert.Single(store.PresenceHead(device).Items).ItemId); Assert.DoesNotContain("Synthetic\\", string.Join("", gate.Payloads));
        Assert.True(File.Exists(Path.Combine(dir.Path, "presence-recovered", batch.Id + ".json")));
        Assert.Equal(0, await sync.SubmitAsync(api, Origin, device, Ct));
    }
    [Fact]
    public async Task LostReplyRetriesTheOriginalIdAndNeverPermitsReplacement()
    {
        using var db = new TestDirectory(); using var dir = new TestDirectory(); await using var app = new Factory(db.Path);
        using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()) { LoseReply = true }; using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "presence-test-key", TimeSpan.FromSeconds(10));
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); var item = store.SaveItem(new(new("Synthetic")), null, "item");
        var device = Guid.NewGuid().ToString("D"); var local = new LocalLibraryStore(dir.Path); var sync = new PresenceSync(local);
        await local.SaveAsync(new([Install(device, item.Id, "present")], []), Ct);
        await Assert.ThrowsAsync<WebUnavailableException>(() => sync.SubmitAsync(api, Origin, device, Ct));
        Assert.Equal("uncertain", (await sync.StateAsync(Origin, device, Ct)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.PreviewRecheckAsync(api, Origin, device, Ct));
        Assert.Equal(1, await sync.SubmitAsync(api, Origin, device, Ct)); Assert.Equal(1, store.PresenceHead(device).Revision);
        Assert.Equal(2, gate.Writes); Assert.Equal(gate.Payloads[0], gate.Payloads[1]);
    }
    [Fact]
    public async Task NewDraftExcludesRemovedAndUnknownItemsButCombinesMultipleCopiesAndDevices()
    {
        using var db = new TestDirectory(); using var dir = new TestDirectory(); await using var app = new Factory(db.Path); using var http = app.CreateClient();
        var api = new WebLibraryClient(http, http.BaseAddress!, "presence-test-key", TimeSpan.FromSeconds(10)); var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        var a = store.SaveItem(new(new("A")), null, "a"); var b = store.SaveItem(new(new("B")), null, "b"); var c = store.SaveItem(new(new("C")), null, "c");
        store.SetItemRemoved(a.Id, new(a.Revision), true, "remove"); var device = Guid.NewGuid().ToString("D"); var local = new LocalLibraryStore(dir.Path);
        await local.SaveAsync(new([Install(device, a.Id, "present"), Install(device, b.Id, "unknown"), Install(device, c.Id, "missing"), Install(device, c.Id, "present", "copy"), Install("other-device", b.Id, "present")], []), Ct);
        var sync = new PresenceSync(local); Assert.Equal(1, await sync.SubmitAsync(api, Origin, device, Ct));
        Assert.Equal(new PresenceFlag(c.Id, true), Assert.Single(store.PresenceHead(device).Items));
        await local.UpdateAsync(l => l with { Installations = [Install(device, c.Id, "unknown")] }, Ct);
        Assert.Equal(0, await sync.SubmitAsync(api, Origin, device, Ct)); Assert.True(Assert.Single(store.PresenceHead(device).Items).IsLocal);
        await local.UpdateAsync(l => l with { Installations = [Install(device, c.Id, "missing")] }, Ct);
        Assert.Equal(1, await sync.SubmitAsync(api, Origin, device, Ct)); Assert.False(Assert.Single(store.PresenceHead(device).Items).IsLocal);
        store.SetItemRemoved(a.Id, new(a.Revision + 1), false, "restore");
        await local.UpdateAsync(l => l with { Installations = [Install(device, a.Id, "present")] }, Ct);
        Assert.Equal(1, await sync.SubmitAsync(api, Origin, device, Ct)); Assert.Contains(store.PresenceHead(device).Items, i => i.ItemId == a.Id && i.IsLocal);
    }
    [Theory]
    [InlineData("library")]
    [InlineData("authority")]
    [InlineData("head")]
    [InlineData("bytes")]
    public async Task PreviewChangesNeverReplaceRejectedDraft(string change)
    {
        using var db = new TestDirectory(); using var dir = new TestDirectory(); await using var app = new Factory(db.Path); using var http = app.CreateClient();
        var api = new WebLibraryClient(http, http.BaseAddress!, "presence-test-key", TimeSpan.FromSeconds(10)); var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        var item = store.SaveItem(new(new("Synthetic")), null, "item"); var device = Guid.NewGuid().ToString("D"); var info = await api.OrganizationInfoAsync(Ct); var local = new LocalLibraryStore(dir.Path); var sync = new PresenceSync(local);
        await local.SaveAsync(new([Install(device, item.Id, "present")], []), Ct);
        await api.SubmitPresenceAsync(new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, device, 0, [new(item.Id, false)]), Ct);
        var batch = await Pending(local, info, device, new PresenceFlag(item.Id, true)); await Assert.ThrowsAsync<PresenceConflictException>(() => sync.SubmitAsync(api, Origin, device, Ct));
        var preview = await sync.PreviewRecheckAsync(api, Origin, device, Ct);
        if (change == "library") await local.UpdateAsync(l => l with { Installations = [Install(device, item.Id, "missing")] }, Ct);
        if (change == "authority") store.SetItemRemoved(item.Id, new(item.Revision), true, "remove");
        if (change == "head") await api.SubmitPresenceAsync(new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, device, 1, [new(item.Id, true)]), Ct);
        if (change == "bytes") await File.AppendAllTextAsync(Path.Combine(dir.Path, "presence-pending.json"), "\n", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.ApplyRecheckAsync(api, preview, LocalArchiveFormat.Hash(preview), Ct));
        Assert.Equal(batch.Id, Read<PresenceBatch>(Path.Combine(dir.Path, "presence-pending.json")).Id); Assert.False(Directory.Exists(Path.Combine(dir.Path, "presence-rejected")));
    }
    [Fact]
    public async Task DesktopRequiresApprovalAndDiscardingPreviewDisablesConfirmation()
    {
        using var dir = new TestDirectory(); var vm = new PresenceFeatureViewModel(() => new(new(dir.Path)), () => null, () => Origin, () => "synthetic", f => f(Ct), _ => { });
        Assert.False(vm.ConfirmCommand.CanExecute(null)); vm.Approved = true; Assert.False(vm.ConfirmCommand.CanExecute(null));
        vm.Invalidate(); Assert.False(vm.Approved); await vm.RefreshCommand.ExecuteAsync(null); Assert.Contains("没有待提交", vm.StateLabel);
    }
    [Fact]
    public async Task RecheckCanFinishWithoutNewFlagsAndCliReportsConflictAsJson()
    {
        using var db = new TestDirectory(); using var dir = new TestDirectory(); await using var app = new Factory(db.Path); using var http = app.CreateClient();
        var api = new WebLibraryClient(http, http.BaseAddress!, "presence-test-key", TimeSpan.FromSeconds(10)); var store = app.Services.GetRequiredService<SqliteLibraryStore>();
        var item = store.SaveItem(new(new("Synthetic")), null, "item"); var device = await LocalDeviceIdentity.LoadOrCreateAsync(dir.Path, Ct); var info = await api.OrganizationInfoAsync(Ct);
        var local = new LocalLibraryStore(dir.Path); var sync = new PresenceSync(local); await local.SaveAsync(new([Install(device.Id, item.Id, "present")], []), Ct);
        await api.SubmitPresenceAsync(new(Guid.NewGuid().ToString("D"), info.ServiceInstanceId, device.Id, 0, [new(item.Id, true)]), Ct);
        await Pending(local, info, device.Id, new PresenceFlag(item.Id, true)); await Assert.ThrowsAsync<PresenceConflictException>(() => sync.SubmitAsync(api, Origin, device.Id, Ct));
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, () => new HttpClient(app.Server.CreateHandler()), n => n == "VN_LIBRARY_KEY" ? "presence-test-key" : null, VndbClientTests.FastOptions, new(Origin));
        Assert.Equal(0, await cli.RunAsync(["presence", "status", "--identity-dir", dir.Path, "--json"], Ct));
        Assert.Equal("conflict", JsonSerializer.Deserialize<PresenceState>(output.ToString(), VndbJson.Options)!.Status);
        output.GetStringBuilder().Clear(); Assert.NotEqual(0, await cli.RunAsync(["presence", "retry", "--identity-dir", dir.Path, "--json"], Ct)); Assert.Contains("conflict", output.ToString());
        var preview = await sync.PreviewRecheckAsync(api, Origin, device.Id, Ct); Assert.Null(preview.Replacement);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(dir.Path, "presence-recovery", preview.RejectedBatchId + ".json"), new PresenceRecoveryIntent(preview, LocalArchiveFormat.Hash(preview)), false, Ct);
        Assert.Equal(0, await sync.SubmitAsync(api, Origin, device.Id, Ct)); Assert.Equal("idle", (await sync.StateAsync(Origin, device.Id, Ct)).Status);
        Assert.Equal(1, store.PresenceHead(device.Id).Revision);
    }
}
