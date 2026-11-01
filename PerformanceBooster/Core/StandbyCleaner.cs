namespace PerformanceBooster.Core;

public readonly record struct MemoryLists(double StandbyMb, double FreeMb, double ModifiedMb, double TotalMb);

public sealed record CleanerStatus(DateTime At, bool Running, double StandbyMb, double FreeMb, int Purges, DateTime? LastPurge, double LastFreedMb, string? Error);

// Windows' memory lists from performance counters (no administrator rights needed): the standby list (cache of recently used files
// and closed apps, which Windows hands out again when needed) and the free + zeroed pages.
public sealed class MemorySampler : IDisposable
{
    IntPtr _query;
    readonly IntPtr[] _standby;
    readonly IntPtr _free, _modified;

    public MemorySampler()
    {
        _standby = [];
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        _standby = [Add(@"\Memory\Standby Cache Core Bytes"), Add(@"\Memory\Standby Cache Normal Priority Bytes"), Add(@"\Memory\Standby Cache Reserve Bytes")];
        _free = Add(@"\Memory\Free & Zero Page List Bytes");
        _modified = Add(@"\Memory\Modified Page List Bytes");
    }

    IntPtr Add(string path) => Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public MemoryLists Sample()
    {
        double total = SystemInfo.Memory().TotalGb * 1024;
        if (_query == IntPtr.Zero || Native.PdhCollectQueryData(_query) != 0) return new(double.NaN, double.NaN, double.NaN, total);
        return new(_standby.Sum(Mb), Mb(_free), Mb(_modified), total);
    }

    static double Mb(IntPtr counter) =>
        counter != IntPtr.Zero && Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE, out _, out var v) == 0 && v.CStatus <= 1
            ? v.DoubleValue / 1048576.0
            : double.NaN;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

// Empties the standby list while a game runs: when the list is at least
// StandbyAtLeastMb and free memory is below FreeBelowMb, checked every second.
// Emptying needs administrator rights. A scheduled task created once (with one approval) starts the app as administrator with
// --memory-cleaner when a game starts, without asking again; the main app signals it through two events in the user's session
// (run, empty now) and the helper writes its state to cleaner-status.json. The helper exits when the game or the app closes.
public static class StandbyCleaner
{
    public const string TaskName = "PerformanceBooster Memory Cleaner";
    const int SystemMemoryListInformation = 0x50, MemoryPurgeStandbyList = 4;

    internal static string StatusFile => Path.Combine(AppPaths.Folder, "cleaner-status.json");
    internal static string RunEvent => AppPaths.LocalName("Cleaner.Run");
    internal static string PurgeEvent => AppPaths.LocalName("Cleaner.PurgeNow");

    // The task starts the exe it was created for; after the app was moved it has to be created again.
    public static bool TaskReady(AppSettings settings) =>
        settings.CleanerTaskExe is { } exe && exe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

    // When the app itself runs as administrator, it empties the list without a task.
    public static bool Ready(AppSettings settings) => Elevated.IsAdmin || TaskReady(settings);

    public static bool ShouldPurge(MemoryLists m, int standbyAtLeastMb, int freeBelowMb) => m.StandbyMb >= standbyAtLeastMb && m.FreeMb < freeBelowMb;

    // Half of the memory is a good free memory limit.
    public static int DefaultFreeBelowMb()
    {
        double totalMb = SystemInfo.Memory().TotalGb * 1024;
        return totalMb <= 0 ? 4096 : Math.Max(1024, (int)(totalMb / 2 / 256) * 256);
    }

    public static CleanerStatus? ReadStatus() => AppPaths.ReadJson(StatusFile, AppJson.Default.CleanerStatus);

