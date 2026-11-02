using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// The demo computer's live values (see Core/Demo.cs): smooth made-up numbers instead of this computer's.
public sealed partial class MainViewModel
{
    readonly Random _demoRandom = new(7);
    double _demoCpu = 14, _demoGpu = 38, _demoNet = 3.2;

    double Wander(ref double value, double min, double max, double step)
    {
        value = Math.Clamp(value + (_demoRandom.NextDouble() - 0.5) * step, min, max);
        return value;
    }

    void DemoTick()
    {
        _tick++;
        double cpu = Wander(ref _demoCpu, 6, 28, 6);
        _cpuHistory.Add(cpu);
        CpuText = Pct(cpu);
        CpuSub = "68°C · 4.90 GHz · " + F("{0} thread|{0} threads", Demo.Threads);
        CpuSeries = _cpuHistory.Snapshot();

        double used = 11.8 + _demoRandom.NextDouble() * 0.2;
        _ramPercent = used * 100 / Demo.RamTotalGb;
        _ramHistory.Add(_ramPercent);
        RamText = Pct(_ramPercent);
        RamSub = $"{used.ToString("0.0", L.Culture)} / {Demo.RamTotalGb.ToString("0.0", L.Culture)} GB";
        RamSeries = _ramHistory.Snapshot();

        double gpu = Wander(ref _demoGpu, 20, 60, 8);
        _gpuHistory.Add(gpu);
        GpuName = Demo.Gpu;
        GpuText = Pct(gpu);
        GpuSub = $"61°C · 142 W · 2610 MHz · VRAM {4.1.ToString("0.0", L.Culture)}/16 GB";
        GpuSeries = _gpuHistory.Snapshot();

        double disk = _demoRandom.NextDouble() < 0.15 ? 6 + _demoRandom.NextDouble() * 10 : _demoRandom.NextDouble() * 2;
        _diskHistory.Add(disk);
        DiskText = Pct(disk);
        DiskSub = F("{0} MB/s", (disk * 0.9).ToString("0.0", L.Culture)) + " · 41°C";
        DiskSeries = _diskHistory.Snapshot();

        double down = Wander(ref _demoNet, 0.5, 9, 3);
        _netHistory.Add(down);
        NetText = $"{down.ToString("0.0", L.Culture)} Mbps";
        NetSub = F("Upload {0} Mbps", (down / 6).ToString("0.0", L.Culture)) + " · Ethernet 1 Gbps";
        NetSeries = _netHistory.Snapshot();

        if (SelectedTab == TabProcesses && _tick % 2 == 0) DemoProcesses();
        if (SelectedTab == TabGame) RefreshGameBoost();
        UpdateIssues();
    }

    // Name, count, CPU %, RAM MB, disk KB/s, GPU %, connections, path.
    static readonly (string Name, int Count, double Cpu, long Ram, double Disk, double Gpu, int Connections, string Path)[] DemoApps =
    [
        ("ArenaLegends", 1, 9.8, 6240, 820, 34.0, 6, @"D:\Games\Arena Legends\ArenaLegends.exe"),
        ("Browser", 14, 2.1, 1860, 96, 0.6, 38, @"C:\Program Files\Browser\Browser.exe"),
        ("VoiceChat", 5, 1.2, 410, 12, 0.4, 4, @"C:\Program Files\Voice Chat\VoiceChat.exe"),
        ("GameLauncher", 3, 0.6, 520, 40, 0, 9, @"C:\Program Files\Game Launcher\GameLauncher.exe"),
        ("MusicPlayer", 2, 0.4, 230, 30, 0, 2, @"C:\Program Files\Music Player\MusicPlayer.exe"),
        ("explorer", 1, 0.3, 160, 4, 0.1, 1, @"C:\Windows\explorer.exe"),
        ("dwm", 1, 0.8, 120, 0, 1.2, 0, @"C:\Windows\System32\dwm.exe"),
        ("svchost", 71, 0.5, 640, 22, 0, 14, @"C:\Windows\System32\svchost.exe"),
        ("PerformanceBooster", 1, 0.2, 70, 1, 0, 0, @"C:\Tools\PerformanceBooster.exe"),
    ];

    void DemoProcesses()
    {
        _allProcesses = DemoApps.Select(a =>
        {
            double jitter = 0.85 + _demoRandom.NextDouble() * 0.3;
            return new ProcessManagerRow(a.Name, a.Count, a.Cpu * jitter, a.Ram * 1048576, a.Disk * 1024 * jitter, a.Gpu * jitter, a.Connections, a.Path,
                a.Name is not ("svchost" or "dwm" or "explorer" or "PerformanceBooster"), a.Name == "ArenaLegends");
        }).ToList();
        ShowProcesses();
    }

    void DemoInterrupts()
    {
        Interrupts.Clear();
        Interrupts.Add(new InterruptRow(Demo.Gpu, T("MSI"), F("{0} message|{0} messages", 1), Palette.Ok));
        Interrupts.Add(new InterruptRow("USB 3.2 eXtensible Host Controller", T("MSI"), F("{0} message|{0} messages", 8), Palette.Ok));
        Interrupts.Add(new InterruptRow("High Definition Audio Controller", T("MSI"), F("{0} message|{0} messages", 1), Palette.Ok));
        Interrupts.Add(new InterruptRow("2.5GbE Ethernet Controller", T("MSI"), F("{0} message|{0} messages", 4), Palette.Ok));
        MousePortsText = F("{0}: {1}", "USB Gaming Mouse", "USB 3.2 eXtensible Host Controller");
    }
}
