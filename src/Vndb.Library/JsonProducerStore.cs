using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class JsonLibraryStore
{
    public StoredProducer? GetProducer(string id) => FindProducers("").FirstOrDefault(p => p.Producer.Id == VndbId.Normalize(id, 'p'));
    public IReadOnlyList<StoredProducer> FindProducers(string query)
    {
        var folder = Path.Combine(DataDirectory, "producers");
        var saved = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "p*.json").Select(path =>
            JsonSerializer.Deserialize<StoredProducer>(File.ReadAllText(path), VndbJson.Options) ?? throw new InvalidDataException("会社资料无效。")) : [];
        return saved.Concat(All().SelectMany(e => e.Producers.Select(p => new StoredProducer(p, e.UpdatedAt))))
            .GroupBy(p => p.Producer.Id).Select(g => g.MaxBy(p => p.FetchedAt)!)
            .Where(p => SqliteLibraryStore.ProducerMatches(p.Producer, query)).OrderBy(p => p.Producer.Id, StringComparer.Ordinal).ToArray();
    }
    public void SaveProducer(StoredProducer entry)
    {
        var folder = Path.Combine(DataDirectory, "producers");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, VndbJson.Options));
            File.Move(temporary, Path.Combine(folder, VndbId.Normalize(entry.Producer.Id, 'p') + ".json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
