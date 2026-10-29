using Vndb.Client;
using Vndb.Contracts;
using Vndb.Desktop.ViewModels;
using Vndb.Desktop.Services;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class DetailEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static PersonalPlayView Play(string id = "play-1") => new(id, new("Synthetic", "v17", "played", null, null, null, 8.5m, "原评价", "备注一", "备注二", "ja"), 3, Now, Now,
        Time: new(id, null, 120, -60, null, 1, 4), ItemId: "item-17");
    private sealed class Service : IQuickPlayService
    {
        public IReadOnlyList<PersonalPlayView> Plays = [Play(), Play("play-2")];
        public PersonalEditDraft? Pending;
        public Exception? Failure;
        public int Writes, Withdrawals;
        public List<PersonalEditDraft> Submitted = [];
        public Task<IReadOnlyList<PersonalPlayView>> LoadAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult(Plays);
        public Task<PersonalEditDraft?> PendingAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult(Pending);
        public Task<PersonalEditDraft> QueueAsync(QuickPlayBook book, PersonalPlayView? play, PersonalPlayData details, CancellationToken ct)
        { Writes++; Pending = new(Guid.NewGuid().ToString("D"), book.Id, "http://ui.test/", play?.Id, new(details, book.Item?.Item.Id, play?.Revision), Now); return Task.FromResult(Pending); }
        public Task<PersonalPlayView> SyncAsync(PersonalEditDraft draft, CancellationToken ct)
        { Submitted.Add(draft);return Failure is not null ? Task.FromException<PersonalPlayView>(Failure) : Task.FromResult(Play(draft.PlayId ?? "new-play") with { Details = draft.Request.Details, Revision = (draft.Request.ExpectedRevision ?? 0) + 1 }); }
        public Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct) { Withdrawals++;Pending=null;return Task.CompletedTask; }
    }
    private static QuickPlayEditorViewModel Editor(Service service, Action? close = null, Func<Task>? saved = null) => new(new(M5Tests.Item(), null), service, close ?? (() => { }), saved ?? (() => Task.CompletedTask));
    private sealed class EmptyLibrary : IDesktopLibrary
    {
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => throw new NotSupportedException();
        public Task<string?> FolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> SaveJsonAsync(string name) => throw new NotSupportedException();
    }
    [Fact]
    public async Task MetadataOnlyActionsRequireSpecificShelfRegistrationWithoutProbingOrLaunching()
    {
        using var root = new TestDirectory();var vm=new MainViewModel(new EmptyLibrary(),new(root.Path),new Dialogs());
        var query=new BookViewModel(null,M5Tests.Item().Metadata) { IsLocal=true };
        vm.ConfigureBookCommand.Execute(query);Assert.Equal(7,vm.Section);Assert.Contains("具体书架条目",vm.Status);
        vm.OpenBookFolderCommand.Execute(query);Assert.Contains("具体条目",vm.Status);
        await vm.DirectBookCommand.ExecuteAsync(query);Assert.Contains("具体条目",vm.Status);Assert.Empty(vm.Profiles);
    }

    [Fact]
    public async Task DirtyCloseRequiresExplicitDiscardAndNeverWrites()
    {
        var service=new Service();var closed=0;var vm=Editor(service,()=>closed++);await vm.InitializeAsync(Ct);
        vm.SelectedPlay=vm.Plays[1];vm.Review="未保存的感想";Assert.True(vm.IsDirty);Assert.False(vm.CanChooseRecord);
        vm.CloseCommand.Execute(null);Assert.True(vm.ConfirmClose);Assert.Equal(0,closed);Assert.Equal("未保存的感想",vm.Review);
        vm.ContinueEditingCommand.Execute(null);Assert.False(vm.ConfirmClose);
        vm.CloseCommand.Execute(null);vm.DiscardAndCloseCommand.Execute(null);Assert.Equal(1,closed);Assert.Equal(0,service.Writes);
    }
    [Fact]
    public async Task SwitchingReplaysCannotSilentlyDiscardFields()
    {
        var vm=Editor(new());await vm.InitializeAsync(Ct);vm.SelectedPlay=vm.Plays[1];vm.Notes="草稿";
        vm.SelectedPlay=vm.Plays[0];Assert.Equal("play-2",vm.SelectedPlay!.Play.Id);Assert.Equal("草稿",vm.Notes);Assert.True(vm.IsDirty);
        vm.SelectedPlay=null;Assert.Equal("play-2",vm.SelectedPlay!.Play.Id);Assert.Equal("草稿",vm.Notes);
        vm.NewRecordCommand.Execute(null);Assert.Equal("play-2",vm.SelectedPlay!.Play.Id);
        vm.ResetUnsavedCommand.Execute(null);Assert.False(vm.IsDirty);vm.SelectedPlay=vm.Plays[0];Assert.Equal("play-1",vm.SelectedPlay!.Play.Id);
    }
    [Fact]
    public async Task SavingAllPersonalFieldsUsesExactReplayAndPreservesTimeSeparation()
    {
        var service=new Service();var vm=Editor(service);await vm.InitializeAsync(Ct);vm.SelectedPlay=vm.Plays[1];
        vm.Review="新感想";vm.Score=0;vm.ExtraNotes="新备注二";vm.DurationKnown=true;vm.Hours=2;vm.Minutes=3;
        await vm.SaveCommand.ExecuteAsync(null);var draft=Assert.Single(service.Submitted);
        Assert.Equal("play-2",draft.PlayId);Assert.Equal("item-17",draft.Request.ItemId);Assert.Equal(3,draft.Request.ExpectedRevision);
        Assert.Equal(123,draft.Request.Details.DurationMinutes);Assert.Equal(0,draft.Request.Details.Score);Assert.Equal("备注一",draft.Request.Details.Notes);
        Assert.Equal("新备注二",draft.Request.Details.ExtraNotes);Assert.Equal("新感想",draft.Request.Details.Review);Assert.Equal("ja",draft.Request.Details.Language);
        Assert.Null(service.Plays[1].Time!.TotalSeconds);Assert.Equal(120,service.Plays[1].Time!.SessionSeconds);Assert.True(vm.Applied);Assert.False(vm.CanRetry);
    }
    [Fact]
    public async Task RestoringOfflineDraftSelectsItsExactReplayAndRetriesOneOperation()
    {
        var service=new Service { Failure=new WebUnavailableException() };var vm=Editor(service);await vm.InitializeAsync(Ct);vm.SelectedPlay=vm.Plays[1];vm.Review="离线草稿";
        await vm.SaveCommand.ExecuteAsync(null);var original=service.Pending!;vm=Editor(service);await vm.InitializeAsync(Ct);
        Assert.Equal("play-2",vm.SelectedPlay!.Play.Id);Assert.Equal("离线草稿",vm.Review);Assert.Contains("play-2",vm.TargetSummary);Assert.False(vm.Editable);
        service.Failure=null;await vm.RetryCommand.ExecuteAsync(null);Assert.Equal(1,service.Writes);Assert.All(service.Submitted,d=>Assert.Equal(original,d));
    }
    [Fact]
    public async Task ConflictRetainsDraftUntilExplicitReloadAndPreservesBothVersions()
    {
        var service=new Service { Failure=new ClientApiException("conflict",409) };var vm=Editor(service);await vm.InitializeAsync(Ct);vm.SelectedPlay=vm.Plays[1];vm.Review="我的版本";
        await vm.SaveCommand.ExecuteAsync(null);var original=vm.Draft!;Assert.True(vm.Conflict);Assert.False(vm.CanRetry);Assert.Equal("我的版本",vm.Review);
        service.Plays=[Play() with { Details=Play().Details with { Review="Web版本" },Revision=4 }];await vm.ReloadConflictCommand.ExecuteAsync(null);
        Assert.Equal(1,service.Withdrawals);Assert.False(vm.HasDraft);Assert.Equal("Web版本",vm.Review);Assert.Equal("我的版本",original.Request.Details.Review);
    }
    [Fact]
    public async Task ConfirmedSaveWithFailedRefreshCannotBeRetriedAsAnotherWrite()
    {
        var service=new Service { Plays=[Play()] };var vm=Editor(service,saved:()=>throw new IOException("synthetic read failure"));await vm.InitializeAsync(Ct);vm.Review="changed";
        await vm.SaveCommand.ExecuteAsync(null);Assert.True(vm.Applied);Assert.False(vm.CanRetry);Assert.Contains("已同步",vm.Message);Assert.Contains("刷新失败",vm.Message);Assert.Equal(1,service.Writes);
    }
    [Fact]
    public void DetailProjectionSeparatesUnknownTimeAndReplayIdentities()
    {
        var model=new BookDetailsViewModel();var book=new BookViewModel(M5Tests.Item(),null,[Play(),Play("play-2")]);model.Update(book,[],[],null);
        Assert.Equal(new[]{"play-1","play-2"},model.Plays.Select(r=>r.Play.Id));Assert.Contains("合计：未知",model.Plays[0].Time);Assert.Contains("手工基数：未知",model.Plays[0].Time);
        Assert.Equal(3,model.Plays[0].Notes.Count);Assert.False(model.LaunchConfigured);Assert.Contains("存在标签",model.LocalStatus);
    }
    [Theory]
    [InlineData("missing","item-17",false)]
    [InlineData("present","other-edition",false)]
    [InlineData("present","item-17",true)]
    public void OnlyExactPresentInstallationAndComponentCanShowConfiguredLaunch(string status,string linked,bool configured)
    {
        var model=new BookDetailsViewModel();var installation=new LocalInstallationView(new("installation",new("device",@"X:\Synthetic","Synthetic"),status,Now,1,Now,Now),[new("component","installation",new(linked,"."),1,Now,Now)]);
        var profile=new LaunchProfile("profile","Synthetic",@"X:\Synthetic\game.exe",[],@"X:\Synthetic",new string('0',64),"installation","component");
        model.Update(new(M5Tests.Item(),null),[installation],[profile],installation);Assert.Equal(configured,model.LaunchConfigured);
        model.Update(new(null,M5Tests.Item().Metadata),[installation],[profile],installation);Assert.False(model.LaunchConfigured);
    }
    [Fact]
    public void AmbiguousInstallationsOrCommandsRequireExplicitConfiguration()
    {
        var model=new BookDetailsViewModel();var book=new BookViewModel(M5Tests.Item(),null);
        var a=new LocalInstallationView(new("a",new("device",@"X:\Synthetic","Synthetic"),"present",Now,1,Now,Now),[new("ca","a",new(book.Id,"."),1,Now,Now)]);
        var b=a with { Installation=a.Installation with { Id="b" } };var profile=new LaunchProfile("p","Synthetic",@"X:\Synthetic\game.exe",[],@"X:\Synthetic",new string('0',64),"a","ca");
        model.Update(book,[a,b],[profile],null);Assert.False(model.LaunchConfigured);Assert.Contains("多个本机安装",model.LocalStatus);
        model.Update(book,[a,b],[profile],a);Assert.True(model.LaunchConfigured);
        model.Update(book,[a],[profile,profile with { Id="p2",Arguments=["other"] }],a);Assert.False(model.LaunchConfigured);Assert.Contains("多个启动命令",model.LocalStatus);
    }
}
