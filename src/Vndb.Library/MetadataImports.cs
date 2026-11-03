using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

/// <summary>One durable metadata consumer, called by the existing container worker.</summary>
public sealed class MetadataImports(SqliteLibraryStore store, VndbClient core, LibraryImporter importer,
    CoverDownloader covers, TimeProvider time, ImportLimits limits) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextJob;
    public ImportBatchReceipt Preview(ImportPreviewRequest request)
    {
        var parsed = MetadataInput.Parse(request.Payload, limits);
        return store.PreviewMetadata(parsed.Items, parsed.ClientId, parsed.RawSha, request.AddToLibrary);
    }
    public ImportBatchReceipt Sync(JsonElement payload, string key)
    {
        var parsed = MetadataInput.Parse(payload, limits);
        var preview = store.PreviewMetadata(parsed.Items, parsed.ClientId, parsed.RawSha, false);
        _ = store.CommitMetadata(preview.Id, key);
        // Deduplicated content may have been first submitted with another batch ID/formatting.
        // A sync response proves this accepted request; the stored cohort evidence remains unchanged.
        return store.GetMetadataBatch(preview.Id) with { ClientBatchId = parsed.ClientId, RawSha256 = parsed.RawSha };
    }
    public async Task<bool> StepAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (store.ProcessMetadataItem()) return true;
            if (_nextJob > time.GetUtcNow()) return false;
            var job = store.BeginMetadataJob(time.GetUtcNow());
            if (job is null) return false;
            try
            {
                IReadOnlyList<MetadataSnapshot> matches;
                if (job.VndbId?.StartsWith('v') == true)
                {
                    var local = store.Get(job.VndbId);
                    if (local?.Provenance == "client_vndb_import")
                    {
                        var revision = store.Provenance(local.Novel.Id)!.Revision;
                        var originalProducers = local.Producers;
                        // Enrichment of an offline snapshot only fills assets/company details; it never silently replaces its novel text.
                        var producers = local.Producers;
                        if (local.Completeness != "full_text")
                        {
                            var full = new List<Producer>();
                            foreach (var chunk in local.Novel.Developers.Select(p => p.Id).Distinct().Chunk(100)) full.AddRange((await core.GetProducersAsync(chunk, ct)).Results);
                            if (!local.Novel.Developers.All(d => full.Any(p => p.Id == d.Id))) throw new InvalidDataException("会社详情暂缺。");
                            producers = local.Producers.Select(p => full.FirstOrDefault(f => f.Id == p.Id) ?? p).ToArray();
                        }
                        var asset = local.Cover;
                        if (local.Novel.Image is { } image && LibraryRules.NeedsCover(local, store.DataDirectory)) asset = await covers.DownloadAsync(image, ct);
                        local = local with { Producers = producers, Cover = asset, CoverStatus = asset is null ? "none" : "ready", CoverError = null,
                            Completeness = local.Novel.Developers.All(d => producers.Any(p => p.Id == d.Id)) ? "full_text" : "partial_text" };
                        store.SaveEnrichment(local, revision, originalProducers);
                    }
                    else local = await importer.ImportAsync(job.VndbId, cancellationToken: ct);
                    if (local?.CoverStatus == "failed") throw new IOException("封面尚未下载，保留资料并按现有队列退避重试。");
                    matches = local is null ? [] : [Snapshot(local)];
                }
                else if (job.VndbId?.StartsWith('p') == true)
                {
                    var producer = await core.GetProducerAsync(job.VndbId, ct);
                    if (producer is not null) store.SaveProducer(new(producer, time.GetUtcNow()));
                    matches = producer is null ? [] : [new(producer.Id, producer.Name, "producer", null, [], producer, new("not_applicable"), time.GetUtcNow(), "web_vndb")];
                }
                else
                {
                    if (job.Title.Length > 200) throw new ArgumentException("标题过长，请在书架编辑后查询。");
                    var found = await core.SearchNovelsAsync(job.Title, 10, cancellationToken: ct);
                    var ids = found.Results.Select(n => n.Id).Distinct().ToArray();
                    var details = ids.Length == 0 ? [] : (await core.GetNovelsAsync(ids, ct)).Results;
                    matches = (await importer.SaveFetchedManyAsync(details, ct)).Select(Snapshot).ToArray();
                }
                store.FinishMetadataJob(job, matches, null, time.GetUtcNow());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or TimeoutException or VndbApiException or JsonException or ArgumentException or IOException or OperationCanceledException or PlayConflictException)
            { store.FinishMetadataJob(job, null, "资料补全未完成；保留输入和快照，可重试或人工核对。", time.GetUtcNow()); }
            _nextJob = time.GetUtcNow().AddSeconds(5); return true;
        }
        finally { _gate.Release(); }
    }
    public static MetadataSnapshot Snapshot(LibraryEntry entry) => new(entry.Novel.Id, entry.Novel.Title, "vn", entry.Novel,
        entry.Producers, null, new(entry.CoverStatus, entry.Cover is null ? null : $"/api/v1/vns/{entry.Novel.Id}/cover", entry.Cover?.ContentType, entry.Cover?.Hash),
        entry.UpdatedAt, entry.Provenance, entry.Completeness);
    public void Dispose() => _gate.Dispose();
}
