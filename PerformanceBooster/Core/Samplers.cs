using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

public sealed class History(int capacity)
{
    readonly Queue<double> _values = new(capacity);

    public int Count => _values.Count;

    public void Add(double value)
    {
        if (_values.Count == capacity) _values.Dequeue();
        _values.Enqueue(value);
    }

    public double[] Snapshot() => _values.ToArray();
}

public sealed class CpuSampler
{
    ulong _idle, _total;

    // Average usage of all cores (%). GetSystemTimes instead of language-dependent performance counters.
    public double Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong i = idle.Value, t = kernel.Value + user.Value;
        ulong di = i - _idle, dt = t - _total;
        _idle = i;
        _total = t;
        return dt == 0 ? 0 : Math.Clamp((1.0 - (double)di / dt) * 100.0, 0, 100);
    }
}

public static class SystemInfo
{
    public static (double UsedGb, double TotalGb, double Percent) Memory()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (!Native.GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (used, total, used / total * 100.0);
    }

    public static bool? OnAcPower()
    {
        if (!Native.GetSystemPowerStatus(out var s)) return null;
        return s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
    }

    // The exact refresh rate of the active display whose rate is closest to the rounded one (59.94, 143.98...), or null.
    public static double? ExactRefreshRate(int rounded)
    {
        if (rounded <= 0 || Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0 || pathCount == 0) return null;
        var paths = Marshal.AllocHGlobal((int)pathCount * Native.DisplayPathSize);
        var modes = Marshal.AllocHGlobal((int)Math.Max(1, modeCount) * Native.DisplayModeSize);
        try
        {
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
            double? best = null;
            for (int i = 0; i < pathCount; i++)
            {
                uint numerator = (uint)Marshal.ReadInt32(paths, i * Native.DisplayPathSize + Native.DisplayPathRefreshOffset);
                uint denominator = (uint)Marshal.ReadInt32(paths, i * Native.DisplayPathSize + Native.DisplayPathRefreshOffset + 4);
                if (denominator == 0) continue;
                double rate = (double)numerator / denominator;
                if (Math.Abs(rate - rounded) < 1 && (best is null || Math.Abs(rate - rounded) < Math.Abs(best.Value - rounded))) best = rate;
            }
            return best;
        }
        finally
        {
            Marshal.FreeHGlobal(paths);
            Marshal.FreeHGlobal(modes);
        }
    }

    // Windows lists fractional rates rounded down next to the whole ones (59 next to 60, 143 next to 144):
    // they are the same refresh rate, so only a mode at least 1 Hz faster counts as a higher one.
    public static bool BelowMaxRefresh(int rounded, double? exact, int max) => exact is { } e ? max - e >= 1 : max > rounded + 1;

    public static unsafe (int Current, int Max, int Width, int Height) RefreshRate()
    {
        var dm = new Native.DEVMODE { dmSize = (short)sizeof(Native.DEVMODE) };
        if (!Native.EnumDisplaySettings(null, Native.ENUM_CURRENT_SETTINGS, ref dm)) return (0, 0, 0, 0);
        int current = dm.dmDisplayFrequency, width = dm.dmPelsWidth, height = dm.dmPelsHeight, max = current;
        var mode = new Native.DEVMODE { dmSize = (short)sizeof(Native.DEVMODE) };
        for (int i = 0; Native.EnumDisplaySettings(null, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height) max = Math.Max(max, mode.dmDisplayFrequency);
        }
        return (current, max, width, height);
    }
}

