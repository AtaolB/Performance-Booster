using System.Text;
using Microsoft.Win32;
using PerformanceBooster.UI;

namespace PerformanceBooster.Tests;

sealed class ListProgress<TValue> : IProgress<TValue>
{
    public List<TValue> Items { get; } = [];

    public void Report(TValue value)
    {
        lock (Items) Items.Add(value);
    }
}

// winget output parsing, the whole flow with a fake winget (list, pin, upgrade), finding open apps.
static class UpdateTests
{
    static string Row(params string[] c) => c[0].PadRight(40) + c[1].PadRight(30) + c[2].PadRight(16) + c[3].PadRight(16) + c[4];

    static readonly string Rule = new('-', 110);

    static List<string> UpgradeOutput(bool localizedHeaders = false)
    {
        var header = localizedHeaders ? Row("Nom", "ID", "Version", "Disponible", "Source") : Row("Name", "Id", "Version", "Available", "Source");
        return
        [
            "   - \r   \\ \r" + header,
            Rule,
            Row("Microsoft Edge", "Microsoft.Edge", "156.0.4314.42", "156.0.4314.50", "winget"),
            Row("Microsoft Visual C++ 2015-2022 Redist…", "Microsoft.VCRedist.2015+.x64", "14.40.33810.0", "14.44.35211.0", "winget"),
            Row("App Installer", "Microsoft.AppInstaller", "1.29.379.0", "1.29.380", "winget"),
            Row("Some Tool", "Vendor.SomeTool", "Unknown", "2.0", "winget"),
            Row("Old Thing", "Vendor.OldThing", "< 1.5", "1.6", "winget"),
            Row("WhatsApp", "9NKSQGP7F2NH", "2.2440.9.0", "2.2445.1.0", "msstore"),
            "6 upgrades available.",
            "",
            "The following packages have an upgrade available, but require explicit targeting for upgrade:",
            header,
            Rule,
            Row("Discord", "Discord.Discord", "1.0.9200", "1.0.9210", "winget"),
            "1 package(s) have pins that prevent upgrade. Use the 'winget pin' command to view and edit pins.",
        ];
    }

    static void FakeWinget(IEnumerable<string> output, int exitCode = 0)
    {
        var file = Path.Combine(Runner.Data, $"winget-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, string.Join("\r\n", output) + "\r\n", new UTF8Encoding(false));
        Winget.Override = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c type \"{file}\" & exit /b {exitCode} & rem ");
    }

    static void CheckUpgrades(List<WingetPackage> list)
    {
        Check.Equal(7, list.Count, "package count");
        var by = list.ToDictionary(p => p.Id);
        Check.Equal("156.0.4314.42", by["Microsoft.Edge"].Installed, "installed version");
        Check.Equal("156.0.4314.50", by["Microsoft.Edge"].Available, "new version");
        Check.True(by["Microsoft.VCRedist.2015+.x64"].Name.EndsWith('…'), "truncated name");
        Check.True(by["Microsoft.AppInstaller"].IsWinget, "winget itself");
        Check.True(by["Vendor.SomeTool"].UnknownVersion && by["Vendor.OldThing"].UnknownVersion, "unknown versions (Unknown, < 1.5)");
        Check.True(by["9NKSQGP7F2NH"].IsStore, "Store app");
        Check.True(by["Discord.Discord"].RequireExplicit && !by["Microsoft.Edge"].RequireExplicit, "packages that require explicit targeting");
    }

