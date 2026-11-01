using System.Text;
using Microsoft.Win32;
using PerformanceBooster.UI;

namespace PerformanceBooster.Tests;

// Programs: registry reading, command parsing, end-to-end uninstall with a fake program, leftover scan/delete, last use.
// Real programs are never touched: the uninstalled program is an HKCU entry created by the test itself.
static class ProgramTests
{
    const RegistryHive HKCU = RegistryHive.CurrentUser;
    const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    static InstalledProgram Fake(string name, string publisher = "", IReadOnlyList<string>? dirs = null, ProgramSource source = ProgramSource.Desktop, string key = "HKCU|x") =>
        new(key, name, publisher, "1.0", null, 0, null, null, "x.exe", null, source, false) { Dirs = dirs ?? [] };

    public static void Run()
    {
        Fixtures.OnCleanup(() => Reg.DeleteTree(HKCU, Fixtures.TestKey));

        Runner.Test("Uninstall command parsing", () =>
        {
            Check.Equal((@"C:\Program Files\A B\unins000.exe", "/x"), Programs.SplitCommand("\"C:\\Program Files\\A B\\unins000.exe\" /x"), "quoted");
            Check.Equal((@"C:\Program Files\A B\unins000.exe", "/x /y"), Programs.SplitCommand(@"C:\Program Files\A B\unins000.exe /x /y"), "unquoted with spaces");
            Check.Equal(("MsiExec.exe", "/X{12345678-1234-1234-1234-123456789012}"), Programs.SplitCommand("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"), "msiexec");
            var (file, _) = Programs.SplitCommand(@"%SystemRoot%\System32\cmd.exe /c exit");
            Check.True(file.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase), "environment variable: " + file);
            Check.Equal(("rundll32", "x.dll,Uninstall"), Programs.SplitCommand("rundll32 x.dll,Uninstall"), "without an extension");
        });

        Runner.Test("Quiet uninstall command (MSI, Inno Setup, the program's own command)", () =>
        {
            const string guid = "{12345678-1234-1234-1234-123456789012}";
            Check.Equal(guid, Programs.ProductCode(guid, "MsiExec.exe /I" + guid, false), "MSI product code");
            Check.Equal<string?>(null, Programs.ProductCode("Discord", "x.exe", true), "key that is not a GUID");
            Check.Equal<string?>(null, Programs.ProductCode(guid, "C:\\x\\setup.exe /uninstall", false), "GUID key that is not MSI");
            Check.Equal($"MsiExec.exe /X{guid} /qn /norestart", Programs.QuietCommand(guid, null, null, guid), "MSI quiet");
            Check.Equal("\"C:\\A\\unins000.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART", Programs.QuietCommand("App_is1", null, "\"C:\\A\\unins000.exe\"", null), "Inno Setup");
            Check.Equal("own /S", Programs.QuietCommand("App_is1", "own /S", "x", null), "the program's own quiet command comes first");
            Check.Equal<string?>(null, Programs.QuietCommand("NSISApp", null, "uninst.exe", null), "an unknown uninstaller is not tried quietly");
        });

