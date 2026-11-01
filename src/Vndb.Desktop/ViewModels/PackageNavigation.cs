using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    [RelayCommand] private Task RegisterPackageBookAsync(BookViewModel? book) => GuardAsync(async ct =>
    {
        if (book is null) throw new ArgumentException("请先选择作品。");
        await RequireBookDetailsAsync(book, ct);
        Packages.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        Section = 4;
        Status = "作品已选定。请在目录整理页的安装包登记中选择／核对文件，预览后确认。";
    });
    private void UpdateOwnership(BookViewModel book)
    {
        // A metadata-only result has no exact personal ItemId. Never borrow variant ownership.
        var records = book.Item is { } item ? _packages.Where(p => p.ItemId == item.Item.Id).ToArray() : [];
        book.SetOwnership(book.Item is not null && _installations.Any(i => i.Installation.RemovedAt is null && i.Installation.Status == "present" && MatchesBook(i, book)),
            records.Where(p => p.Status == "present").Select(p => p.RelativePath).ToArray(),
            records.Select(p => p.RelativePath + " · " + (p.Status == "present" ? "存在" : p.Status == "missing" ? "缺失" : "未知／待核对")).ToArray());
    }
    private async Task PackagesSavedAsync(CancellationToken ct)
    {
        var services = Services(); var local = await services.LocalLibrary.ReadAsync(ct); services.Library.Seed(local);
        _packages = local.Packages ?? [];
        _books = _books.Concat(local.Items.Where(i => i.Item.DeletedAt is null && LocalInstallationRegistration.IsPending(i.Item.Id) && _books.All(b => b.Id != i.Item.Id)).Select(i => new BookViewModel(i, i.Metadata))).ToArray();
        foreach (var book in _books.Concat(_queryBooks)) UpdateOwnership(book);
        Shelf.SetLibrary(_books); UpdateBookDetails(); services.ActivityHost?.SignalSync();
    }
}
