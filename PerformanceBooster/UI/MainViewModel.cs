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
    public const int TabLive = 0, TabIssues = 1, TabUpdates = 3, TabPrograms = 4, TabTweaks = 5, TabNetwork = 6, TabGame = 7, TabStartup = 8, TabSystem = 9, TabProcesses = 10;

    static string HeatHint => GameSession.HeatHint;

    sealed record IssueData(string Key, Brush Severity, string Title, string Detail, string Suggestion,
        string? TweakId = null, string? Uri = null, int? Tab = null, string? TabLabel = null, IssueGroup Group = IssueGroup.Now);

    readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _cardTimer = new(); // the Live cards and the app list, at the interval chosen on the Live tab
    readonly DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly CpuSampler _cpu = new();
    readonly Lazy<CounterSampler> _counters = new(() => new CounterSampler());
    readonly NetSampler _net = new();
    readonly GpuMonitor _nvidia = new();
    readonly ProcessSampler _processSampler = new();
    readonly History _cpuHistory = new(60), _ramHistory = new(60), _gpuHistory = new(60), _diskHistory = new(60), _netHistory = new(60), _diskBusy = new(15);

    NetworkInterface? _activeNic;
    volatile GpuSample? _nvidiaSample;
    List<ProcessUsage> _top = [];
    bool _processBusy, _visible = true, _active = true;
    int _tick;
    double _ramPercent, _downMbps, _upMbps, _cpuNow;
    DateTime _tempsAt, _diskTempAt;

    public ObservableCollection<IssueRow> Issues { get; } = [];

    // The Issues tab: the issues by where they are solved, and the hardware notes (BIOS, memory modules) last.
    public IReadOnlyList<IssueSection> IssueSections { get; } = [.. Enum.GetValues<IssueGroup>().Select(g => new IssueSection(g))];

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
            Raise(nameof(NoIssues));
            Raise(nameof(IssuesTabTitle));
            Raise(nameof(IssuesSummary));
            Raise(nameof(StatusTooltip));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasIssues => IssueCount > 0;
    public bool NoIssues => IssueCount == 0;
    public string IssuesTabTitle => IssueCount > 0 ? F("Issues ({0})", IssueCount) : T("Issues");
    public string IssuesSummary => IssueCount == 0
        ? T("No issues right now.")
        : F("{0} issue found, grouped by where it is solved.|{0} issues found, grouped by where they are solved.", IssueCount);
    public string? StatusTooltip => HasIssues ? T("Show the issues") : null;
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

        OpenUriCommand = new RelayCommand(p => { if (p is string uri) Shell.Open(uri); });
        ShowIssuesCommand = new RelayCommand(_ => SelectedTab = TabIssues, _ => HasIssues);
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
        InitLiveApps();
        InitSensors();

        _nvidia.Sampled += s => _nvidiaSample = s;
        _liveTimer.Tick += (_, _) => Tick();
        _cardTimer.Tick += (_, _) => SampleCards();
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

    // The window is in front: the Live and Sensors tabs read only then (and not while a game runs).
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active && _visible)
        {
            StartSensors();
            StartLiveApps();
        }
    }

    bool ReadingAllowed => Demo.On || (_visible && _active && _watcher.ActiveGame is null);

    void Resume()
    {
        if (!Demo.On) _nvidia.Start();
        _cpu.Sample(); // so the time spent hidden does not distort the first sample
        _liveTimer.Start();
        _cardTimer.Start();
        StartSensors();
        StartLiveApps();
        _ = RefreshTweaksAsync();
    }

    void Pause()
    {
        _liveTimer.Stop();
        _cardTimer.Stop();
        if (_session is null) // a running game keeps the graphics card values coming
        {
            _nvidia.Stop();
            _nvidiaSample = null;
        }
        StopLiveApps(); // the elevated network monitor does not run while hidden
    }

    // Every 3 s: the timer, game mode, the mouse and a pending app update; every 5 min: record running programs (for the last-used info).
    int _backgroundTick;

    void TickBackground()
    {
        if (Demo.On) return;
        TimerResolution.Apply(_settings); // tries again if Windows refused it
        TickGameWatcher();
        CheckMouse();
        CheckTemperaturesInBackground();
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
        if (_tick % 5 == 0) _activeNic = NetInfo.Active();

        // The process lists are expensive: the full one only while its tab is open, the top users when needed for a RAM/CPU warning.
        // While a game runs, they and the regular settings check wait, so an open window costs the game nothing.
        bool gaming = _watcher.ActiveGame is not null;
        if (gaming && SelectedTab == TabProcesses) ProcessSummary = PausedText;
        if (!gaming && SelectedTab == TabProcesses && _tick % 2 == 0) _ = RefreshProcessManagerAsync();
        if (!gaming && (_ramPercent >= 85 || _cpuNow >= 90) && _tick % 3 == 0) _ = RefreshProcessesAsync();
        if (!gaming && _tick % 30 == 0) _ = RefreshTweaksAsync();
        if (SelectedTab == TabGame) RefreshGameBoost();
        UpdateIssues();
    }

    // The Live cards (and the app list under them) at the chosen interval; nothing is read while a game runs or the window
    // is behind another one.
    void SampleCards()
    {
        if (Demo.On || !ReadingAllowed) return;
        var cpu = _cpu.Sample();
        _cpuNow = cpu;
        var counters = _counters.Value.Sample();
        _cpuMhz = counters.CpuMhz;
        _cpuHistory.Add(cpu);
        CpuText = Pct(cpu);
        CpuSub = (_cpuTemp is null ? "" : Celsius(_cpuTemp) + " · ") + (double.IsNaN(counters.CpuMhz) ? "" : (counters.CpuMhz / 1000).ToString("0.00", L.Culture) + " GHz · ")
                 + F("{0} thread|{0} threads", Environment.ProcessorCount);
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
        DiskSub = string.Join(" · ", new[] { double.IsNaN(counters.DiskMBps) ? "" : F("{0} MB/s", counters.DiskMBps.ToString("0.0", L.Culture)), Celsius(_diskTemp) }.Where(s => s.Length > 0));
        DiskSeries = _diskHistory.Snapshot();

        (_downMbps, _upMbps) = _net.Sample(_activeNic);
        _netHistory.Add(_downMbps);
        NetText = $"{_downMbps.ToString("0.0", L.Culture)} Mbps";
        NetSub = F("Upload {0} Mbps", _upMbps.ToString("0.0", L.Culture)) + " · " + (_activeNic is null ? T("no connection") : $"{_activeNic.Name} {LinkSpeed(_activeNic.Speed)}");
        NetSeries = _netHistory.Snapshot();

        ReadTemperaturesForLive();
        if (SelectedTab == TabLive) _ = RefreshLiveAppsAsync();
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
            GpuSub = string.Join(" · ", new[] { Celsius(_gpuTemp), double.IsNaN(counters.GpuMemoryGb) ? "" : F("VRAM {0} GB in use", counters.GpuMemoryGb.ToString("0.0", L.Culture)) }.Where(s => s.Length > 0));
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
                T("If the adapter's power saving settings are off, the cause is most likely the cable or the router port: try a Cat5e/Cat6 cable, try another router port and check the connectors. If your internet plan is faster than 100 Mbps, it is capped here."), Group: IssueGroup.Network));

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
                Tab: TabTweaks, TabLabel: T("Go to Settings"), Group: IssueGroup.Settings));
        foreach (var port in _mice.Where(m => m.ThirdParty))
            found.Add(new("mouse.port", Palette.Warn, F("The mouse is on an add-on USB controller ({0})", Devices.VendorName(port.Vendor) ?? port.Controller ?? "?"),
                $"{port.Mouse} · {port.Controller}",
                T("Add-on USB chips usually add latency and jitter compared with the processor's or chipset's own controller. Try the other rear ports and check the result with the polling test on the Latency tab."),
                Tab: TabLatency, TabLabel: T("Go to Latency"), Group: IssueGroup.Devices));

        if (MouseNeedsFix && _mouse is { } mouse)
            found.Add(new(MouseIssueId, Palette.Warn,
                mouse.Acceleration ? T("Mouse acceleration is on") : F("Pointer speed is {0}, not {1}", mouse.Speed, MouseSettings.RecommendedSpeed),
                MouseStatusText,
                F("For consistent aim, turn off Enhance pointer precision and use pointer speed {0} (Windows default); set the sensitivity in the game. Fix keeps them this way from now on.", MouseSettings.RecommendedSpeed),
                MouseIssueId, Group: IssueGroup.Settings));

        // High-impact settings one by one, the others in a single line (mouse acceleration has its own issue above).
        var problems = Tweaks.Where(t => t.State == CheckState.Problem && t.Id != "input.mouseaccel").ToList();
        foreach (var t in problems.Where(t => t.Tweak.Impact == Impact.High))
            found.Add(new("tweak." + t.Id, Palette.Bad, t.Title, F("Current: {0} · recommended: {1}", t.CurrentLabel, t.Recommended?.Label), t.Description, t.Id, Group: IssueGroup.Settings));
        int rest = problems.Count(t => t.Tweak.Impact != Impact.High);
        if (rest > 0)
            found.Add(new("tweak.rest", Palette.Warn, F("{0} setting is not at the recommended value|{0} settings are not at the recommended value", rest),
                string.Join(", ", problems.Where(t => t.Tweak.Impact != Impact.High).Take(4).Select(t => t.Title)) + (rest > 4 ? "…" : ""),
                T("On the Settings tab you can change them one by one or set all of them to the recommended value."), Tab: TabTweaks, TabLabel: T("Go to Settings"), Group: IssueGroup.Settings));

        if (UpdatableCount > 0)
            found.Add(new("updates.apps", Palette.Warn, F("{0} app has an update|{0} apps have updates", UpdatableCount),
                string.Join(", ", Packages.Take(4).Select(p => p.Name)) + (UpdatableCount > 4 ? "…" : ""),
                T("On the Updates tab you can update them one by one or all at once."), Tab: TabUpdates, TabLabel: T("Go to Updates"), Group: IssueGroup.Updates));
        if (Drivers.Count > 0)
            found.Add(new("updates.drivers", Palette.Warn, F("{0} driver update available (Windows Update)|{0} driver updates available (Windows Update)", Drivers.Count),
                string.Join(", ", Drivers.Take(3).Select(d => d.Title)) + (Drivers.Count > 3 ? "…" : ""),
                T("You can install them from the driver list at the bottom of the Updates tab."), Tab: TabUpdates, TabLabel: T("Go to Updates"), Group: IssueGroup.Updates));

        foreach (var info in SystemItems.Where(i => i.Item.State == CheckState.Problem))
            found.Add(new("info." + info.Group + info.Label, Palette.Warn, $"{info.Label}: {info.Value}", info.Group, info.Hint ?? "", Uri: info.Uri,
                Tab: info.Uri is null ? TabSystem : null, TabLabel: info.Uri is null ? T("Go to System") : null, Group: IssueGroup.System));
        // Done in the BIOS or with the hardware itself (memory profile, Resizable BAR, modules, slots): notes, not issues.
        foreach (var info in SystemItems.Where(i => i.Item.Note && i.Hint is not null))
            found.Add(new("note." + info.Group + info.Label, Palette.Info, $"{info.Label}: {info.Value}", info.Group, info.Hint!, Group: IssueGroup.Hardware));

        SyncIssues(found);
        int count = found.Count(f => f.Group != IssueGroup.Hardware);
        IssueCount = count;

        if (Busy) return;
        StatusText = count == 0 ? T("No issues") : F("{0} issue|{0} issues", count);
        StatusBrush = count == 0 ? Palette.Ok : found.Any(f => f.Severity == Palette.Bad) ? Palette.Bad : Palette.Warn;
    }

    // Rows are kept between checks; each one also sits in the section of its group.
    void SyncIssues(List<IssueData> found)
    {
        var keys = found.Where(f => f.Group != IssueGroup.Hardware).Select(f => f.Key).ToHashSet();
        for (int i = Issues.Count - 1; i >= 0; i--)
            if (!keys.Contains(Issues[i].Key)) Issues.RemoveAt(i);
        foreach (var section in IssueSections) section.Sync(found.Where(f => f.Group == section.Group).Select(f => f.Key));
        foreach (var f in found)
        {
            var section = IssueSections.First(s => s.Group == f.Group);
            var row = section.Rows.FirstOrDefault(r => r.Key == f.Key);
            if (row is null)
            {
                row = new IssueRow(f.Key);
                section.Rows.Add(row);
                section.RaiseRows();
            }
            if (f.Group != IssueGroup.Hardware && !Issues.Contains(row)) Issues.Add(row);
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

    static string LinkSpeed(long bps) => bps >= 1_000_000_000 ? $"{(bps / 1e9).ToString("0.#", L.Culture)} Gbps" : $"{bps / 1e6:0} Mbps";

    public void Dispose()
    {
        _liveTimer.Stop();
        _cardTimer.Stop();
        _sensorTimer.Stop();
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
        _sensors.Dispose();
    }
}
