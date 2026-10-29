using Vndb.Core;

namespace Vndb.Library;

public sealed record CoverAsset(string Hash, string RelativePath, string ContentType, long SizeBytes,
    string SourceUrl, DateTimeOffset DownloadedAt);
public sealed record LibraryEntry(VisualNovel Novel, IReadOnlyList<Producer> Producers, CoverAsset? Cover,
    string CoverStatus, string? CoverError, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string Provenance = "web_vndb", string Completeness = "full_text");
public sealed record LibraryPage(IReadOnlyList<LibraryEntry> Results, int Page, int PageSize, int Total, bool More);
public sealed record ProducerCount(string Id, string Name, string? Original, int NovelCount);
public sealed record LibraryStatistics(int Novels, int Producers, int Covers, int CoverFailures);
public sealed record StoredProducer(Producer Producer, DateTimeOffset FetchedAt);

public interface ILibraryStore
{
    string DataDirectory { get; }
    LibraryEntry? Get(string id);
    void Save(LibraryEntry entry);
    LibraryPage List(string? query = null, string? producer = null, int page = 1, int limit = 24);
    IReadOnlyList<ProducerCount> Producers();
    StoredProducer? GetProducer(string id);
    IReadOnlyList<StoredProducer> FindProducers(string query);
    void SaveProducer(StoredProducer producer);
    LibraryStatistics Statistics();
    ArchiveLocation RegisterArchive(ArchiveRegistration registration);
    IReadOnlyList<ArchiveLocation> Archives(string? novelId = null, string? runId = null);
    PlayRecord SavePlay(PlayDetails details, string? id = null, long? expectedRevision = null);
    PlayRecord? GetPlay(string id);
    PlayPage Plays(string? query = null, string? status = null, bool? played = null, int? year = null,
        bool unmatched = false, string? novelId = null, int page = 1, int limit = 24);
    PlayStatistics PlayStatistics();
    PlayImportResult ImportPlays(PlayImportBatch batch);
}

public static class LibraryRules
{
    public static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100) throw new ArgumentException("页码须大于 0，每页数量须为 1–100。");
        if ((long)(page - 1) * limit > int.MaxValue) throw new ArgumentException("页码过大。");
    }
    public static string SearchText(VisualNovel novel) => string.Join('\n',
        new[] { novel.Title, novel.AltTitle ?? "", novel.Id }
            .Concat(novel.Aliases).Concat(novel.Titles.SelectMany(t => new[] { t.Title, t.Latin ?? "" })))
        .ToLowerInvariant();
    public static string CoverPath(string dataDirectory, CoverAsset asset)
    {
        if (asset.Hash.Length != 64 || asset.Hash.Any(c => !char.IsAsciiHexDigit(c)) ||
            asset.RelativePath != "covers/" + asset.Hash + Path.GetExtension(asset.RelativePath) ||
            Path.GetExtension(asset.RelativePath) is not (".jpg" or ".png" or ".webp"))
            throw new InvalidDataException("封面路径不合法。");
        return Path.Combine(dataDirectory, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
