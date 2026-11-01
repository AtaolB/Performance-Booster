using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PerformanceBooster.Core;

// Number of established TCP connections per process. Needs no admin rights; gives no byte counts.
public static partial class NetConnections
{
    const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5, StateEstablished = 5;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int af, int tableClass, uint reserved);

    public static Dictionary<int, int> CountByProcess()
    {
        var counts = new Dictionary<int, int>();
        // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, process = 24 bytes
        Add(counts, AfInet, rowSize: 24, stateOffset: 0, pidOffset: 20);
        // MIB_TCP6ROW_OWNER_PID: local address[16], scope, port, remote address[16], scope, port, state, process = 56 bytes
        Add(counts, AfInet6, rowSize: 56, stateOffset: 48, pidOffset: 52);
        return counts;
    }

    static void Add(Dictionary<int, int> counts, int family, int rowSize, int stateOffset, int pidOffset)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
                if (status == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew in the meantime
                if (status != 0) return;
                int rows = Marshal.ReadInt32(buffer);
                for (int i = 0; i < rows; i++)
                {
                    int row = 4 + i * rowSize;
                    if (Marshal.ReadInt32(buffer, row + stateOffset) != StateEstablished) continue;
                    int pid = Marshal.ReadInt32(buffer, row + pidOffset);
                    counts[pid] = counts.GetValueOrDefault(pid) + 1;
                }
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}

// Network traffic per process: Windows kernel network events (Microsoft-Windows-Kernel-Network ETW provider).
// An ETW session needs admin rights: the app starts itself elevated with "--netmon"; the helper copy
// writes a "process,downloaded,uploaded;..." line to a named pipe every second and exits when the main app exits.
public static partial class NetMonitor
{
    internal const string SessionName = "PerformanceBooster-Net";

    public static int RunHelper(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var parentId)) return 2;
        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { return 3; }

        var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.Out);
        var session = new EtwNetworkSession();
        try
        {
            pipe.Connect(15000);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            if (!session.Start(out var error))
            {
                writer.WriteLine("E " + error);
                return 5;
            }
            writer.WriteLine("OK");
            while (!parent.HasExited)
            {
                Thread.Sleep(1000);
                writer.WriteLine(session.Drain());
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed: the main app closed the network details (or never connected).
            return 0;
        }
        finally
        {
            // Windowless helper: exits quietly even if closing fails; the ETW session stops in every case.
            session.Dispose();
            try { pipe.Dispose(); }
            catch (IOException) { }
        }
    }

    // "1234,5000,200;88,0,1500" -> bytes downloaded/uploaded per process in the last second.
    internal static Dictionary<int, (long Down, long Up)> Parse(string line)
    {
        var result = new Dictionary<int, (long, long)>();
        foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(',');
            if (f.Length == 3 && int.TryParse(f[0], out var pid) && long.TryParse(f[1], out var down) && long.TryParse(f[2], out var up))
                result[pid] = (down, up);
        }
        return result;
    }
}

// Real-time ETW session running in the elevated copy.
internal sealed partial class EtwNetworkSession : IDisposable
{
    static readonly Guid Provider = new("7dd42a49-5329-4832-8dfd-43d979153a88"); // Microsoft-Windows-Kernel-Network
    const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    const uint RealTimeMode = 0x100, TracedGuid = 0x20000, ControlStop = 1, ErrorAlreadyExists = 183;
    const uint ProcessTraceModeRealTime = 0x100, ProcessTraceModeEventRecord = 0x10000000;
    const ulong InvalidHandle = ulong.MaxValue;
    const int PropertiesSize = 120, LogFileSize = 448;

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint StartTrace(out ulong handle, string name, IntPtr properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ControlTrace(ulong handle, string? name, IntPtr properties, uint control);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    private static partial ulong OpenTrace(IntPtr logfile);

    [LibraryImport("advapi32.dll")]
    private static partial uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong handle);

    readonly Dictionary<int, (long Down, long Up)> _bytes = [];
    readonly Lock _gate = new();
    GCHandle _self;
    ulong _session, _trace = InvalidHandle;
    IntPtr _logfile, _loggerName;
    Thread? _thread;

