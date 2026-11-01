using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// StablePercent: reports that arrived within 25% of the expected interval.
public sealed record PollingResult(int Reports, double AverageHz, int NominalHz, double MeanMs, double StdDevMs, double MinMs, double MaxMs, double StablePercent);

// Mouse polling: the time between the mouse's movement reports (raw input) shows the real polling rate
// and how evenly the reports arrive. A busy system delivers them unevenly, so this also hints at smoothness in games.
public static partial class MousePolling
{
    public static readonly int[] Rates = [125, 250, 500, 1000, 2000, 4000, 8000];

    // A longer gap means the mouse stopped (slower than 50 Hz); those gaps are left out.
    const double PauseMs = 20;

    public static PollingResult? Analyze(IReadOnlyList<long> timestamps, double ticksPerMs)
    {
        var intervals = new List<double>();
        for (int i = 1; i < timestamps.Count; i++)
        {
            double ms = (timestamps[i] - timestamps[i - 1]) / ticksPerMs;
            if (ms > 0 && ms <= PauseMs) intervals.Add(ms);
        }
        if (intervals.Count < 20) return null;
        double mean = intervals.Average();
        double sd = Math.Sqrt(intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Count);
        double hz = 1000 / mean;
        int nominal = Rates.MinBy(r => Math.Abs(Math.Log(r / hz)));
        double expected = 1000.0 / nominal;
        double stable = intervals.Count(ms => Math.Abs(ms - expected) <= expected * 0.25) * 100.0 / intervals.Count;
        return new PollingResult(intervals.Count + 1, Math.Round(hz, 1), nominal, Math.Round(mean, 4), Math.Round(sd, 4),
            Math.Round(intervals.Min(), 4), Math.Round(intervals.Max(), 4), Math.Round(stable, 1));
    }
}

// Raw mouse input (WM_INPUT) to one window: movement reports straight from the device, before pointer speed and acceleration.
internal static partial class RawInput
{
    public const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIDEV_REMOVE = 0x1;
    const int HeaderSize = 24; // RAWINPUTHEADER (64-bit): type, size, device handle, wParam
    public const int BufferSize = 64;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static partial uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    // Generic desktop page (1), mouse (2).
    public static bool Register(IntPtr window, bool remove = false)
    {
        var device = new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = remove ? RIDEV_REMOVE : 0, Target = remove ? IntPtr.Zero : window };
        return RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    // RAWMOUSE after the header: flags, buttons, raw buttons, lLastX 12, lLastY 16.
    public static (IntPtr Device, int Dx, int Dy)? ReadMouse(IntPtr lParam, IntPtr buffer)
    {
        uint size = BufferSize;
        if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, HeaderSize) == uint.MaxValue || size < HeaderSize + 20) return null;
        if (Marshal.ReadInt32(buffer, 0) != RIM_TYPEMOUSE) return null;
        return (Marshal.ReadIntPtr(buffer, 8), Marshal.ReadInt32(buffer, HeaderSize + 12), Marshal.ReadInt32(buffer, HeaderSize + 16));
    }
}
