using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ObservationDesktopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
    [Fact]
    public async Task ExplicitSettingsAndUiRefreshKeepLaunchDraftAndPersonalProgressWhileDisablingMissingEntry()
    {
        using var f = new ObservationTests.Fixture(); await f.InitializeAsync();
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(f.Data, "device.json"), new ClientDeviceIdentity(f.Device, "Synthetic", "windows", f.Clock.GetUtcNow()), false, Ct);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(f.Data, "launch-profiles.json"), new[] { f.Profile }, false, Ct);
        var watches = new ObservationHostTests.Watches();
        using var services = new DesktopServices(f.Data, observationFiles: f.Files, observationWatches: watches, observationTime: f.Clock);
        var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services);
        await vm.InitializeAsync(); await vm.RefreshCommand.ExecuteAsync(null); var book = Assert.Single(vm.Shelf.Books); var progress = book.Progress.State;
        Assert.Null(services.ObservationHost); vm.Observation.RootPath = f.Root;
        await vm.Observation.AddRootCommand.ExecuteAsync(null); vm.Observation.Enabled = true;
        await vm.Observation.SaveCommand.ExecuteAsync(null); Assert.Null(services.ObservationHost); Assert.Contains("请先核对", vm.Status);
        vm.Observation.Approved = true; await vm.Observation.SaveCommand.ExecuteAsync(null); Assert.NotNull(services.ObservationHost);
        await services.CheckObservationAsync(Ct); vm.UpdateSessionStatus();
        vm.ConfigureBookCommand.Execute(book); vm.ArgumentsText = "--unsaved-draft"; Assert.True(vm.LaunchDraftChanged);
        var component = vm.ComponentId; f.Files.RemoveTree(f.Exe);
        await services.CheckObservationAsync(Ct); vm.UpdateSessionStatus();
        Assert.False(book.CanLaunchObserved); Assert.True(book.HasInstallation); Assert.Equal(progress, book.Progress.State);
        Assert.Equal("--unsaved-draft", vm.ArgumentsText); Assert.Equal(component, vm.ComponentId); Assert.NotNull(vm.SelectedProfile);
        Assert.Contains("启动入口", book.LocalObservation); Assert.Contains("元数据检查", vm.Observation.Summary);
        vm.Observation.Enabled = false; vm.Observation.Approved = true; await vm.Observation.SaveCommand.ExecuteAsync(null);
        Assert.Null(services.ObservationHost); Assert.Equal(watches.Created, watches.Disposed); Assert.True(File.Exists(Path.Combine(f.Data, "launch-profiles.json")));
    }
}
