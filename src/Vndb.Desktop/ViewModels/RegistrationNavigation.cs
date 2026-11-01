using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    private async Task RequireBookDetailsAsync(BookViewModel book, CancellationToken ct)
    {
        if (book.DetailsPending && book.Item is not null)
            book.ApplyDetails(await _library.ItemAsync(book.Id, ct) ?? throw new InvalidDataException("完整条目尚未加载，请读取详情后重试登记。"));
    }
    [RelayCommand] private Task RegisterBookAsync(BookViewModel? book) => GuardAsync(async ct =>
    {
        if (book is null) throw new ArgumentException("请先选择作品。");
        await RequireBookDetailsAsync(book, ct);
        SelectedBook = book;
        Registration.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        Section = 7;
    });
    [RelayCommand] private Task RegisterCandidateAsync() => GuardAsync(async ct =>
    {
        if (Organization.SelectedCandidate is not { } candidate) throw new ArgumentException("请先选择扫描候选。");
        var book = SelectedBook; if (book is not null) await RequireBookDetailsAsync(book, ct);
        Registration.RootPath = candidate.Path;
        if (book is not null) Registration.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        if (_installations.SingleOrDefault(i => i.Installation.Id == candidate.InstallationId) is { } known)
        { Registration.ContainerTitle = known.Installation.Details.Title; Registration.Company = known.Installation.Details.CompanyGroup ?? ""; }
        Section = 7;
    });
    private async Task RegistrationSavedAsync(RegistrationResult result, CancellationToken ct)
    {
        var services = Services(); var local = await services.LocalLibrary.ReadAsync(ct); services.Library.Seed(local);
        _installations = local.Installations;
        _packages = local.Packages ?? [];
        var books = _books.ToList();
        foreach (var item in result.Items)
            if (books.All(b => b.Id != item.Item.Id)) books.Add(new(item, item.Metadata));
        _books = books.ToArray();
        foreach (var book in _books.Concat(_queryBooks)) UpdateOwnership(book);
        Shelf.SetLibrary(_books);
        SelectedBook = _books.FirstOrDefault(b => result.Items.Any(i => i.Item.Id == b.Id));
        Installations.Clear(); foreach (var installation in _installations.Where(i => SelectedBook is null || MatchesBook(i, SelectedBook))) Installations.Add(installation);
        SelectedInstallation = Installations.Single(i => i.Installation.Id == result.Installation.Installation.Id);
        services.ActivityHost?.SignalSync();
    }
}
