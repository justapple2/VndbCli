using System.Text.Json;
using System.Diagnostics;

// Synthetic direct-launch fixture, never a real game or launcher/shell.
if (args.Length >= 3 && args[0] == "-runas")
{
    using var child = Process.Start(new ProcessStartInfo(args[2]) { UseShellExecute = false, Arguments = string.Join(" ", args.Skip(3)), WorkingDirectory = Path.GetDirectoryName(args[2])! });
    if (child is null) return 3;
    await child.WaitForExitAsync(); return child.ExitCode;
}
if (args.Length >= 4 && args[0] == "--spawn")
{
    var start = new ProcessStartInfo(args[1]) { UseShellExecute = false };
    foreach (var argument in new[] { "--receipt", args[2], "--hold", args[3] }) start.ArgumentList.Add(argument);
    var parentHold = args.Length == 5 && int.TryParse(args[4], out var requested) ? Math.Clamp(requested, 300, 3000) : 300;
    using var child = Process.Start(start); await Task.Delay(parentHold); return child is null ? 3 : 0;
}
if (args.Length < 2 || args[0] != "--receipt") return 2;
var temporary = args[1] + ".writing-" + Guid.NewGuid().ToString("N");
await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { cwd = Environment.CurrentDirectory, args, pid = Environment.ProcessId }));
File.Move(temporary, args[1]);
if (args.Length == 4 && args[2] == "--hold" && int.TryParse(args[3], out var milliseconds)) await Task.Delay(Math.Clamp(milliseconds, 1, 60000));
return 0;