    public static void Run()
    {
        Fixtures.OnCleanup(() => Winget.Override = null);

        Runner.Test("winget table (two tables, spinner, truncation)", () => CheckUpgrades(Winget.ParseUpgrades(UpgradeOutput())));

        Runner.Test("winget table with column headers in another language", () => CheckUpgrades(Winget.ParseUpgrades(UpgradeOutput(localizedHeaders: true))));

        Runner.Test("Empty and invalid output", () =>
        {
            Check.Equal(0, Winget.ParseUpgrades(["No installed package found matching input criteria."]).Count, "no updates");
            Check.Equal(0, Winget.ParseUpgrades([]).Count, "empty output");
            Check.Equal(0, Winget.ParseUpgrades([Row("Name", "Id", "Version", "Available", "Source"), Rule]).Count, "header only");
        });

        Runner.Test("winget pin list", () =>
        {
            var header = "Name".PadRight(20) + "Id".PadRight(24) + "Version".PadRight(12) + "Source".PadRight(10) + "Pin type";
            var pins = Winget.ParsePins([header, new string('-', 74), "Discord".PadRight(20) + "Discord.Discord".PadRight(24) + "1.0.9200".PadRight(12) + "winget".PadRight(10) + "Pinning"]);
            Check.True(pins.Count == 1 && pins.Contains("discord.discord"), "pin (case insensitive)");
            Check.Equal(0, Winget.ParsePins(["There are no pins configured."]).Count, "no pins");
        });

        Runner.Test("Test versions (alpha, beta…) are not offered", async () =>
        {
            foreach (var v in new[] { "1.0.0-beta.2", "2.1-rc1", "3.13.0b1", "3.14.0a7", "7.5.0-preview.3", "1.0.0.dev20", "2.0.0-alpha", "1.4-pre", "5.0.0-Nightly" })
                Check.True(Winget.IsPrerelease(v), "test version: " + v);
            foreach (var v in new[] { "156.0.4314.56", "1.0.9210", "2.2445.1.0", "Unknown", "< 1.5", "v2.0", "1.2.3-hotfix", "8u401", "4.0a", "2024.10" })
                Check.True(!Winget.IsPrerelease(v), "regular version: " + v);

            Check.True(Winget.CompareVersions("1.10", "1.9") > 0 && Winget.CompareVersions("2.0", "2.0.0") == 0 && Winget.CompareVersions("v1.2.1", "1.2") > 0, "version order");
            Check.Equal("2.0.1 1.9.2", string.Join(" ", Winget.ParseVersions(["   - ", "Found Some Tool [Vendor.SomeTool]", "Version", "--------", "2.0.1", "1.9.2"])), "version list");

            var channel = new WingetPackage("PowerShell Preview", "Microsoft.PowerShell.Preview", "7.6.0-preview.4", "7.6.0-preview.5", "winget", false);
            var devEdition = new WingetPackage("Firefox Developer Edition", "Mozilla.Firefox.DeveloperEdition", "134.0b4", "134.0b5", "winget", false);
            var regular = new WingetPackage("Microsoft Edge", "Microsoft.Edge", "156.0.4314.50", "156.0.4314.56", "winget", false);
            var newerStable = new WingetPackage("Some Tool", "Vendor.SomeTool", "1.9.0", "2.1.0-beta.1", "winget", false);
            var onlyBeta = new WingetPackage("Other Tool", "Vendor.OtherTool", "3.0.0", "3.1.0-rc1", "winget", false);
            var unknown = new WingetPackage("Old Tool", "Vendor.OldTool", "Unknown", "5.0b2", "winget", false);
            Check.True(Winget.IsTestChannel(channel) && Winget.IsTestChannel(devEdition) && !Winget.IsTestChannel(newerStable), "test channels by ID or name");

            var asked = new List<string>();
            var versions = new Dictionary<string, List<string>>
            {
                ["Vendor.SomeTool"] = ["2.1.0-beta.1", "2.0.1", "1.9.2", "1.9.0"],
                ["Vendor.OtherTool"] = ["3.1.0-rc1", "3.0.0", "2.9"],
                ["Vendor.OldTool"] = ["5.0b2", "4.2", "4.10"],
            };
            var list = await Winget.SkipPrereleasesAsync([channel, devEdition, regular, newerStable, onlyBeta, unknown], (p, _) =>
            {
                asked.Add(p.Id);
                return Task.FromResult(versions[p.Id]);
            }, CancellationToken.None);
            var by = list.ToDictionary(p => p.Id);
            Check.True(by.ContainsKey(channel.Id) && by.ContainsKey(devEdition.Id) && by[regular.Id] == regular, "test channels and regular versions stay as they are");
            Check.True(by[newerStable.Id] is { Available: "2.0.1", ExactVersion: true }, "the newest regular version above the installed one");
            Check.True(!by.ContainsKey(onlyBeta.Id), "no newer regular version: not offered");
            Check.True(by[unknown.Id] is { Available: "4.10", ExactVersion: true }, "unknown installed version: the newest regular version");
            Check.Equal("Vendor.SomeTool Vendor.OtherTool Vendor.OldTool", string.Join(" ", asked), "versions are asked only for test versions");

            // The upgrade asks for that version by number; a regular package does not.
            var args = Path.Combine(Runner.Data, $"winget-args-{Guid.NewGuid():N}.txt");
            Winget.Override = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c >\"{args}\" echo ");
            await Winget.UpgradeAsync(by[newerStable.Id], false, true, new ListProgress<UpgradeProgress>(), CancellationToken.None);
            Check.True(File.ReadAllText(args).Contains("--version \"2.0.1\""), "exact version: " + File.ReadAllText(args));
            await Winget.UpgradeAsync(regular, false, true, new ListProgress<UpgradeProgress>(), CancellationToken.None);
            Check.True(!File.ReadAllText(args).Contains("--version"), "newest version: " + File.ReadAllText(args));
            Winget.Override = null;
        });

