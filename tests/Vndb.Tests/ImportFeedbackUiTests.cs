using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ImportFeedbackUiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("waiting_web", "等待Web")][InlineData("unconfirmed", "未确认")][InlineData("preview", "已接收")]
    [InlineData("needs_review", "候选")][InlineData("conflict", "冲突")][InlineData("dependency_pending", "依赖")]
    public void DisplayLabelsRetainOriginalCodesAndReceiptKeys(string state, string expected)
    {
        var original = new ImportFeedbackRow("metadata", "original-stable-key", state, "原消息"); var display = new ImportFeedbackDisplay(original);
        Assert.Contains(expected, display.State); Assert.Same(original, display.Original); Assert.Equal(state, display.Original.Status);
        Assert.Equal("original-stable-key", display.Key); Assert.Contains(state, display.Evidence);
    }
    [Fact]
    public async Task FailedReadKeepsLastSuccessfulRowsWithoutClaimingNewWritesWereConfirmed()
    {
        var fail = false; var row = new ImportFeedbackRow("metadata", "key", "preview", "原消息");
        var vm = new ImportFeedbackViewModel(_ => fail ? throw new IOException("Synthetic read failure") : Task.FromResult(new LocalImportStatus(null, [row], false)), action => action(Ct));
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Contains("快照", vm.Summary); Assert.Contains("已接收", Assert.Single(vm.DisplayResults).State);
        fail = true; await Assert.ThrowsAsync<IOException>(() => vm.RefreshCommand.ExecuteAsync(null));
        Assert.Same(row, Assert.Single(vm.Results)); Assert.Contains("上次", vm.Summary); Assert.Contains("不能", vm.Summary);
    }
    [Fact]
    public async Task PendingWithoutReceiptSeparatesUnsentFromSealedUncertainOperationWithoutRewriting()
    {
        using var root = new TestDirectory(); const string origin = "http://library.test/";
        var scope = LocalQueryScope.Resolve(root.Path, new(origin), environment: _ => null); var store = new JsonSnapshotStore(scope.DirectoryPath, origin);
        await store.SaveAsync(ClientQuery.FromText("v17"), new([M5Tests.Item().Metadata!], "client_vndb", DateTimeOffset.UnixEpoch, 1, 10, 1, false, PendingImport: true), Ct);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "pending"))); var bytes = await File.ReadAllBytesAsync(path, Ct); var id = Path.GetFileNameWithoutExtension(path);
        Assert.Equal("waiting_web", Assert.Single((await ImportStatus.ReadAsync(null, scope, Path.Combine(root.Path, "none"), Ct)).Results).Status);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(scope.DirectoryPath, "metadata-operations", id + ".json"), new { server = origin, service_instance_id = "synthetic", sha256 = "synthetic" }, false, Ct);
        Assert.Equal("unconfirmed", Assert.Single((await ImportStatus.ReadAsync(null, scope, Path.Combine(root.Path, "none"), Ct)).Results).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Ct)); Assert.True((await store.ReadAsync(ClientQuery.FromText("v17"), Ct))!.PendingImport);
    }
}
