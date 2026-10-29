using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

public static class DumpNormalization
{
    public static DumpManifest Normalize(string directory, CancellationToken ct)
    {
        var manifest = DumpPackage.Verify(directory, ct);
        if (manifest.Normalized) return manifest;
        var proofs = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "stage.db"), Pooling = false }.ToString()))
        {
            db.Open();
            using (var create = db.CreateCommand()) { create.CommandText = "CREATE TABLE normalized_novels(id TEXT PRIMARY KEY,entry_json TEXT NOT NULL); CREATE TABLE normalized_producers(id TEXT PRIMARY KEY,entry_json TEXT NOT NULL)"; create.ExecuteNonQuery(); }
            var projection = new DumpProjection(db, manifest);
            foreach (var kind in new[] { "producer", "vn" })
            {
                var cursor = "";
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = projection.EntityIds(kind, cursor, 100); if (ids.Count == 0) break;
                    var rows = ids.Select(id => (Id: id, Json: kind == "vn" ? JsonSerializer.Serialize(projection.Novel(id, ct), VndbJson.Options) : JsonSerializer.Serialize(projection.Producer(id), VndbJson.Options))).ToArray();
                    using var tx = db.BeginTransaction();
                    foreach (var row in rows)
                    {
                        using var insert = db.CreateCommand(); insert.Transaction = tx;
                        insert.CommandText = $"INSERT INTO normalized_{(kind == "vn" ? "novels" : "producers")} VALUES($id,$json)";
                        insert.Parameters.AddWithValue("$id", row.Id); insert.Parameters.AddWithValue("$json", row.Json); insert.ExecuteNonQuery();
                        proofs.Add(kind + "/" + row.Id, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.Json))));
                    }
                    tx.Commit(); cursor = ids[^1];
                }
            }
            using var validate = db.CreateCommand();
            validate.CommandText = "SELECT (SELECT count(*) FROM normalized_producers),(SELECT count(*) FROM normalized_novels)";
            using var count = validate.ExecuteReader(); count.Read();
            if (count.GetInt64(0) != manifest.Rows["producers"] || count.GetInt64(1) != manifest.Rows["vn"])
                throw new InvalidDataException("规范投影覆盖不完整。");
        }
        var files = manifest.Files.ToDictionary(p => p.Key, p => p.Key == "stage.db" ? DumpPackage.Hash(Path.Combine(directory, "stage.db")) : p.Value);
        File.WriteAllBytes(Path.Combine(directory, "normalized-proofs.json"), JsonSerializer.SerializeToUtf8Bytes(proofs));
        files.Add("normalized-proofs.json", DumpPackage.Hash(Path.Combine(directory, "normalized-proofs.json")));
        manifest = manifest with { Files = files, Normalized = true };
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, VndbJson.Options));
        return manifest;
    }
}
