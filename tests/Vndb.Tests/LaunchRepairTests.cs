using System.Text;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class LaunchRepairTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task RestoreRejectsAReceiptWhoseDisplayedPathsDisagreeWithItsOriginalBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var root = Directory.CreateDirectory(Path.Combine(dir.Path, "game")).FullName;
        var old = Path.Combine(dir.Path, "missing");
        var before = Encoding.ASCII.GetBytes($"[PATH]\nCurrent=\"{old}\"\n");
        var after = LaunchRepair.RewriteInstall(before, root)!.Value.Bytes;
        File.WriteAllBytes(Path.Combine(root, "install.inf"), after);
        var forged = new InstallPathRepair(Guid.NewGuid().ToString("D"), "synthetic", root, NativeFiles.Identity(root), before, after,
            old + "-different", root, false, null);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(dir.Path, "engine-path-repairs", forged.Id + ".json"), forged, false, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LaunchRepair(dir.Path).RestoreInstallAsync(forged, Ct));
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(root, "install.inf")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RepairBackupFollowsTwoCompletedMovesAndReturnWithoutRewritingHistory(bool bundle, bool repairAfterFirstMove)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory();
        var data = Path.Combine(dir.Path, "client");
        var originalRoot = Directory.CreateDirectory(Path.Combine(dir.Path, "original")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "target")).FullName;
        var component = bundle ? Directory.CreateDirectory(Path.Combine(originalRoot, "bonus")).FullName : originalRoot;
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct);
        var now = DateTimeOffset.UtcNow;
        var installation = new LocalInstallation("synthetic-installation", new(device.Id, originalRoot, "Synthetic", "Studio"), "present", now, 1, now, now);
        var components = new[] { new LocalInstallationComponent("main", installation.Id, new("one", "."), 1, now, now) }
            .Concat(bundle ? [new LocalInstallationComponent("bonus", installation.Id, new("two", "bonus"), 1, now, now)] : []).ToArray();
        var store = new LocalLibraryStore(data);
        await store.SaveAsync(new([new(installation, components)], []), Ct);
        var before = Encoding.ASCII.GetBytes($"[PATH]\r\nCurrent = \"{Path.Combine(dir.Path, "missing")}\"\r\n");
        var after = LaunchRepair.RewriteInstall(before, component)!.Value.Bytes;
        await File.WriteAllBytesAsync(Path.Combine(component, "install.inf"), after, Ct);
        var repair = new InstallPathRepair(Guid.NewGuid().ToString("D"), installation.Id, component, NativeFiles.Identity(component), before, after, Path.Combine(dir.Path, "missing"), component, false, null);
        var receipt = Path.Combine(data, "engine-path-repairs", repair.Id + ".json");
        if (!repairAfterFirstMove) await JsonSnapshotStore.AtomicWriteAsync(receipt, repair, false, Ct);
        var workspace = new LocalOrganizationWorkspace(data, device);
        async Task<LocalArchivePlan> Move(string title)
        {
            var plan = await workspace.PlanAsync(installation.Id, null, dir.Path, target, "Studio", title, Ct);
            await workspace.Runner().ExecuteAsync(plan, LocalArchiveFormat.Hash(plan), workspace.RunDirectory(plan.Id), Ct);
            return plan;
        }
        var first = await Move("First");
        if (repairAfterFirstMove)
        {
            var movedComponent = bundle ? Path.Combine(first.DestinationPath, "bonus") : first.DestinationPath;
            before = after; after = LaunchRepair.RewriteInstall(before, movedComponent)!.Value.Bytes;
            repair = repair with { Root = movedComponent, Original = before, Updated = after, OldPath = component, NewPath = movedComponent };
            await File.WriteAllBytesAsync(Path.Combine(movedComponent, "install.inf"), after, Ct);
            await JsonSnapshotStore.AtomicWriteAsync(receipt, repair, false, Ct);
        }
        var immutable = await File.ReadAllBytesAsync(receipt, Ct);
        var second = await Move("Second");
        var currentRoot = bundle ? Path.Combine(second.DestinationPath, "bonus") : second.DestinationPath;
        workspace = new(data, device); // reopen the JSON state as after an application restart
        var restore = Assert.IsType<InstallRepairRestore>(await workspace.PreviewInstallRestoreAsync(installation.Id, currentRoot, Ct));
        Assert.Equal(repair.Id, restore.Repair.Id);
        if (bundle) Assert.Null(await workspace.PreviewInstallRestoreAsync(installation.Id, second.DestinationPath, Ct));
        // The game changing its config after preview must invalidate approval without overwriting it.
        await File.WriteAllTextAsync(Path.Combine(currentRoot, "install.inf"), "external modification", Ct);
        await Assert.ThrowsAsync<IOException>(() => workspace.RestoreInstallAsync(restore, Ct));
        Assert.Equal("external modification", await File.ReadAllTextAsync(Path.Combine(currentRoot, "install.inf"), Ct));
        await File.WriteAllBytesAsync(Path.Combine(currentRoot, "install.inf"), after, Ct);
        await workspace.RestoreInstallAsync(restore, Ct);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(currentRoot, "install.inf"), Ct));
        await workspace.RecheckAsync(dir.Path, target, Ct); // a fresh local observation also advances the installation revision
        // Returning a whole game preserves repaired/restored bytes and the same installation.
        foreach (var plan in new[] { second, first })
        {
            var runner = workspace.Runner(); var run = workspace.RunDirectory(plan.Id);
            var reverse = await runner.PreviewRestoreAsync(run, Ct);
            await runner.RestoreAsync(reverse, LocalArchiveFormat.Hash(reverse), run, Ct);
            if (plan == second)
            {
                var returnedComponent = bundle ? Path.Combine(first.DestinationPath, "bonus") : first.DestinationPath;
                await File.WriteAllBytesAsync(Path.Combine(returnedComponent, "install.inf"), after, Ct);
                var returned = Assert.IsType<InstallRepairRestore>(await workspace.PreviewInstallRestoreAsync(installation.Id, returnedComponent, Ct));
                await workspace.RestoreInstallAsync(returned, Ct);
            }
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(component, "install.inf"), Ct));
        Assert.Equal(immutable, await File.ReadAllBytesAsync(receipt, Ct));
    }

    [Fact]
    public async Task PhysicalIdentityAloneDoesNotAuthorizeAnUnrecordedMove()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); var data = Path.Combine(dir.Path, "client");
        var old = Directory.CreateDirectory(Path.Combine(dir.Path, "old")).FullName;
        var target = Path.Combine(dir.Path, "new"); var now = DateTimeOffset.UtcNow;
        var device = await LocalDeviceIdentity.LoadOrCreateAsync(data, Ct);
        var before = Encoding.ASCII.GetBytes($"[PATH]\nCurrent=\"{Path.Combine(dir.Path, "missing")}\"\n");
        var updated = LaunchRepair.RewriteInstall(before, old)!.Value.Bytes;
        File.WriteAllBytes(Path.Combine(old, "install.inf"), updated);
        var plan = new InstallPathRepair(Guid.NewGuid().ToString("D"), "i", old, NativeFiles.Identity(old), before, updated, Path.Combine(dir.Path, "missing"), old, false, null);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(data, "engine-path-repairs", plan.Id + ".json"), plan, false, Ct);
        Directory.Move(old, target);
        await new LocalLibraryStore(data).SaveAsync(new([new(new("i", new(device.Id, target, "Synthetic"), "present", now, 2, now, now), [])], []), Ct);
        var workspace = new LocalOrganizationWorkspace(data, device);
        await Assert.ThrowsAsync<IOException>(() => workspace.PreviewInstallRestoreAsync("i", target, Ct));
        Assert.Equal(updated, File.ReadAllBytes(Path.Combine(target, "install.inf")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallInfRewritesOnlyKnownCurrentAndMirroredMediaPath(bool mirror)
    {
        if (!OperatingSystem.IsWindows()) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(932);
        const string old = @"E:\旧作品\";
        const string target = @"E:\作品\現在";
        var media = mirror ? old : @"I:\disc [edition]\Data\";
        var source = $"[PATH]\r\nSetupType = 0\r\nCurrent = \"{old}\"\r\nCDDrive = \"{media}\"\r\n[Other]\r\nCurrent = \"{old}\"\r\n";
        var repair = LaunchRepair.RewriteInstall(encoding.GetBytes(source), target)!.Value;
        var expected = $"[PATH]\r\nSetupType = 0\r\nCurrent = \"{target}\\\"\r\nCDDrive = \"{(mirror ? target + "\\" : media)}\"\r\n[Other]\r\nCurrent = \"{old}\"\r\n";
        Assert.Equal(expected, encoding.GetString(repair.Bytes));
        Assert.Equal(mirror, repair.Mirror);
        Assert.Null(LaunchRepair.RewriteInstall(repair.Bytes, target));
        Assert.Throws<InvalidDataException>(() => LaunchRepair.RewriteInstall(encoding.GetBytes(source.Replace("SetupType = 0", "Current = duplicate")), target));
        Assert.Throws<EncoderFallbackException>(() => LaunchRepair.RewriteInstall(encoding.GetBytes(source), target + "🧪"));
    }

    [Fact]
    public async Task SavedPathRepairRebasesOnlyStandaloneArgumentsAndChecksExecutableHash()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory();
        var source = Directory.CreateDirectory(Path.Combine(dir.Path, "old")).FullName;
        var target = Path.Combine(dir.Path, "new");
        var executable = Path.Combine(source, "start.exe");
        var data = Path.Combine(source, "game.dat");
        await File.WriteAllTextAsync(executable, "synthetic executable bytes", Ct);
        await File.WriteAllTextAsync(data, "synthetic data", Ct);
        var launcher = new LocalLauncher(dir.Path);
        var profile = await launcher.SaveAsync("Synthetic", executable, [data, "game.dat", "--other=" + data], source, "install", ct: Ct);
        Directory.Move(source, target);
        var plan = Assert.IsType<LaunchProfileRepair>(LocalLauncher.PreviewPathRepair(profile, source, target));
        var updated = await launcher.ApplyPathRepairAsync(plan, Ct);
        Assert.Equal(Path.Combine(target, "start.exe"), updated.Executable);
        Assert.Equal(new[] { Path.Combine(target, "game.dat"), "game.dat", "--other=" + data }, updated.Arguments);
        Assert.Equal(updated.Arguments.ToArray(), Assert.Single(await launcher.ReadAsync(Ct)).Arguments.ToArray());
        await Assert.ThrowsAsync<IOException>(() => launcher.ApplyPathRepairAsync(plan, Ct));
        Assert.Equal("\"" + target + "\\\"", RegistryRepair.MapPath("\"" + source + "\\\"", source, target));
        Assert.Null(RegistryRepair.MapPath("\"" + source + "\" -argument", source, target));
    }
}
