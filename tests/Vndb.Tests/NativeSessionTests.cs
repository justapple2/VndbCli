using System.Text.Json;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

[CollectionDefinition("Synthetic native session", DisableParallelization = true)]
public sealed class NativeSessionCollection;

// Short-lived process-discovery fixtures require a quiet scheduler; no real game is executed.
[Collection("Synthetic native session")]
public sealed class NativeSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Id() => Guid.NewGuid().ToString("D");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeDirectAndWrapperTrackExactProcessAndClosingNeverTerminatesIt(bool wrapper)
    {
        using var dir = new TestDirectory();
        var fixture = Path.GetFullPath(Path.Combine(TestDirectory.ScratchRoot, "../build/Vndb.FakeGame/bin/Release/net10.0/Vndb.FakeGame.exe"));
        var childDir = Path.Combine(dir.Path, "child"); Directory.CreateDirectory(childDir);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(fixture)!)) File.Copy(file, Path.Combine(childDir, Path.GetFileName(file)));
        var child = Path.Combine(childDir, "Vndb.FakeGame.exe"); var receipt = Path.Combine(dir.Path, "child.json");
        var launcher = new LocalLauncher(dir.Path); var profile = await launcher.SaveAsync("Synthetic game", fixture, wrapper ? ["--spawn", child, receipt, "15000", "1500"] : ["--receipt", receipt, "--hold", "15000"], dir.Path, ct: Ct);
        if (wrapper) await launcher.ConfigureTrackingAsync(profile.Id, child, ct: Ct);
        var outbox = new ActivityOutbox(dir.Path); var target = await outbox.CreateTargetAsync(new(Id(), "Synthetic", "windows", DateTimeOffset.UtcNow), "Synthetic", null, ct: Ct);
        using var manager = new LocalSessionManager(launcher, outbox, dir.Path);
        try
        {
            await manager.StartAsync(profile.Id, target, Ct);
            for (var i = 0; i < 60 && manager.Current?.Process is null; i++) { await Task.Delay(100, Ct); await manager.TickAsync(Ct); }
            Assert.True(manager.Current?.Process is not null, $"Synthetic process not discovered: {manager.Current?.Stage}/{manager.Current?.Sample.Reason}");
            Assert.Equal(wrapper ? child : fixture, manager.Current!.Process!.Executable); Assert.Equal("tracking", manager.Current.Stage);
            using var process = System.Diagnostics.Process.GetProcessById(manager.Current.Process.Pid);
            await manager.StopAsync(closing: true, Ct); Assert.False(process.HasExited); Assert.Equal("interrupted", manager.Current.Sample.State);
            await process.WaitForExitAsync(Ct); Assert.True(File.Exists(receipt));
        }
        finally
        {
            // Even a failed assertion must let this exact synthetic child exit before fixture disposal.
            // Otherwise the locked DLL hides the original assertion behind a cleanup exception.
            if (File.Exists(receipt))
            {
                using var proof = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt, Ct));
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(proof.RootElement.GetProperty("pid").GetInt32());
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, wrapper ? child : fixture, StringComparison.OrdinalIgnoreCase))
                        await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
                }
                catch (ArgumentException) { /* This fixture already exited. */ }
            }
        }
    }
}
