using System.Globalization;
using Vndb.Contracts;
using Vndb.Desktop.Converters;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class ConfigurationUiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => throw new InvalidOperationException("No dialog expected");
        public Task<string?> FolderAsync(string title) => throw new InvalidOperationException("No dialog expected");
        public Task<string?> SaveJsonAsync(string name) => throw new InvalidOperationException("No dialog expected");
    }
    private sealed class Library(IReadOnlyList<LibraryItemView>? items = null, IReadOnlyList<LocalInstallationView>? installations = null) : IDesktopLibrary
    {
        public Func<string,Task<IReadOnlyList<PersonalPlayView>>>? ReadPlays;
        public int Queries;
        public Task<ShelfResult> ShelfAsync(CancellationToken ct) => Task.FromResult(new ShelfResult(new("http://synthetic.test/",items ?? [],[],installations ?? [],Now),"Synthetic"));
        public Task<MetadataResponse> QueryAsync(string text,string source,CancellationToken ct) { Queries++;throw new InvalidOperationException("No network expected"); }
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId,CancellationToken ct) => ReadPlays?.Invoke(itemId) ?? Task.FromResult<IReadOnlyList<PersonalPlayView>>([]);
    }
    private sealed class DelayedRegistryDialog : IDesktopDialogs
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> File { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> RegistryFileAsync() { Started.SetResult();return File.Task; }
        public Task<string?> ExecutableAsync() => throw new NotSupportedException();
        public Task<string?> FolderAsync(string title) => throw new NotSupportedException();
        public Task<string?> SaveJsonAsync(string name) => throw new NotSupportedException();
    }
    private static PersonalPlayView Play(string id) => new(id,new("同名合成作品","v17","played",null,null,null,8.25m,"原感想",null,"原备注二","ja"),2,Now,Now,ItemId:"item-17");
    private static LocalInstallationView Install(string path,string device="synthetic-device") => new(new("synthetic-install",new(device,path,"合成完整合集"),"present",Now,1,Now,Now),
        [new("component-game","synthetic-install",new("item-17",".","game"),1,Now,Now),new("component-bonus","synthetic-install",new("item-18","bonus","bonus","特典"),1,Now,Now)]);

    [Fact]
    public void ReadableReplayChoicesKeepUnknownTimeAndDistinctStableIds()
    {
        var converter=new LocalChoiceConverter();var a=Play("replay-a");var b=Play("replay-b");
        var first=Assert.IsType<string>(converter.Convert(a,typeof(string),null,CultureInfo.InvariantCulture));
        var second=Assert.IsType<string>(converter.Convert(b,typeof(string),null,CultureInfo.InvariantCulture));
        Assert.Contains("已游玩",first);Assert.Contains("开始未知",first);Assert.Contains("手工 未知",first);Assert.Contains(a.Id,first);Assert.NotEqual(first,second);
        Assert.Equal("原备注二",a.Details.ExtraNotes);Assert.Null(a.Details.DurationMinutes);
    }
    [Theory]
    [InlineData("game","本篇")]
    [InlineData("bonus","特典")]
    [InlineData("edition","版本／章节")]
    [InlineData("other","其他组件")]
    public void ComponentDisplayDoesNotRewriteStoredCodes(string code,string label)
    {
        var row=new RegistrationRow(new(new("Synthetic"),".",code));
        Assert.Contains(label,row.Location);Assert.Equal(code,row.Component.Kind);
        Assert.Equal(label,new LocalChoiceConverter().Convert(code,typeof(string),"kind",CultureInfo.InvariantCulture));
    }
    [Fact]
    public async Task ComponentPickerKeepsExactIdsAndManualUnresolvedAssociation()
    {
        using var root=new TestDirectory();var installation=Install(root.Path);var library=new Library([M5Tests.Item(17),M5Tests.Item(18)],[installation]);
        var vm=new MainViewModel(library,new(root.Path),new Dialogs());await vm.RefreshCommand.ExecuteAsync(null);vm.SelectedInstallation=installation;
        Assert.Equal(2,vm.LaunchComponents.Count);Assert.Contains("特典",vm.LaunchComponents[1].Kind);
        vm.SelectedLaunchComponent=vm.LaunchComponents[1];Assert.Equal("component-bonus",vm.ComponentId);
        vm.SelectedLaunchComponent=null;Assert.Equal("component-bonus",vm.ComponentId);
        vm.ComponentId="retained-manual-id";Assert.Null(vm.SelectedLaunchComponent);Assert.Equal("retained-manual-id",vm.ComponentId);
        Assert.Equal(0,library.Queries);
    }
    [Fact]
    public void LaunchDraftAndModeChangesCannotReuseAnotherSavedCommandPreview()
    {
        using var root=new TestDirectory();var vm=new MainViewModel(new Library(),new(root.Path),new Dialogs());
        var profile=new LaunchProfile("profile","Synthetic",@"X:\Synthetic\wrapper.exe",["SiglusEngine.exe","a b"],@"X:\Synthetic",new string('0',64));
        vm.SelectedProfile=profile;Assert.True(vm.HasSavedLaunch);Assert.False(vm.LaunchDraftChanged);
        vm.ArgumentsText="\"unfinished";Assert.True(vm.LaunchDraftChanged);Assert.Contains("已保存配置",vm.LaunchDraftNotice);
        vm.ArgumentsText=LaunchArguments.Format(profile.Arguments);Assert.False(vm.LaunchDraftChanged);
        vm.LaunchPreview="old command";vm.LaunchMode="LE 默认配置";Assert.DoesNotContain("old command",vm.LaunchPreview);
        vm.LaunchPreview="other command";vm.SelectedProfile=profile with { Id="other",Title="Other" };Assert.DoesNotContain("other command",vm.LaunchPreview);
    }
    [Fact]
    public async Task ChangingLaunchAssociationPreservesDraftAndRequiresExplicitComponentChoice()
    {
        using var root=new TestDirectory();var first=Install(root.Path);var second=first with { Installation=first.Installation with { Id="second-install" },
            Components=[first.Components[0] with { Id="second-component",InstallationId="second-install" }] };
        var vm=new MainViewModel(new Library([M5Tests.Item()],[first,second]),new(root.Path),new Dialogs());await vm.RefreshCommand.ExecuteAsync(null);
        var profile=new LaunchProfile("p","Saved",@"X:\Synthetic\wrapper.exe",["SiglusEngine.exe"],@"X:\Synthetic",new string('0',64),first.Installation.Id,first.Components[0].Id);
        vm.SelectedProfile=profile;Assert.Same(first,vm.SelectedInstallation);
        vm.ArgumentsText="SiglusEngine.exe \"draft argument\"";var executable=vm.Executable;var arguments=vm.ArgumentsJson;var working=vm.WorkingDirectory;
        vm.SelectedLaunchInstallation=second;
        Assert.Same(profile,vm.SelectedProfile);Assert.Equal(executable,vm.Executable);Assert.Equal(arguments,vm.ArgumentsJson);Assert.Equal(working,vm.WorkingDirectory);
        Assert.Empty(vm.ComponentId);Assert.Null(vm.SelectedLaunchComponent);Assert.True(vm.LaunchDraftChanged);
        vm.SelectedLaunchComponent=vm.LaunchComponents[0];Assert.Equal("second-component",vm.ComponentId);
        vm.SelectedProfile=profile with { Id="p2",InstallationId=first.Installation.Id };Assert.Same(first,vm.SelectedInstallation);Assert.Equal(first.Components[0].Id,vm.ComponentId);
        vm.NewLaunchCommand.Execute(null);Assert.Null(vm.SelectedInstallation);Assert.Null(vm.SelectedProfile);Assert.Empty(vm.ComponentId);Assert.Empty(vm.Executable);Assert.Equal("Saved",profile.Title);
    }
    [Fact]
    public async Task OfflineWrapperSaveAndPreviewKeepAllArgumentsWithoutLaunchingOrQuerying()
    {
        using var root=new TestDirectory();var game=Path.Combine(root.Path,"Synthetic game");Directory.CreateDirectory(game);var executable=Path.Combine(game,"wrapper.exe");
        await File.WriteAllTextAsync(executable,"Synthetic fixture, never executed",Ct);var library=new Library();var vm=new MainViewModel(library,new(root.Path),new Dialogs());
        vm.LaunchTitle="Synthetic wrapper";vm.Executable=executable;vm.WorkingDirectory=game;
        var args=new[]{"SiglusEngine.exe","space argument","日本語",""};vm.ArgumentsText=LaunchArguments.Format(args);
        await vm.SaveLaunchCommand.ExecuteAsync(null);Assert.True(vm.HasSavedLaunch);Assert.False(vm.LaunchDraftChanged);
        Assert.Equal(args,vm.SelectedProfile!.Arguments);Assert.Equal(args,(await new LocalLauncher(root.Path).ReadAsync(Ct)).Single().Arguments);
        await vm.PreviewLaunchCommand.ExecuteAsync(null);Assert.Contains("尚未运行",vm.LaunchPreview);Assert.Contains("SiglusEngine.exe",vm.LaunchPreview);
        Assert.Equal(0,library.Queries);Assert.Equal("Synthetic fixture, never executed",await File.ReadAllTextAsync(executable,Ct));Assert.Empty(Directory.EnumerateFiles(root.Path,"*.db",SearchOption.AllDirectories));
    }
    [Fact]
    public void TrackingPathChangeDropsOldProcessSelectionWithoutEnumeratingProcesses()
    {
        using var root=new TestDirectory();var vm=new MainViewModel(new Library(),new(root.Path),new Dialogs());var process=new ProcessObservation(new(123,Now.UtcTicks,@"X:\Synthetic\old.exe"),1);
        vm.RunningProcesses.Add(process);vm.SelectedProcess=process;vm.ActualExecutable=@"X:\Synthetic\new.exe";
        Assert.Empty(vm.RunningProcesses);Assert.Null(vm.SelectedProcess);Assert.False(vm.CanStartTracked);
    }
    [Fact]
    public void RegistryConfirmCannotBeEnabledByCheckboxWithoutAValidPreview()
    {
        using var root=new TestDirectory();var vm=new MainViewModel(new Library(),new(root.Path),new Dialogs());vm.RegistryApproved=true;
        Assert.False(vm.HasRegistryPreview);Assert.False(vm.ApplyRegistryCommand.CanExecute(null));Assert.False(vm.RestoreRegistryCommand.CanExecute(null));
        vm.RegistryView=64;Assert.False(vm.RegistryApproved);Assert.Contains("重新",vm.RegistryPreview);
        vm.RegistryApproved=true;vm.RegistryEncoding="日文 Shift-JIS";Assert.False(vm.RegistryApproved);
        vm.RegistryApproved=true;vm.RegistrySource=@"X:\Synthetic\Changed";Assert.False(vm.RegistryApproved);
    }
    [Fact]
    public async Task LateRegFileReadCannotRevivePreviewForChangedConditions()
    {
        using var root=new TestDirectory();var file=Path.Combine(root.Path,"synthetic.reg");
        var text="Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\Software\\Synthetic]\n\"InstallDir\"=\"X:\\\\Synthetic\\\\Old\"\n";
        await File.WriteAllTextAsync(file,text,Ct);var dialogs=new DelayedRegistryDialog();var vm=new MainViewModel(new Library(),new(root.Path),dialogs);
        vm.RegistrySource=@"X:\Synthetic\Old";vm.RegistryTarget=@"X:\Synthetic\New";
        var pending=vm.ReadRegistryFileCommand.ExecuteAsync(null);await dialogs.Started.Task.WaitAsync(Ct);vm.RegistryView=64;dialogs.File.SetResult(file);await pending;
        Assert.Empty(vm.RegistryCandidates);Assert.False(vm.HasRegistryPreview);Assert.False(vm.RegistryApproved);Assert.Contains("条件已变化",vm.Status);
        Assert.Equal(text,await File.ReadAllTextAsync(file,Ct));
    }
    [Fact]
    public async Task LateSessionReadCannotReplaceAnotherItemsExperiences()
    {
        using var root=new TestDirectory();var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<IReadOnlyList<PersonalPlayView>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var library=new Library([M5Tests.Item(17),M5Tests.Item(18)]) { ReadPlays=id=>{Assert.Equal("item-17",id);start.SetResult();return release.Task;} };
        var vm=new MainViewModel(library,new(root.Path),new Dialogs());await vm.RefreshCommand.ExecuteAsync(null);vm.SelectedBook=vm.Shelf.Books.Single(b=>b.Id=="item-17");
        var pending=vm.LoadSessionPlaysCommand.ExecuteAsync(null);await start.Task.WaitAsync(Ct);var other=vm.Shelf.Books.Single(b=>b.Id=="item-18");vm.SelectedBook=other;release.SetResult([Play("replay-a")]);await pending;
        Assert.Same(other,vm.SelectedBook);Assert.Empty(other.Plays);Assert.Empty(vm.SessionPlays);Assert.Same(other,vm.BookDetails.Book);
    }
    [Fact]
    public async Task OrganizationPreviewAndApprovalAreInvalidatedByChangedConditionsOrNewScan()
    {
        using var root=new TestDirectory();var scope=Path.Combine(root.Path,"scope");var game=Path.Combine(scope,"game");var target=Path.Combine(root.Path,"target");Directory.CreateDirectory(game);Directory.CreateDirectory(target);
        var file=Path.Combine(game,"fixture.txt");await File.WriteAllTextAsync(file,"Synthetic, never moved",Ct);var data=Path.Combine(root.Path,"data");var device=await LocalDeviceIdentity.LoadOrCreateAsync(data,Ct);
        var installation=Install(game,device.Id) with { Components=[Install(game,device.Id).Components[0]] };var workspace=new LocalOrganizationWorkspace(data,device);
        var vm=new OrganizationFeatureViewModel(()=>workspace,()=>installation,()=>[installation],new Dialogs(),action=>action(Ct),_=>{});
        vm.SourceScope=scope;vm.TargetRoot=target;vm.Company="Synthetic";vm.JapaneseTitle="Synthetic game";
        vm.PlanHash=new string('a',64);vm.Approval=vm.PlanHash;Assert.False(vm.ExecuteCommand.CanExecute(null));Assert.False(vm.RestoreCommand.CanExecute(null));
        await vm.PreviewCommand.ExecuteAsync(null);Assert.Contains(game,vm.Summary);Assert.Equal("整理预览",vm.PreviewKind);Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.Approval=vm.PlanHash;Assert.True(vm.ExecuteCommand.CanExecute(null));Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.Company="Changed";Assert.Empty(vm.Approval);Assert.Empty(vm.PlanHash);Assert.False(vm.ExecuteCommand.CanExecute(null));
        await vm.PreviewCommand.ExecuteAsync(null);vm.Approval=vm.PlanHash;await vm.ScanCommand.ExecuteAsync(null);Assert.Empty(vm.PlanHash);Assert.Empty(vm.Approval);Assert.False(vm.ExecuteCommand.CanExecute(null));
        Assert.Equal("Synthetic, never moved",await File.ReadAllTextAsync(file,Ct));Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }
}
