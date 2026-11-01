using System.Globalization;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Launcher;

// The installer download with WinHTTP, Windows' own HTTP client (System.Net.Http would make this exe megabytes larger), on a
// background thread; the progress window reads its counters.
sealed unsafe partial class Download(string url, string file) : IDisposable
{
    const uint AutomaticProxy = 4, Secure = 0x00800000, QueryStatusCode = 19, QueryContentLength = 5, QueryNumber = 0x20000000,
        OptionUrl = 34, FormatIgnoreInserts = 0x200, FormatFromModule = 0x800, FormatFromSystem = 0x1000;
    const string Scheme = "https://";

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WinHttpOpen(string agent, uint accessType, char* proxy, char* proxyBypass, uint flags);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);

    [LibraryImport("winhttp.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WinHttpOpenRequest(IntPtr connection, string verb, string path, char* version, char* referrer, char** acceptTypes, uint flags);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpSendRequest(IntPtr request, char* headers, uint headersLength, void* optional, uint optionalLength, uint totalLength, nuint context);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpReceiveResponse(IntPtr request, void* reserved);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpQueryHeaders(IntPtr request, uint infoLevel, char* name, void* buffer, uint* length, uint* index);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpQueryOption(IntPtr handle, uint option, void* buffer, uint* length);

    [LibraryImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpReadData(IntPtr request, void* buffer, uint toRead, uint* read);

    [LibraryImport("winhttp.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpCloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "FormatMessageW")]
    private static partial uint FormatMessage(uint flags, IntPtr source, int id, uint language, char* buffer, uint size, IntPtr arguments);

    long _received, _total;
    IntPtr _request;
    volatile bool _started, _cancelled, _done;

    public long Received => Interlocked.Read(ref _received);
    public long Total => Interlocked.Read(ref _total);
    public bool Done => _done;
    public bool Succeeded { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        new Thread(Run) { IsBackground = true }.Start();
    }

    // Closing the request also ends a read that is waiting for the network.
    public void Cancel()
    {
        _cancelled = true;
        CloseRequest();
    }

    // Waits until the file is closed (after a cancel, too).
    public void Wait()
    {
        if (!_started) return;
        while (!_done) Thread.Sleep(50);
    }

    void Run()
    {
        IntPtr session = 0, connection = 0;
        try
        {
            int slash = url.StartsWith(Scheme, StringComparison.Ordinal) ? url.IndexOf('/', Scheme.Length) : -1;
            if (slash < 0) throw new InvalidDataException(url);
            session = WinHttpOpen("Performance Booster", AutomaticProxy, null, null, 0);
            if (session == 0) throw Failure();
            connection = WinHttpConnect(session, url[Scheme.Length..slash], 443, 0);
            if (connection == 0) throw Failure();
            var request = WinHttpOpenRequest(connection, "GET", url[slash..], null, null, null, Secure);
            if (request == 0) throw Failure();
            Interlocked.Exchange(ref _request, request); // a fence: Cancel either sees the request or is seen below
            if (_cancelled) return;
            if (!WinHttpSendRequest(request, null, 0, null, 0, 0, 0) || !WinHttpReceiveResponse(request, null)) throw Failure();

            uint status = 0, size = sizeof(uint);
            if (!WinHttpQueryHeaders(request, QueryStatusCode | QueryNumber, null, &status, &size, null)) throw Failure();
            if (status != 200) throw new IOException("HTTP " + status.ToString(CultureInfo.InvariantCulture));
            // Only from Microsoft's download servers (aka.ms forwards the request to them).
            const int UrlCapacity = 2048;
            char* final = stackalloc char[UrlCapacity];
            size = UrlCapacity * sizeof(char);
            if (!WinHttpQueryOption(request, OptionUrl, final, &size)) throw Failure();
            var host = Host(new string(final));
            if (!host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(host);

            char* length = stackalloc char[32];
            size = 32 * sizeof(char);
            if (WinHttpQueryHeaders(request, QueryContentLength, null, length, &size, null)
                && long.TryParse(new ReadOnlySpan<char>(length, (int)(size / sizeof(char))), NumberStyles.None, CultureInfo.InvariantCulture, out var total))
                Interlocked.Exchange(ref _total, total);

            using var target = File.Create(file);
            var buffer = new byte[128 * 1024];
            fixed (byte* data = buffer)
                while (!_cancelled)
                {
                    uint read;
                    if (!WinHttpReadData(request, data, (uint)buffer.Length, &read)) throw Failure();
                    if (read == 0)
                    {
                        if (Total > 0 && Received != Total) throw new IOException(string.Create(CultureInfo.InvariantCulture, $"{Received} of {Total} bytes"));
                        Succeeded = true;
                        break;
                    }
                    target.Write(buffer, 0, (int)read);
                    Interlocked.Add(ref _received, read);
                }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!_cancelled) Error = ex.Message;
        }
        finally
        {
            CloseRequest();
            if (connection != 0) WinHttpCloseHandle(connection);
            if (session != 0) WinHttpCloseHandle(session);
            _done = true;
        }
    }

    void CloseRequest()
    {
        var request = Interlocked.Exchange(ref _request, 0);
        if (request != 0) WinHttpCloseHandle(request);
    }

    // The host of https://host[:port]/path; empty for anything else.
    static string Host(string address)
    {
        if (!address.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return "";
        var rest = address.AsSpan(Scheme.Length);
        int end = rest.IndexOfAny('/', ':');
        return (end < 0 ? rest : rest[..end]).ToString();
    }

    // The last WinHTTP error as Windows words it (winhttp.dll has the texts of its own errors).
    static IOException Failure()
    {
        int error = Marshal.GetLastPInvokeError();
        const int Capacity = 512;
        char* text = stackalloc char[Capacity];
        uint length = FormatMessage(FormatIgnoreInserts | FormatFromModule | FormatFromSystem, GetModuleHandle("winhttp.dll"), error, 0, text, Capacity, 0);
        var code = error.ToString(CultureInfo.InvariantCulture);
        return new IOException(length > 0 ? $"{new string(text, 0, (int)length).TrimEnd()} ({code})" : "WinHTTP " + code);
    }

    public void Dispose()
    {
        Cancel();
        Wait();
    }
}
