namespace PerformanceBooster.Tests;

// The version notes: both CHANGELOG files list the same versions, the newest one is the app's version (so every release has
// notes), and the notes are read the same way from the exe, from a GitHub release and after an update.
static class ChangelogTests
{
    public static void Run()
    {
        Runner.Test("CHANGELOG files: same versions in both languages, newest = app version", () =>
        {
            var en = Changelog.Parse(File.ReadAllText(Path.Combine(Runner.Root, "CHANGELOG.md")));
            var tr = Changelog.Parse(File.ReadAllText(Path.Combine(Runner.Root, "CHANGELOG.tr.md")));
            Check.True(en.Count > 0, "versions in CHANGELOG.md");
            Check.Equal(SelfUpdate.Current, en[0].Version, "the newest version in CHANGELOG.md is the version in the project file");
            Check.Equal(string.Join(", ", en.Select(e => e.Version)), string.Join(", ", tr.Select(e => e.Version)), "the same versions in CHANGELOG.tr.md");
            for (int i = 0; i < en.Count; i++)
            {
                var (a, b) = (en[i], tr[i]);
                Check.True(i == 0 || en[i - 1].Version > a.Version, $"{a.Version}: newest first");
                Check.True(a.Date is not null && a.Date == b.Date, $"{a.Version}: the same date in both");
                Check.True(a.Sections.Count > 0 && a.Sections.All(s => s.Items.Count > 0 && s.Title.Length > 0), $"{a.Version}: groups with items");
                Check.Equal(string.Join("/", a.Sections.Select(s => s.Items.Count)), string.Join("/", b.Sections.Select(s => s.Items.Count)), $"{a.Version}: as many items in each group in both languages");
            }
        });

        Runner.Test("Changelog inside the exe, in the UI language", () =>
        {
            var en = Changelog.Embedded("en");
            var tr = Changelog.Embedded("tr");
            Check.True(en.Count > 0 && en[0].Version == SelfUpdate.Current, "English notes in the exe");
            Check.True(tr.Count == en.Count && tr[0].Sections[0].Title != en[0].Sections[0].Title, "Turkish notes in the exe");
            Check.Equal(en.Count, Changelog.Embedded("de").Count, "a language without notes gets English");
        });

        Runner.Test("Changelog reading: groups, items, lines that go on, markdown marks", () =>
        {
            var entries = Changelog.Parse("# Title\n\nIntro\n\n## 2.0.0 - 2026-01-02\n\n### New\n- One **bold** `code`\n  goes on\n- [Link](https://x) two\n\n### Fixed\n* Three\n\n## 1.5 - 2025-12-31\n### New\n- Four\n");
            Check.Equal(2, entries.Count, "versions");
            Check.Equal(new Version(2, 0, 0, 0), entries[0].Version, "version");
            Check.Equal(new DateTime(2026, 1, 2), entries[0].Date, "date");
            Check.Equal("New|Fixed", string.Join("|", entries[0].Sections.Select(s => s.Title)), "groups");
            Check.Equal("One bold code goes on|Link two", string.Join("|", entries[0].Sections[0].Items), "items");
            Check.Equal(new Version(1, 5, 0, 0), entries[1].Version, "two-part version");

            var between = Changelog.Between(entries, new Version(1, 5), new Version(2, 0));
            Check.True(between.Count == 1 && between[0].Version == new Version(2, 0, 0, 0), "versions after 1.5 up to 2.0");
            Check.Equal(2, Changelog.Between(entries, null, new Version(9, 0)).Count, "all");
        });

        Runner.Test("Release notes: the block of the UI language", () =>
        {
            const string body = "<!-- lang:en -->\n### New\n- English item\n\n<details>\n<summary>Turkish</summary>\n\n<!-- lang:tr -->\n### Yeni\n- Turkish item\n\n</details>\n";
            Check.Equal("Yeni: Turkish item", Flat(Changelog.FromRelease(body, "tr")), "Turkish");
            Check.Equal("New: English item", Flat(Changelog.FromRelease(body, "en")), "English");
            Check.Equal("New: English item", Flat(Changelog.FromRelease(body, "de")), "other language: English");
            Check.Equal("What's new: Plain item", Flat(Changelog.FromRelease("## What's new\n- Plain item\n", "tr")), "a release without language blocks");
            Check.Equal("", Flat(Changelog.FromRelease("Just a sentence.", "en")), "no list");

            static string Flat(IReadOnlyList<ChangelogSection> sections) => string.Join(" / ", sections.Select(s => s.Title + ": " + string.Join(", ", s.Items)));
        });

        Runner.Test("What's new after an update", () =>
        {
            var entries = Changelog.Parse("## 3.2.0 - 2026-03-01\n### New\n- c\n## 3.1.0 - 2026-02-01\n### New\n- b\n## 3.0.0 - 2026-01-01\n### New\n- a\n");
            var v32 = new Version(3, 2, 0, 0);
            Check.Equal("3.2.0.0 3.1.0.0", Versions(Changelog.AfterUpdate(entries, "3.0.0", true, v32)), "skipped versions too");
            Check.Equal("3.2.0.0", Versions(Changelog.AfterUpdate(entries, "3.1.0", true, v32)), "one version");
            Check.Equal("", Versions(Changelog.AfterUpdate(entries, "3.2.0", true, v32)), "same version");
            Check.Equal("", Versions(Changelog.AfterUpdate(entries, "4.0.0", true, v32)), "older version started (downgrade)");
            Check.Equal("", Versions(Changelog.AfterUpdate(entries, null, false, v32)), "first start");
            Check.Equal("3.2.0.0", Versions(Changelog.AfterUpdate(entries, null, true, v32)), "from a version that did not record itself");

            static string Versions(List<ChangelogEntry> list) => string.Join(" ", list.Select(e => e.Version));
        });
    }
}