        Runner.Test("Name cleanup and system component detection", () =>
        {
            Check.Equal("7-Zip", Programs.CleanName("7-Zip 24.08 (x64)"), "version + architecture");
            Check.Equal("Microsoft Visual C++ 2015-2022 Redistributable", Programs.CleanName("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810"), "VC++");
            Check.Equal("Node.js", Programs.CleanName("Node.js"), "name with a dot");
            Check.Equal("Python", Programs.CleanName("Python 3.12.4 (64-bit)"), "Python");
            Check.Equal("Valve", Programs.CleanPublisher("Valve Corporation"), "company suffix");
            Check.Equal("Discord", Programs.CleanPublisher("Discord Inc."), "Inc.");
            Check.True(Fake("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810").IsSystemLike, "VC++ system component");
            Check.True(Fake("NVIDIA Graphics Driver 581.57").IsSystemLike && Fake("Microsoft Edge WebView2 Runtime").IsSystemLike, "driver, WebView2");
            Check.True(!Fake("Discord").IsSystemLike && !Fake("Steam").IsSystemLike, "ordinary program");
            Check.True(Fake("x", source: ProgramSource.Store, key: "MSIX|Microsoft.XboxGamingOverlay_7.1_x64__8wekyb3d8bbwe").IsSystemLike, "Xbox package");
            Check.True(!Fake("x", source: ProgramSource.Store, key: "MSIX|5319275A.WhatsAppDesktop_2.2640_x64__cv1g1gvanyjgm").IsSystemLike, "WhatsApp");
            Check.True(!Fake("x", source: ProgramSource.Store, key: "MSIX|Microsoft.Windows.DevHome_0.0.0.0_x64__8wekyb3d8bbwe").IsSystemLike
                       && !Fake("x", source: ProgramSource.Store, key: "MSIX|Microsoft.WindowsCalculator_11.2_x64__8wekyb3d8bbwe").IsSystemLike, "removable Windows apps");
            Check.True(Fake("x", source: ProgramSource.Store, key: "MSIX|Microsoft.DesktopAppInstaller_1.29_x64__8wekyb3d8bbwe").IsSystemLike, "winget (App Installer)");
        });

