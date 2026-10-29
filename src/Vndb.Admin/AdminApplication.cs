using System.CommandLine;
using System.Text.Json;
using Vndb.Core;
using Vndb.Library;
using System.Net.Http.Json;

namespace Vndb.Admin;

public sealed partial class AdminApplication(
    TextWriter output,
    TextWriter error,
    Func<HttpClient>? httpClientFactory = null,
    Func<string, string?>? environment = null,
    VndbClientOptions? clientOptions = null)
{
    private readonly Option<string> _dataDirectory = new("--data-dir")
    {
        Description = "本地资料目录。", Recursive = true,
        DefaultValueFactory = _ => Environment.GetEnvironmentVariable("VNDB_DATA_DIR") ?? "/data"
    };
    private readonly Option<string?> _server = new("--server")
    {
        Description = "NAS 资料库地址，也可设置 VN_LIBRARY_URL。", Recursive = true,
        DefaultValueFactory = _ => Environment.GetEnvironmentVariable("VN_LIBRARY_URL") ?? Environment.GetEnvironmentVariable("VNDB_LIBRARY_URL")
    };
    private readonly Option<string> _storage = new("--storage")
    {
        Description = "library 命令的本地存储类型：sqlite / json。", Recursive = true,
        DefaultValueFactory = _ => "sqlite"
    };
    private readonly Option<bool> _json = new("--json")
    {
        Description = "输出 UTF-8 JSON，适合脚本处理。", Recursive = true
    };
    private readonly Option<bool> _offlineMaintenance = new("--offline-maintenance")
    {
        Description = "显式离线维护：允许直接写项目数据库。日常写入必须使用 --server。", Recursive = true
    };
    private readonly Option<string?> _outputFile = new("--output", "-o")
    {
        Description = "保存结果到新文件（不覆盖已有文件）。", Recursive = true
    };
    private readonly Option<int> _timeout = new("--timeout")
    {
        Description = "单次 HTTP 请求超时秒数（1–300），重试等待另计。",
        DefaultValueFactory = _ => 30, Recursive = true
    };
    private readonly Option<int> _limit = new("--limit")
    {
        Description = "每页结果数（1–100）。", DefaultValueFactory = _ => 10, Recursive = true
    };
    private readonly Option<int> _page = new("--page")
    {
        Description = "页码，从 1 开始。", DefaultValueFactory = _ => 1, Recursive = true
    };

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var root = CreateRootCommand();
        var parsed = root.Parse(args.Length == 0 ? ["--help"] : args);
        var code = await parsed.InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false
        }, cancellationToken);
        return parsed.Errors.Count > 0 ? AdminExitCodes.Usage : code;
    }

    private RootCommand CreateRootCommand()
    {
        var root = new RootCommand("NAS 容器内的资料库运维工具；日常管理使用 Web 页面。")
        {
            _json, _outputFile, _timeout, _dataDirectory, _server, _storage, _offlineMaintenance
        };
        var library = new Command("library", "导入、离线浏览或备份本地/NAS 资料库。");
        var import = new Command("import", "收录指定 VNDB ID 及封面，已存在时直接读取；--refresh 强制刷新。");
        var importId = new Argument<string>("id");
        var refresh = new Option<bool>("--refresh");
        import.Add(importId);
        import.Add(refresh);
        Bind(import, async (client, parsed, token) =>
        {
            if (parsed.GetValue(_server) is { } server)
                return await RemoteAsync<LibraryEntry>(server, "api/imports/" + VndbId.Normalize(parsed.GetValue(importId)!, 'v') +
                    "?refresh=" + parsed.GetValue(refresh).ToString().ToLowerInvariant(), true, token);
            if (!parsed.GetValue(_offlineMaintenance)) throw new ArgumentException("日常导入请用 --server；受控离线维护需要 --offline-maintenance。");
            var store = Store(parsed, parsed.GetValue(_storage)!);
            using var coversHttp = CoverHttp();
            using var importer = new LibraryImporter(client, new(coversHttp, store.DataDirectory), store);
            return await importer.ImportAsync(parsed.GetValue(importId)!, parsed.GetValue(refresh), token);
        });
        var list = new Command("list", "离线搜索资料库；--server 可查询 NAS。") { _limit, _page };
        var localQuery = new Option<string?>("--query");
        var localProducer = new Option<string?>("--producer");
        list.Add(localQuery);
        list.Add(localProducer);
        Bind(list, async (_, parsed, token) =>
        {
            var q = parsed.GetValue(localQuery);
            var p = parsed.GetValue(localProducer);
            if (parsed.GetValue(_server) is { } server)
                return await RemoteAsync<LibraryPage>(server, $"api/library?q={Uri.EscapeDataString(q ?? "")}&producer={Uri.EscapeDataString(p ?? "")}&page={parsed.GetValue(_page)}&limit={parsed.GetValue(_limit)}", false, token);
            return Store(parsed, parsed.GetValue(_storage)!).List(q, p, parsed.GetValue(_page), parsed.GetValue(_limit));
        });
        var backup = new Command("backup", "在线备份本地 SQLite 元数据到新文件；封面需另外备份。");
        var destination = new Argument<string>("path");
        backup.Add(destination);
        Bind(backup, (_, parsed, _) =>
        {
            if (parsed.GetValue(_server) is not null || parsed.GetValue(_storage) != "sqlite") throw new ArgumentException("backup 只支持本地 SQLite；NAS 请在容器中执行。");
            ((SqliteLibraryStore)Store(parsed, "sqlite")).Backup(parsed.GetValue(destination)!);
            return Task.FromResult<object?>(new BackupResult(Path.GetFullPath(parsed.GetValue(destination)!)));
        });
        library.Add(import);
        library.Add(list);
        library.Add(backup);
        AddRecovery(library);
        AddDumpProbe(library);
        var sync = new Command("sync-archive", "已停用的历史入口；目录证据仅保存在本机，不发送到Web。");
        var planFile = new Argument<string>("plan");
        var machine = new Option<string>("--machine") { DefaultValueFactory = _ => Environment.MachineName };
        var stage = new Option<string>("--state") { DefaultValueFactory = _ => "planned", Description = "planned / archived / restored" };
        var journal = new Option<string?>("--journal");
        sync.Add(planFile); sync.Add(machine); sync.Add(stage); sync.Add(journal);
        Bind(sync, (_, _, _) => throw new ArgumentException("sync-archive 已停用：目录结构与恢复证据仅保存在本机，请使用 vndb organize；Web只同步本地存在布尔值。"));
        library.Add(sync);
        var importPlays = new Command("import-plays", "导入已规范化的个人游玩记录 JSON；同一源文件行重复导入不覆盖手动编辑。");
        var playFile = new Argument<string>("file");
        importPlays.Add(playFile);
        Bind(importPlays, async (_, parsed, token) =>
        {
            if (parsed.GetValue(_server) is null && !parsed.GetValue(_offlineMaintenance)) throw new ArgumentException("日常导入请用 --server；受控离线维护需要 --offline-maintenance。");
            var batch = JsonSerializer.Deserialize<PlayImportBatch>(await File.ReadAllTextAsync(parsed.GetValue(playFile)!, token), VndbJson.Options)
                ?? throw new ArgumentException("游玩记录导入文件为空。");
            return parsed.GetValue(_server) is { } server
                ? await RemoteAsync<PlayImportResult>(server, "api/plays/imports", true, token, batch)
                : Store(parsed, parsed.GetValue(_storage)!).ImportPlays(batch);
        });
        library.Add(importPlays);
        var plays = new Command("plays", "查询本地或 NAS 个人游玩记录。") { _limit, _page };
        var playQuery = new Option<string?>("--query");
        var playState = new Option<string?>("--status");
        var playYear = new Option<int?>("--year");
        var unmatched = new Option<bool>("--unmatched");
        plays.Add(playQuery); plays.Add(playState); plays.Add(playYear); plays.Add(unmatched);
        Bind(plays, async (_, parsed, token) => parsed.GetValue(_server) is { } server
            ? await RemoteAsync<PlayPage>(server, $"api/plays?q={Uri.EscapeDataString(parsed.GetValue(playQuery) ?? "")}&status={Uri.EscapeDataString(parsed.GetValue(playState) ?? "")}&unmatched={parsed.GetValue(unmatched).ToString().ToLowerInvariant()}&page={parsed.GetValue(_page)}&limit={parsed.GetValue(_limit)}" +
                (parsed.GetValue(playYear) is { } year ? $"&year={year}" : ""), false, token)
            : Store(parsed, parsed.GetValue(_storage)!).Plays(parsed.GetValue(playQuery), parsed.GetValue(playState), year: parsed.GetValue(playYear), unmatched: parsed.GetValue(unmatched), page: parsed.GetValue(_page), limit: parsed.GetValue(_limit)));
        library.Add(plays);
        root.Add(library);
        return root;
    }

    private void Bind(Command command, Func<VndbClient, ParseResult, CancellationToken, Task<object?>> action) =>
        command.SetAction((parsed, token) => ExecuteAsync(parsed, action, token));

    private async Task<int> ExecuteAsync(ParseResult parsed,
        Func<VndbClient, ParseResult, CancellationToken, Task<object?>> action, CancellationToken cancellationToken)
    {
        try
        {
            var seconds = parsed.GetValue(_timeout);
            if (seconds is < 1 or > 300) throw new ArgumentException("--timeout 必须在 1 到 300 秒之间。");
            using var httpClient = httpClientFactory?.Invoke() ?? new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
            using var client = new VndbClient(httpClient,
                (clientOptions ?? new()) with { RequestTimeout = TimeSpan.FromSeconds(seconds) });
            var data = await action(client, parsed, cancellationToken);
            if (data is null)
            {
                await error.WriteLineAsync("未找到对应的 VNDB 条目。");
                return AdminExitCodes.NotFound;
            }
            var content = JsonSerializer.Serialize(data, data.GetType(), VndbJson.Options);
            var path = parsed.GetValue(_outputFile);
            if (path is null)
            {
                await output.WriteLineAsync(content.AsMemory(), cancellationToken);
            }
            else
            {
                await AdminOutputFile.WriteAsync(path, content, cancellationToken);
            }
            return AdminExitCodes.Success;
        }
        catch (ArgumentException exception) { return await FailAsync(AdminExitCodes.Usage, exception.Message); }
        catch (PlayConflictException exception) { return await FailAsync(AdminExitCodes.Output, exception.Message); }
        catch (VndbApiException exception) { return await FailAsync(AdminExitCodes.Api, exception.Message); }
        catch (TimeoutException exception) { return await FailAsync(AdminExitCodes.Network, exception.Message); }
        catch (HttpRequestException) { return await FailAsync(AdminExitCodes.Network, "无法连接 VNDB，请检查网络、DNS 或 HTTPS_PROXY 设置。"); }
        catch (JsonException) { return await FailAsync(AdminExitCodes.Api, "VNDB 返回的数据格式无法解析，API 可能发生变化。"); }
        catch (OperationCanceledException) { return await FailAsync(AdminExitCodes.Cancelled, "操作已取消。"); }
        catch (InvalidDataException) { return await FailAsync(AdminExitCodes.Output, "资料库或文件数据无效，请检查数据库版本与本地数据。"); }
        catch (IOException exception) { return await FailAsync(AdminExitCodes.Output, $"无法写入结果：{exception.Message}"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return await FailAsync(AdminExitCodes.Output, "SQLite 读写失败，请检查数据目录、数据库版本和文件锁。"); }
        catch (UnauthorizedAccessException) { return await FailAsync(AdminExitCodes.Output, "没有权限写入指定的输出文件。"); }
    }

    private ILibraryStore Store(ParseResult parsed, string kind) => kind switch
    {
        "sqlite" => new SqliteLibraryStore(parsed.GetValue(_dataDirectory)!),
        "json" => new JsonLibraryStore(parsed.GetValue(_dataDirectory)!),
        _ => throw new ArgumentException("--storage 必须是 sqlite 或 json。")
    };
    private static HttpClient CoverHttp() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(35) };
    private async Task<T?> RemoteAsync<T>(string server, string path, bool post, CancellationToken cancellationToken, object? body = null)
    {
        if (!Uri.TryCreate(server.TrimEnd('/') + '/', UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || baseUri.UserInfo.Length > 0 || baseUri.AbsolutePath != "/" || baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
            throw new ArgumentException("--server 须为不含路径或凭据的 http/https 服务地址。");
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, new Uri(baseUri, path));
        if (body is not null) request.Content = JsonContent.Create(body, options: VndbJson.Options);
        request.Headers.Add("X-Library-Request", "1");
        var key = (environment ?? Environment.GetEnvironmentVariable)("VN_LIBRARY_KEY") ?? (environment ?? Environment.GetEnvironmentVariable)("VNDB_LIBRARY_KEY");
        if (key is not null && key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw new ArgumentException("VNDB_LIBRARY_KEY 不得包含空白或控制字符。");
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("X-Library-Key", key);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new ArgumentException("NAS 管理密钥无效，请设置 VNDB_LIBRARY_KEY。");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(VndbJson.Options, cancellationToken);
    }

    private sealed record BackupResult(string Path);

    private async Task<int> FailAsync(int code, string message)
    {
        await error.WriteLineAsync($"错误：{message}");
        return code;
    }
}
