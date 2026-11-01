using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public enum ProcessColumn { Name, Cpu, Ram, Disk, Gpu, Net }

// One app on the Processes tab (processes with the same name together), with the values the columns sort by.
public sealed record ProcessManagerRow(string Name, int Instances, double CpuPercent, long Ram, double DiskPerSec, double GpuPercent, int Connections,
    string? Path, bool CanKill, bool IsGame)
{
    public string Count => Instances > 1 ? $"x{Instances}" : "";
    public string Cpu => Pct(CpuPercent, "0.0");
    public string RamText => Ram >= 1073741824 ? (Ram / 1073741824.0).ToString("0.0", L.Culture) + " GB" : (Ram / 1048576.0).ToString("0", L.Culture) + " MB";
    public string Disk => DiskPerSec >= 1048576 ? (DiskPerSec / 1048576.0).ToString("0.0", L.Culture) + " MB/s" : DiskPerSec >= 1024 ? (DiskPerSec / 1024.0).ToString("0", L.Culture) + " KB/s" : DiskPerSec > 0 ? "< 1 KB/s" : "0";
    public string Gpu => GpuPercent > 0.05 ? Pct(GpuPercent, "0.0") : "–";
    public string Net => Connections > 0 ? Connections.ToString(L.Culture) : "–";
    public string Detail => Path ?? "";
    public bool HasPath => Path is not null;

    // Heat of the busiest value, like Task Manager's shaded cells.
    public Brush CpuBrush => CpuPercent >= 25 ? Palette.Bad : CpuPercent >= 8 ? Palette.Warn : Palette.Muted;
}

// Processes tab: a full process manager (every app, sortable columns, search, file location, end task), while the Live tab
// stays an overview whose cards show only the top users of one resource.
public sealed partial class MainViewModel
{
    readonly ProcessRates _managerRates = new();
    GpuProcessSampler? _managerGpu;
    ProcessColumn _processSort = ProcessColumn.Cpu;
    bool _processDescending = true, _managerBusy;
    string _processSearch = "", _processSummary = "";
    List<ProcessManagerRow> _allProcesses = [];

    public ObservableCollection<ProcessManagerRow> ProcessList { get; } = [];

    public ICommand SortProcessesCommand { get; private set; } = null!;
    public ICommand EndTaskCommand { get; private set; } = null!;
    public ICommand OpenProcessLocationCommand { get; private set; } = null!;
    public ICommand ShowAllProcessesCommand { get; private set; } = null!;

    // The row menu (right click or ⋯): priority, game list, kept at full speed while a game runs.
    public ICommand AddProcessAsGameCommand { get; private set; } = null!;
    public ICommand KeepProcessFastCommand { get; private set; } = null!;
    public ICommand PriorityHighCommand { get; private set; } = null!;
    public ICommand PriorityAboveNormalCommand { get; private set; } = null!;
    public ICommand PriorityNormalCommand { get; private set; } = null!;
    public ICommand PriorityBelowNormalCommand { get; private set; } = null!;
    public ICommand PriorityLowCommand { get; private set; } = null!;

    public string ProcessSummary { get => _processSummary; private set => Set(ref _processSummary, value); }

    public string ProcessSearch
    {
        get => _processSearch;
        set
        {
            if (Set(ref _processSearch, value)) ShowProcesses();
        }
    }

    public string ProcessNameHeader => ProcessHeader(ProcessColumn.Name, T("Name"));
    public string ProcessCpuHeader => ProcessHeader(ProcessColumn.Cpu, "CPU");
    public string ProcessRamHeader => ProcessHeader(ProcessColumn.Ram, "RAM");
    public string ProcessDiskHeader => ProcessHeader(ProcessColumn.Disk, T("Disk"));
    public string ProcessGpuHeader => ProcessHeader(ProcessColumn.Gpu, "GPU");
    public string ProcessNetHeader => ProcessHeader(ProcessColumn.Net, T("Connections"));

    string ProcessHeader(ProcessColumn column, string text) => column == _processSort ? text + (_processDescending ? " ▼" : " ▲") : text;

