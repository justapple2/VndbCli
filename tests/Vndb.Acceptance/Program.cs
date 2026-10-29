using Vndb.Local;
using Vndb.Acceptance;
using Vndb.Client;

if (args.Length == 5 && args[0] == "--state-worker") return await LocalStateWorker.RunAsync(args[1..]);

var recovery = args.Length == 4 && args[3] == "--recover-personal";
if (args.Length != 3 && !recovery || Environment.GetEnvironmentVariable("VN_ACCEPTANCE_SYNTHETIC") != "1")
{
    Console.Error.WriteLine("Explicit synthetic acceptance only: <new-private-run-directory> <server-root> <existing-vndb-id>. Set VN_ACCEPTANCE_SYNTHETIC=1 and VN_LIBRARY_KEY in process memory. No real games.");
    return 2;
}
var privateRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("VN_PRIVATE_ROOT") ?? throw new ArgumentException("VN_PRIVATE_ROOT required."));
var run = Path.GetFullPath(args[0]);
if (!Path.IsPathFullyQualified(args[0]) || !Path.GetRelativePath(privateRoot, run).StartsWith("m7" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !recovery && Directory.Exists(run)) throw new ArgumentException("Choose a new directory under private m7/ (recovery requires an existing fixture run).");
var server = ClientSettings.ServerUri(args[1]);
var fake = Path.Combine(privateRoot, "build/Vndb.FakeGame/bin/Release/net10.0/Vndb.FakeGame.exe");
var key = Environment.GetEnvironmentVariable("VN_LIBRARY_KEY") ?? throw new ArgumentException("Management key required in memory.");
using var gate = new AvailabilityHandler(new HttpClientHandler { AllowAutoRedirect = false });
using var http = new HttpClient(gate, false) { BaseAddress = server };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    if (recovery) { await AcceptanceWorkflow.RecoverPersonalAsync(run, server, key, http, timeout.Token); Console.WriteLine("PASS: original synthetic outbox recovered and soft removed without double counting; originals retained."); return 0; }
    var report = await AcceptanceWorkflow.RunAsync(run, server, key, args[2], fake, http, gate, timeout.Token);
    Console.WriteLine($"PASS: {report.Checks.Count} end-to-end checks; synthetic-only records soft removed; evidence retained.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Acceptance failed: {error.GetType().Name}: {error.Message}. Preserve the run directory and resume its original organization intent before retrying any move.");
    return 1;
}