        Runner.Test("Download percentage", () =>
        {
            Check.Equal<double?>(12.5, Winget.DownloadPercent("  ██████▒▒▒▒▒▒▒▒▒▒▒▒  12.5 MB / 100 MB"), "MB");
            Check.Equal<double?>(50, Winget.DownloadPercent("  ████▒▒▒▒  1,5 GB / 3 GB"), "GB with a decimal comma");
            Check.Equal<double?>(45, Winget.DownloadPercent("  ████▒▒▒▒  45%"), "percent");
            Check.Equal<double?>(null, Winget.DownloadPercent("Downloading https://example.com/setup.exe"), "address line");
            Check.Equal<double?>(null, Winget.DownloadPercent("Successfully installed. 100%"), "percent without a bar");
        });

        Runner.Test("Upgrade output: stages and results", () =>
        {
            var o = new Winget.UpgradeOutput();
            var stages = new[]
            {
                "Found Node.js [OpenJS.NodeJS] Version 26.12.1",
                "This application is licensed to you by its owner.",
                "Downloading https://nodejs.org/dist/v26.12.1/node-v26.12.1-x64.msi",
                "\r  ██▒▒▒▒  10 MB / 30 MB",
                "Successfully verified installer hash",
                "Starting package install...",
                "Successfully installed",
            }.Select(o.Read).Where(p => p is not null).ToList();
            Check.True(stages.Any(p => p!.Stage == UpgradeStage.Downloading && Math.Abs(p.Percent - 33.3) < 0.1), "download percentage");
            Check.True(stages.Any(p => p!.Stage == UpgradeStage.Verifying) && stages.Any(p => p!.Stage == UpgradeStage.Installing), "verification and install");
            var ok = o.Result(0);
            Check.True(ok.Success && !ok.RebootRequired && ok.Message == L.T("Updated"), "success: " + ok.Message);

            var r = new Winget.UpgradeOutput();
            r.Read("Successfully installed. Restart the application to complete the upgrade.");
            var reboot = r.Result(0);
            Check.True(reboot.Success && reboot.RebootRequired, "restart required");

            var n = new Winget.UpgradeOutput();
            n.Read("No applicable upgrade found.");
            var na = n.Result(unchecked((int)0x8A15002B));
            Check.True(!na.Success && na.NotApplicable && na.Message == L.T("No applicable update found"), "not applicable");

            var m = new Winget.UpgradeOutput();
            m.Read("A newer version was found, but the install technology is different from the current version installed. Please uninstall the package and install the newer version.");
            var mismatch = m.Result(Winget.InstallTechnologyMismatch);
            Check.True(!mismatch.Success && mismatch.Message.StartsWith(L.T("Installed with a different installer; winget cannot update it. If the app updates itself (browsers, for example), use its own updater."), StringComparison.Ordinal), "installer mismatch: " + mismatch.Message);
            Check.True(mismatch.Message.EndsWith("(code 0x8A15008E)", StringComparison.Ordinal), "code: " + mismatch.Message);

            var busy = new Winget.UpgradeOutput();
            busy.Read("Installer failed with exit code: 1618");
            Check.True(busy.Result(unchecked((int)0x8A150049)).Message.StartsWith(L.T("Another installation is in progress. Try again when it finishes."), StringComparison.Ordinal), "1618");

            var other = new Winget.UpgradeOutput();
            other.Read("Installer failed with exit code: 5");
            Check.True(other.Result(1).Message.StartsWith("Installer failed with exit code: 5", StringComparison.Ordinal), "winget's message for an unknown error");
        });

