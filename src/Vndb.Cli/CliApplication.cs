using Vndb.Local;
using System.CommandLine;
using System.Text.Json;
using Vndb.Core;
using Vndb.Contracts;
using Vndb.Client;

namespace Vndb.Cli;

public sealed partial class CliApplication(
    TextWriter output,
    TextWriter error,
    Func<HttpClient>? httpClientFactory = null,
    Func<string, string?>? environment = null,
    VndbClientOptions? clientOptions = null,
    ClientSettings? settings = null)
{
    private readonly Option<string?> _server = new("--server") { Description = "Web 根地址；也可设置 VN_LIBRARY_URL。", Recursive = true };
    private readonly Option<string> _source = new("--source") { Description = "auto / web / vndb / cache。", DefaultValueFactory = _ => "auto", Recursive = true };
    private readonly Option<string?> _cacheDirectory = new("--cache-dir") { Description = "查询JSON根目录；其下按服务／离线作用域隔离。", Recursive = true };
    private readonly Option<string?> _config = new("--config") { Description = "客户端 JSON 配置文件（不保存密钥）。", Recursive = true };
    private readonly Option<bool> _json = new("--json")
    {
        Description = "输出 UTF-8 JSON，适合脚本处理。", Recursive = true
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
        return parsed.Errors.Count > 0 ? ExitCodes.Usage : code;
    }

    private RootCommand CreateRootCommand()
    {
        var root = new RootCommand("视觉小说查询工具：Web 优先、Core 回退及本机 JSON 快照（.NET 10）。")
        {
            _json, _outputFile, _timeout, _server, _source, _cacheDirectory, _config
        };
        var vn = new Command("vn", "查询视觉小说。");
        var search = new Command("search", "按标题、别名或发行版标题搜索，按相关性排序。") { _limit, _page };
        var query = new Argument<string>("query") { Description = "作品名，含空格时请使用引号。" };
        var developer = new Option<string?>("--developer") { Description = "限定开发会社 ID，例如 p166。" };
        search.Add(query);
        search.Add(developer);
        BindMetadata(search, parsed => new("vn_search", new(parsed.GetValue(query)!, parsed.GetValue(developer), parsed.GetValue(_page), parsed.GetValue(_limit))));

        var get = new Command("get", "按 v 开头的 ID 或 VNDB 链接获取完整作品资料。");
        var novelId = new Argument<string>("id") { Description = "例如 v17 或 https://vndb.org/v17。" };
        get.Add(novelId);
        BindMetadata(get, parsed => new("vn_get", new(), parsed.GetValue(novelId)!));
        vn.Add(search);
        vn.Add(get);
        var releases = new Command("releases", "查询已收录发行版本和语言；缺失时联网并缓存。");
        var releaseId = new Argument<string>("id");
        var refreshReleases = new Option<bool>("--refresh") { Description = "明确刷新发行目录。" };
        releases.Add(releaseId); releases.Add(refreshReleases);
        BindMetadata(releases, p => new("vn_releases", new(Mode: p.GetValue(refreshReleases) ? "explicit_refresh" : "local_first"), p.GetValue(releaseId)!));
        vn.Add(releases);
        root.Add(vn);

        var producer = new Command("producer", "查询会社、同人团体或个人开发者。");
        var producerSearch = new Command("search", "按会社名称或别名搜索。") { _limit, _page };
        var name = new Argument<string>("name") { Description = "例如 ゆずソフト 或 Yuzusoft。" };
        producerSearch.Add(name);
        BindMetadata(producerSearch, parsed => new("producer_search", new(parsed.GetValue(name)!, Page: parsed.GetValue(_page), PageSize: parsed.GetValue(_limit))));

        var producerGet = new Command("get", "按 p 开头的 ID 或 VNDB 链接获取会社详情。");
        var producerId = new Argument<string>("id") { Description = "例如 p166。" };
        producerGet.Add(producerId);
        BindMetadata(producerGet, parsed => new("producer_get", new(), parsed.GetValue(producerId)!));

        var producerVns = new Command("vns", "列出该会社参与开发的作品，按 VNDB ID 排序。") { _limit, _page };
        var vnsProducerId = new Argument<string>("id") { Description = "开发会社 ID 或链接。" };
        producerVns.Add(vnsProducerId);
        BindMetadata(producerVns, parsed => new("producer_vns", new(Page: parsed.GetValue(_page), PageSize: parsed.GetValue(_limit)), parsed.GetValue(vnsProducerId)!));
        producer.Add(producerSearch);
        producer.Add(producerGet);
        producer.Add(producerVns);
        root.Add(producer);

        var auth = new Command("authinfo", "验证 VNDB_API_TOKEN 环境变量中的令牌；公开查询不需要令牌。");
        Bind(auth, async (client, _, token) =>
        {
            var apiToken = (environment ?? Environment.GetEnvironmentVariable)("VNDB_API_TOKEN");
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("请先设置 VNDB_API_TOKEN 环境变量。令牌可在 https://vndb.org/u/tokens 创建。");
            return await client.GetAuthInfoAsync(apiToken, token);
        });
        root.Add(auth);

        var stats = new Command("stats", "获取明确范围的统计。");
        var scope = new Option<string>("--scope") { Description = "vndb（上游全站）或 library（Web 本地库）。", DefaultValueFactory = _ => "vndb" };
        stats.Add(scope);
        Bind(stats, async (client, parsed, token) =>
        {
            if (parsed.GetValue(scope) == "vndb") return await client.GetStatsAsync(token);
            if (parsed.GetValue(scope) != "library") throw new ArgumentException("--scope 须为 library 或 vndb。");
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var server = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server ?? throw new ArgumentException("本地库统计需要 --server。");
            using var webHttp = CreateHttpClient();
            return await new WebLibraryClient(webHttp, ClientSettings.ServerUri(server), Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout))).StatisticsAsync(token);
        });
        root.Add(stats);
        var sync = new Command("sync", "将本机 pending metadata 提交至 Web，保留原 JSON 并保存幂等回执。");
        Bind(sync, async (_, parsed, token) =>
        {
            var scope = QueryScope(parsed); var uri = ClientSettings.ServerUri(scope.Origin ?? throw new ArgumentException("sync需要明确Web地址。"));
            using var http = CreateHttpClient();
            return await new PendingMetadataSync(new WebLibraryClient(http, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout))), uri.AbsoluteUri, scope.DirectoryPath).SubmitAsync(token);
        });
        root.Add(sync);
        var device = new Command("device", "查看稳定本机身份、注册设备及明确认领旧机器记录；不扫描或启动游戏。");
        var identityDirectory = new Option<string?>("--identity-dir") { Description = "设备JSON所在目录；默认客户端用户数据目录。", Recursive = true };
        device.Add(identityDirectory);
        var showDevice = new Command("show", "创建或读取随机稳定设备ID，离线可用。");
        Bind(showDevice, async (_, parsed, token) => await LocalDeviceIdentity.LoadOrCreateAsync(parsed.GetValue(identityDirectory), token));
        device.Add(showDevice);
        var registerDevice = new Command("register", "向Web注册本机身份并报告本次连接，不按机器名自动认领历史。");
        Bind(registerDevice, async (_, parsed, token) =>
        {
            var identity = await LocalDeviceIdentity.LoadOrCreateAsync(parsed.GetValue(identityDirectory), token);
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var server = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server ?? throw new ArgumentException("设备注册需要 --server。");
            using var http = CreateHttpClient(); var sdk = new WebLibraryClient(http, ClientSettings.ServerUri(server), Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
            var found = (await sdk.DevicesAsync(token)).FirstOrDefault(d => d.Id == identity.Id) ??
                await sdk.RegisterDeviceAsync(new(identity.Id, new(identity.Name, identity.Platform)), "device-register-" + identity.Id, token);
            return await sdk.DeviceSeenAsync(found.Id, found.Revision, "device-seen-" + Guid.NewGuid().ToString("N"), token);
        });
        device.Add(registerDevice);
        var claimDevice = new Command("claim", "明确将指定旧设备映射到本机；需核对显示名及当前修订，不自动执行。");
        var legacyDeviceId = new Argument<string>("legacy-id"); var legacyRevision = new Option<long>("--expected-revision") { Required = true };
        claimDevice.Add(legacyDeviceId); claimDevice.Add(legacyRevision);
        Bind(claimDevice, async (_, parsed, token) =>
        {
            var identity = await LocalDeviceIdentity.LoadOrCreateAsync(parsed.GetValue(identityDirectory), token);
            var config = settings ?? ClientSettings.Load(parsed.GetValue(_config));
            var server = parsed.GetValue(_server) ?? Env("VN_LIBRARY_URL") ?? config.Server ?? throw new ArgumentException("设备认领需要 --server。");
            using var http = CreateHttpClient(); var sdk = new WebLibraryClient(http, ClientSettings.ServerUri(server), Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
            return await sdk.ClaimDeviceAsync(parsed.GetValue(legacyDeviceId)!, new(identity.Id, parsed.GetValue(legacyRevision)), "device-claim-" + Guid.NewGuid().ToString("N"), token);
        });
        device.Add(claimDevice); root.Add(device); AddOrganization(root); AddPresence(root); AddQueryData(root); AddLocalData(root); AddObservation(root);
        return root;
    }

    private void Bind(Command command, Func<VndbClient, ParseResult, CancellationToken, Task<object?>> action) =>
        command.SetAction((parsed, token) => ExecuteAsync(parsed, action, token));

    private string? Env(string name) => (environment ?? Environment.GetEnvironmentVariable)(name);
    private LocalQueryScope QueryScope(ParseResult parsed)
    {
        var dataRoot = Env("VN_DESKTOP_HOME") ?? LocalDataRoot.Default;
        var config = settings ?? ClientSettings.Load(parsed.GetValue(_config) ?? ClientSettings.DefaultFile(dataRoot));
        return LocalQueryScope.Resolve(dataRoot, config, parsed.GetValue(_server), parsed.GetValue(_cacheDirectory), Env);
    }
    private HttpClient CreateHttpClient()
    {
        var http = httpClientFactory?.Invoke() ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }
    private void BindMetadata(Command command, Func<ParseResult, ClientQuery> request) =>
        Bind(command, async (core, parsed, token) =>
        {
            var query = request(parsed).Validate();
            var source = parsed.GetValue(_source)!;
            if (source is not ("auto" or "web" or "vndb" or "cache")) throw new ArgumentException("无效 --source。");
            var scope = QueryScope(parsed); var uri = scope.Origin is null ? null : ClientSettings.ServerUri(scope.Origin); var path = scope.DirectoryPath;
            using var webHttp = CreateHttpClient();
            var web = uri is null ? null : new WebLibraryClient(webHttp, uri, Env("VN_LIBRARY_KEY"), TimeSpan.FromSeconds(parsed.GetValue(_timeout)));
            var result = await new QueryRouter(core, new JsonSnapshotStore(path, uri?.ToString()), web).QueryAsync(query, source, token);
            if (web is not null && uri is not null && !string.IsNullOrWhiteSpace(Env("VN_LIBRARY_KEY")) && result.Source is "library" or "query_cache" or "vndb")
            {
                try { await new PendingMetadataSync(web, uri.ToString(), path).SubmitAsync(token, maximumFiles: 1); }
                catch (Exception e) when (e is ClientApiException or WebUnavailableException or IOException or JsonException)
                { await error.WriteLineAsync("查询成功；离线批次暂未同步，原 JSON 已保留，可使用 sync 查看／重试。"); }
            }
            return query.Kind is "vn_get" or "producer_get" && result.Items.Count == 0 ? null : result;
        });

    private async Task<int> ExecuteAsync(ParseResult parsed,
        Func<VndbClient, ParseResult, CancellationToken, Task<object?>> action, CancellationToken cancellationToken)
    {
        try
        {
            var seconds = parsed.GetValue(_timeout);
            if (seconds is < 1 or > 300) throw new ArgumentException("--timeout 必须在 1 到 300 秒之间。");
            using var httpClient = CreateHttpClient();
            using var client = new VndbClient(httpClient,
                (clientOptions ?? new()) with { RequestTimeout = TimeSpan.FromSeconds(seconds) });
            var data = await action(client, parsed, cancellationToken);
            if (data is null)
            {
                await error.WriteLineAsync("未找到对应的 VNDB 条目。");
                return ExitCodes.NotFound;
            }
            var content = parsed.GetValue(_json)
                ? JsonSerializer.Serialize(data, data.GetType(), VndbJson.Options)
                : TextOutput.Format(data);
            var path = parsed.GetValue(_outputFile);
            if (path is null)
            {
                await output.WriteLineAsync(content.AsMemory(), cancellationToken);
            }
            else
            {
                if (data is MetadataResponse metadata)
                    await JsonSnapshotStore.AtomicWriteAsync(path, new MetadataBatch(metadata.Items, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D")), false, cancellationToken);
                else await OutputFile.WriteAsync(path, content, cancellationToken);
            }
            return ExitCodes.Success;
        }
        catch (ImportValidationException exception)
        {
            if (parsed.GetValue(_json)) await output.WriteLineAsync(JsonSerializer.Serialize(new ApiProblem(exception.Code, exception.Message, exception.Status), VndbJson.Options));
            return await FailAsync(ExitCodes.Api, exception.Code + ": " + exception.Message);
        }
        catch (ArgumentException exception) { return await FailAsync(ExitCodes.Usage, exception.Message); }
        catch (PlatformNotSupportedException exception) { return await FailAsync(ExitCodes.Usage, exception.Message); }
        catch (PresenceConflictException exception)
        {
            if (parsed.GetValue(_json)) await output.WriteLineAsync(JsonSerializer.Serialize(exception.State, VndbJson.Options));
            return await FailAsync(ExitCodes.Api, exception.Message);
        }
        catch (InvalidOperationException exception) { return await FailAsync(ExitCodes.Usage, exception.Message); }
        catch (ClientApiException exception) { return await FailAsync(ExitCodes.Api, exception.Message); }
        catch (WebUnavailableException) { return await FailAsync(ExitCodes.Network, "Web 服务不可达。"); }
        catch (OfflineCacheMissException exception) { return await FailAsync(ExitCodes.Network, exception.Message); }
        catch (VndbApiException exception) { return await FailAsync(ExitCodes.Api, exception.Message); }
        catch (TimeoutException exception) { return await FailAsync(ExitCodes.Network, exception.Message); }
        catch (HttpRequestException) { return await FailAsync(ExitCodes.Network, "无法连接 VNDB，请检查网络、DNS 或 HTTPS_PROXY 设置。"); }
        catch (JsonException) { return await FailAsync(ExitCodes.Api, "VNDB 返回的数据格式无法解析，API 可能发生变化。"); }
        catch (OperationCanceledException) { return await FailAsync(ExitCodes.Cancelled, "操作已取消。"); }
        catch (InvalidDataException) { return await FailAsync(ExitCodes.Output, "返回的数据或输出文件无效。"); }
        catch (IOException exception) { return await FailAsync(ExitCodes.Output, $"本地操作／输出停止：{exception.Message}"); }
        catch (UnauthorizedAccessException) { return await FailAsync(ExitCodes.Output, "没有权限写入指定的输出文件。"); }
    }

    private async Task<int> FailAsync(int code, string message)
    {
        await error.WriteLineAsync($"错误：{message}");
        return code;
    }
}
