using System.Net;
using System.Text;
using System.Text.Json;
using Vndb.Cli;
using Xunit;

namespace Vndb.Tests;

public sealed class CliTests
{
    private const string NovelPage = """
        {"results":[{"id":"v17","title":"Test","alttitle":"作品名","released":"TBA","olang":"ja",
        "developers":[{"id":"p1","name":"Studio 1","original":"会社一"},{"id":"p2","name":"Studio 2"}]}],"more":true}
        """;

    private static CliApplication App(StubHandler handler, TextWriter output, TextWriter error, string? token = null) =>
        new(output, error, () => new HttpClient(handler, disposeHandler: false), name => name == "VNDB_API_TOKEN" ? token : null, VndbClientTests.FastOptions,
            new(CacheDirectory: Path.Combine(TestDirectory.ScratchRoot, "cli-cache-" + Guid.NewGuid().ToString("N"))));

    private static StubHandler NovelHandler()
    {
        StubHandler? handler = null;
        handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json(handler!.Requests[^1].Uri.EndsWith("producer", StringComparison.Ordinal) ?
            """{"results":[{"id":"p1","name":"Studio 1","original":"会社一"},{"id":"p2","name":"Studio 2"}],"more":false}""" : NovelPage)));
        return handler;
    }

    [Fact]
    public async Task JsonOutputHasPaginationAndNoStatusText()
    {
        using var handler = NovelHandler();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await App(handler, output, error).RunAsync(["vn", "search", "作品名", "--json", "--limit", "2", "--page", "3"], TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(3, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("page_size").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("items")[0].GetProperty("developers").GetArrayLength());
        Assert.Equal("client_vndb", document.RootElement.GetProperty("source").GetString());
        Assert.True(document.RootElement.GetProperty("pending_import").GetBoolean());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task TextDetailShowsEveryDeveloperAndPartialDate()
    {
        using var handler = NovelHandler();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await App(handler, output, error).RunAsync(["vn", "get", "https://vndb.org/v17"], TestContext.Current.CancellationToken));
        Assert.Contains("会社一", output.ToString());
        Assert.Contains("Studio 2", output.ToString());
        Assert.Contains("TBA", output.ToString());
        Assert.Contains("多家开发会社", output.ToString());
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("vn search")]
    [InlineData("vn get p98")]
    [InlineData("vn search test --limit 101")]
    [InlineData("producer vns p98 --page 0")]
    [InlineData("stats --timeout 0")]
    [InlineData("stats --timeout abc")]
    [InlineData("library list")]
    [InlineData("vn get v17 --save json")]
    [InlineData("vn search test --server invalid-server")]
    [InlineData("vn search test --source unknown")]
    [InlineData("stats --data-dir temporary-library")]
    [InlineData("stats --storage sqlite")]
    public async Task InvalidArgumentsReturnUsageWithoutNetwork(string command)
    {
        using var handler = new StubHandler(NovelPage);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await App(handler, output, error).RunAsync(command.Split(' '), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task MissingEntryAndEmptySearchHaveDifferentOutcomes()
    {
        using var handler = new StubHandler("""{"results":[],"more":false}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var app = App(handler, output, error);
        Assert.Equal(ExitCodes.NotFound, await app.RunAsync(["vn", "get", "v999999"], TestContext.Current.CancellationToken));
        Assert.Empty(output.ToString());
        Assert.Equal(ExitCodes.Success, await app.RunAsync(["vn", "search", "nothing", "--json"], TestContext.Current.CancellationToken));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task MissingTokenDoesNotMakeARequest()
    {
        using var handler = new StubHandler("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCodes.Usage, await App(handler, output, error).RunAsync(["authinfo"], TestContext.Current.CancellationToken));
        Assert.Contains("VNDB_API_TOKEN", error.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AuthinfoUsesEnvironmentAndDoesNotPrintToken()
    {
        using var handler = new StubHandler("""{"id":"u1","username":"Example","permissions":["listread"]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await App(handler, output, error, "test-token").RunAsync(["authinfo", "--json"], TestContext.Current.CancellationToken));
        Assert.Equal("Token test-token", handler.Requests[0].Authorization);
        Assert.DoesNotContain("test-token", output.ToString() + error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ApiErrorsUseStderrAndStableExitCode(HttpStatusCode status)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("private-response", status)));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCodes.Api, await App(handler, output, error).RunAsync(["stats", "--json"], TestContext.Current.CancellationToken));
        Assert.Empty(output.ToString());
        Assert.Contains(((int)status).ToString(), error.ToString());
        Assert.DoesNotContain("private-response", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransportFailureHasNetworkExitCode()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("network failure"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(ExitCodes.Network, await App(handler, output, error).RunAsync(["stats"], TestContext.Current.CancellationToken));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ExportIsUtf8WithoutBomAndDoesNotOverwriteExistingFiles()
    {
        var projectTemporaryRoot = TestDirectory.ScratchRoot;
        var directory = Path.Combine(projectTemporaryRoot, "vndb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "results.json");
            using var handler = NovelHandler();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var app = App(handler, output, error);
            string[] command = ["vn", "search", "test", "--json", "--output", path];
            var code = await app.RunAsync(command, TestContext.Current.CancellationToken);
            Assert.True(code == 0, error.ToString());
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
            Assert.Contains("作品名", Encoding.UTF8.GetString(bytes));
            Assert.Empty(output.ToString());
            Assert.Equal(ExitCodes.Output, await app.RunAsync(command, TestContext.Current.CancellationToken));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HelpNeedsNoNetwork()
    {
        using var handler = new StubHandler("{}");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await App(handler, output, error).RunAsync([], TestContext.Current.CancellationToken));
        Assert.Contains("producer", output.ToString());
        Assert.DoesNotContain("library", output.ToString());
        Assert.DoesNotContain("--save", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void DesktopProgramHasNoDatabaseOrLibraryDependency()
    {
        var references = typeof(CliApplication).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("Vndb.Library", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain("Vndb.Admin", references);
    }
}