        Runner.Test("Download size, install percentage and estimated install progress", () =>
        {
            var o = new Winget.UpgradeOutput();
            var download = o.Read("\r  ██▒▒▒▒  12.5 MB / 100 MB");
            Check.True(download is { Stage: UpgradeStage.Downloading, Percent: 12.5 } && download.Text == L.F("Downloading · {0}", "12.5 MB / 100 MB"), "download: " + download?.Text);
            o.Read("Starting package install...");
            var install = o.Read("  ████▒▒▒▒  40%");
            Check.True(install is { Stage: UpgradeStage.Installing, Percent: 40 }, "a bar during the install (MSIX) is the install percentage");

            double start = Winget.EstimatedInstallPercent(TimeSpan.Zero), half = Winget.EstimatedInstallPercent(TimeSpan.FromSeconds(20)), late = Winget.EstimatedInstallPercent(TimeSpan.FromHours(1));
            Check.True(start == 0 && Math.Abs(half - 47.5) < 0.01 && late > half && late <= 95, $"estimate: {start} {half} {late}");
        });

        Runner.Test("Package row: estimated percentage, stopping, cancelling and share of the overall progress", () =>
        {
            var row = new PackageRow(new WingetPackage("Node.js", "OpenJS.NodeJS", "26.6.0", "26.12.1", "winget", false));
            row.SetWaiting();
            Check.True(row.CanCancel && row.Share == 0 && !row.ShowPercent, "queued");
            var at = DateTime.UtcNow;
            row.Report(new(UpgradeStage.Downloading, 50, "x"), at);
            Check.True(row.PercentText == L.Pct(50) && Math.Abs(row.Share - 0.25) < 1e-9, "download half way: " + row.PercentText);
            row.Report(new(UpgradeStage.Installing, -1, "y"), at.AddSeconds(1));
            Check.True(row.IsInstalling && row.ShowPercent && row.PercentText == L.Pct(0), "the install estimate starts from zero, shown as a plain percentage: " + row.PercentText);
            row.Tick(at.AddSeconds(21));
            Check.True(Math.Abs(row.ProgressValue - 47.5) < 0.01 && Math.Abs(row.Share - 0.7375) < 1e-6, "after 20 s: " + row.ProgressValue);
            row.Report(new(UpgradeStage.Installing, -1, "y"), at.AddSeconds(31));
            Check.True(row.ProgressValue > 47.5, "a repeated line of the same stage does not reset the estimate");
            row.Report(new(UpgradeStage.Installing, 80, "z"), at.AddSeconds(32));
            Check.True(row.PercentText == L.Pct(80), "a real percentage ends the estimate: " + row.PercentText);

            row.Stopping();
            row.Report(new(UpgradeStage.Installing, 90, "late"), at.AddSeconds(33));
            Check.True(row.IsStopping && !row.IsInstalling && row.CanCancel && row.StatusText == L.T("Stopping…"), "progress arriving while stopping is ignored");
            row.Skip(L.T("Cancelled"));
            Check.True(row.State == PackageState.Skipped && row.CanUpdate && !row.CanCancel && row.Share == 1, "cancelled: can be updated again");

            row.SetWaiting();
            row.Reset();
            Check.True(row.State == PackageState.Available && row.InBulk && row.Share == 0, "cancelled while queued: back to the list");
        });

        Runner.Test("Fake winget: list, pin, details, upgrade", async () =>
        {
            FakeWinget(UpgradeOutput());
            CheckUpgrades(await Winget.ListUpgradesAsync(true, CancellationToken.None));

            FakeWinget(["Failed in attempting to update the source: winget", "An unexpected error occurred while executing the command:", "0x8a15000f : Data required by the source is missing"], unchecked((int)0x8A15000F));
            var error = await ThrowsAsync<InvalidOperationException>(() => Winget.ListUpgradesAsync(true, CancellationToken.None));
            Check.Equal("0x8a15000f : Data required by the source is missing", error.Message, "list error");

            FakeWinget(["Pin added successfully"]);
            var (pinOk, pinMessage) = await Winget.PinAsync(new WingetPackage("Discord", "Discord.Discord", "1", "2", "winget", false), true, CancellationToken.None);
            Check.True(pinOk && pinMessage == "Pin added successfully", "pin");

            FakeWinget(["   - ", "Found Discord [Discord.Discord]", "Version: 1.0.9210", "Publisher: Discord Inc."]);
            var show = await Winget.ShowAsync(new WingetPackage("Discord", "Discord.Discord", "1", "2", "winget", false), CancellationToken.None);
            Check.True(show.StartsWith("Found Discord", StringComparison.Ordinal) && show.Contains("Publisher: Discord Inc.") && !show.Contains("   - "), "details: " + show);

            FakeWinget(["Found Node.js [OpenJS.NodeJS] Version 26.12.1", "Downloading https://x/y.msi", "  ██▒▒  5 MB / 10 MB", "Successfully verified installer hash", "Starting package install...", "Successfully installed"]);
            var progress = new ListProgress<UpgradeProgress>();
            var result = await Winget.UpgradeAsync(new WingetPackage("Node.js", "OpenJS.NodeJS", "26.6.0", "26.12.1", "winget", false), true, true, progress, CancellationToken.None);
            Check.True(result.Success, "upgrade: " + result.Message);
            Check.True(progress.Items[0].Stage == UpgradeStage.Preparing && progress.Items.Any(p => p.Stage == UpgradeStage.Downloading && p.Percent == 50), "progress");

            FakeWinget(["A newer version was found, but the install technology is different from the current version installed."], Winget.InstallTechnologyMismatch);
            var mismatch = await Winget.UpgradeAsync(new WingetPackage("Microsoft Edge", "Microsoft.Edge", "1", "2", "winget", false), true, true, new ListProgress<UpgradeProgress>(), CancellationToken.None);
            Check.True(!mismatch.Success && mismatch.ExitCode == Winget.InstallTechnologyMismatch, "exit code carried: " + mismatch.ExitCode);
            Winget.Override = null;
        });

