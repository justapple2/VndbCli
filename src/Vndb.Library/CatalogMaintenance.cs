namespace Vndb.Library;

/// <summary>One persisted queue step; the host owns triggers, cancellation and diagnostics.</summary>
public sealed class CatalogMaintenance(MetadataImports imports, ActivityImports activity, CatalogJobs jobs,
    SqliteLibraryStore store, TimeProvider time, DatasetImports? datasets = null)
{
    private DateTimeOffset _nextActivity = DateTimeOffset.MinValue;
    public async Task StepAsync(CancellationToken ct)
    {
        await imports.StepAsync(ct);
        if (time.GetUtcNow() >= _nextActivity)
        {
            activity.Step(File.ReadAllText(Path.Combine(store.DataDirectory, "service-instance-id")).Trim());
            _nextActivity = time.GetUtcNow().AddSeconds(30);
        }
        await jobs.StepAsync(ct);
        if (datasets is not null)
        {
            await datasets.StepSyncAsync(ct);
            // Local batches need no upstream pacing. Yield between short bursts so foreground reads
            // and other queues keep their opportunity; each existing transaction remains 25 entities.
            var start = time.GetTimestamp();
            for (var batch = 0; batch < 8; batch++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await datasets.StepAsync(ct) || time.GetElapsedTime(start) >= TimeSpan.FromMilliseconds(250)) break;
            }
        }
    }
}
