using System.CommandLine;
using Vndb.Library;

namespace Vndb.Admin;

public sealed partial class AdminApplication
{
    private void AddDumpProbe(Command library)
    {
        var command = new Command("dump-probe", "O3：在全新隔离目录验证官方包及全规模公共资料检索，不连接Web。");
        var archive = new Argument<string>("archive"); var stage = new Argument<string>("stage"); var probe = new Argument<string>("probe");
        command.Add(archive); command.Add(stage); command.Add(probe);
        Bind(command, (_, parsed, ct) =>
        {
            if (!parsed.GetValue(_offlineMaintenance) || parsed.GetValue(_server) is not null)
                throw new ArgumentException("dump-probe仅允许显式--offline-maintenance和新隔离目录。");
            try { return Task.FromResult<object?>(DumpProbe.Run(parsed.GetValue(archive)!, parsed.GetValue(stage)!, parsed.GetValue(probe)!, ct)); }
            catch (InvalidDataException exception) { throw new ArgumentException("O3验证未通过：" + exception.Message); }
        });
        library.Add(command);
        var queue = new Command("dataset-probe", "O4：新隔离数据库中执行可恢复公共导入，验证真实队列，不连接NAS。");
        var source = new Argument<string>("archive"); var target = new Argument<string>("new-data"); queue.Add(source); queue.Add(target);
        Bind(queue, async (_, parsed, ct) =>
        {
            if (!parsed.GetValue(_offlineMaintenance) || parsed.GetValue(_server) is not null) throw new ArgumentException("需要离线维护模式。");
            var directory = Path.GetFullPath(parsed.GetValue(target)!); DumpPackage.Ordinary(directory);
            if (Directory.Exists(directory)) throw new IOException("探针需要新隔离数据目录。");
            var store = new SqliteLibraryStore(directory); using var http = new HttpClient();
            using var imports = new DatasetImports(store, http);
            await using var input = File.OpenRead(parsed.GetValue(source)!);
            var job = await imports.UploadAsync(input, "isolated-probe", ct);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var n = 0; n < 100000 && store.DatasetJobs()[0].State is not ("completed" or "failed"); n++) await imports.StepAsync(ct);
            var completed = store.DatasetJobs()[0];
            if (completed.State != "completed" || store.CollectionStatistics().PersonalItems != 0) throw new InvalidDataException("O4隔离队列未完成。");
            return new { job = completed, seconds = watch.Elapsed.TotalSeconds, statistics = store.Statistics(),
                bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length),
                peak_working_set = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 };
        });
        library.Add(queue);
    }
}