        Runner.Test("Fake winget: cancelling an upgrade closes winget and the next command runs", async () =>
        {
            Winget.Override = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c echo Downloading https://x/y.msi & ping -n 30 127.0.0.1 >nul & rem ");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var clock = Stopwatch.StartNew();
            var progress = new ListProgress<UpgradeProgress>();
            await ThrowsAsync<OperationCanceledException>(() => Winget.UpgradeAsync(new WingetPackage("Node.js", "OpenJS.NodeJS", "1", "2", "winget", false), true, true, progress, cts.Token));
            Check.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"stopped without waiting 30 s ({clock.Elapsed.TotalSeconds:0.0} s)");
            Check.True(progress.Items.Any(p => p.Stage == UpgradeStage.Downloading), "progress before the cancel arrived");

            FakeWinget(["Pin added successfully"]);
            var (ok, _) = await Winget.PinAsync(new WingetPackage("Discord", "Discord.Discord", "1", "2", "winget", false), true, CancellationToken.None);
            Check.True(ok, "the next winget command ran without waiting");
            Winget.Override = null;
        });

        Runner.Test("Packages that were updated but still show, and those winget cannot update, are held", () =>
        {
            var boot = DateTime.Now.AddHours(-1);
            var appInstaller = new WingetPackage("App Installer", "Microsoft.AppInstaller", "1.29.379.0", "1.29.380", "winget", false);
            var history = new List<HistoryEntry> { new(DateTime.Now.AddMinutes(-5), "App Installer", "", true, "", "Microsoft.AppInstaller", "1.29.379.0", "1.29.380") };
            Check.Equal(L.T("Updated; finishes after the computer restarts"), MainViewModel.HoldReason(appInstaller, history, boot), "updated since this boot");
            Check.Equal<string?>(null, MainViewModel.HoldReason(appInstaller, history, DateTime.Now), "offered again after a restart");
            Check.Equal<string?>(null, MainViewModel.HoldReason(appInstaller with { Available = "1.29.400" }, history, boot), "a newer version is offered");
            Check.Equal<string?>(null, MainViewModel.HoldReason(appInstaller, [history[0] with { Ok = false }], boot), "an ordinary failed attempt does not hold");
            Check.Equal<string?>(null, MainViewModel.HoldReason(appInstaller, [], boot), "no history");

            // A record from the old version (no ID, code in the message) and a later successful attempt.
            var edge = new WingetPackage("Microsoft Edge", "Microsoft.Edge", "156.0.4314.42", "156.0.4314.50", "winget", false);
            var old = new HistoryEntry(DateTime.Now.AddDays(-1), "Microsoft Edge", "156.0.4314.42 → 156.0.4314.50", false, "A newer version was found, but the install technology is different from the current version installed. (kod 0x8A15008E)");
            Check.Equal(L.T("Updates with its own updater; winget cannot update it"), MainViewModel.HoldReason(edge, [old], boot), "winget cannot update it (old entry)");
            var later = new HistoryEntry(DateTime.Now.AddHours(-2), "Microsoft Edge", "", true, "ok", "Microsoft.Edge", "156.0.4314.30", "156.0.4314.42");
            Check.Equal<string?>(null, MainViewModel.HoldReason(edge, [old, later], boot), "a later successful attempt removes the hold");

            var row = new PackageRow(edge) { IsSelected = true };
            Check.True(row.InBulk && row.CanUpdate, "in bulk updates at first");
            row.Hold("x");
            Check.True(!row.InBulk && row.CanUpdate && !row.IsSelected && row.State == PackageState.Held, "held: can be updated individually, not in bulk");
            row.Finish(new UpgradeResult(true, false, false, 0, "ok"));
            Check.True(row.State == PackageState.Done && !row.CanUpdate, "done");
        });

        Runner.Test("Real winget: update list and pins (read only)", async () =>
        {
            Winget.Override = null;
            if (!Winget.Available) Check.Skip("no winget");
            var clock = Stopwatch.StartNew();
            var list = await Winget.ListUpgradesAsync(true, CancellationToken.None);
            var pins = await Winget.PinnedIdsAsync(CancellationToken.None);
            Check.True(list.All(p => p.Id.Length > 0 && !p.Id.Contains(' ') && p.Available.Length > 0), "valid rows");
            Runner.Note($"{list.Count} updates, {pins.Count} pins, {clock.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", list.Select(p => $"{p.Name} {p.Installed}→{p.Available}")));
        });

        Runner.Test("The running process of an app to update is found and closed", async () =>
        {
            const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PerformanceBoosterTestApp";
            var install = Path.Combine(Runner.Data, "FakeApp");
            var bin = Path.Combine(install, "bin");
            Fixtures.OnCleanup(() => Reg.DeleteTree(RegistryHive.CurrentUser, uninstall));
            var fake = Fixtures.StartFake(bin, "fakeapp.exe");
            Reg.Set(RegistryHive.CurrentUser, uninstall, "DisplayName", "PB Fake App", RegistryValueKind.String);
            Reg.Set(RegistryHive.CurrentUser, uninstall, "InstallLocation", install, RegistryValueKind.String);
            try
            {
                var package = new WingetPackage("PB Fake App", "Test.PBFakeApp", "1.0", "2.0", "winget", false);
                var other = new WingetPackage("Something Else", "Test.Else", "1.0", "2.0", "winget", false);
                var running = AppProcesses.FindRunning([package, other]);
                Check.True(running.TryGetValue("Test.PBFakeApp", out var hits) && hits.Any(h => h.Id == fake.Id), "running process found");
                Check.True(!running.ContainsKey("Test.Else"), "an unrelated package does not match");
                var left = await AppProcesses.CloseGracefullyAsync(hits!, TimeSpan.FromSeconds(1.5));
                Check.True(left.Any(l => l.Id == fake.Id), "a process without a window does not close gracefully");
                AppProcesses.Kill(left);
                Check.True(fake.WaitForExit(5000), "killed");
            }
            finally
            {
                Reg.DeleteTree(RegistryHive.CurrentUser, uninstall);
            }
        });

        Runner.Test("MSIX package matching", () =>
        {
            const string apps = @"C:\Program Files\WindowsApps\";
            var terminal = new WingetPackage("Windows Terminal", "Microsoft.WindowsTerminal", "1", "2", "winget", false);
            var notes = new WingetPackage("Contoso Notes", "Contoso.Notes", "1", "2", "winget", false);
            Check.True(AppProcesses.IsMsixOf(apps + @"Microsoft.WindowsTerminal_1.24.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", terminal), "by ID");
            Check.True(AppProcesses.IsMsixOf(apps + @"Notes_1.0.0_x64__a1b2c3d4e5f6g\app\notes.exe", notes), "by the last part of the ID");
            Check.True(!AppProcesses.IsMsixOf(apps + @"Microsoft.Windows.Photos_2025.1_x64__8wekyb3d8bbwe\Photos.exe", terminal), "another package");
            Check.True(!AppProcesses.IsMsixOf(@"C:\Program Files\Contoso Notes\notes.exe", notes), "not MSIX");
            Check.True(!AppProcesses.IsMsixOf(apps + @"Go_1.0_x64__abc\go.exe", new WingetPackage("Go", "X.Go", "1", "2", "winget", false)), "a very short name does not match");
        });
    }

    static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }
        throw new TestFailure($"expected {typeof(TException).Name}");
    }
}