    public bool Start(out string error)
    {
        error = "";
        Stop(); // session left over from an earlier crash
        var props = Properties();
        try
        {
            uint status = StartTrace(out _session, NetMonitor.SessionName, props);
            if (status == ErrorAlreadyExists)
            {
                Stop();
                Marshal.FreeHGlobal(props);
                props = Properties();
                status = StartTrace(out _session, NetMonitor.SessionName, props);
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

        var provider = Provider;
        uint enable = EnableTraceEx2(_session, ref provider, 1, 4, KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
        if (enable != 0)
        {
            error = "EnableTraceEx2 " + enable;
            return false;
        }

        // EVENT_TRACE_LOGFILEW (64-bit): LoggerName 8, ProcessTraceMode 28, EventRecordCallback 424, Context 440.
        // The callback is a native entry point (no delegate per event); the context carries this session to it.
        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(NetMonitor.SessionName);
        unsafe
        {
            var log = (byte*)NativeMemory.AllocZeroed(LogFileSize);
            _logfile = (IntPtr)log;
            *(IntPtr*)(log + 8) = _loggerName;
            *(uint*)(log + 28) = ProcessTraceModeRealTime | ProcessTraceModeEventRecord;
            *(IntPtr*)(log + 424) = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEventRecord;
            *(IntPtr*)(log + 440) = GCHandle.ToIntPtr(_self);
        }
        _trace = OpenTrace(_logfile);
        if (_trace == InvalidHandle)
        {
            error = "OpenTrace " + Marshal.GetLastPInvokeError();
            return false;
        }
        _thread = new Thread(() => ProcessTrace([_trace], 1, IntPtr.Zero, IntPtr.Zero)) { IsBackground = true, Name = "ETW" };
        _thread.Start();
        return true;
    }

    // EVENT_TRACE_PROPERTIES + session name. Wnode: BufferSize 0, ClientContext 40, Flags 44.
    static IntPtr Properties()
    {
        int size = PropertiesSize + (NetMonitor.SessionName.Length + 1) * 2 + 16;
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, size);
        Marshal.WriteInt32(p, 40, 1); // QPC timestamps
        Marshal.WriteInt32(p, 44, unchecked((int)TracedGuid));
        // Do not lose events under heavy traffic: 128 KB buffers, 16-64 of them.
        Marshal.WriteInt32(p, 48, 128);
        Marshal.WriteInt32(p, 52, 16);
        Marshal.WriteInt32(p, 56, 64);
        Marshal.WriteInt32(p, 64, unchecked((int)RealTimeMode));
        Marshal.WriteInt32(p, 68, 1); // buffers are flushed every second
        Marshal.WriteInt32(p, 116, PropertiesSize); // LoggerNameOffset
        return p;
    }

    // EVENT_RECORD: event id 40, UserDataLength 86, UserData 96, UserContext 104. Payload: process id (4) + size (4) + addresses...
    // One event per packet: read in place, one dictionary lookup.
    [UnmanagedCallersOnly]
    static unsafe void OnEventRecord(byte* record)
    {
        int id = *(ushort*)(record + 40);
        bool sent = id is 10 or 26 or 42 or 58, received = id is 11 or 27 or 43 or 59;
        if (!sent && !received || *(ushort*)(record + 86) < 8) return;
        if (GCHandle.FromIntPtr(*(IntPtr*)(record + 104)).Target is not EtwNetworkSession session) return;
        var data = *(byte**)(record + 96);
        int pid = *(int*)data;
        long size = *(uint*)(data + 4);
        lock (session._gate)
        {
            ref var bytes = ref CollectionsMarshal.GetValueRefOrAddDefault(session._bytes, pid, out _);
            if (sent) bytes.Up += size;
            else bytes.Down += size;
        }
    }

    public string Drain()
    {
        lock (_gate)
        {
            var line = string.Join(";", _bytes.Select(kv => FormattableString.Invariant($"{kv.Key},{kv.Value.Down},{kv.Value.Up}")));
            _bytes.Clear();
            return line;
        }
    }

    static void Stop()
    {
        var props = Properties();
        try { ControlTrace(0, NetMonitor.SessionName, props, ControlStop); }
        finally { Marshal.FreeHGlobal(props); }
    }

    public void Dispose()
    {
        Stop();
        if (_trace != InvalidHandle) CloseTrace(_trace);
        _trace = InvalidHandle;
        _thread?.Join(2000);
        unsafe { if (_logfile != IntPtr.Zero) NativeMemory.Free((void*)_logfile); }
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        _logfile = _loggerName = IntPtr.Zero;
        if (_self.IsAllocated) _self.Free();
    }
}

// Client in the main app: starts the helper copy elevated and listens on the pipe.
public sealed class NetMonitorClient : IDisposable
{
    NamedPipeServerStream? _pipe;
    CancellationTokenSource? _cts;
    volatile IReadOnlyDictionary<int, (long Down, long Up)> _rates = new Dictionary<int, (long, long)>();

    // Exe to run as the helper in tests.
    internal static string? HelperExe { get; set; }

    public bool Running { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyDictionary<int, (long Down, long Up)> Rates => _rates;

    // Total bytes per process since start.
    readonly Dictionary<int, (long Down, long Up)> _totals = [];

    public Dictionary<int, (long Down, long Up)> Totals()
    {
        lock (_totals) return new(_totals);
    }
    public DateTime LastUpdate { get; private set; }

    public async Task<bool> StartAsync()
    {
        if (Running) return true;
        Error = null;
        lock (_totals) _totals.Clear();
        var name = $"PerformanceBooster.Net.{Environment.ProcessId}.{Guid.NewGuid():N}";
        // Only the signed-in user (the elevated helper runs as the same user) can connect.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
        _pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(HelperExe ?? Environment.ProcessPath!, $"--netmon {name} {Environment.ProcessId}")
            {
                UseShellExecute = true, Verb = Elevated.ElevateVerb, WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex)
        {
            Error = ex.NativeErrorCode == 1223 ? T("Cancelled: administrator permission was not granted.") : ex.Message;
            Stop();
            return false;
        }

        var reader = new StreamReader(_pipe, Encoding.UTF8);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _pipe.WaitForConnectionAsync(timeout.Token);
            var first = await reader.ReadLineAsync(timeout.Token);
            if (first != "OK")
            {
                Error = first?.StartsWith("E ", StringComparison.Ordinal) == true ? first[2..] : T("The network monitor did not start.");
                Stop();
                return false;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Error = T("The network monitor did not start.");
            Stop();
            return false;
        }

        Running = true;
        _cts = new CancellationTokenSource();
        _ = ReadAsync(reader, _cts.Token);
        return true;
    }

    async Task ReadAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                var rates = NetMonitor.Parse(line);
                lock (_totals)
                    foreach (var (pid, (down, up)) in rates)
                    {
                        var (d, u) = _totals.GetValueOrDefault(pid);
                        _totals[pid] = (d + down, u + up);
                    }
                _rates = rates;
                LastUpdate = DateTime.Now;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        Running = false;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
        _pipe = null;
        Running = false;
        _rates = new Dictionary<int, (long, long)>();
    }

    public void Dispose() => Stop();
}
