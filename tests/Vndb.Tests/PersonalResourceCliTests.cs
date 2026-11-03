using System.Text.Json;
using Vndb.Cli;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class PersonalResourceCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static CliApplication App(TestDirectory dir, StubHandler handler, StringWriter output, StringWriter error) =>
        new(output, error, () => new HttpClient(handler, false), _ => null, VndbClientTests.FastOptions, new(CacheDirectory: Path.Combine(dir.Path, "queries")));
    [Fact]
    public async Task PersonalCliQueuesOfflineJsonInTheSameServiceScopeWithoutNetwork()
    {
        using var dir = new TestDirectory(); using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No network"));
        using var output = new StringWriter(); using var error = new StringWriter(); var app = App(dir, handler, output, error);
        var file = Path.Combine(dir.Path, "change.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new PersonalOrganizationChange(0, new("list-a", "Synthetic list", "collection")), VndbJson.Options), Ct);
        Assert.Equal(0, await app.RunAsync(["personal", "queue", "--change-json", file, "--server", "http://organization.test/", "--json"], Ct));
        using var doc = JsonDocument.Parse(output.ToString()); Assert.Equal(1, doc.RootElement.GetProperty("pending").GetArrayLength());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["personal", "status", "--server", "http://organization.test/", "--json"], Ct));
        var scope = LocalQueryScope.ServerDirectory(Path.Combine(dir.Path, "queries"), "http://organization.test/");
        Assert.Single(PersonalOrganizationStore.View(await new PersonalOrganizationStore(scope, "http://organization.test/").ReadAsync(Ct)).Labels);
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task ResourceCliSavesAndInspectsLocalArchiveWithoutCreatingOwnershipOrExecutingFiles()
    {
        using var dir = new TestDirectory(); var state = Path.Combine(dir.Path, "state"); var item = M5Tests.Item();
        await new LocalLibraryStore(state).SaveAsync(new([], [item]), Ct);
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No network")); using var output = new StringWriter(); using var error = new StringWriter();
        var app = App(dir, handler, output, error); var file = Path.Combine(dir.Path, "record.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new LocalResourceData(item.Item.Id, "patch", dir.Path, "unknown-patch.zip"), VndbJson.Options), Ct);
        Assert.Equal(0, await app.RunAsync(["resources", "save", "--identity-dir", state, "--record-json", file, "--json"], Ct));
        using var saved = JsonDocument.Parse(output.ToString()); var id = saved.RootElement.GetProperty("id").GetString()!;
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["resources", "check", "--identity-dir", state, "--id", id, "--json"], Ct));
        using var facts = JsonDocument.Parse(output.ToString()); Assert.Equal("missing", facts.RootElement.GetProperty("status").GetString());
        Assert.Empty((await new LocalLibraryStore(state).ReadAsync(Ct)).Installations); Assert.Empty(handler.Requests); Assert.False(File.Exists(Path.Combine(dir.Path, "unknown-patch.zip")));
    }
}
