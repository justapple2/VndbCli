using System.Text.Json;
using Vndb.Contracts;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class DesktopUsabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("SiglusEngine.exe", "SiglusEngine.exe")]
    [InlineData("\"game with spaces.exe\"", "game with spaces.exe")]
    [InlineData("\"\"", "")]
    [InlineData("a&b|c>file", "a&b|c>file")]
    [InlineData("E:\\ゲーム", "E:\\ゲーム")]
    public void CustomArgumentsAreData(string input, string expected) => Assert.Equal(expected, Assert.Single(LaunchArguments.Parse(input)));
    [Fact]
    public void ArgumentQuotingRoundTripsAndRejectsUnclosedQuotes()
    {
        string[] args = ["SiglusEngine.exe", "", "日本語 space", "quote\"inside", @"E:\game folder\", "&echo", "a\\\\\"b"];
        Assert.Equal(args, LaunchArguments.Parse(LaunchArguments.Format(args)));
        Assert.Throws<ArgumentException>(() => LaunchArguments.Parse("\"unclosed"));
    }
    [Fact]
    public async Task WrapperPreviewAndRuntimeLocaleChoiceDoNotRewriteProfilesOrRunAnything()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var tool = Path.Combine(dir.Path, "LEProc.exe"); var wrapper = Path.Combine(dir.Path, "custom-wrapper.exe");
        await File.WriteAllTextAsync(tool, "synthetic tool", Ct); await File.WriteAllTextAsync(wrapper, "not an executable", Ct);
        const string guid = "33333333-3333-4333-8333-333333333333";
        var config = $"<LEConfig><Profiles><Profile Guid=\"{guid}\" Name=\"Japanese\"><Location>ja-JP</Location><RunAsAdmin>false</RunAsAdmin><RunWithSuspend>false</RunWithSuspend><Parameter /></Profile></Profiles></LEConfig>";
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "LEConfig.xml"), config, Ct);
        var launcher = new LocalLauncher(dir.Path); var profile = await launcher.SaveAsync("wrapper", wrapper, ["SiglusEngine.exe", "argument with space"], dir.Path, ct: Ct);
        var original = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct);
        var global = await LocaleEmulator.CaptureAsync(new(tool, guid), Ct);
        var direct = await launcher.PreviewStartAsync(profile.Id, Ct, LaunchMethod.Direct); Assert.Equal(wrapper, direct.FileName); Assert.Equal(profile.Arguments, direct.ArgumentList);
        var locale = await launcher.PreviewStartAsync(profile.Id, Ct, LaunchMethod.GlobalLocale, global);
        Assert.Equal(tool, locale.FileName); Assert.Equal(wrapper, locale.ArgumentList[2]); Assert.Equal("\"SiglusEngine.exe\"", locale.ArgumentList[3]);
        Assert.False(direct.UseShellExecute); Assert.Equal(dir.Path, locale.WorkingDirectory);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => launcher.PreviewStartAsync(profile.Id, Ct, LaunchMethod.GlobalLocale));
        await File.AppendAllTextAsync(Path.Combine(dir.Path, "LEConfig.xml"), " ", Ct);
        await File.WriteAllTextAsync(tool, "changed", Ct);
        await Assert.ThrowsAsync<IOException>(() => launcher.PreviewStartAsync(profile.Id, Ct, LaunchMethod.GlobalLocale, global));
    }
    [Fact]
    public async Task OneVerifiedLegacyLocaleCanBecomeGlobalWithoutChangingAnyProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var tool = Path.Combine(dir.Path, "LEProc.exe"); var game = Path.Combine(dir.Path, "game.exe");
        const string guid = "33333333-3333-4333-8333-333333333333";
        await File.WriteAllTextAsync(tool, "synthetic", Ct); await File.WriteAllTextAsync(game, "synthetic", Ct);
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "LEConfig.xml"), $"<LEConfig><Profiles><Profile Guid=\"{guid}\" Name=\"Japanese\"><Location>ja-JP</Location><RunAsAdmin>false</RunAsAdmin><RunWithSuspend>false</RunWithSuspend><Parameter /></Profile></Profiles></LEConfig>", Ct);
        var launcher = new LocalLauncher(dir.Path); await launcher.SaveAsync("old", game, [], dir.Path, ct: Ct, locale: new(tool, guid));
        var before = await File.ReadAllBytesAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct);
        using var services = new DesktopServices(dir.Path); await services.InitializeAsync(Ct);
        Assert.Equal(guid, services.Settings.DefaultLocale!.ProfileId);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "launch-profiles.json"), Ct));
        Assert.Equal(guid, (await services.SettingsStore.LoadAsync(Ct)).DefaultLocale!.ProfileId);
    }
    private sealed class FakeRegistry : IGameRegistry
    {
        public Dictionary<RegistryLocation, RegistryText> Values { get; } = [];
        public int Writes { get; private set; }
        public int? FailAt { get; set; }
        public bool SharedViews { get; set; }
        public RegistryText? Read(RegistryLocation location) => Values.GetValueOrDefault(SharedViews ? location with { View = 32 } : location);
        public RegistrySearch Find(string term, CancellationToken ct) => new(Values.Select(v => new RegistryEntry(v.Key, v.Value)).ToArray(), []);
        public void Write(RegistryLocation location, RegistryText? value)
        {
            if (++Writes == FailAt) throw new UnauthorizedAccessException("synthetic permission failure");
            if (SharedViews) location = location with { View = 32 };
            if (value is null) Values.Remove(location); else Values[location] = value;
        }
    }
    private static RegistryLocation Location(string name = "InstallPath", int view = 32) => new("HKCU", view, "Software\\SyntheticVN\\Game", name);
    [Fact]
    public async Task RegistryRepairBacksUpBeforeWritingAndRestoresAcrossRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var old = Path.Combine(dir.Path, "old"); var target = Path.Combine(dir.Path, "new"); Directory.CreateDirectory(target);
        var registry = new FakeRegistry(); var location = Location(); registry.Values[location] = new(old);
        var service = new RegistryRepair(registry, dir.Path); var plan = service.Preview([new(location, new(old))], old, target);
        Assert.Equal(0, registry.Writes); var run = await service.ApplyAsync(plan, Ct);
        Assert.True(File.Exists(Path.Combine(run, "intent.json"))); Assert.Equal(target, registry.Read(location)!.Data);
        var restarted = new RegistryRepair(registry, dir.Path); Assert.Contains(plan.Id, restarted.Runs());
        await restarted.RestoreAsync(await restarted.ReadRunAsync(plan.Id, Ct), Ct); Assert.Equal(old, registry.Read(location)!.Data);
    }
    [Fact]
    public async Task PartialRegistryFailureAndNewValueCanBeRestoredWithoutTouchingOtherKeys()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var old = Path.Combine(dir.Path, "old"); var target = Path.Combine(dir.Path, "new"); Directory.CreateDirectory(target);
        var registry = new FakeRegistry { FailAt = 2 }; registry.Values[Location()] = new(old); registry.Values[Location("Other")] = new("leave this alone");
        var service = new RegistryRepair(registry, dir.Path);
        var plan = service.Preview([new(Location(), new(old)), new(Location("New"), new(old))], old, target);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApplyAsync(plan, Ct));
        var statuses = new RegistryRepair(registry, dir.Path).InspectRun(await service.ReadRunAsync(plan.Id, Ct));
        Assert.Equal("已更新／可恢复", statuses[0].State);
        Assert.Equal("原值／未写入或已恢复", statuses[1].State);
        Assert.Contains(plan.Id, service.Runs()); registry.FailAt = null; await service.RestoreAsync(await service.ReadRunAsync(plan.Id, Ct), Ct);
        Assert.Equal(old, registry.Read(Location())!.Data); Assert.Null(registry.Read(Location("New"))); Assert.Equal("leave this alone", registry.Read(Location("Other"))!.Data);
        var intent = Path.Combine(dir.Path, "registry-repairs", plan.Id, "intent.json");
        await File.WriteAllTextAsync(intent, System.Text.Json.JsonSerializer.Serialize(plan with { Source = old + "-changed" }, Vndb.Core.VndbJson.Options), Ct);
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(plan, Ct));
    }
    [Fact]
    public async Task RegistryConcurrentChangesStopBeforeWriteOrRestore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var old = Path.Combine(dir.Path, "old"); var target = Path.Combine(dir.Path, "new"); Directory.CreateDirectory(target);
        var registry = new FakeRegistry(); registry.Values[Location()] = new(old); var service = new RegistryRepair(registry, dir.Path);
        var plan = service.Preview([new(Location(), new(old))], old, target); registry.Values[Location()] = new("external change");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyAsync(plan, Ct)); Assert.Equal(0, registry.Writes);
        registry.Values[Location()] = new(old); await service.ApplyAsync(plan, Ct); registry.Values[Location()] = new("external change");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(plan, Ct)); Assert.Equal("external change", registry.Read(Location())!.Data);
    }
    [Fact]
    public async Task SharedWowViewsAreAppliedAndRecoveredOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var old = Path.Combine(dir.Path, "old"); var target = Path.Combine(dir.Path, "new"); Directory.CreateDirectory(target);
        var registry = new FakeRegistry { SharedViews = true }; registry.Values[Location()] = new(old, 2);
        var service = new RegistryRepair(registry, dir.Path);
        var plan = service.Preview([new(Location(), new(old, 2)), new(Location(view: 64), new(old, 2))], old, target);
        await service.ApplyAsync(plan, Ct); Assert.Equal(1, registry.Writes); Assert.Equal(2, registry.Read(Location())!.Kind);
        await service.RestoreAsync(plan, Ct); Assert.Equal(2, registry.Writes); Assert.Equal(old, registry.Read(Location())!.Data);
    }
    [Fact]
    public void RegWow6432NodeUses32BitVendorViewAndDuplicatesCannotBeApplied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = RegistryRepair.ParseReg("Windows Registry Editor Version 5.00\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\SyntheticVN]\n\"Path\"=\"E:\\\\Old\\\\Game\"", 64);
        var entry = Assert.Single(result.Entries); Assert.Equal(32, entry.Location.View); Assert.Equal("Software\\SyntheticVN", entry.Location.Key);
        using var dir = new TestDirectory(); var old = Path.Combine(dir.Path, "old"); var target = Path.Combine(dir.Path, "new"); Directory.CreateDirectory(target);
        var service = new RegistryRepair(new FakeRegistry(), dir.Path);
        Assert.Throws<ArgumentException>(() => service.Preview([new(Location(), new(old)), new(Location() with { Key = Location().Key.ToLowerInvariant() }, new(old))], old, target));
    }
    [Fact]
    public async Task LegacyJapaneseRegEncodingIsExplicitAndBadUtf8NeverBecomesPathData()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var file = Path.Combine(dir.Path, "synthetic.reg");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var text = "REGEDIT4\n[HKEY_CURRENT_USER\\Software\\合成会社]\n\"Path\"=\"E:\\\\合成作品\"";
        await File.WriteAllBytesAsync(file, System.Text.Encoding.GetEncoding(932).GetBytes(text), Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => RegistryRepair.ReadTextAsync(file, "自动 UTF-8／UTF-16", Ct));
        var parsed = RegistryRepair.ParseReg(await RegistryRepair.ReadTextAsync(file, "日文 Shift-JIS", Ct), 32);
        Assert.Equal("Software\\合成会社", Assert.Single(parsed.Entries).Location.Key);
    }
    [Theory]
    [InlineData("HKLM", "Software\\Microsoft\\Windows")]
    [InlineData("HKCU", "Software\\Classes\\exefile")]
    [InlineData("HKLM", "SYSTEM\\Services\\x")]
    [InlineData("HKCR", "Software\\Vendor")]
    public void RegistryRepairRejectsSystemAndShellKeys(string hive, string key) => Assert.Throws<ArgumentException>(() => RegistryRepair.Validate(new(hive, 32, key, "")));
    [Fact]
    public void RegParsingIsDataOnlyAndPathMatchingNeverReplacesSubstrings()
    {
        if (!OperatingSystem.IsWindows()) return;
        var parsed = RegistryRepair.ParseReg("Windows Registry Editor Version 5.00\n[HKEY_CURRENT_USER\\Software\\SyntheticVN\\Game]\n\"Path\"=\"E:\\\\Old\\\\Game\"\n\"Count\"=dword:00000001\n[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows]\n\"Run\"=\"bad.exe\"\n[-HKEY_CURRENT_USER\\Software\\SyntheticVN]", 32);
        var entry = Assert.Single(parsed.Entries); Assert.Equal(@"E:\Old\Game", entry.Value.Data); Assert.NotEmpty(parsed.Warnings);
        Assert.Null(RegistryRepair.MapPath(@"E:\Old\Game2", @"E:\Old\Game", @"E:\New\Game"));
        Assert.Null(RegistryRepair.MapPath(@"cmd /c E:\Old\Game", @"E:\Old\Game", @"E:\New\Game"));
        Assert.Null(RegistryRepair.MapPath(@"E:\Old\Game\..\Other", @"E:\Old\Game", @"E:\New\Game"));
        Assert.Equal(@"E:\New\Game\save", RegistryRepair.MapPath(@"E:\Old\Game\save", @"E:\Old\Game", @"E:\New\Game"));
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
    private sealed class Library(IReadOnlyList<LibraryItemView> items) : IDesktopLibrary
    {
        public IReadOnlyList<LocalInstallationView> Installations { get; init; } = [];
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => Task.FromResult(new ShelfResult(new("test", items, [], Installations, DateTimeOffset.UtcNow), "test"));
        public Task<Vndb.Contracts.MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => Task.FromResult<IReadOnlyList<PersonalPlayView>>([]);
    }
    [Fact]
    public async Task ShelfSortsDatesUnknownLastAndFindsEveryDeveloperWithoutChangingItems()
    {
        using var dir = new TestDirectory(); var first = M5Tests.Item(1); var second = M5Tests.Item(2); var third = M5Tests.Item(3);
        first = first with { Item = first.Item with { CreatedAt = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) }, Metadata = first.Metadata! with { Novel = first.Metadata.Novel! with { Released = "2025-01-01" } } };
        second = second with { Metadata = second.Metadata! with { Novel = second.Metadata.Novel! with { Released = "2023-01-01" } } };
        third = third with { Metadata = third.Metadata! with { Novel = third.Metadata.Novel! with { Released = "TBA" } } };
        var vm = new MainViewModel(new Library([first, second, third]), new(dir.Path), new Dialogs()); await vm.RefreshCommand.ExecuteAsync(null);
        vm.Shelf.ShelfSort = "发售时间"; Assert.Equal([first.Item.Id, second.Item.Id, third.Item.Id], vm.Shelf.Books.Select(b => b.Id));
        vm.SelectedBook = vm.Shelf.Books[0]; var selected = vm.SelectedBook;
        vm.Shelf.SortDescending = false; Assert.Equal([second.Item.Id, first.Item.Id, third.Item.Id], vm.Shelf.Books.Select(b => b.Id));
        Assert.Same(selected, vm.SelectedBook);
        vm.Shelf.CompanyFilter = "Second"; Assert.Equal(3, vm.Shelf.Books.Count); Assert.Contains("Second", vm.Shelf.CompanyFilters);
        vm.Shelf.ShelfState = "未记录"; Assert.Single(vm.Shelf.Books);
        vm.Shelf.ShelfState = "未游玩"; Assert.Empty(vm.Shelf.Books);
        vm.Shelf.ResetFiltersCommand.Execute(null); Assert.Equal(3, vm.Shelf.Books.Count);
        vm.Shelf.ShelfSort = "添加时间"; Assert.Equal(first.Item.Id, vm.Shelf.Books[0].Id);
    }
    [Fact]
    public async Task ShelfPreferencesAndLaunchHistoryRemainSeparateLocalJson()
    {
        using var dir = new TestDirectory(); var store = new ShelfPreferencesStore(dir.Path); var p = new ShelfPreferences("上次打开", true, "会社", "游玩中", true, true);
        await store.SaveAsync(p, Ct); Assert.Equal(p, await new ShelfPreferencesStore(dir.Path).ReadAsync(Ct));
        var profile = new LaunchProfile("profile", "test", @"E:\local\game.exe", [], @"E:\local", new('0', 64), "install", "component");
        await store.OpenedAsync(profile, Ct); await store.OpenedAsync(profile, Ct);
        var entry = Assert.Single(await store.HistoryAsync(Ct)); Assert.Equal("component", entry.ComponentId);
        var json = await File.ReadAllTextAsync(Path.Combine(dir.Path, "launch-history.json"), Ct); Assert.DoesNotContain("game.exe", json); Assert.DoesNotContain("E:", json);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.db", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task OpeningAnUnconfiguredBookNeverEditsAnotherGamesProfile()
    {
        using var dir = new TestDirectory(); var vm = new MainViewModel(new Library([M5Tests.Item(17)]), new(dir.Path), new Dialogs());
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedProfile = new("other", "Other", @"E:\Synthetic\other.exe", ["--other"], @"E:\Synthetic", new('0', 64), "other-install", "other-component");
        vm.ConfigureBookCommand.Execute(vm.Shelf.Books[0]);
        Assert.Null(vm.SelectedProfile); Assert.Equal("", vm.Executable); Assert.Equal("[]", vm.ArgumentsJson); Assert.Equal(vm.SelectedBook!.Title, vm.LaunchTitle);
        Assert.Equal(3, vm.Section);
    }
    [Fact]
    public async Task GlobalSettingsAndBookConfigurationNeverLoseLocalComponentIdentity()
    {
        using var dir = new TestDirectory(); var now = DateTimeOffset.UtcNow; var localItem = M5Tests.Item(17);
        var webItem = localItem; // A synchronized installation uses the stable Web item ID; metadata equality is not an identity mapping.
        var installation = new LocalInstallationView(new("install", new("device", Path.Combine(dir.Path, "game"), "Synthetic"), "present", now, 1, now, now), [new("component", "install", new(localItem.Item.Id, "."), 1, now, now)]);
        var profile = new LaunchProfile("profile", "Synthetic", Path.Combine(dir.Path, "game.exe"), [], dir.Path, new('0', 64), "install", "component");
        var store = new LocalLibraryStore(dir.Path); await store.SaveAsync(new([installation], [localItem]), Ct);
        await new ShelfPreferencesStore(dir.Path).OpenedAsync(profile, Ct);
        using var services = new DesktopServices(dir.Path);
        var library = new Library([webItem]) { Installations = [installation] };
        var vm = new MainViewModel(library, services.Launcher, new Dialogs(), services); await vm.InitializeAsync(); await vm.RefreshCommand.ExecuteAsync(null); vm.Profiles.Add(profile);
        var book = Assert.Single(vm.Shelf.Books); Assert.True(book.IsLocal); Assert.NotNull(book.LastOpenedAt);
        vm.ConfigureBookCommand.Execute(book); Assert.Equal("component", vm.ComponentId); Assert.Same(profile, vm.SelectedProfile);
        Assert.Same(profile, Assert.Single(vm.FilteredProfiles)); Assert.True(vm.ProfilesForSelectedBook);
        vm.Section = 2;
        vm.ConfigureBookCommand.Execute(new BookViewModel(null, localItem.Metadata));
        Assert.NotNull(vm.SelectedBook); Assert.Equal("component", vm.ComponentId); Assert.Same(profile, vm.SelectedProfile);
        vm.ArgumentsText = "\"bad"; vm.NewLaunchCommand.Execute(null); Assert.Equal("", vm.ArgumentsText); Assert.Equal("[]", vm.ArgumentsJson);
    }
}
