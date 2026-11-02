using Vndb.Local;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Desktop;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Desktop.Views;
using Vndb.Desktop.Controls;
using Vndb.Desktop.Converters;
using Vndb.Client;
using Avalonia.Interactivity;

namespace Vndb.Desktop.Smoke;

public static class Program
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Provide an absolute private output directory.");
        var output = args[0]; Directory.CreateDirectory(output);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        var passed = session.Dispatch(async () =>
        {
            var logs = new Logs(); Logger.Sink = logs;
            var library = new DemoLibrary(); using var http = new HttpClient(new Covers());
            App.Covers = new(http, output, null);
            var vm = new MainViewModel(library, new(output), new Dialogs(), quickPlayService: new QuickService());
            var window = new MainWindow { DataContext = vm };
            window.Show(); await vm.RefreshCommand.ExecuteAsync(null); await Render(window);
            Check(window.GetVisualDescendants().OfType<AppIcon>().All(i => i.Data is not null && i.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(p => p.Bounds.Width > 0)), "embedded vector icons resolve and render");
            Save(window, output, "initial-layout.png");
            Check(vm.Shelf.Books.Count == 60 && vm.Shelf.Rows.Sum(r => r.Books.Count) == 60, $"large library is paged (books={vm.Shelf.Books.Count}, rows={vm.Shelf.Rows.Count}, status={vm.Status})");
            var cards = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CardRows");
            var initialRealizedRows = cards.GetRealizedContainers().Count(); var initialRows = vm.Shelf.Rows.Count;
            Check(cards.GetRealizedContainers().Count() < vm.Shelf.Rows.Count, "card rows virtualized");
            var card = cards.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card"));
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(vm.SelectedBook is not null, "card input routes to the shelf selection command");
            await Render(window);
            card = cards.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, vm.SelectedBook));
            window.MouseMove(card.TranslatePoint(new Point(20, 20), window)!.Value); await Render(window);
            Check(card.IsPointerOver && card.RenderTransform!.Value.M32 < 0, "card hover animates only the render transform");
            vm.ReducedMotion = true; await Render(window); Check(card.Transitions is null && card.RenderTransform!.Value.M32 == 0, "reduced motion removes hover movement");
            vm.ReducedMotion = false; window.MouseMove(new Point(1, 1)); await Render(window);
            var shelf = window.GetVisualDescendants().OfType<ShelfView>().First(s => s.IsEffectivelyVisible);
            var menu = shelf.CreateBookMenu((BookViewModel)card.DataContext!); menu.Open(card); await Render(window);
            var actions = menu.Items.OfType<MenuItem>().ToArray();
            Check(actions.Length == 9 && ReferenceEquals(actions[0].Command, vm.DirectBookCommand) && ReferenceEquals(actions[1].Command, vm.LocaleBookCommand) && ReferenceEquals(actions[2].Command, vm.EditBookPlayCommand), "book context actions use explicit book parameters and a launch reason");
            var archiveBook = (BookViewModel)card.DataContext!;
            var archiveOnly = new BookViewModel(archiveBook.Item, archiveBook.Metadata); archiveOnly.SetOwnership(false, ["Synthetic/game.zip"]);
            var archiveActions = shelf.CreateBookMenu(archiveOnly).Items.OfType<MenuItem>().ToArray();
            Check(archiveOnly.Item is not null && !archiveActions[0].IsEnabled && !archiveActions[1].IsEnabled && !archiveActions[3].IsEnabled && !archiveActions[5].IsEnabled, "archive-only personal cards never offer launch, installation folder or registry writes");
            Check(ReferenceEquals(archiveActions[6].Command, vm.RegisterPackageBookCommand), "card package association uses the shared feature command");
            Check(ReferenceEquals(actions[3].CommandParameter, card.DataContext), "context target is clicked book");
            Save(window, output, "book-context-menu.png"); menu.Close();
            var sort = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "书架排序");
            sort.SelectedItem = "所属会社"; Check(vm.Shelf.ShelfSort == "所属会社", "shelf sort compiled binding");
            await Render(window); Save(window, output, "cards-wide-light.png");
            Check(window.WindowDecorations == WindowDecorations.BorderOnly && WindowDecorationProperties.GetElementRole(window.FindControl<Border>("DragRegion")!).ToString() == "TitleBar", "custom chrome drag region keeps resize frame");
            await vm.SetThemeAsync("Dark"); await Render(window); Save(window, output, "cards-wide-dark.png");
            window.FindControl<Button>("ThemeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Render(window); Check(window.ActualThemeVariant == ThemeVariant.Light, "caption theme button changes application theme");
            window.FindControl<Button>("MaximizeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(window.WindowState == WindowState.Maximized, "custom maximize button");
            window.FindControl<Button>("MaximizeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(window.WindowState == WindowState.Normal, "custom restore button");
            window.FindControl<Button>("MinimizeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(window.WindowState == WindowState.Minimized, "custom minimize button"); window.WindowState = WindowState.Normal;
            await vm.SetThemeAsync("Dark");
            window.Width = 800; window.Height = 700; await Render(window); Save(window, output, "cards-compact-dark.png");
            shelf = window.GetVisualDescendants().OfType<ShelfView>().First(s => s.IsEffectivelyVisible);
            Check(!shelf.FindControl<Border>("DetailPanel")!.IsVisible && shelf.FindControl<Grid>("BookArea")!.Bounds.Width > 640, "800px keeps the full book area without a reserved detail column");
            vm.Shelf.SelectCommand.Execute(vm.Shelf.Books[0]); await Render(window);
            card = shelf.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, vm.SelectedBook));
            card.Focus(); window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); await Render(window);
            Check(vm.SelectedBook == vm.Shelf.Books[1], "card arrow key selects the adjacent exact item");
            Check(!vm.Shelf.DetailsOpen && library.QueryCalls == 0 && library.PlayReads == 0, "selection and arrow keys never query, edit or launch");
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Check(library.QueryCalls == 0, "Ctrl+Enter outside the query page never starts a query");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Render(window);
            Check(vm.Shelf.DetailsOpen && shelf.FindControl<Border>("DetailPanel")!.IsVisible && !shelf.FindControl<Grid>("BookArea")!.IsEnabled, "Enter opens the compact read-only detail pane");
            Check(vm.BookDetails.Releases.Any(r => r.Value.Contains("机翻")) && vm.BookDetails.Releases.Any(r => r.Value.Contains("2099-12")), "release catalogue retains translation flags and future partial dates in desktop details");
            Check(shelf.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("机翻") == true), "release metadata compiled template renders in the detail pane");
            Check(ReferenceEquals(window.FocusManager!.GetFocusedElement(), shelf.FindControl<Button>("CloseDetails")), "detail pane receives keyboard focus");
            Save(window, output, "u3-detail-800-dark.png");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await Render(window);
            Check(!vm.Shelf.DetailsOpen && !shelf.FindControl<Border>("DetailPanel")!.IsVisible, "Escape closes only the on-demand detail pane");
            Check((window.FocusManager!.GetFocusedElement() as Control)?.DataContext == vm.SelectedBook, "closing detail returns focus to the selected book");
            window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null); window.KeyRelease(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null); await Render(window);
            card = shelf.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, vm.SelectedBook));
            Check(card.ContextMenu?.IsOpen == true, "Shift+F10 opens the same book menu"); card.ContextMenu!.Close();
            var more = shelf.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("book-more") && ReferenceEquals(b.DataContext, vm.SelectedBook));
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Render(window);
            Check(more.ContextMenu!.IsOpen && more.ContextMenu.Items.OfType<MenuItem>().All(m => ReferenceEquals(m.CommandParameter, vm.SelectedBook)), "visible more menu uses the same exact target for every action");
            more.ContextMenu.Close();
            window.Width = 1024; window.Height = 768; await Render(window);
            Check(!shelf.FindControl<Border>("DetailPanel")!.IsVisible && shelf.FindControl<Grid>("BookArea")!.Bounds.Width > 860, "1024px uses a full-width shelf");
            Save(window, output, "u3-cards-1024-dark.png");
            window.Width = 1400; window.Height = 900; await Render(window);
            Check(shelf.FindControl<Border>("DetailPanel")!.IsVisible && shelf.FindControl<Border>("DetailPanel")!.Bounds.Width == 304, "wide window reserves one shared detail pane");
            var beforeViewSwitch = vm.SelectedBook;
            vm.Shelf.ListMode = true; await Render(window); vm.ArgumentsText = "--u3-draft";
            vm.Shelf.SortDescending = !vm.Shelf.SortDescending; await Render(window);
            Check(ReferenceEquals(beforeViewSwitch, vm.SelectedBook) && vm.ArgumentsText == "--u3-draft", "list sort does not clear selection or a local launch draft");
            vm.ArgumentsText = "";
            Check(library.QueryCalls == 0 && library.PlayReads == 0 && !vm.Status.Contains("启动失败"), "shelf input and details perform no launch or network action");
            await vm.SetThemeAsync("Light"); window.Width = 800; window.Height = 640; vm.Shelf.ListMode = true; await Render(window); Save(window, output, "list-compact-light.png");
            window.Width = 1920; window.Height = 1080; await Render(window); Save(window, output, "list-large-light.png");
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "BookList");
            Check(list.GetRealizedContainers().Count() < vm.Shelf.Books.Count, "list virtualized");
            list.SelectedIndex = 1; Check(vm.SelectedBook == vm.Shelf.Books[1], "compiled list selection binding");
            list.ScrollIntoView(vm.Shelf.Books[^1]); await Render(window); Check(list.GetRealizedContainers().Count() < vm.Shelf.Books.Count, "scroll recycles");
            var originalOffset = list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y;
            Check(originalOffset > 0, "K2 scroll fixture actually moved beyond the first row");
            vm.Section = 1; await Render(window); vm.Section = 0; await Render(window);
            var restoredList = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "BookList");
            Check(Math.Abs(restoredList.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y - originalOffset) < 2, "K2 returning from played view restores the home list position");
            window.Width = 1320; window.Height = 880; vm.Section = 1; vm.Shelf.ListMode = true; await Render(window); Save(window, output, "played-list.png");
            Check(vm.Shelf.Books.Count > 0 && vm.Shelf.Books.All(b => b.HasPlayed && b.Plays.All(p => p.ItemId == b.Id)), "played list uses exact stable associations");
            vm.Shelf.ListMode = false; await Render(window); Save(window, output, "played-cards.png");
            Check(vm.Shelf.Rows.SelectMany(r => r.Books).All(b => b.Plays.All(p => p.ItemId == b.Id)), "played card templates share stable records");
            vm.Section = 0; window.Width = 1400; vm.Shelf.SelectCommand.Execute(vm.Shelf.Books.First(b => b.Plays.Count > 0)); await Render(window);
            var recordEdit = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.EditPlayRecordCommand));
            var exactRecord = (PlayDetailRow)recordEdit.CommandParameter!;
            Check(vm.BookDetails.Plays.All(p => p.Play.ItemId == vm.SelectedBook!.Id), "structured details contain only the exact selected item replays");
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)recordEdit.Command!).ExecuteAsync(recordEdit.CommandParameter); await Render(window);
            Check(vm.QuickEditor?.SelectedPlay?.Play.Id == exactRecord.Play.Id && vm.QuickEditor.TargetSummary.Contains(exactRecord.Play.Id), "detail record action opens the exact replay with its revision");
            vm.QuickEditor!.CloseCommand.Execute(null); await Render(window);
            Check(!vm.QuickEditorOpen && window.IsVisible, "clean editor close returns to the same window without writes");
            await vm.EditBookPlayCommand.ExecuteAsync(vm.Shelf.Books[0]); await Render(window);
            Check(vm.QuickEditorOpen, "book action opens in-window quick editor");
            vm.QuickEditor!.NewRecordCommand.Execute(null); vm.QuickEditor.DurationKnown = true;
            var hours = window.GetVisualDescendants().OfType<NumericUpDown>().Single(n => Avalonia.Automation.AutomationProperties.GetName(n) == "手工游玩小时");
            hours.Value = 2; Check(vm.QuickEditor.Hours == 2, "manual hours compiled binding");
            Save(window, output, "quick-edit-light.png"); await vm.SetThemeAsync("Dark"); await Render(window); Save(window, output, "quick-edit-dark.png");
            window.Width = 800; await Render(window); Save(window, output, "quick-edit-compact.png"); window.Width = 1320;
            var editorView = window.GetVisualDescendants().OfType<QuickPlayEditorView>().Single();
            var editorTarget = vm.QuickEditor.TargetSummary;
            foreach (var theme in new[] { "Light", "Dark" })
            {
                await vm.SetThemeAsync(theme);
                foreach (var width in new[] { 800d, 1024d, 1400d, 1920d })
                {
                    window.Width = width; window.Height = width == 800 ? 640 : 900; await Render(window);
                    Check(editorView.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox or NumericUpDown or Button).All(c => c.TranslatePoint(default, window) is not { } p || p.X >= -1 && p.X + c.Bounds.Width <= window.Bounds.Width + 1) && vm.QuickEditor.TargetSummary == editorTarget, "editor viewport/theme changes retain target and horizontal access");
                    Save(window, output, $"u7-editor-{theme}-{width}.png");
                }
            }
            window.Width = 1320; window.Height = 880;
            Check(!window.FindControl<Grid>("Workspace")!.IsEnabled && editorView.IsEffectivelyEnabled, "personal editor isolates the browsing workspace");
            Check(!vm.QuickEditor.CanChooseRecord && vm.QuickEditor.TargetSummary.Contains("新建"), "dirty replay draft locks target selection and displays exact intent");
            var reviewBox = editorView.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "本次游玩感想");
            reviewBox.Text = "合成编辑感想"; Check(vm.QuickEditor.Review == reviewBox.Text, "personal review uses a compiled editable binding");
            vm.EscapeCommand.Execute(null); await Render(window);
            Check(vm.QuickEditorOpen && vm.QuickEditor.ConfirmClose && vm.QuickEditor.Review == "合成编辑感想", "Escape retains dirty fields and requests explicit discard");
            Save(window, output, "quick-edit-discard.png");
            vm.QuickEditor.ContinueEditingCommand.Execute(null); await Render(window);
            var closeEditor = editorView.FindControl<Button>("CloseEditor")!; closeEditor.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null); window.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null); await Render(window);
            Check(window.FocusManager!.GetFocusedElement() is Control inside && inside.GetVisualAncestors().Contains(editorView), "reverse Tab remains within the editor focus cycle");
            vm.EscapeCommand.Execute(null); vm.QuickEditor.DiscardAndCloseCommand.Execute(null); await Render(window);
            Check(!vm.QuickEditorOpen && window.FindControl<Grid>("Workspace")!.IsEnabled, "explicit discard returns without creating a write");
            Check(window.FocusManager!.GetFocusedElement() is Control returned && returned.IsEffectivelyEnabled && !returned.GetVisualAncestors().OfType<QuickPlayEditorView>().Any(), "closing editor restores a usable browser focus target: " + (window.FocusManager!.GetFocusedElement() is Control diagnostic ? $"{diagnostic.GetType().Name}/{diagnostic.Name}, enabled={diagnostic.IsEffectivelyEnabled}, visible={diagnostic.IsEffectivelyVisible}, selected={vm.SelectedBook?.Id}, page={vm.Section}" : "none"));
            await vm.SetThemeAsync("Light"); vm.ReducedMotion = true; Check(window.Classes.Contains("no-motion"), "reduced motion disables custom transitions");
            vm.Section = 2; await Render(window);
            Check(vm.Shelf.IsQuery && !vm.Shelf.FiltersExpanded && vm.Shelf.FilterHeading.Contains("本次"), "query result filtering is independently scoped and initially collapsed");
            var querySource = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "查询来源");
            querySource.SelectedItem = vm.Discovery.SourceChoices.Single(s => s.Code == "cache");
            Check(vm.Discovery.QuerySource == "cache", "readable query source choices preserve the existing protocol code");
            querySource.SelectedItem = vm.Discovery.SourceChoices[0];
            var query = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "VNDB查询文字");
            query.Focus(); window.KeyTextInput("合成查询"); Check(vm.Discovery.Query == "合成查询", "query feature IME text binding");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Discovery.SearchCommand)) &&
                window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Discovery.ExportCommand)), "query feature command bindings");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Check(window.FocusManager!.GetFocusedElement() != query, "keyboard Tab navigation");
            library.Block = true; var pending = vm.Discovery.SearchCommand.ExecuteAsync(null); Check(vm.Busy, "query running");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); await pending; Check(!vm.Busy && vm.Status.Contains("已取消"), "Escape cancels query");
            library.Block = false; await vm.Discovery.SearchCommand.ExecuteAsync(null); Check(vm.Shelf.Source.Contains("待同步"), "offline query source");
            await Render(window); Save(window, output, "query.png");
            var noActionQueries = library.QueryCalls; var noActionPlays = library.PlayReads;
            var u5Now = DateTimeOffset.UtcNow;
            var u5Install = new LocalInstallationView(new("u5-install", new("synthetic-device", @"X:\Synthetic\Complete", "合成完整合集"), "present", u5Now, 1, u5Now, u5Now),
                [new("u5-game", "u5-install", new("item-1", ".", "game"), 1, u5Now, u5Now), new("u5-bonus", "u5-install", new("item-3", "bonus", "bonus", "特典"), 1, u5Now, u5Now)]);
            vm.SelectedInstallation = u5Install;
            var u5Profile = new LaunchProfile("u5-profile", "合成包装器配置", @"X:\Synthetic\Complete\wrapper.exe", ["SiglusEngine.exe", "argument with spaces"], @"X:\Synthetic\Complete", new string('0', 64), "u5-install", "u5-game");
            vm.Profiles.Add(u5Profile); vm.SelectedProfile = u5Profile;
            for (var section = 3; section <= 6; section++)
            {
                vm.Section = section; await Render(window); Save(window, output, "section-" + section + ".png");
                window.Width = 800; await Render(window); Save(window, output, "section-" + section + "-compact.png"); window.Width = 1320;
            }
            Check(library.QueryCalls == noActionQueries && library.PlayReads == noActionPlays, "configuration page navigation never queries metadata or personal records");
            vm.Section = 3; window.Width = 800; await Render(window);
            var launchView = window.GetVisualDescendants().OfType<LaunchView>().Single();
            Check(Grid.GetRow(launchView.FindControl<ScrollViewer>("EditorScroll")!) == 1 && launchView.FindControl<Border>("ProfilesPane")!.Bounds.Width > 600, "compact launch stacks editor below a bounded profile list");
            var launchScroll = launchView.FindControl<ScrollViewer>("PageScroll")!;
            launchScroll.Offset = new(0, Math.Max(0, launchScroll.Extent.Height - launchScroll.Viewport.Height)); await Render(window);
            Save(window, output, "u5-launch-compact-lower.png");
            window.Width = 1400; await Render(window);
            Check(Grid.GetColumn(launchView.FindControl<ScrollViewer>("EditorScroll")!) == 1, "wide launch retains independently scrollable columns");
            var componentPicker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "启动关联组件");
            componentPicker.SelectedItem = vm.LaunchComponents[1]; Check(vm.ComponentId == "u5-bonus", "readable component picker writes the exact stable component ID");
            var launchArgs = window.GetVisualDescendants().OfType<TextBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "自定义启动参数");
            launchArgs.Text = "SiglusEngine.exe \"new argument\""; await Render(window);
            Check(vm.LaunchDraftChanged && window.GetVisualDescendants().OfType<TextBlock>().Any(c => c.Text == vm.LaunchDraftNotice), "saved-launch provenance remains visible while editing a draft");
            Save(window, output, "u5-launch-saved-vs-draft.png");
            vm.Section = 4; window.Width = 800; await Render(window);
            var organizeView = window.GetVisualDescendants().OfType<OrganizeView>().Single();
            var packageRoot = organizeView.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "安装包检索根");
            packageRoot.Text = @"D:\Synthetic\Packages";
            Check(vm.Packages.ScanRoot == packageRoot.Text, "package root uses compiled feature binding without scanning");
            Check(organizeView.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Packages.ConfirmCommand) && !b.IsEffectivelyEnabled), "package confirmation disabled without a verified filename match preview");
            Check(Grid.GetRow(organizeView.FindControl<Border>("PreviewPane")!) == 1, "compact organization stacks preview after scope setup");
            var orgScroll = organizeView.FindControl<ScrollViewer>("PageScroll")!; orgScroll.Offset = new(0, Math.Max(0, orgScroll.Extent.Height - orgScroll.Viewport.Height)); await Render(window);
            var moveButton = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.Organization.ExecuteCommand));
            var movePoint = moveButton.TranslatePoint(default, window)!.Value;
            Check(movePoint.Y >= 0 && movePoint.Y + moveButton.Bounds.Height < window.Bounds.Height && !moveButton.IsEffectivelyEnabled, "organization confirmation is reachable and disabled without proof");
            Save(window, output, "u5-organization-compact-confirm.png");
            window.Width = 1320; vm.Section = 4; await Render(window);
            vm.RegisterBookCommand.Execute(vm.Shelf.Books.First()); vm.Section = 7; await Render(window);
            var registrationRoot = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "登记完整目录");
            registrationRoot.Text = @"D:\Synthetic\CompleteContainer";
            Check(vm.Registration.RootPath == registrationRoot.Text, "registration root uses independent feature state");
            vm.Registration.WorkTitle = "合成ゲーム · 長いタイトルの表示確認";
            var addComponent = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.Registration.AddComponentCommand));
            addComponent.Command!.Execute(addComponent.CommandParameter); await Render(window);
            Check(vm.Registration.Components.Count == 1, "registration adds explicit component drafts without filesystem access");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Registration.ConfirmCommand) && !b.IsEffectivelyEnabled), "registration confirmation disabled until a verified preview and approval");
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await vm.SetThemeAsync(theme.ToString()); Check(window.ActualThemeVariant == theme, "registration uses the selected " + theme + " theme");
                foreach (var width in new[] { 1320d, 800d })
                {
                    window.Width = width; await Render(window); Save(window, output, "registration-" + theme + "-" + width + ".png");
                    if (width == 800)
                    {
                        var registration = window.GetVisualDescendants().OfType<RegistrationView>().Single(); var scroll = registration.FindControl<ScrollViewer>("PageScroll")!;
                        scroll.Offset = new(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)); await Render(window);
                        var confirm = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.Registration.ConfirmCommand));
                        var location = confirm.TranslatePoint(default, window)!.Value;
                        Check(location.Y >= 0 && location.Y + confirm.Bounds.Height <= window.Bounds.Height, "compact registration confirmation stays reachable by scrolling");
                        Save(window, output, "registration-" + theme + "-compact-confirm.png");
                    }
                }
            }
            Check(window.GetVisualDescendants().OfType<RegistrationView>().Any(v => v.Bounds.Width > 0), "registration renders wide and stacked compact layouts");
            window.Width = 1320; vm.Section = 4; await Render(window);
            var scope = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "工作台源范围");
            scope.Text = "Synthetic scope"; Check(vm.Organization.SourceScope == scope.Text, "organization feature and settings share one scope state");
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "高级：回执、续跑与复原")).IsExpanded = true; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Organization.PreviewCommand)) &&
                window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Organization.ResumeCommand)), "organization feature command bindings");
            vm.Section = 5; await Render(window);
            var settingsScope = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "扫描源范围");
            Check(settingsScope.Text == "Synthetic scope", "settings binds organization state directly without shell forwarding");
            var observation = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "本机变化观测／待匹配候选"));
            observation.IsExpanded = true; await Render(window);
            var observePath = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "观测管理根");
            observePath.Text = @"E:\Synthetic\Games"; Check(vm.Observation.RootPath == observePath.Text, "observation management root compiled binding");
            var observationRoot = new ObservationRoot(Guid.NewGuid().ToString("D"), @"E:\Synthetic\Games", "games", 2, new("synthetic-volume", "synthetic-root", 1));
            vm.Observation.Load(new(true, [observationRoot]));
            var candidate = new ObservationCandidate("synthetic-candidate", observationRoot.Id, "Studio/NewGame", "directory", new("synthetic-volume", "synthetic-new", 1), 0, default, DateTimeOffset.UtcNow, "pending");
            vm.Observation.Poll(new(new("synthetic-device", [], [], [candidate]), new(1, 12, 2, 5, 0, 0, 0, 0), [observationRoot.Path], false, false, DateTimeOffset.UtcNow), "合成浅层监控示例");
            await Render(window);
            Check(window.GetVisualDescendants().OfType<ListBox>().Any(b => ReferenceEquals(b.ItemsSource, vm.Observation.Candidates)), "local candidate ListBox compiled binding");
            Check(vm.Observation.Candidates.Count == 1 && vm.Observation.Summary.Contains("哈希0字节"), "observation candidates and metadata-only disclosure are visible without scanning");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Observation.CheckCommand)), "manual observation command binding");
            observation.BringIntoView(); await Render(window);
            Save(window, output, "k1-observation-settings.png"); observation.IsExpanded = false;
            var presence = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "本地存在标记／冲突恢复"));
            presence.IsExpanded = true; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Presence.ConfirmCommand) && !b.IsEffectivelyEnabled), "presence replacement requires preview and explicit approval");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Presence.RetryCommand)), "presence retry binding");
            var queryData = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "查询JSON作用域／旧资料迁移"));
            queryData.IsExpanded = true; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.QueryData.ApplyCommand) && !b.IsEffectivelyEnabled), "query migration requires preview and explicit approval");
            var imports = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "导入限额／最近逐项回执"));
            imports.IsExpanded = true; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.Imports.RefreshCommand)), "import discovery and receipt command binding");
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == vm.Imports.Summary), "import guidance compiled binding");
            vm.Imports.Results = [new("metadata", "synthetic-original-batch", "preview", "原稿已接收，等待处理"), new("play_edits", "synthetic-conflict", "conflict", "双方原稿保留")];
            await Render(window);
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Web已接收，等待处理"), "metadata receipt acceptance does not claim processing complete");
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "冲突待处理"), "activity conflict has a readable local state");
            Check(window.GetVisualDescendants().OfType<Expander>().Count(e => Equals(e.Header, "原始状态与键")) == 2, "each receipt retains expandable original evidence");
            imports.BringIntoView(); await Render(window);
            Save(window, output, "u6-import-feedback.png");
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "查询JSON存放位置")).IsExpanded = true; await Render(window);
            var queryRoot = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "查询JSON根目录");
            queryRoot.Text = "Synthetic query root"; Check(vm.QueryRootOverride == queryRoot.Text, "query root override binding");
            var localeTool = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "默认转区工具路径");
            localeTool.Text = @"C:\Synthetic\Locale\LEProc.exe"; Check(vm.LocaleEmulatorPath == localeTool.Text, "global locale tool compiled binding");
            Save(window, output, "global-locale.png"); vm.Section = 3; await Render(window);
            var arguments = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "自定义启动参数");
            arguments.Text = "SiglusEngine.exe"; Check(vm.ArgumentsJson.Contains("SiglusEngine.exe"), "custom wrapper argument binding");
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "高级：参数、组件ID与候选程序")).IsExpanded = true; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.DiscoverExecutablesCommand)), "executable discovery command binding");
            var repair = window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "迁移后的注册信息检查／路径修复"));
            repair.IsExpanded = true; await Render(window);
            var oldPath = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "注册表旧目录");
            oldPath.Text = @"E:\Synthetic\Old"; Check(vm.RegistrySource == oldPath.Text, "registry path preview binding without registry access");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.ApplyRegistryCommand) && !b.IsEffectivelyEnabled), "registry apply requires explicit approval");
            Save(window, output, "registry-preview.png"); vm.Section = 6; await Render(window);
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "高级：包装器与实际进程")).IsExpanded = true;
            window.GetVisualDescendants().OfType<Expander>().Single(e => Equals(e.Header, "高级：同步、导出与冲突")).IsExpanded = true; await Render(window);
            var actual = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "实际游戏路径");
            actual.Text = @"C:\Synthetic\game.exe"; Check(vm.ActualExecutable == actual.Text, "session executable binding");
            var review = window.GetVisualDescendants().OfType<TextBox>().Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == "本次感想");
            review.Text = "合成会话感想"; Check(vm.QuickReview == review.Text, "session review binding");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.StartTrackedCommand)), "session start command binding");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.ExportActivityCommand)), "activity export command binding");
            var readablePlay = new PersonalPlayView("u5-replay-one", new("合成重玩记录", "v1", "played", null, null, null, 8.25m, "感想", null, null, "ja"), 2, u5Now, u5Now, ItemId: "item-1");
            vm.SessionPlays.Add(readablePlay); vm.SessionPlays.Add(readablePlay with { Id = "u5-replay-two" }); vm.SelectedPlay = readablePlay; vm.QuickStatus = "completed"; await Render(window);
            var experience = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "选择已有游玩经历");
            Check(ReferenceEquals(experience.SelectedItem, readablePlay) && experience.GetVisualDescendants().OfType<TextBlock>().Any(c => c.Text?.Contains("开始未知") == true && c.Text.Contains(readablePlay.Id)), "readable replay template preserves exact identity and unknown time");
            var stateChoice = window.GetVisualDescendants().OfType<ComboBox>().Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "本次游玩状态");
            Check(Equals(stateChoice.SelectedItem, "completed") && stateChoice.GetVisualDescendants().OfType<TextBlock>().Any(c => c.Text == "本次完成"), "localized state labels retain the stored state code");
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.LaunchCommand) && b.IsEffectivelyEnabled) && !vm.CanStartTracked, "basic launch stays available while optional timing has no selected target");
            Save(window, output, "u5-readable-session.png");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                await vm.SetThemeAsync(theme);
                foreach (var width in new[] { 800d, 1024d, 1400d, 1920d })
                {
                    window.Width = width; window.Height = width == 800 ? 640 : width == 1024 ? 768 : 900;
                    foreach (var section in new[] { 3, 4, 5, 6, 7 })
                    {
                        vm.Section = section; await Render(window);
                        var page = window.FindControl<TransitioningContentControl>("PageHost")!;
                        var overflowing = page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBox or ComboBox or Button)
                            .Any(c => c.TranslatePoint(default, window) is { } point && (point.X < -1 || point.X + c.Bounds.Width > window.Bounds.Width + 1));
                        Check(!overflowing, $"U5 form controls stay inside the {width}px {theme} section {section}");
                        Save(window, output, $"u5-{theme}-{width}-section-{section}.png");
                    }
                }
            }
            foreach (var theme in new[] { "Light", "Dark" })
            {
                await vm.SetThemeAsync(theme);
                foreach (var width in new[] { 800d, 1024d, 1400d, 1920d })
                {
                    window.Width = width; window.Height = width == 800 ? 640 : width == 1024 ? 768 : 900;
                    foreach (var section in new[] { 0, 1, 2 })
                    {
                        vm.Section = section;
                        foreach (var isList in new[] { false, true })
                        {
                            vm.Shelf.ListMode = isList; await Render(window);
                            Check(window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("book-more")).All(b => b.Bounds.Width >= 32), "visible book menus retain keyboard/touch targets");
                            Save(window, output, $"u7-{theme}-{width}-section-{section}-{(isList ? "list" : "cards")}.png");
                        }
                    }
                }
            }
            var beforeCatalog = library.QueryCalls; vm.Section = 8; await Render(window);
            Check(library.QueryCalls == beforeCatalog && library.CatalogCalls == 0, "public catalogue navigation never automatically queries or mutates a personal shelf");
            Check(!vm.PublicCatalog.PreviousCommand.CanExecute(null) && !vm.PublicCatalog.NextCommand.CanExecute(null) && !vm.PublicCatalog.DetailsCommand.CanExecute(null), "public navigation boundaries disabled before an explicit read");
            vm.PublicCatalog.FiltersExpanded = true; await Render(window);
            vm.PublicCatalog.ProducerQuery = "合成会社";
            Check(library.ProducerCalls == 0, "K2 typing a company name does not query");
            await vm.PublicCatalog.FindProducersCommand.ExecuteAsync(null); await Render(window);
            vm.PublicCatalog.Producer = vm.PublicCatalog.Producers[0];
            Check(vm.PublicCatalog.ProducerId == "p1" && library.ProducerCalls == 1 && library.CatalogCalls == 0 && library.QueryCalls == beforeCatalog, "K2 named company selection edits the draft without upstream or catalog browsing");
            var catalogQuery = window.GetVisualDescendants().OfType<TextBox>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "公共目录关键词");
            catalogQuery.Focus(); window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null); await Render(window);
            Check(library.CatalogCalls == 1, "public catalogue Ctrl+Enter applies an explicit read from the filter input");
            vm.PublicCatalog.Selected = vm.PublicCatalog.Books[0]; await Render(window);
            Check(window.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("catalog-card") && b.Classes.Contains("chosen")), "public card selection has a persistent shared selected border");
            vm.PublicCatalog.Language = vm.PublicCatalog.Languages[1];
            Check(vm.PublicCatalog.HasPendingFilters && vm.PublicCatalog.FilterStatus.Contains("筛选已修改"), "public draft filters are distinguished from the successful page");
            vm.PublicCatalog.ResetFiltersCommand.Execute(null); vm.PublicCatalog.FiltersExpanded = true;
            foreach (var theme in new[] { "Light", "Dark" })
            {
                await vm.SetThemeAsync(theme);
                foreach (var width in new[] { 800d, 1400d })
                {
                    window.Width = width; window.Height = width == 800 ? 640 : 900;
                    foreach (var catalogListMode in new[] { false, true })
                    {
                        vm.PublicCatalog.ListMode = catalogListMode; await Render(window);
                        var page = window.FindControl<TransitioningContentControl>("PageHost")!;
                        Check(!page.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBox or ComboBox or Button)
                            .Any(c => c.TranslatePoint(default, window) is { } p && (p.X < -1 || p.X + c.Bounds.Width > window.Bounds.Width + 1)), "O5 public catalogue controls remain inside compact/wide light/dark layouts");
                        Check(vm.PublicCatalog.Books.All(b => b.Personal.Contains("未记录") && b.Source.Contains("部分覆盖")), "public card/list share explicit personal aggregate and partial provenance text");
                        Save(window, output, $"o5-{theme}-{width}-{(catalogListMode ? "list" : "cards")}.png");
                    }
                }
            }
            Check(library.CatalogCalls == 1 && library.QueryCalls == beforeCatalog, "layout and theme changes do not refetch public catalogue or alter query results");
            vm.Section = 2; window.Width = 1320; window.Height = 880; await Render(window);
            vm.Discovery.History.Entries = ["合成の履歴"]; vm.Discovery.History.Selected = "合成の履歴";
            window.GetVisualDescendants().OfType<Expander>().First(e => Equals(e.Header, "最近搜索（仅本机）")).IsExpanded = true; await Render(window);
            var recall = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.Discovery.History.RecallCommand));
            recall.Focus(); window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); await Render(window);
            Check(vm.Discovery.Query == "合成の履歴" && library.QueryCalls == beforeCatalog, "K2 keyboard history recall fills without querying");
            Save(window, output, "k2-query-history.png");
            library.Summary = true; vm.Section = 0; vm.Shelf.Filter = ""; vm.Shelf.CompanyFilter = "全部会社"; vm.Shelf.ShelfState = "全部状态"; vm.Shelf.LocalOnly = false; vm.Shelf.SelectedBook = null;
            await vm.RefreshCommand.ExecuteAsync(null); await Render(window);
            var summaryBook = vm.Shelf.Books.First(b => b.HasPlayed); vm.SelectedBook = summaryBook;
            Check(summaryBook.PlaysPending && summaryBook.DetailsPending && !summaryBook.NeedsPlayReview, "summary card retains played state without fabricated full records");
            Check(summaryBook.Description.Contains("尚未加载"), "summary detail has an explicit unloaded state");
            Save(window, output, "k1-summary-shelf.png");
            await vm.DetailCommand.ExecuteAsync(null); await Render(window);
            Check(!summaryBook.PlaysPending && !summaryBook.DetailsPending && summaryBook.HasPlayed && summaryBook.BookNotes.Count == 1, "lazy detail loads notes and exact play records without losing played state");
            Save(window, output, "k1-lazy-detail.png");
            Check(window.GetVisualDescendants().OfType<ComboBox>().Any(c => ReferenceEquals(c.ItemsSource, vm.Shelf.ResourceFilters)), "K2 resource filter compiled binding");
            vm.Shelf.QuickView = "未记录"; await Render(window);
            Check(vm.Shelf.Books.All(b => b.IsUnrecorded) && vm.Shelf.Conditions.Any(c => c.Key == "progress"), "K2 quick view and removable condition chips share predicates");
            vm.Shelf.ResetFiltersCommand.Execute(null); vm.Shelf.SelectedBook = null; vm.Shelf.ListMode = false; await Render(window);
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text?.StartsWith("本机：", StringComparison.Ordinal) == true), "K2 cards display independent local resource status");
            var stateCard = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
            Check(stateCard.GetVisualDescendants().OfType<TextBlock>().All(t => t.TranslatePoint(default, stateCard) is { } p && p.Y >= 0 && p.Y + t.Bounds.Height <= stateCard.Bounds.Height), "K2 state card has enough height for all independent labels");
            Save(window, output, "k2-state-cards.png");
            vm.Shelf.ListMode = true; await Render(window); Save(window, output, "k2-state-list.png");
            vm.SelectedBook = summaryBook; vm.Shelf.OpenDetailsCommand.Execute(null); await Render(window);
            var k3parent = vm.SelectedBook;
            Check(vm.BookDetails.Relations.Rows.Count == 1 && library.LocalReads == 0, "K3 opening details never prefetches relation targets");
            var relationButton = window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, vm.OpenRelatedCommand));
            await vm.OpenRelatedCommand.ExecuteAsync(relationButton.CommandParameter); await Render(window);
            Check(library.LocalReads == 1 && ReferenceEquals(vm.SelectedBook, k3parent) && !vm.BookDetails.IsPersonal && vm.BookDetails.Book?.Metadata?.VndbId == "v2", "K3 relation input opens read-only public metadata without selecting a personal variant");
            Check(!window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.EditBookPlayCommand)), "K3 related public detail hides personal record mutation controls");
            Save(window, output, "k3-related-details.png");
            vm.BackRelatedCommand.Execute(null); await Render(window);
            Check(ReferenceEquals(vm.BookDetails.Book, k3parent) && library.LocalReads == 1, "K3 back returns cached original detail without transport");
            vm.TitleChoice = vm.TitleChoices.Single(c => c.Code == "latin"); await Render(window);
            Check(summaryBook.JapaneseSubtitle.Length > 0 && summaryBook.ArchiveTitle.StartsWith("星の", StringComparison.Ordinal), "K3 title preference adds Japanese subtitle and leaves archive title Japanese");
            vm.Shelf.ListMode = false; vm.Shelf.CloseDetailsCommand.Execute(null); await Render(window);
            var bilingual = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
            Check(bilingual.Classes.Contains("bilingual") && bilingual.GetVisualDescendants().OfType<TextBlock>().All(t => t.TranslatePoint(default, bilingual) is { } p && p.Y >= 0 && p.Y + t.Bounds.Height <= bilingual.Bounds.Height), "K3 bilingual cards keep all labels inside the card bounds");
            Save(window, output, "k3-bilingual-cards.png");
            vm.Section = 5; await Render(window);
            Check(window.GetVisualDescendants().OfType<ComboBox>().Any(c => ReferenceEquals(c.ItemsSource, vm.TitleChoices)), "K3 title language preference compiled binding resolves in settings");
            vm.Section = 0; vm.TitleChoice = vm.TitleChoices[0]; await Render(window);
            var chromeFocus = window.FindControl<Button>("ThemeButton")!;
            chromeFocus.Focus(NavigationMethod.Tab); await Render(window);
            Check(chromeFocus.IsFocused && chromeFocus.BorderThickness.Left == 2, "custom chrome keyboard focus has an explicit visible border");
            await File.WriteAllTextAsync(Path.Combine(output, "ui-warnings.json"), JsonSerializer.Serialize(logs.Messages));
            Console.WriteLine("Screens rendered; checking bindings.");
            var bindingsValid = !logs.Messages.Any(m => m.Contains("Binding") || m.Contains("Error"));
            Console.WriteLine("Closing synthetic window.");
            window.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!window.IsVisible, "custom close button closes only the synthetic window"); App.Covers = null;
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = bindingsValid, books = 1200, initialPageBooks = 60, initialRows, initialRealizedRows, harnessElapsedMs = elapsed.ElapsedMilliseconds, checks = _checks, screenshots = Directory.GetFiles(output, "*.png").Length, logs = logs.Messages }));
            return bindingsValid;
        }, timeout.Token).GetAwaiter().GetResult();
        Console.WriteLine($"Desktop Headless {(passed ? "PASS" : "FAIL")} ({_checks} checks); artifacts outside repository."); return passed ? 0 : 1;
    }
    private static int _checks;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static async Task Render(Window window) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); await Task.Delay(100); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    private static void Save(Window window, string output, string name) { using var bitmap = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame"); bitmap.Save(Path.Combine(output, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
    private sealed class Dialogs : IDesktopDialogs
    {
        public Task<string?> ExecutableAsync() => Task.FromResult<string?>(null);
        public Task<string?> FolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> SaveJsonAsync(string name) => Task.FromResult<string?>(null);
    }
    private sealed class QuickService : IQuickPlayService
    {
        public Task<IReadOnlyList<PersonalPlayView>> LoadAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult(book.Plays);
        public Task<PersonalEditDraft?> PendingAsync(QuickPlayBook book, CancellationToken ct) => Task.FromResult<PersonalEditDraft?>(null);
        public Task<PersonalEditDraft> QueueAsync(QuickPlayBook book, PersonalPlayView? play, PersonalPlayData details, CancellationToken ct) => Task.FromResult(new PersonalEditDraft(Guid.NewGuid().ToString("D"), book.Id, "http://test.local/", play?.Id, new(details, book.Item?.Item.Id, play?.Revision), DateTimeOffset.UtcNow));
        public Task<PersonalPlayView> SyncAsync(PersonalEditDraft draft, CancellationToken ct) => throw new InvalidOperationException("Headless appearance inspection must not submit writes.");
        public Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class DemoLibrary : IDesktopLibrary
    {
        public bool Summary { get; set; }
        private IReadOnlyList<PersonalPlayView> _plays = [];
        public bool Block { get; set; }
        public int QueryCalls { get; private set; }
        public int PlayReads { get; private set; }
        public int CatalogCalls { get; private set; }
        public Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery q, bool cacheOnly, CancellationToken ct)
        {
            CatalogCalls++;
            return Task.FromResult(new PublicCatalogPage(Enumerable.Range(1, 24).Select(n => new PublicCatalogEntry(Metadata(n) with { Provenance = "vndb_dump", Completeness = "partial_text" }, new(3, 1, 1, 1, 0, n % 2 == 0), true)).ToArray(), q.Page, q.PageSize, 67459, true, new(67459, new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), "paused", 10, 100), DateTimeOffset.UtcNow));
        }
        public int ProducerCalls;
        public int LocalReads;
        public Task<MetadataResponse> LocalNovelAsync(string id, CancellationToken ct)
        { LocalReads++; return Task.FromResult(new MetadataResponse([Metadata(int.Parse(id.AsSpan(1)))], "web_local", DateTimeOffset.UtcNow, 1, 10, 1, false)); }
        public Task<IReadOnlyList<CatalogProducerChoice>> CatalogProducersAsync(string text, bool cacheOnly, CancellationToken ct)
        { ProducerCalls++; return Task.FromResult<IReadOnlyList<CatalogProducerChoice>>([new("p1", "Synthetic Studio", "合成会社", 24)]); }
        private static MetadataSnapshot Metadata(int n) => new("v" + n, "星の彼方で、もう一度あなたと " + n, "vn",
            new() { Id = "v" + n, Title = "星の彼方で、もう一度あなたと " + n, Released = "2026-10-23", Description = "合成展示资料。\n\nこれはテスト用の架空の作品です。真实个人资料不在测试中。",
                Languages = ["ja"], OriginalLanguage = "ja", Titles = [new() { Lang = "ja", Title = "星の彼方で、もう一度あなたと " + n, Latin = "Synthetic Latin " + n, Main = true }], Relations = [new("v2", "seq", true)],
                ReleaseCatalog = new([new NovelRelease { Id = "r" + n, Title = "合成发行版", Released = "2099-12", Official = false, Patch = true,
                    Languages = [new("zh-Hans", null, null, true, true)], Platforms = ["win"], Vns = [new("v" + n, "partial")] }], DateTimeOffset.UtcNow),
                Image = new() { Id = "cv" + n, Url = "https://t.vndb.org/cv/" + n + ".png" } },
            [new() { Id = "p1", Name = "Demo Studio", Original = "星空ソフト" }, new() { Id = "p2", Name = "第二制作室" }], null, new("not_downloaded"), DateTimeOffset.UtcNow, "test");
        public Task<ShelfResult> ShelfAsync(CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var items = Enumerable.Range(1, 1200).Select(n => new LibraryItemView(new("item-" + n, new("Demo " + n, "v" + n, Notes: "合成备注：在本地"), 1, now, now), Metadata(n), n % 2)).ToArray();
            var plays = Enumerable.Range(1, 1200).Where(n => n % 2 == 1).Select(n => new PersonalPlayView("play-" + n,
                new("Demo " + n, "v" + n, "completed", new(2026, 1, 1), new(2026, 1, 2), 120, 8.5m, "合成感想：温暖的故事。", null, null, "ja"), 1, now, now, ItemId: "item-" + n)).ToArray();
            _plays = plays;
            if (Summary) items = items.Select(i => ShelfSummaries.Item(i) with { State = new(PersonalProgress.FromStatuses(plays.Where(p => p.ItemId == i.Item.Id).Select(p => p.Details.Status)), false, plays.FirstOrDefault(p => p.ItemId == i.Item.Id)?.Details.Status) }).ToArray();
            return Task.FromResult(new ShelfResult(new("http://test.local/", items, Summary ? [] : plays, [], now, SummaryOnly: Summary), "Web 本地资料库 · 合成演示"));
        }
        public async Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct)
        {
            QueryCalls++;
            if (Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new([Metadata(17)], "client_vndb", DateTimeOffset.UtcNow, 1, 30, null, false, PendingImport: true);
        }
        public Task<LibraryItemView?> ItemAsync(string id, CancellationToken ct)
        { var n = int.Parse(id.AsSpan(5)); var now = DateTimeOffset.UtcNow; return Task.FromResult<LibraryItemView?>(new(new(id, new("Demo " + n, "v" + n, Notes: "合成备注：在本地"), 1, now, now), Metadata(n), n % 2)); }
        public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct) { PlayReads++; return Task.FromResult<IReadOnlyList<PersonalPlayView>>(Summary ? _plays.Where(p => p.ItemId == itemId).ToArray() : []); }
    }
    private sealed class Covers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var bitmap = new SKBitmap(240, 340); using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(48, 71, 106)); using var paint = new SKPaint { Color = new(118, 156, 179), IsAntialias = true };
            canvas.DrawCircle(170, 85, 90, paint); paint.Color = new(72, 99, 126); canvas.DrawCircle(50, 270, 150, paint);
            paint.Color = SKColors.White; using var font = new SKFont(SKTypeface.Default, 30); canvas.DrawText("DEMO VN", 35, 175, font, paint);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var content = new ByteArrayContent(data.ToArray()); content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class Logs : ILogSink
    {
        public List<string> Messages { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Messages.Add(area + ": " + template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Messages.Add(area + ": " + template + " " + string.Join(" | ", values));
    }
}
