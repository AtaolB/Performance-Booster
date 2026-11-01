using System.Globalization;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Launcher;

// The launcher's few texts: English in the code, Turkish from Texts.tr.txt when Windows is in Turkish (the app itself is not
// running yet, so its language files cannot be used).
static partial class Texts
{
    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    const int LangTurkish = 0x1F;

    static readonly Dictionary<string, string> Translations = Load();

    static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((GetUserDefaultUILanguage() & 0x3FF) != LangTurkish) return map;
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream("Texts.tr.txt");
        if (stream is null) return map;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (line.StartsWith('#') || tab <= 0) continue;
            map[Unescape(line[..tab])] = Unescape(line[(tab + 1)..]);
        }
        return map;
    }

    static string Unescape(string text) => text.Replace("\\n", "\n", StringComparison.Ordinal);

    public static string T(string text) => Translations.TryGetValue(text, out var translated) ? translated : text;

    public static string F(string text, params ReadOnlySpan<object?> args) => string.Format(CultureInfo.InvariantCulture, T(text), args);
}
