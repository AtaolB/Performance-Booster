using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace PerformanceBooster.Core;

// UI language. The English text in the code is the key; every language is a file in the Languages folder (en.json, tr.json, ...)
// embedded in the exe, so adding a language means adding a file there. English needs no translations; it is the default
// and the fallback for any text a language does not have.
// Changing the language restarts the app, so all texts (catalog, hints) are built in the chosen language at once.
public static class L
{
    public sealed class Language
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Culture { get; set; } = "";
        // Shown on the language button, written in this language ("Switch to English").
        public string SwitchTo { get; set; } = "";
        // Names Windows and drivers use in this language for things the app matches by name (power plans, display adapter).
        public Dictionary<string, string[]> WindowsNames { get; set; } = [];
        public Dictionary<string, string> Strings { get; set; } = [];
    }

    const string ResourcePrefix = "Languages.";
    static readonly Language Builtin = new() { Code = "en", Name = "English", Culture = "en-US", SwitchTo = "Switch to English" };
    static readonly Lazy<IReadOnlyList<Language>> _all = new(Load);

    // English first, then the others by code.
    public static IReadOnlyList<Language> All => _all.Value;
    public static Language Current { get; private set; } = Builtin;
    public static string Code => Current.Code;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    // The language the language button switches to.
    public static Language Next => All[(IndexOf(Current) + 1) % All.Count];

    static int IndexOf(Language language)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i] == language) return i;
        return 0;
    }

    static List<Language> Load()
    {
        var assembly = typeof(L).Assembly;
        var list = new List<Language>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var language = JsonSerializer.Deserialize(stream, AppJson.Default.Language) ?? throw new InvalidDataException(name);
            language.Code = name[ResourcePrefix.Length..^".json".Length];
            list.Add(language);
        }
        if (!list.Any(l => l.Code == "en")) list.Add(Builtin);
        return list.OrderBy(l => l.Code != "en").ThenBy(l => l.Code, StringComparer.Ordinal).ToList();
    }

    // "auto": the Windows display language if there is a file for it, otherwise English.
    public static void Init(string? language)
    {
        var code = language is null or "auto" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : language;
        Current = All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        _strings = Current.Strings.ToFrozenDictionary(StringComparer.Ordinal);
        Culture = CultureInfo.GetCultureInfo(Current.Culture);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    // Number/date formats of WPF bindings follow the chosen language too. Called once before the first window.
    public static void ApplyToWpf() =>
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Culture.IetfLanguageTag)));

    public static string T(string text) => _strings.TryGetValue(text, out var translated) ? translated : text;

    static FrozenDictionary<string, string> _strings = FrozenDictionary<string, string>.Empty;
    static readonly ConcurrentDictionary<string, (CompositeFormat One, CompositeFormat? Many)> Formats = new(StringComparer.Ordinal);

    // Plurals: if the text is "singular|plural", the form is chosen by the last integer argument.
    // A language that keeps the noun singular after a number (Turkish) has no such split in its translation.
    // Each text is parsed once; the arguments are passed without an array.
    public static string F(string text, params ReadOnlySpan<object?> args)
    {
        var (one, many) = Formats.GetOrAdd(T(text), static format =>
        {
            int bar = format.IndexOf('|');
            return bar < 0 ? (CompositeFormat.Parse(format), null) : (CompositeFormat.Parse(format[..bar]), CompositeFormat.Parse(format[(bar + 1)..]));
        });
        return string.Format(Culture, many is null || LastCount(args) == 1 ? one : many, args);
    }

    static long? LastCount(ReadOnlySpan<object?> args)
    {
        for (int i = args.Length - 1; i >= 0; i--)
            if (args[i] is int or long or uint or ulong or short or ushort or byte or sbyte) return Convert.ToInt64(args[i], CultureInfo.InvariantCulture);
        return null;
    }

    // 45% (English) / %45 (Turkish)
    public static string Pct(double value, string format = "0") => F("{0}%", value.ToString(format, Culture));

    // The English name and its names in every language we have: Windows may be in another language than the app.
    public static IEnumerable<string> WindowsNames(string english) =>
        All.SelectMany(l => l.WindowsNames.TryGetValue(english, out var names) ? names : []).Prepend(english).Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool HasWindowsName(string text, string english) =>
        WindowsNames(english).Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
}