public static class NetInfo
{
    // The adapter carrying the default network: a working adapter with an IPv4 gateway; a cable first if there is one.
    public static NetworkInterface? Active()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => Gateway(n) is not null)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public static string? Gateway(NetworkInterface n) =>
        n.GetIPProperties().GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            ?.ToString();

    // The first IPv4 DNS server the adapter uses.
    public static string? Dns(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

public sealed class NetSampler
{
    string? _id;
    long _rx, _tx;
    DateTime _at;

    // Download / upload rate of the active adapter (Mbit/s).
    public (double DownMbps, double UpMbps) Sample(NetworkInterface? n)
    {
        if (n is null) return (0, 0);
        IPInterfaceStatistics stats;
        try { stats = n.GetIPStatistics(); }
        catch (NetworkInformationException) { return (0, 0); }
        var now = DateTime.UtcNow;
        long rx = stats.BytesReceived, tx = stats.BytesSent;
        (double, double) result = (0, 0);
        if (_id == n.Id)
        {
            double seconds = (now - _at).TotalSeconds;
            if (seconds > 0) result = (Math.Max(0, rx - _rx) * 8 / seconds / 1e6, Math.Max(0, tx - _tx) * 8 / seconds / 1e6);
        }
        _id = n.Id;
        _rx = rx;
        _tx = tx;
        _at = now;
        return result;
    }
}

public sealed record CounterSample(double GpuPercent, double GpuMemoryGb, double DiskActivePercent, double DiskMBps, double CpuMhz);

// Windows performance counters. Added with English names, so they also work on Turkish Windows.
// GPU usage comes from the same source as Task Manager for every vendor (NVIDIA/AMD/Intel).
public sealed class CounterSampler : IDisposable
{
    IntPtr _query;
    readonly IntPtr _gpu, _gpuMemory, _diskIdle, _diskBytes, _cpuPerformance;
    readonly int _baseMhz = Cpu.BaseMhz();
    readonly Dictionary<long, double> _perAdapter = [];
    IntPtr _items;
    uint _itemsSize;

    public CounterSampler()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        _gpu = Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        _gpuMemory = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _diskIdle = Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _diskBytes = Add(@"\PhysicalDisk(_Total)\Disk Bytes/sec");
        _cpuPerformance = Add(@"\Processor Information(_Total)\% Processor Performance");
        Native.PdhCollectQueryData(_query);
    }

    IntPtr Add(string path) => Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public CounterSample Sample()
    {
        if (_query == IntPtr.Zero) return new(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        Native.PdhCollectQueryData(_query);

        // GPU: the 3D engines of each graphics card (luid) are summed and the busiest card is taken.
        _perAdapter.Clear();
        double vram = double.NaN;
        Read(_gpu, (name, value) =>
        {
            ref var sum = ref CollectionsMarshal.GetValueRefOrAddDefault(_perAdapter, LuidKey(name), out _);
            sum += value;
        });
        Read(_gpuMemory, (_, value) => vram = double.IsNaN(vram) ? value : Math.Max(vram, value));
        double gpu = double.NaN;
        foreach (var sum in _perAdapter.Values) gpu = double.IsNaN(gpu) ? sum : Math.Max(gpu, sum);
        vram /= 1073741824.0;
        double idle = Single(_diskIdle);
        double perf = Single(_cpuPerformance);
        return new(
            double.IsNaN(gpu) ? double.NaN : Math.Min(gpu, 100),
            vram,
            double.IsNaN(idle) ? double.NaN : Math.Clamp(100 - idle, 0, 100),
            Single(_diskBytes) / 1048576.0,
            double.IsNaN(perf) || _baseMhz == 0 ? double.NaN : _baseMhz * perf / 100.0);
    }

    // "pid_1234_luid_0x00000000_0x0000C5E3_phys_0_eng_0_engtype_3D" -> the adapter's LUID as a number.
    internal static long LuidKey(ReadOnlySpan<char> instance)
    {
        int i = instance.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || instance.Length < i + 26) return 0;
        var luid = instance.Slice(i + 7, 19); // 00000000_0x0000C5E3
        return uint.TryParse(luid[..8], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high)
               && uint.TryParse(luid[11..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low)
            ? (long)high << 32 | low
            : 0;
    }

    delegate void ItemReader(ReadOnlySpan<char> name, double value);

    // The instances of a counter, read from a buffer kept between samples (PDH_FMT_COUNTERVALUE_ITEM_W: name pointer 8,
    // CStatus 4, padding 4, double 8); the names are not copied into strings.
    unsafe void Read(IntPtr counter, ItemReader item)
    {
        if (counter == IntPtr.Zero) return;
        uint size = _itemsSize;
        uint status = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out var count, _items);
        if (status == Native.PDH_MORE_DATA)
        {
            if (_items != IntPtr.Zero) NativeMemory.Free((void*)_items);
            _itemsSize = size + 4096;
            _items = (IntPtr)NativeMemory.Alloc(_itemsSize);
            size = _itemsSize;
            status = Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, _items);
        }
        if (status != 0) return;
        var p = (byte*)_items;
        for (int i = 0; i < count; i++, p += 24)
        {
            if (*(uint*)(p + 8) > 1) continue;
            item(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(*(char**)p), *(double*)(p + 16));
        }
    }

    double Single(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        return Native.PdhGetFormattedCounterValue(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus <= 1
            ? v.DoubleValue
            : double.NaN;
    }

    internal static List<(string Name, double Value)> Array(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint size = 0;
        if (Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero) != Native.PDH_MORE_DATA || size == 0)
            return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArray(counter, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer (8) + CStatus (4) + padding (4) + double (8)
            for (int i = 0; i < count; i++)
            {
                int offset = i * 24;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, offset)) ?? "";
                uint status = (uint)Marshal.ReadInt32(buffer, offset + 8);
                if (status > 1) continue;
                result.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, offset + 16))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public unsafe void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        if (_items != IntPtr.Zero) NativeMemory.Free((void*)_items);
        _items = IntPtr.Zero;
    }
}

// Graphics usage per process: the process's busiest engine type (3D, copy, video...)
// and its dedicated video memory. Only created while the details view is open.
public sealed class GpuProcessSampler : IDisposable
{
    IntPtr _query;
    readonly IntPtr _engines, _memory, _adapters;

