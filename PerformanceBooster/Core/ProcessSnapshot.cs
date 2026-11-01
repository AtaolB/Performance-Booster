using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// Snapshot of one process. Times are in 100 ns units, memory and I/O in bytes.
public readonly record struct ProcessInfo(
    int Id, int ParentId, string Name, int SessionId, long CreateTime, long CpuTime,
    long PrivateWorkingSet, long WorkingSet, long PrivateBytes, long ReadBytes, long WriteBytes);

// All processes are read with a single system call (the same source as Task Manager): no need to open processes,
// protected processes (Defender, anti-cheat) included. The buffer is reused and process names repeat from one snapshot to
// the next, so a snapshot allocates only its list.
public static partial class ProcessSnapshot
{
    const int SystemProcessInformation = 5;
    const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    static readonly Lock Gate = new();
    static IntPtr _buffer;
    static int _size = 1024 * 1024, _lastCount = 400;
    static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> NameLookup = Names.GetAlternateLookup<ReadOnlySpan<char>>();

    public static List<ProcessInfo> Take()
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_size);
                int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                    _size = Math.Max(needed, _size) + 256 * 1024;
                    continue;
                }
                return status == 0 ? Parse(_buffer) : [];
            }
            return [];
        }
    }

    // SYSTEM_PROCESS_INFORMATION (64-bit) field offsets.
    static unsafe List<ProcessInfo> Parse(IntPtr start)
    {
        var list = new List<ProcessInfo>(_lastCount + 16);
        var p = (byte*)start;
        while (true)
        {
            int next = *(int*)p;
            long privateWs = *(long*)(p + 8);
            long create = *(long*)(p + 32);
            long user = *(long*)(p + 40), kernel = *(long*)(p + 48);
            int nameBytes = *(ushort*)(p + 56);
            var namePtr = *(char**)(p + 64);
            int id = (int)*(long*)(p + 80);
            int parent = (int)*(long*)(p + 88);
            int session = *(int*)(p + 100);
            long ws = *(long*)(p + 144);
            long privateBytes = *(long*)(p + 184);
            long read = *(long*)(p + 232), write = *(long*)(p + 240);

            string name = id == 0 ? "Idle" : namePtr == null || nameBytes == 0 ? "System" : Name(new ReadOnlySpan<char>(namePtr, nameBytes / 2));
            list.Add(new ProcessInfo(id, parent, name, session, create, user + kernel, privateWs, ws, privateBytes, read, write));
            if (next == 0) break;
            p += next;
        }
        _lastCount = list.Count;
        return list;
    }

    // "chrome.exe" -> "chrome", the same string as in the previous snapshots.
    static string Name(ReadOnlySpan<char> name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (NameLookup.TryGetValue(name, out var known)) return known;
        if (Names.Count >= 4096) Names.Clear(); // names of processes long gone
        var text = name.ToString();
        Names[text] = text;
        return text;
    }
}

// Difference between two snapshots: CPU percentage and disk read/write rate per process.
public sealed class ProcessRates
{
    readonly record struct Last(long Cpu, long Read, long Write, int Generation);

    readonly Dictionary<(int Id, long Create), Last> _last = [];
    long _lastTicks;
    int _generation;

    public readonly record struct Rate(ProcessInfo Info, double CpuPercent, double ReadPerSec, double WritePerSec);

    public List<Rate> Sample()
    {
        var now = Environment.TickCount64;
        var snapshot = ProcessSnapshot.Take();
        double seconds = _lastTicks == 0 ? 0 : (now - _lastTicks) / 1000.0;
        double cpuCapacity = seconds * Environment.ProcessorCount * 1e7; // 100 ns units
        var result = new List<Rate>(snapshot.Count);
        int generation = ++_generation;
        foreach (var info in snapshot)
        {
            // One lookup per process: the entry is read and replaced in place.
            ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(_last, (info.Id, info.CreateTime), out bool known);
            double cpu = 0, read = 0, write = 0;
            if (seconds > 0 && known)
            {
                cpu = Math.Max(0, info.CpuTime - last.Cpu) / cpuCapacity * 100.0;
                read = Math.Max(0, info.ReadBytes - last.Read) / seconds;
                write = Math.Max(0, info.WriteBytes - last.Write) / seconds;
            }
            last = new Last(info.CpuTime, info.ReadBytes, info.WriteBytes, generation);
            result.Add(new Rate(info, info.Id == 0 ? 0 : cpu, read, write));
        }
        // Processes that exited (removing while enumerating is allowed for Dictionary).
        if (_last.Count > snapshot.Count)
            foreach (var (key, entry) in _last)
                if (entry.Generation != generation) _last.Remove(key);
        _lastTicks = now;
        return result;
    }
}
