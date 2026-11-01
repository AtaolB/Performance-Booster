using System.Globalization;
using System.Text.RegularExpressions;

namespace PerformanceBooster.Core;

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);

public sealed record ChangelogEntry(Version Version, DateTime? Date, IReadOnlyList<ChangelogSection> Sections);

// The version notes: CHANGELOG.md (English) and CHANGELOG.tr.md (Turkish) in the repository, inside the exe as Changelog.<code>.md.
// "## 1.0.0 - 2026-10-29" starts a version, "### New" a group, "- text" an item. The release workflow puts the same text
// in each GitHub release, one block per language after a "<!-- lang:en -->" line, so the notes of a version that is not
// installed yet can be shown too.
public static partial class Changelog
{
    [GeneratedRegex(@"^##\s+v?(\d+(?:\.\d+){1,3})(?:\s*[-–]\s*(\d{4}-\d{2}-\d{2}))?\s*$")]
    private static partial Regex VersionHeading();

    [GeneratedRegex(@"^\s*<!--\s*lang:(\w+)\s*-->\s*$")]
    private static partial Regex LanguageMark();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    static readonly Dictionary<string, IReadOnlyList<ChangelogEntry>> _embedded = new();

    // The notes inside the exe in the UI language (English if that language has none).
    public static IReadOnlyList<ChangelogEntry> Embedded() => Embedded(L.Code);

    public static IReadOnlyList<ChangelogEntry> Embedded(string language)
    {
        lock (_embedded)
        {
            if (_embedded.TryGetValue(language, out var cached)) return cached;
            var assembly = typeof(Changelog).Assembly;
            using var stream = assembly.GetManifestResourceStream($"Changelog.{language}.md") ?? assembly.GetManifestResourceStream("Changelog.en.md");
            var entries = stream is null ? [] : Parse(new StreamReader(stream).ReadToEnd());
            return _embedded[language] = entries;
        }
    }

    // Versions newer than "after" up to and including "upTo", newest first.
    public static List<ChangelogEntry> Between(IEnumerable<ChangelogEntry> entries, Version? after, Version upTo)
    {
        var (from, to) = (after is null ? null : SelfUpdate.Normalize(after), SelfUpdate.Normalize(upTo));
        return entries.Where(e => e.Version <= to && (from is null || e.Version > from)).OrderByDescending(e => e.Version).ToList();
    }

    // What to show when "current" starts: the versions since the one that ran last. A first start (no settings yet) and
    // a downgrade show nothing; a version from before this was recorded shows the current version only.
    public static List<ChangelogEntry> AfterUpdate(IEnumerable<ChangelogEntry> entries, string? lastRun, bool hadSettings, Version current)
    {
        var last = SelfUpdate.ParseVersion(lastRun ?? "");
        if (last is null ? !hadSettings : last >= current) return [];
        return last is null ? Between(entries, null, current).Take(1).ToList() : Between(entries, last, current);
    }

    public static List<ChangelogEntry> Parse(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        Version? version = null;
        DateTime? date = null;
        var sections = new List<ChangelogSection>();
        string? title = null;
        var items = new List<string>();

        void EndSection()
        {
            if (items.Count > 0) sections.Add(new ChangelogSection(title ?? "", items.ToList()));
            items.Clear();
        }

        void EndVersion()
        {
            EndSection();
            if (version is not null) entries.Add(new ChangelogEntry(version, date, sections.ToList()));
            sections.Clear();
            title = null;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (VersionHeading().Match(line) is { Success: true } v)
            {
                EndVersion();
                version = SelfUpdate.ParseVersion(v.Groups[1].Value);
                date = DateTime.TryParseExact(v.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                EndSection();
                title = Plain(line.Substring(4));
            }
            else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
                items.Add(Plain(line.TrimStart().Substring(2)));
            else if (line.Length > 0 && char.IsWhiteSpace(raw[0]) && items.Count > 0)
                items[items.Count - 1] += " " + Plain(line.Trim()); // an item that goes on in the next line
        }
        EndVersion();
        return entries;
    }

    // The notes of a GitHub release in one language: the block after "<!-- lang:xx -->" (English if there is none for it),
    // read like one version of the changelog. Without language blocks the whole text is used.
    public static IReadOnlyList<ChangelogSection> FromRelease(string body, string language)
    {
        var blocks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        var all = new List<string>();
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (LanguageMark().Match(line) is { Success: true } m)
            {
                blocks[m.Groups[1].Value] = current = [];
                continue;
            }
            if (line.TrimStart().StartsWith("<details", StringComparison.Ordinal) || line.TrimStart().StartsWith("</details", StringComparison.Ordinal) || line.TrimStart().StartsWith("<summary", StringComparison.Ordinal)) continue;
            (current ?? all).Add(line);
        }
        var lines = blocks.TryGetValue(language, out var mine) ? mine : blocks.TryGetValue("en", out var english) ? english : all;
        var text = "## 0.0\n" + string.Join("\n", lines.Select(l => l.StartsWith("## ", StringComparison.Ordinal) ? "#" + l : l));
        return Parse(text).FirstOrDefault()?.Sections ?? [];
    }

    // Markdown marks are left out on screen: **bold**, `code`, [text](link).
    static string Plain(string text) => Link().Replace(text.Replace("**", "").Replace("`", ""), "$1").Trim();
}
