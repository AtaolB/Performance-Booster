using System.Text.Json;
using Microsoft.Win32;

namespace PerformanceBooster.Tests;

// Settings engine: option matching, state, registry stores, the Set/Restore flow of the elevated copy, settings files.
static class EngineTests
{
    const RegistryHive HKCU = RegistryHive.CurrentUser;
    const string Key = Fixtures.TestKey;

    static Tweak Fake(string id, ISettingStore store, params TweakOption[] options) => new()
    {
        Id = id, Category = "Test", Title = "Test " + id, Description = "-", Store = () => store, Options = Catalog.Fixed(options),
    };

    public static void Run()
    {
        Fixtures.OnCleanup(() => Reg.DeleteTree(HKCU, Key));

        Runner.Test("Requests for the elevated copy are not left behind", () =>
        {
            Directory.CreateDirectory(AppPaths.Folder);
            File.WriteAllText(AppPaths.DefenderRequest, "{}");
            File.WriteAllText(AppPaths.LeftoverRequest, "{}");
            AppPaths.DeleteRequests();
            Check.True(!File.Exists(AppPaths.DefenderRequest) && !File.Exists(AppPaths.LeftoverRequest), "both deleted");
            AppPaths.DeleteRequests(); // nothing there: no error
        });

        Runner.Test("Option matching and state", () =>
        {
            TweakOption[] options = [Catalog.Rec("off", "Off", "0", [null]), Catalog.Opt("on", "On", "1", "2")];
            var s = Fake("t", new FakeStore("0"), options).Evaluate();
            Check.Equal(CheckState.Ok, s.State, "recommended value");
            Check.Equal("Off", s.CurrentLabel, "label");
            Check.Equal(CheckState.Ok, Fake("t", new FakeStore(null), options).Evaluate().State, "no value is recommended too (also=null)");
            s = Fake("t", new FakeStore("2"), options).Evaluate();
            Check.Equal(CheckState.Problem, s.State, "equivalent value (also)");
            Check.Equal("on", s.Current?.Key, "matching option");
            s = Fake("t", new FakeStore("7"), options).Evaluate();
            Check.Equal(L.F("custom value ({0})", "7"), s.CurrentLabel, "unknown value");
            Check.Equal(CheckState.Problem, s.State, "unknown value is a problem");
            s = Fake("t", new FakeStore(null), Catalog.Rec("on", "On", "1")).Evaluate();
            Check.Equal(L.T("not set (Windows default)"), s.CurrentLabel, "no value");
            Check.Equal(CheckState.Info, Fake("t", new FakeStore("1"), Catalog.Opt("a", "A", "1"), Catalog.Opt("b", "B", "2")).Evaluate().State, "setting without recommendation is info");
            s = Fake("t", new FakeStore("1", available: false), options).Evaluate();
            Check.Equal(CheckState.NotApplicable, s.State, "no store");
            Check.Equal(L.T("not present on this computer"), s.CurrentLabel, "no store label");
            var na = new Tweak { Id = "t", Category = "T", Title = "T", Description = "-", Store = () => new FakeStore("0"), Options = Catalog.Fixed(options), Applicable = () => false };
            Check.Equal(L.T("not applicable to this computer"), na.Evaluate().CurrentLabel, "not applicable");
            s = Fake("t", new ThrowingStore(), options).Evaluate();
            Check.True(s.State == CheckState.NotApplicable && s.CurrentLabel.Contains("read error"), "read error is caught: " + s.CurrentLabel);
            Check.Equal(L.F("{0} (recommended)", "Off"), options[0].ToString(), "recommended label");
            Check.True(new TweakOption("dhcp", "DHCP", "dhcp").Matches("DHCP") && options[1].Matches("2") && !options[1].Matches("3"), "case and equivalent values");
        });

        Runner.Test("Registry store (temporary HKCU key)", () =>
        {
            var dword = new RegistryStore(HKCU, () => Key, "Dword", RegistryValueKind.DWord);
            Check.True(dword.Available, "path exists");
            Check.Equal<string?>(null, dword.Read(), "no value at first");
            dword.Write("4294967295");
            Check.Equal("4294967295", dword.Read(), "unsigned DWORD");
            Check.Equal(-1, (int)Reg.Get(HKCU, Key, "Dword")!, "0xFFFFFFFF in the registry");
            dword.Write(null);
            Check.Equal<string?>(null, dword.Read(), "null deletes the value");
            var text = new RegistryStore(HKCU, () => Key, "Text", RegistryValueKind.String);
            text.Write("hello");
            Check.Equal("hello", text.Read(), "string value");
            var none = new RegistryStore(HKCU, () => null, "X", RegistryValueKind.DWord);
            Check.True(!none.Available && none.Read() is null, "unavailable without a path");
            Check.Throws<InvalidOperationException>(() => none.Write("1"), "write without a path");
        });

        Runner.Test("Settings row: the selection is always one of the shown options (options read again each check)", () =>
        {
            var store = new FakeStore("1");
            string onLabel = "On";
            // Like the power plans and services: every check builds new option objects.
            var tweak = new Tweak
            {
                Id = "t", Category = "T", Title = "T", Description = "-", Store = () => store,
                Options = () => [Catalog.Rec("off", "Off", "0"), Catalog.Opt("on", onLabel, "1"), Catalog.Opt("max", "Max", "2")],
            };
            var row = new PerformanceBooster.UI.TweakRow(tweak);
            bool Shown() => row.SelectedOption is { } s && row.Options.Any(o => ReferenceEquals(o, s));

            row.Update(tweak.Evaluate(), false);
            Check.True(Shown() && row.SelectedOption!.Key == "on", "first check");
            row.Update(tweak.Evaluate(), false);
            Check.True(Shown() && row.SelectedOption!.Key == "on", "second check: the object of the shown list, not the copy the check read");
            Check.True(new TweakOption("on", "x", "1").Equals(Catalog.Opt("on", "On", "1")) && !new TweakOption("on", "On", "1").Equals(Catalog.Opt("on", "On", "2")), "equal by key and value");

            row.SelectedOption = null; // the drop-down while its list is replaced
            Check.True(Shown() && row.SelectedOption!.Key == "on", "an empty selection from the drop-down is ignored");

            row.SelectedOption = row.Options.First(o => o.Key == "max");
            row.Update(tweak.Evaluate(), false);
            Check.True(Shown() && row.SelectedOption!.Key == "max" && row.CanApply, "the user's pick survives a check");

            onLabel = "On for 3 of 5 devices";
            row.Update(tweak.Evaluate(), false);
            Check.True(row.Options.Any(o => o.Label == onLabel), "changed labels are shown");
            Check.True(Shown() && row.SelectedOption!.Key == "max", "the pick survives a new list");

            row.ResetPick();
            store.Write("2");
            row.Update(tweak.Evaluate(), false);
            Check.True(Shown() && row.SelectedOption!.Key == "max" && !row.CanApply, "after applying: the current value");
            store.Write("7");
            row.Update(tweak.Evaluate(), false);
            Check.True(Shown() && row.SelectedOption!.Key == "off", "unknown value: the recommended option is offered");
        });

        Runner.Test("Composite store", () =>
        {
            var a = new RegistryStore(HKCU, () => Key, "CA", RegistryValueKind.DWord);
            var b = new RegistryStore(HKCU, () => Key, "CB", RegistryValueKind.DWord);
            var c = new CompositeStore(a, b);
            Check.Equal("-|-", c.Read(), "neither exists");
            c.Write("1|-");
            Check.Equal("1", a.Read(), "first part");
            Check.Equal<string?>(null, b.Read(), "- = no value");
            c.Write("0|5");
            Check.Equal("0|5", c.Read(), "read");
            Check.Throws<ArgumentException>(() => c.Write("1"), "value count mismatch");
            c.Write("-|-");
            Check.Equal("-|-", c.Read(), "cleared");
        });

        Runner.Test("DirectX flag store", () =>
        {
            var path = Key + @"\DirectX";
            var swap = new DxFlagStore("SwapEffectUpgradeEnable", path);
            var vrr = new DxFlagStore("VRROptimizeEnable", path);
            Check.Equal<string?>(null, swap.Read(), "absent at first");
            swap.Write("1");
            vrr.Write("0");
            Check.Equal("1", swap.Read(), "first flag");
            Check.Equal("0", vrr.Read(), "second flag");
            var raw = Reg.Get(HKCU, path, "DirectXUserGlobalSettings") as string ?? "";
            Check.True(raw.Contains("SwapEffectUpgradeEnable=1;") && raw.Contains("VRROptimizeEnable=0;"), "two flags in one value: " + raw);
            Reg.Set(HKCU, path, "DirectXUserGlobalSettings", raw + "AutoHDREnable=2097;", RegistryValueKind.String);
            swap.Write(null);
            Check.Equal<string?>(null, swap.Read(), "flag deleted");
            Check.True((Reg.Get(HKCU, path, "DirectXUserGlobalSettings") as string ?? "").Contains("AutoHDREnable=2097;"), "other flags are kept");
            vrr.Write(null);
            new DxFlagStore("AutoHDREnable", path).Write(null);
            Check.True(Reg.Get(HKCU, path, "DirectXUserGlobalSettings") is null, "the value is deleted with the last flag");
        });

        Runner.Test("Elevated copy: apply, back up, undo", () =>
        {
            var a = new RegistryStore(HKCU, () => Key, "A", RegistryValueKind.DWord);
            var plan = new FakeStore("x");
            var tweakA = new Tweak
            {
                Id = "test.a", Category = "Test", Title = "Test test.a", Description = "-", Store = () => a, NeedsReboot = true,
                Options = Catalog.Fixed(Catalog.Rec("off", "Off", "0"), Catalog.Opt("on", "On", "1")),
            };
            Catalog.Override =
            [
                tweakA,
                Fake("test.missing", new FakeStore("1", available: false), Catalog.Rec("on", "On", "1")),
                Fake("power.plan", plan, Catalog.Opt("p1", "P1", "p1v"), Catalog.Rec("p2", "P2", "p2v")),
            ];
            try
            {
                File.Delete(AppPaths.Backups);
                var log = new List<string>();
                var effects = new Elevated.Effects();
                int failures = Elevated.Set("test.a=on,test.missing=on,power.plan=p2,nosuch.id=1,test.a=bogus", false, log, effects);
                Check.Equal(1, failures, "only the invalid option fails");
                Check.True(log[0].Contains("Test power.plan") && log[0].Contains("P2"), "the power plan is applied first: " + log[0]);
                Check.Equal("1", a.Read(), "A written");
                Check.Equal("p2v", plan.Value, "plan written");
                Check.True(log.Any(l => l.StartsWith(L.T("SKIPPED"), StringComparison.Ordinal) && l.Contains("test.missing")), "missing setting skipped");
                Check.True(log.Any(l => l.Contains(L.F("no such option: {0}", "bogus"))), "invalid option reported");
                var backups = Backups.Load();
                Check.True(backups.TryGetValue("test.a", out var first) && first.Value is null, "A's original value (none) backed up");
                Check.Equal("x", backups["power.plan"].Value, "the plan's original value backed up");
                Check.True(!backups.ContainsKey("test.missing"), "a skipped setting is not backed up");
                Check.True(effects.Reboot && !effects.Power && !effects.Nic, "effects: restart only");

                Elevated.Set("test.a=off", false, log, effects);
                Check.Equal("0", a.Read(), "second change");
                Check.True(Backups.Load()["test.a"].Value is null, "the first backup does not change");

                var tail = new List<string>();
                effects.Finish(tail);
                Check.True(tail.Contains(Elevated.RebootMarker), "restart marker");

                var report = Elevated.Report();
                Check.True(report.Contains("=== Test ===") && report.Contains($"[{L.T("OK   ")}] Test test.a = Off") && report.Contains($"[{L.T("N/A  ")}] Test test.missing"), "text report:\n" + report);
                a.Write("1");
                Check.True(Elevated.Report().Contains($"[{L.T("ISSUE")}] Test test.a = On  ({L.F("recommended: {0}", "Off")})"), "different setting and its recommendation");

                log.Clear();
                int restoreFailures = Elevated.Restore("all", log, new Elevated.Effects());
                Check.Equal(0, restoreFailures, "undo without errors");
                Check.True(log[0].Contains("Test power.plan"), "the plan comes first when undoing too");
                Check.Equal<string?>(null, a.Read(), "A back to its original state (no value)");
                Check.Equal("x", plan.Value, "plan back to its original value");
                Check.Equal(0, Backups.Load().Count, "backups emptied");
                Check.Equal(0, Elevated.Restore("all", log, new Elevated.Effects()), "undo with nothing to undo");
            }
            finally
            {
                Catalog.Override = null;
            }
        });

        Runner.Test("Elevated command line (in process)", () =>
        {
            Catalog.Override = [Fake("test.cli", new FakeStore("0"), Catalog.Rec("on", "On", "1"))];
            try
            {
                Check.Equal(0, Elevated.RunCli(["--set", ""]), "--set empty");
                Check.Equal(L.T("There was nothing to change."), File.ReadAllText(AppPaths.Log).Trim(), "log");
                Check.Equal(0, Elevated.RunCli(["--set", "test.cli=on"]), "--set setting");
                Check.True(File.ReadAllText(AppPaths.Log).Contains("Test test.cli → On"), "change in the log");
                Check.Equal(0, Elevated.RunCli(["--restore", "test.cli"]), "--restore");
                var report = Path.Combine(Runner.Data, "report.txt");
                Check.Equal(0, Elevated.RunCli(["--report", report]), "--report");
                Check.True(File.ReadAllText(report).Contains("Test test.cli"), "report file");
                Check.Equal(2, Elevated.RunCli(["--report"]), "--report without a file");
                Check.Equal(1, Elevated.RunCli(["--startup", "UserRun"]), "missing argument");
                Check.Equal(1, Elevated.RunCli(["--unknown"]), "unknown command");
            }
            finally
            {
                Catalog.Override = null;
            }
        });

        Runner.Test("Settings file: save, load, defaults when broken", () =>
        {
            var s = new AppSettings { Language = "en", Games = ["cs2.exe"], SkippedVersions = ["A|1"], RaisePriority = true };
            s.Save();
            var r = AppSettings.Load();
            Check.True(r.Language == "en" && r.Games.SequenceEqual(["cs2.exe"]) && r.RaisePriority && r.SkippedVersions.Count == 1, "read back");
            Check.True(!File.Exists(AppPaths.Settings + ".tmp"), "no temporary file left");
            File.WriteAllText(AppPaths.Settings, "{ broken");
            var d = AppSettings.Load();
            Check.True(d.Language == "auto" && d.GameBoost && d.IncludeUnknown, "defaults for a broken file");
            AppSettings.ResetCurrent();
            Check.True(ReferenceEquals(AppSettings.Current, AppSettings.Current), "single settings object");
            File.Delete(AppPaths.Settings);
            AppSettings.ResetCurrent();
        });

        Runner.Test("Update history: old format is read, 100 entry limit", () =>
        {
            File.WriteAllText(AppPaths.History, """[{"At":"2026-10-27T16:30:27+03:00","Name":"App Installer","Change":"a → b","Ok":true,"Message":"m"}]""");
            var old = UpdateHistory.Load();
            Check.True(old.Count == 1 && old[0].Id is null && old[0].Ok, "old entry");
            var many = Enumerable.Range(0, 130).Select(i => new HistoryEntry(DateTime.Now.AddMinutes(-i), $"P{i}", "1 → 2", true, "ok", $"P.{i}", "1", "2")).ToList();
            UpdateHistory.Save(many);
            var loaded = UpdateHistory.Load();
            Check.Equal(100, loaded.Count, "at most 100");
            Check.Equal("P0", loaded[0].Name, "newest first");
            Check.Equal("P.0", loaded[0].Id, "new fields");
            File.Delete(AppPaths.History);
        });

        Runner.Test("Search boxes: words, case, Turkish letters", () =>
        {
            Check.True(TextSearch.Matches(TextSearch.Words("  "), "anything"), "empty query matches everything");
            Check.True(TextSearch.Matches(TextSearch.Words("msi"), "MSI mode"), "case");
            Check.True(TextSearch.Matches(TextSearch.Words("MSI"), "ms\u0131 modu"), "dotless i");
            Check.True(TextSearch.Matches(TextSearch.Words("i\u015flemci"), "\u0130\u015eLEMC\u0130"), "dotted capital I");
            Check.True(TextSearch.Matches(TextSearch.Words("ag gecikme"), "A\u011f", "Gecikmeyi d\u00fc\u015f\u00fcr\u00fcr"), "accents, words across texts");
            Check.True(TextSearch.Matches(TextSearch.Words("timer HPET"), null, "HPET (platform timer)"), "words in any order, null text");
            Check.True(!TextSearch.Matches(TextSearch.Words("hpet nagle"), "HPET"), "every word has to match");
            Check.True(!TextSearch.Matches(TextSearch.Words("ab"), "a", "b"), "a word never spans two texts");
            var tweak = new Tweak { Id = "t", Category = "Network", Title = "Nagle", Description = "Sends small packets at once.", Store = () => new FakeStore("0"), Options = Catalog.Fixed(Catalog.Rec("a", "A", "0")) };
            var row = new PerformanceBooster.UI.TweakRow(tweak);
            Check.True(row.Matches(TextSearch.Words("network")), "a setting is found by its group");
            Check.True(row.Matches(TextSearch.Words("packets nagle")), "title and description");
            Check.True(!row.Matches(TextSearch.Words("power")), "no match");
        });
    }
}
