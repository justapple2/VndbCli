using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

public sealed class M4Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", data).UseSetting("Library:WriteKey", "m4-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Proxy(IOrganizationServer inner) : IOrganizationServer
    {
        public bool Offline { get; set; }
        private void Check() { if (Offline) throw new WebUnavailableException(); }
        public Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct) { Check(); return inner.OrganizationInfoAsync(ct); }
        public Task<LocalArchiveRunView> ArchiveRunAsync(string id, CancellationToken ct) { Check(); return inner.ArchiveRunAsync(id, ct); }
        public Task<ArchiveReceipt> PlanArchiveAsync(LocalArchivePlan p, CancellationToken ct) { Check(); return inner.PlanArchiveAsync(p, ct); }
        public Task<ArchiveReceipt> PlanRestoreAsync(LocalRestorePlan p, CancellationToken ct) { Check(); return inner.PlanRestoreAsync(p, ct); }
        public Task<EvidenceReceipt> UploadEvidenceAsync(string file, string id, string phase, CancellationToken ct) { Check(); return inner.UploadEvidenceAsync(file, id, phase, ct); }
        public Task<ArchiveReceipt> CompleteArchiveAsync(string id, ArchiveCompletion c, bool restore, CancellationToken ct) { Check(); return inner.CompleteArchiveAsync(id, c, restore, ct); }
        public Task<ArchiveReceipt> CancelArchiveAsync(string id, ArchiveCompletion c, CancellationToken ct) { Check(); return inner.CancelArchiveAsync(id, c, ct); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public TestDirectory Directory { get; } = new();
        public Factory App { get; }
        public HttpClient Http { get; }
        public SqliteLibraryStore Store { get; }
        public IOrganizationServer Sdk { get; }
        public LocalLibraryStore Local { get; }
        public LocalInstallation Current => Local.ReadAsync().GetAwaiter().GetResult().Installations.Single().Installation;
        public Proxy Server { get; }
        public LocalInstallationView Installation { get; }
        public LocalArchivePlan Plan { get; }
        public string Run => Path.Combine(Directory.Path, "run");
        public string Source => Plan.SourcePath;
        public Fixture()
        {
            App = new(Path.Combine(Directory.Path, "database")); Http = App.CreateClient(); Store = App.Services.GetRequiredService<SqliteLibraryStore>();

            var d = Store.SaveDevice(new(Guid.NewGuid().ToString("D"), new("Synthetic M4", "windows")), "device");
            var scope = Path.Combine(Directory.Path, "scope"); var target = Path.Combine(Directory.Path, "target"); var source = Path.Combine(scope, "old-parent", "Old title");
            System.IO.Directory.CreateDirectory(source); System.IO.Directory.CreateDirectory(target);
            for (var n = 1; n <= 3; n++)
            {
                var part = Path.Combine(source, "game" + n); System.IO.Directory.CreateDirectory(part); File.WriteAllText(Path.Combine(part, "story.dat"), "日本語 story " + n);
            }
            var hidden = Path.Combine(source, "hidden.dat"); File.WriteAllText(hidden, "hidden content"); File.SetAttributes(hidden, FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(source, "restore.reg"), "Windows Registry Editor Version 5.00\n; synthetic fixture; never imported");
            var i = Store.SaveInstallation(new(new(d.Id, source, "Complete synthetic bundle")), null, "installation");
            var components = new List<InstallationComponent>();
            for (var n = 1; n <= 3; n++)
            {
                var item = Store.SaveItem(new(new("Chapter " + n, Notes: "keep notes")), null, "item" + n);
                components.Add(Store.SaveComponent(i.Id, new(new(item.Id, "game" + n)), null, "component" + n));
            }
            Installation = System.Text.Json.JsonSerializer.Deserialize<LocalInstallationView>(System.Text.Json.JsonSerializer.Serialize(new InstallationView(i, components), VndbJson.Options), VndbJson.Options)!; Plan = OrganizationPlanner.Create(Installation, new(scope, target), "会社：テスト", "合本／日本語", Ct);
            Local = new(Path.Combine(Directory.Path, "client")); Local.SaveAsync(new([Installation], [])).GetAwaiter().GetResult();
            Sdk = new LocalOrganizationServer(Local, new(Local.DirectoryPath), d.Id); Server = new(Sdk);
        }
        public OrganizationRunner Runner(Action<string>? fault = null) => new(Server, Http.BaseAddress!.ToString(), Plan.DeviceId, fault);
        public ValueTask DisposeAsync() { Http.Dispose(); App.Dispose(); Directory.Dispose(); return ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task WholeBundleMovesWithoutCopyAndRestoresFreshSavesAndManualAssociation()
    {
        await using var f = new Fixture(); var originalIds = f.Plan.Before.Entries.Select(e => e.Identity).ToArray();
        var archived = await f.Runner().ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct);
        Assert.Equal("archived", archived.State); Assert.False(System.IO.Directory.Exists(f.Source));
        var moved = LocalFiles.Snapshot(f.Plan.DestinationPath, Ct); Assert.Equal(f.Plan.Before.Sha256, moved.Sha256); Assert.Equal(originalIds, moved.Entries.Select(e => e.Identity).ToArray());
        Assert.All(f.Installation.Components, component => Assert.Equal(component.Id, f.Store.Components().Single(c => c.Id == component.Id).Id));
        f.Store.Save(StorageTests.Entry()); var item = f.Store.GetItem(f.Installation.Components[0].Details.ItemId)!;
        f.Store.LinkItem(item.Id, new("v17", item.Revision), "manual-link");
        File.WriteAllText(Path.Combine(f.Plan.DestinationPath, "new-save.dat"), "new progress after archive");
        System.IO.Directory.Delete(Path.GetDirectoryName(f.Source)!); // Empty synthetic parent only; exercises safe recreation.
        var restore = await f.Runner().PreviewRestoreAsync(f.Run, Ct);
        Assert.Equal(f.Plan.Before.Files + 1, restore.Before.Files);
        var restored = await f.Runner().RestoreAsync(restore, LocalArchiveFormat.Hash(restore), f.Run, Ct);
        Assert.Equal("restored", restored.State); Assert.Equal(restore.Before.Sha256, LocalFiles.Snapshot(f.Source, Ct).Sha256);
        Assert.Equal("v17", f.Store.GetItem(item.Id)!.Details.NovelId); Assert.Equal("keep notes", f.Store.GetItem(item.Id)!.Details.Notes);
        Assert.Equal(f.Source, f.Current.Details.RootPath);
        Assert.False(System.IO.Directory.Exists(f.Plan.DestinationPath)); Assert.Contains(f.Plan.Dependencies, h => h.Kind == "registry");
    }
    [Theory]
    [InlineData("after_intent")]
    [InlineData("before_rename")]
    [InlineData("after_rename")]
    [InlineData("before_sync")]
    [InlineData("after_commit")]
    public async Task CrashWindowsResumeByStableIdentityAndNeverRepeatRename(string point)
    {
        await using var f = new Fixture(); var failed = false;
        await Assert.ThrowsAsync<IOException>(() => f.Runner(stage => { if (!failed && stage == point) { failed = true; throw new IOException("synthetic crash"); } }).ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        Assert.True(failed); var result = await f.Runner().ResumeAsync(f.Run, Ct); Assert.Equal("archived", result.State);
        var revision = f.Current.Revision;
        Assert.Equal("archived", (await f.Runner().ResumeAsync(f.Run, Ct)).State); Assert.Equal(revision, f.Current.Revision);
        Assert.Equal(f.Plan.Before.Sha256, LocalFiles.Snapshot(f.Plan.DestinationPath, Ct).Sha256);
    }
    [Fact]
    public async Task NewOfflinePlanCannotExecuteButDurablePlannedReceiptCanMoveAndQueueEvidence()
    {
        await using var f = new Fixture(); f.Server.Offline = true;
        await Assert.ThrowsAsync<WebUnavailableException>(() => f.Runner().ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        Assert.True(System.IO.Directory.Exists(f.Source)); Assert.Empty(f.Store.ArchiveRuns());
        f.Server.Offline = false;
        await Assert.ThrowsAsync<IOException>(() => f.Runner(stage => { if (stage == "after_intent") throw new IOException("crash"); }).ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        f.Server.Offline = true; var pending = await f.Runner().ResumeAsync(f.Run, Ct); Assert.Equal("awaiting_sync", pending.State);
        Assert.False(System.IO.Directory.Exists(f.Source)); Assert.Equal("planned", (await f.Sdk.ArchiveRunAsync(f.Plan.Id, Ct)).Receipt.State);
        f.Server.Offline = false; Assert.Equal("archived", (await f.Runner().ResumeAsync(f.Run, Ct)).State);
    }
    [Theory]
    [InlineData("added-hidden")]
    [InlineData("replaced-root")]
    [InlineData("destination")]
    public async Task ChangedIdentityHiddenFilesAndDestinationConflictsStopWithoutLosingFiles(string mutation)
    {
        await using var f = new Fixture(); var held = Path.Combine(f.Directory.Path, "held-original");
        if (mutation == "added-hidden") { var path = Path.Combine(f.Source, "added.dat"); File.WriteAllText(path, "extra"); File.SetAttributes(path, FileAttributes.Hidden); }
        if (mutation == "replaced-root") { System.IO.Directory.Move(f.Source, held); System.IO.Directory.CreateDirectory(f.Source); File.WriteAllText(Path.Combine(f.Source, "replacement.dat"), "preserve this too"); }
        if (mutation == "destination") { System.IO.Directory.CreateDirectory(f.Plan.DestinationPath); File.WriteAllText(Path.Combine(f.Plan.DestinationPath, "existing.dat"), "never overwrite"); }
        await Assert.ThrowsAnyAsync<IOException>(() => f.Runner().ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        Assert.True(System.IO.Directory.Exists(f.Source)); Assert.Empty(f.Store.ArchiveRuns());
        if (mutation == "replaced-root") Assert.True(System.IO.Directory.Exists(held));
        if (mutation == "destination") Assert.Equal("never overwrite", File.ReadAllText(Path.Combine(f.Plan.DestinationPath, "existing.dat")));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PermissionOrDiskFailureStopsThenOriginalIntentCanResume(bool permission)
    {
        await using var f = new Fixture(); var injected = false;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Runner(stage => { if (stage == "before_rename") { injected = true; if (permission) throw new UnauthorizedAccessException("synthetic permission"); throw new IOException("synthetic disk failure"); } }).ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        Assert.True(injected);
        Assert.True(System.IO.Directory.Exists(f.Source)); Assert.False(System.IO.Directory.Exists(f.Plan.DestinationPath));
        Assert.Equal("archived", (await f.Runner().ResumeAsync(f.Run, Ct)).State);
    }
    [Fact]
    public async Task LeaseBlocksConcurrentLocationEditsAndBadEvidenceNeverCompletes()
    {
        await using var f = new Fixture(); var receipt = await f.Sdk.PlanArchiveAsync(f.Plan, Ct);
        Assert.Equal(receipt, await f.Sdk.PlanArchiveAsync(f.Plan, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Sdk.PlanArchiveAsync(f.Plan with { Id = Guid.NewGuid().ToString("D") }, Ct));
        await Assert.ThrowsAnyAsync<IOException>(() => f.Sdk.CompleteArchiveAsync(f.Plan.Id, new(new string('a', 64), receipt.Revision), false, Ct));
        using var bad = new HttpRequestMessage(HttpMethod.Put, "/api/v1/archive-runs/" + f.Plan.Id + "/evidence/archive");
        bad.Headers.Add("X-Library-Request", "1"); bad.Headers.Add("X-Library-Key", "m4-test-key"); bad.Headers.Add("Idempotency-Key", "bad-evidence"); bad.Headers.Add("X-Evidence-SHA256", new string('a', 64));
        bad.Content = new StringContent("{}", Encoding.UTF8, "application/json"); using var response = await f.Http.SendAsync(bad, Ct); Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("planned", (await f.Sdk.ArchiveRunAsync(f.Plan.Id, Ct)).Receipt.State); Assert.Equal(f.Source, f.Current.Details.RootPath);
    }
    [Fact]
    public async Task ReparseEntriesAndForeignVolumesAreRejectedWithoutFollowingThem()
    {
        await using var f = new Fixture(); var link = Path.Combine(f.Source, "junction"); var elsewhere = Path.Combine(f.Directory.Path, "elsewhere"); System.IO.Directory.CreateDirectory(elsewhere); File.WriteAllText(Path.Combine(elsewhere, "keep.dat"), "preserve");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, elsewhere }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(Ct); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<IOException>(() => LocalFiles.Snapshot(f.Source, Ct)); Assert.Equal("preserve", File.ReadAllText(Path.Combine(elsewhere, "keep.dat"))); }
        finally { System.IO.Directory.Delete(link); } // Only the synthetic junction entry; never recurse through it.
        if (NativeFiles.Volume(f.Source) != NativeFiles.Volume(@"C:\")) Assert.Throws<IOException>(() => OrganizationPlanner.Create(f.Installation, f.Plan.Policy with { TargetRoot = @"C:\" }, "M4-synthetic-only", "never-created", Ct));
        var wrong = f.Plan with { TargetRootIdentity = f.Plan.TargetRootIdentity with { Volume = "different-volume" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Sdk.PlanArchiveAsync(wrong, Ct)); Assert.True(System.IO.Directory.Exists(f.Source));
    }
    [Fact]
    public async Task JournalRejectsTamperingAndPreservesTornTailInNewSegment()
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => f.Runner(stage => { if (stage == "after_intent") throw new IOException("crash"); }).ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        var log = Path.Combine(f.Run, "journal-000000.jsonl"); File.AppendAllText(log, "{\"torn\":"); var original = File.ReadAllBytes(log);
        Assert.Equal("archived", (await f.Runner().ResumeAsync(f.Run, Ct)).State); Assert.Equal(original, File.ReadAllBytes(log)); Assert.True(File.Exists(Path.Combine(f.Run, "journal-000001.jsonl")));
        File.WriteAllText(log, File.ReadAllText(log).Replace("approved", "tampered"));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Runner().ResumeAsync(f.Run, Ct));
    }
    [Fact]
    public async Task UnapprovedPlanAndMetadataJsonAreNeverExecuted()
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Runner().ExecuteAsync(f.Plan, new string('a', 64), f.Run, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Runner().ExecuteAsync(f.Plan with { Format = "vn-library-batch" }, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        Assert.Empty(f.Store.ArchiveRuns()); Assert.True(System.IO.Directory.Exists(f.Source));
    }
    [Fact]
    public async Task UnmovedPlanCanBeCancelledAfterFilesChangeAndReplannedWithoutStaleOfflineExecution()
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<IOException>(() => f.Runner(stage => { if (stage == "after_intent") throw new IOException("stopped"); }).ExecuteAsync(f.Plan, LocalArchiveFormat.Hash(f.Plan), f.Run, Ct));
        File.WriteAllText(Path.Combine(f.Source, "new-file.dat"), "preserve new data");
        Assert.Equal("cancelled", (await f.Runner().CancelAsync(f.Run, LocalArchiveFormat.Hash(f.Plan), Ct)).State);
        f.Server.Offline = true; await Assert.ThrowsAsync<WebUnavailableException>(() => f.Runner().ResumeAsync(f.Run, Ct));
        Assert.True(System.IO.Directory.Exists(f.Source)); Assert.False(System.IO.Directory.Exists(f.Plan.DestinationPath));
        f.Server.Offline = false; var next = OrganizationPlanner.Create(f.Installation, f.Plan.Policy, f.Plan.Company, f.Plan.Title, Ct);
        Assert.Equal("archived", (await f.Runner().ExecuteAsync(next, LocalArchiveFormat.Hash(next), Path.Combine(f.Directory.Path, "next-run"), Ct)).State);
        Assert.Equal("preserve new data", File.ReadAllText(Path.Combine(next.DestinationPath, "new-file.dat")));
    }
    [Fact]
    public async Task CliPlansExecutesAndRestoresThroughWebWithoutAdminAccess()
    {
        await using var f = new Fixture(); var identity = Path.Combine(f.Directory.Path, "identity"); System.IO.Directory.CreateDirectory(identity);
        RunJournal.Write(Path.Combine(identity, "device.json"), new ClientDeviceIdentity(f.Plan.DeviceId, "Synthetic CLI", "windows", DateTimeOffset.UtcNow));
        await new LocalLibraryStore(identity).SaveAsync(new([f.Installation], []), Ct);
        var output = new StringWriter(); var error = new StringWriter();
        Task<int> Cli(params string[] arguments) => new Vndb.Cli.CliApplication(output, error, () => f.App.CreateClient(), name => name == "VN_LIBRARY_KEY" ? "m4-test-key" : null, settings: new(f.Http.BaseAddress!.ToString())).RunAsync(arguments, Ct);
        var planFile = Path.Combine(f.Directory.Path, "plan.json"); var directory = Path.Combine(f.Directory.Path, "cli-run");
        Assert.Equal(0, await Cli("organize", "plan", "--installation", f.Plan.InstallationId, "--scope", f.Plan.Policy.SourceScope, "--target-root", f.Plan.Policy.TargetRoot, "--company", f.Plan.Company, "--title", f.Plan.Title, "--identity-dir", identity, "--json", "--output", planFile));
        var plan = RunJournal.Read<LocalArchivePlan>(planFile);
        Assert.Equal(0, await Cli("organize", "execute", planFile, "--approve", LocalArchiveFormat.Hash(plan), "--run-directory", directory, "--identity-dir", identity, "--json"));
        var restoreFile = Path.Combine(f.Directory.Path, "restore.json");
        Assert.Equal(0, await Cli("organize", "restore-plan", "--run-directory", directory, "--identity-dir", identity, "--json", "--output", restoreFile));
        var restore = RunJournal.Read<LocalRestorePlan>(restoreFile);
        Assert.Equal(0, await Cli("organize", "restore", "--plan-json", restoreFile, "--approve", LocalArchiveFormat.Hash(restore), "--run-directory", directory, "--identity-dir", identity, "--json"));
        Assert.True(System.IO.Directory.Exists(f.Source)); Assert.Equal("restored", (await new LocalOrganizationServer(new(identity), new(identity), f.Plan.DeviceId).ArchiveRunAsync(plan.Id, Ct)).Receipt.State);
    }
    [Fact]
    public void PublicSdkNoLongerExposesCloudEvidenceUploadCapability()
    {
        Assert.Null(typeof(WebLibraryClient).GetMethod("UploadEvidenceAsync"));
        Assert.False(typeof(IOrganizationServer).IsAssignableFrom(typeof(WebLibraryClient)));
    }
    [Fact]
    public async Task DiscoveryKeepsKnownBundleWholeAndReportsUnmatchedExecutablesAsCandidatesOnly()
    {
        await using var f = new Fixture(); var unknown = Path.Combine(f.Plan.Policy.SourceScope, "unknown"); System.IO.Directory.CreateDirectory(unknown);
        File.WriteAllBytes(Path.Combine(unknown, "game.exe"), [0, 1, 2, 3]);
        var scan = DirectoryScanner.Scan(f.Plan.Policy.SourceScope, known: [f.Installation], ct: Ct);
        Assert.Equal(2, scan.Candidates.Count); Assert.Single(scan.Candidates, c => c.InstallationId == f.Plan.InstallationId);
        Assert.Single(scan.Candidates, c => c.InstallationId is null); Assert.DoesNotContain(scan.Candidates, c => c.Path.EndsWith("game1", StringComparison.Ordinal));
        Assert.Empty(f.Store.ArchiveRuns()); Assert.True(System.IO.Directory.Exists(f.Source));
    }
}
