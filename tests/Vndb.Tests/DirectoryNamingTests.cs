using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class DirectoryNamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("らぶ♥らぶ♥らいふ ～お嬢様７人～", "らぶらぶらいふ お嬢様7人")]
    [InlineData("ゲーム★☆†♪😀\u200D\uFE0F", "ゲーム")]
    [InlineData("会社：テスト／ブランド", "会社 テスト ブランド")]
    [InlineData("ゲーム「完全版」!?", "ゲーム 完全版")]
    [InlineData("猫忍えくすはーと ＋PLUS 彩羽編", "猫忍えくすはーと PLUS 彩羽編")]
    [InlineData("ゲーム＋", "ゲーム PLUS")]
    [InlineData("A×B & C", "A x B and C")]
    [InlineData("ＮＥＸＴＯＮ＿Ａ－Ｂ", "NEXTON_A-B")]
    [InlineData("CON", "_CON")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("ゲーム™®©", "ゲーム")]
    [InlineData("か\u3099", "が")]
    public void FilesystemAliasKeepsTextAndEditionMeaningWithoutSymbols(string original, string expected)
    {
        var name = OrganizationPlanner.Name(original);
        Assert.Equal(expected, name); Assert.True(GameDirectoryNaming.IsCompatibleName(name)); Assert.Equal(name, OrganizationPlanner.Name(name));
    }
    [Theory]
    [InlineData("♥★†")]
    [InlineData("\u200D\uFE0F")]
    [InlineData("   ")]
    public void UnusableNamesNeedManualTextInsteadOfAnInventedSharedFolder(string value) => Assert.Throws<ArgumentException>(() => OrganizationPlanner.Name(value));

    private static (LocalInstallationView View, LibraryItemView Book) Entry(string root, string title, string id, string device)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new LibraryItem("item-" + id, new(title, "v17"), 1, now, now);
        var metadata = new MetadataSnapshot("v17", title, "vn", new() { Id = "v17", Title = title, Titles = [new() { Lang = "ja", Title = title, Main = true }] },
            [new() { Id = "p1", Name = "Studio" }], null, new("none"), now, "test");
        return (new(new("install-" + id, new(device, root, title), "present", now, 1, now, now), [new("component-" + id, "install-" + id, new(item.Id, "."), 1, now, now)]), new(item, metadata, 0));
    }
    [Theory]
    [InlineData("ゲーム♥", "ゲーム♥", "proposed_only")]
    [InlineData("ゲーム", "ゲーム♥", "already_organized")]
    [InlineData("ゲーム PLUS", "ゲーム＋", "already_organized")]
    [InlineData("ゲーム Special Edition", "ゲーム♥", "preserve_variant")]
    [InlineData("ゲーム♥ Special Edition", "ゲーム♥", "preserve_variant")]
    public void AuditUsesTheSameAliasWithoutReintroducingSymbolsOrDroppingEditionLabels(string folder, string title, string state)
    {
        using var dir = new TestDirectory(); var target = Directory.CreateDirectory(Path.Combine(dir.Path, "library")).FullName;
        var game = Directory.CreateDirectory(Path.Combine(target, "Studio", folder)).FullName; File.WriteAllText(Path.Combine(game, "story.dat"), "unchanged");
        var device = Guid.NewGuid().ToString("D"); var entry = Entry(game, title, "one", device);
        var row = Assert.Single(InstallationAuditor.Check("local", device, dir.Path, target, [entry.View], ct: Ct, items: [entry.Book]).Items);
        Assert.Equal(state, row.Suggestion!.State);
        if (state == "proposed_only") Assert.Equal(Path.Combine(target, "Studio", OrganizationPlanner.Name(title)), row.Suggestion.DestinationPath);
        var scan = DirectoryScanner.Scan(game, known: [entry.View], ct: Ct); var candidate = Assert.Single(scan.Candidates);
        Assert.Equal(game, candidate.Path); Assert.Equal(!GameDirectoryNaming.IsCompatibleName(folder), candidate.NameNeedsReview);
        Assert.Equal(title, entry.Book.Metadata!.Novel!.Titles[0].Title); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(game, "story.dat")));
    }
    [Fact]
    public void SanitizationCollisionsNeverMergeGamesAndExistingCompanyNamesNeedReview()
    {
        using var dir = new TestDirectory(); var target = Directory.CreateDirectory(Path.Combine(dir.Path, "library")).FullName; var device = Guid.NewGuid().ToString("D");
        var first = Entry(Directory.CreateDirectory(Path.Combine(target, "Studio", "ゲーム♥")).FullName, "ゲーム♥", "one", device);
        var second = Entry(Directory.CreateDirectory(Path.Combine(target, "Studio", "ゲーム☆")).FullName, "ゲーム☆", "two", device);
        var report = InstallationAuditor.Check("local", device, dir.Path, target, [first.View, second.View], ct: Ct, items: [first.Book, second.Book]);
        Assert.All(report.Items, r => Assert.Equal("target_conflict", r.Suggestion!.State));
        Assert.True(Directory.Exists(first.View.Installation.Details.RootPath)); Assert.True(Directory.Exists(second.View.Installation.Details.RootPath));
        var other = Entry(Directory.CreateDirectory(Path.Combine(target, "Studio♥", "ゲーム")).FullName, "ゲーム", "three", device);
        Assert.Equal("manual_company_name", InstallationAuditor.Check("local", device, dir.Path, target, [other.View], ct: Ct, items: [other.Book]).Items[0].Suggestion!.State);
        Assert.NotEqual(OrganizationPlanner.Name("ゲーム"), OrganizationPlanner.Name("ゲーム＋"));
    }
    [Fact]
    public void CompatibleCompanyFoldersAndTheExplicitSystemAnchorArePreserved()
    {
        using var dir = new TestDirectory(); var target = Directory.CreateDirectory(Path.Combine(dir.Path, "Program Files (x86)")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(target, "ＳＴＵＤＩＯ", "ゲーム♥")).FullName;
        var device = Guid.NewGuid().ToString("D"); var entry = Entry(source, "ゲーム♥", "one", device);
        var plan = OrganizationPlanner.Create(entry.View, new(dir.Path, target), "STUDIO", "ゲーム♥", Ct);
        Assert.Equal(Path.Combine(target, "ＳＴＵＤＩＯ", "ゲーム"), plan.DestinationPath);
        Assert.True(GameDirectoryNaming.IsCompatibleRoot(target));
        var suggestion = InstallationAuditor.Check("local", device, dir.Path, target, [entry.View], ct: Ct, items: [entry.Book]).Items[0].Suggestion!;
        Assert.Equal(plan.DestinationPath, suggestion.DestinationPath);
    }
    [Fact]
    public async Task NewApprovedPlansWithUnsafeDestinationsStopBeforeMovingOrCreatingAnIntent()
    {
        using var dir = new TestDirectory(); var source = Directory.CreateDirectory(Path.Combine(dir.Path, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "target")).FullName; File.WriteAllText(Path.Combine(source, "story.dat"), "keep");
        var device = Guid.NewGuid().ToString("D"); var entry = Entry(source, "ゲーム♥", "one", device);
        var plan = OrganizationPlanner.Create(entry.View, new(dir.Path, target), "Studio", "ゲーム♥", Ct);
        Assert.Equal("ゲーム♥", plan.Title); Assert.Equal(Path.Combine(target, "Studio", "ゲーム"), plan.DestinationPath);
        var bad = plan with { DestinationPath = Path.Combine(target, "Studio", "ゲーム♥") }; var data = new LocalLibraryStore(Path.Combine(dir.Path, "client"));
        await data.SaveAsync(new([entry.View], [entry.Book]), Ct);
        var runner = new OrganizationRunner(new LocalOrganizationServer(data, new(data.DirectoryPath), device), "local:" + device, device);
        var run = Path.Combine(dir.Path, "run");
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.ExecuteAsync(bad, LocalArchiveFormat.Hash(bad), run, Ct));
        Assert.False(File.Exists(Path.Combine(run, "intent.json"))); Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "story.dat")));
        var unsafeRoot = Directory.CreateDirectory(Path.Combine(dir.Path, "target♥")).FullName;
        Assert.Throws<ArgumentException>(() => OrganizationPlanner.Create(entry.View, new(dir.Path, unsafeRoot), "Studio", "ゲーム", Ct));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyUnsafePlansStopIfUnmovedButMovedGamesCanFinalizeAndRestoreOriginalPaths(bool alreadyMoved)
    {
        using var dir = new TestDirectory(); var source = Directory.CreateDirectory(Path.Combine(dir.Path, "source", "ゲーム♥ 原位置")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(dir.Path, "target")).FullName; File.WriteAllText(Path.Combine(source, "story.dat"), "keep");
        var device = Guid.NewGuid().ToString("D"); var entry = Entry(source, "ゲーム☆", "one", device);
        var plan = OrganizationPlanner.Create(entry.View, new(dir.Path, target), "Studio", "ゲーム☆", Ct) with { DestinationPath = Path.Combine(target, "Studio", "ゲーム☆") };
        var data = new LocalLibraryStore(Path.Combine(dir.Path, "client")); await data.SaveAsync(new([entry.View], [entry.Book]), Ct);
        var server = new LocalOrganizationServer(data, new(data.DirectoryPath), device); var runner = new OrganizationRunner(server, "local:" + device, device);
        var now = DateTimeOffset.UtcNow; var receipt = new ArchiveReceipt(plan.Id, LocalArchiveFormat.Hash(plan), device, entry.View.Installation.Id, "planned", 1, 1, now, now);
        var state = Directory.CreateDirectory(Path.Combine(data.DirectoryPath, "organization-state")).FullName;
        RunJournal.Write(Path.Combine(state, plan.Id + ".json"), new LocalArchiveRunView(plan, receipt));
        var run = Directory.CreateDirectory(Path.Combine(dir.Path, "runs", plan.Id)).FullName;
        var intent = new LocalArchiveIntent(plan, receipt, "local-" + device, "local:" + device);
        using (var journal = new RunJournal(run))
        {
            RunJournal.Write(Path.Combine(run, "intent.json"), intent); journal.BindValue("approved", intent);
            if (alreadyMoved) { journal.Append("archive_rename_intent", plan.Before.Sha256); Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath)!); Directory.Move(source, plan.DestinationPath); }
        }
        var originalIntent = File.ReadAllBytes(Path.Combine(run, "intent.json"));
        if (!alreadyMoved)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.ResumeAsync(run, Ct)); Assert.True(Directory.Exists(source));
            Assert.Equal("cancelled", (await runner.CancelAsync(run, LocalArchiveFormat.Hash(plan), Ct)).State);
        }
        else
        {
            Assert.Equal("archived", (await runner.ResumeAsync(run, Ct)).State);
            var restore = await runner.PreviewRestoreAsync(run, Ct);
            Assert.Equal("restored", (await runner.RestoreAsync(restore, LocalArchiveFormat.Hash(restore), run, Ct)).State);
        }
        Assert.Equal(originalIntent, File.ReadAllBytes(Path.Combine(run, "intent.json")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "story.dat")));
    }
}
