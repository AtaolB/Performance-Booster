using System.Runtime.InteropServices;

namespace PerformanceBooster.Core;

// One look at what can hold a running game back. GPU values only with an NVIDIA card (nvidia-smi); NaN/null when unknown.
public sealed record HealthSample(GpuSample? Gpu, double CpuLimitPercent, double AvailableRamMb, double TotalRamMb, double CommitPercent, bool? OnAc);

public sealed record Finding(string Key, string Title, string Detail, string Hint);

// What held the computer back while a game ran: the graphics card slowing down for heat or power, full video memory,
// low RAM, the processor held below its full speed, battery power. Sampled every 3 s while the game runs; a finding
// needs at least two samples (6 s). The summary of the last game is saved, so the Game mode tab shows it after a restart too.
public sealed class GameSession
{
    public const int SampleSeconds = 3;
    const int Needed = 2;

    public string Game { get; set; } = "";
    public DateTime Start { get; set; } = DateTime.Now;
    public DateTime? End { get; set; }
    public int Samples { get; set; }
    public int GpuSamples { get; set; }
    public int GpuSlowed { get; set; }
    public int VramFull { get; set; }
    public int RamLow { get; set; }
    public int CpuLimited { get; set; }
    public int OnBattery { get; set; }
    public double MaxGpuTemp { get; set; }
    public double MaxVramPercent { get; set; }
    public double MinRamMb { get; set; } = double.MaxValue;
    public double MinCpuLimit { get; set; } = 100;

    internal static string StateFile => Path.Combine(AppPaths.Folder, "last-game.json");

    public static string HeatHint => T("Keep the air vents clear, use a laptop stand or cooling pad, and set the fans to high. If it has not been cleaned in a long time, have the dust cleaned and the thermal paste replaced.");

    public void Add(HealthSample s)
    {
        Samples++;
        if (s.Gpu is { } g)
        {
            GpuSamples++;
            if (g.ThermalThrottle) GpuSlowed++;
            if (!double.IsNaN(g.Temperature)) MaxGpuTemp = Math.Max(MaxGpuTemp, g.Temperature);
            if (g.MemTotalMb > 0 && !double.IsNaN(g.MemUsedMb))
            {
                double vram = g.MemUsedMb * 100 / g.MemTotalMb;
                MaxVramPercent = Math.Max(MaxVramPercent, vram);
                if (vram >= 95) VramFull++;
            }
        }
        if (s.TotalRamMb > 0)
        {
            MinRamMb = Math.Min(MinRamMb, s.AvailableRamMb);
            if (s.AvailableRamMb < Math.Min(1024, s.TotalRamMb * 0.05) || s.CommitPercent >= 90) RamLow++;
        }
        if (!double.IsNaN(s.CpuLimitPercent))
        {
            MinCpuLimit = Math.Min(MinCpuLimit, s.CpuLimitPercent);
            if (s.CpuLimitPercent < 90) CpuLimited++;
        }
        if (s.OnAc == false) OnBattery++;
    }

    public TimeSpan Length => (End ?? DateTime.Now) - Start;

    public List<Finding> Findings()
    {
        var list = new List<Finding>();
        string For(int samples) => F("for {0} of {1}", Duration(samples * SampleSeconds), Duration(Samples * SampleSeconds));
        if (GpuSlowed >= Needed)
            list.Add(new("gpu", T("The graphics card slowed down because of heat or power"), For(GpuSlowed) + " · " + F("up to {0}°C", MaxGpuTemp.ToString("0")), HeatHint));
        if (VramFull >= Needed)
            list.Add(new("vram", T("Video memory was full"), For(VramFull) + " · " + F("up to {0}", Pct(MaxVramPercent)),
                T("The game moved textures to the slower system memory, which causes stutter. Lower the texture quality or the resolution in the game, and close other apps that use the graphics card (browsers, video).")));
        if (RamLow >= Needed)
            list.Add(new("ram", T("RAM ran low"), For(RamLow) + " · " + F("{0} MB free at the lowest", MinRamMb.ToString("0")),
                T("Windows moved memory to the disk while the game ran, which causes stutter. Close browsers and launchers before playing, or add RAM.")));
        if (CpuLimited >= Needed)
            list.Add(new("cpu", T("The processor ran below its full speed"), For(CpuLimited) + " · " + F("at the lowest {0} of its full speed", Pct(MinCpuLimit)),
                T("Heat or a power limit held the processor back. Check the cooling; on a laptop, plug in the charger and choose the performance mode in the manufacturer's app.")));
        if (OnBattery >= Needed)
            list.Add(new("battery", T("The game ran on battery"), For(OnBattery),
                T("On battery the graphics card and processor get much less power. Plug in the charger while gaming.")));
        return list;
    }

    public static string Duration(int seconds) => seconds < 90 ? F("{0} s", seconds) : F("{0} min", (seconds / 60.0).ToString("0", L.Culture));

    public void Save()
    {
        try { AppPaths.WriteJson(StateFile, this, AppJson.Default.GameSession); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static GameSession? Load() => AppPaths.ReadJson(StateFile, AppJson.Default.GameSession);
}

// The values a sample needs besides the GPU: the processor's performance limit (heat or power, as Windows reports it),
// free RAM and the commit charge, AC power.
public sealed class HealthSampler : IDisposable
{
    IntPtr _query, _limit;

    public HealthSampler()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        if (Native.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Performance Limit", IntPtr.Zero, out _limit) != 0) _limit = IntPtr.Zero;
        Native.PdhCollectQueryData(_query);
    }

    public HealthSample Sample(GpuSample? gpu)
    {
        double limit = double.NaN;
        if (_query != IntPtr.Zero && _limit != IntPtr.Zero && Native.PdhCollectQueryData(_query) == 0
            && Native.PdhGetFormattedCounterValue(_limit, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus <= 1)
            limit = v.DoubleValue;
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        bool memory = Native.GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0;
        return new HealthSample(gpu, limit,
            memory ? m.ullAvailPhys / 1048576.0 : double.NaN,
            memory ? m.ullTotalPhys / 1048576.0 : 0,
            memory && m.ullTotalPageFile > 0 ? (m.ullTotalPageFile - m.ullAvailPageFile) * 100.0 / m.ullTotalPageFile : double.NaN,
            SystemInfo.OnAcPower());
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