    public GpuProcessSampler()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        _engines = Add(@"\GPU Engine(*)\Utilization Percentage");
        _memory = Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _adapters = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        Native.PdhCollectQueryData(_query);
    }

    IntPtr Add(string path) => Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    public Dictionary<int, (double Percent, long Vram)> Sample()
    {
        if (_query == IntPtr.Zero) return [];
        Native.PdhCollectQueryData(_query);
        return Combine(CounterSampler.Array(_engines), CounterSampler.Array(_memory), CounterSampler.Array(_adapters));
    }

    // Engine names: pid_1234_luid_0x00000000_0x0000C5E3_phys_0_eng_0_engtype_3D; memory: pid_1234_luid_0x00000000_0x0000C5E3_phys_0;
    // adapters: luid_0x00000000_0x0000C5E3_phys_0.
    // Video memory of a process: the adapter it uses most, not the sum over adapters (a game is also listed on the integrated GPU
    // or under a second ID of the same card, and the sum then exceeded the card's memory), and never more than that adapter uses in total.
    internal static Dictionary<int, (double Percent, long Vram)> Combine(
        IReadOnlyList<(string Name, double Value)> engines, IReadOnlyList<(string Name, double Value)> memory, IReadOnlyList<(string Name, double Value)> adapters)
    {
        var result = new Dictionary<int, (double, long)>();
        var perEngine = new Dictionary<(int Pid, string Engine), double>();
        foreach (var (name, value) in engines)
        {
            if (Pid(name) is not int pid) continue;
            int e = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            var key = (pid, e < 0 ? "" : name[(e + 8)..]);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        foreach (var g in perEngine.GroupBy(kv => kv.Key.Pid))
            result[g.Key] = (Math.Min(100, g.Max(kv => kv.Value)), 0);

        var adapterUse = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in adapters)
            if (Adapter(name) is { } luid) adapterUse[luid] = adapterUse.GetValueOrDefault(luid) + value;
        var perAdapter = new Dictionary<(int Pid, string Luid), double>();
        foreach (var (name, value) in memory)
        {
            if (Pid(name) is not int pid || Adapter(name) is not { } luid) continue;
            perAdapter[(pid, luid)] = perAdapter.GetValueOrDefault((pid, luid)) + value;
        }
        foreach (var g in perAdapter.GroupBy(kv => kv.Key.Pid))
        {
            double vram = g.Max(kv => adapterUse.TryGetValue(kv.Key.Luid, out var total) ? Math.Min(kv.Value, total) : kv.Value);
            var (percent, _) = result.GetValueOrDefault(g.Key);
            result[g.Key] = (percent, (long)Math.Max(0, vram));
        }
        return result;
    }

    // "luid_0x00000000_0x0000C5E3" of an instance name.
    internal static string? Adapter(string instance)
    {
        int start = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int end = instance.IndexOf("_phys", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? instance[start..] : instance[start..end];
    }

    internal static int? Pid(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return null;
        int end = instance.IndexOf('_', 4);
        return int.TryParse(end < 0 ? instance[4..] : instance[4..end], out var pid) ? pid : null;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

public sealed record ProcessUsage(int Id, string Name, int Count, double RamMb, double CpuPercent, bool CanKill);

public sealed class ProcessSampler
{
    // Processes whose termination breaks Windows or signs the user out.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "LsaIso",
        "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
        "ShellExperienceHost", "ShellHost", "SearchHost", "SearchIndexer", "LockApp", "TextInputHost", "audiodg", "conhost", "dllhost",
        "WmiPrvSE", "spoolsv", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "smartscreen", "WUDFHost",
        "ApplicationFrameHost", "UserOOBEBroker", "SgrmBroker", "MpDefenderCoreService", AppPaths.Id,
    };

    public static bool IsProtected(string name) => Protected.Contains(name);

    readonly ProcessRates _rates = new();

    // Processes grouped by name (name x6), sorted by RAM (private working set).
    public List<ProcessUsage> Sample(int top) =>
        Group(_rates.Sample())
            .OrderByDescending(g => g.PrivateWorkingSet)
            .Take(top)
            .Select(g => new ProcessUsage(g.FirstId, g.Name, g.Count, g.PrivateWorkingSet / 1048576.0, g.CpuPercent, g.CanKill))
            .ToList();

    // Processes with the same name are combined. The Idle process is skipped; session 0 processes are system processes and cannot be closed.
    public static List<ProcessGroup> Group(IEnumerable<ProcessRates.Rate> rates)
    {
        var groups = new Dictionary<string, List<ProcessRates.Rate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rates)
        {
            if (r.Info.Id == 0) continue;
            if (!groups.TryGetValue(r.Info.Name, out var list)) groups[r.Info.Name] = list = [];
            list.Add(r);
        }
        return groups.Select(kv => new ProcessGroup(
                kv.Key, kv.Value[0].Info.Id, kv.Value.Count, kv.Value.Select(r => r.Info.Id).ToList(),
                kv.Value.Sum(r => r.CpuPercent), kv.Value.Sum(r => r.Info.PrivateWorkingSet),
                kv.Value.Sum(r => r.ReadPerSec), kv.Value.Sum(r => r.WritePerSec),
                CanKill: kv.Value.All(r => r.Info.SessionId != 0) && !IsProtected(kv.Key)))
            .ToList();
    }
}

public sealed record ProcessGroup(string Name, int FirstId, int Count, IReadOnlyList<int> Ids, double CpuPercent, long PrivateWorkingSet,
    double ReadPerSec, double WritePerSec, bool CanKill);
