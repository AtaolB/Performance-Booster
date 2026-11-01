using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PerformanceBooster.Tests;

// Startup manager, cleanup, hardware and counters, driver search, game mode, HTML report.
static partial class SystemTests
{
    const RegistryHive HKCU = RegistryHive.CurrentUser;
    // Letters specific to Turkish, lower and upper case: text that should be English must not contain them.
    internal const string TurkishLetters = "[\u00E7\u011F\u0131\u00F6\u015F\u00FC\u00C7\u011E\u0130\u00D6\u015E\u00DC]";

    [GeneratedRegex(TurkishLetters)]
    internal static partial Regex Turkish();

    public static void Run()
    {
        Runner.Test("A startup item is disabled and enabled (temporary Run value)", () =>
        {
            const string name = "PerformanceBoosterTest";
            var approved = StartupManager.Approved + @"\Run";
            Fixtures.OnCleanup(() => { Reg.Delete(HKCU, StartupManager.RunPath, name); Reg.Delete(HKCU, approved, name); });
            Reg.Set(HKCU, StartupManager.RunPath, name, $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c exit", RegistryValueKind.String);
            StartupItem? Find() => StartupManager.List().FirstOrDefault(i => i.Name == name && i.Location == StartupLocation.UserRun);

            Check.True(Find() is { Enabled: true, NeedsAdmin: false }, "listed, enabled");
            Check.Equal(L.T("This user"), Find()!.Scope, "scope");
            StartupManager.SetEnabled(StartupLocation.UserRun, name, false);
            Check.True(Find() is { Enabled: false }, "disabled");
            var data = (byte[])Reg.Get(HKCU, approved, name)!;
            Check.True(data.Length == 12 && data[0] == 3 && BitConverter.ToInt64(data, 4) > 0, "Task Manager format (03 + time)");
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
            Check.Equal(0, Elevated.RunCli(["--startup", "UserRun", b64, "1"]), "through the elevated copy");
            Check.True(Find() is { Enabled: true }, "enabled again");
            Check.True(File.ReadAllText(AppPaths.Log).Contains(L.F("{0} enabled at startup", name)), "log");
            Check.True(StartupManager.List().Count > 1, "other items are read too");
        });

        Runner.Test("Temporary file cleanup (fake folder)", () =>
        {
            var folder = Path.Combine(Runner.Data, "clean");
            var sub = Path.Combine(folder, "sub");
            Directory.CreateDirectory(sub);
            var old = DateTime.Now.AddDays(-2);
            void Make(string path, int size, DateTime at)
            {
                File.WriteAllBytes(path, new byte[size]);
                File.SetLastWriteTime(path, at);
            }
            Make(Path.Combine(folder, "old.txt"), 1000, old);
            Make(Path.Combine(folder, "new.txt"), 300, DateTime.Now);
            Make(Path.Combine(sub, "old2.bin"), 500, old);
            var lockedPath = Path.Combine(folder, "locked.txt");
            Make(lockedPath, 200, old);
            Check.Equal(1700L, Maintenance.FolderSize(folder, TimeSpan.FromDays(1)), "size older than 1 day");
            Check.Equal(2000L, Maintenance.FolderSize(folder, TimeSpan.Zero), "total size");
            CleanResult r;
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
                r = Maintenance.Clean(folder, TimeSpan.FromDays(1));
            Check.Equal(new CleanResult(1500, 2, 1), r, "deleted / skipped");
            Check.True(File.Exists(Path.Combine(folder, "new.txt")) && File.Exists(lockedPath), "the new and the locked file stayed");
            Check.True(!Directory.Exists(sub), "the emptied subfolder was deleted");
            Check.Equal(new CleanResult(0, 0, 0), Maintenance.Clean(Path.Combine(Runner.Data, "missing"), TimeSpan.Zero), "missing folder");
            Runner.Note($"TEMP (older than 1 day): {Maintenance.Size(Maintenance.TempSize())}, shader cache: {Maintenance.Size(Maintenance.ShaderCacheSize())} ({Maintenance.ShaderCacheFolders().Count} folders)");
        });

        Runner.Test("System info (WMI, registry, power)", () =>
        {
            var items = HardwareInfo.Collect();
            foreach (var group in new[] { HardwareInfo.GroupWindows, HardwareInfo.GroupCpu, HardwareInfo.GroupMemory, HardwareInfo.GroupGpu, HardwareInfo.GroupDisk })
                Check.True(items.Any(i => i.Group == group), "group exists: " + group);
            Check.Empty(items.Where(i => string.IsNullOrWhiteSpace(i.Value)).Select(i => $"{i.Group}/{i.Label}"), "empty value");
            // Values may come from Windows (plan name etc.); labels and hints belong to the app.
            Check.Empty(items.Where(i => Turkish().IsMatch(i.Group + i.Label + i.Hint)).Select(i => $"{i.Label}: {i.Hint}"), "Turkish text in the English UI");
            foreach (var i in items) Runner.Note($"[{i.State}] {i.Group} / {i.Label}: {i.Value}" + (i.Hint is null ? "" : $" — {i.Hint}"));
        });

        Runner.Test("Live counters: processor, memory, disk, GPU, display", () =>
        {
            var cpu = new CpuSampler();
            cpu.Sample();
            using var counters = new CounterSampler();
            Thread.Sleep(1000);
            double usage = cpu.Sample();
            var c = counters.Sample();
            Check.True(usage is >= 0 and <= 100, "processor %: " + usage);
            var (used, total, percent) = SystemInfo.Memory();
            Check.True(total > 1 && used > 0 && percent is > 0 and < 100, $"memory {used:0.0}/{total:0.0} GB");
            Check.True(double.IsNaN(c.DiskActivePercent) || c.DiskActivePercent is >= 0 and <= 100, "disk %: " + c.DiskActivePercent);
            Check.True(double.IsNaN(c.GpuPercent) || c.GpuPercent is >= 0 and <= 100, "GPU %: " + c.GpuPercent);
            var (hz, max, w, h) = SystemInfo.RefreshRate();
            Check.True(hz > 0 && max >= hz && w > 0 && h > 0, "display");
            Runner.Note($"CPU {usage:0.0}%, {c.CpuMhz:0} MHz · RAM {used:0.0}/{total:0.0} GB · disk {c.DiskActivePercent:0.0}% {c.DiskMBps:0.0} MB/s · GPU {c.GpuPercent:0.0}%, VRAM {c.GpuMemoryGb:0.0} GB · {w}x{h} {hz}/{max} Hz · AC: {SystemInfo.OnAcPower()}");
            Runner.Note($"Processor: {Cpu.Name()} · hybrid: {Cpu.IsHybrid} · base {Cpu.BaseMhz()} MHz · HAGS: {Gpu.HardwareScheduling()} · power plan: {Power.FriendlyName(Power.Active() ?? Guid.Empty)}");
        });

        Runner.Test("Refresh rate: fractional rates are not a lower refresh rate", () =>
        {
            Check.True(!SystemInfo.BelowMaxRefresh(143, 143.98, 144) && !SystemInfo.BelowMaxRefresh(59, 59.94, 60), "143.98 next to 144, 59.94 next to 60");
            Check.True(!SystemInfo.BelowMaxRefresh(143, null, 144) && !SystemInfo.BelowMaxRefresh(59, null, 60), "without the exact rate: 1 Hz below is the same rate");
            Check.True(SystemInfo.BelowMaxRefresh(120, 120.0, 144) && SystemInfo.BelowMaxRefresh(60, null, 144) && SystemInfo.BelowMaxRefresh(143, 143.0, 144), "really lower");
            Check.True(!SystemInfo.BelowMaxRefresh(144, 144.0, 144), "at the highest");
            var (hz, max, _, _) = SystemInfo.RefreshRate();
            var exact = SystemInfo.ExactRefreshRate(hz);
            Check.True(exact is null || Math.Abs(exact.Value - hz) < 1, $"exact rate near {hz}: {exact}");
            Runner.Note($"{hz} Hz (exact {exact?.ToString("0.###") ?? "unknown"}), highest mode {max} Hz");
        });

        Runner.Test("Process list and protection", () =>
        {
            var sampler = new ProcessSampler();
            sampler.Sample(15);
            Thread.Sleep(500);
            var top = sampler.Sample(15);
            Check.True(top.Count is > 0 and <= 15, "top 15");
            Check.True(top.Zip(top.Skip(1)).All(p => p.First.RamMb >= p.Second.RamMb), "sorted by RAM");
            Check.True(top.All(p => !ProcessSampler.IsProtected(p.Name) || !p.CanKill), "protected processes cannot be closed");
            Check.True(ProcessSampler.IsProtected("explorer") && ProcessSampler.IsProtected("csrss") && ProcessSampler.IsProtected(AppPaths.Id), "protection list");
            Runner.Note(string.Join(", ", top.Take(6).Select(p => $"{p.Name}{(p.Count > 1 ? " x" + p.Count : "")} {p.RamMb:0} MB")));
        });

        Runner.Test("NVIDIA monitoring (nvidia-smi)", () =>
        {
            var s = GpuMonitor.Parse("NVIDIA GeForce RTX 4080, 12, 45, 30.55, 2100, 1024, 12282, 0x0000000000000004");
            Check.True(s is { Utilization: 12, Temperature: 45, PowerW: 30.55, MemTotalMb: 12282, ThermalThrottle: false }, "parsing");
            Check.True(GpuMonitor.Parse("X, 1, 2, 3, 4, 5, 6, 0x40")!.ThermalThrottle, "thermal throttle bit");
            Check.True(GpuMonitor.Parse("[N/A], [N/A]") is null && GpuMonitor.Parse(null) is null, "incomplete line");
            if (GpuMonitor.FindNvidiaSmi() is null) Check.Skip("no nvidia-smi (not an NVIDIA graphics card); the parsing test passed");
            using var monitor = new GpuMonitor();
            GpuSample? sample = null;
            using var got = new ManualResetEventSlim();
            monitor.Sampled += x => { sample = x; got.Set(); };
            monitor.Start();
            Check.True(got.Wait(6000), "a sample within 6 s");
            monitor.Stop();
            Check.True(!monitor.Available, "stopped");
            Runner.Note($"{sample!.Name}: {sample.Utilization}% · {sample.Temperature}°C · {sample.PowerW} W · {sample.MemUsedMb}/{sample.MemTotalMb} MB");
        });

        Runner.Test("Windows Update driver search (read only)", () =>
        {
            var task = Task.Run(DriverUpdates.Search);
            if (!task.Wait(TimeSpan.FromMinutes(4))) throw new TestFailure("did not finish in 4 minutes");
            Runner.Note($"{task.Result.Count} drivers: " + string.Join("; ", task.Result.Select(d => $"{d.Title} ({d.Class}, {d.SizeMb:0.#} MB{(d.Optional ? ", optional" : "")})")));
        });

        Runner.Test("Demo data: a made-up computer, consistent results", () =>
        {
            var latency = Demo.Latency();
            Check.Equal(latency.Dpcs, latency.Drivers.Sum(d => d.Dpcs), "DPC total");
            Check.True(latency.DpcHistogram.Length == LatencyTrace.Buckets.Length + 1, "one bar per range");
            Check.Equal(Demo.Threads, latency.Cpus.Count, "a tile per core");
            Check.True(Demo.Dns().Select(r => r.EffectiveMs).SequenceEqual(Demo.Dns().Select(r => r.EffectiveMs).Order()), "DNS results fastest first");
            Check.True(Demo.Packages().Count > 0 && Demo.Hardware().Count > 0, "updates and system info");
            Check.True(Demo.WindowsGamePaths.All(p => p.EndsWith(".exe", StringComparison.Ordinal)) && Demo.Games.All(g => g.EndsWith(".exe", StringComparison.Ordinal)), "game names");
            Check.Equal(SpeedTest.Grade(9).Grade, Demo.Speed().Grade, "speed grade");
        });

        Runner.Test("Boot settings, page file and Resizable BAR: reading the tools' output", () =>
        {
            var bcd = BcdStore.Parse("Windows Boot Loader\r\n-------------------\r\nidentifier              {current}\r\nuseplatformclock        Yes\r\ntscsyncpolicy           Enhanced\r\n");
            Check.True(bcd["useplatformclock"] == "Yes" && bcd["tscsyncpolicy"] == "Enhanced" && !bcd.ContainsKey("disabledynamictick"), "elements");
            const string custom = PageFileStore.CustomPrefix + @"C:\pagefile.sys=4096-8192;D:\pagefile.sys=0-0";
            Check.True(PageFileStore.Files(custom).SequenceEqual([(@"C:\pagefile.sys", 4096u, 8192u), (@"D:\pagefile.sys", 0u, 0u)]), "custom sizes");
            Check.Equal("C: 4096–8192 MB, D: size managed by Windows", PageFileStore.Summary(custom), "shown as sizes");
            Check.Equal("auto", PageFileStore.Summary("auto"), "auto stays auto");
            const string memory = "    FB Memory Usage\n        Total : 8188 MiB\n    BAR1 Memory Usage\n        Total : 16384 MiB\n        Used : 2 MiB\n";
            Check.Equal<double?>(16384, HardwareInfo.ParseBar1Mb(memory), "BAR1 size");
            Check.Equal<double?>(null, HardwareInfo.ParseBar1Mb("No devices were found"), "no NVIDIA card");
            Check.True(new PcieLink(1, 8, 4, 16).FewerLanes && !new PcieLink(1, 16, 4, 16).FewerLanes, "fewer lanes than the card supports");
        });

        Runner.Test("New settings read on this computer (nothing is changed)", () =>
        {
            foreach (var id in new[] { "sched.quantum", "boot.platformclock", "boot.dynamictick", "boot.tscsync", "mem.pagefile", "mem.compression", "nv.refresh" })
            {
                var status = Catalog.Find(id)!.Evaluate();
                Check.True(!status.CurrentLabel.StartsWith("could not be read", StringComparison.Ordinal), $"{id}: {status.CurrentLabel}");
                Runner.Note($"{id}: {status.CurrentLabel} ({status.State})");
            }
            foreach (var card in Devices.Snapshot().Where(d => d.IsPci && d.ClassCode is "0300" or "0302" or "0380"))
                Runner.Note($"{card.Name}: PCIe {Devices.Link(card)}");
        });

        Runner.Test("Game mode: fake game process (the power plan does not change)", () =>
        {
            var plan = Power.Active() ?? throw new TestFailure("could not read the active plan");
            // Background limiting stays off: it would slow down the real apps on this computer during the test.
            var settings = new AppSettings
            {
                Games = ["pbfakegame.exe"], GameBoost = true, GamePlan = plan.ToString(), RaisePriority = true, LimitBackground = false, DetectWindowsGames = false,
            };
            TimerResolution.Request(1); // held by the app, not by game mode
            var watcher = new GameWatcher();
            watcher.Tick(settings);
            Check.True(watcher.ActiveGame is null && watcher.Status == L.T("Waiting for a game"), "waits while no game runs");
            var game = Fixtures.StartFake(Path.Combine(Runner.Data, "Game"), "pbfakegame.exe");
            watcher.Tick(settings);
            Check.Equal("pbfakegame", watcher.ActiveGame, "game detected");
            Check.True(watcher.Status.StartsWith(L.F("{0} is running", "pbfakegame"), StringComparison.Ordinal) && watcher.Status.Contains(L.T("priority: high")), "status: " + watcher.Status);
            game.Refresh();
            Check.Equal(ProcessPriorityClass.High, game.PriorityClass, "the game's priority was raised");
            Check.Equal(plan, Power.Active(), "same power plan (the chosen plan is already active)");
            game.Kill();
            game.WaitForExit(5000);
            watcher.Tick(settings);
            Check.True(watcher.ActiveGame is null && watcher.Status == L.T("Waiting for a game"), "goes back when the game closes");
            Check.True(TimerResolution.Holding, "the timer stays when the game closes");
            TimerResolution.Release();
            Check.Equal(plan, Power.Active(), "the power plan is kept");
            settings.GameBoost = false;
            watcher.Tick(settings);
            Check.Equal(L.T("Off"), watcher.Status, "off");
            settings.Games.Clear();
            watcher.Tick(settings);
            Check.Equal(L.T("No games in the list"), watcher.Status, "empty list");
        });
    }
}
