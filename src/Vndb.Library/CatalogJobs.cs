namespace Vndb.Library;

public sealed class CatalogJobs(CatalogEnricher catalog, IAutomationStore store, TimeProvider time)
{
    public CatalogJob? Queue(CatalogJobRequest request)
    {
        AutomationRules.Interval(request.IntervalSeconds);
        if (request.Scope is not (null or "played" or "local") || request.Keys?.Count > 5000) throw new ArgumentException("无效的后台补全范围。");
        var keys = request.Keys?.ToHashSet(StringComparer.Ordinal);
        var rows = catalog.Rows().Where(r => (keys is null || keys.Contains(r.Source.Key)) &&
            (request.Scope != "played" || r.Source.PlayIds.Count > 0) && (request.Scope != "local" || r.Source.LocalIds.Count + r.Source.ArchiveIds.Count > 0) &&
            (r.Status is "pending" or "error" || request.RetryUnmatched && (r.Status is "needs_review" or "not_found"))).ToArray();
        return rows.Length == 0 ? null : store.QueueCatalogJob(rows.Select(r => new CatalogTask(r.Source.Key, r.Source.Title, request.RetryUnmatched)).ToArray(), request.IntervalSeconds, time.GetUtcNow());
    }
    public async Task<bool> StepAsync(CancellationToken token = default)
    {
        var job = store.BeginCatalogJob(time.GetUtcNow());
        if (job is null) return false;
        var item = job.Tasks[job.Cursor];
        var row = catalog.Rows().FirstOrDefault(r => r.Source.Key == item.Key);
        string outcome;
        if (row is null) outcome = "skipped";
        else if (row.Status == "matched" || !item.Force && (row.Status is "needs_review" or "not_found")) outcome = row.Status;
        else
        {
            var result = await catalog.CompleteAsync(new(1, item.Key, true), token);
            outcome = result.Results.FirstOrDefault()?.Status ?? "skipped";
        }
        store.FinishCatalogItem(job.Id, item.Key, outcome, time.GetUtcNow(), outcome == "error" ? "上游资料获取失败，已保留进度；按退避间隔最多尝试三次。" : null);
        return true;
    }
}
