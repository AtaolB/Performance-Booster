using System.Collections.ObjectModel;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Details opened by clicking a live card: the top users of that resource. The whole list is on the Processes tab.
public sealed partial class MainViewModel
{
    const int TopCount = 8;

    public const string DetailCpu = "cpu", DetailRam = "ram", DetailGpu = "gpu", DetailDisk = "disk", DetailNet = "net";

    readonly ProcessRates _detailRates = new();
    readonly NetMonitorClient _netMonitor = new();
    GpuProcessSampler? _gpuProcesses;
    string? _detail;
    bool _detailBusy, _netStarting;
    string _detailNote = "";

    public ObservableCollection<UsageRow> DetailRows { get; } = [];

    public ICommand ShowDetailCommand { get; private set; } = null!;
    public ICommand CloseDetailCommand { get; private set; } = null!;
    public ICommand StartNetMonitorCommand { get; private set; } = null!;
    public ICommand KillUsageCommand { get; private set; } = null!;

    public string? Detail
    {
        get => _detail;
        private set
        {
            if (!Set(ref _detail, value)) return;
            foreach (var n in new[] { nameof(HasDetail), nameof(DetailTitle), nameof(DetailPrimary), nameof(DetailSecondary), nameof(DetailThird), nameof(DetailIcon),
                         nameof(IsDetailCpu), nameof(IsDetailRam), nameof(IsDetailGpu), nameof(IsDetailDisk), nameof(IsDetailNet), nameof(CanStartNetMonitor) })
                Raise(n);
            CommandManager.InvalidateRequerySuggested(); // so buttons update without waiting for a mouse/keyboard event
        }
    }

    void RaiseNetMonitorState()
    {
        Raise(nameof(CanStartNetMonitor));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool HasDetail => _detail is not null;
    public bool IsDetailCpu => _detail == DetailCpu;
    public bool IsDetailRam => _detail == DetailRam;
    public bool IsDetailGpu => _detail == DetailGpu;
    public bool IsDetailDisk => _detail == DetailDisk;
    public bool IsDetailNet => _detail == DetailNet;
    public bool CanStartNetMonitor => IsDetailNet && !_netMonitor.Running && !_netStarting;
    public string DetailNote { get => _detailNote; private set => Set(ref _detailNote, value); }

    public string DetailTitle => _detail switch
    {
        DetailCpu => T("PROCESSOR USAGE BY PROCESS"),
        DetailRam => T("MEMORY USAGE BY PROCESS"),
        DetailGpu => T("GRAPHICS USAGE BY PROCESS"),
        DetailDisk => T("DISK USAGE BY PROCESS"),
        DetailNet => T("NETWORK USAGE BY PROCESS"),
        _ => "",
    };

    public string DetailIcon => _detail switch { DetailCpu => "", DetailRam => "", DetailGpu => "", DetailDisk => "", _ => "" };
    public string DetailPrimary => _detail switch { DetailCpu => "CPU", DetailRam => "RAM", DetailGpu => "GPU", DetailDisk => T("Read"), _ => T("Download") };
    public string DetailSecondary => _detail switch { DetailCpu => "RAM", DetailRam => T("Of total"), DetailGpu => "VRAM", DetailDisk => T("Write"), _ => T("Upload") };
    public string DetailThird => _detail == DetailNet ? T("Connections") : "";

    void InitDetail()
    {
        ShowDetailCommand = new RelayCommand(p => { if (p is string kind) _ = OpenDetailAsync(kind == _detail ? null : kind); });
        CloseDetailCommand = new RelayCommand(_ => _ = OpenDetailAsync(null));
        StartNetMonitorCommand = new RelayCommand(async _ => await StartNetMonitorAsync(), _ => CanStartNetMonitor);
        KillUsageCommand = new RelayCommand(p => { if (p is UsageRow { CanKill: true } r) KillProcess(r.Name, r.Instances); });
    }

    async Task OpenDetailAsync(string? kind)
    {
        if (kind != DetailNet) _netMonitor.Stop();
        if (kind != DetailGpu)
        {
            _gpuProcesses?.Dispose();
            _gpuProcesses = null;
        }
        DetailRows.Clear();
        Detail = kind;
        if (kind is null) return;
        if (kind == DetailGpu) _gpuProcesses ??= await Task.Run(() => new GpuProcessSampler());
        if (kind == DetailNet && _settings.NetMonitorAllowed && !_netMonitor.Running) _ = StartNetMonitorAsync();
        DetailNote = Note(kind);
        _detailRates.Sample(); // first sample: differences start from the next one
        await RefreshDetailAsync();
    }

    string Note(string kind) => kind switch
    {
        DetailCpu => T("Share of total processor power (same measure as Task Manager). Processes with the same name are combined."),
        DetailRam => T("Private working set: the RAM that belongs only to the process (the Memory column in Task Manager)."),
        DetailGpu => T("The graphics engine the process uses most (3D, video, copy) and the video memory it has allocated."),
        DetailDisk => T("File read and write rate of processes; data read from the cache is counted too."),
        _ => _netMonitor.Running
            ? T("Live traffic per app. Connections: number of established TCP connections.")
            : T("Speed per app needs administrator permission; until then, the number of established connections is shown."),
    };

    async Task StartNetMonitorAsync()
    {
        if (_netMonitor.Running || _netStarting) return;
        _netStarting = true;
        RaiseNetMonitorState();
        DetailNote = T("Starting the network monitor (administrator permission)…");
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
            if (IsDetailNet) DetailNote = Note(DetailNet);
        }
    }

