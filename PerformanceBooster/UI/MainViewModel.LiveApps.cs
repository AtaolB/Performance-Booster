using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// The running apps under the Live cards: processor, memory, disk, graphics and network use of each (same names combined),
// read at the interval chosen on the tab (the cards use it too) and paused while a game runs or the window is behind
// another one. Pinned apps stay on top; the search box filters by name. The network speed per app comes from the network
// monitor, which runs with administrator rights while the tab is open.
public sealed partial class MainViewModel
{
    public enum LiveAppColumn { Name, Cpu, Ram, Read, Write, Gpu, Down, Up }

    readonly ProcessRates _appRates = new();
    readonly NetMonitorClient _netMonitor = new();
    readonly Dictionary<string, LiveAppRow> _appRows = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ImageSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase); // by app name; read once
    GpuProcessSampler? _gpuProcesses;
    bool _appsBusy, _netStarting, _appsDescending = true;
    LiveAppColumn _appSort = LiveAppColumn.Cpu;
    string _liveAppsNote = "", _appSearch = "";

    public ObservableCollection<LiveAppRow> LiveApps { get; } = [];
    public IntervalSetting LiveInterval { get; private set; } = null!;
    public ICommand SortLiveAppsCommand { get; private set; } = null!;
    public ICommand StartNetMonitorCommand { get; private set; } = null!;
    public ICommand KillLiveAppCommand { get; private set; } = null!;
    public ICommand PinLiveAppCommand { get; private set; } = null!;
    public ICommand ClearAppSearchCommand { get; private set; } = null!;

    public string LiveAppsNote { get => _liveAppsNote; private set => Set(ref _liveAppsNote, value); }
    public bool CanStartNetMonitor => !Demo.On && !_netMonitor.Running && !_netStarting;

    public string AppSearch
    {
        get => _appSearch;
        set { if (Set(ref _appSearch, value ?? "")) ShowLiveApps(); }
    }

    // Column titles with the sort arrow.
    public string AppNameHeader => AppHeader(LiveAppColumn.Name, T("App"));
    public string AppCpuHeader => AppHeader(LiveAppColumn.Cpu, "CPU");
    public string AppRamHeader => AppHeader(LiveAppColumn.Ram, "RAM");
    public string AppReadHeader => AppHeader(LiveAppColumn.Read, T("Disk read"));
    public string AppWriteHeader => AppHeader(LiveAppColumn.Write, T("Disk write"));
    public string AppGpuHeader => AppHeader(LiveAppColumn.Gpu, "GPU");
    public string AppDownHeader => AppHeader(LiveAppColumn.Down, T("Download"));
    public string AppUpHeader => AppHeader(LiveAppColumn.Up, T("Upload"));

    string AppHeader(LiveAppColumn column, string text) => column == _appSort ? text + (_appsDescending ? " ▼" : " ▲") : text;

    // The hover text of the card charts.
    public Func<double, string> PercentChartFormat { get; } = v => Pct(v);
    public Func<double, string> RamChartFormat { get; } = v => Pct(v) + " · " + (v / 100 * SystemInfo.Memory().TotalGb).ToString("0.0", L.Culture) + " GB";
    public Func<double, string> NetChartFormat { get; } = v => v.ToString("0.0", L.Culture) + " Mbps";

    void InitLiveApps()
    {
        LiveInterval = new IntervalSetting(_settings.LiveIntervalSeconds);
        LiveInterval.Changed += seconds =>
        {
            _settings.LiveIntervalSeconds = seconds;
            _settings.Save();
            _cardTimer.Interval = TimeSpan.FromSeconds(seconds);
        };
        _cardTimer.Interval = TimeSpan.FromSeconds(LiveInterval.Seconds);

        SortLiveAppsCommand = new RelayCommand(p =>
        {
            if (p is not string name || !Enum.TryParse<LiveAppColumn>(name, out var column)) return;
            _appsDescending = column == _appSort ? !_appsDescending : column != LiveAppColumn.Name;
            _appSort = column;
            foreach (var header in new[] { nameof(AppNameHeader), nameof(AppCpuHeader), nameof(AppRamHeader), nameof(AppReadHeader), nameof(AppWriteHeader),
                         nameof(AppGpuHeader), nameof(AppDownHeader), nameof(AppUpHeader) })
                Raise(header);
            ShowLiveApps();
        });
        StartNetMonitorCommand = new RelayCommand(async _ => await StartNetMonitorAsync(), _ => CanStartNetMonitor);
        KillLiveAppCommand = new RelayCommand(p => { if (p is LiveAppRow { CanKill: true } r) KillProcess(r.Name, r.Instances); });
        PinLiveAppCommand = new RelayCommand(p =>
        {
            if (p is not LiveAppRow row) return;
            row.IsPinned = !row.IsPinned;
            _settings.PinnedApps.RemoveAll(n => n.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            if (row.IsPinned) _settings.PinnedApps.Add(row.Name);
            _settings.Save();
            ShowLiveApps();
        });
        ClearAppSearchCommand = new RelayCommand(_ => AppSearch = "");
        LiveAppsNote = AppsNote();
        if (Demo.On) ApplyApps(Demo.Apps(), hasNet: true);
    }

    string AppsNote() => _netMonitor.Running || Demo.On
        ? T("Processes with the same name are combined. Right-click an app to close it.")
        : T("Processes with the same name are combined. The network speed per app needs administrator permission.");

    // The Live tab opened or the window came back: the graphics and network samplers start; leaving the tab stops them.
    void StartLiveApps()
    {
        if (Demo.On || !_visible || SelectedTab != TabLive) return;
        _appRates.Sample(); // the first sample: rates start from the next one
        if (!_netMonitor.Running && (Elevated.IsAdmin || _settings.NetMonitorAllowed)) _ = StartNetMonitorAsync();
        _ = RefreshLiveAppsAsync();
    }

    void StopLiveApps()
    {
        _netMonitor.Stop();
        _gpuProcesses?.Dispose();
        _gpuProcesses = null;
        RaiseNetMonitorState();
    }

    void RaiseNetMonitorState()
    {
        Raise(nameof(CanStartNetMonitor));
        LiveAppsNote = AppsNote();
        CommandManager.InvalidateRequerySuggested();
    }

    async Task StartNetMonitorAsync()
    {
        if (_netMonitor.Running || _netStarting) return;
        _netStarting = true;
        RaiseNetMonitorState();
        try
        {
            if (await _netMonitor.StartAsync())
            {
                if (!_settings.NetMonitorAllowed)
                {
                    _settings.NetMonitorAllowed = true;
                    _settings.Save();
                }
            }
            else Message = _netMonitor.Error ?? T("The network monitor did not start.");
        }
        finally
        {
            _netStarting = false;
            RaiseNetMonitorState();
        }
    }

    // From the card timer; a reading still running is not doubled (at short intervals the next one simply waits).
    async Task RefreshLiveAppsAsync()
    {
        if (Demo.On || _appsBusy || SelectedTab != TabLive) return;
        if (_watcher.ActiveGame is not null)
        {
            LiveAppsNote = PausedText;
            return;
        }
        if (!ReadingAllowed) return;
        _appsBusy = true;
        try
        {
            _gpuProcesses ??= await Task.Run(() => new GpuProcessSampler());
            var gpu = _gpuProcesses;
            var net = _netMonitor.Running ? _netMonitor.Rates : null;
            var known = _appIcons.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (usage, icons) = await Task.Run(() => SampleApps(gpu, net, known));
            foreach (var (name, icon) in icons)
            {
                _appIcons[name] = icon;
                if (_appRows.TryGetValue(name, out var row)) row.Icon = icon;
            }
            if (SelectedTab != TabLive) return;
            ApplyApps(usage, net is not null);
            if (LiveAppsNote == PausedText) LiveAppsNote = AppsNote();
        }
        finally
        {
            _appsBusy = false;
        }
    }

    // On a background thread; the icons of apps seen for the first time are read here too (from the exe file).
    (List<AppUsage> Usage, List<(string Name, ImageSource? Icon)> Icons) SampleApps(GpuProcessSampler? gpu, IReadOnlyDictionary<int, (long Down, long Up)>? net, HashSet<string> known)
    {
        var perPid = gpu?.Sample() ?? [];
        var list = new List<AppUsage>();
        var icons = new List<(string, ImageSource?)>();
        foreach (var g in ProcessSampler.Group(_appRates.Sample()))
        {
            double gpuPercent = 0;
            long down = 0, up = 0;
            foreach (var id in g.Ids)
            {
                if (perPid.TryGetValue(id, out var v)) gpuPercent += v.Percent;
                if (net is not null && net.TryGetValue(id, out var n)) { down += n.Down; up += n.Up; }
            }
            list.Add(new(g.Name, g.Count, g.CpuPercent, g.PrivateWorkingSet, Math.Min(100, gpuPercent), down, up, g.CanKill, g.ReadPerSec, g.WritePerSec));
            if (!known.Contains(g.Name))
                icons.Add((g.Name, g.Ids.Select(AppProcesses.ImagePath).FirstOrDefault(p => p is not null) is { } path ? ProgramIcons.ForExe(path) : null));
        }
        return (list, icons);
    }

    // Rows are kept between readings and updated in place, so the list does not jump while it refreshes.
    void ApplyApps(List<AppUsage> usage, bool hasNet)
    {
        var names = new HashSet<string>(usage.Select(u => u.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _appRows.Keys.Where(k => !names.Contains(k)).ToList()) _appRows.Remove(gone);
        foreach (var u in usage)
        {
            if (!_appRows.TryGetValue(u.Name, out var row))
            {
                row = new LiveAppRow(u.Name)
                {
                    Icon = _appIcons.GetValueOrDefault(u.Name),
                    IsPinned = _settings.PinnedApps.Contains(u.Name, StringComparer.OrdinalIgnoreCase),
                };
                _appRows[u.Name] = row;
            }
            row.Update(u, hasNet);
        }
        ShowLiveApps();
    }

    // The rows that match the search, pinned ones first, the rest by the chosen column; the list is moved, not rebuilt.
    void ShowLiveApps()
    {
        var words = TextSearch.Words(AppSearch);
        Func<LiveAppRow, double> key = _appSort switch
        {
            LiveAppColumn.Ram => r => r.Usage.RamBytes,
            LiveAppColumn.Read => r => r.Usage.ReadPerSec,
            LiveAppColumn.Write => r => r.Usage.WritePerSec,
            LiveAppColumn.Gpu => r => r.Usage.GpuPercent,
            LiveAppColumn.Down => r => r.Usage.Down,
            LiveAppColumn.Up => r => r.Usage.Up,
            _ => r => r.Usage.CpuPercent,
        };
        var visible = _appRows.Values.Where(r => TextSearch.Matches(words, r.Name)).OrderByDescending(r => r.IsPinned);
        var sorted = (_appSort == LiveAppColumn.Name
                ? _appsDescending ? visible.ThenByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase) : visible.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                : (_appsDescending ? visible.ThenByDescending(key) : visible.ThenBy(key)).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        var keep = sorted.ToHashSet();
        for (int i = LiveApps.Count - 1; i >= 0; i--)
            if (!keep.Contains(LiveApps[i])) LiveApps.RemoveAt(i);
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = LiveApps.IndexOf(sorted[i]);
            if (at < 0) LiveApps.Insert(i, sorted[i]);
            else if (at != i) LiveApps.Move(at, i);
        }
    }
}