    void InitProcesses()
    {
        SortProcessesCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<ProcessColumn>(s, out var c)) SortProcesses(c, toggle: true); });
        EndTaskCommand = new RelayCommand(p => { if (p is ProcessManagerRow { CanKill: true } r) KillProcess(r.Name, r.Instances); });
        OpenProcessLocationCommand = new RelayCommand(p => { if (p is ProcessManagerRow { Path: { } path }) Shell.ShowInFolder(path); });
        AddProcessAsGameCommand = new RelayCommand(p =>
        {
            if (p is not ProcessManagerRow r) return;
            AddEntry(r.Name, game: true, r.Path);
            Message = F("{0} was added to the game list.", r.Name);
            _ = RefreshProcessManagerAsync();
        }, p => p is ProcessManagerRow { IsGame: false });
        KeepProcessFastCommand = new RelayCommand(p =>
        {
            if (p is not ProcessManagerRow r) return;
            AddEntry(r.Name, game: false, null);
            Message = F("{0} is not limited while a game runs.", r.Name);
        }, p => p is ProcessManagerRow { IsGame: false });
        RelayCommand Priority(ProcessPriorityClass level) =>
            new(p => { if (p is ProcessManagerRow r) SetProcessPriority(r, level); }, p => p is ProcessManagerRow { CanKill: true });
        PriorityHighCommand = Priority(ProcessPriorityClass.High);
        PriorityAboveNormalCommand = Priority(ProcessPriorityClass.AboveNormal);
        PriorityNormalCommand = Priority(ProcessPriorityClass.Normal);
        PriorityBelowNormalCommand = Priority(ProcessPriorityClass.BelowNormal);
        PriorityLowCommand = Priority(ProcessPriorityClass.Idle);
        // From a Live card: the full list, sorted by that card's resource.
        ShowAllProcessesCommand = new RelayCommand(_ =>
        {
            SortProcesses(_detail switch { DetailRam => ProcessColumn.Ram, DetailGpu => ProcessColumn.Gpu, DetailDisk => ProcessColumn.Disk, DetailNet => ProcessColumn.Net, _ => ProcessColumn.Cpu }, toggle: false);
            SelectedTab = TabProcesses;
        });
    }

    void SortProcesses(ProcessColumn column, bool toggle)
    {
        _processDescending = toggle && column == _processSort ? !_processDescending : column != ProcessColumn.Name;
        _processSort = column;
        foreach (var n in new[] { nameof(ProcessNameHeader), nameof(ProcessCpuHeader), nameof(ProcessRamHeader), nameof(ProcessDiskHeader), nameof(ProcessGpuHeader), nameof(ProcessNetHeader) })
            Raise(n);
        ShowProcesses();
    }

    // Every 2 s while the tab is open.
    async Task RefreshProcessManagerAsync()
    {
        if (Demo.On)
        {
            DemoProcesses();
            return;
        }
        if (_managerBusy) return;
        _managerBusy = true;
        try
        {
            _managerGpu ??= await Task.Run(() => new GpuProcessSampler());
            var gpu = _managerGpu;
            var games = _settings.Games.Select(System.IO.Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_watcher.ActiveGame is { } active) games.Add(active);
            _allProcesses = await Task.Run(() =>
            {
                var groups = ProcessSampler.Group(_managerRates.Sample());
                var perPid = gpu.Sample();
                var connections = NetConnections.CountByProcess();
                return groups.Select(g => new ProcessManagerRow(g.Name, g.Count, g.CpuPercent, g.PrivateWorkingSet, g.ReadPerSec + g.WritePerSec,
                        Math.Min(100, g.Ids.Sum(id => perPid.TryGetValue(id, out var v) ? v.Percent : 0)),
                        g.Ids.Sum(id => connections.GetValueOrDefault(id)),
                        g.Ids.Select(AppProcesses.ImagePath).FirstOrDefault(p => p is not null), g.CanKill, games.Contains(g.Name)))
                    .ToList();
            });
            ShowProcesses();
        }
        finally
        {
            _managerBusy = false;
        }
    }

    // Until the app restarts; game mode's own priority changes are separate.
    void SetProcessPriority(ProcessManagerRow row, ProcessPriorityClass level)
    {
        if (Demo.On) return;
        int failed = 0;
        foreach (var p in Process.GetProcessesByName(row.Name))
            using (p)
            {
                try { p.PriorityClass = level; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { failed++; }
            }
        Message = failed == 0
            ? F("{0}: priority changed until it restarts.", row.Name)
            : F("{0}: the priority could not be changed for {1} process.|{0}: the priority could not be changed for {1} processes.", row.Name, failed);
    }

    void ShowProcesses()
    {
        var words = TextSearch.Words(_processSearch);
        IEnumerable<ProcessManagerRow> rows = _allProcesses.Where(r => TextSearch.Matches(words, r.Name, r.Path));
        Func<ProcessManagerRow, double> key = _processSort switch
        {
            ProcessColumn.Ram => r => r.Ram,
            ProcessColumn.Disk => r => r.DiskPerSec,
            ProcessColumn.Gpu => r => r.GpuPercent,
            ProcessColumn.Net => r => r.Connections,
            _ => r => r.CpuPercent,
        };
        rows = _processSort == ProcessColumn.Name
            ? (_processDescending ? rows.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase) : rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            : (_processDescending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        var list = rows.ToList();

        // Rows are replaced in place, so the list does not jump while it updates.
        for (int i = 0; i < list.Count; i++)
        {
            if (i < ProcessList.Count) { if (ProcessList[i] != list[i]) ProcessList[i] = list[i]; }
            else ProcessList.Add(list[i]);
        }
        while (ProcessList.Count > list.Count) ProcessList.RemoveAt(ProcessList.Count - 1);

        int processes = _allProcesses.Sum(r => r.Instances);
        ProcessSummary = F("{0} processes in {1} apps · CPU {2} · RAM {3}", processes, _allProcesses.Count,
            Pct(Math.Min(100, _allProcesses.Sum(r => r.CpuPercent))), (_allProcesses.Sum(r => r.Ram) / 1073741824.0).ToString("0.0", L.Culture) + " GB");
    }
}
