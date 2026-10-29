using System.Text.Json;
using Vndb.Admin;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceEntryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static Task Run(Func<CancellationToken, Task> action) => action(Ct);
    private sealed class Dialogs(string? file = null) : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult(file);
    }
    private sealed class QueryLibrary : IDesktopLibrary
    {
        public string? Text, Source;
        public bool Fail;
        public MetadataResponse Result { get; } = new([M5Tests.Item().Metadata!], "client_vndb", DateTimeOffset.UtcNow, 1, 30, null, false, PendingImport: true);
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Text = text; Source = source; if (Fail) throw new IOException("synthetic"); return Task.FromResult(Result); }
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => throw new InvalidOperationException("Unused shelf operation");
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => throw new InvalidOperationException("Unused plays operation");
    }
    [Fact]
    public void AdminCompiledGraphExcludesClientEntrypointsAndLocalPlatformDependencies()
    {
        var assembly = typeof(AdminApplication).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name is "vndb" or "Vndb.Cli" or "Vndb.Local" or "Vndb.Client" or "Vndb.Desktop");
        var deps = Path.Combine(TestDirectory.ScratchRoot, "../build/Vndb.Admin/bin/Release/net10.0/vndb-admin.deps.json");
        using var json = JsonDocument.Parse(File.ReadAllText(deps));
        Assert.DoesNotContain(json.RootElement.GetProperty("libraries").EnumerateObject(), p =>
            p.Name.StartsWith("VndbCli/", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Vndb.Local/", StringComparison.OrdinalIgnoreCase) ||
            p.Name.StartsWith("Vndb.Client/", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
            p.Name.StartsWith("System.Security.Cryptography.ProtectedData/", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task DisabledAdminArchiveCommandRejectsBeforeReadingMissingPlanOrContactingServer()
    {
        using var directory = new TestDirectory(); using var handler = new StubHandler("{}");
        using var output = new StringWriter(); using var error = new StringWriter();
        var app = new AdminApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions);
        var code = await app.RunAsync(["library", "sync-archive", Path.Combine(directory.Path, "missing.json"), "--server", "http://test.local/", "--data-dir", directory.Path], Ct);
        Assert.Equal(2, code); Assert.Contains("已停用", error.ToString()); Assert.Empty(output.ToString()); Assert.Empty(handler.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
    [Fact]
    public async Task AdminOutputKeepsUtf8NoOverwriteAndCleansItsOwnTemporaryFile()
    {
        using var directory = new TestDirectory(); using var handler = new StubHandler("{}");
        using var output = new StringWriter(); using var error = new StringWriter();
        var app = new AdminApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions);
        var file = Path.Combine(directory.Path, "output.json");
        string[] args = ["library", "list", "--storage", "json", "--data-dir", directory.Path, "--output", file];
        Assert.Equal(0, await app.RunAsync(args, Ct)); var original = await File.ReadAllBytesAsync(file, Ct);
        Assert.False(original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        using var json = JsonDocument.Parse(original); Assert.Empty(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(6, await app.RunAsync(args, Ct)); Assert.Equal(original, await File.ReadAllBytesAsync(file, Ct));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp", SearchOption.AllDirectories)); Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task QueryFeatureRunsWithoutMainWindowAndExportsOnlyImportableMetadata()
    {
        using var directory = new TestDirectory(); var file = Path.Combine(directory.Path, "export.json");
        var library = new QueryLibrary(); var messages = new List<string>(); var completions = 0;
        var feature = new QueryFeatureViewModel(library, new Dialogs(file), Run, messages.Add) { Query = "作品", QuerySource = "cache" };
        feature.Completed += _ => completions++;
        await feature.SearchCommand.ExecuteAsync(null); await feature.ExportCommand.ExecuteAsync(null);
        Assert.Equal("作品", library.Text); Assert.Equal("cache", library.Source); Assert.Equal(1, completions); Assert.Same(library.Result, feature.Result);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, Ct)); Assert.False(WirePrivacy.ContainsPrivateData(json.RootElement));
        var parsed = MetadataInput.Parse(json.RootElement, new()); Assert.Single(parsed.Items); Assert.Equal("v17", parsed.Items[0].VndbId); Assert.NotNull(parsed.Items[0].Snapshot);
        library.Fail = true; await Assert.ThrowsAsync<IOException>(() => feature.SearchCommand.ExecuteAsync(null));
        Assert.Same(library.Result, feature.Result); Assert.Equal(1, completions);
        feature.Clear(); await Assert.ThrowsAsync<ArgumentException>(() => feature.ExportCommand.ExecuteAsync(null));
    }
    [Fact]
    public async Task LocalProfileInitializationKeepsIdentitySettingsAndCatalogWithoutDesktopHost()
    {
        using var directory = new TestDirectory(); var settings = new LocalClientSettingsStore(directory.Path); var launcher = new LocalLauncher(directory.Path);
        var library = new LocalLibraryStore(directory.Path); await library.SaveAsync(new([], [M5Tests.Item()]), Ct);
        await settings.SaveAsync(new("http://saved.local/", "synthetic scope", "synthetic target"), Ct);
        var first = await new LocalClientProfile(settings, launcher, library).LoadAsync("http://fallback.local/", Ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "desktop-settings.json"), Ct);
        var second = await new LocalClientProfile(new(directory.Path), new(directory.Path), new(directory.Path)).LoadAsync(null, Ct);
        Assert.Equal(first.Identity.Id, second.Identity.Id); Assert.Equal("http://saved.local/", second.Settings.Server);
        Assert.Single(second.Library.Items); Assert.Empty(await launcher.ReadAsync(Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "desktop-settings.json"), Ct));
    }
    [Fact]
    public async Task MetadataExportCancellationAndExistingFileNeverReplaceOriginalData()
    {
        using var directory = new TestDirectory(); var file = Path.Combine(directory.Path, "export.json"); var response = new QueryLibrary().Result;
        await File.WriteAllTextAsync(file, "retained original", Ct);
        await Assert.ThrowsAsync<IOException>(() => MetadataExport.WriteAsync(response, file, Ct));
        Assert.Equal("retained original", await File.ReadAllTextAsync(file, Ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var missing = Path.Combine(directory.Path, "cancelled.json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MetadataExport.WriteAsync(response, missing, cancelled.Token));
        Assert.False(File.Exists(missing)); Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }
    [Fact]
    public async Task ScopedRecheckUpdatesOnlyThisDeviceAndWritesEvidenceLocally()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory(); var state = Path.Combine(directory.Path, "state");
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(state, Ct); var scope = Path.Combine(directory.Path, "scope");
        var game = Path.Combine(scope, "game"); Directory.CreateDirectory(game); await File.WriteAllTextAsync(Path.Combine(game, "keep.txt"), "synthetic", Ct);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallationView(new("test-install", new(identity.Id, game, "Company"), "missing", now, 3, now, now),
            [new("test-component", "test-install", new("item-17", "."), 1, now, now)]);
        var foreign = installation with { Installation = installation.Installation with { Id = "foreign-install", Details = installation.Installation.Details with { DeviceId = Guid.NewGuid().ToString("D") } } };
        var library = new LocalLibraryStore(state); await library.SaveAsync(new([installation, foreign], [M5Tests.Item()]), Ct);
        var report = await new LocalOrganizationWorkspace(state, identity).RecheckAsync(scope, Path.Combine(directory.Path, "target"), Ct);
        var current = await library.ReadAsync(Ct); var updated = current.Installations.Single(i => i.Installation.Id == "test-install");
        Assert.Equal("present", updated.Installation.Status); Assert.Equal(4, updated.Installation.Revision);
        Assert.Equal(JsonSerializer.Serialize(foreign, VndbJson.Options), JsonSerializer.Serialize(current.Installations.Single(i => i.Installation.Id == "foreign-install"), VndbJson.Options)); Assert.Single(report.Items);
        Assert.True(File.Exists(Path.Combine(state, "local-checks", report.Id + ".json"))); Assert.Single(current.Items);
        Assert.Equal("synthetic", await File.ReadAllTextAsync(Path.Combine(game, "keep.txt"), Ct));
    }
    [Fact]
    public async Task LaunchConfigurationRejectsForeignDeviceAndUnknownComponentBeforeWriting()
    {
        using var directory = new TestDirectory(); var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallationView(new("test-install", new("test-device", directory.Path, "Company"), "present", now, 1, now, now), []);
        var service = new LaunchConfiguration(new(directory.Path));
        var draft = new LaunchConfigurationDraft("synthetic", "missing.exe", [], directory.Path, installation, "", null);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(draft, "other-device", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(draft with { ComponentId = "unknown" }, "test-device", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(draft with { Installation = null, ComponentId = "unknown" }, "test-device", Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
    [Fact]
    public async Task OrganizationFeatureUsesLocalUseCaseWithoutMainWindowAndRestoresWholeSyntheticDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory(); var state = Path.Combine(directory.Path, "state");
        var identity = await LocalDeviceIdentity.LoadOrCreateAsync(state, Ct); var workspace = new LocalOrganizationWorkspace(state, identity);
        var scope = Path.Combine(directory.Path, "scope"); var source = Path.Combine(scope, "old"); var target = Path.Combine(directory.Path, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "synthetic", Ct);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallationView(new("test-install", new(identity.Id, source, "Company"), "present", now, 1, now, now),
            [new("test-component", "test-install", new("item-17", ""), 1, now, now)]);
        await new LocalLibraryStore(state).SaveAsync(new([installation], [M5Tests.Item()]), Ct);
        var feature = new OrganizationFeatureViewModel(() => workspace, () => installation, () => [installation], new Dialogs(), Run, _ => { })
            { SourceScope = scope, TargetRoot = target, Company = "会社", JapaneseTitle = "作品" };
        await feature.PreviewCommand.ExecuteAsync(null); var hash = feature.PlanHash; Assert.NotEmpty(hash);
        feature.Approval = hash; feature.RunDirectory += "-changed"; Assert.Empty(feature.PlanHash); Assert.Empty(feature.Approval);
        await Assert.ThrowsAsync<ArgumentException>(() => feature.ExecuteCommand.ExecuteAsync(null)); Assert.True(File.Exists(Path.Combine(source, "keep.txt")));
        await feature.PreviewCommand.ExecuteAsync(null); feature.Approval = feature.PlanHash; await feature.ExecuteCommand.ExecuteAsync(null);
        var destination = Path.Combine(target, "会社", "作品"); Assert.True(File.Exists(Path.Combine(destination, "keep.txt"))); Assert.False(Directory.Exists(source));
        await File.WriteAllTextAsync(Path.Combine(destination, "save.dat"), "new save", Ct);
        await feature.PreviewRestoreCommand.ExecuteAsync(null); feature.Approval = feature.PlanHash; await feature.RestoreCommand.ExecuteAsync(null);
        Assert.Equal("synthetic", await File.ReadAllTextAsync(Path.Combine(source, "keep.txt"), Ct)); Assert.True(File.Exists(Path.Combine(source, "save.dat")));
        Assert.Equal(source, Assert.Single((await new LocalLibraryStore(state).ReadAsync(Ct)).Installations).Installation.Details.RootPath);
    }
    [Fact]
    public async Task LibraryMaintenanceStepRunsWithoutWebAndHonorsActivityPacing()
    {
        using var directory = new TestDirectory(); var store = new SqliteLibraryStore(directory.Path);
        var instance = Path.Combine(directory.Path, "service-instance-id"); await File.WriteAllTextAsync(instance, "synthetic-instance", Ct);
        var clock = new Clock(); using var handler = new StubHandler("{}"); using var http = new HttpClient(handler);
        using var core = new VndbClient(http, VndbClientTests.FastOptions); var covers = new CoverDownloader(http, directory.Path);
        using var importer = new LibraryImporter(core, covers, store); using var catalog = new CatalogEnricher(core, importer, store, store);
        using var imports = new MetadataImports(store, core, importer, covers, clock, new());
        var maintenance = new CatalogMaintenance(imports, new ActivityImports(store, imports), new CatalogJobs(catalog, store, clock), store, clock);
        await maintenance.StepAsync(Ct); File.Move(instance, instance + ".retained");
        await maintenance.StepAsync(Ct); // Not due: no access to the instance file or network.
        clock.Now += TimeSpan.FromSeconds(30); await Assert.ThrowsAsync<FileNotFoundException>(() => maintenance.StepAsync(Ct));
        File.Move(instance + ".retained", instance); await maintenance.StepAsync(Ct); Assert.Empty(handler.Requests);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
