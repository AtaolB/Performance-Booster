using System.Diagnostics;
using System.Globalization;

namespace PerformanceBooster.Core;

public sealed record GpuSample(
    string Name, double Utilization, double Temperature, double PowerW, double ClockMhz,
    double MemUsedMb, double MemTotalMb, ulong ThrottleReasons)
{
    // nvidia-smi clocks_throttle_reasons bits
    const ulong SwThermal = 0x20, HwThermal = 0x40, HwSlowdown = 0x8, PowerBrake = 0x80;

    public bool ThermalThrottle => (ThrottleReasons & (SwThermal | HwThermal | HwSlowdown | PowerBrake)) != 0;
}

// Starts nvidia-smi once and reads one line per second. Stopped while the window is hidden, restarted when it is shown.
public sealed class GpuMonitor : IDisposable
{
    Process? _process;

    public event Action<GpuSample>? Sampled;

    public bool Available => _process is { HasExited: false };

    internal static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Start()
    {
        if (Available) return;
        var exe = FindNvidiaSmi();
        if (exe is null) return;
        var psi = new ProcessStartInfo(exe,
            "--query-gpu=name,utilization.gpu,temperature.gpu,power.draw,clocks.gr,memory.used,memory.total,clocks_throttle_reasons.active " +
            "--format=csv,noheader,nounits -lms 1000")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Shell.ConsoleEncoding,
            StandardErrorEncoding = Shell.ConsoleEncoding,
        };
        try
        {
            _process = Process.Start(psi);
            if (_process is null) return;
            ChildJob.Add(_process);
            _process.OutputDataReceived += (_, e) => { if (Parse(e.Data) is { } s) Sampled?.Invoke(s); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _process = null;
        }
    }

    internal static GpuSample? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var f = line.Split(',').Select(x => x.Trim()).ToArray();
        if (f.Length < 8) return null;
        static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        var hex = f[7].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? f[7][2..] : f[7];
        _ = ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var reasons);
        return new GpuSample(f[0], D(f[1]), D(f[2]), D(f[3]), D(f[4]), D(f[5]), D(f[6]), reasons);
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It may already have exited.
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}

// Long-running child processes (nvidia-smi) are added to this job object. Even if the app is killed or crashes,
// the OS closes the job object and ends the child processes; no orphaned process is left behind.
// winget is not added: an interrupted installation can break the app being installed.
internal static class ChildJob
{
    static readonly Lazy<IntPtr> Job = new(Create);

    static IntPtr Create()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        return Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            ? job
            : IntPtr.Zero;
    }

    public static bool Add(Process process)
    {
        try
        {
            return Job.Value != IntPtr.Zero && Native.AssignProcessToJobObject(Job.Value, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
