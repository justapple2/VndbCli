using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class QualityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static (LibraryDevice Device, Installation Installation, InstallationComponent Component) Seed(SqliteLibraryStore store, string path)
    {
        var device = store.SaveDevice(new(Guid.NewGuid().ToString("D"), new("Quality test", "windows")), "device-test");
        var item = store.SaveItem(new(new("未关联作品", Notes: "保留备注一", ExtraNotes: "保留备注二")), null, "item-test");
        var installation = store.SaveInstallation(new(new(device.Id, path, "完整游戏")), null, "installation-test");
        var component = store.SaveComponent(installation.Id, new(new(item.Id, ".")), null, "component-test");
        return (device, installation, component);
    }
    private static QualityReview Review(SqliteLibraryStore store)
    {
        var client = new VndbClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("No upstream permitted"))), VndbClientTests.FastOptions);
        return new(store, new(client, new(client, new(new HttpClient(), store.DataDirectory), store), store, store));
    }
    [Fact]
    public void QualitySeparatesUnlinkedAndUnverifiedWithoutNetworkOrWindowsFilesystemOnServer()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); var (_, installation, _) = Seed(store, @"E:\OpaqueWindows\UnreachableOnNas");
        var review = Review(store); var report = review.Read();
        Assert.Equal(1, report.Counts["unlinked"]); Assert.Equal(1, report.Counts["observation_needed"]);
        Assert.DoesNotContain(report.Items, i => i.Kind == "path_missing");
        Assert.Single(review.Read(kind: "unlinked").Items); Assert.Equal(installation, store.GetInstallation(installation.Id));
        Assert.Throws<ArgumentException>(() => review.Read(page: 0));
    }
    [Fact]
    public void LocalAuditKeepsUnknownReparseAndComponentsDistinctFromMissingAndNeverCreatesProfiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "db"));
        var source = Path.Combine(dir.Path, "scope"); Directory.CreateDirectory(source); var game = Directory.CreateDirectory(Path.Combine(source, "game")).FullName;
        File.WriteAllText(Path.Combine(game, "config.exe"), "synthetic helper");
        var (device, legacyInstallation, legacyComponent) = Seed(store, game);
        var installation = JsonSerializer.Deserialize<LocalInstallation>(JsonSerializer.Serialize(legacyInstallation, VndbJson.Options), VndbJson.Options)!;
        var component = JsonSerializer.Deserialize<LocalInstallationComponent>(JsonSerializer.Serialize(legacyComponent, VndbJson.Options), VndbJson.Options)!;
        var view = new LocalInstallationView(installation, [component]);
        var report = InstallationAuditor.Check("instance", device.Id, source, source, [view], ct: Ct);
        Assert.Equal("present", report.Items[0].Status); Assert.Equal("not_configured", report.Items[0].LaunchStatus); Assert.Empty(report.Items[0].Executables);
        var missing = view with { Installation = installation with { Details = installation.Details with { RootPath = Path.Combine(source, "missing") } } };
        Assert.Equal("missing", InstallationAuditor.Check("instance", device.Id, source, source, [missing], ct: Ct).Items[0].Status);
        var absentComponent = view with { Components = [component with { Details = component.Details with { RelativePath = "absent" } }] };
        Assert.Equal("unverified", InstallationAuditor.Check("instance", device.Id, source, source, [absentComponent], ct: Ct).Items[0].Status);
        var escaped = view with { Components = [component with { Details = component.Details with { RelativePath = "../escape" } }] };
        Assert.Equal("unknown", InstallationAuditor.Check("instance", device.Id, source, source, [escaped], ct: Ct).Items[0].Components[0].Status);
        Assert.False(File.Exists(Path.Combine(source, "launch-profiles.json")));
        var metadata = new MetadataSnapshot("v17", "Game", "vn", new VisualNovel { Id = "v17", Title = "Game", Titles = [new() { Lang = "ja", Title = "正しい名前", Main = true }], Developers = [new() { Id = "p1", Name = "Developer" }] },
            [new() { Id = "p1", Name = "Developer" }], null, new("none"), DateTimeOffset.UtcNow, "test");
        var item = store.PersonalItems().Single();
        var book = new LibraryItemView(item, metadata, 0);
        var target = Path.Combine(dir.Path, "target");
        var suggestion = InstallationAuditor.Check("instance", device.Id, source, target, [view], ct: Ct, items: [book]).Items[0].Suggestion!;
        Assert.Equal("proposed_only", suggestion.State); Assert.Equal(Path.Combine(target, "Developer", "正しい名前"), suggestion.DestinationPath);
        var bundled = view with { Components = [component, component with { Id = "second-component" }] };
        Assert.Equal("manual_bundle", InstallationAuditor.Check("instance", device.Id, source, source, [bundled], ct: Ct, items: [book]).Items[0].Suggestion!.State);
        Assert.True(Directory.Exists(game)); Assert.False(Directory.Exists(Path.Combine(source, "Developer")));
        var editionPath = Directory.CreateDirectory(Path.Combine(source, "Studio", "Game Special Edition")).FullName;
        var edition = view with { Installation = installation with { Details = installation.Details with { RootPath = editionPath } } };
        Assert.Equal("preserve_variant", InstallationAuditor.Check("instance", device.Id, source, source, [edition], ct: Ct, items: [book]).Items[0].Suggestion!.State);
        var link = Path.Combine(source, "link");
        var command = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J"); command.ArgumentList.Add(link); command.ArgumentList.Add(game);
        using var process = System.Diagnostics.Process.Start(command)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try
        {
            var linked = view with { Installation = installation with { Details = installation.Details with { RootPath = link } } };
            Assert.Equal("unknown", InstallationAuditor.Check("instance", device.Id, source, source, [linked], ct: Ct).Items[0].Status);
        }
        finally { Directory.Delete(link); }
    }
    private sealed class Factory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Library:DataDirectory", directory)
            .UseSetting("Library:WriteKey", "quality-test-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task QualityApiReadIsPublicAuditWritesRequireKeyAndAreSdkReplayable()
    {
        using var dir = new TestDirectory(); await using var factory = new Factory(dir.Path); using var http = factory.CreateClient();
        var sdk = new WebLibraryClient(http, new("http://localhost/"), "quality-test-key", TimeSpan.FromSeconds(30));
        Assert.NotNull(await sdk.QualityAsync(ct: Ct));
        using var old = new HttpRequestMessage(HttpMethod.Post, "/api/v1/quality/installation-audits") { Content = JsonContent.Create(new { root_path = @"E:\Private" }) };
        old.Headers.Add("X-Library-Key", "quality-test-key"); old.Headers.Add("X-Library-Request", "1");
        using var response = await http.SendAsync(old, Ct); Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("E:", await response.Content.ReadAsStringAsync(Ct));

    }
}
