using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// While a game runs, the user's other apps get "Low" priority and Windows' efficiency mode (EcoQoS), the leaf icon in Task Manager:
// they run only when the game leaves the processor free, and on hybrid processors they move to the efficiency cores.
// Needs no administrator rights. Never touched: system processes, other users' processes, the game and every process it started,
// anti-cheat, overlays, voice chat and audio apps (AlwaysFullSpeed), the exempt list of the settings and apps that raised their own
// priority (audio tools, for example).
// Everything is put back when the game closes; the list is saved too, so a crash of the app does not leave apps slowed down.
// With background cores given (CpuTopology), the limited apps also run only on those, so the game has its cores to itself.
public sealed class BackgroundLimiter
{
    // CpuSetsChanged: the app was moved to the background cores (only apps that had not chosen cores of their own).
    internal sealed record Entry(int Id, long Create, string Name, uint Priority, uint? ThrottleControl, uint? ThrottleState, bool CpuSetsChanged = false);

    readonly Dictionary<int, Entry> _limited = [];
    readonly HashSet<(int, long)> _refused = [];

    internal static string StateFile => Path.Combine(AppPaths.Folder, "limited-apps.json");
    static readonly uint ThrottleSize = (uint)Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>();

    public int Count => _limited.Count;

    // Kept at full speed whatever the exempt list says: a game waits on its anti-cheat, and slowed overlays, voice chat and audio
    // apps stutter or crackle.
    public static readonly FrozenSet<string> AlwaysFullSpeed = new[]
    {
        "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "vgtray", "EAAntiCheat.GameServiceLauncher",
        "steam", "steamwebhelper", "GameOverlayUI", "EOSOverlayRenderer-Win64-Shipping", "NVIDIA Overlay", "NVIDIA Share", "RTSS", "MSIAfterburner",
        "GameBar", "GameBarFTServer",
        "Discord", "DiscordPTB", "DiscordCanary", "ts3client_win64", "TeamSpeak", "Mumble",
        "NVIDIA Broadcast", "voicemeeter", "voicemeeter_x64", "voicemeeterpro", "voicemeeterpro_x64", "voicemeeter8", "voicemeeter8x64",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsCandidate(ProcessInfo p, int session, int self, ISet<string> skipNames, ISet<int> skipIds) =>
        p.SessionId == session && p.Id != self && p.Id > 4 && !skipIds.Contains(p.Id) && !skipNames.Contains(p.Name)
        && !AlwaysFullSpeed.Contains(p.Name) && !ProcessSampler.IsProtected(p.Name);

    // Limits the given processes that are not limited yet. One no longer given (it exited, or was added to the exempt list
    // during the game) is put back. Returns how many are limited.
    public int Limit(IReadOnlyCollection<ProcessInfo> processes, IReadOnlyList<uint>? backgroundCores = null)
    {
        bool changed = false;
        var given = processes.Select(p => (p.Id, p.CreateTime)).ToHashSet();
        foreach (var entry in _limited.Values.Where(e => !given.Contains((e.Id, e.Create))).ToList())
        {
            Restore(entry); // does nothing for a process that exited
            changed |= _limited.Remove(entry.Id);
        }
        foreach (var p in processes)
        {
            if (_limited.TryGetValue(p.Id, out var known) && known.Create == p.CreateTime) continue;
            if (_refused.Contains((p.Id, p.CreateTime))) continue;
            if (TryLimit(p, backgroundCores) is { } entry)
            {
                _limited[p.Id] = entry;
                changed = true;
            }
            else _refused.Add((p.Id, p.CreateTime));
        }
        if (changed) Save();
        return _limited.Count;
    }

    public void RestoreAll()
    {
        foreach (var entry in _limited.Values) Restore(entry);
        _limited.Clear();
        _refused.Clear();
        Save();
    }

    // Apps left slowed down by a previous run that ended unexpectedly.
    public static void RestoreLeftovers()
    {
        var entries = AppPaths.ReadJson(StateFile, AppJson.Default.LimitedApps) ?? [];
        foreach (var entry in entries) Restore(entry);
        try { File.Delete(StateFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void Save()
    {
        try
        {
            if (_limited.Count == 0) File.Delete(StateFile);
            else AppPaths.WriteJson(StateFile, _limited.Values.ToList(), AppJson.Default.LimitedApps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    const uint Access = Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION;

    static Entry? TryLimit(ProcessInfo p, IReadOnlyList<uint>? cores)
    {
        var handle = Native.OpenProcess(Access, false, p.Id);
        if (handle == IntPtr.Zero) return null; // an app running as administrator, or a protected one
        try
        {
            uint priority = Native.GetPriorityClass(handle);
            // An app that raised its own priority (audio, streaming) asked for it on purpose; one that is already idle needs nothing.
            if (priority is not (Native.NORMAL_PRIORITY_CLASS or Native.BELOW_NORMAL_PRIORITY_CLASS)) return null;
            var before = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            bool known = Native.GetProcessInformation(handle, Native.ProcessPowerThrottling, ref before, ThrottleSize);
            if (!Native.SetPriorityClass(handle, Native.IDLE_PRIORITY_CLASS)) return null;
            var eco = new Native.PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1, ControlMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED, StateMask = Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            };
            Native.SetProcessInformation(handle, Native.ProcessPowerThrottling, ref eco, ThrottleSize);
            // An app that chose its own cores is left on them.
            uint[]? ownCores = DefaultCores(handle);
            bool moved = cores is { Count: > 0 } && ownCores is { Length: 0 } && Native.SetProcessDefaultCpuSets(handle, [.. cores], (uint)cores.Count);
            return new Entry(p.Id, p.CreateTime, p.Name, priority, known ? before.ControlMask : null, known ? before.StateMask : null, moved);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    static void Restore(Entry e)
    {
        var handle = Native.OpenProcess(Access, false, e.Id);
        if (handle == IntPtr.Zero) return;
        try
        {
            // The same process ID may belong to a new process by now.
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _) || created != e.Create) return;
            Native.SetPriorityClass(handle, e.Priority);
            // Without the earlier state, Windows decides again (the default).
            var state = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = e.ThrottleControl ?? 0, StateMask = e.ThrottleState ?? 0 };
            Native.SetProcessInformation(handle, Native.ProcessPowerThrottling, ref state, ThrottleSize);
            if (e.CpuSetsChanged) Native.SetProcessDefaultCpuSets(handle, null, 0); // all cores again
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    // The process's default CPU sets; empty = no choice of its own (all cores), null = unknown.
    static uint[]? DefaultCores(IntPtr handle)
    {
        if (Native.GetProcessDefaultCpuSets(handle, null, 0, out var required)) return [];
        if (required == 0) return null;
        var ids = new uint[required];
        return Native.GetProcessDefaultCpuSets(handle, ids, required, out _) ? ids : null;
    }

    // For tests: the default CPU sets of a process (empty = all cores).
    internal static uint[]? Cores(int id)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, id);
        if (handle == IntPtr.Zero) return null;
        try { return DefaultCores(handle); }
        finally { Native.CloseHandle(handle); }
    }

    // For tests: the process's priority class and whether efficiency mode is on (null if Windows does not report it).
    internal static (uint Priority, bool? Eco) Inspect(int id)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, id);
        if (handle == IntPtr.Zero) return (0, null);
        try
        {
            var state = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            bool known = Native.GetProcessInformation(handle, Native.ProcessPowerThrottling, ref state, ThrottleSize);
            return (Native.GetPriorityClass(handle), known ? (state.ControlMask & state.StateMask & Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 : null);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}
