using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class DatasetImports
{
    private static readonly Uri LatestDataset = new("https://dl.vndb.org/dump/vndb-db-latest.tar.zst");
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _nextSyncPollTicks;
    public DatasetSyncStatus SyncStatus() => store.DatasetSyncStatus(_time.GetUtcNow(), workerEnabled);
    public DatasetSyncSettings ConfigureSync(DatasetSyncWrite request, string key)
    {
        if (request.Enabled && !workerEnabled) throw new ArgumentException("Web后台任务未启用；请先启用CatalogJobs:Enabled。");
        var saved = store.SaveDatasetSync(request, key, _time.GetUtcNow()); Volatile.Write(ref _nextSyncPollTicks, 0); return saved;
    }
    private static void RequireSyncKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("需要有效Idempotency-Key。");
    }
    private static string SnapshotDate(Uri location, DateTimeOffset now)
    {
        var match = Regex.Match(location.AbsolutePath, @"^/dump/vndb-db-(\d{4}-\d{2}-\d{2})\.tar\.zst$", RegexOptions.CultureInvariant);
        if (location.Scheme != "https" || location.Host != LatestDataset.Host || !location.IsDefaultPort || location.UserInfo.Length != 0 ||
            location.Query.Length != 0 || location.Fragment.Length != 0 || !match.Success ||
            !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date.Year < 2000 || date > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1)) throw new InvalidDataException("官方最新包未返回有效的固定来源日期。");
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    public async Task<DatasetSyncStatus> CheckLatestAsync(string key, CancellationToken ct = default)
    {
        RequireSyncKey(key); await _syncGate.WaitAsync(ct);
        try
        {
            var claim = store.BeginDatasetCheck(key, _time.GetUtcNow());
            if (claim.Token is null || !store.OwnsDatasetCheck(claim.Token)) return SyncStatus();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Head, LatestDataset);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var now = _time.GetUtcNow();
                if ((int)response.StatusCode is 429 or 503)
                {
                    var retry = response.Headers.RetryAfter?.Date ?? now.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
                    retry = retry < now.AddMinutes(15) ? now.AddMinutes(15) : retry > now.AddDays(1) ? now.AddDays(1) : retry;
                    store.FinishDatasetCheck(claim.Token, null, "官方源暂时限流或维护，请在允许时间后重试。", retry, now);
                }
                else
                {
                    if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } target)
                        throw new InvalidDataException("官方最新包没有返回日期化重定向。");
                    var date = SnapshotDate(new Uri(LatestDataset, target), now);
                    store.FinishDatasetCheck(claim.Token, date, null, null, now);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { store.FinishDatasetCheck(claim.Token, null, "检查已中断，保留上次发现信息。", null, _time.GetUtcNow()); throw; }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or ArgumentException or OperationCanceledException)
            { store.FinishDatasetCheck(claim.Token, null, "最新资料检查未完成；保留本库及上次发现信息，请稍后重试。", null, _time.GetUtcNow()); }
            return SyncStatus();
        }
        finally { _syncGate.Release(); }
    }
    public Task<DatasetSyncResult> UpdateLatestAsync(string key, CancellationToken ct = default) => UpdateLatestCoreAsync(key, null, ct);
    private async Task<DatasetSyncResult> UpdateLatestCoreAsync(string key, long? scheduleRevision, CancellationToken ct)
    {
        RequireSyncKey(key);
        var checkKey = "update-check-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await CheckLatestAsync(checkKey, ct); ct.ThrowIfCancellationRequested();
        return store.UpdateLatestDataset(key, _time.GetUtcNow(), scheduleRevision);
    }
    public async Task StepSyncAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow(); if (!workerEnabled || now.UtcTicks < Volatile.Read(ref _nextSyncPollTicks)) return;
        Volatile.Write(ref _nextSyncPollTicks, now.AddMinutes(1).UtcTicks);
        if (store.BeginScheduledDatasetSync(now) is not { } revision) return;
        await UpdateLatestCoreAsync("scheduled-" + Guid.NewGuid().ToString("N"), revision, ct);
    }
}