        Runner.Test("Registry reading: Control Panel rules (fake keys)", () =>
        {
            var root = Fixtures.TestKey + @"\Uninstall";
            var install = Path.Combine(Runner.Data, "Read", "App");
            Directory.CreateDirectory(install);
            void Entry(string name, params (string Name, object Value)[] values)
            {
                foreach (var (n, v) in values) Reg.Set(HKCU, root + "\\" + name, n, v, v is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
            Entry("Visible", ("DisplayName", "PB Visible 2.1 (x64)"), ("Publisher", "PB Corp"), ("DisplayVersion", "2.1"), ("UninstallString", $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c exit"),
                ("InstallDate", "20260115"), ("EstimatedSize", 2048), ("InstallLocation", install));
            Entry("Component", ("DisplayName", "PB Component"), ("UninstallString", "x"), ("SystemComponent", 1));
            Entry("Patch", ("DisplayName", "PB Patch"), ("UninstallString", "x"), ("ParentKeyName", "Visible"));
            Entry("Hotfix", ("DisplayName", "PB Hotfix"), ("UninstallString", "x"), ("ReleaseType", "Security Update"));
            Entry("NoCommand", ("DisplayName", "PB NoCommand"));
            Entry("Locked", ("DisplayName", "PB Locked"), ("UninstallString", "x"), ("NoRemove", 1));
            Entry("{12345678-1234-1234-1234-123456789012}", ("DisplayName", "PB Msi"), ("UninstallString", "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}"), ("WindowsInstaller", 1));

            using var baseKey = RegistryKey.OpenBaseKey(HKCU, RegistryView.Registry64);
            InstalledProgram? Read(string name)
            {
                using var key = baseKey.OpenSubKey(root + "\\" + name)!;
                return Programs.Read(key, HKCU, root + "\\" + name, name);
            }
            var visible = Read("Visible")!;
            Check.True(visible.Name == "PB Visible 2.1 (x64)" && visible.Publisher == "PB Corp" && visible.Version == "2.1", "fields");
            Check.Equal(2048L * 1024, visible.SizeBytes, "size (KB)");
            Check.Equal<DateTime?>(new DateTime(2026, 1, 15), visible.InstallDate, "install date");
            Check.True(visible.Dirs.Count == 1 && visible.Dirs[0].Equals(install + "\\", StringComparison.OrdinalIgnoreCase), "folder");
            Check.True(!visible.MachineWide && visible.Key == "HKCU|" + root + @"\Visible", "key");
            Check.True(visible.CanUninstall && !visible.CanUninstallQuietly && visible.Health == ProgramHealth.Ok, "can be uninstalled, not quietly, healthy");
            Check.True(Read("Component") is null && Read("Patch") is null && Read("Hotfix") is null && Read("NoCommand") is null, "hidden entries");
            var locked = Read("Locked")!;
            Check.True(!locked.CanUninstall, "NoRemove: no uninstall button");
            var msi = Read("{12345678-1234-1234-1234-123456789012}")!;
            Check.True(msi.UninstallCommand == "MsiExec.exe /X{12345678-1234-1234-1234-123456789012}" && msi.QuietCommand!.EndsWith("/qn /norestart", StringComparison.Ordinal), "MSI commands: " + msi.UninstallCommand);
        });

        Runner.Test("Broken entry detection (programs deleted by hand)", () =>
        {
            var root = Fixtures.TestKey + @"\Broken";
            string? offline = Enumerable.Range('G', 20).Select(c => $"{(char)c}:\\").FirstOrDefault(d => !Directory.Exists(d));
            void Entry(string name, string uninstall, string? location, bool msi = false)
            {
                Reg.Set(HKCU, root + "\\" + name, "DisplayName", "PB " + name, RegistryValueKind.String);
                Reg.Set(HKCU, root + "\\" + name, "UninstallString", uninstall, RegistryValueKind.String);
                if (location is not null) Reg.Set(HKCU, root + "\\" + name, "InstallLocation", location, RegistryValueKind.String);
                if (msi) Reg.Set(HKCU, root + "\\" + name, "WindowsInstaller", 1, RegistryValueKind.DWord);
            }
            Entry("NoUninstaller", @"""C:\PBGone12345\unins000.exe""", @"C:\PBGone12345\");
            Entry("NoFolder", $@"""{Path.Combine(Environment.SystemDirectory, "cmd.exe")}"" /c exit", @"C:\PBGone12345\Game");
            Entry("Healthy", $@"""{Path.Combine(Environment.SystemDirectory, "cmd.exe")}"" /c exit", Environment.SystemDirectory);
            Entry("SystemCommand", "rundll32.exe pbtest.dll,Uninstall", null);
            Entry("{12345678-1234-1234-1234-1234567890AB}", "MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}", @"C:\PBGone12345\Msi", msi: true);
            if (offline is not null) Entry("OfflineDrive", @"""C:\Windows\System32\cmd.exe"" /c exit", offline + @"Games\X");

            using var baseKey = RegistryKey.OpenBaseKey(HKCU, RegistryView.Registry64);
            InstalledProgram Read(string name)
            {
                using var key = baseKey.OpenSubKey(root + "\\" + name)!;
                return Programs.Read(key, HKCU, root + "\\" + name, name)!;
            }
            var broken = Read("NoUninstaller");
            Check.True(broken.Health == ProgramHealth.MissingUninstaller && !broken.CanUninstall && broken.IsBroken, "uninstaller deleted: cannot be uninstalled, broken");
            var noFolder = Read("NoFolder");
            Check.True(noFolder.Health == ProgramHealth.MissingFolder && noFolder.CanUninstall, "folder deleted: the uninstaller can still be tried");
            Check.Equal(ProgramHealth.Ok, Read("Healthy").Health, "healthy entry");
            Check.Equal(ProgramHealth.Ok, Read("SystemCommand").Health, "a Windows command (no path) is assumed to exist");
            Check.Equal(ProgramHealth.Ok, Read("{12345678-1234-1234-1234-1234567890AB}").Health, "MSI uninstalls from the cached package, not broken");
            if (offline is not null) Check.Equal(ProgramHealth.Ok, Read("OfflineDrive").Health, "a drive that is not connected is not broken");
        });

        Runner.Test("A broken entry is removed from the Windows list (with a backup)", () =>
        {
            const string keyName = "PBBrokenEntryTest";
            var key = UninstallRoot + "\\" + keyName;
            Fixtures.OnCleanup(() => Reg.DeleteTree(HKCU, key));
            Reg.Set(HKCU, key, "DisplayName", "PB Broken Entry Test", RegistryValueKind.String);
            Reg.Set(HKCU, key, "UninstallString", @"""C:\PBGone12345\Uninstall.exe""", RegistryValueKind.String);
            var all = Programs.Desktop();
            var program = all.FirstOrDefault(p => p.Name == "PB Broken Entry Test");
            Check.True(program is { Health: ProgramHealth.MissingUninstaller }, "listed as broken");
            var found = Leftovers.Scan(program!, all);
            var entry = found.FirstOrDefault(f => f.ListEntry);
            Check.True(entry is { Kind: LeftoverKind.Registry, NeedsAdmin: false } && entry.Path == @"HKCU\" + key, "list entry found: " + entry?.Path);
            Check.True(found[0].ListEntry, "the list entry comes first");
            var backup = Path.Combine(Runner.Data, "BrokenBackup");
            var (log, failures) = Leftovers.Delete([entry!], backup);
            Check.Equal(0, failures, "delete: " + string.Join(" ", log));
            Check.True(!Programs.Exists(program!) && !Programs.Desktop().Any(p => p.Name == "PB Broken Entry Test"), "removed from the Windows list");
            Check.True(Directory.GetFiles(backup, "*.reg").Any(f => File.ReadAllText(f).Contains(keyName)), ".reg backup taken");
        });

        Runner.Test("Clear history: the list empties, the hold info stays", () =>
        {
            var boot = DateTime.Now.AddHours(-1);
            var edge = new WingetPackage("Microsoft Edge", "Microsoft.Edge", "1", "2", "winget", false);
            var hidden = new HistoryEntry(DateTime.Now.AddMinutes(-5), "Microsoft Edge", "1 → 2", false, "x (code 0x8A15008E)", Hidden: true);
            Check.Equal(L.T("Updates with its own updater; winget cannot update it"), MainViewModel.HoldReason(edge, [hidden], boot), "a hidden entry still counts for the hold");
            UpdateHistory.Save([hidden, new HistoryEntry(DateTime.Now, "Node.js", "1 → 2", true, "ok")]);
            var loaded = UpdateHistory.Load();
            Check.True(loaded.Count == 2 && loaded.Single(h => h.Name == "Microsoft Edge").Hidden && !loaded.Single(h => h.Name == "Node.js").Hidden, "the hidden flag is saved");
            File.Delete(AppPaths.History);
        });

        Runner.Test("Real program list (read only)", async () =>
        {
            var clock = Stopwatch.StartNew();
            var all = await Programs.ListAsync(includeStore: true, CancellationToken.None);
            var desktop = all.Where(p => p.Source == ProgramSource.Desktop).ToList();
            var store = all.Where(p => p.Source == ProgramSource.Store).ToList();
            Check.True(desktop.Count > 10, "desktop program count: " + desktop.Count);
            Check.True(store.Count > 0, "Store app found");
            Check.Empty(all.Where(p => string.IsNullOrWhiteSpace(p.Name) || p.Name.StartsWith("@{", StringComparison.Ordinal) || p.Name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key), "unresolved name");
            Check.True(all.Select(p => p.Key).Distinct().Count() == all.Count, "no duplicates");
            Runner.Note($"{desktop.Count} desktop + {store.Count} Store, {clock.ElapsedMilliseconds} ms · quiet uninstall: {all.Count(p => p.CanUninstallQuietly)}");
            Runner.Note("Store: " + string.Join(", ", store.Select(p => p.Name)));
            Runner.Note("Largest: " + string.Join(", ", desktop.OrderByDescending(p => p.SizeBytes).Take(5).Select(p => $"{p.Name} {Maintenance.Size(p.SizeBytes)}")));
        });

        Runner.Test("A fake program is uninstalled end to end (quiet command)", async () =>
        {
            var (program, install) = CreateFakeProgram("PBFakeProgramA", "PB Fake Program A", "@echo off\r\nreg delete \"HKCU\\" + UninstallRoot + "\\PBFakeProgramA\" /f >nul\r\nexit /b 0\r\n");
            Check.True(Programs.Exists(program), "entry exists");
            var fake = Fixtures.StartFake(install, "fakeprogram.exe");
            var running = AppProcesses.FindRunningIn(program.Dirs);
            Check.True(running.Any(r => r.Id == fake.Id), "running process found");
            fake.Kill();
            fake.WaitForExit(5000);
            var result = await Uninstaller.RunAsync(program, quiet: true, CancellationToken.None);
            Check.True(result.Removed && result.Message == L.T("Uninstalled"), "uninstalled: " + result.Message);
            Check.True(!Programs.Exists(program), "entry deleted");
        });

        Runner.Test("An uninstaller that relaunches itself is waited for", async () =>
        {
            var script = "@echo off\r\nstart \"\" /b cmd /d /c \"ping -n 3 127.0.0.1 >nul & reg delete \"HKCU\\" + UninstallRoot + "\\PBFakeProgramB\" /f >nul\"\r\nexit /b 0\r\n";
            var (program, _) = CreateFakeProgram("PBFakeProgramB", "PB Fake Program B", script);
            var clock = Stopwatch.StartNew();
            var result = await Uninstaller.RunAsync(program, quiet: true, CancellationToken.None);
            Check.True(result.Removed, "uninstalled when the child process ended: " + result.Message);
            Check.True(clock.ElapsedMilliseconds >= 1500, "the child process was waited for: " + clock.ElapsedMilliseconds + " ms");
        });

        Runner.Test("Cancelled and failed uninstalls are reported correctly", async () =>
        {
            var (cancelled, _) = CreateFakeProgram("PBFakeProgramC", "PB Fake Program C", "@echo off\r\nexit /b 1602\r\n");
            var r1 = await Uninstaller.RunAsync(cancelled, quiet: true, CancellationToken.None);
            Check.True(!r1.Removed && r1.Cancelled && r1.Message == L.T("Cancelled"), "cancelled: " + r1.Message);
            var (failed, _) = CreateFakeProgram("PBFakeProgramD", "PB Fake Program D", "@echo off\r\nexit /b 5\r\n");
            var r2 = await Uninstaller.RunAsync(failed, quiet: true, CancellationToken.None);
            Check.True(!r2.Removed && !r2.Cancelled && r2.Message == L.F("Could not uninstall (exit code {0})", 5), "error: " + r2.Message);
            Check.True(Uninstaller.Interpret(3010, true).RebootRequired && Uninstaller.Interpret(1605, false).Removed, "MSI codes");
            Check.True(Uninstaller.Interpret(1618, false).Message.StartsWith(L.T("Another installation is in progress. Try again when it finishes."), StringComparison.Ordinal), "1618");
            Check.Equal(L.T("Uninstall did not finish; the program still appears to be installed."), Uninstaller.Interpret(0, false).Message, "0 but the entry stays");
        });

        Runner.Test("Leftover scan: name, publisher\\name, shortcut, registry; shared ones excluded", () =>
        {
            var root = Path.Combine(Runner.Data, "Leftovers");
            var appData = Path.Combine(root, "AppData");
            var shortcuts = Path.Combine(root, "StartMenu");
            Directory.CreateDirectory(Path.Combine(appData, "PB Fake Program", "cache"));
            File.WriteAllBytes(Path.Combine(appData, "PB Fake Program", "cache", "data.bin"), new byte[4096]);
            Directory.CreateDirectory(Path.Combine(appData, "PB Test", "PB Fake Program"));
            Directory.CreateDirectory(Path.Combine(appData, "Microsoft"));
            Directory.CreateDirectory(Path.Combine(appData, "PB Shared", "Other"));
            Directory.CreateDirectory(shortcuts);
            File.WriteAllText(Path.Combine(shortcuts, "PB Fake Program.lnk"), "x");
            var soft = Fixtures.TestKey + @"\Soft";
            Reg.Set(HKCU, soft + @"\PB Fake Program", "x", "1", RegistryValueKind.String);
            Reg.Set(HKCU, soft + @"\PB Test\PB Fake Program", "x", "1", RegistryValueKind.String);
            Reg.Set(HKCU, soft + @"\Unrelated", "x", "1", RegistryValueKind.String);

            var places = new Leftovers.Places([appData], [shortcuts], [(HKCU, soft)]);
            var program = new InstalledProgram("HKCU|" + Fixtures.TestKey + @"\Gone", "PB Fake Program 3.2 (x64)", "PB Test Inc.", "3.2", null, 0, null, null, "x", null, ProgramSource.Desktop, false);
            var other = Fake("PB Shared Thing", dirs: [Path.Combine(appData, "PB Shared", "Other") + "\\"], key: "HKCU|other");
            var found = Leftovers.Scan(program, [program, other], places);
            var paths = found.Select(f => f.Path).ToList();
            Check.True(paths.Contains(Path.Combine(appData, "PB Fake Program")), "folder by name");
            Check.True(paths.Contains(Path.Combine(appData, "PB Test", "PB Fake Program")), "publisher\\name");
            Check.True(paths.Contains(Path.Combine(shortcuts, "PB Fake Program.lnk")), "shortcut");
            Check.True(paths.Contains(@"HKCU\" + soft + @"\PB Fake Program") && paths.Contains(@"HKCU\" + soft + @"\PB Test\PB Fake Program"), "registry keys");
            Check.True(!paths.Any(p => p.Contains("Unrelated") || p.EndsWith("Microsoft", StringComparison.Ordinal) || p.Contains("PB Shared")), "no unrelated or shared items: " + string.Join(" | ", paths));
            Check.True(found.First(f => f.Path.EndsWith(Path.Combine(appData, "PB Fake Program"), StringComparison.Ordinal)).SizeBytes == 4096, "folder size");
            Check.True(found.All(f => !f.NeedsAdmin), "user folders do not need administrator rights");
            Check.True(Leftovers.NeedsAdmin(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "X")), "Program Files needs administrator rights");

            // Deleting: folder and shortcut go to the Recycle Bin, the registry key gets a .reg backup first.
            var backup = Path.Combine(Runner.Data, "LeftoverBackup");
            var (log, failures) = Leftovers.Delete(found.Where(f => f.Kind != LeftoverKind.Folder || f.Path.EndsWith("PB Fake Program", StringComparison.Ordinal)).ToList(), backup);
            Check.Equal(0, failures, "no delete errors:\n" + string.Join("\n", log));
            Check.True(!Directory.Exists(Path.Combine(appData, "PB Fake Program")) && !File.Exists(Path.Combine(shortcuts, "PB Fake Program.lnk")), "files moved");
            Check.True(Reg.Get(HKCU, soft + @"\PB Fake Program", "x") is null, "key deleted");
            var regs = Directory.GetFiles(backup, "*.reg");
            Check.True(regs.Length >= 2 && File.ReadAllText(regs[0]).Contains("PB Fake Program"), ".reg backup");
        });

        Runner.Test("Store app uninstall (fake PowerShell)", async () =>
        {
            Programs.PowerShellOverride = (Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0");
            try
            {
                var store = new InstalledProgram("MSIX|PBFake.Package_1.0.0.0_x64__abcdefghijklm", "PB Fake Store App", "PB", "1.0", null, 0, null, null, null, null, ProgramSource.Store, false);
                var result = await Uninstaller.RunAsync(store, quiet: true, CancellationToken.None);
                Check.True(result.Removed, "uninstalled: " + result.Message);
            }
            finally
            {
                Programs.PowerShellOverride = null;
            }
        });

        Runner.Test("Matching an update row to a program", () =>
        {
            var programs = new List<InstalledProgram>
            {
                Fake("Microsoft Edge", key: "HKLM|edge"),
                Fake("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", key: "HKLM|vc"),
                Fake("WhatsApp", source: ProgramSource.Store, key: "MSIX|5319275A.WhatsAppDesktop_2.2640.100.0_x64__cv1g1gvanyjgm"),
                Fake("Windows Terminal", source: ProgramSource.Store, key: "MSIX|Microsoft.WindowsTerminal_1.24.0_x64__8wekyb3d8bbwe"),
                Fake("Discord", key: "HKCU|discord"),
            };
            WingetPackage Pkg(string name, string id, string source = "winget") => new(name, id, "1", "2", source, false);
            Check.Equal("HKLM|edge", MainViewModel.Match(Pkg("Microsoft Edge", "Microsoft.Edge"), programs)?.Key, "same name");
            Check.Equal("HKLM|vc", MainViewModel.Match(Pkg("Microsoft Visual C++ 2015-2022 Redist…", "Microsoft.VCRedist.2015+.x64"), programs)?.Key, "truncated name");
            Check.Equal("MSIX|5319275A.WhatsAppDesktop_2.2640.100.0_x64__cv1g1gvanyjgm", MainViewModel.Match(Pkg("WhatsApp", "9NKSQGP7F2NH", "msstore"), programs)?.Key, "Store name");
            Check.Equal("MSIX|Microsoft.WindowsTerminal_1.24.0_x64__8wekyb3d8bbwe", MainViewModel.Match(Pkg("Windows Terminal Preview", "Microsoft.WindowsTerminal"), programs)?.Key, "package ID");
            Check.Equal<string?>(null, MainViewModel.Match(Pkg("Unknown App", "Vendor.Unknown"), programs)?.Key, "no match");
        });

        Runner.Test("Last use: UserAssist, Store ID, the app's own observation", () =>
        {
            Check.Equal("Pbqr", UsageHistory.Rot13("Code"), "ROT13");
            Check.Equal("Code", UsageHistory.Rot13(UsageHistory.Rot13("Code")), "ROT13 back");
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Check.Equal(pf + @"\App\app.exe", UsageHistory.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\App\app.exe"), "known folder");
            Check.True(UsageHistory.AumidMatches("Valve.Steam.Client", "Steam") && UsageHistory.AumidMatches("com.squirrel.Discord.Discord", "Discord") && UsageHistory.AumidMatches("Chrome", "Google Chrome"), "app ID matches");
            Check.True(!UsageHistory.AumidMatches("Valve.Steam.Client", "Riot Client") && !UsageHistory.AumidMatches("Microsoft.Windows.Explorer", "Windows Terminal"), "generic parts do not match");

            var dir = Path.Combine(Runner.Data, "UsageApp") + "\\";
            var program = Fake("Usage App", dirs: [dir]);
            var storeProgram = new InstalledProgram("MSIX|Notes_1.0_x64__a1b2c3d4e5f6g", "Notes", "", "1", null, 0, null, null, null, null, ProgramSource.Store, false) { PackageFamily = "Notes_a1b2c3d4e5f6g" };
            var t1 = new DateTime(2026, 3, 1, 12, 0, 0);
            var t2 = new DateTime(2026, 5, 1, 12, 0, 0);
            var ua = new List<UsageHistory.Entry> { new(dir + "app.exe", t1, 3), new("Notes_a1b2c3d4e5f6g!App", t2, 1), new(@"C:\Other\x.exe", t2, 1) };
            var seen = new Dictionary<string, DateTime> { [dir + "helper.exe"] = t2 };
            Check.Equal<DateTime?>(t2, UsageHistory.LastUsed(program, ua, seen, []), "newest record (observation)");
            Check.Equal<DateTime?>(t1, UsageHistory.LastUsed(program, ua, new Dictionary<string, DateTime>(), []), "UserAssist");
            Check.Equal<DateTime?>(t2, UsageHistory.LastUsed(storeProgram, ua, seen, []), "Store package family");
            Check.True(UsageHistory.LastUsed(program, [], new Dictionary<string, DateTime>(), [dir + "app.exe"]) is { } now && DateTime.Now - now < TimeSpan.FromSeconds(5), "open now");
            Check.Equal<DateTime?>(null, UsageHistory.LastUsed(Fake("Nothing", dirs: [@"C:\Nope\"]), ua, seen, []), "no information");

            var real = UsageHistory.ReadUserAssist();
            Check.True(real.Count > 0 && real.All(e => !e.Key.StartsWith('{') || e.Key.Length < 40), "real UserAssist read: " + real.Count);
            UsageHistory.ResetForTests();
            UsageHistory.RecordRunning();
            Check.True(UsageHistory.Seen().Count > 0, "running programs recorded");
            Runner.Note($"UserAssist: {real.Count} entries; latest: " + string.Join(", ", real.OrderByDescending(e => e.LastRun).Take(3).Select(e => $"{Path.GetFileName(e.Key)} {e.LastRun:g}")));
        });

        Runner.Test("Last use and size texts (en/tr)", () =>
        {
            var now = new DateTime(2026, 10, 29, 12, 0, 0);
            try
            {
                L.Init("en");
                Check.Equal("open now", ProgramRow.Relative(now.AddSeconds(-30), now), "now");
                Check.Equal("today", ProgramRow.Relative(now.AddHours(-3), now), "today");
                Check.Equal("yesterday", ProgramRow.Relative(now.AddDays(-1), now), "yesterday");
                Check.Equal("1 month ago", ProgramRow.Relative(now.AddDays(-35), now), "singular");
                Check.Equal("5 days ago", ProgramRow.Relative(now.AddDays(-5), now), "days");
                Check.Equal("2 months ago", ProgramRow.Relative(now.AddDays(-65), now), "months");
                Check.Equal("1 year ago", ProgramRow.Relative(now.AddDays(-400), now), "year");
                Check.Equal("< 1 MB", ProgramRow.FormatSize(1000), "small size");
                Check.Equal("–", ProgramRow.FormatSize(0), "unknown size");
                L.Init("tr");
                var turkish = ProgramRow.Relative(now.AddDays(-5), now);
                Check.True(turkish.StartsWith("5 ", StringComparison.Ordinal) && turkish != "5 days ago", "translated to Turkish: " + turkish);
            }
            finally
            {
                L.Init("en");
            }
        });
    }

    // Temporary fake program in the HKCU Programs list (uninstaller: a cmd script). Its entry and folder are deleted when the test ends.
    static (InstalledProgram Program, string Install) CreateFakeProgram(string keyName, string displayName, string uninstallScript)
    {
        var key = UninstallRoot + "\\" + keyName;
        Fixtures.OnCleanup(() => Reg.DeleteTree(HKCU, key));
        var install = Path.Combine(Runner.Data, "Programs", keyName);
        Directory.CreateDirectory(install);
        var script = Path.Combine(install, "uninstall.cmd");
        File.WriteAllText(script, uninstallScript, new UTF8Encoding(false));
        var command = $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /d /c \"\"{script}\"\"";
        Reg.Set(HKCU, key, "DisplayName", displayName, RegistryValueKind.String);
        Reg.Set(HKCU, key, "Publisher", "PB Test", RegistryValueKind.String);
        Reg.Set(HKCU, key, "InstallLocation", install, RegistryValueKind.String);
        Reg.Set(HKCU, key, "UninstallString", command, RegistryValueKind.String);
        Reg.Set(HKCU, key, "QuietUninstallString", command, RegistryValueKind.String);
        using var root = RegistryKey.OpenBaseKey(HKCU, RegistryView.Registry64);
        using var k = root.OpenSubKey(key)!;
        return (Programs.Read(k, HKCU, key, keyName)!, install);
    }
}
