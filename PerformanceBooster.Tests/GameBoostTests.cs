using System.Security.Principal;
using System.Xml.Linq;

namespace PerformanceBooster.Tests;

// Game mode extras: timer resolution, background apps on low CPU (fake processes only), the RAM cleaner's rule, loop and task.
static class GameBoostTests
{
    static ProcessInfo Proc(int id, string name, int session = 1) => new(id, 0, name, session, 0, 0, 0, 0, 0, 0, 0);

    static ProcessInfo Snapshot(Process p) => ProcessSnapshot.Take().Single(i => i.Id == p.Id);

    public static void Run()
    {
        Runner.Test("Timer resolution: query, request and release", () =>
        {
            var info = TimerResolution.Query() ?? throw new TestFailure("Windows did not report the timer resolution");
            Check.True(info.FinestMs is > 0 and <= 1 && info.CoarsestMs >= 10 && info.CurrentMs >= info.FinestMs - 0.001, $"values: {info}");
            try
            {
                var got = TimerResolution.Request(1.0);
                Check.True(got is <= 1.0001 && TimerResolution.Holding && Math.Abs(TimerResolution.RequestedMs - 1.0) < 0.01, "requested: " + got);
                Check.True(TimerResolution.Query()!.Value.CurrentMs <= 1.0001, "the timer runs at 1 ms or finer");
            }
            finally
            {
                TimerResolution.Release();
            }
            Check.True(!TimerResolution.Holding, "released");
        });

        Runner.Test("Timer resolution: held by the helper copy as the setting says (the app's exe)", () =>
        {
            if (!File.Exists(Runner.Exe)) Check.Skip("no exe");
            TimerResolution.HelperExe = Runner.Exe;
            var settings = new AppSettings { TimerOn = true, TimerMs = 1 };
            try
            {
                TimerResolution.Apply(settings);
                Thread.Sleep(3000); // the helper starts
                Check.True(TimerResolution.HelperRunning && TimerResolution.HelperMs == 1, "helper running");
                Check.True(TimerResolution.Query()!.Value.CurrentMs <= 1.0001, "the timer runs at 1 ms or finer: " + TimerResolution.Query());
                var helper = ProcessSnapshot.Take().Where(p => p.ParentId == Environment.ProcessId && p.Name == "PerformanceBooster").Select(p => p.Id).ToList();
                TimerResolution.Apply(settings);
                Check.True(ProcessSnapshot.Take().Any(p => helper.Contains(p.Id)), "kept as it is (not started again)");
                settings.TimerOn = false;
                TimerResolution.Apply(settings);
                Check.True(!TimerResolution.HelperRunning, "turned off");
            }
            finally
            {
                TimerResolution.StopHelper();
                TimerResolution.HelperExe = null;
            }
        });

        Runner.Test("Game detection: the list, the games Windows recognizes and the running game kept (fake processes)", () =>
        {
            int session = Process.GetCurrentProcess().SessionId;
            ProcessInfo P(int id, string name, int s = -1) => new(id, 0, name, s < 0 ? session : s, 0, 0, 0, 0, 0, 0, 0);
            string[] known = [@"D:\Games\Valorant\VALORANT-Win64-Shipping.exe", @"C:\Steam\common\cs2\game\bin\win64\cs2.exe"];
            var paths = new Dictionary<int, string> { [20] = @"d:\games\valorant\VALORANT-Win64-Shipping.exe", [30] = @"C:\Other\cs2.exe", [31] = @"C:\Steam\common\cs2\game\bin\win64\cs2.exe" };
            string? PathOf(int id) => paths.GetValueOrDefault(id);

            Check.Equal<GameMatch?>(null, GameWatcher.Find([P(10, "chrome"), P(11, "Discord")], ["cs2.exe"], known, 0, PathOf), "nothing running");
            var listed = GameWatcher.Find([P(20, "VALORANT-Win64-Shipping"), P(12, "mygame")], ["mygame.exe"], known, 0, PathOf);
            Check.True(listed is { Id: 12, Entry: "mygame.exe", FromWindows: false }, "the list comes first: " + listed);
            var windows = GameWatcher.Find([P(20, "VALORANT-Win64-Shipping")], [], known, 0, PathOf);
            Check.True(windows is { Id: 20, FromWindows: true, Entry: "VALORANT-Win64-Shipping.exe" }, "recognized by Windows (path, any case): " + windows);
            Check.Equal<GameMatch?>(null, GameWatcher.Find([P(30, "cs2")], [], known, 0, PathOf), "same name, another path: not the game");
            Check.True(GameWatcher.Find([P(30, "cs2"), P(31, "cs2")], [], known, 0, PathOf) is { Id: 31 }, "the copy at the known path");
            Check.Equal<GameMatch?>(null, GameWatcher.Find([P(12, "mygame", s: session + 1)], ["mygame.exe"], [], 0, PathOf), "another user's session");
            Check.True(GameWatcher.Find([P(12, "mygame"), P(20, "VALORANT-Win64-Shipping")], ["mygame.exe"], known, 20, PathOf) is { Id: 20 }, "the running game is kept while it runs");

            Check.True(WindowsGames.IsGame(@"D:\Games\x\game.exe") && !WindowsGames.IsGame(@"C:\Program Files\Google\Chrome\Application\chrome.exe")
                       && !WindowsGames.IsGame(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer2.exe") && !WindowsGames.IsGame("notes.txt"), "what counts as a game");
            Check.Equal("cs2", WindowsGames.DisplayName(@"C:\Steam\common\cs2\game\bin\win64\cs2.exe"), "engine folders skipped, same name once");
            Check.Equal("Valorant (VALORANT-Win64-Shipping)", WindowsGames.DisplayName(@"D:\Games\Valorant\VALORANT-Win64-Shipping.exe"), "game folder");
            Runner.Note("Windows recognizes: " + string.Join(", ", WindowsGames.Known().Select(WindowsGames.DisplayName)));
        });

        Runner.Test("Background apps: which processes are limited", () =>
        {
            var skipNames = new HashSet<string>(["cs2", "Discord"], StringComparer.OrdinalIgnoreCase);
            var skipIds = new HashSet<int> { 500, 501 };
            bool Candidate(ProcessInfo p) => BackgroundLimiter.IsCandidate(p, 1, 99, skipNames, skipIds);
            Check.True(Candidate(Proc(100, "chrome")), "an ordinary app");
            Check.True(!Candidate(Proc(101, "chrome", session: 0)) && !Candidate(Proc(102, "chrome", session: 2)), "other sessions");
            Check.True(!Candidate(Proc(99, "PerformanceBooster")), "the app itself");
            Check.True(!Candidate(Proc(103, "explorer")) && !Candidate(Proc(104, "audiodg")) && !Candidate(Proc(4, "System")), "system processes");
            Check.True(!Candidate(Proc(105, "discord")) && !Candidate(Proc(106, "CS2")), "exempt list and games, case insensitive");
            Check.True(!Candidate(Proc(500, "cs2")) && !Candidate(Proc(501, "crashhandler")), "the game and what it started");
        });

        Runner.Test("Background apps: low priority and efficiency mode, then restored (fake process)", () =>
        {
            var fake = Fixtures.StartFake(Path.Combine(Runner.Data, "Background"), "pbbackground.exe");
            var info = Snapshot(fake);
            var limiter = new BackgroundLimiter();
            try
            {
                Check.Equal(1, limiter.Limit([info]), "limited");
                var (priority, eco) = BackgroundLimiter.Inspect(fake.Id);
                Check.Equal(Native.IDLE_PRIORITY_CLASS, priority, "low priority");
                if (eco is not null) Check.True(eco.Value, "efficiency mode");
                Check.True(File.Exists(BackgroundLimiter.StateFile), "saved in case the app closes unexpectedly");
                Check.Equal(1, limiter.Limit([info]), "not limited twice");
                Check.Equal(0, limiter.Limit([]), "no longer given (added to the exempt list)");
                Check.Equal(Native.NORMAL_PRIORITY_CLASS, BackgroundLimiter.Inspect(fake.Id).Priority, "put back at once");
                Check.Equal(1, limiter.Limit([info]), "limited again");
            }
            finally
            {
                limiter.RestoreAll();
            }
            var (restored, ecoAfter) = BackgroundLimiter.Inspect(fake.Id);
            Check.Equal(Native.NORMAL_PRIORITY_CLASS, restored, "normal priority again");
            if (ecoAfter is not null) Check.True(!ecoAfter.Value, "efficiency mode off again");
            Check.True(!File.Exists(BackgroundLimiter.StateFile), "nothing left to restore");
        });

        Runner.Test("Background apps: restored at the next start after an unexpected exit", () =>
        {
            var fake = Fixtures.StartFake(Path.Combine(Runner.Data, "Background"), "pbleftover.exe");
            new BackgroundLimiter().Limit([Snapshot(fake)]); // never restored, like a crash
            Check.Equal(Native.IDLE_PRIORITY_CLASS, BackgroundLimiter.Inspect(fake.Id).Priority, "limited");
            BackgroundLimiter.RestoreLeftovers();
            Check.Equal(Native.NORMAL_PRIORITY_CLASS, BackgroundLimiter.Inspect(fake.Id).Priority, "restored");
            Check.True(!File.Exists(BackgroundLimiter.StateFile), "list removed");
        });

        Runner.Test("Background apps: moved to the background cores and back, also after an unexpected exit (fake process)", () =>
        {
            var cores = CpuTopology.Read().Take(2).Select(c => c.Id).ToList();
            Check.True(cores.Count == 2, "this computer's CPU sets");
            var fake = Fixtures.StartFake(Path.Combine(Runner.Data, "Background"), "pbcores.exe");
            var limiter = new BackgroundLimiter();
            try
            {
                Check.Equal(1, limiter.Limit([Snapshot(fake)], cores), "limited");
                Check.True(BackgroundLimiter.Cores(fake.Id)?.Order().SequenceEqual(cores.Order()) == true, "runs on the background cores only");
            }
            finally
            {
                limiter.RestoreAll();
            }
            Check.True(BackgroundLimiter.Cores(fake.Id) is { Length: 0 }, "all cores again");

            new BackgroundLimiter().Limit([Snapshot(fake)], cores); // never restored, like a crash
            BackgroundLimiter.RestoreLeftovers();
            Check.True(BackgroundLimiter.Cores(fake.Id) is { Length: 0 }, "all cores again at the next start");
        });

        Runner.Test("Cores for the background: efficiency cores, the smaller cache, nothing when all are alike", () =>
        {
            static CpuTopology.Cpu Cpu(uint id, int core, int cls, long l3Mb) => new(id, 0, (int)id, core, cls, l3Mb << 20);
            // 2 performance cores with two threads each, 4 efficiency cores.
            var hybrid = CpuTopology.Plan([Cpu(0, 0, 1, 24), Cpu(1, 0, 1, 24), Cpu(2, 1, 1, 24), Cpu(3, 1, 1, 24), Cpu(4, 2, 0, 24), Cpu(5, 3, 0, 24), Cpu(6, 4, 0, 24), Cpu(7, 5, 0, 24)]);
            Check.True(hybrid is { Kind: CpuTopology.Kind.Hybrid, GameCores: 2, BackgroundCores: 4 }, "hybrid: " + hybrid);
            Check.True(hybrid!.BackgroundSets.SequenceEqual(new uint[] { 4, 5, 6, 7 }), "the efficiency cores");
            // Two dies of 4 threads: 96 MB and 32 MB of L3.
            var cache = CpuTopology.Plan([Cpu(0, 0, 0, 96), Cpu(1, 1, 0, 96), Cpu(2, 2, 0, 96), Cpu(3, 3, 0, 96), Cpu(4, 4, 0, 32), Cpu(5, 5, 0, 32), Cpu(6, 6, 0, 32), Cpu(7, 7, 0, 32)]);
            Check.True(cache is { Kind: CpuTopology.Kind.Cache, GameCacheMb: 96, BackgroundCacheMb: 32 }, "two caches: " + cache);
            Check.True(cache!.BackgroundSets.SequenceEqual(new uint[] { 4, 5, 6, 7 }), "the die with the smaller cache");
            Check.Equal<CpuTopology.Layout?>(null, CpuTopology.Plan([Cpu(0, 0, 0, 32), Cpu(1, 1, 0, 32), Cpu(2, 2, 0, 32), Cpu(3, 3, 0, 32)]), "all alike");
            Runner.Note("This computer: " + (CpuTopology.Current?.ToString() ?? "all cores alike"));
        });

        Runner.Test("Last game: what held it back (samples, thresholds, saved)", () =>
        {
            static GpuSample Gpu(double temp, double usedMb, ulong reasons = 0) => new("GPU", 99, temp, 200, 2500, usedMb, 8192, reasons);
            static HealthSample Sample(GpuSample? gpu, double limit = 100, double freeMb = 8000, bool? ac = true) => new(gpu, limit, freeMb, 16384, 50, ac);
            var s = new GameSession { Game = "cs2" };
            s.Add(Sample(Gpu(70, 4000)));
            s.Add(Sample(Gpu(88, 8100, 0x20), limit: 80, freeMb: 500, ac: false));
            Check.Empty(s.Findings().Select(f => f.Key), "one sample is not enough");
            s.Add(Sample(Gpu(89, 8150, 0x40), limit: 85, freeMb: 600, ac: false));
            var keys = s.Findings().Select(f => f.Key).ToList();
            Check.True(keys.SequenceEqual(["gpu", "vram", "ram", "cpu", "battery"]), "all of them after two samples: " + string.Join(",", keys));
            Check.True(s.Findings()[0].Detail.Contains("89"), "highest temperature");
            var calm = new GameSession { Game = "cs2" };
            for (int i = 0; i < 5; i++) calm.Add(Sample(null, limit: double.NaN));
            Check.Empty(calm.Findings().Select(f => f.Key), "nothing without a GPU and with plenty of memory");
            Check.Equal(0, calm.GpuSamples, "no graphics card values");
            s.End = DateTime.Now;
            s.Save();
            Check.Equal(5, GameSession.Load()?.Findings().Count ?? 0, "saved for the next start");
        });

        Runner.Test("Per-game Windows settings: graphics card choice and compatibility flags (text only)", () =>
        {
            var pairs = GameCompat.Pairs("SwapEffectUpgradeEnable=1;GpuPreference=2;");
            Check.True(pairs["GpuPreference"] == "2" && pairs["SwapEffectUpgradeEnable"] == "1", "pairs");
            Check.True(GameCompat.Flags("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE").SequenceEqual(["RUNASADMIN", "DISABLEDXMAXIMIZEDWINDOWEDMODE"]), "flags without the tilde");
            Check.Empty(GameCompat.Flags(null), "no value");
        });

        Runner.Test("RAM cleaner: the emptying rule and the loop (fake memory)", () =>
        {
            Check.True(StandbyCleaner.ShouldPurge(new(2048, 500, 0, 16384), 1024, 1024), "large list, little free memory");
            Check.True(!StandbyCleaner.ShouldPurge(new(512, 500, 0, 16384), 1024, 1024), "small list");
            Check.True(!StandbyCleaner.ShouldPurge(new(4096, 8000, 0, 16384), 1024, 4096), "enough free memory");

            double standby = 2048;
            int rounds = 0, purges = 0;
            var reports = new List<CleanerStatus>();
            using var purgeNow = new AutoResetEvent(false);
            StandbyCleaner.Loop(
                sample: () => new MemoryLists(standby, 500, 0, 16384),
                purge: () =>
                {
                    purges++;
                    standby = 0;
                    return 0;
                },
                keepRunning: () => rounds++ < 3,
                purgeNow,
                limits: () => (1024, 1024),
                report: s =>
                {
                    reports.Add(s);
                    standby = 100;
                    if (reports.Count == 2) purgeNow.Set(); // "Empty now" during the game
                },
                interval: TimeSpan.FromMilliseconds(10));
            Check.Equal(2, purges, "emptied by the rule, then when asked");
            Check.True(reports[0] is { Purges: 1, LastFreedMb: 2048, StandbyMb: 0, Running: true, Error: null }, "first report: " + reports[0]);
            Check.True(reports[1].Purges == 1 && reports[2].Purges == 2, "the small list is left alone until asked");
            Check.True(reports[^1] is { Running: false, Purges: 2 }, "stopped: " + reports[^1]);

            rounds = 0;
            standby = 2048;
            reports.Clear();
            StandbyCleaner.Loop(() => new MemoryLists(standby, 500, 0, 16384), () => unchecked((int)0xC0000061), () => rounds++ < 1, purgeNow,
                () => (1024, 1024), reports.Add, TimeSpan.FromMilliseconds(1));
            Check.True(reports[0] is { Purges: 0, Error: not null } && reports[0].Error!.Contains("C0000061"), "refusal reported: " + reports[0].Error);
        });

        Runner.Test("RAM cleaner: scheduled task definition", () =>
        {
            const string exe = @"C:\Games & Tools\PerformanceBooster.exe";
            var doc = XDocument.Parse(StandbyCleaner.TaskXml(exe, "S-1-5-21-1-2-3-1001"));
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            string Value(string name) => doc.Descendants(ns + name).Single().Value;
            Check.Equal(exe, Value("Command"), "exe (escaped in the XML)");
            Check.Equal("--memory-cleaner", Value("Arguments"), "helper mode");
            Check.Equal("S-1-5-21-1-2-3-1001", Value("UserId"), "the user");
            Check.Equal("InteractiveToken", Value("LogonType"), "in the user's session");
            Check.Equal("HighestAvailable", Value("RunLevel"), "as administrator");
            Check.Equal("PT0S", Value("ExecutionTimeLimit"), "no time limit");
            Check.True(!doc.Descendants(ns + "Triggers").Any(), "no schedule of its own");

            // The sign-in task used when the app runs as administrator.
            var logon = XDocument.Parse(ScheduledTasks.Xml("x", exe, "--minimized", "S-1-5-21-1-2-3-1001", @"PC\Gamer"));
            Check.Equal(@"PC\Gamer", logon.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")?.Value, "starts at the user's sign-in");
            Check.Equal("--minimized", logon.Descendants(ns + "Arguments").Single().Value, "in the tray");
        });

        Runner.Test("Mouse: acceleration and speed are read (nothing is changed)", () =>
        {
            Check.True(MouseSettings.IsRecommended(new(false, 10)), "acceleration off, speed 10");
            Check.True(!MouseSettings.IsRecommended(new(true, 10)) && !MouseSettings.IsRecommended(new(false, 12)), "acceleration on or another speed");
            var state = MouseSettings.Read() ?? throw new TestFailure("the mouse settings could not be read");
            Check.True(state.Speed is >= 1 and <= 20, "speed 1-20: " + state.Speed);
            Runner.Note($"Enhance pointer precision: {state.Acceleration} · speed {state.Speed}");
        });

        Runner.Test("RAM cleaner: memory lists (emptying only when the tests run as administrator)", () =>
        {
            using var sampler = new MemorySampler();
            var m = sampler.Sample();
            Check.True(m.TotalMb > 1000 && m.FreeMb >= 0 && m.StandbyMb >= 0 && m.FreeMb + m.StandbyMb <= m.TotalMb + 1, $"lists: {m}");
            Check.True(StandbyCleaner.DefaultFreeBelowMb() >= 1024 && StandbyCleaner.DefaultFreeBelowMb() <= m.TotalMb / 2 + 256, "half of the memory");
            Runner.Note($"standby {m.StandbyMb:0} MB · free {m.FreeMb:0} MB · modified {m.ModifiedMb:0} MB · total {m.TotalMb:0} MB");
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                Check.Skip("not running as administrator; emptying was not tried");
            Check.Equal(0, StandbyCleaner.PurgeStandbyList(), "emptied");
        });
    }
}
