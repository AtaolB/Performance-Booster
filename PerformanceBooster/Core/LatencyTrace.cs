using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PerformanceBooster.Core;

public sealed record DriverLatency(string Module, string? Description, long Dpcs, long Isrs, double MaxDpcUs, double MaxIsrUs, double TotalMs)
{
    public double MaxUs => Math.Max(MaxDpcUs, MaxIsrUs);
}

public sealed record CpuLatency(int Cpu, long Dpcs, long Isrs, double TotalMs);

// DpcHistogram: DPCs by execution time, see LatencyTrace.Buckets.
public sealed record LatencyResult(double Seconds, long Dpcs, long Isrs, double MaxDpcUs, string? MaxDpcModule, double MaxIsrUs, string? MaxIsrModule,
    long[] DpcHistogram, List<DriverLatency> Drivers, List<CpuLatency> Cpus, long EventsLost, string? Error)
{
    public static LatencyResult Failed(string error) => new(0, 0, 0, 0, null, 0, null, [], [], [], 0, error);
}

// Driver latency: the Windows kernel reports every deferred procedure call (DPC) and interrupt
// service routine (ISR) with its start time and routine address (ETW, SystemTraceProvider). The time each one ran is added
// up per driver and per processor. Needs administrator rights: runs in the elevated copy
// (--latency seconds result.json stopEvent); the app sets the named event to stop before the time is up.
public static partial class LatencyTrace
{
    // Upper limits of the DPC histogram in microseconds; the last bucket is everything above.
    public static readonly double[] Buckets = [10, 50, 100, 250, 500, 1000];

    public static readonly int[] Durations = [10, 30, 60, 120, 300];

    public static string StopEventName => AppPaths.LocalName("LatencyStop");

    internal static int RunCli(string secondsText, string file, string? stopEvent, List<string> log)
    {
        if (!int.TryParse(secondsText, out int seconds)) return 1;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopEvent ?? StopEventName);
        var result = Measure(TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 300)), stop);
        AppPaths.WriteJson(file, result, AppJson.Default.LatencyResult);
        if (result.Error is { } error)
        {
            log.Add($"{T("ERROR")} {error}");
            return 1;
        }
        log.Add($"{T("OK   ")} {F("{0} DPCs and {1} interrupts measured", result.Dpcs, result.Isrs)}");
        return 0;
    }

    // Runs until the time is up or until stop is set.
    public static LatencyResult Measure(TimeSpan duration, WaitHandle? stop = null)
    {
        // Windows 11 24H2 hides kernel addresses from processes without the debug privilege.
        Privilege.Enable("SeDebugPrivilege");
        var modules = KernelModules.Read();
        if (modules.Count == 0) return LatencyResult.Failed(T("The list of loaded drivers could not be read."));
        var aggregator = new LatencyAggregator(modules, Stopwatch.Frequency / 1_000_000.0);
        using var session = new KernelLatencySession(aggregator);
        if (!session.Start(out var error)) return LatencyResult.Failed(F("The kernel trace could not be started ({0}).", error));
        var clock = Stopwatch.StartNew();
        if (stop is null) Thread.Sleep(duration);
        else stop.WaitOne(duration);
        long lost = session.Stop();
        return aggregator.Result(clock.Elapsed.TotalSeconds, lost, KernelModules.Describe);
    }
}

// Adds up DPC and ISR execution times per driver (routine address -> loaded module) and per processor.
internal sealed partial class LatencyAggregator
{
    sealed class Totals
    {
        public long Dpcs, Isrs, MaxDpc, MaxIsr, Total;
    }

    readonly ulong[] _bases, _ends;
    readonly string[] _names;
    readonly Totals[] _modules;
    readonly List<Totals> _cpus = [];
    readonly long[] _histogram = new long[LatencyTrace.Buckets.Length + 1];
    readonly long[] _bucketTicks;
    readonly double _ticksPerUs;
    readonly long _limit;

