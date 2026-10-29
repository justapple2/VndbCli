using System.Text.Json;
using Vndb.Admin;
using Vndb.Cli;
using Xunit;

namespace Vndb.Tests;

public sealed class AdminTests
{
    private static AdminApplication App(StubHandler handler, TextWriter output, TextWriter error) =>
        new(output, error, () => new HttpClient(handler, disposeHandler: false), clientOptions: VndbClientTests.FastOptions);
    [Fact]
    public async Task MaintenanceImportKeepsCompleteMetadata()
    {
        using var directory = new TestDirectory();
        using var handler = new StubHandler((call, _) => Task.FromResult(StubHandler.Json(call == 1
            ? """{"more":false,"results":[{"id":"v17","title":"Test","aliases":["别名"],"developers":[{"id":"p98","name":"Studio"}]}]}"""
            : """{"more":false,"results":[{"id":"p98","name":"Studio","original":"会社","type":"co"}]}""")));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await App(handler, output, error).RunAsync(["library", "import", "v17", "--offline-maintenance", "--storage", "json", "--data-dir", directory.Path, "--json"], TestContext.Current.CancellationToken));
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("v17", result.RootElement.GetProperty("novel").GetProperty("id").GetString());
        var saved = new Vndb.Library.JsonLibraryStore(directory.Path).Get("v17");
        Assert.Equal("会社", saved!.Producers[0].Original);
        Assert.Contains("别名", saved.Novel.Aliases);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task FutureDatabaseVersionHasAControlledError()
    {
        using var directory = new TestDirectory();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(directory.Path, "library.db") + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        using var handler = new StubHandler("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCodes.Output, await App(handler, output, error).RunAsync(["library", "list", "--data-dir", directory.Path], TestContext.Current.CancellationToken));
        Assert.Contains("资料库", error.ToString());
        Assert.Empty(handler.Requests);
    }

}