    async Task RefreshDetailAsync()
    {
        if (Demo.On) return;
        if (_detailBusy || _detail is not { } kind) return;
        _detailBusy = true;
        try
        {
            var gpu = _gpuProcesses;
            var net = _netMonitor.Running ? _netMonitor.Rates : null;
            var rows = await Task.Run(() => BuildDetail(kind, gpu, net));
            if (_detail != kind) return;
            DetailRows.Clear();
            foreach (var r in rows) DetailRows.Add(r);
        }
        finally
        {
            _detailBusy = false;
        }
    }

    List<UsageRow> BuildDetail(string kind, GpuProcessSampler? gpu, IReadOnlyDictionary<int, (long Down, long Up)>? net)
    {
        var groups = ProcessSampler.Group(_detailRates.Sample());
        var (_, totalGb, _) = SystemInfo.Memory();
        double totalRam = totalGb * 1073741824.0;
        static string Mb(long bytes) => bytes >= 1073741824 ? (bytes / 1073741824.0).ToString("0.0", L.Culture) + " GB" : (bytes / 1048576.0).ToString("0", L.Culture) + " MB";
        static string Rate(double bytesPerSec) => bytesPerSec >= 1048576 ? (bytesPerSec / 1048576.0).ToString("0.0", L.Culture) + " MB/s"
            : bytesPerSec >= 1024 ? (bytesPerSec / 1024.0).ToString("0", L.Culture) + " KB/s" : bytesPerSec > 0 ? "< 1 KB/s" : "0";
        static string Bits(double bytesPerSec) => bytesPerSec * 8 >= 1e6 ? (bytesPerSec * 8 / 1e6).ToString("0.0", L.Culture) + " Mbps"
            : bytesPerSec * 8 >= 1e3 ? (bytesPerSec * 8 / 1e3).ToString("0", L.Culture) + " kbps" : bytesPerSec > 0 ? "< 1 kbps" : "0";

        switch (kind)
        {
            case DetailCpu:
                return groups.OrderByDescending(g => g.CpuPercent).Take(TopCount)
                    .Select(g => new UsageRow(g.Name, g.Count, Pct(g.CpuPercent, "0.0"), Mb(g.PrivateWorkingSet), "", g.CpuPercent / 100, g.CanKill)).ToList();
            case DetailRam:
                return groups.OrderByDescending(g => g.PrivateWorkingSet).Take(TopCount)
                    .Select(g => new UsageRow(g.Name, g.Count, Mb(g.PrivateWorkingSet), totalRam > 0 ? Pct(g.PrivateWorkingSet / totalRam * 100, "0.0") : "", "",
                        totalRam > 0 ? g.PrivateWorkingSet / totalRam : 0, g.CanKill)).ToList();
            case DetailDisk:
            {
                var active = groups.Where(g => g.ReadPerSec + g.WritePerSec > 0).OrderByDescending(g => g.ReadPerSec + g.WritePerSec).Take(TopCount).ToList();
                double max = active.Count > 0 ? active.Max(g => g.ReadPerSec + g.WritePerSec) : 1;
                return active.Select(g => new UsageRow(g.Name, g.Count, Rate(g.ReadPerSec), Rate(g.WritePerSec), "", (g.ReadPerSec + g.WritePerSec) / max, g.CanKill)).ToList();
            }
            case DetailGpu:
            {
                var perPid = gpu?.Sample() ?? [];
                var rows = groups.Select(g =>
                    {
                        double percent = g.Ids.Sum(id => perPid.TryGetValue(id, out var v) ? v.Percent : 0);
                        long vram = g.Ids.Sum(id => perPid.TryGetValue(id, out var v) ? v.Vram : 0);
                        return (g, percent: Math.Min(100, percent), vram);
                    })
                    .Where(x => x.percent > 0.05 || x.vram > 0)
                    .OrderByDescending(x => x.percent).ThenByDescending(x => x.vram).Take(TopCount);
                return rows.Select(x => new UsageRow(x.g.Name, x.g.Count, Pct(x.percent, "0.0"), x.vram > 0 ? Mb(x.vram) : "–", "", x.percent / 100, x.g.CanKill)).ToList();
            }
            default:
            {
                var connections = NetConnections.CountByProcess();
                var rows = groups.Select(g =>
                    {
                        long down = 0, up = 0;
                        if (net is not null)
                            foreach (var id in g.Ids)
                                if (net.TryGetValue(id, out var v)) { down += v.Down; up += v.Up; }
                        int links = g.Ids.Sum(id => connections.GetValueOrDefault(id));
                        return (g, down, up, links);
                    })
                    .Where(x => x.down + x.up > 0 || x.links > 0)
                    .OrderByDescending(x => x.down + x.up).ThenByDescending(x => x.links).Take(TopCount).ToList();
                double max = rows.Count > 0 ? Math.Max(1, rows.Max(x => x.down + x.up)) : 1;
                return rows.Select(x => new UsageRow(x.g.Name, x.g.Count,
                    net is null ? "–" : Bits(x.down), net is null ? "–" : Bits(x.up), x.links.ToString(L.Culture),
                    net is null ? 0 : (x.down + x.up) / max, x.g.CanKill)).ToList();
            }
        }
    }
}
