using System.Diagnostics;

namespace Vndb.Library;

public static class DumpProbe
{
    public static object Run(string archive, string stageDirectory, string probeDirectory, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var manifest = DumpPackage.Prepare(archive, stageDirectory, ct);
        var prepareSeconds = watch.Elapsed.TotalSeconds;
        using var stage = DumpPackage.OpenStage(stageDirectory);
        var projection = new DumpProjection(stage, manifest);
        if (Directory.Exists(probeDirectory)) throw new IOException("性能夹具需要新目录。");
        var store = new SqliteLibraryStore(probeDirectory); string cursor = ""; long count = 0;
        watch.Restart();
        while (true)
        {
            var ids = projection.Ids(cursor, 100); if (ids.Count == 0) break;
            var entries = ids.Select(id => projection.Novel(id, ct)).ToArray();
            store.SaveMetadataBatch(entries); count += ids.Count; cursor = ids[^1];
        }
        var importSeconds = watch.Elapsed.TotalSeconds;
        var reads = new List<double>();
        foreach (var query in new[] { "加奈", "恋", "かな", "Kana", "v17", "不存在的合成标题" })
            for (var n = 0; n < 10; n++) { watch.Restart(); _ = store.List(query); reads.Add(watch.Elapsed.TotalMilliseconds); }
        watch.Restart(); _ = store.List(producer: "p1"); reads.Add(watch.Elapsed.TotalMilliseconds);
        reads.Sort();
        var p95 = reads[(int)((reads.Count - 1) * 0.95)];
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        if (count != manifest.Rows["vn"] || store.CollectionStatistics().PersonalItems != 0 || store.List("加奈").Total == 0 || store.List("Kana").Total == 0 || p95 > 1000 || peak > 768L * 1024 * 1024)
            throw new InvalidDataException("O3规模/检索/内存闸门未通过，保留隔离夹具；不启用导入。");
        return new { status = "passed", manifest, prepare_seconds = prepareSeconds, import_seconds = importSeconds,
            novels = count, search_p95_ms = p95, peak_working_set_bytes = peak,
            stage_bytes = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length),
            database_bytes = Directory.EnumerateFiles(probeDirectory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length),
            cpu = "single synchronous worker; no parallel projections", memory = "use DOTNET_GCHeapHardLimit=0x20000000 for the isolated probe",
            coverage = "selected public metadata only; no images, user lists, private collection or upstream requests" };
    }
}
