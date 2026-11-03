using System.Net.Http.Json;
using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Acceptance;

public sealed class AvailabilityHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public bool Offline { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Offline ? throw new HttpRequestException("Synthetic client-side disconnect; NAS remains available to other clients.") : base.SendAsync(request, ct);
}
public sealed record AcceptanceReport(string ServiceInstanceId, string DeviceId, string PlayId, string InstallationId,
    string ArchiveRunId, long SessionSeconds, IReadOnlyList<string> Checks);

/// <summary>Runs only copied FakeGame fixtures. No SQLite, real game discovery, registry import or physical deletion.</summary>
public static class AcceptanceWorkflow
{
    public static async Task RecoverPersonalAsync(string run, Uri server, string key, HttpClient http, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(run, "organization"))) throw new InvalidOperationException("This fixture has an organization intent; resume that exact journal before personal cleanup.");
        var outbox = new ActivityOutbox(Path.Combine(run, "client")); var target = (await outbox.TargetsAsync(ct)).Single();
        if (!target.Title.StartsWith("M7 synthetic acceptance ", StringComparison.Ordinal) || target.Dependencies.Metadata.Count != 0 || target.Dependencies.Items.Count != 1) throw new InvalidDataException("Not the bounded synthetic fixture.");
        var web = new WebLibraryClient(http, server, key, TimeSpan.FromSeconds(30));
        var originalFile = Directory.GetFiles(Path.Combine(run, "export"), "*.json").Single();
        var original = JsonSerializer.Deserialize<ActivityBatch>(await File.ReadAllTextAsync(originalFile, ct), VndbJson.Options)!;
        var first = await web.SubmitActivityAsync(original, ct);
        var playId = first.Results.Single(r => r.Kind == "plays").ServerId!; var itemId = first.Results.Single(r => r.Kind == "items").ServerId!;
        var before = await web.PlayTimeAsync(playId, ct);
        var receipts = await outbox.SyncAsync(web, server.AbsoluteUri, ct);
        if (receipts.Any(r => !r.Complete) || (await outbox.BatchesAsync(ct: ct)).Count != 0 || (await web.PlayTimeAsync(playId, ct)).TotalSeconds != before.TotalSeconds) throw new InvalidDataException("Recovery did not preserve idempotent time.");
        var item = await web.ItemAsync(itemId, ct);
        var play = await http.GetFromJsonAsync<PersonalPlayView>(new Uri(server, "api/v1/plays/" + playId), VndbJson.Options, ct) ?? throw new InvalidDataException("Missing synthetic play.");
        if (item.Item.Details.Title != target.Title || play.Details.Title != target.Title || play.Details.Review != "Synthetic offline review; not a real play.") throw new InvalidDataException("Synthetic identity mismatch; no cleanup performed.");
        if (play.DeletedAt is null) await RemoveAsync(http, server, key, "plays/" + playId, play.Revision, ct);
        if (item.Item.DeletedAt is null) await RemoveAsync(http, server, key, "items/" + itemId, item.Item.Revision, ct);
        var device = (await web.DevicesAsync(ct)).Single(d => d.Id == original.DeviceId);
        if (device.Details.Name != "M7 synthetic recovered fixture") await web.RegisterDeviceAsync(new(device.Id, new("M7 synthetic recovered fixture", "windows"), device.Revision), Guid.NewGuid().ToString("D"), ct);
        if (!File.Exists(Path.Combine(run, "personal-recovery.json"))) await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(run, "personal-recovery.json"), new { playId, itemId, unchangedSeconds = before.TotalSeconds, receipts }, false, ct);
    }
    public static async Task<AcceptanceReport> RunAsync(string run, Uri server, string key, string novelId, string fake,
        HttpClient http, AvailabilityHandler gate, CancellationToken ct, bool legacyWindowsPlan = false)
    {
        run = Path.GetFullPath(run);
        if (Directory.Exists(run) || Path.GetFileName(fake) != "Vndb.FakeGame.exe" || !File.Exists(fake)) throw new ArgumentException("A new run directory and built FakeGame are required.");
        LocalFiles.CheckAncestors(run); LocalFiles.CheckAncestors(fake); Directory.CreateDirectory(run);
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add(message); }
        var web = new WebLibraryClient(http, server, key, TimeSpan.FromSeconds(30));
        var info = await web.OrganizationInfoAsync(ct);
        Check(info.SchemaVersion == 14 && info.Capabilities.Contains("activity_import") && info.Capabilities.Contains("boolean_presence"), "Web v1/schema14 path-free capabilities");
        var beforePlays = (await web.PlaysAsync(ct: ct)).Select(p => p.Id).Order().ToArray();
        var beforeItems = await ItemIdsAsync(web, ct);
        var localLibrary = new LocalLibraryStore(Path.Combine(run, "client"));
        var client = Path.Combine(run, "client"); var snapshots = new JsonSnapshotStore(Path.Combine(client, "queries"), server.AbsoluteUri);
        using var upstream = new HttpClient(new RejectUpstream()); using var core = new VndbClient(upstream, new() { MaxRetries = 0 });
        var router = new QueryRouter(core, snapshots, web); var query = new ClientQuery("vn_get", new(Mode: "local_only"), novelId);
        var online = await router.QueryAsync(query, "web", ct);
        Check(online.Items.Count == 1 && online.Source == "library", "Online metadata read and cached without refreshing existing VNDB data");
        gate.Offline = true;
        var cached = await router.QueryAsync(query, "auto", ct); Check(cached.Source == "client_cache", "Same-origin Web disconnect returns client cache");
        var scope = Path.Combine(run, "scope"); var source = Path.Combine(scope, "old", "Complete fixture");
        var game = Path.Combine(source, "game"); var targetRoot = Path.Combine(run, "target");
        Directory.CreateDirectory(game); Directory.CreateDirectory(targetRoot); Directory.CreateDirectory(Path.Combine(source, "bonus"));
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fake)!)) File.Copy(file, Path.Combine(game, Path.GetFileName(file)));
        await File.WriteAllTextAsync(Path.Combine(source, "bonus", "bonus.txt"), "Synthetic bonus component", ct);
        var hidden = Path.Combine(source, "hidden.dat"); await File.WriteAllTextAsync(hidden, "Synthetic hidden content", ct); File.SetAttributes(hidden, FileAttributes.Hidden);
        await File.WriteAllTextAsync(Path.Combine(source, "restore.reg"), "Windows Registry Editor Version 5.00\n; Synthetic dependency evidence; never imported\n", ct);
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(client, ct);
        identity = identity with { Name = "M7 synthetic fixture " + identity.Id[..8] };
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(client, "device.json"), identity, true, ct);
        var title = "M7 synthetic acceptance " + Guid.NewGuid().ToString("N");
        var outbox = new ActivityOutbox(client); var playTarget = await outbox.CreateTargetAsync(identity, title, null, ct: ct);
        var launcher = new LocalLauncher(client);
        var profile = await launcher.SaveAsync(title, Path.Combine(game, "Vndb.FakeGame.exe"), ["--receipt", Path.Combine(run, "fake-game.json"), "--hold", "4500"], game, ct: ct);
        using (var session = new LocalSessionManager(launcher, outbox, client))
        {
            await session.StartAsync(profile.Id, playTarget, ct);
            while (session.Running) { await Task.Delay(500, ct); await session.TickAsync(ct); }
            Check(session.Current?.Sample.State == "ended" && session.Current.Sample.CumulativeSeconds > 0, "Offline copied FakeGame launches, is timed by exact identity, and ends naturally");
        }
        playTarget = await outbox.EditAsync(playTarget, "completed", "Synthetic offline review; not a real play.", ct);
        var export = Path.Combine(run, "export"); Check(await outbox.ExportAsync(export, ct) == 1, "Offline session and review exported with creation dependencies");
        var disconnected = false;
        try { await outbox.SyncAsync(web, server.AbsoluteUri, ct); } catch (WebUnavailableException) { disconnected = true; }
        Check(disconnected && (await outbox.BatchesAsync(ct: ct)).Count == 1, "Disconnected sync retains pending originals");
        gate.Offline = false;
        var receipt = (await outbox.SyncAsync(web, server.AbsoluteUri, ct)).Single();
        Check(receipt.Complete && (await outbox.BatchesAsync(ct: ct)).Count == 0, "Reconnect SDK sync acknowledges every dependent operation");
        var itemId = receipt.Results.Single(r => r.Kind == "items").ServerId!;
        var playId = receipt.Results.Single(r => r.Kind == "plays").ServerId!;
        var time = await web.PlayTimeAsync(playId, ct);
        Check(time.BaseMinutes == 0 && time.SessionSeconds > 0 && time.TotalSeconds == time.SessionSeconds, "New replay base and confirmed seconds remain separate");
        var exported = JsonSerializer.Deserialize<ActivityBatch>(await File.ReadAllTextAsync(Directory.GetFiles(export, "*.json").Single(), ct), VndbJson.Options)!;
        var imported = await web.SubmitActivityAsync(exported, ct);
        Check(imported.Complete && imported.PayloadSha256 == receipt.PayloadSha256 && (await web.PlayTimeAsync(playId, ct)).TotalSeconds == time.TotalSeconds, "Manual JSON import and automatic sync share idempotency and never double count");
        var play = (await web.PlaysAsync(itemId, ct)).Single();
        Check(play.Details.Review == "Synthetic offline review; not a real play.", "Offline personal review reaches the selected play");
        var bonus = await WriteAsync<LibraryItem>(http, server, key, "items", new ItemWrite(new(title + " bonus")), ct);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallation(Guid.NewGuid().ToString("D"), new(identity.Id, source, title, "M7 テスト会社"), "present", now, 1, now, now);
        var primary = new LocalInstallationComponent(Guid.NewGuid().ToString("D"), installation.Id, new(itemId, "game"), 1, now, now);
        var extra = new LocalInstallationComponent(Guid.NewGuid().ToString("D"), installation.Id, new(bonus.Id, "bonus", "bonus"), 1, now, now);
        await localLibrary.SaveAsync(new([new(installation, [primary, extra])], []), ct);
        var localServer = new LocalOrganizationServer(localLibrary, new(client), identity.Id);
        var plan = OrganizationPlanner.Create(new(installation, [primary, extra]), new(scope, targetRoot), "M7 テスト会社", "M7 検証用作品", ct);
        if (legacyWindowsPlan) plan = plan with { Before = plan.Before with { Sha256 = LocalArchiveFormat.LegacyWindowsHash(new { root = plan.Before.Root, entries = plan.Before.Entries }) } };
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(run, "approved-synthetic-plan.json"), plan, false, ct);
        var journal = Path.Combine(run, "organization");
        var runner = new OrganizationRunner(localServer, "local:" + identity.Id, identity.Id, stage => { if (stage == "before_sync") throw new IOException("Synthetic interruption"); });
        try { await runner.ExecuteAsync(plan, legacyWindowsPlan ? LocalArchiveFormat.LegacyWindowsHash(plan) : LocalArchiveFormat.Hash(plan), journal, ct); throw new InvalidOperationException("Fault not injected"); }
        catch(IOException) { Check(!Directory.Exists(source) && Directory.Exists(plan.DestinationPath), "Whole bundle renamed once before simulated local receipt interruption"); }
        Check((await localServer.ArchiveRunAsync(plan.Id, ct)).Receipt.State == "planned", "Local JSON retains planned state while completion is pending");
        var resumedRunner = new OrganizationRunner(localServer, "local:" + identity.Id, identity.Id);
        Check((await resumedRunner.ResumeAsync(journal, ct)).State == "archived", "Local JSON resumes evidence without repeating the move");
        var moved = LocalFiles.Snapshot(plan.DestinationPath, ct);
        Check(LocalArchiveFormat.MatchesTree(moved, plan.Before) && moved.Entries.Select(e => e.Identity).SequenceEqual(plan.Before.Entries.Select(e => e.Identity)), "File identities and contents prove direct rename, including hidden files and bonus");
        var remoteInstallation = (await localLibrary.ReadAsync(ct)).Installations.Single();
        Check(remoteInstallation.Installation.Details.RootPath == plan.DestinationPath && remoteInstallation.Components.Select(c => c.Id).Order().SequenceEqual(new[] { primary.Id, extra.Id }.Order()), "Local JSON path updates while component IDs stay stable");
        await File.WriteAllTextAsync(Path.Combine(plan.DestinationPath, "game", "save-after-archive.txt"), "Synthetic save created after archive", ct);
        var restore = await resumedRunner.PreviewRestoreAsync(journal, ct);
        Check((await resumedRunner.RestoreAsync(restore, LocalArchiveFormat.Hash(restore), journal, ct)).State == "restored", "Complete reverse move and local JSON restored receipt");
        Check(File.Exists(Path.Combine(source, "game", "save-after-archive.txt")) && File.GetAttributes(Path.Combine(source, "hidden.dat")).HasFlag(FileAttributes.Hidden), "New saves and hidden attributes survive complete restoration");
        remoteInstallation = (await localLibrary.ReadAsync(ct)).Installations.Single();
        Check(remoteInstallation.Installation.Details.RootPath == source && (await web.PlayTimeAsync(playId, ct)).TotalSeconds == time.TotalSeconds, "Restored local JSON agrees with disk and Web play time is unchanged");
        Check(await localLibrary.SyncPresenceAsync(web, server.AbsoluteUri, identity.Id, ct) == 2, "Only boolean presence crosses the network");
        await RemoveAsync(http, server, key, "plays/" + playId, play.Revision, ct);
        await RemoveAsync(http, server, key, "items/" + itemId, (await web.ItemAsync(itemId, ct)).Item.Revision, ct);
        await RemoveAsync(http, server, key, "items/" + bonus.Id, bonus.Revision, ct);
        Check(beforePlays.SequenceEqual((await web.PlaysAsync(ct: ct)).Select(p => p.Id).Order()), "Original active personal plays unchanged after synthetic soft removal");
        var afterItems = await ItemIdsAsync(web, ct);
        Check(beforeItems.SequenceEqual(afterItems), "Original active item identities unchanged; installations stay in local JSON");
        var report = new AcceptanceReport(info.ServiceInstanceId, identity.Id, playId, installation.Id, plan.Id, time.SessionSeconds, checks);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(run, "result.json"), report, false, ct); return report;
    }
    private sealed class RejectUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Warm-cache acceptance must not send real VNDB requests.");
    }
    private static async Task<string[]> ItemIdsAsync(WebLibraryClient web, CancellationToken ct)
    { var result = new List<string>(); for (var page = 1; ; page++) { var data = await web.ItemsAsync(page, ct); result.AddRange(data.Items.Select(i => i.Item.Id)); if (!data.More) return result.Order().ToArray(); } }
    private static async Task<T> WriteAsync<T>(HttpClient http, Uri server, string key, string path, object body, CancellationToken ct)
    {
        using var request = Request(server, key, path, HttpMethod.Post); request.Content = JsonContent.Create(body, options: VndbJson.Options);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(VndbJson.Options, ct) ?? throw new InvalidDataException("Missing API result.");
    }
    private static async Task RemoveAsync(HttpClient http, Uri server, string key, string path, long revision, CancellationToken ct)
    { using var request = Request(server, key, path + "?expected_revision=" + revision, HttpMethod.Delete); using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode(); }
    private static HttpRequestMessage Request(Uri server, string key, string path, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, new Uri(server, "api/v1/" + path)); request.Headers.Add("X-Library-Request", "1");
        request.Headers.Add("X-Library-Key", key); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D")); return request;
    }
}
