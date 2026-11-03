using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class K6Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PersonalDetails Details() => new("Synthetic", "v17", "played", null, null, null, 8m, "original", "", null, "ja");
    [Fact]
    public void PersonalComparisonKeepsUnknownMissingNullZeroAndDecimalMeaningSeparate()
    {
        var original = Details(); var incoming = original with { DurationMinutes = 0, ExtraNotes = "", Score = 8.0m };
        var rows = PersonalComparison.Play(original, true, original with { Review = "Web" }, true, incoming);
        Assert.DoesNotContain(rows, r => r.Key == "score");
        var time = Assert.Single(rows, r => r.Key == "duration_minutes"); Assert.Null(time.Original.Value); Assert.Equal("0", time.Incoming.Value);
        Assert.True(time.DraftChanged); Assert.False(time.RemoteChanged);
        Assert.Equal("", Assert.Single(rows, r => r.Key == "extra_notes").Incoming.Value);
        var unknown = PersonalComparison.Play(null, false, null, false, incoming); Assert.All(unknown, r => { Assert.False(r.Original.Known); Assert.False(r.Current.Known); Assert.False(r.RemoteChanged); });
        Assert.All(PersonalComparison.Play(original, true, null, true, incoming), r => Assert.False(r.Current.Exists));
    }
    [Fact]
    public void ConflictingReviewAndAssociationRemainReadOnlyAndLongTextStartsFolded()
    {
        var original = Details(); var incoming = original with { Review = new('x', 1000), NovelId = "v18" };
        var rows = PersonalComparison.Play(original, true, original with { Review = "Web", NovelId = "v19" }, true, incoming);
        Assert.All(rows, r => Assert.True(r.Conflict)); Assert.False(new FieldComparisonDisplay(rows.Single(r => r.Key == "review")).Expanded);
        Assert.Equal("v17", original.NovelId); Assert.Equal("original", original.Review);
    }
    [Fact]
    public async Task ConflictIsDurableWhenFollowupReadFailsAndCanBeInspectedWithoutChangingTheDraft()
    {
        using var f = new UiRefreshTests.Fixture(); var original = f.Store.ReadPersonalPlay(f.Play.Id)!;
        var draft = await f.Outbox.EnqueueAsync(f.Item.Id, f.Origin, original.Id, new(original.Details with { Review = "My version", DurationMinutes = 0 }, f.Item.Id, original.Revision), Ct, original);
        f.Store.SavePersonalPlay(new(original.Details with { Review = "Web version" }, f.Item.Id, original.Revision), original.Id, "k6-web-edit");
        var file = Path.Combine(f.Outbox.DirectoryPath, draft.Id, "draft.json"); var bytes = await File.ReadAllBytesAsync(file, Ct);
        f.Traffic.FailReadsAfterConflict = true;
        Assert.Equal(409, (await Assert.ThrowsAsync<ClientApiException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct))).Status);
        var inspection = await new PersonalEditOutbox(Path.Combine(f.Dir.Path, "client-json")).InspectAsync(draft, f.Origin, ct: Ct);
        Assert.Equal(PersonalOperationState.Conflict, inspection.State); Assert.False(inspection.CurrentKnown); Assert.True(inspection.Draft.OriginalKnown); Assert.True(inspection.ReadFailed);
        f.Traffic.ReadUnavailable = false;
        inspection = await f.Outbox.InspectAsync(draft, f.Origin, f.Web, true, Ct);
        Assert.True(inspection.CurrentKnown); Assert.True(inspection.Differences.Single(r => r.Key == "review").Conflict); Assert.Equal("Web version", inspection.Current!.Details.Review);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct)); Assert.Single(f.Traffic.Writes);
    }
    [Fact]
    public async Task MatchingReadAfterLostReplyDoesNotPretendToBeAReceiptOrCreateANewOperation()
    {
        using var f = new UiRefreshTests.Fixture(); var original = f.Store.ReadPersonalPlay(f.Play.Id)!;
        var draft = await f.Outbox.EnqueueAsync(f.Item.Id, f.Origin, original.Id, new(original.Details with { Review = "Offline review" }, f.Item.Id, original.Revision), Ct, original);
        f.Traffic.LoseNextWrite = true; await Assert.ThrowsAsync<WebUnavailableException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        var inspection = await f.Outbox.InspectAsync(draft, f.Origin, f.Web, true, Ct);
        Assert.Equal(PersonalOperationState.Uncertain, inspection.State); Assert.Equal(draft.Request.Details, inspection.Current!.Details);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Outbox.WithdrawConflictAsync(draft, Ct));
        await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        Assert.Equal(f.Traffic.Writes[0], f.Traffic.Writes[1]);
        Assert.Equal(PersonalOperationState.Applied, (await f.Outbox.InspectAsync(draft, f.Origin, ct: Ct)).State);
        Assert.DoesNotContain("original_known", f.Traffic.Writes[0].Body); Assert.DoesNotContain("original", JsonDocument.Parse(f.Traffic.Writes[0].Body).RootElement.EnumerateObject().Select(p => p.Name));
    }
    [Fact]
    public async Task LostReceiptIsRecoveredWithTheOriginalKeyEvenWhenTheParentItemWasRemovedAfterCommit()
    {
        using var f = new UiRefreshTests.Fixture(); var original = f.Store.ReadPersonalPlay(f.Play.Id)!;
        var draft = await f.Outbox.EnqueueAsync(f.Item.Id, f.Origin, original.Id, new(original.Details with { Review = "Saved before item removal" }, f.Item.Id, original.Revision), Ct, original);
        f.Traffic.LoseNextWrite = true;
        await Assert.ThrowsAsync<WebUnavailableException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        f.Store.SetItemRemoved(f.Item.Id, new(f.Item.Revision), true, "k6-remove-after-lost-reply");
        var saved = await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        Assert.Equal(draft.Request.Details, saved.Details); Assert.Equal(f.Traffic.Writes[0], f.Traffic.Writes[1]);
        Assert.Equal(PersonalOperationState.Applied, (await f.Outbox.InspectAsync(draft, f.Origin, ct: Ct)).State);
    }
    [Fact]
    public async Task PreK6DraftAndReceiptHashStayCompatibleAndDoNotInventAnOriginalValue()
    {
        using var f = new UiRefreshTests.Fixture(); var original = f.Store.ReadPersonalPlay(f.Play.Id)!; var id = Guid.NewGuid().ToString("D");
        var legacy = new { id, book_key = f.Item.Id, origin = f.Origin, play_id = original.Id, request = new PersonalPlayWrite(original.Details with { Notes = "Legacy pending" }, f.Item.Id, original.Revision), created_at = DateTimeOffset.UtcNow };
        var file = Path.Combine(f.Outbox.DirectoryPath, id, "draft.json"); await JsonSnapshotStore.AtomicWriteAsync(file, legacy, false, Ct);
        var draft = Assert.Single(await f.Outbox.PendingAsync(f.Origin, Ct)); Assert.False(draft.OriginalKnown); Assert.Null(draft.Original);
        Assert.Equal(LocalArchiveFormat.Hash(legacy), LocalArchiveFormat.Hash(draft));
        var bytes = await File.ReadAllBytesAsync(file, Ct); await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        Assert.Equal(PersonalOperationState.Applied, (await f.Outbox.InspectAsync(draft, f.Origin, ct: Ct)).State); Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
    }
    [Fact]
    public async Task ReadOnlyInspectionAndRetryRejectAnAlteredReceiptBindingWithoutSendingAWritingRequest()
    {
        using var f = new UiRefreshTests.Fixture(); var draft = await f.EditAsync();
        await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        var file = Path.Combine(f.Outbox.DirectoryPath, draft.Id, "receipt.json");
        var receipt = JsonSerializer.Deserialize<PersonalEditReceipt>(await File.ReadAllTextAsync(file, Ct), VndbJson.Options)!;
        await JsonSnapshotStore.AtomicWriteAsync(file, receipt with { Binding = receipt.Binding with { ServiceInstanceId = "altered-instance" } }, true, Ct);
        var bytes = await File.ReadAllBytesAsync(file, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Outbox.InspectAsync(draft, f.Origin, ct: Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        Assert.Single(f.Traffic.Writes); Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
    }
    [Fact]
    public async Task AuthenticationFailureAfterALostReplyCannotEraseTheUncertainState()
    {
        using var f = new UiRefreshTests.Fixture(); var draft = await f.EditAsync();
        f.Traffic.LoseNextWrite = true; await Assert.ThrowsAsync<WebUnavailableException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        f.Traffic.UnauthorizedWrites = true; Assert.Equal(401, (await Assert.ThrowsAsync<ClientApiException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct))).Status);
        Assert.Equal(PersonalOperationState.Uncertain, (await f.Outbox.InspectAsync(draft, f.Origin, ct: Ct)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Outbox.WithdrawConflictAsync(draft, Ct));
    }
    [Fact]
    public async Task ClassificationReviewDoesNotClearConflictOrChangeQueueBytes()
    {
        using var f = new UiRefreshTests.Fixture(); var store = new PersonalOrganizationStore(Path.Combine(f.Dir.Path, "organization"), f.Origin);
        await store.RefreshAsync(f.Web, ct: Ct);
        await store.EnqueueAsync(new(0, new("same-label", "My label", "collection")), Ct);
        f.Store.SavePersonalOrganization(new(0, new("same-label", "Web label", "collection")), "web-label");
        Assert.Equal(1, await store.SyncAsync(f.Web, Ct));
        var file = Path.Combine(f.Dir.Path, "organization", "personal-organization.json"); var bytes = await File.ReadAllBytesAsync(file, Ct);
        var review = await store.InspectAsync(f.Web, true, Ct); Assert.Equal(PersonalOperationState.Conflict, review.State);
        var name = review.Differences.Single(r => r.Key.EndsWith(":name", StringComparison.Ordinal)); Assert.Equal("My label", name.Incoming.Value); Assert.Equal("Web label", name.Current.Value);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct)); Assert.Single((await store.ReadAsync(Ct)).Pending);
    }
    [Fact]
    public async Task StartupTraceUsesAllowlistedFactsAndCannotOverwriteAnExportTarget()
    {
        using var dir = new TestDirectory(); var trace = new StartupDiagnostics(); trace.Begin();
        var privateMessage = Path.Combine(dir.Path, "synthetic-private-value");
        await Assert.ThrowsAsync<IOException>(() => trace.RunAsync(StartupStage.Shelf, _ => throw new IOException(privateMessage), Ct));
        var report = trace.Snapshot(); Assert.Equal("io", Assert.Single(report.Steps).ErrorCode);
        var text = JsonSerializer.Serialize(report, VndbJson.Options); Assert.DoesNotContain(privateMessage, text); Assert.DoesNotContain(dir.Path, text); Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(report, VndbJson.Options)));
        var file = Path.Combine(dir.Path, "diagnostic-export.json"); await trace.SaveAsync(file, Ct); var bytes = await File.ReadAllBytesAsync(file, Ct);
        await Assert.ThrowsAsync<IOException>(() => trace.SaveAsync(file, Ct)); Assert.Equal(bytes, await File.ReadAllBytesAsync(file, Ct));
        trace.Begin(); Assert.Empty(trace.Snapshot().Steps); Assert.False(trace.Failed);
    }
    [Fact]
    public async Task StartupCancellationAndUnknownExceptionsAreClassifiedWithoutRawMessages()
    {
        var trace = new StartupDiagnostics(); trace.Begin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trace.RunAsync(StartupStage.Configuration, _ => throw new OperationCanceledException("Synthetic private cancellation"), Ct));
        Assert.Equal("cancelled", trace.Snapshot().Steps[0].ErrorCode);
        trace.Begin(); await Assert.ThrowsAsync<NotSupportedException>(() => trace.RunAsync(StartupStage.Covers, _ => throw new NotSupportedException("Synthetic private message"), Ct));
        Assert.Equal("unexpected", trace.Snapshot().Steps[0].ErrorCode); Assert.DoesNotContain("private message", JsonSerializer.Serialize(trace.Snapshot()));
    }
    [Fact]
    public async Task FailedStartupCanBeRetriedWithoutResettingTheDataOrLosingSessionTimerNotification()
    {
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Path, "local-library.json");
        await JsonSnapshotStore.AtomicWriteAsync(file, new LocalLibrary([], [], FormatVersion: 99), false, Ct); var original = await File.ReadAllBytesAsync(file, Ct);
        using var services = new DesktopServices(dir.Path); var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services); var outcomes = new List<bool>(); vm.StartupFinished += outcomes.Add;
        try
        {
            Assert.False(await vm.StartAsync(() => { })); Assert.True(vm.RetryStartupCommand.CanExecute(null)); Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
            Assert.Equal(StartupStage.Configuration, Assert.Single(vm.Diagnostics.Trace.Snapshot().Steps).Stage);
            await JsonSnapshotStore.AtomicWriteAsync(file, new LocalLibrary([], [M5Tests.Item()]), true, Ct); await vm.RetryStartupCommand.ExecuteAsync(null);
            Assert.False(vm.Diagnostics.Trace.Failed); Assert.Equal(4, vm.Diagnostics.Trace.Snapshot().Steps.Count); Assert.Equal(new[] { false, true }, outcomes); Assert.NotNull(services.Sessions);
            Assert.True(new FileInfo(Path.Combine(dir.Path, "startup-diagnostics.json")).Length < 65536);
        }
        finally { if (services.ActivityHost is { } host) await host.DisposeAsync(); }
    }
    [Fact]
    public async Task UnexpectedStartupFailureRemainsARecoverableWindowState()
    {
        using var dir = new TestDirectory(); using var services = new DesktopServices(dir.Path); var library = new BrokenLibrary();
        var vm = new MainViewModel(library, services.Launcher, new Dialogs(), services);
        Assert.False(await vm.StartAsync(() => { })); Assert.False(vm.Busy); Assert.Null(services.Sessions);
        Assert.Equal("unexpected", vm.Diagnostics.Trace.Snapshot().Steps.Last().ErrorCode); Assert.DoesNotContain("private unexpected", vm.Status);
    }
    [Fact]
    public async Task RealDesktopCompositionUsesTenThousandSummariesAndCanRestartOfflineWithUnchangedCache()
    {
        using var dir = new TestDirectory(); const string origin = "http://k6-startup.test/";
        await new LocalClientSettingsStore(dir.Path).SaveAsync(new(Server: origin), Ct);
        using var handler = new ModernLibrary(); string path; byte[] before;
        using (var http = new HttpClient(handler, false))
        using (var services = new DesktopServices(dir.Path, http))
        {
            var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services);
            try
            {
                Assert.True(await vm.StartAsync(() => { })); Assert.Contains("10000", vm.Status); Assert.Equal(60, vm.Shelf.Books.Count); Assert.Equal(0, handler.PlayReads);
                path = Path.Combine(services.StateDirectory, "shelf.json"); Assert.InRange(new FileInfo(path).Length, 10000, 64 * 1024 * 1024);
                Assert.True(handler.MaximumShelfResponse < 8 * 1024 * 1024); before = await File.ReadAllBytesAsync(path, Ct);
                Assert.Equal(4, vm.Diagnostics.Trace.Snapshot().Steps.Count); Assert.DoesNotContain(dir.Path, JsonSerializer.Serialize(vm.Diagnostics.Trace.Snapshot()));
            }
            finally { if (services.ActivityHost is { } host) await host.DisposeAsync(); }
        }
        handler.Offline = true;
        using (var offlineHttp = new HttpClient(handler, false))
        using (var offlineServices = new DesktopServices(dir.Path, offlineHttp))
        {
            var offline = new MainViewModel(offlineServices.Library, offlineServices.Launcher, new Dialogs(), offlineServices);
            try { Assert.True(await offline.StartAsync(() => { })); Assert.Contains("10000", offline.Status); Assert.Equal(before, await File.ReadAllBytesAsync(path, Ct)); Assert.Equal(0, handler.PlayReads); }
            finally { if (offlineServices.ActivityHost is { } host) await host.DisposeAsync(); }
        }
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title) => Task.FromResult<string?>(null);
    }
    private sealed class BrokenLibrary : IDesktopLibrary
    {
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => throw new NotSupportedException("Synthetic private unexpected");
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ModernLibrary : HttpMessageHandler
    {
        public bool Offline; public int PlayReads; public int MaximumShelfResponse;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("Synthetic offline");
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query); object value;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/api/v1/info": value = new ServiceInfo("synthetic-instance", [1], "test", 14, false, ["shelf_summary_v1", "item_heads_v1"], [1], true); break;
                case "/api/v1/shelf":
                    var page = int.Parse(query["page"]!);
                    value = query["deleted"] == "true" ? new ShelfSummaryPage([], page, 100, 0, false) : new ShelfSummaryPage(
                        Enumerable.Range((page - 1) * 100 + 1, 100).Select(n => { var item = M5Tests.Item(n) with { PlayCount = 0, State = new(PersonalProgress.FromStatuses([]), false) }; return ShelfSummaries.Item(item); }).ToArray(), page, 100, 10000, page < 100); break;
                case "/api/v1/plays": PlayReads++; value = Array.Empty<PersonalPlayView>(); break;
                case "/api/v1/item-heads": value = new ItemHeadPage([], 1, 100, 0, false); break;
                default: return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new ApiProblem("not_found", "Synthetic missing", 404), options: VndbJson.Options) });
            }
            if (value is ShelfSummaryPage) MaximumShelfResponse = Math.Max(MaximumShelfResponse, JsonSerializer.SerializeToUtf8Bytes(value, VndbJson.Options).Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: VndbJson.Options) });
        }
    }
}
