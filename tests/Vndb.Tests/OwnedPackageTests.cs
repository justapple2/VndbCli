using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vndb.Cli;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class OwnedPackageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Folder(string root, string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static RegistrationSelection Choice => new("合成作品", Metadata: MetadataImports.Snapshot(StorageTests.Entry("v17")));
    private static async Task<PackageRegistrationPreview> Register(OwnedPackages service, string root, string relative, RegistrationSelection? choice = null)
    { var preview = await service.PreviewAsync(root, relative, choice ?? Choice, ct: Ct); await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct); return preview; }
    private sealed class Factory(string root) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", root)
            .UseSetting("Library:WriteKey", "owned-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    private sealed class Gate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool LoseActivityReply = true;
        public List<string> Activity { get; } = [];
        public List<string> Payloads { get; } = [];
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri));
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct); Payloads.Add(body);
                if (request.RequestUri!.AbsolutePath.EndsWith("/activity/batches")) Activity.Add(body);
            }
            var response = await base.SendAsync(request, ct);
            if (Activity.Count > 0 && LoseActivityReply) { LoseActivityReply = false; response.Dispose(); throw new HttpRequestException("synthetic reply lost"); }
            return response;
        }
    }
    [Fact]
    public void FilenameScanIsBoundedReadOnlyAndOnlyOffersFirstMultipartVolume()
    {
        using var dir = new TestDirectory(); var nested = Folder(dir.Path, "会社");
        foreach (var file in new[] { "[brand] 合成作品.zip", "title.part01.rar", "title.part02.rar", "multi.7z.001", "multi.7z.002", "game.iso", "game.msi", "book.txt", "setup.exe" }) File.WriteAllText(Path.Combine(nested, file), "fixture");
        var before = Directory.GetFiles(nested).Select(File.ReadAllText).ToArray();
        var scan = OwnedPackages.Scan(dir.Path, ct: Ct);
        Assert.Equal(5, scan.Candidates.Count); Assert.Contains(scan.Candidates, c => c.QueryTitle == "合成作品" && c.RelativePath == "会社/[brand] 合成作品.zip");
        Assert.DoesNotContain(scan.Candidates, c => c.RelativePath.EndsWith("002") || c.RelativePath.EndsWith("02.rar") || c.RelativePath.EndsWith("exe"));
        Assert.Equal(before, Directory.GetFiles(nested).Select(File.ReadAllText).ToArray());
        Assert.Throws<ArgumentException>(() => OwnedPackages.FilePath(dir.Path, "../escape.zip"));
        Assert.Throws<ArgumentException>(() => OwnedPackages.FilePath(dir.Path, "/escape.zip"));
        Assert.Throws<ArgumentException>(() => OwnedPackages.Scan(dir.Path, 7, Ct));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => OwnedPackages.Scan(dir.Path, ct: cancel.Token));
    }
    [Fact]
    public async Task ExternalPackageScanSkipsOnlyTheExactRegisteredInstallationTree()
    {
        using var dir = new TestDirectory(); var root = Folder(dir.Path, "scope"); var data = Folder(dir.Path, "client");
        var game = Folder(root, "game"); var sibling = Folder(root, "game-backup");
        File.WriteAllText(Path.Combine(game, "internal-patch.zip"), "fixture");
        File.WriteAllText(Path.Combine(sibling, "uninstalled.zip"), "fixture");
        Assert.Equal(2, OwnedPackages.Scan(root, ct: Ct).Candidates.Count);
        Assert.Equal("game-backup/uninstalled.zip", Assert.Single(OwnedPackages.Scan(root, ct: Ct, excludedDirectories: [game]).Candidates).RelativePath);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallation("fixture", new("device", game, "Synthetic game"), "present", now, 1, now, now);
        var local = new LocalLibraryStore(data); await local.SaveAsync(new([new(installation, [])], []), Ct);
        var before = await File.ReadAllBytesAsync(local.FilePath, Ct);
        using var output = new StringWriter(); using var error = new StringWriter();
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Local scanning must not use the network"));
        var cli = new CliApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions, new());
        Assert.Equal(0, await cli.RunAsync(["organize", "package-scan", root, "--identity-dir", data, "--outside-installations", "--json"], Ct));
        var scan = JsonSerializer.Deserialize<PackageScan>(output.ToString(), VndbJson.Options)!;
        Assert.Equal("game-backup/uninstalled.zip", Assert.Single(scan.Candidates).RelativePath);
        Assert.Equal(before, await File.ReadAllBytesAsync(local.FilePath, Ct));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(game, "internal-patch.zip")));
    }
    [Fact]
    public async Task ExplicitPackageRegistrationKeepsRelativePathAndNeverCreatesInstallationOrPlay()
    {
        using var dir = new TestDirectory(); var root = Folder(dir.Path, "packages"); var data = Folder(dir.Path, "client");
        File.WriteAllText(Path.Combine(root, "合成作品.7z"), "not extracted");
        var local = new LocalLibraryStore(data); var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var service = new OwnedPackages(local, device);
        var preview = await service.PreviewAsync(root, "合成作品.7z", Choice, ct: Ct);
        Assert.False(File.Exists(local.FilePath)); Assert.Empty((await local.ReadAsync(Ct)).Installations);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CommitAsync(preview, new string('a', 64), Ct));
        await service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        var catalog = await local.ReadAsync(Ct); var package = Assert.Single(catalog.Packages!);
        Assert.Equal(root, package.ScanRoot); Assert.Equal("合成作品.7z", package.RelativePath); Assert.Equal("present", package.Status);
        Assert.Single(catalog.Items); Assert.Empty(catalog.Installations); Assert.Equal(0, catalog.Items[0].PlayCount);
        Assert.True((await service.PreviewAsync(root, package.RelativePath, Choice, ct: Ct)).Reused);
        var export = await new LocalRegistrationSync(local).ExportAsync(package.ItemId, Ct);
        var json = JsonSerializer.Serialize(export, VndbJson.Options);
        Assert.False(WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(export, VndbJson.Options)));
        Assert.DoesNotContain("relative_path", json); Assert.DoesNotContain("scan_root", json); Assert.DoesNotContain(root, json);
        Assert.Empty(export.Installations); Assert.Empty(export.Plays);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(root, package.RelativePath, new("filename only"), ct: Ct));
    }
    [Fact]
    public async Task ChangedFileOrLibraryInvalidatesApprovedPreviewAndUnknownExtensionSurvivesWrites()
    {
        using var dir = new TestDirectory(); var root = Folder(dir.Path, "packages"); var data = Folder(dir.Path, "client"); var path = Path.Combine(root, "game.zip"); File.WriteAllText(path, "fixture");
        var local = new LocalLibraryStore(data); var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var service = new OwnedPackages(local, device);
        await File.WriteAllTextAsync(local.FilePath, "{\"installations\":[],\"items\":[],\"format\":\"vn-local-library\",\"format_version\":1,\"future_field\":{\"retain\":true}}", Ct);
        var preview = await service.PreviewAsync(root, "game.zip", Choice, ct: Ct); File.AppendAllText(path, "changed");
        await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct));
        await Register(service, root, "game.zip");
        Assert.True(JsonDocument.Parse(await File.ReadAllTextAsync(local.FilePath, Ct)).RootElement.GetProperty("future_field").GetProperty("retain").GetBoolean());
        preview = await service.PreviewAsync(root, "game.zip", Choice, ct: Ct);
        await local.UpdateAsync(l => l with { Items = [] }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CommitAsync(preview, LocalArchiveFormat.Hash(preview), Ct));
    }
    [Fact]
    public async Task PackageSeedRetriesExactActivityAndOnlyBooleanPresenceReachesWeb()
    {
        using var dir = new TestDirectory(); using var db = new TestDirectory(); await using var app = new Factory(db.Path);
        using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()); using var http = new HttpClient(gate) { BaseAddress = initial.BaseAddress };
        var api = new WebLibraryClient(http, http.BaseAddress!, "owned-test-key", TimeSpan.FromSeconds(10));
        var root = Folder(dir.Path, "packages"); var data = Folder(dir.Path, "client"); File.WriteAllText(Path.Combine(root, "game.zip"), "fixture");
        var local = new LocalLibraryStore(data); var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var service = new OwnedPackages(local, device);
        var original = await Register(service, root, "game.zip"); var sync = new LocalRegistrationSync(local);
        await Assert.ThrowsAnyAsync<Exception>(() => sync.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.Equal(1, await sync.SyncAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.Equal(2, gate.Activity.Count); Assert.Equal(gate.Activity[0], gate.Activity[1]);
        var serverItem = Assert.Single((await api.ItemsAsync(1, Ct)).Items);
        var catalog = await local.ReadAsync(Ct); Assert.Equal(serverItem.Item.Id, Assert.Single(catalog.Packages!).ItemId); Assert.Empty(catalog.Installations);
        Assert.Equal(serverItem.Item.Id, await sync.MappedItemAsync(original.Item.Item.Id, http.BaseAddress!.AbsoluteUri, Ct, original.Item));
        Assert.Equal(1, await new PresenceSync(local).SubmitAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        Assert.True(Assert.Single((await api.PresenceHeadAsync(device.Id, Ct)).Items).IsLocal);
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); Assert.Equal(0, store.Plays().Total); Assert.Empty(await api.PlaysAsync(serverItem.Item.Id, Ct));
        var variant = store.SaveItem(new(new("different edition", "v17")), null, "variant");
        var now = DateTimeOffset.UtcNow;
        await local.UpdateAsync(l => l with { Installations = [new(new("missing-install", new(device.Id, root, "missing installation"), "missing", now, 1, now, now), [new("component", "missing-install", new(serverItem.Item.Id, "."), 1, now, now)])] }, Ct);
        Assert.Equal(0, await new PresenceSync(local).SubmitAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct)); // present archive outranks missing installation
        var disconnected = Path.Combine(dir.Path, "disconnected"); Directory.Move(root, disconnected);
        await service.VerifyAsync(Ct);
        Assert.Equal(0, await new PresenceSync(local).SubmitAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct)); // unknown retains last true
        Directory.Move(disconnected, root); File.Move(Path.Combine(root, "game.zip"), Path.Combine(root, "retained.zip")); await service.VerifyAsync(Ct);
        Assert.Equal(1, await new PresenceSync(local).SubmitAsync(api, http.BaseAddress!.AbsoluteUri, device.Id, Ct));
        var flags = (await api.PresenceHeadAsync(device.Id, Ct)).Items;
        Assert.False(Assert.Single(flags).IsLocal); Assert.DoesNotContain(flags, f => f.ItemId == variant.Id); // never borrow by VN ID
        Assert.All(gate.Payloads, body => { Assert.DoesNotContain("scan_root", body); Assert.DoesNotContain("relative_path", body); Assert.DoesNotContain("game.zip", body); });
    }
    [Fact]
    public async Task DisconnectedRootAndChangedPackageAreUnknownButReadableMissingFileIsMissing()
    {
        using var dir = new TestDirectory(); var root = Folder(dir.Path, "packages"); var data = Folder(dir.Path, "client"); File.WriteAllText(Path.Combine(root, "game.zip"), "fixture");
        var local = new LocalLibraryStore(data); var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct); var service = new OwnedPackages(local, device);
        await Register(service, root, "game.zip");
        var renamed = Path.Combine(dir.Path, "disconnected"); Directory.Move(root, renamed);
        Assert.Equal("unknown", Assert.Single((await service.VerifyAsync(Ct)).Packages!).Status);
        Directory.Move(renamed, root);
        Assert.Equal("present", Assert.Single((await service.VerifyAsync(Ct)).Packages!).Status);
        File.AppendAllText(Path.Combine(root, "game.zip"), "changed");
        Assert.Equal("unknown", Assert.Single((await service.VerifyAsync(Ct)).Packages!).Status);
        File.Move(Path.Combine(root, "game.zip"), Path.Combine(root, "retained.zip"));
        Assert.Equal("missing", Assert.Single((await service.VerifyAsync(Ct)).Packages!).Status);
        Assert.True(File.Exists(Path.Combine(root, "retained.zip")));
    }
    [Fact]
    public async Task CliPackageCommandsPreviewAndConfirmWithoutNetworkAndKeepOutputOutsideSource()
    {
        using var dir = new TestDirectory(); var root = Folder(dir.Path, "packages"); var data = Folder(dir.Path, "client"); File.WriteAllText(Path.Combine(root, "game.zip"), "fixture");
        var metadata = Path.Combine(dir.Path, "metadata.json"); await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(Choice.Metadata, VndbJson.Options), Ct);
        using var output = new StringWriter(); using var error = new StringWriter();
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No network expected"));
        var cli = new CliApplication(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions, new());
        Assert.Equal(0, await cli.RunAsync(["organize", "package-scan", root, "--json"], Ct));
        string[] args = ["organize", "package-register", "--identity-dir", data, "--scan-root", root, "--relative-path", "game.zip", "--item-json", metadata, "--json"];
        Assert.Equal(0, await cli.RunAsync(args, Ct)); Assert.False(File.Exists(Path.Combine(data, "local-library.json")));
        Assert.Equal(0, await cli.RunAsync([.. args, "--confirm"], Ct)); Assert.Empty((await new LocalLibraryStore(data).ReadAsync(Ct)).Installations);
        Assert.Equal(2, await cli.RunAsync([.. args, "--output", Path.Combine(root, "bad.json")], Ct));
    }
    [Fact]
    public async Task FilenameQueryUsesWebFirstAndNeverSendsScanRootOrRelativeLocation()
    {
        using var dir = new TestDirectory(); using var db = new TestDirectory(); await using var app = new Factory(db.Path);
        using var initial = app.CreateClient(); using var gate = new Gate(app.Server.CreateHandler()) { LoseActivityReply = false };
        var store = app.Services.GetRequiredService<SqliteLibraryStore>(); store.Save(StorageTests.Entry());
        var root = Folder(dir.Path, "packages"); File.WriteAllText(Path.Combine(root, "[brand] v17.zip"), "fixture");
        using var output = new StringWriter(); using var error = new StringWriter();
        var cli = new CliApplication(output, error, () => new HttpClient(gate, false), _ => null, VndbClientTests.FastOptions, new());
        Assert.Equal(0, await cli.RunAsync(["organize", "package-query", root, "--relative-path", "[brand] v17.zip", "--server", initial.BaseAddress!.AbsoluteUri, "--cache-dir", Path.Combine(dir.Path, "cache"), "--json"], Ct));
        var json = JsonDocument.Parse(output.ToString()); Assert.Equal("v17", json.RootElement.GetProperty("matches").GetProperty("items")[0].GetProperty("vndb_id").GetString());
        Assert.All(gate.Payloads, body => { Assert.DoesNotContain("scan_root", body); Assert.DoesNotContain("relative_path", body); Assert.DoesNotContain("zip", body); });
        Assert.NotEmpty(gate.Requests); Assert.All(gate.Requests, uri => { Assert.DoesNotContain(root, uri); Assert.DoesNotContain("zip", uri); });
        Assert.Empty(store.ReadItems().Items); // reading public metadata never adds personal membership
    }
    [Fact]
    public async Task GrowingLocalCatalogueCannotReplaceReadableJsonWithAnOversizedFile()
    {
        using var dir = new TestDirectory(); var local = new LocalLibraryStore(dir.Path); await local.SaveAsync(new([], []), Ct);
        var original = await File.ReadAllBytesAsync(local.FilePath, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => local.UpdateAsync(l => l with {
            Extensions = new() { ["oversized"] = JsonSerializer.SerializeToElement(new string('x', 8 * 1024 * 1024)) } }, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(local.FilePath, Ct)); Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }
    [Fact]
    public void OwnershipPresentationNeverPromisesLaunchForAnArchiveAndSelectionInvalidatesApproval()
    {
        var book = new BookViewModel(null, Choice.Metadata); book.SetOwnership(false, ["会社/game.zip"]);
        Assert.True(book.IsLocal); Assert.False(book.HasInstallation); Assert.Contains("未登记安装", book.LocalLabel);
        var status = ""; var query = "";
        var vm = new PackagesFeatureViewModel(() => throw new InvalidOperationException(), () => null, action => action(Ct), text => status = text, text => query = text, _ => Task.CompletedTask);
        vm.Selected = new(Path.GetTempPath(), "game.zip", "suggested", "压缩包", 1); vm.QuerySelectedCommand.Execute(null); Assert.Equal("suggested", query);
        vm.Select(Choice); vm.Approved = true; vm.Selected = new(Path.GetTempPath(), "other.zip", "other", "压缩包", 2);
        Assert.False(vm.Approved); Assert.False(vm.ConfirmCommand.CanExecute(null)); Assert.Contains("尚未选择", vm.MatchLabel);
        Assert.NotEmpty(status);
    }
}
