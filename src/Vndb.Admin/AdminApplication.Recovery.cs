using System.CommandLine;
using Vndb.Library;

namespace Vndb.Admin;

public sealed partial class AdminApplication
{
    private void AddRecovery(Command library)
    {
        var snapshot = new Command("snapshot", "完整一致备份：SQLite、已登记封面、实例身份与恢复清单；密钥独立保管。");
        var destination = new Argument<string>("destination");
        var image = new Option<string>("--image-id") { Required = true };
        var owner = new Option<string?>("--owner");
        var key = new Option<string?>("--key-file");
        var inputs = new Option<bool>("--include-dataset-inputs") { Description = "包含未完成公共导入引用的已校验原包／暂存；未登记临时文件不打包。" };
        snapshot.Add(destination); snapshot.Add(image); snapshot.Add(owner); snapshot.Add(key); snapshot.Add(inputs);
        Bind(snapshot, (_, parsed, token) =>
        {
            LocalOnly(parsed);
            return Task.FromResult<object?>(LibrarySnapshot.Create(parsed.GetValue(_dataDirectory)!, parsed.GetValue(destination)!,
                parsed.GetValue(image)!, parsed.GetValue(owner), parsed.GetValue(key), token, parsed.GetValue(inputs)));
        });
        library.Add(snapshot);
        var restore = new Command("restore", "校验并恢复到全新隔离目录；不切换服务，不覆盖旧目录。");
        var source = new Argument<string>("snapshot");
        var target = new Argument<string>("destination");
        var restoreKey = new Option<string?>("--key-file");
        restore.Add(source); restore.Add(target); restore.Add(restoreKey);
        Bind(restore, (_, parsed, token) =>
        {
            LocalOnly(parsed);
            if (!parsed.GetValue(_offlineMaintenance)) throw new ArgumentException("恢复需要 --offline-maintenance，并以原数据库UID:GID运行。");
            return Task.FromResult<object?>(LibrarySnapshot.Restore(parsed.GetValue(source)!, parsed.GetValue(target)!, parsed.GetValue(restoreKey), token));
        });
        library.Add(restore);
    }
    private void LocalOnly(ParseResult parsed)
    {
        if (parsed.GetValue(_server) is not null || parsed.GetValue(_storage) != "sqlite")
            throw new ArgumentException("完整备份／恢复仅支持容器内或本机SQLite受控维护。");
    }
}
