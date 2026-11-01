using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Launcher;

// Programs are started with CreateProcess itself: System.Diagnostics.Process would make this exe larger, and once this exe
// has renamed itself it could not be loaded any more (see Payload).
static unsafe partial class Processes
{
    struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(char* application, char* commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, char* directory, StartupInfo* startup, ProcessInformation* info);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, uint* code);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    const uint Infinite = uint.MaxValue;

    // The arguments are the rest of the command line after the exe's quoted path, starting with a space.
    public static void Start(string exe, string arguments) => Close(Create(exe, arguments));

    // Runs the exe until it exits and returns its exit code.
    public static int Run(string exe, string arguments)
    {
        var info = Create(exe, arguments);
        try
        {
            uint code;
            WaitForSingleObject(info.Process, Infinite);
            if (!GetExitCodeProcess(info.Process, &code)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return (int)code;
        }
        finally
        {
            Close(info);
        }
    }

    static ProcessInformation Create(string exe, string arguments)
    {
        var startup = new StartupInfo { Size = (uint)sizeof(StartupInfo) };
        ProcessInformation info;
        fixed (char* path = exe)
        fixed (char* line = $"\"{exe}\"{arguments}\0".ToCharArray()) // a copy: CreateProcess may write to the command line
        {
            if (!CreateProcess(path, line, 0, 0, false, 0, 0, null, &startup, &info)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return info;
    }

    static void Close(ProcessInformation info)
    {
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
    }
}
