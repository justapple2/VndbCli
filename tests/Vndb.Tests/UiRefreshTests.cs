using Vndb.Local;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;
public sealed class UiRefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", path).UseSetting("Library:WriteKey", "ui-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Traffic : DelegatingHandler
    {
        public bool LoseNextWrite { get; set; }
        public bool UnauthorizedWrites { get; set; }
        public string? InstanceOverride { get; set; }
        public List<(string Body, string Key)> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var write = request.Method is { } method && (method == HttpMethod.Post || method == HttpMethod.Patch) && request.RequestUri!.AbsolutePath.StartsWith("/api/v1/plays", StringComparison.Ordinal);
            if (write)
            {
                Writes.Add((await request.Content!.ReadAsStringAsync(ct), request.Headers.GetValues("Idempotency-Key").Single()));
                if (UnauthorizedWrites) return new(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new ApiProblem("unauthorized", "synthetic", 401)) };
            }
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/api/v1/info" && InstanceOverride is not null)
            {
                var info = (await response.Content.ReadFromJsonAsync<ServiceInfo>(VndbJson.Options, ct))!;
                response.Content.Dispose(); response.Content = JsonContent.Create(info with { ServiceInstanceId = InstanceOverride }, options: VndbJson.Options);
            }
            if (write && LoseNextWrite) { LoseNextWrite = false; response.Dispose(); throw new HttpRequestException("synthetic lost response after commit"); }
            return response;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestDirectory Dir = new();
        public readonly Factory App;
        public readonly Traffic Traffic = new();
        public readonly HttpClient Http;
        public readonly WebLibraryClient Web;
        public readonly SqliteLibraryStore Store;
        public readonly LibraryItem Item;
        public readonly PlayRecord Play;
        public readonly PersonalEditOutbox Outbox;
        public Fixture()
        {
            App = new(Dir.Path); Http = App.CreateDefaultClient(Traffic); Web = new(Http, Http.BaseAddress!, "ui-test-key", TimeSpan.FromSeconds(5));
            Store = App.Services.GetRequiredService<SqliteLibraryStore>(); Store.ImportPlays(PlayTests.Batch());
            var original = Store.Plays().Results[0];
            Item = Store.SaveItem(new(new(original.Details.Title, original.Details.NovelId)), null, "ui-item");
            Play = Store.SavePersonalPlay(new(Store.PlayView(original).Details, Item.Id, original.Revision), original.Id, "ui-link");
            Outbox = new(Path.Combine(Dir.Path, "client-json"));
        }
        public string Origin => Http.BaseAddress!.AbsoluteUri;
        public Task<PersonalEditDraft> EditAsync(int? minutes = 90) => Outbox.EnqueueAsync(Item.Id, Origin, Play.Id,
            new(Store.PlayView(Play).Details with { DurationMinutes = minutes }, ExpectedRevision: Play.Revision), Ct);
        public void Dispose() { Http.Dispose(); App.Dispose(); Dir.Dispose(); }
    }
    [Fact]
    public async Task ManualTimeUsesExistingCrudAndPreservesWorkbookReviewSessionsAndReplay()
    {
        using var f = new Fixture();
        var original = f.Store.GetPlay(f.Play.Id)!; var replay = f.Store.Plays().Results.Single(p => p.Id != f.Play.Id);
        var device = f.Store.SaveDevice(new(Guid.NewGuid().ToString("D"), new("Synthetic", "windows")), "ui-device");
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var session = f.Store.UpsertSession(new(Guid.NewGuid().ToString("D"), device.Id, original.Id, null, null, start, start.AddMinutes(2), start.AddMinutes(2), 1, 120, "ended"));
        var adjustment = f.Store.AdjustTime(new(Guid.NewGuid().ToString("D"), original.Id, 60, "synthetic correction", f.Store.PlayTime(original.Id).Revision), "ui-adjust");
        var draft = await f.EditAsync(); var saved = await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        Assert.Equal(90, saved.Details.DurationMinutes); Assert.Equal(original.Revision + 1, saved.Revision);
        var after = f.Store.GetPlay(original.Id)!; Assert.Equivalent(original.Source, after.Source);
        Assert.Equal(original.Details.Review, after.Details.Review); Assert.Equal(original.Details.Score, after.Details.Score);
        Assert.Equal(original.Details.Notes, after.Details.Notes); Assert.Equal(original.Details.ExtraNotes, after.Details.ExtraNotes); Assert.Equal(original.Details.Language, after.Details.Language);
        Assert.Equal(5580, f.Store.PlayTime(original.Id).TotalSeconds); Assert.Equal(session, Assert.Single(f.Store.Sessions()));
        Assert.Equal(60, f.Store.PlayTime(original.Id).AdjustmentSeconds); Assert.Equivalent(replay, f.Store.GetPlay(replay.Id));
        Assert.Empty(await f.Outbox.PendingAsync(f.Origin, Ct)); Assert.DoesNotContain("client-json", Assert.Single(f.Traffic.Writes).Body);
        Assert.DoesNotContain("installation", Assert.Single(f.Traffic.Writes).Body);
    }
    [Fact]
    public async Task LostResponseAndRestartRetrySameIdWithoutAddingTimeTwice()
    {
        using var f = new Fixture(); var draft = await f.EditAsync(); f.Traffic.LoseNextWrite = true;
        await Assert.ThrowsAsync<WebUnavailableException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        var revision = f.Store.GetPlay(f.Play.Id)!.Revision;
        var restarted = new PersonalEditOutbox(Path.Combine(f.Dir.Path, "client-json")); var pending = Assert.Single(await restarted.PendingAsync(f.Origin, Ct));
        var saved = await restarted.SyncOneAsync(f.Web, f.Origin, pending, Ct);
        Assert.Equal(revision, saved.Revision); Assert.Equal(revision, f.Store.GetPlay(f.Play.Id)!.Revision);
        Assert.Equal(f.Traffic.Writes[0], f.Traffic.Writes[1]); Assert.Empty(await restarted.PendingAsync(f.Origin, Ct));
        await restarted.SyncOneAsync(f.Web, f.Origin, pending, Ct); Assert.Equal(2, f.Traffic.Writes.Count);
    }
    [Fact]
    public async Task ConflictRetainsBothVersionsAndRequiresExplicitReload()
    {
        using var f = new Fixture(); var draft = await f.EditAsync();
        var current = f.Store.SavePersonalPlay(new(f.Store.PlayView(f.Play).Details with { Review = "new Web review" }, ExpectedRevision: f.Play.Revision), f.Play.Id, "ui-external");
        Assert.Equal(409, (await Assert.ThrowsAsync<ClientApiException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct))).Status);
        var folder = Path.Combine(f.Outbox.DirectoryPath, draft.Id); Assert.True(File.Exists(Path.Combine(folder, "conflict.json")));
        var conflict = await File.ReadAllTextAsync(Path.Combine(folder, "conflict.json"), Ct); Assert.Contains("new Web review", conflict); Assert.Contains("90", conflict);
        await f.Outbox.SyncAsync(f.Web, f.Origin, Ct); Assert.Single(f.Traffic.Writes); Assert.Equivalent(current, f.Store.GetPlay(f.Play.Id));
        await f.Outbox.WithdrawConflictAsync(draft, Ct); Assert.True(File.Exists(Path.Combine(folder, "draft.json"))); Assert.Empty(await f.Outbox.PendingAsync(f.Origin, Ct));
    }
    [Fact]
    public async Task ResultUnknownCannotBeWithdrawnAndChangedInstanceCannotReceiveRetry()
    {
        using var f = new Fixture(); var draft = await f.EditAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Outbox.WithdrawConflictAsync(draft, Ct));
        f.Traffic.LoseNextWrite = true; await Assert.ThrowsAsync<WebUnavailableException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct));
        f.Traffic.InstanceOverride = "different-instance";
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct)); Assert.Single(f.Traffic.Writes);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Outbox.SyncOneAsync(f.Web, "http://another.local/", draft, Ct));
    }
    [Fact]
    public async Task NewReplayIsExplicitAndIdempotentAndUnknownTimeStaysUnknown()
    {
        using var f = new Fixture(); var before = f.Store.Plays().Total;
        var details = f.Store.PlayView(f.Play).Details with { StartedOn = null, FinishedOn = null, DurationMinutes = null, Status = "playing" };
        var draft = await f.Outbox.EnqueueAsync(f.Item.Id, f.Origin, null, new(details, f.Item.Id), Ct);
        var saved = await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct); await f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct);
        Assert.Equal(before + 1, f.Store.Plays().Total); Assert.NotEqual(f.Play.Id, saved.Id); Assert.Null(saved.Details.DurationMinutes);
        Assert.Contains(f.Store.ItemPlays(f.Item.Id), p => p.Id == saved.Id);
    }
    [Fact]
    public async Task UnauthorizedWritesRemainPendingAndNeverUseAnotherService()
    {
        using var f = new Fixture(); var draft = await f.EditAsync(); f.Traffic.UnauthorizedWrites = true;
        Assert.Equal(401, (await Assert.ThrowsAsync<ClientApiException>(() => f.Outbox.SyncOneAsync(f.Web, f.Origin, draft, Ct))).Status);
        Assert.Single(await f.Outbox.PendingAsync(f.Origin, Ct)); Assert.Null(f.Store.GetPlay(f.Play.Id)!.Details.DurationMinutes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.EditAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Outbox.EnqueueAsync("synthetic", f.Origin, null, new(f.Store.PlayView(f.Play).Details with { Review = @"E:\Synthetic\Private" }, f.Item.Id), Ct));
    }
    private sealed class EditorService(IReadOnlyList<PersonalPlayView> plays) : IQuickPlayService
    {
        public int Queued { get; private set; }
        public bool Offline { get; set; }
        public PersonalEditDraft? Pending { get; private set; }
        public Task<IReadOnlyList<PersonalPlayView>> LoadAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult(plays);
        public Task<PersonalEditDraft?> PendingAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult(Pending);
        public Task<PersonalEditDraft> QueueAsync(QuickPlayBook book, PersonalPlayView? play, PersonalPlayData details, CancellationToken ct)
        { Queued++; Pending = new(Guid.NewGuid().ToString("D"), book.Id, "http://test.local/", play?.Id, new(details, book.Item?.Item.Id, play?.Revision), DateTimeOffset.UtcNow); return Task.FromResult(Pending); }
        public Task<PersonalPlayView> SyncAsync(PersonalEditDraft draft, CancellationToken ct) => Offline ? throw new WebUnavailableException() : Task.FromResult(new PersonalPlayView(draft.PlayId ?? "new-play", draft.Request.Details, (draft.Request.ExpectedRevision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        public Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct) => throw new NotSupportedException();
    }
    private static PersonalPlayView Play(int n = 1) => new("play-" + n, new("Synthetic", null, "playing", null, null, null, 8.5m, "review preserved", "note one", "note two", "ja"), 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    [Fact]
    public async Task QuickEditorRequiresReplaySelectionAndChangesOnlyExplicitPersonalFields()
    {
        var book = new BookViewModel(M5Tests.Item(), null); var service = new EditorService([Play(), Play(2)]);
        var vm = new QuickPlayEditorViewModel(book, service, () => { }, () => Task.CompletedTask); await vm.InitializeAsync(Ct);
        Assert.False(vm.HasSelection); Assert.False(vm.SaveCommand.CanExecute(null));
        vm.SelectedPlay = vm.Plays[1]; Assert.True(vm.HasSelection); Assert.Null(vm.BuildDetails().DurationMinutes);
        vm.DurationKnown = true; vm.Hours = 2; vm.Minutes = 15; vm.SelectedState = vm.States.Single(s => s.Code == "completed");
        vm.StartedOn = new(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)); vm.FinishedOn = new(2026, 1, 3, 0, 0, 0, TimeSpan.FromHours(8));
        var details = vm.BuildDetails(); Assert.Equal(135, details.DurationMinutes); Assert.Equal(new DateOnly(2026, 1, 1), details.StartedOn);
        Assert.Equal("note two", details.ExtraNotes); Assert.Equal("review preserved", details.Review); Assert.Equal(8.5m, details.Score);
        Assert.Equal(0, service.Queued);
        vm.DurationKnown = false; Assert.Null(vm.BuildDetails().DurationMinutes);
    }
    [Fact]
    public async Task EditorValidatesDateStatusAndMinutesBeforeQueueing()
    {
        var vm = new QuickPlayEditorViewModel(new(M5Tests.Item(), null), new EditorService([Play()]), () => { }, () => Task.CompletedTask);
        await vm.InitializeAsync(Ct); vm.DurationKnown = true; vm.Hours = 1; vm.Minutes = 60;
        Assert.Throws<ArgumentException>(() => vm.BuildDetails()); vm.Minutes = 0; vm.Hours = .5m; Assert.Throws<ArgumentException>(() => vm.BuildDetails());
        vm.Hours = 1; vm.SelectedState = vm.States[0]; Assert.Throws<ArgumentException>(() => vm.BuildDetails());
        vm.SelectedState = vm.States[1]; vm.StartedOn = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero); vm.FinishedOn = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => vm.BuildDetails());
    }
    [Fact]
    public async Task EditorOfflineRetryKeepsOneDraftAndNeverQueuesAgain()
    {
        var service = new EditorService([Play()]) { Offline = true }; var saved = 0;
        var vm = new QuickPlayEditorViewModel(new(M5Tests.Item(), null), service, () => { }, () => { saved++; return Task.CompletedTask; });
        await vm.InitializeAsync(Ct); vm.DurationKnown = true; vm.Hours = 1; await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasDraft); Assert.Contains("不可达", vm.Message); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.Equal(1, service.Queued);
        service.Offline = false; await vm.RetryCommand.ExecuteAsync(null); Assert.True(vm.Applied); Assert.Equal(1, service.Queued); Assert.Equal(1, saved);
    }
    [Fact]
    public async Task AppearanceSettingsAreLocalJsonAndInvalidThemeFallsBack()
    {
        using var dir = new TestDirectory(); var store = new UiPreferencesStore(dir.Path);
        await store.SaveAsync(new("Dark", true), Ct); Assert.Equal(new UiPreferences("Dark", true), await new UiPreferencesStore(dir.Path).ReadAsync(Ct));
        await store.SaveAsync(new("unknown", true), Ct); Assert.Equal("Default", (await store.ReadAsync(Ct)).Theme);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.db", SearchOption.AllDirectories));
    }
}
