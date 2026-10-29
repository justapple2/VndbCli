using System.Diagnostics;
using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceSyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Api : ISyncApi
    {
        public Func<PersonalPlayWrite, string?, Task<PersonalPlayView>>? Save { get; set; }
        public int PresenceRevision { get; private set; }
        public int PresenceWrites { get; private set; }
        private IReadOnlyList<PresenceFlag> _flags = [];
        public Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct) => Task.FromResult(new ServiceInfo("test-instance", [1], "test", 10, true, ["activity_import", "boolean_presence"], [1], true));
        public Task<LibraryItemView> ItemAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ItemPage> ItemsAsync(int page = 1, CancellationToken ct = default) => Task.FromResult(new ItemPage([M5Tests.Item() with { Item = M5Tests.Item().Item with { Id = "test-item" } }], page, 100, 1, false));
        public Task<ItemPage> RemovedItemsAsync(int page = 1, CancellationToken ct = default) => Task.FromResult(new ItemPage([], page, 100, 0, false));
        public Task<PersonalPlayView> PersonalPlayAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PersonalPlayView> SavePersonalPlayAsync(PersonalPlayWrite request, string? id, string key, CancellationToken ct = default) => Save!(request, id);
        public Task<ActivityReceipt> SubmitActivityAsync(ActivityBatch batch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ImportBatchReceipt> SyncBatchAsync(JsonElement payload, string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<PresenceHead> PresenceHeadAsync(string deviceId, CancellationToken ct = default) => Task.FromResult(new PresenceHead(deviceId, PresenceRevision, _flags));
        public Task<PresenceReceipt> SubmitPresenceAsync(PresenceBatch batch, CancellationToken ct = default)
        {
            Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options)));
            Assert.Equal(PresenceRevision, batch.ExpectedRevision);
            PresenceWrites++; _flags = batch.Items;
            return Task.FromResult(new PresenceReceipt(batch.Id, "test-instance", LocalArchiveFormat.Hash(batch), ++PresenceRevision));
        }
    }

    [Fact]
    public async Task SlowPersonalSubmissionDoesNotBlockAnotherDraftOrSessionCheckpoint()
    {
        using var directory = new TestDirectory(); var outbox = new PersonalEditOutbox(directory.Path);
        var details = new PersonalPlayData("Synthetic", null, "playing", null, null, null, null, null, null, null, null);
        var draft = await outbox.EnqueueAsync("book", "http://library.test/", "play-a", new(details, ExpectedRevision: 1), Ct);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Save = async (request, id) => { started.SetResult(); await release.Task; return new(id!, request.Details, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); } };
        var sync = outbox.SyncOneAsync(api, "http://library.test/", draft, Ct);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await new PersonalEditOutbox(directory.Path).EnqueueAsync("book", "http://library.test/", "play-b", new(details, ExpectedRevision: 1), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var now = DateTimeOffset.UtcNow;
            await new ActivityOutbox(directory.Path).SaveSessionAsync(Guid.NewGuid().ToString("D"),
                new(Guid.NewGuid().ToString("D"), "device", "play", null, null, now, now, null, 1, 0, "active"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally { release.TrySetResult(); }
        await sync;
        Assert.Single(await outbox.PendingAsync("http://library.test/", Ct));
    }
    [Fact]
    public async Task ConcurrentPresenceReadersSubmitOneRevisionAndNeverUploadTheLocalTree()
    {
        using var directory = new TestDirectory(); var store = new LocalLibraryStore(directory.Path); var now = DateTimeOffset.UtcNow;
        var install = new LocalInstallation("test-install", new("test-device", Path.Combine(directory.Path, "synthetic-game"), "Synthetic"), "present", now, 1, now, now);
        await store.SaveAsync(new([new(install, [new("test-component", install.Id, new("test-item", "."), 1, now, now)])], []), Ct);
        var api = new Api();
        await Task.WhenAll(new PresenceSync(store).SubmitAsync(api, "http://library.test/", "test-device", Ct),
            new PresenceSync(new(directory.Path)).SubmitAsync(api, "http://library.test/", "test-device", Ct));
        Assert.Equal(1, api.PresenceWrites); Assert.Equal(1, api.PresenceRevision);
        Assert.False(File.Exists(Path.Combine(directory.Path, "presence-pending.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "presence-receipts")));
    }
    private sealed class Lanes : IClientSyncLanes
    {
        public List<string> Calls { get; } = [];
        public Task<int> MetadataAsync(CancellationToken ct) { Calls.Add("metadata"); return Task.FromResult(2); }
        public Task<bool> ActivitiesAsync(CancellationToken ct) { Calls.Add("activity"); return Task.FromResult(false); }
        public Task<int> PersonalAsync(CancellationToken ct) { Calls.Add("personal"); return Task.FromResult(1); }
        public Task<int> PresenceAsync(CancellationToken ct) { Calls.Add("presence"); return Task.FromResult(3); }
    }
    [Fact]
    public async Task CoordinatorKeepsTypedDependenciesAndReportsPendingConflicts()
    {
        var lanes = new Lanes(); var result = await new ClientSyncCoordinator(lanes).SyncAsync(Ct);
        Assert.Equal(["metadata", "activity", "personal", "presence"], lanes.Calls);
        Assert.Equal(new ClientSyncResult(2, false, 1, 3), result);
    }
    [Fact]
    public async Task TwoProcessesCannotCreateTwoPendingEditsForTheSamePlay()
    {
        using var directory = new TestDirectory(); await WorkersAsync(directory.Path, "enqueue", "unused");
        var pending = await new PersonalEditOutbox(directory.Path).PendingAsync("http://library.test/", Ct);
        Assert.Single(pending);
        var values = new[] { "a", "b" }.Select(name => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "worker-result-" + name + ".json")))).ToArray();
        try { Assert.Single(values, v => v.RootElement.GetProperty("rejected").GetBoolean()); }
        finally { foreach (var value in values) value.Dispose(); }
    }
    [Fact]
    public async Task TwoProcessesAppendIndependentRevisionsWithoutLosingAnActivityEdit()
    {
        using var directory = new TestDirectory(); var outbox = new ActivityOutbox(directory.Path);
        var target = await outbox.CreateTargetAsync(new(Guid.NewGuid().ToString("D"), "Synthetic", "windows", DateTimeOffset.UtcNow), "Synthetic worker", null, ct: Ct);
        await WorkersAsync(directory.Path, "edit", target.Id);
        var batches = await outbox.BatchesAsync(true, Ct); var edits = batches.SelectMany(b => b.PlayEdits).ToArray();
        Assert.Equal([1L, 2L], edits.Select(e => e.ExpectedRevision));
        Assert.Equal(3, Assert.Single(await outbox.TargetsAsync(Ct)).ExpectedRevision);
    }
    [Fact]
    public async Task SessionSnapshotsCannotRegressOrReopenAndRepeatedBytesStayUnchanged()
    {
        using var directory = new TestDirectory(); var id = Guid.NewGuid().ToString("D"); var target = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow;
        var final = new SessionSample(id, "test-device", "test-play", null, null, now, now.AddMinutes(1), now.AddMinutes(1), 3, 60, "ended");
        var first = new ActivityOutbox(directory.Path); var other = new ActivityOutbox(directory.Path);
        await first.SaveSessionAsync(target, final, Ct);
        var file = Path.Combine(first.DirectoryPath, target, "play_sessions", id + ".json"); var bytes = await File.ReadAllBytesAsync(file, Ct);
        await other.SaveSessionAsync(target, final with { Sequence = 2, CumulativeSeconds = 30, EndedAt = null, State = "active" }, Ct);
        await other.SaveSessionAsync(target, final, Ct);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => other.SaveSessionAsync(target, final with { Sequence = 4, EndedAt = null, State = "active" }, Ct));
    }
    [Fact]
    public async Task LeaseWaitCanBeCancelledWithoutDeletingTheOtherOwnersLock()
    {
        using var directory = new TestDirectory(); using var lease = await LocalFileLease.AcquireAsync(directory.Path, "test-resource", Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var second = await LocalFileLease.AcquireAsync(directory.Path, "test-resource", timeout.Token); });
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "locks")));
    }
    [Theory]
    [InlineData("completed", null, true)]
    [InlineData("not_started", 0, true)]
    [InlineData("not_started", 1, false)]
    [InlineData("invalid", null, false)]
    public void SharedPersonalRulesKeepUnknownTimeAndStateSemantics(string state, int? minutes, bool valid)
    {
        var details = new PersonalDetails("Synthetic", null, state, null, null, minutes, null, null, null, null, null);
        var failure = Record.Exception(() => PersonalRules.Validate(details));
        if (valid) Assert.Null(failure); else Assert.IsType<ArgumentException>(failure);
        Assert.DoesNotContain(typeof(IQuickPlayService).GetMethods().SelectMany(m => m.GetParameters()), p => p.ParameterType.Namespace?.StartsWith("Vndb.Desktop", StringComparison.Ordinal) == true);
    }
    internal static async Task WorkersAsync(string home, string mode, string target)
    {
        var privateRoot = Path.GetDirectoryName(TestDirectory.ScratchRoot)!;
        var driver = Path.Combine(privateRoot, "build/Vndb.Acceptance/bin/Release/net10.0/Vndb.Acceptance.dll");
        var children = new List<Process>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var name in new[] { "a", "b" })
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { driver, "--state-worker", home, mode, name, target }) start.ArgumentList.Add(arg);
                start.Environment["VN_PRIVATE_ROOT"] = privateRoot; start.Environment["VN_STATE_WORKER"] = "1";
                start.Environment.Remove("VN_LIBRARY_KEY"); start.Environment.Remove("VNDB_API_TOKEN"); start.Environment.Remove("VN_LIBRARY_URL");
                children.Add(Process.Start(start)!);
            }
            while (!new[] { "a", "b" }.All(name => File.Exists(Path.Combine(home, "worker-ready-" + name + ".json"))))
            { if (children.Any(p => p.HasExited)) throw new InvalidOperationException("Synthetic worker exited before synchronization."); await Task.Delay(10, timeout.Token); }
            await File.WriteAllTextAsync(Path.Combine(home, "worker-go.json"), "{}", timeout.Token);
            foreach (var child in children)
            {
                var error = child.StandardError.ReadToEndAsync(timeout.Token); var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
                await child.WaitForExitAsync(timeout.Token); await output;
                Assert.True(child.ExitCode == 0, await error);
            }
        }
        finally
        {
            foreach (var child in children) { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); } child.Dispose(); }
        }
    }
}
