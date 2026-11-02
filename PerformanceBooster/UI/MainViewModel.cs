using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Live monitoring and the issue panel. Settings, updates, network test, game mode and tools are in the other partial files.
// Live monitoring stops while the window is hidden/minimized; game mode and update checks keep running.
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public const int TabLive = 0, TabUpdates = 1, TabPrograms = 2, TabTweaks = 3, TabNetwork = 4, TabGame = 5, TabStartup = 6, TabSystem = 7, TabProcesses = 8;

    static string HeatHint => GameSession.HeatHint;

    sealed record IssueData(string Key, Brush Severity, string Title, string Detail, string Suggestion,
        string? TweakId = null, string? Uri = null, int? Tab = null, string? TabLabel = null);

    readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly CpuSampler _cpu = new();
    readonly Lazy<CounterSampler> _counters = new(() => new CounterSampler());
    readonly NetSampler _net = new();
    readonly GpuMonitor _nvidia = new();
    readonly ProcessSampler _processSampler = new();
    readonly History _cpuHistory = new(60), _ramHistory = new(60), _gpuHistory = new(60), _diskHistory = new(60), _netHistory = new(60), _diskBusy = new(15);
    readonly PingRow _gateway;

    NetworkInterface? _activeNic;
    volatile GpuSample? _nvidiaSample;
    List<ProcessUsage> _top = [];
    bool _processBusy, _visible = true;
    int _tick;
    double _ramPercent, _downMbps, _upMbps;

    public ObservableCollection<PingRow> Pings { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];

    public ICommand OpenUriCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

    // Changing the language restarts the app (App listens).
    public event Action<string>? LanguageChangeRequested;

    string _cpuText = "–", _cpuSub = "", _ramText = "–", _ramSub = "", _gpuText = "–", _gpuSub = "", _gpuName = "GPU",
        _diskText = "–", _diskSub = "", _netText = "–", _netSub = "", _statusText = T("Checking…"), _message = "";
    double[] _cpuSeries = [], _ramSeries = [], _gpuSeries = [], _diskSeries = [], _netSeries = [];
    Brush _statusBrush = Palette.Muted;
    bool _busy;
    int _issueCount;

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string CpuSub { get => _cpuSub; private set => Set(ref _cpuSub, value); }
    public double[] CpuSeries { get => _cpuSeries; private set => Set(ref _cpuSeries, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public string RamSub { get => _ramSub; private set => Set(ref _ramSub, value); }
    public double[] RamSeries { get => _ramSeries; private set => Set(ref _ramSeries, value); }
    public string GpuName { get => _gpuName; private set => Set(ref _gpuName, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string GpuSub { get => _gpuSub; private set => Set(ref _gpuSub, value); }
    public double[] GpuSeries { get => _gpuSeries; private set => Set(ref _gpuSeries, value); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }
    public string DiskSub { get => _diskSub; private set => Set(ref _diskSub, value); }
    public double[] DiskSeries { get => _diskSeries; private set => Set(ref _diskSeries, value); }
    public string NetText { get => _netText; private set => Set(ref _netText, value); }
    public string NetSub { get => _netSub; private set => Set(ref _netSub, value); }
    public double[] NetSeries { get => _netSeries; private set => Set(ref _netSeries, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public int IssueCount
    {
        get => _issueCount;
        private set
        {
            if (!Set(ref _issueCount, value)) return;
            Raise(nameof(HasIssues));
            Raise(nameof(StatusTooltip));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasIssues => IssueCount > 0;
    public string? StatusTooltip => HasIssues ? T("Show the issues on the Live tab") : null;
    public ICommand ShowIssuesCommand { get; }
    public string LanguageButton => L.Next.Code.ToUpperInvariant();
    public string LanguageTooltip => L.Next.SwitchTo; // written in the target language

    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public MainViewModel()
    {
        _activeNic = NetInfo.Active();
        _gateway = new PingRow(new PingTarget(T("Router"), Demo.On ? Demo.Router : _activeNic is null ? "" : NetInfo.Gateway(_activeNic) ?? "", PingKind.Gateway));
        Pings.Add(_gateway);
        RefreshPingTargets();

        OpenUriCommand = new RelayCommand(p => { if (p is string uri) Shell.Open(uri); });
        ShowIssuesCommand = new RelayCommand(_ => SelectedTab = TabLive, _ => HasIssues);
        ToggleLanguageCommand = new RelayCommand(_ =>
        {
            if (Busy || Updating) { Message = T("The language can be changed when the running operation finishes."); return; }
            LanguageChangeRequested?.Invoke(L.Next.Code);
        });

        InitTweaks();
        InitTools();
        InitUpdates();
        InitSelfUpdate();
        InitGameBoost();
        InitGameHealth();
        InitLatency();
        InitProcesses();
        InitPrograms();
        InitDetail();

        _nvidia.Sampled += s => _nvidiaSample = s;
        _liveTimer.Tick += (_, _) => Tick();
        _backgroundTimer.Tick += (_, _) => TickBackground();
        _backgroundTimer.Start();
        Resume();
        _ = RefreshTweaksAsync();
        _ = RefreshSystemAsync();
    }

    // Live monitoring runs while the window is visible; it stops while hidden/minimized.
    // In the tray (window hidden, not just minimized) the app may also install its own update.
    public void SetVisible(bool visible, bool inTray = false)
    {
        _inTray = inTray;
        if (_visible == visible) return;
        _visible = visible;
        if (visible) Resume();
        else Pause();
    }

    void Resume()
    {
        if (!Demo.On) _nvidia.Start();
        _cpu.Sample(); // so the time spent hidden does not distort the first sample
        _liveTimer.Start();
        _ = RefreshTweaksAsync();
        if (IsDetailNet && _settings.NetMonitorAllowed) _ = StartNetMonitorAsync();
    }

    void Pause()
    {
        _liveTimer.Stop();
        if (_session is null) // a running game keeps the graphics card values coming
        {
            _nvidia.Stop();
            _nvidiaSample = null;
        }
        _netMonitor.Stop(); // the elevated network monitor does not run while hidden
    }

    // Every 3 s: the timer, game mode, the mouse and a pending app update; every 5 min: record running programs (for the last-used info).
    int _backgroundTick;

    void TickBackground()
    {
        if (Demo.On) return;
        TimerResolution.Apply(_settings); // tries again if Windows refused it
        TickGameWatcher();
        CheckMouse();
        TryAutoInstallSelfUpdate();
        if (_backgroundTick % 100 == 10) _ = Task.Run(UsageHistory.RecordRunning);
        if (_backgroundTick % 100 == 50) _ = KeepDevicePowerSavingOffAsync();
        _backgroundTick++;
    }

    void Tick()
    {
        if (Demo.On)
        {
            DemoTick();
            return;
        }
        _tick++;

        var cpu = _cpu.Sample();
        var counters = _counters.Value.Sample();
        _cpuHistory.Add(cpu);
        CpuText = Pct(cpu);
        CpuSub = (double.IsNaN(counters.CpuMhz) ? "" : (counters.CpuMhz / 1000).ToString("0.00", L.Culture) + " GHz · ") + F("{0} thread|{0} threads", Environment.ProcessorCount);
        CpuSeries = _cpuHistory.Snapshot();

        var (used, total, percent) = SystemInfo.Memory();
        _ramPercent = percent;
        _ramHistory.Add(percent);
        RamText = Pct(percent);
        RamSub = $"{used.ToString("0.0", L.Culture)} / {total.ToString("0.0", L.Culture)} GB";
        RamSeries = _ramHistory.Snapshot();

        UpdateGpu(counters);

        double disk = double.IsNaN(counters.DiskActivePercent) ? 0 : counters.DiskActivePercent;
        _diskHistory.Add(disk);
        _diskBusy.Add(disk);
        DiskText = double.IsNaN(counters.DiskActivePercent) ? "–" : Pct(disk);
        DiskSub = double.IsNaN(counters.DiskMBps) ? "" : F("{0} MB/s", counters.DiskMBps.ToString("0.0", L.Culture));
        DiskSeries = _diskHistory.Snapshot();

        if (_tick % 5 == 0) _activeNic = NetInfo.Active();
        (_downMbps, _upMbps) = _net.Sample(_activeNic);
        _netHistory.Add(_downMbps);
        NetText = $"{_downMbps.ToString("0.0", L.Culture)} Mbps";
        NetSub = F("Upload {0} Mbps", _upMbps.ToString("0.0", L.Culture)) + " · " + (_activeNic is null ? T("no connection") : $"{_activeNic.Name} {LinkSpeed(_activeNic.Speed)}");
        NetSeries = _netHistory.Snapshot();

        foreach (var row in Pings) _ = row.SampleAsync();

        // The process lists are expensive: the full one only while its tab is open, the top users when needed for a RAM/CPU warning.
        // While a game runs, they and the regular settings check wait, so an open window costs the game nothing.
        bool gaming = _watcher.ActiveGame is not null;
        if (gaming && SelectedTab == TabProcesses) ProcessSummary = PausedText;
        if (!gaming && SelectedTab == TabProcesses && _tick % 2 == 0) _ = RefreshProcessManagerAsync();
        if (!gaming && (_ramPercent >= 85 || cpu >= 90) && _tick % 3 == 0) _ = RefreshProcessesAsync();
        if (!gaming && _tick % 30 == 0) _ = RefreshTweaksAsync();
        if (SelectedTab == TabGame) RefreshGameBoost();
        if (HasDetail && SelectedTab == TabLive && _tick % 2 == 0)
        {
            if (gaming) DetailNote = PausedText;
            else
            {
                if (DetailNote == PausedText && Detail is { } kind) DetailNote = Note(kind);
                if (IsDetailNet) RaiseNetMonitorState();
                _ = RefreshDetailAsync();
            }
        }
        UpdateIssues();
    }

    static string PausedText => T("Paused while a game runs.");

    void UpdateGpu(Core.CounterSample counters)
    {
        var nv = _nvidiaSample;
        double util = nv is not null && !double.IsNaN(nv.Utilization) ? nv.Utilization : counters.GpuPercent;
        _gpuHistory.Add(double.IsNaN(util) ? 0 : util);
        GpuSeries = _gpuHistory.Snapshot();
        GpuText = double.IsNaN(util) ? "–" : Pct(util);
        if (nv is not null)
        {
            GpuName = nv.Name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
            GpuSub = $"{nv.Temperature:0}°C · {nv.PowerW:0} W · {nv.ClockMhz:0} MHz · VRAM {(nv.MemUsedMb / 1024).ToString("0.0", L.Culture)}/{nv.MemTotalMb / 1024:0} GB";
        }
        else
        {
            GpuName = PrimaryGpuName;
            GpuSub = double.IsNaN(counters.GpuMemoryGb) ? "" : F("VRAM {0} GB in use", counters.GpuMemoryGb.ToString("0.0", L.Culture));
        }
    }

    async Task RefreshProcessesAsync()
    {
        if (_processBusy) return;
        _processBusy = true;
        try
        {
            _top = await Task.Run(() => _processSampler.Sample(15)); // the top users named in the issues
        }
        finally
        {
            _processBusy = false;
        }
    }

    // All processes with the same name are closed (after confirmation). System processes are never touched.
    void KillProcess(string name, int instances)
    {
        if (ProcessSampler.IsProtected(name)) return;
        var row = (Name: name, Instances: instances);
        var message = F("Close {0}?", row.Name) + (row.Instances > 1 ? " " + F("(all {0} processes)", row.Instances) : "") + "\n\n" + T("Unsaved data will be lost.");
        if (!Dialogs.Confirm(T("Close process"), message, T("Close"), danger: true)) return;
        int closed = 0, failed = 0;
        foreach (var p in Process.GetProcessesByName(row.Name))
            using (p)
            {
                try
                {
                    if (p.SessionId == 0) { failed++; continue; }
                    p.Kill();
                    closed++;
                }
                catch (Exception) { failed++; }
            }
        Message = failed == 0 ? F("{0} closed ({1} process).|{0} closed ({1} processes).", row.Name, closed) : F("{0}: {1} closed, {2} could not be closed (system or protected process).", row.Name, closed, failed);
        _ = RefreshProcessesAsync();
        if (SelectedTab == TabProcesses) _ = RefreshProcessManagerAsync();
    }

    void UpdateIssues()
    {
        var found = new List<IssueData>();
        string TopRam(int n) => string.Join(", ", _top.Take(n).Select(p => $"{p.Name} {p.RamMb:0} MB"));
        string TopCpu(int n) => string.Join(", ", _top.OrderByDescending(p => p.CpuPercent).Take(n).Select(p => $"{p.Name} {Pct(p.CpuPercent)}"));

        if (_ramPercent >= 85)
            found.Add(new("live.ram", _ramPercent >= 93 ? Palette.Bad : Palette.Warn, F("High RAM usage ({0})", Pct(_ramPercent)),
                F("Top users: {0}", TopRam(3)),
                T("Close browsers, launchers and chat apps while gaming (Processes tab). When RAM is full, Windows writes to disk and the game stutters."),
                Tab: TabProcesses, TabLabel: T("Go to Processes")));

        var recentCpu = _cpuHistory.Snapshot().TakeLast(10).ToArray();
        if (recentCpu.Length == 10 && recentCpu.Average() >= 90)
            found.Add(new("live.cpu", Palette.Warn, F("Processor constantly busy ({0})", Pct(recentCpu.Average())), F("Top users: {0}", TopCpu(3)),
                T("Close apps that scan, update or process video in the background. If the game itself fills the CPU, set an FPS limit."),
                Tab: TabProcesses, TabLabel: T("Go to Processes")));

        var disk = _diskBusy.Snapshot();
        if (disk.Length == 15 && disk.Average() >= 90)
            found.Add(new("live.disk", Palette.Warn, F("Disk constantly busy ({0})", Pct(disk.Average())), "",
                T("An update, download or virus scan may be running in the background. If the game is on an HDD, moving it to an SSD reduces stutter.")));

        if (!Demo.On && Catalog.Nic is { IsEthernet: true, SupportsGigabit: true } nic && nic.SpeedBps is > 0 and < 1_000_000_000)
            found.Add(new("live.link", Palette.Warn, F("Ethernet is linked at {0} Mbps", nic.SpeedBps / 1_000_000), F("{0} supports gigabit.", nic.Description),
                T("If the adapter's power saving settings are off, the cause is most likely the cable or the router port: try a Cat5e/Cat6 cable, try another router port and check the connectors. If your internet plan is faster than 100 Mbps, it is capped here.")));

        bool busyLine = _downMbps >= 20 || _upMbps >= 10;
        string lineNote = busyLine
            ? " " + F("There is background network traffic right now (↓{0} / ↑{1} Mbps): pause Steam, Windows Update or torrent downloads.", _downMbps.ToString("0", L.Culture), _upMbps.ToString("0", L.Culture))
            : "";
        foreach (var row in Pings)
        {
            var t = row.Target;
            if (t.Samples < 10) continue;
            string key = $"live.ping.{t.Name}.{t.Host}";
            if (double.IsNaN(t.Average))
            {
                found.Add(new(key + ".down", Palette.Bad, F("{0} ({1}) is not responding", t.Name, t.Host), "", T("Check the internet connection and the cable.")));
                continue;
            }
            string stats = F("avg {0} ms · {1}–{2} ms · jitter {3} ms · loss {4}", t.Average.ToString("0.0", L.Culture), t.Min.ToString("0"), t.Max.ToString("0"), t.Jitter.ToString("0.0", L.Culture), Pct(t.LossPercent));
            if (t.Kind == PingKind.Gateway)
            {
                if (t.LossPercent > 0 || t.Average >= 5 || t.Jitter >= 3)
                    found.Add(new(key, Palette.Bad, T("Local network problem (between the computer and the router)"), stats,
                        T("Check the cable and the router port. If you use Wi-Fi, switch to a cable or move closer to the router. Restart the router.") + lineNote));
                continue;
            }
            if (t.LossPercent >= 2)
                found.Add(new(key + ".loss", Palette.Bad, F("Packet loss: {0} {1}", t.Name, Pct(t.LossPercent)), stats,
                    T("If the router side is clean, the problem may be the line. Restart the router; if it continues, ask your provider to check the line.") + lineNote));
            if (t.Jitter >= 5)
                found.Add(new(key + ".jitter", Palette.Warn, F("Ping is fluctuating: {0} (jitter {1} ms)", t.Name, t.Jitter.ToString("0.0", L.Culture)), stats,
                    busyLine ? lineNote.Trim() : T("If it happens while someone at home downloads or streams, run the load test on the Network tab (bufferbloat)."),
                    Tab: TabNetwork, TabLabel: T("Go to network test")));
            if (t.Average >= 60)
                found.Add(new(key + ".high", Palette.Warn, F("High ping: {0} {1} ms", t.Name, t.Average.ToString("0")), stats,
                    T("This can be normal if the server is far away. Pick the closest region in the game.") + lineNote));
        }

        if (_nvidiaSample is { } g)
        {
            if (g.ThermalThrottle)
                found.Add(new("live.gpu.throttle", Palette.Bad, T("GPU is slowing down because of heat or power"), $"{g.Temperature:0}°C · {g.ClockMhz:0} MHz", HeatHint));
            else if (g.Temperature >= 87)
                found.Add(new("live.gpu.temp", Palette.Warn, F("GPU is hot ({0}°C)", g.Temperature.ToString("0")), "", HeatHint));
        }

        // Shared interrupt lines of the graphics card, USB controllers, audio and network (read with the Settings tab).
        foreach (var shared in _sharedIrqs.Where(s => s.Devices.Any(d => _gameDevices.Contains(d.InstanceId))))
            found.Add(new($"irq.{shared.Irq}", Palette.Warn, F("Devices share the interrupt line IRQ {0}", shared.Irq),
                string.Join(", ", shared.Devices.Select(d => d.Name).Distinct()),
                T("Devices on a shared line wait for each other. Turn on MSI mode for them on the Settings tab (Interrupts (MSI)) and restart."),
                Tab: TabTweaks, TabLabel: T("Go to Settings")));
        foreach (var port in _mice.Where(m => m.ThirdParty))
            found.Add(new("mouse.port", Palette.Warn, F("The mouse is on an add-on USB controller ({0})", Devices.VendorName(port.Vendor) ?? port.Controller ?? "?"),
                $"{port.Mouse} · {port.Controller}",
                T("Add-on USB chips usually add latency and jitter compared with the processor's or chipset's own controller. Try the other rear ports and check the result with the polling test on the Latency tab."),
                Tab: TabLatency, TabLabel: T("Go to Latency")));

        if (MouseNeedsFix && _mouse is { } mouse)
            found.Add(new(MouseIssueId, Palette.Warn,
                mouse.Acceleration ? T("Mouse acceleration is on") : F("Pointer speed is {0}, not {1}", mouse.Speed, MouseSettings.RecommendedSpeed),
                MouseStatusText,
                F("For consistent aim, turn off Enhance pointer precision and use pointer speed {0} (Windows default); set the sensitivity in the game. Fix keeps them this way from now on.", MouseSettings.RecommendedSpeed),
                MouseIssueId));

        // High-impact settings one by one, the others in a single line (mouse acceleration has its own issue above).
        var problems = Tweaks.Where(t => t.State == CheckState.Problem && t.Id != "input.mouseaccel").ToList();
        foreach (var t in problems.Where(t => t.Tweak.Impact == Impact.High))
            found.Add(new("tweak." + t.Id, Palette.Bad, t.Title, F("Current: {0} · recommended: {1}", t.CurrentLabel, t.Recommended?.Label), t.Description, t.Id));
        int rest = problems.Count(t => t.Tweak.Impact != Impact.High);
        if (rest > 0)
            found.Add(new("tweak.rest", Palette.Warn, F("{0} setting is not at the recommended value|{0} settings are not at the recommended value", rest),
                string.Join(", ", problems.Where(t => t.Tweak.Impact != Impact.High).Take(4).Select(t => t.Title)) + (rest > 4 ? "…" : ""),
                T("On the Settings tab you can change them one by one or set all of them to the recommended value."), Tab: TabTweaks, TabLabel: T("Go to Settings")));

        if (UpdatableCount > 0)
            found.Add(new("updates.apps", Palette.Warn, F("{0} app has an update|{0} apps have updates", UpdatableCount),
                string.Join(", ", Packages.Take(4).Select(p => p.Name)) + (UpdatableCount > 4 ? "…" : ""),
                T("On the Updates tab you can update them one by one or all at once."), Tab: TabUpdates, TabLabel: T("Go to Updates")));
        if (Drivers.Count > 0)
            found.Add(new("updates.drivers", Palette.Warn, F("{0} driver update available (Windows Update)|{0} driver updates available (Windows Update)", Drivers.Count),
                string.Join(", ", Drivers.Take(3).Select(d => d.Title)) + (Drivers.Count > 3 ? "…" : ""),
                T("You can install them from the driver list at the bottom of the Updates tab."), Tab: TabUpdates, TabLabel: T("Go to Updates")));

        foreach (var info in SystemItems.Where(i => i.Item.State == CheckState.Problem))
            found.Add(new("info." + info.Group + info.Label, Palette.Warn, $"{info.Label}: {info.Value}", info.Group, info.Hint ?? "", Uri: info.Uri,
                Tab: info.Uri is null ? TabSystem : null, TabLabel: info.Uri is null ? T("Go to System") : null));

        SyncIssues(found);
        IssueCount = found.Count;

        if (Busy) return;
        StatusText = found.Count == 0 ? T("No issues") : F("{0} issue|{0} issues", found.Count);
        StatusBrush = found.Count == 0 ? Palette.Ok : found.Any(f => f.Severity == Palette.Bad) ? Palette.Bad : Palette.Warn;
    }

    void SyncIssues(List<IssueData> found)
    {
        var keys = found.Select(f => f.Key).ToHashSet();
        for (int i = Issues.Count - 1; i >= 0; i--)
            if (!keys.Contains(Issues[i].Key)) Issues.RemoveAt(i);
        foreach (var f in found)
        {
            var row = Issues.FirstOrDefault(r => r.Key == f.Key);
            if (row is null)
            {
                row = new IssueRow(f.Key);
                Issues.Add(row);
            }
            row.Title = f.Title;
            row.Detail = f.Detail;
            row.Suggestion = f.Suggestion;
            row.Severity = f.Severity;
            row.TweakId = f.TweakId;
            row.Uri = f.Uri;
            row.TabTarget = f.Tab;
            row.TabLabel = f.TabLabel ?? "";
        }
    }

    // Only the router and the DNS server in use are pinged. When the router is also the DNS server, one row is enough.
    // A changed DNS server (for example after the DNS test) gets a new row, so its history does not mix with the old one.
    void RefreshPingTargets()
    {
        if (!Demo.On && _activeNic is not null && NetInfo.Gateway(_activeNic) is string gw) _gateway.Target.Host = gw;
        var dns = Demo.On ? Demo.DnsServer : _activeNic is null ? null : NetInfo.Dns(_activeNic);
        var row = Pings.FirstOrDefault(p => p != _gateway);
        if (dns is null || dns == _gateway.Target.Host)
        {
            if (row is not null) Pings.Remove(row);
            return;
        }
        if (row?.Target.Host == dns) return;
        if (row is not null) Pings.Remove(row);
        Pings.Add(new PingRow(new PingTarget(T("DNS server"), dns, PingKind.Internet)));
    }

    static string LinkSpeed(long bps) => bps >= 1_000_000_000 ? $"{(bps / 1e9).ToString("0.#", L.Culture)} Gbps" : $"{bps / 1e6:0} Mbps";

    public void Dispose()
    {
        _liveTimer.Stop();
        _backgroundTimer.Stop();
        _updateTimer.Stop();
        _progressTimer.Stop();
        _watcher.Stop();
        TimerResolution.StopHelper();
        _managerGpu?.Dispose();
        _nvidia.Dispose();
        _netMonitor.Dispose();
        _gpuProcesses?.Dispose();
        _sizeCts?.Cancel();
        if (_counters.IsValueCreated) _counters.Value.Dispose();
        _memorySampler?.Dispose();
    }
}
