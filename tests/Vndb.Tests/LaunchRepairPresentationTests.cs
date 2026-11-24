using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class LaunchRepairPresentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneFeatureInvalidatesApprovalsAndCannotApplyALateCompletionToAnotherSelection(bool selectionChanges)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var data = Path.Combine(dir.Path, "client");
        var old = Directory.CreateDirectory(Path.Combine(dir.Path, "old")).FullName; var target = Path.Combine(dir.Path, "new");
        await File.WriteAllTextAsync(Path.Combine(old, "game.exe"), "synthetic executable, never launched", Ct);
        var launcher = new LocalLauncher(data); var profile = await launcher.SaveAsync("Synthetic", Path.Combine(old, "game.exe"), [], old, "install", "component", ct: Ct);
        Directory.Move(old, target); var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallationView(new("install", new(device.Id, target, "Synthetic"), "present", now, 1, now, now), [new("component", "install", new("item", "."), 1, now, now)]);
        await new LocalLibraryStore(data).SaveAsync(new([installation], []), Ct);
        var context = new LaunchRepairContext(installation, "component", "本篇", profile, false, old); var status = "";
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var feature = new LaunchRepairViewModel(() => context, () => new(data, device), () => new(data), launcher, action => action(Ct), (_, _) => { },
            async (_, _) => { updated.SetResult(); await release.Task; }, _ => throw new InvalidOperationException("No directory operation expected"), text => status = text);
        await feature.InspectCommand.ExecuteAsync(null); Assert.Contains("Synthetic", feature.Context); Assert.Contains("工作目录", feature.Summary);
        await feature.PreviewPathCommand.ExecuteAsync(null); Assert.True(feature.HasPreview); Assert.False(feature.ApplyCommand.CanExecute(null));
        feature.Approved = true; Assert.True(feature.ApplyCommand.CanExecute(null));
        context = context with { DraftChanged = true }; feature.Invalidate(); Assert.False(feature.HasPreview); Assert.False(feature.Approved);
        await Assert.ThrowsAsync<ArgumentException>(() => feature.PreviewPathCommand.ExecuteAsync(null));
        context = context with { DraftChanged = false }; await feature.PreviewPathCommand.ExecuteAsync(null); feature.Approved = true;
        var applying = feature.ApplyCommand.ExecuteAsync(null); await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        context = selectionChanges ? context with { ComponentId = "other", ComponentTitle = "別版", Profile = null } :
            context with { Profile = Assert.Single(await launcher.ReadAsync(Ct)) };
        feature.Invalidate(); release.SetResult(); await applying;
        Assert.False(feature.HasPreview);
        if (selectionChanges) { Assert.Contains("別版", feature.Context); Assert.Contains("选择本机安装", feature.Summary); Assert.Contains("当前选择已变化", status); }
        else { Assert.Contains("已应用", feature.Summary); Assert.Contains("保存到本机", feature.NextStep); }
        Assert.Equal(Path.Combine(target, "game.exe"), Assert.Single(await launcher.ReadAsync(Ct)).Executable);
    }
}
