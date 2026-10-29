using System.Globalization;
using System.Text;

namespace Vndb.Local;

/// <summary>Filesystem aliases only; never changes VNDB titles or establishes a title match.</summary>
public static class GameDirectoryNaming
{
    private static bool Text(Rune rune) => Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    private static bool Reserved(string name) => name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
        name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';

    public static string Name(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        // Remove marks whose compatibility decomposition would otherwise add brand words.
        var normalized = value.Replace("™", "").Replace("®", "").Replace("©", "").Replace("℠", "").Normalize(NormalizationForm.FormKC);
        var output = new StringBuilder();
        void Space() { if (output.Length > 0 && output[^1] != ' ') output.Append(' '); }
        var runes = normalized.EnumerateRunes().ToArray();
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i]; var category = Rune.GetUnicodeCategory(rune);
            if (Text(rune) || rune.Value is '-' or '_') output.Append(rune.ToString());
            else if (rune.Value == '+')
            {
                Space();
                // '+PLUS' is a common fandisc label. Keep the existing word once.
                if (!HasPlusWord(runes, i + 1)) { output.Append("PLUS"); Space(); }
            }
            else if (rune.Value is '&' or '×') { Space(); output.Append(rune.Value == '&' ? "and" : "x"); Space(); }
            else if (rune.Value is '~' or '〜' || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) Space();
            else if (category == UnicodeCategory.DashPunctuation) output.Append('-');
            else if (rune.Value is '†' or '‡' || category is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) { }
            else if (category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation) Space();
            // Other symbols (hearts, stars, music marks, emoji, etc.) are omitted.
        }
        var name = output.ToString().Trim();
        if (name.Length is 0 or > 120 || !name.EnumerateRunes().Any(Text)) throw new ArgumentException("目录名清理后为空、缺少文字／数字或超过120字符，需要明确简化。");
        if (Reserved(name)) name = "_" + name;
        return name;
    }

    private static bool HasPlusWord(Rune[] runes, int offset)
    {
        while (offset < runes.Length && Rune.IsWhiteSpace(runes[offset])) offset++;
        const string word = "PLUS";
        if (offset + word.Length > runes.Length) return false;
        for (var i = 0; i < word.Length; i++) if (Rune.ToUpperInvariant(runes[offset + i]).Value != word[i]) return false;
        return offset + word.Length == runes.Length || !Rune.IsLetterOrDigit(runes[offset + word.Length]);
    }

    public static bool IsCompatibleName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && value == value.Trim() &&
        !Reserved(value.Normalize(NormalizationForm.FormKC)) && value.EnumerateRunes().Any(Text) && value.EnumerateRunes().All(r => Text(r) || r.Value is ' ' or '-' or '_');

    public static bool IsCompatibleRoot(string path)
    {
        // The explicit target anchor may include normal system folders such as Program Files (x86).
        var root = Path.GetPathRoot(path);
        return root is not null && path[root.Length..].EnumerateRunes().All(r => Text(r) || r.Value is ' ' or '-' or '_' or '.' or '(' or ')' or '\\' or '/');
    }
}