    public LatencyAggregator(IReadOnlyList<KernelModule> modules, double ticksPerUs)
    {
        var sorted = modules.OrderBy(m => m.Base).ToArray();
        _bases = sorted.Select(m => m.Base).ToArray();
        _ends = sorted.Select(m => m.Base + m.Size).ToArray();
        _names = [.. sorted.Select(m => m.Name), "?"];
        _modules = _names.Select(_ => new Totals()).ToArray();
        _ticksPerUs = ticksPerUs;
        _bucketTicks = LatencyTrace.Buckets.Select(us => (long)(us * ticksPerUs)).ToArray();
        _limit = (long)(1_000_000 * ticksPerUs); // longer than a second: a broken timestamp, not a real DPC
    }

    public void Add(bool isr, int cpu, ulong routine, long ticks)
    {
        if (ticks < 0 || ticks > _limit) return;
        var m = _modules[Module(routine)];
        while (_cpus.Count <= cpu) _cpus.Add(new Totals());
        var c = _cpus[cpu];
        if (isr)
        {
            m.Isrs++;
            c.Isrs++;
            if (ticks > m.MaxIsr) m.MaxIsr = ticks;
        }
        else
        {
            m.Dpcs++;
            c.Dpcs++;
            if (ticks > m.MaxDpc) m.MaxDpc = ticks;
            int bucket = 0;
            while (bucket < _bucketTicks.Length && ticks >= _bucketTicks[bucket]) bucket++;
            _histogram[bucket]++;
        }
        m.Total += ticks;
        c.Total += ticks;
    }

    // The module whose image contains the address; the last index when none does.
    int Module(ulong address)
    {
        int i = Array.BinarySearch(_bases, address);
        if (i < 0) i = ~i - 1;
        return i >= 0 && address < _ends[i] ? i : _names.Length - 1;
    }

    public LatencyResult Result(double seconds, long lost, Func<string, string?> describe)
    {
        double Us(long ticks) => ticks / _ticksPerUs;
        var drivers = _modules.Select((t, i) => (Totals: t, Name: _names[i]))
            .Where(x => x.Totals.Dpcs + x.Totals.Isrs > 0)
            .Select(x => new DriverLatency(x.Name, x.Name == "?" ? null : describe(x.Name), x.Totals.Dpcs, x.Totals.Isrs,
                Math.Round(Us(x.Totals.MaxDpc), 2), Math.Round(Us(x.Totals.MaxIsr), 2), Math.Round(Us(x.Totals.Total) / 1000, 3)))
            .OrderByDescending(d => d.MaxUs)
            .ToList();
        var topDpc = drivers.MaxBy(d => d.MaxDpcUs);
        var topIsr = drivers.MaxBy(d => d.MaxIsrUs);
        return new LatencyResult(Math.Round(seconds, 1), drivers.Sum(d => d.Dpcs), drivers.Sum(d => d.Isrs),
            topDpc?.MaxDpcUs ?? 0, topDpc is { Dpcs: > 0 } ? topDpc.Module : null, topIsr?.MaxIsrUs ?? 0, topIsr is { Isrs: > 0 } ? topIsr.Module : null,
            (long[])_histogram.Clone(), drivers,
            _cpus.Select((c, i) => new CpuLatency(i, c.Dpcs, c.Isrs, Math.Round(Us(c.Total) / 1000, 3))).ToList(), lost, null);
    }
}

public sealed record KernelModule(ulong Base, uint Size, string Name, string Path);

