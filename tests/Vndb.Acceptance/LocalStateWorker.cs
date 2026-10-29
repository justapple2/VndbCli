using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Acceptance;

/// <summary>One-shot offline fixture used only by explicitly scoped cross-process tests.</summary>
public static class LocalStateWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || Environment.GetEnvironmentVariable("VN_STATE_WORKER") != "1") return 2;
        var privateRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("VN_PRIVATE_ROOT") ?? throw new ArgumentException("Isolated root required."));
        var scratch = Path.Combine(privateRoot, "test-scratch"); var home = Path.GetFullPath(args[0]);
        var relative = Path.GetRelativePath(scratch, home);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p == "..") || !relative.StartsWith("test-", StringComparison.Ordinal) ||
            !Directory.Exists(home) || (File.GetAttributes(home) & FileAttributes.ReparsePoint) != 0 || args[2] is not ("a" or "b")) return 2;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)); var ct = timeout.Token;
        await File.WriteAllTextAsync(Path.Combine(home, "worker-ready-" + args[2] + ".json"), "{}", ct);
        while (!File.Exists(Path.Combine(home, "worker-go.json"))) await Task.Delay(10, ct);
        var rejected = false;
        if (args[1] == "enqueue")
        {
            try
            {
                await new PersonalEditOutbox(home).EnqueueAsync("test-book", "http://library.test/", "test-play",
                    new(new("Synthetic worker", null, "playing", null, null, null, null, null, null, null, null), ExpectedRevision: 1), ct);
            }
            catch (InvalidOperationException) { rejected = true; }
        }
        else if (args[1] == "migrate")
            await LocalDataRoot.MigrateAsync(Path.Combine(home, "legacy"), Path.Combine(home, "current"), ct);
        else if (args[1] == "profile")
        {
            var executable = Path.GetFullPath(args[3]);
            var executableRelative = Path.GetRelativePath(home, executable);
            if (Path.IsPathRooted(executableRelative) || executableRelative.Split(Path.DirectorySeparatorChar).Any(p => p == "..")) return 2;
            await new LocalLauncher(home).SaveAsync("Synthetic profile " + args[2], executable, [], home, ct: ct);
        }
        else if (args[1] == "edit")
        {
            var outbox = new ActivityOutbox(home);
            var target = (await outbox.TargetsAsync(ct)).Single(t => t.Id == args[3]);
            await outbox.EditAsync(target, "playing", "worker-" + args[2], ct);
        }
        else if (args[1] == "query")
        {
            var scope = LocalQueryScope.Resolve(home, new("http://library.test/"));
            var id = args[2] == "a" ? "v17" : "v18";
            var metadata = new MetadataSnapshot(id, "Synthetic " + id, "vn", new() { Id = id, Title = "Synthetic " + id }, [], null, new("none"), DateTimeOffset.UnixEpoch, "test");
            await new JsonSnapshotStore(scope.DirectoryPath, scope.Origin).SaveAsync(ClientQuery.FromText(id), new([metadata], "client_vndb", metadata.FetchedAt, 1, 10, 1, false, PendingImport: true), ct);
        }
        else if (args[1] == "query-migrate")
        {
            var scope = LocalQueryScope.Resolve(home, new("http://library.test/")); var migration = new QueryDataMigration(scope);
            var preview = await migration.PreviewAsync(ct: ct); await migration.ApplyAsync(preview, LocalArchiveFormat.Hash(preview), ct);
        }
        else return 2;
        await File.WriteAllTextAsync(Path.Combine(home, "worker-result-" + args[2] + ".json"), JsonSerializer.Serialize(new { rejected }), ct);
        return 0;
    }
}
