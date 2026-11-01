using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

public readonly record struct TimerInfo(double CoarsestMs, double FinestMs, double CurrentMs);

// A finer Windows timer, held the whole time the app runs; needs no administrator rights.
// Since Windows 10 2004 a request applies to the requesting app only; on Windows 11 the "Global timer resolution"
// setting (GlobalTimerResolutionRequests) makes it apply to the whole system again, so the game gets it too.
//
// Windows keeps one request per process, and the app's own window can overwrite it: WPF raises the timer to 1 ms while it
// draws (timeBeginPeriod) and then releases it (timeEndPeriod), which also drops the app's 0.5 ms request. So the request is
// held by a small windowless copy of the app (--timer ms parentId) that does nothing else and exits with the app.
// This is why the timer worked when Windows started the app in the tray (nothing drawn) but not when the window was open.
public static class TimerResolution
{
    static uint _requested;
    static double _askedMs;

    public static bool Holding => _requested != 0;
    public static double RequestedMs => _requested / 10000.0;

    public static TimerInfo? Query() =>
        Native.NtQueryTimerResolution(out var coarsest, out var finest, out var current) == 0
            ? new TimerInfo(coarsest / 10000.0, finest / 10000.0, current / 10000.0)
            : null;

    // Returns the timer resolution Windows reports after the request (ms), or null if it was refused.
    public static double? Request(double ms)
    {
        Release();
        if (Query() is not { } info) return null;
        uint desired = (uint)Math.Round(Math.Clamp(ms, info.FinestMs, info.CoarsestMs) * 10000);
        KeepWhenHidden();
        if (Native.NtSetTimerResolution(desired, true, out var current) != 0) return null;
        _requested = desired;
        _askedMs = ms;
        return current / 10000.0;
    }

    public static void Release()
    {
        if (_requested == 0) return;
        Native.NtSetTimerResolution(_requested, false, out _);
        _requested = 0;
    }

    // Windows 11 ignores the timer requests of an app whose window is hidden (the app usually waits in the tray) unless it opts out.
    static void KeepWhenHidden()
    {
        var state = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = Native.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, StateMask = 0 };
        Native.SetProcessInformation(Native.GetCurrentProcess(), Native.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<Native.PROCESS_POWER_THROTTLING_STATE>());
    }

    // --- The helper copy that holds the request ---

    static Process? _helper;
    static double _helperMs;
    static long _startedAt;

    // Tests start the app's exe (the test runner is a different program).
    internal static string? HelperExe { get; set; }

    public static bool HelperRunning => _helper is { HasExited: false };
    public static double HelperMs => HelperRunning ? _helperMs : 0;

    // Holds the chosen resolution while the setting is on and releases it otherwise. Does nothing while the helper holds the
    // chosen value; a helper that exited (or whose request Windows refused) is started again, at most every 30 seconds.
    public static void Apply(AppSettings settings)
    {
        if (Demo.On) return;
        if (!settings.TimerOn) StopHelper();
        else if (HelperRunning ? _helperMs != settings.TimerMs : _helperMs != settings.TimerMs || Environment.TickCount64 - _startedAt > 30_000)
            StartHelper(settings.TimerMs);
    }

    static void StartHelper(double ms)
    {
        StopHelper();
        var exe = HelperExe ?? Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            var psi = new ProcessStartInfo(exe, $"--timer {ms.ToString(CultureInfo.InvariantCulture)} {Environment.ProcessId}")
            {
                UseShellExecute = false, CreateNoWindow = true,
            };
            _startedAt = Environment.TickCount64;
            _helperMs = ms;
            _helper = Process.Start(psi);
            if (_helper is null) return;
            ChildJob.Add(_helper); // ends with the app even if the app is killed
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _helper = null;
        }
    }

    public static void StopHelper()
    {
        _helperMs = 0;
        if (_helper is null) return;
        try
        {
            if (!_helper.HasExited) _helper.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _helper.Dispose();
        _helper = null;
    }

    // In the helper copy: requests the resolution and waits until the app exits. Exit code 3 = Windows refused the request.
    public static int RunHelper(string[] args)
    {
        if (args.Length < 3 || !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || !int.TryParse(args[2], out var parentId))
            return 2;
        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { return 0; }
        if (Request(ms) is null) return 3;
        using (parent) parent.WaitForExit();
        Release();
        return 0;
    }
}
