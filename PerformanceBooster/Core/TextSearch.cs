using System.Globalization;
using System.Text;

namespace PerformanceBooster.Core;

// The search boxes: every word of the query has to appear in one of the texts, in any order. Case and accents do not
// count and the Turkish dotted and dotless i are both i, so "msi" finds "MSI" in either language and a Turkish word is
// found when typed without its accents.
public static class TextSearch
{
    public static string[] Words(string query) => Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(string[] words, params string?[] texts)
    {
        if (words.Length == 0) return true;
        var all = Fold(string.Join("\n", texts.Where(t => !string.IsNullOrEmpty(t))));
        return words.All(w => all.Contains(w, StringComparison.Ordinal));
    }

    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c is '\u0131' or 'I' ? 'i' : char.ToLowerInvariant(c)); // dotless i; the dotted capital lost its dot above
        }
        return sb.ToString();
    }
}
