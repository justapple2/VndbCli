using System.Text.Json;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

[Collection("Launch environment isolation")]
public sealed class LocaleLaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string ProfileId = "11111111-2222-3333-4444-555555555555";
    private static string Fake => Path.GetFullPath(Path.Combine(TestDirectory.ScratchRoot, "../build/Vndb.FakeGame/bin/Release/net10.0/Vndb.FakeGame.exe"));
    private static string Config(bool admin = false, bool suspended = false, string parameter = "") => $"""
        <LEConfig><Profiles><Profile Guid="{ProfileId}" Name="Synthetic Japanese"><Location>ja-JP</Location><RunAsAdmin>{admin.ToString().ToLowerInvariant()}</RunAsAdmin><RunWithSuspend>{suspended.ToString().ToLowerInvariant()}</RunWithSuspend><Parameter>{parameter}</Parameter></Profile></Profiles></LEConfig>
        """;
    private static async Task<(string Game, string Tool)> FixturesAsync(string path)
    {
        var game = Path.Combine(path, "game.exe"); var tool = Path.Combine(path, "LEProc.exe");
        foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(Fake)!, "Vndb.FakeGame.*").Where(p => !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            File.Copy(dependency, Path.Combine(path, Path.GetFileName(dependency)));
        File.Copy(Fake, game); File.Copy(Fake, tool); await File.WriteAllTextAsync(Path.Combine(path, "LEConfig.xml"), Config(), Ct);
        return (game, tool);
    }

    [Fact]
    public async Task LocaleArgumentsSurviveBothParsingStepsAndProfilesRemainOffline()
    {
        using var dir = new TestDirectory(); var (game, tool) = await FixturesAsync(dir.Path);
        var receipt = Path.Combine(dir.Path, "日文 空格.json"); var args = new[] { "--receipt", receipt, "space arg", "embedded\"quote", "trailing slash\\", "" };
        var launcher = new LocalLauncher(dir.Path);
        var profile = await launcher.SaveAsync("Synthetic", game, args, dir.Path, ct: Ct, locale: new(dir.Path, ProfileId));
        var reloaded = new LocalLauncher(dir.Path); Assert.Equal(tool, Assert.Single(await reloaded.ReadAsync(Ct)).Locale!.ToolPath);
        var start = await reloaded.PreviewStartAsync(profile.Id, Ct); Assert.Equal(tool, start.FileName); Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "-runas", ProfileId, game }, start.ArgumentList.Take(3));
        var launch = await reloaded.LaunchTrackedAsync(profile.Id, Ct); using var process = launch.Process;
        await process.WaitForExitAsync(Ct); Assert.Equal(0, process.ExitCode);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, Ct));
        Assert.Equal(args, result.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(dir.Path, result.RootElement.GetProperty("cwd").GetString());
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "LEConfig.xml"), Config(parameter: "changed"), Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => reloaded.PreviewStartAsync(profile.Id, Ct));
    }

    [Theory]
    [InlineData(true, false, "")]
    [InlineData(false, true, "")]
    [InlineData(false, false, "override")]
    public async Task UnsafeOrParameterOverridingLocaleDoesNotReplaceExistingProfile(bool admin, bool suspended, string parameter)
    {
        using var dir = new TestDirectory(); var (game, tool) = await FixturesAsync(dir.Path); var launcher = new LocalLauncher(dir.Path);
        var old = await launcher.SaveAsync("Direct", game, [], dir.Path, ct: Ct);
        var original = await File.ReadAllTextAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "LEConfig.xml"), Config(admin, suspended, parameter), Ct);
        Assert.False(Assert.Single(LocaleEmulator.Profiles(tool)).Usable);
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.SaveAsync("Changed", game, [], dir.Path, id: old.Id, ct: Ct, locale: new(tool, ProfileId)));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct));
    }

    [Fact]
    public async Task DiscoveryFiltersHelpersButManualExeAndSavedTrackingRemainAvailable()
    {
        using var dir = new TestDirectory(); var (game, _) = await FixturesAsync(dir.Path);
        foreach (var name in new[] { "config.exe", "setup.exe", "settings.exe", "uninstall.exe", "inst.exe", "エンジン設定.exe", "ファイル破損チェック.exe" }) File.Copy(Fake, Path.Combine(dir.Path, name));
        var candidates = GameExecutables.Discover(dir.Path); Assert.Contains(candidates, c => c.Path == game);
        Assert.DoesNotContain(candidates, c => Path.GetFileName(c.Path).Contains("config", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, candidates.Count); // game plus the synthetic LE tool; never auto-selected.
        var launcher = new LocalLauncher(dir.Path);
        var manual = await launcher.SaveAsync("Manual override", Path.Combine(dir.Path, "config.exe"), [], dir.Path, ct: Ct);
        await launcher.ConfigureTrackingAsync(manual.Id, game, 60, Ct);
        var changed = await launcher.SaveAsync("Edited title", manual.Executable, [], dir.Path, id: manual.Id, ct: Ct);
        Assert.Equal(game, changed.TrackedExecutable); Assert.Equal(60, changed.MatchTimeoutSeconds);
        var direct = await launcher.PreviewStartAsync(changed.Id, Ct); Assert.Equal(manual.Executable, direct.FileName);
    }

    [Fact]
    public async Task ExistingJsonWithoutLocaleLoadsAndDefaultToolSettingSurvivesRestart()
    {
        using var dir = new TestDirectory(); var (game, tool) = await FixturesAsync(dir.Path); var launcher = new LocalLauncher(dir.Path);
        var profile = await launcher.SaveAsync("Old direct", game, [], dir.Path, "installation-synthetic", "component-synthetic", ct: Ct);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct))!;
        node[0]!.AsObject().Remove("locale"); await File.WriteAllTextAsync(Path.Combine(dir.Path, "launch-profiles.json"), node.ToJsonString(), Ct);
        Assert.Null(Assert.Single(await new LocalLauncher(dir.Path).ReadAsync(Ct)).Locale);
        var settings = new LocalClientSettingsStore(dir.Path); await settings.SaveAsync(new(LocaleEmulatorPath: tool), Ct);
        Assert.Equal(tool, (await new LocalClientSettingsStore(dir.Path).LoadAsync(Ct)).LocaleEmulatorPath);
        using var services = new DesktopServices(dir.Path);
        var vm = new MainViewModel(services.Library, services.Launcher, new Dialogs(), services); await vm.InitializeAsync();
        vm.SelectedProfile = Assert.Single(vm.Profiles); vm.LocaleEmulatorPath = tool; vm.LocaleProfileId = ProfileId;
        await vm.LoadLocaleProfilesCommand.ExecuteAsync(null); Assert.Equal(ProfileId, vm.SelectedLocaleProfile!.Id);
        await vm.SaveSettingsCommand.ExecuteAsync(null); Assert.NotNull(services.Settings.DefaultLocale);
        Assert.Null(vm.SelectedProfile!.Locale);
        await vm.SaveLaunchCommand.ExecuteAsync(null); Assert.Null(vm.SelectedProfile!.Locale);
        Assert.Equal(profile.Id, vm.SelectedProfile.Id);
        Assert.Equal(profile.InstallationId, vm.SelectedProfile.InstallationId); Assert.Equal(profile.ComponentId, vm.SelectedProfile.ComponentId);
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameAndLocaleWrapperDoNotInheritProjectCredentials(bool locale)
    {
        using var dir = new TestDirectory(); var (game, tool) = await FixturesAsync(dir.Path); var launcher = new LocalLauncher(dir.Path);
        var profile = await launcher.SaveAsync("Synthetic", game, [], dir.Path, ct: Ct, locale: locale ? new(tool, ProfileId) : null);
        var oldKey = Environment.GetEnvironmentVariable("VN_LIBRARY_KEY"); var oldToken = Environment.GetEnvironmentVariable("VNDB_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("VN_LIBRARY_KEY", "synthetic-test-key"); Environment.SetEnvironmentVariable("VNDB_API_TOKEN", "synthetic-test-token");
            var start = await launcher.PreviewStartAsync(profile.Id, Ct);
            Assert.False(start.Environment.ContainsKey("VN_LIBRARY_KEY")); Assert.False(start.Environment.ContainsKey("VNDB_API_TOKEN"));
            Assert.Equal(Environment.GetEnvironmentVariable("PATH"), start.Environment["PATH"]);
        }
        finally { Environment.SetEnvironmentVariable("VN_LIBRARY_KEY", oldKey); Environment.SetEnvironmentVariable("VNDB_API_TOKEN", oldToken); }
    }

    [Fact]
    public async Task InvalidStatusNeverCreatesAnUnsendableImmutableLocalEdit()
    {
        using var dir = new TestDirectory(); var outbox = new Vndb.Local.ActivityOutbox(dir.Path);
        var target = await outbox.CreateTargetAsync(new(Guid.NewGuid().ToString("D"), "Synthetic", "windows", DateTimeOffset.UtcNow), "Synthetic", null, ct: Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.EditAsync(target, "planned", "Invalid", Ct));
        Assert.Empty(await outbox.BatchesAsync(ct: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => outbox.EditAsync(target, "not_started", "Invalid with existing start date", Ct));
        Assert.Empty(await outbox.BatchesAsync(ct: Ct));
        await outbox.EditAsync(target, "on_hold", "Launch check only; no completion inferred", Ct);
        Assert.Equal("on_hold", Assert.Single(await outbox.BatchesAsync(ct: Ct)).PlayEdits.Single().Details.Status);
    }

    [Fact]
    public async Task LocaleConfigOrToolChangesRequireAnExplicitResaveAndMalformedXmlIsRecoverable()
    {
        using var dir = new TestDirectory(); var (game, tool) = await FixturesAsync(dir.Path); var launcher = new LocalLauncher(dir.Path);
        var profile = await launcher.SaveAsync("Synthetic", game, [], dir.Path, ct: Ct, locale: new(tool, ProfileId));
        var file = Path.Combine(dir.Path, "LEConfig.xml"); await File.WriteAllTextAsync(file, Config().Replace("ja-JP", "zh-CN", StringComparison.Ordinal), Ct);
        await Assert.ThrowsAsync<IOException>(() => launcher.PreviewStartAsync(profile.Id, Ct));
        await File.WriteAllTextAsync(file, Config(), Ct); await File.AppendAllTextAsync(tool, "changed", Ct);
        await Assert.ThrowsAsync<IOException>(() => launcher.PreviewStartAsync(profile.Id, Ct));
        await File.WriteAllTextAsync(file, "<!DOCTYPE LEConfig [<!ENTITY ext SYSTEM 'file:///nonexistent'>]><LEConfig>&ext;</LEConfig>", Ct);
        Assert.Throws<InvalidDataException>(() => LocaleEmulator.Profiles(tool));
    }
}

[CollectionDefinition("Launch environment isolation", DisableParallelization = true)]
public sealed class LaunchEnvironmentCollection;