    static void WriteStatus(CleanerStatus status)
    {
        try { AppPaths.WriteJson(StatusFile, status, AppJson.Default.CleanerStatus); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // --- Emptying (administrator) ---

    // NTSTATUS: 0 = done; 0xC0000061 = the privilege is missing (not running as administrator).
    internal static int PurgeStandbyList()
    {
        if (!Privilege.Enable("SeProfileSingleProcessPrivilege")) return unchecked((int)0xC0000061);
        int command = MemoryPurgeStandbyList;
        return Native.NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
    }

    static string Refused(int status) => F("Windows refused to empty the standby list (0x{0:X8}).", status);

    // The cleaner's loop: purges when the rule holds or when asked, reports every round. Exits when keepRunning turns false.
    internal static void Loop(Func<MemoryLists> sample, Func<int> purge, Func<bool> keepRunning, WaitHandle purgeNow,
        Func<(int StandbyAtLeastMb, int FreeBelowMb)> limits, Action<CleanerStatus> report, TimeSpan interval, CleanerStatus? previous = null)
    {
        int purges = previous?.Purges ?? 0;
        DateTime? last = previous?.LastPurge;
        double freed = previous?.LastFreedMb ?? 0;
        string? error = null;
        var m = sample();
        bool asked = false;
        while (keepRunning())
        {
            m = sample();
            var (atLeast, below) = limits();
            if (asked || ShouldPurge(m, atLeast, below))
            {
                int status = purge();
                if (status == 0)
                {
                    var after = sample();
                    purges++;
                    last = DateTime.Now;
                    freed = Math.Max(0, m.StandbyMb - after.StandbyMb);
                    m = after;
                    error = null;
                }
                else error = Refused(status);
            }
            report(new CleanerStatus(DateTime.Now, true, m.StandbyMb, m.FreeMb, purges, last, freed, error));
            asked = purgeNow.WaitOne(interval);
        }
        report(new CleanerStatus(DateTime.Now, false, m.StandbyMb, m.FreeMb, purges, last, freed, error));
    }

    // --memory-cleaner: the copy the scheduled task starts as administrator.
    public static int RunHelper()
    {
        using var single = new Mutex(false, AppPaths.LocalName("Cleaner"));
        bool owned;
        try { owned = single.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;
        try
        {
            using var sampler = new MemorySampler();
            var previous = ReadStatus();
            // Without the run signal (no game): "Empty now" was pressed; empty once and exit.
            if (!EventWaitHandle.TryOpenExisting(RunEvent, out var run))
                return PurgeOnce(sampler, previous) ? 0 : 1;
            using (run)
            {
                using var purgeNow = EventWaitHandle.TryOpenExisting(PurgeEvent, out var asked) ? asked : new EventWaitHandle(false, EventResetMode.AutoReset);
                var settings = new SettingsFile();
                Loop(sampler.Sample, PurgeStandbyList, () => run.WaitOne(0) && MainAppRunning() && settings.Current.CleanStandby, purgeNow,
                    () => (settings.Current.StandbyAtLeastMb, settings.Current.FreeBelowMb), WriteStatus, TimeSpan.FromSeconds(1), previous);
            }
            return 0;
        }
        finally
        {
            single.ReleaseMutex();
        }
    }

    static bool PurgeOnce(MemorySampler sampler, CleanerStatus? previous)
    {
        var before = sampler.Sample();
        int status = PurgeStandbyList();
        var after = sampler.Sample();
        bool ok = status == 0;
        WriteStatus(new CleanerStatus(DateTime.Now, false, after.StandbyMb, after.FreeMb, (previous?.Purges ?? 0) + (ok ? 1 : 0),
            ok ? DateTime.Now : previous?.LastPurge, ok ? Math.Max(0, before.StandbyMb - after.StandbyMb) : previous?.LastFreedMb ?? 0, ok ? null : Refused(status)));
        return ok;
    }

    // Elevated copy without the task (--purge-standby): one emptying, reported in the log.
    internal static int PurgeOnce(List<string> log)
    {
        using var sampler = new MemorySampler();
        bool ok = PurgeOnce(sampler, ReadStatus());
        var status = ReadStatus();
        log.Add(ok ? F("Standby list emptied: {0} freed.", Maintenance.Size((long)((status?.LastFreedMb ?? 0) * 1048576))) : status?.Error ?? Refused(-1));
        return ok ? 0 : 1;
    }

    static bool MainAppRunning()
    {
        if (!Mutex.TryOpenExisting(AppPaths.LocalName("Instance"), out var instance)) return false;
        instance.Dispose();
        return true;
    }

    // Settings are read again when the main app saves them (thresholds changed during the game).
    sealed class SettingsFile
    {
        DateTime _stamp;
        AppSettings _current = AppSettings.Load();

        public AppSettings Current
        {
            get
            {
                var stamp = File.Exists(AppPaths.Settings) ? File.GetLastWriteTimeUtc(AppPaths.Settings) : default;
                if (stamp != _stamp)
                {
                    _stamp = stamp;
                    _current = AppSettings.Load();
                }
                return _current;
            }
        }
    }

    // --- Scheduled task ---

    // No trigger: the app starts it on demand.
    internal static string TaskXml(string exe, string userSid) =>
        ScheduledTasks.Xml("Performance Booster: empties the standby list while a game runs. The app starts it; it has no schedule of its own.",
            exe, "--memory-cleaner", userSid, logonUser: null);

    // --memory-task install|remove (elevated copy).
    internal static int Task(string action, List<string> log)
    {
        if (action == "remove")
        {
            bool removed = ScheduledTasks.Delete(TaskName);
            log.Add(removed ? T("The scheduled task of the RAM cleaner was removed.") : T("The scheduled task could not be removed."));
            return removed ? 0 : 1;
        }
        bool created = ScheduledTasks.Create(TaskName, TaskXml(Environment.ProcessPath!, ScheduledTasks.CurrentUserSid));
        log.Add(created ? T("The scheduled task of the RAM cleaner was created; it starts without asking when a game starts.") : T("The scheduled task could not be created."));
        return created ? 0 : 1;
    }

    // Starts the cleaner (works without administrator rights once the task exists).
    public static bool RunTask() => ScheduledTasks.Start(TaskName);

    // "Empty now" inside the app when it runs as administrator.
    public static bool PurgeNow()
    {
        using var sampler = new MemorySampler();
        return PurgeOnce(sampler, ReadStatus());
    }

    // The loop inside the app when it runs as administrator; stops when the game ends or RAM cleaning is turned off.
    internal static void RunInProcess(WaitHandle run, WaitHandle purgeNow, AppSettings settings)
    {
        using var sampler = new MemorySampler();
        Loop(sampler.Sample, PurgeStandbyList, () => run.WaitOne(0) && settings.CleanStandby, purgeNow,
            () => (settings.StandbyAtLeastMb, settings.FreeBelowMb), WriteStatus, TimeSpan.FromSeconds(1), ReadStatus());
    }
}

// The cleaner while one game runs: the signals the cleaner watches. When the app runs as administrator the cleaner is a thread
// of the app; otherwise it is the elevated helper the scheduled task starts. Disposing stops it.
public sealed class CleanerSession : IDisposable
{
    readonly EventWaitHandle _run = new(true, EventResetMode.ManualReset, StandbyCleaner.RunEvent);
    readonly EventWaitHandle _purgeNow = new(false, EventResetMode.AutoReset, StandbyCleaner.PurgeEvent);
    Thread? _worker;

    public bool? Started { get; private set; }

    CleanerSession() => _run.Set(); // a helper of an earlier game may still hold the event

    public static CleanerSession Start(AppSettings settings)
    {
        var session = new CleanerSession();
        if (Elevated.IsAdmin)
        {
            session._worker = new Thread(() => StandbyCleaner.RunInProcess(session._run, session._purgeNow, settings)) { IsBackground = true, Name = "RAM cleaner" };
            session._worker.Start();
            session.Started = true;
        }
        else _ = Task.Run(() => session.Started = StandbyCleaner.RunTask());
        return session;
    }

    public void PurgeNow() => _purgeNow.Set();

    public void Dispose()
    {
        _run.Reset();
        _purgeNow.Set(); // wakes the cleaner, which then sees the run signal is gone and stops
        _worker?.Join(TimeSpan.FromSeconds(3));
        _run.Dispose();
        _purgeNow.Dispose();
    }
}
