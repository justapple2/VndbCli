using System.Text;

namespace Vndb.Local;

/// <summary>Windows argument quoting only; never interprets shell operators or environment variables.</summary>
public static class LaunchArguments
{
    public static string Format(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(LocaleEmulator.QuoteArgument));
    public static IReadOnlyList<string> Parse(string text)
    {
        if (text.Length > 32768 || text.Contains('\0')) throw new ArgumentException("启动参数过长或包含空字符。");
        var args = new List<string>(); var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at == text.Length) break;
            var value = new StringBuilder(); var quoted = false;
            while (at < text.Length && (quoted || !char.IsWhiteSpace(text[at])))
            {
                var slashes = 0;
                while (at < text.Length && text[at] == '\\') { slashes++; at++; }
                if (at < text.Length && text[at] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) value.Append('"');
                    else if (quoted && at + 1 < text.Length && text[at + 1] == '"') { value.Append('"'); at++; }
                    else quoted = !quoted;
                    at++;
                }
                else { value.Append('\\', slashes); if (at < text.Length && (quoted || !char.IsWhiteSpace(text[at]))) value.Append(text[at++]); }
            }
            if (quoted) throw new ArgumentException("启动参数引号未闭合；带空格的参数请使用成对双引号。");
            args.Add(value.ToString());
            if (args.Count > 100) throw new ArgumentException("启动参数超过100项。");
        }
        return args;
    }
}
