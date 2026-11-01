using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PerformanceBooster.Tests;

// The English text in the code is the key; every other language is a file in PerformanceBooster/Languages.
// Each file must translate every text, have no leftovers and keep the placeholders; the code itself has no Turkish.
static partial class LocalizationTests
{
    sealed record Key(string Text, string Kind, string Where);

    [GeneratedRegex(@"\b(T|F|Cat)\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CsCall();

    [GeneratedRegex(@"\{ui:T\s+'((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlCall();

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\b(Text|Content|ToolTip|Header|Title|Description)\s*=\s*""([^""]*)""")]
    private static partial Regex XamlText();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    [GeneratedRegex("[A-Za-z]{2,}")]
    private static partial Regex Word();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escaped();

    [GeneratedRegex(@"\bTexts\.[TF]\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex LauncherCall();

    // Technical terms written the same in every language.
    static readonly HashSet<string> SameInAllLanguages = new(StringComparer.Ordinal) { "Performance", "Booster", "RAM", "CPU", "GPU", "VRAM", "Jitter", "DNS", "Mbps", "MB", "GB", "ms", "Hz", "DPC", "ISR" };

    static string App => Path.Combine(Runner.Root, "PerformanceBooster");
    static string Tests => Path.Combine(Runner.Root, "PerformanceBooster.Tests");
    static string Launcher => Path.Combine(Runner.Root, "PerformanceBooster.Launcher");

    static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\"));

    static string Rel(string file) => Path.GetRelativePath(Runner.Root, file);

    static List<Key> Extract()
    {
        var keys = new List<Key>();
        foreach (var file in Files(App, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in CsCall().Matches(lines[i]))
                    keys.Add(new(UnescapeCs(m.Groups[2].Value), m.Groups[1].Value, $"{Rel(file)}:{i + 1}"));
        }
        foreach (var file in Files(App, "*.xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in XamlCall().Matches(lines[i]))
                    keys.Add(new(Escaped().Replace(WebUtility.HtmlDecode(m.Groups[1].Value), "$1"), "Xaml", $"{Rel(file)}:{i + 1}"));
        }
        return keys;
    }

    static string UnescapeCs(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', _ => c });
        }
        return sb.ToString();
    }

    static string[] Tokens(string s) => Placeholder().Matches(s).Cast<Match>().Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    static string[] Halves(string text) => text.Contains('|') ? text.Split('|') : [text];

    public static void Run()
    {
        // The catalog is built here for the first time, in English.
        Runner.Test("Settings catalog builds in English", () =>
        {
            var bad = new List<string>();
            foreach (var t in Catalog.All)
            {
                void Need(string? s, string what)
                {
                    if (s is not null && SystemTests.Turkish().IsMatch(s)) bad.Add($"{t.Id}.{what}: {s}");
                }
                if (!t.Id.StartsWith("msi.", StringComparison.Ordinal)) Need(t.Title, "Title"); // MSI rows are named after the device, in the Windows language
                Need(t.Description, "Description");
                Need(t.Category, "Category");
                Need(t.Note, "Note");
                if (t.Id != "power.plan") // plan names come from Windows
                    foreach (var o in t.Options()) Need(o.Label, "Option " + o.Key);
            }
            Check.Empty(bad, "Turkish catalog text in English");
        });

        var keys = Extract();
        var distinct = keys.GroupBy(k => k.Text).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var languages = L.All.Where(l => l.Code != "en").ToList();
        Runner.Note($"{distinct.Count} texts; languages: " + string.Join(", ", L.All.Select(l => $"{l.Code} ({l.Strings.Count})")));

        Runner.Test("Language files: English first, name, culture and switch text", () =>
        {
            Check.Equal("en", L.All[0].Code, "English first");
            Check.True(File.Exists(Path.Combine(App, "Languages", "en.json")), "en.json");
            Check.True(languages.Any(l => l.Code == "tr"), "Turkish file embedded");
            foreach (var l in L.All)
            {
                Check.True(l.Name.Length > 0 && l.SwitchTo.Length > 0, l.Code + ": name and switch text");
                Check.Equal(l.Culture, CultureInfo.GetCultureInfo(l.Culture).Name, l.Code + ": culture");
            }
        });

        foreach (var language in languages)
        {
            var dict = language.Strings;
            Runner.Test($"Every text in the source has a translation ({language.Code})", () =>
                Check.Empty(distinct.Where(k => !dict.ContainsKey(k.Key)).Select(k => $"«{k.Key}» ({k.Value[0].Where})"), "untranslated"));

            Runner.Test($"No unused translations ({language.Code})", () =>
                Check.Empty(dict.Keys.Where(k => !distinct.ContainsKey(k)).Select(k => $"«{k}»"), "not in the code"));

            Runner.Test($"Placeholders match ({language.Code})", () =>
                Check.Empty(dict.SelectMany(kv => Halves(kv.Value)
                        .Where(h => !Halves(kv.Key).All(k => Tokens(k).SequenceEqual(Tokens(h))))
                        .Select(h => $"«{kv.Key}» → «{h}»")), "mismatched"));
        }

        Runner.Test("Plural forms are used only with F", () =>
            Check.Empty(distinct.Where(kv => kv.Key.Contains('|') && kv.Value.Any(u => u.Kind != "F"))
                .Select(kv => $"«{kv.Key}» ({kv.Value.First(u => u.Kind != "F").Where})"), "plural with T/XAML"));

        Runner.Test("Every format text formats without errors", () =>
        {
            var bad = new List<string>();
            foreach (var (text, uses) in distinct)
            {
                if (!uses.Any(u => u.Kind == "F")) continue;
                var all = new List<string>(Halves(text));
                foreach (var l in languages)
                    if (l.Strings.TryGetValue(text, out var translated)) all.AddRange(Halves(translated));
                int count = Tokens(text).Select(t => int.Parse(Number().Match(t).Value)).DefaultIfEmpty(-1).Max() + 1;
                var args = Enumerable.Range(0, count).Select(i => (object)(i + 2)).ToArray();
                foreach (var f in all)
                {
                    try { _ = string.Format(CultureInfo.InvariantCulture, f, args); }
                    catch (FormatException) { bad.Add($"«{f}»"); }
                }
            }
            Check.Empty(bad, "does not format");
        });

        // The launcher runs before the app (and .NET) is there, so it has its own few texts: Turkish in Texts.tr.txt.
        Runner.Test("Launcher: every text has a Turkish translation, none unused, placeholders match", () =>
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Files(Launcher, "*.cs"))
                foreach (Match m in LauncherCall().Matches(File.ReadAllText(file)))
                    used.Add(UnescapeCs(m.Groups[1].Value));
            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(Path.Combine(Launcher, "Texts.tr.txt")))
            {
                int tab = line.IndexOf('\t');
                if (line.StartsWith('#') || tab <= 0) continue;
                translations[line[..tab].Replace("\\n", "\n", StringComparison.Ordinal)] = line[(tab + 1)..].Replace("\\n", "\n", StringComparison.Ordinal);
            }
            Check.True(used.Count >= 8, "texts found in the launcher's code: " + used.Count);
            Check.Empty(used.Where(t => !translations.ContainsKey(t)).ToList(), "missing in Texts.tr.txt");
            Check.Empty(translations.Keys.Where(t => !used.Contains(t)).ToList(), "unused in Texts.tr.txt");
            Check.Empty(translations.Where(p => !Tokens(p.Key).SequenceEqual(Tokens(p.Value))).Select(p => p.Key).ToList(), "placeholders differ");
        });

        Runner.Test("No Turkish text in the code (app, launcher, tests, XAML); it lives in the language files only", () =>
        {
            var bad = new List<string>();
            foreach (var folder in new[] { App, Launcher, Tests })
                foreach (var file in Files(folder, "*.*").Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".manifest", StringComparison.Ordinal)))
                {
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                        if (SystemTests.Turkish().IsMatch(lines[i])) bad.Add($"{Rel(file)}:{i + 1} {lines[i].Trim()}");
                }
            Check.Empty(bad, "Turkish text");
        });

        Runner.Test("XAML has no fixed (untranslatable) text", () =>
        {
            var bad = new List<string>();
            foreach (var file in Files(App, "*.xaml"))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match m in XamlText().Matches(lines[i]))
                    {
                        var value = WebUtility.HtmlDecode(m.Groups[2].Value);
                        if (value.StartsWith('{')) continue;
                        var words = Word().Matches(value).Cast<Match>().Select(w => w.Value);
                        if (words.Any(w => !SameInAllLanguages.Contains(w))) bad.Add($"{Rel(file)}:{i + 1} {m.Groups[1].Value}=«{value}»");
                    }
            }
            Check.Empty(bad, "fixed text");
        });

        Runner.Test("Plural, percent and number formats (en/tr)", () =>
        {
            const string settings = "{0} setting|{0} settings";
            var tr = L.All.Single(l => l.Code == "tr");
            try
            {
                L.Init("en");
                Check.Equal("en", L.Code, "language code");
                Check.Equal("1 setting", L.F(settings, 1), "singular");
                Check.Equal("3 settings", L.F(settings, 3), "plural");
                Check.Equal("Performance Booster · 1 update", L.F("{0} · {1} update|{0} · {1} updates", AppPaths.Name, 1), "chosen by the last number");
                Check.Equal("2.5 GB freed (1 file", L.F("{0} freed ({1} file|{0} freed ({1} files", "2.5 GB", 1), "text + number");
                Check.Equal("16 GB DDR5 · 6000 MT/s · 1 module", L.F("{0} GB {1} · {2} MT/s · {3} module|{0} GB {1} · {2} MT/s · {3} modules", "16", "DDR5", 6000u, 1), "last integer");
                Check.Equal("45%", L.Pct(45), "percent");
                Check.Equal("Downloading · 7 MB / 10 MB", L.F("Downloading · {0}", "7 MB / 10 MB"), "download size");
                Check.Equal("1.5 GB", Maintenance.Size(1610612736), "size");
                Check.Equal("code 0x8A15008E", L.F("code 0x{0:X8}", unchecked((int)0x8A15008E)), "hex code");
                Check.Equal("tr", L.Next.Code, "the language button switches to Turkish");

                L.Init("tr");
                Check.Equal("tr", L.Code, "language code");
                Check.Equal(string.Format(L.Culture, tr.Strings[settings], 3), L.F(settings, 3), "Turkish has no plural split");
                Check.True(!tr.Strings[settings].Contains('|'), "Turkish text without a plural form");
                Check.Equal("%45", L.Pct(45), "percent");
                Check.Equal("1,5 GB", Maintenance.Size(1610612736), "size");
                Check.Equal(tr.Strings["Power"], L.T("Power"), "translated");
                Check.Equal("A text with no translation", L.T("A text with no translation"), "English fallback");
                Check.Equal("en", L.Next.Code, "the language button switches to English");

                L.Init("de");
                Check.Equal("en", L.Code, "a language without a file falls back to English");
            }
            finally
            {
                L.Init("en");
            }
        });

        Runner.Test("Windows names in every language (power plan, display adapter, driver)", () =>
        {
            var tr = L.All.Single(l => l.Code == "tr");
            var high = tr.WindowsNames["High performance"][0];
            Check.True(L.WindowsNames("High performance").SequenceEqual(["High performance", high]), "English and Turkish names");
            Check.True(L.HasWindowsName(high + " (2)", "High performance") && L.HasWindowsName("HIGH PERFORMANCE", "High performance"), "matched in any language, case insensitive");
            Check.True(!L.HasWindowsName("Balanced", "High performance"), "another plan does not match");
            var driver = tr.WindowsNames["Driver"][0];
            Check.True(new InstalledProgram("HKLM|x", $"NVIDIA {driver} 581.57", "", "1", null, 0, null, null, "x", null, ProgramSource.Desktop, false).IsSystemLike, "a driver named in Turkish is a system component");
        });
    }
}
