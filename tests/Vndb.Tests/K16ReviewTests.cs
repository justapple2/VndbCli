using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class K16ReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClassificationCommandsCannotReuseAnotherServicesLoadedState()
    {
        using var a = new TestDirectory(); using var b = new TestDirectory();
        var first = new PersonalOrganizationStore(a.Path, "http://first.test/");
        var second = new PersonalOrganizationStore(b.Path, "http://second.test/");
        await first.EnqueueAsync(new(0, new("a", "First", "collection")), Ct);
        await second.EnqueueAsync(new(0, new("b", "Second", "collection")), Ct);
        var current = first;
        var vm = new PersonalOrganizationViewModel(() => current, () => null, () => [], _ => { }, () => { });
        await vm.LoadAsync(false, Ct); vm.Name = "Old scope draft";
        current = second;
        await vm.CreateLabelCommand.ExecuteAsync(null);
        Assert.Single((await second.ReadAsync(Ct)).Pending);
        Assert.Contains("读取", vm.Message);
        await vm.LoadAsync(false, Ct); vm.Name = "New scope draft";
        await vm.CreateLabelCommand.ExecuteAsync(null);
        Assert.Equal(2, (await second.ReadAsync(Ct)).Pending.Count);
    }

    [Fact]
    public async Task ObservationStartupFailureReleasesSessionAndAllowsAnActualRetry()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync();
        await new ObservationSettingsStore(f.Data, f.Files).SaveAsync(f.Settings, Ct);
        var watches = new FailingWatches();
        using var service = new DesktopServices(f.Data, observationFiles: f.Files, observationWatches: watches, observationTime: f.Clock);
        await service.InitializeAsync(Ct);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => service.StartSessionsAsync(Ct));
            Assert.Null(service.Sessions); Assert.Null(service.ActivityHost); Assert.Null(service.ObservationHost);
            await service.StartSessionsAsync(Ct);
            Assert.NotNull(service.Sessions); Assert.NotNull(service.ActivityHost);
            Assert.True(service.ObservationHost!.Running); Assert.True(watches.Attempts >= 2);
        }
        finally { if (service.ActivityHost is { } host) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task LateResourceFolderSelectionCannotPopulateAnotherBooksDraft()
    {
        using var dir = new TestDirectory();
        var picker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new ResourceArchiveViewModel(() => new(new(dir.Path), "synthetic-device"), () => { opened.SetResult(); return picker.Task; });
        vm.Select(new(M5Tests.Item(17), null, []));
        var operation = vm.ChooseRootCommand.ExecuteAsync(null); await opened.Task.WaitAsync(Ct);
        vm.Select(new(M5Tests.Item(18), null, [])); vm.RootPath = Path.Combine(dir.Path, "second");
        picker.SetResult(Path.Combine(dir.Path, "first")); await operation;
        Assert.Equal(Path.Combine(dir.Path, "second"), vm.RootPath);
    }

    [Fact]
    public void PersonalCompanyOverrideIsDisplayedButPublicDevelopersRemainFilterable()
    {
        var item = M5Tests.Item() with { Item = M5Tests.Item().Item with { Details = M5Tests.Item().Item.Details with { CompanyOverride = "Personal company" } } };
        var book = new BookViewModel(item, item.Metadata, []);
        Assert.StartsWith("Personal company", book.Company);
        Assert.Contains("Second", book.Company);
        Assert.Contains("Personal company", book.Companies);
        Assert.Contains(item.Metadata!.Developers[0].Original ?? item.Metadata.Developers[0].Name, book.Companies);
    }

    [Fact]
    public async Task SessionTargetReadFailureCanRetryWithoutCreatingASecondSessionHost()
    {
        using var dir = new TestDirectory(); using var service = new DesktopServices(dir.Path);
        await service.LocalLibrary.SaveAsync(new([], [M5Tests.Item()]), Ct);
        var targetFile = Path.Combine(service.Activities.DirectoryPath, Guid.NewGuid().ToString("D"), "target.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!); await File.WriteAllTextAsync(targetFile, "{", Ct);
        var vm = new MainViewModel(service.Library, service.Launcher, new Dialogs(), service);
        try
        {
            Assert.False(await vm.StartAsync(() => { }));
            var sessions = service.Sessions; var host = service.ActivityHost;
            Assert.NotNull(sessions); Assert.True(vm.RetryStartupCommand.CanExecute(null));
            // Explicitly repair only the synthetic malformed fixture, retaining the failed evidence in this test's assertion.
            Assert.Equal("{", await File.ReadAllTextAsync(targetFile, Ct)); File.Delete(targetFile);
            await vm.RetryStartupCommand.ExecuteAsync(null);
            Assert.False(vm.Diagnostics.Trace.Failed); Assert.Same(sessions, service.Sessions); Assert.Same(host, service.ActivityHost);
        }
        finally { if (service.ActivityHost is { } host) await host.DisposeAsync(); }
    }

    [Fact]
    public async Task RemovedObservationRootsCannotLeaveActionableCandidatesInTheUi()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync();
        f.Files.Put(Path.Combine(f.Company, "New-game"), true); await f.CheckAsync(); f.Clock.Advance(20);
        var result = await f.CheckAsync(); Assert.NotEmpty(result.Snapshot.Candidates);
        var vm = new ObservationFeatureViewModel(() => throw new NotSupportedException(), new Dialogs(), action => action(Ct), _ => throw new NotSupportedException());
        vm.Load(f.Settings); vm.Poll(result, "Synthetic"); Assert.NotEmpty(vm.Candidates); vm.SelectedCandidate = vm.Candidates[0];
        vm.Load(f.Settings with { Roots = [] }); vm.Poll(result, "Synthetic");
        Assert.Empty(vm.Candidates); Assert.Null(vm.SelectedCandidate);
    }

    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string title) => Task.FromResult<string?>(null);
    }

    private sealed class FailingWatches : IObservationWatchFactory
    {
        public int Attempts;
        public IDisposable Create(string path, Action<string?, bool> changed)
        {
            if (Interlocked.Increment(ref Attempts) == 1) throw new NotSupportedException("Synthetic startup failure");
            return new Handle();
        }
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
