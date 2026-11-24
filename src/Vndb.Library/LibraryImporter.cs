using Vndb.Core;

namespace Vndb.Library;

public sealed class LibraryImporter(VndbClient client, CoverDownloader covers, ILibraryStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<LibraryEntry?> ImportAsync(string id, bool refresh = false, CancellationToken cancellationToken = default)
    {
        id = VndbId.Normalize(id, 'v');
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = store.Get(id);
            if (existing is not null && !refresh && !LibraryRules.NeedsCover(existing, store.DataDirectory)) return existing;
            var novel = await client.GetNovelAsync(id, cancellationToken);
            return novel is null ? null : await SaveCoreAsync(novel, existing, cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public async Task<LibraryEntry> SaveFetchedAsync(VisualNovel novel, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await SaveCoreAsync(novel, store.Get(novel.Id), cancellationToken); }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<LibraryEntry>> SaveFetchedManyAsync(IReadOnlyList<VisualNovel> novels,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (novels.Count == 0) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cache = new Dictionary<string, Producer>(StringComparer.Ordinal);
            var requested = novels.SelectMany(n => n.Developers).Select(p => p.Id).Distinct().ToArray();
            var known = store is SqliteLibraryStore sqlite ? sqlite.GetProducers(requested) : requested.Select(store.GetProducer).OfType<StoredProducer>().ToArray();
            foreach (var producer in known) cache.TryAdd(producer.Producer.Id, producer.Producer);
            var missing = requested.Where(id => !cache.ContainsKey(id)).ToArray();
            foreach (var ids in missing.Chunk(100))
                foreach (var producer in (await client.GetProducersAsync(ids, cancellationToken)).Results) cache[producer.Id] = producer;
            var results = new List<LibraryEntry>();
            foreach (var novel in novels.DistinctBy(n => n.Id))
                results.Add(await SaveCoreAsync(novel, store.Get(novel.Id), cancellationToken, cache));
            return results;
        }
        finally { _gate.Release(); }
    }

    private async Task<LibraryEntry> SaveCoreAsync(VisualNovel novel, LibraryEntry? existing, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Producer>? cachedProducers = null)
    {
        novel = novel with { ReleaseCatalog = ReleaseRules.Prefer(novel.ReleaseCatalog, existing?.Novel.ReleaseCatalog) };
        var producers = new List<Producer>();
        foreach (var d in novel.Developers.DistinctBy(p => p.Id))
            producers.Add(cachedProducers is not null && cachedProducers.TryGetValue(d.Id, out var cached) ? cached :
                await client.GetProducerAsync(d.Id, cancellationToken) ?? new Producer { Id = d.Id, Name = d.Name, Original = d.Original });
        CoverAsset? cover = null;
        string status = "none";
        string? error = null;
        if (novel.Image is not null)
        {
            try
            {
                if (existing?.Cover is { } previous && previous.SourceUrl == novel.Image.Url && File.Exists(LibraryRules.CoverPath(store.DataDirectory, previous))) cover = previous;
                else cover = await covers.DownloadAsync(novel.Image, cancellationToken);
                status = "ready";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                status = "failed";
                error = "封面下载失败，可通过刷新重试。";
                cover = existing?.Cover;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var entry = new LibraryEntry(novel, producers, cover, status, error, existing?.CreatedAt ?? now, now);
        store.Save(entry);
        return store.Get(novel.Id)!;
    }
    public void Dispose() => _gate.Dispose();
}