internal static partial class KernelModules
{
    const int SystemModuleInformation = 11, ModuleSize = 296, NameOffset = 40;
    const uint InfoLengthMismatch = 0xC0000004;

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);

    // RTL_PROCESS_MODULES: count, then RTL_PROCESS_MODULE_INFORMATION entries (ImageBase 16, ImageSize 24,
    // OffsetToFileName 38, FullPathName 40, 256 ANSI characters).
    public static IReadOnlyList<KernelModule> Read()
    {
        int length = 256 * 1024;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                int status = NtQuerySystemInformation(SystemModuleInformation, buffer, length, out int needed);
                if (unchecked((uint)status) == InfoLengthMismatch)
                {
                    length = Math.Max(length * 2, needed + 4096);
                    continue;
                }
                if (status != 0) return [];
                int count = Marshal.ReadInt32(buffer);
                var list = new List<KernelModule>(count);
                var name = new byte[256];
                for (int i = 0; i < count; i++)
                {
                    var entry = buffer + 8 + i * ModuleSize;
                    ulong imageBase = unchecked((ulong)Marshal.ReadInt64(entry, 16));
                    uint size = unchecked((uint)Marshal.ReadInt32(entry, 24));
                    int fileOffset = (ushort)Marshal.ReadInt16(entry, 38);
                    Marshal.Copy(entry + NameOffset, name, 0, name.Length);
                    int end = Array.IndexOf(name, (byte)0);
                    string full = Shell.AnsiEncoding.GetString(name, 0, end < 0 ? name.Length : end);
                    string file = fileOffset < full.Length ? full[fileOffset..] : full;
                    if (imageBase == 0) continue; // hidden without the debug privilege
                    list.Add(new KernelModule(imageBase, size, file, full));
                    Paths[file] = full;
                }
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    // "\SystemRoot\System32\drivers\nvlddmkm.sys" -> the file's description ("NVIDIA Windows Kernel Mode Driver, Version ...").
    public static string? Describe(string module)
    {
        if (!Paths.TryGetValue(module, out var path)) return null;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
        else if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        else if (path.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[9..]);
        try
        {
            return File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d.Trim() : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}

// Real-time kernel trace with DPC and interrupt events. A private SystemTraceProvider session (Windows 8 and later),
// so it does not take the single "NT Kernel Logger" another tool may be using.
internal sealed partial class KernelLatencySession(LatencyAggregator aggregator) : IDisposable
{
    const string SessionName = "PerformanceBooster-Latency";
    const uint RealTimeMode = 0x100, SystemLoggerMode = 0x2000000, TracedGuid = 0x20000, ControlStop = 1, ErrorAlreadyExists = 183;
    const uint FlagDpc = 0x20, FlagInterrupt = 0x40;
    const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeRawTimestamp = 0x1000, ProcessTraceModeEventRecord = 0x10000000;
    const ulong InvalidHandle = ulong.MaxValue;
    const int PropertiesSize = 120, LogFileSize = 448;

    // PerfInfo events: 66 threaded DPC, 68 DPC, 69 timer DPC, 67 ISR, 50 ISR (MSI).
    static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    static readonly long PerfInfoLow = BitConverter.ToInt64(PerfInfo.ToByteArray(), 0), PerfInfoHigh = BitConverter.ToInt64(PerfInfo.ToByteArray(), 8);

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTrace(out ulong handle, string name, IntPtr properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong handle, string? name, IntPtr properties, uint control);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    private static partial ulong OpenTrace(IntPtr logfile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong handle);

    readonly LatencyAggregator _aggregator = aggregator;
    GCHandle _self;
    ulong _trace = InvalidHandle;
    IntPtr _logfile, _loggerName;
    Thread? _thread;
    bool _stopped;

    public bool Start(out string error)
    {
        error = "";
        StopSession(); // left over from an earlier run that ended unexpectedly
        var props = Properties();
        try
        {
            uint status = StartTrace(out _, SessionName, props);
            if (status == ErrorAlreadyExists)
            {
                StopSession();
                Marshal.FreeHGlobal(props);
                props = Properties();
                status = StartTrace(out _, SessionName, props);
            }
            if (status != 0)
            {
                error = "StartTrace " + status;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        // EVENT_TRACE_LOGFILEW (64-bit): LoggerName 8, ProcessTraceMode 28, EventRecordCallback 424, Context 440.
        // Raw time stamps: otherwise Windows turns the event's time stamp into system time, while the start time in the
        // payload stays a performance counter value, and no execution time could be worked out.
        // The callback is a native entry point (no delegate per event); the context carries this session to it.
        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        unsafe
        {
            var log = (byte*)NativeMemory.AllocZeroed(LogFileSize);
            _logfile = (IntPtr)log;
            *(IntPtr*)(log + 8) = _loggerName;
            *(uint*)(log + 28) = ProcessTraceModeRealTime | ProcessTraceModeRawTimestamp | ProcessTraceModeEventRecord;
            *(IntPtr*)(log + 424) = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEventRecord;
            *(IntPtr*)(log + 440) = GCHandle.ToIntPtr(_self);
        }
        _trace = OpenTrace(_logfile);
        if (_trace == InvalidHandle)
        {
            error = "OpenTrace " + Marshal.GetLastPInvokeError();
            StopSession();
            return false;
        }
        _thread = new Thread(() => ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "ETW latency" };
        _thread.Start();
        return true;
    }

    // Stops the session and waits for the remaining events; returns how many events Windows dropped.
    public long Stop()
    {
        if (_stopped) return 0;
        _stopped = true;
        long lost = StopSession();
        _thread?.Join(5000);
        return lost;
    }

    // EVENT_TRACE_PROPERTIES + session name. Wnode: BufferSize 0, Guid 24, ClientContext 40, Flags 44.
    static IntPtr Properties()
    {
        int size = PropertiesSize + (SessionName.Length + 1) * 2 + 16;
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);
        Marshal.Copy(Guid.NewGuid().ToByteArray(), 0, p + 24, 16); // a private session needs its own GUID
        Marshal.WriteInt32(p, 40, 1); // QPC timestamps
        Marshal.WriteInt32(p, 44, unchecked((int)TracedGuid));
        // DPCs and interrupts arrive by the tens of thousands per second: 256 KB buffers, 32-128 of them.
        Marshal.WriteInt32(p, 48, 256);
        Marshal.WriteInt32(p, 52, 32);
        Marshal.WriteInt32(p, 56, 128);
        Marshal.WriteInt32(p, 64, unchecked((int)(RealTimeMode | SystemLoggerMode)));
        Marshal.WriteInt32(p, 68, 1); // flushed every second
        Marshal.WriteInt32(p, 72, unchecked((int)(FlagDpc | FlagInterrupt))); // EnableFlags
        Marshal.WriteInt32(p, 116, PropertiesSize); // LoggerNameOffset
        return p;
    }

    // EVENT_RECORD: TimeStamp 16, ProviderId 24, Opcode 45, ProcessorNumber 80, UserDataLength 86, UserData 96, UserContext 104.
    // DPC and ISR payload: InitialTime (8 bytes, same clock as the time stamp), Routine (pointer).
    // Tens of thousands per second: read in place, nothing allocated.
    [UnmanagedCallersOnly]
    static unsafe void OnEventRecord(byte* record)
    {
        if (*(long*)(record + 24) != PerfInfoLow || *(long*)(record + 32) != PerfInfoHigh) return;
        byte opcode = record[45];
        bool isr = opcode is 67 or 50;
        if (!isr && opcode is not (66 or 68 or 69)) return;
        if (*(ushort*)(record + 86) < 16) return;
        if (GCHandle.FromIntPtr(*(IntPtr*)(record + 104)).Target is not KernelLatencySession session) return;
        var data = *(byte**)(record + 96);
        long end = *(long*)(record + 16);
        long start = *(long*)data;
        ulong routine = *(ulong*)(data + 8);
        session._aggregator.Add(isr, record[80], routine, end - start);
    }

    static long StopSession()
    {
        var props = Properties();
        try
        {
            // EventsLost + RealTimeBuffersLost (the consumer could not keep up).
            return ControlTrace(0, SessionName, props, ControlStop) == 0 ? (uint)Marshal.ReadInt32(props, 88) + (long)(uint)Marshal.ReadInt32(props, 100) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_trace != InvalidHandle) CloseTrace(_trace);
        _trace = InvalidHandle;
        unsafe { if (_logfile != IntPtr.Zero) NativeMemory.Free((void*)_logfile); }
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        _logfile = _loggerName = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
    }
}
