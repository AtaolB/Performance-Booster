using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Updates tab: apps with winget, drivers with Windows Update.
// Installing only starts after the user confirms; checks run at startup and every 6 hours (in the tray too).
// While packages update, a bar under the tabs shows the progress on every tab; queued and running packages can be cancelled.
public sealed partial class MainViewModel
{
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly List<HistoryEntry> _history = UpdateHistory.Load();
    List<WingetPackage> _lastPackages = [];
    HashSet<string> _lastPins = new(StringComparer.OrdinalIgnoreCase);
    // The packages of the running batch (a package cancelled while queued leaves it), the one being installed and its cancellation.
    List<PackageRow> _queue = [];
    PackageRow? _current;
    CancellationTokenSource? _currentCts;
    bool _checkingUpdates, _updating, _stopRequested, _checkingDrivers, _wingetMissing;
    string _updateStatus = T("Not checked yet."), _driverStatus = T("Not checked yet."), _activityText = "", _activityDetail = "";
    int _updatableCount, _selectedPackageCount, _selectedDriverCount;
    double _activityPercent;

    public ObservableCollection<PackageRow> Packages { get; } = [];
    public ObservableCollection<IgnoredRow> IgnoredPackages { get; } = [];
    public ObservableCollection<DriverRow> Drivers { get; } = [];
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];

    // When a new update is found: the taskbar button flashes if the window is visible, a notification appears if it is in the tray.
    public event Action<int>? UpdatesFound;

    public ICommand CheckUpdatesCommand { get; private set; } = null!;
    public ICommand UpdateAllCommand { get; private set; } = null!;
    public ICommand UpdateSelectedCommand { get; private set; } = null!;
    public ICommand UpdateOneCommand { get; private set; } = null!;
    public ICommand StopUpdatesCommand { get; private set; } = null!;
    public ICommand CancelPackageCommand { get; private set; } = null!;
    public ICommand IgnoreCommand { get; private set; } = null!;
    public ICommand SkipVersionCommand { get; private set; } = null!;
    public ICommand UnignoreCommand { get; private set; } = null!;
    public ICommand DetailsCommand { get; private set; } = null!;
    public ICommand CheckDriversCommand { get; private set; } = null!;
    public ICommand InstallDriversCommand { get; private set; } = null!;
    public ICommand OpenWingetStoreCommand { get; private set; } = null!;
    public ICommand GoToTabCommand { get; private set; } = null!;
    public ICommand ClearHistoryCommand { get; private set; } = null!;

    public bool CheckingUpdates { get => _checkingUpdates; private set { if (Set(ref _checkingUpdates, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Updating { get => _updating; private set { if (Set(ref _updating, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool CheckingDrivers { get => _checkingDrivers; private set { if (Set(ref _checkingDrivers, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool WingetMissing { get => _wingetMissing; private set => Set(ref _wingetMissing, value); }
    // Before the first package starts (open apps are checked and closed), the progress bar shows this status.
    public string UpdateStatus { get => _updateStatus; private set { if (Set(ref _updateStatus, value) && Updating && _current is null) ActivityText = value; } }
    public string DriverStatus { get => _driverStatus; private set => Set(ref _driverStatus, value); }

    // The update progress bar shown on every tab.
    public string ActivityText { get => _activityText; private set => Set(ref _activityText, value); }
    public string ActivityDetail { get => _activityDetail; private set => Set(ref _activityDetail, value); }
    public double ActivityPercent { get => _activityPercent; private set { if (Set(ref _activityPercent, value)) Raise(nameof(ActivityPercentText)); } }
    public string ActivityPercentText => Pct(ActivityPercent);

    public int UpdatableCount
    {
        get => _updatableCount;
        private set { if (Set(ref _updatableCount, value)) RaiseUpdateTitles(); }
    }

    public int SelectedPackageCount
    {
        get => _selectedPackageCount;
        private set { if (Set(ref _selectedPackageCount, value)) { Raise(nameof(UpdateSelectedText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int SelectedDriverCount
    {
        get => _selectedDriverCount;
        private set { if (Set(ref _selectedDriverCount, value)) { Raise(nameof(InstallDriversText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int PendingTotal => UpdatableCount + Drivers.Count;
    public string UpdatesTabTitle => PendingTotal > 0 ? F("Updates ({0})", PendingTotal) : T("Updates");
    public string WindowTitle => PendingTotal > 0 ? F("{0} · {1} update|{0} · {1} updates", AppPaths.Name, PendingTotal) : AppPaths.Name;
    public string UpdateAllText => UpdatableCount > 0 ? F("Update all ({0})", UpdatableCount) : T("No apps to update");
    public string UpdateSelectedText => F("Update selected ({0})", SelectedPackageCount);
    public string InstallDriversText => F("Install selected drivers ({0})", SelectedDriverCount);
    public bool HasIgnored => IgnoredPackages.Count > 0;
    public bool HasDrivers => Drivers.Count > 0;
    public bool HasHistory => HistoryRows.Count > 0;
    public bool HasPackages => Packages.Count > 0;

    public bool IncludeUnknown
    {
        get => _settings.IncludeUnknown;
        set
        {
            if (_settings.IncludeUnknown == value) return;
            _settings.IncludeUnknown = value;
            _settings.Save();
            Raise(nameof(IncludeUnknown));
            _ = CheckUpdatesAsync();
        }
    }

    public bool IncludeStore
    {
        get => _settings.IncludeStore;
        set
        {
            if (_settings.IncludeStore == value) return;
            _settings.IncludeStore = value;
            _settings.Save();
            Raise(nameof(IncludeStore));
            RebuildPackages();
        }
    }

    public bool SilentInstall
    {
        get => _settings.SilentInstall;
        set
        {
            if (_settings.SilentInstall == value) return;
            _settings.SilentInstall = value;
            _settings.Save();
            Raise(nameof(SilentInstall));
        }
    }

    void RaiseUpdateTitles()
    {
        foreach (var n in new[] { nameof(UpdatesTabTitle), nameof(WindowTitle), nameof(UpdateAllText), nameof(PendingTotal), nameof(HasPackages) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    void InitUpdates()
    {
        bool Idle() => !CheckingUpdates && !Updating;
        // Apps, drivers (Windows Update) and the app itself; before, the button checked only the apps, so Windows Update
        // was searched only at start and every 6 hours.
        CheckUpdatesCommand = new RelayCommand(async _ => await CheckAllUpdatesAsync(), _ => Idle());
        UpdateAllCommand = new RelayCommand(async _ => await UpdatePackagesAsync(Packages.Where(p => p.InBulk).ToList()),
            _ => Idle() && Packages.Any(p => p.InBulk));
        UpdateSelectedCommand = new RelayCommand(async _ => await UpdatePackagesAsync(Packages.Where(p => p.IsSelected && p.CanUpdate).ToList()),
            _ => Idle() && SelectedPackageCount > 0);
        UpdateOneCommand = new RelayCommand(async p => { if (p is PackageRow { CanUpdate: true } row) await UpdatePackagesAsync([row]); }, _ => Idle());
        StopUpdatesCommand = new RelayCommand(_ => StopUpdates(), _ => Updating && !_stopRequested);
        CancelPackageCommand = new RelayCommand(p => { if (p is PackageRow row) CancelPackage(row); }, _ => Updating);
        IgnoreCommand = new RelayCommand(async p => { if (p is PackageRow row) await PinAsync(row.Package, add: true); }, _ => Idle());
        SkipVersionCommand = new RelayCommand(p => { if (p is PackageRow row) SkipVersion(row.Package); }, _ => Idle());
        UnignoreCommand = new RelayCommand(async p => { if (p is IgnoredRow row) await UnignoreAsync(row); }, _ => Idle());
        DetailsCommand = new RelayCommand(async p => { if (p is PackageRow row) await ShowDetailsAsync(row.Package); }, _ => !CheckingUpdates);
        CheckDriversCommand = new RelayCommand(async _ => await CheckDriversAsync(), _ => !CheckingDrivers && !Busy);
        InstallDriversCommand = new RelayCommand(async _ => await InstallDriversAsync(), _ => !CheckingDrivers && !Busy && SelectedDriverCount > 0);
        OpenWingetStoreCommand = new RelayCommand(_ => Shell.Open("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1"));
        GoToTabCommand = new RelayCommand(p =>
        {
            if (p is not int tab) return;
            if (tab == TabTweaks) OnlyDifferent = true;
            SelectedTab = tab;
        });

        ClearHistoryCommand = new RelayCommand(_ => ClearHistory(), _ => HasHistory && !Updating);
        RefreshHistory();
        _progressTimer.Tick += (_, _) =>
        {
            _current?.Tick(DateTime.UtcNow);
            RaiseActivity();
        };
        _updateTimer.Tick += async (_, _) => await CheckAllUpdatesAsync();
        _updateTimer.Start();
        _ = FirstUpdateCheckAsync();
    }

    async Task FirstUpdateCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4)); // so it does not compete with the other startup work
        await CheckAllUpdatesAsync();
    }

    // Windows Update takes a minute or two, so it runs alongside the app check instead of after it.
    public async Task CheckAllUpdatesAsync()
    {
        await CheckSelfUpdateAsync();
        await Task.WhenAll(CheckUpdatesAsync(), CheckDriversAsync());
    }

    // --- Apps (winget) ---

    async Task CheckUpdatesAsync()
    {
        if (Demo.On)
        {
            _lastPackages = Demo.Packages();
            _lastPins = [];
            RebuildPackages();
            UpdateStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture)) + " · " + F("{0} update|{0} updates", UpdatableCount);
            return;
        }
        if (CheckingUpdates || Updating) return;
        if (!Winget.Available)
        {
            WingetMissing = true;
            UpdateStatus = T("winget not found. Install \"App Installer\" from the Microsoft Store.");
            return;
        }
        CheckingUpdates = true;
        UpdateStatus = T("Looking for updates…");
        try
        {
            int before = UpdatableCount;
            _lastPackages = await Winget.ListUpgradesAsync(IncludeUnknown, CancellationToken.None);
            _lastPins = await Winget.PinnedIdsAsync(CancellationToken.None);
            RebuildPackages();
            UpdateStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture))
                           + " · " + (UpdatableCount == 0 ? T("all apps are up to date") : F("{0} update|{0} updates", UpdatableCount))
                           + (IgnoredPackages.Count > 0 ? " · " + F("{0} ignored", IgnoredPackages.Count) : "");
            if (UpdatableCount > before) UpdatesFound?.Invoke(UpdatableCount);
        }
        catch (Exception ex)
        {
            UpdateStatus = F("Check failed: {0}", ex.Message);
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    void RebuildPackages()
    {
        var previous = Packages.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var skipped = _settings.SkippedVersions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        Packages.Clear();
        IgnoredPackages.Clear();
        foreach (var p in _lastPackages.Where(p => IncludeStore || !p.IsStore)
                     .OrderBy(p => p.IsWinget).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (_lastPins.Contains(p.Id)) { IgnoredPackages.Add(new IgnoredRow(p, pinned: true)); continue; }
            if (skipped.Contains($"{p.Id}|{p.Available}")) { IgnoredPackages.Add(new IgnoredRow(p, pinned: false)); continue; }
            var row = new PackageRow(p);
            if (previous.TryGetValue(p.Id, out var old))
            {
                row.IsSelected = old.IsSelected;
                row.CopyStatusFrom(old);
            }
            if (HoldReason(p, _history, boot) is { } why) row.Hold(why);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PackageRow.IsSelected) or nameof(PackageRow.CanUpdate))
                    SelectedPackageCount = Packages.Count(x => x.IsSelected && x.CanUpdate);
            };
            Packages.Add(row);
        }
        UpdatableCount = Packages.Count(p => p.InBulk);
        SelectedPackageCount = Packages.Count(x => x.IsSelected && x.CanUpdate);
        Raise(nameof(HasIgnored));
        Raise(nameof(HasPackages));
    }

    // Why a package is left out of bulk updates and the counter, based on the package's last attempt:
    // installed successfully since this boot but the old version still shows (completes after a restart)
    // or winget cannot update it (apps that update themselves). Updating it individually is still possible.
    internal static string? HoldReason(WingetPackage p, IEnumerable<HistoryEntry> history, DateTime boot)
    {
        // Records from the old version have no ID; they match by name.
        var last = history.Where(h => h.Id is not null ? h.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase) : h.Name == p.Name).MaxBy(h => h.At);
        if (last is null) return null;
        if (last.Ok && last.At > boot && last.Installed == p.Installed && last.Available == p.Available)
            return T("Updated; finishes after the computer restarts");
        if (!last.Ok && last.Message.Contains($"0x{Winget.InstallTechnologyMismatch:X8}", StringComparison.OrdinalIgnoreCase))
            return T("Updates with its own updater; winget cannot update it");
        return null;
    }

    async Task UpdatePackagesAsync(List<PackageRow> rows)
    {
        if (rows.Count == 0 || Updating || CheckingUpdates) return;
        Updating = true;
        _stopRequested = false;
        RaiseActivity();
        try
        {
            rows = await CloseRunningAppsAsync(rows);
            if (rows.Count == 0) return;

            _queue = rows.OrderBy(r => r.Package.IsWinget).ToList(); // winget itself goes last
            foreach (var r in _queue) r.SetWaiting();
            _progressTimer.Start();
            int ok = 0, failed = 0, cancelled = 0;
            bool reboot = false;
            foreach (var row in _queue.ToList())
            {
                if (row.State != PackageState.Waiting) continue; // cancelled while queued
                if (_stopRequested)
                {
                    row.Reset();
                    continue;
                }
                using var cts = new CancellationTokenSource();
                _current = row;
                _currentCts = cts;
                RaiseActivity();
                UpgradeResult result;
                try
                {
                    result = await Winget.UpgradeAsync(row.Package, IncludeUnknown, SilentInstall, new Progress<UpgradeProgress>(p => { row.Report(p); RaiseActivity(); }), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    row.Skip(T("Cancelled"));
                    cancelled++;
                    continue;
                }
                catch (Exception ex)
                {
                    result = new UpgradeResult(false, false, false, -1, ex.Message);
                }
                finally
                {
                    _current = null;
                    _currentCts = null;
                }
                row.Finish(result);
                if (result.Success) ok++;
                else if (!result.NotApplicable) failed++;
                reboot |= result.RebootRequired;
                var p = row.Package;
                _history.Add(new HistoryEntry(DateTime.Now, row.Name, row.VersionText, result.Success || result.NotApplicable, result.Message, p.Id, p.Installed, p.Available));
            }
            UpdateHistory.Save(_history);
            RefreshHistory();
            Message = (_stopRequested ? T("Updates stopped.") + " " : "")
                      + F("{0} app updated|{0} apps updated", ok) + (failed > 0 ? ", " + F("{0} failed", failed) : "") + (cancelled > 0 ? ", " + F("{0} cancelled", cancelled) : "")
                      + (reboot ? ". " + T("Some updates need a restart.") : ".");
        }
        finally
        {
            _progressTimer.Stop();
            _queue = [];
            Updating = false;
            RaiseActivity();
        }
        await CheckUpdatesAsync();
    }

    // Overall progress of the batch: finished packages count whole, the running one by its stage.
    void RaiseActivity()
    {
        if (!Updating)
        {
            ActivityText = ActivityDetail = "";
            ActivityPercent = 0;
            return;
        }
        int done = _queue.Count(r => r.IsFinished);
        if (_current is { } row)
        {
            ActivityText = F("Updating {0} ({1}/{2})…", row.Name, Math.Min(done + 1, _queue.Count), _queue.Count);
            ActivityDetail = row.StatusText + (row.ShowPercent ? " · " + row.PercentText : "")
                             + (_stopRequested && !row.IsStopping ? " · " + T("stops after this package") : "");
            if (!_stopRequested) UpdateStatus = ActivityText;
        }
        else
        {
            ActivityText = UpdateStatus;
            ActivityDetail = _queue.Count > 0 ? F("{0}/{1} packages done", done, _queue.Count) : "";
        }
        ActivityPercent = _queue.Count == 0 ? 0 : _queue.Sum(r => r.Share) / _queue.Count * 100;
    }

    // Queued: leaves the batch. Running: winget is closed; during the install it is asked first, since the app may be left half installed.
    void CancelPackage(PackageRow row)
    {
        if (row.State == PackageState.Waiting)
        {
            row.Reset();
            _queue.Remove(row);
            RaiseActivity();
            return;
        }
        var cts = _currentCts;
        if (row != _current || cts is null || row.IsStopping) return;
        if (row.IsInstalling && !Dialogs.Confirm(T("Install in progress"), F("{0} is being installed right now. Stopping halfway can leave the app broken, and an installer running with administrator rights may not stop.", row.Name),
                T("Stop anyway"), danger: true))
            return;
        if (row != _current || cts != _currentCts) return; // finished while the question was open
        row.Stopping();
        cts.Cancel();
        RaiseActivity();
    }

    // Stops the whole batch at once: the queued packages leave, the running one is closed (or, if the user prefers, finishes its install first).
    void StopUpdates()
    {
        if (!Updating || _stopRequested) return;
        var row = _current;
        var cts = _currentCts;
        bool now = true;
        if (row is { IsInstalling: true })
        {
            int choice = Dialogs.Choice(T("Install in progress"),
                F("{0} is being installed right now. Stopping halfway can leave the app broken, and an installer running with administrator rights may not stop.", row.Name),
                [], T("Stop after the install"), T("Stop now"), T("Cancel"));
            if (choice is 2 or -1) return;
            now = choice == 1;
        }
        if (!Updating || _stopRequested) return; // the batch ended while the question was open
        _stopRequested = true;
        foreach (var queued in _queue.Where(r => r.State == PackageState.Waiting).ToList())
        {
            queued.Reset();
            _queue.Remove(queued);
        }
        if (now && row is not null && row == _current && cts == _currentCts)
        {
            row.Stopping();
            cts?.Cancel();
        }
        UpdateStatus = now ? T("Stopping updates…") : T("Will stop after the package being installed…");
        CommandManager.InvalidateRequerySuggested();
        RaiseActivity();
    }

    // If an app is open, permission to close it is asked; if allowed it is closed gracefully first, and the user is asked again for those that do not close.
    async Task<List<PackageRow>> CloseRunningAppsAsync(List<PackageRow> rows)
    {
        UpdateStatus = T("Checking for open apps…");
        var running = await Task.Run(() => AppProcesses.FindRunning(rows.Select(r => r.Package)));
        var open = rows.Where(r => running.ContainsKey(r.Id)).ToList();
        if (open.Count == 0) return rows;

        static string Describe(List<RunningProcess> list) =>
            string.Join(", ", list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
        int choice = Dialogs.Choice(T("Open apps"),
            T("These apps are open right now. They have to be closed to update; save your work first."),
            open.Select(r => $"{r.Name} ({Describe(running[r.Id])})"),
            T("Close and update"), T("Skip these"), T("Cancel"));
        if (choice is 2 or -1)
        {
            UpdateStatus = T("Update cancelled.");
            return [];
        }
        if (choice == 1)
        {
            foreach (var r in open) r.Skip(T("Skipped: the app was open"));
            return rows.Except(open).ToList();
        }

        UpdateStatus = T("Closing apps…");
        var left = await AppProcesses.CloseGracefullyAsync(open.SelectMany(r => running[r.Id]).ToList(), TimeSpan.FromSeconds(15));
        if (left.Count == 0) return rows;

        var leftIds = left.Select(p => p.Id).ToHashSet();
        var stuck = open.Where(r => running[r.Id].Any(p => leftIds.Contains(p.Id))).ToList();
        int force = Dialogs.Choice(T("Apps that did not close"),
            T("These apps did not close within 15 seconds (a save prompt may be open, or they minimize to the tray). Forcing them closed loses unsaved data."),
            stuck.Select(r => r.Name), T("Force close"), T("Skip these"));
        if (force == 0)
        {
            AppProcesses.Kill(left);
            await Task.Delay(1000);
            return rows;
        }
        foreach (var r in stuck) r.Skip(T("Skipped: the app did not close"));
        return rows.Except(stuck).ToList();
    }

    async Task PinAsync(WingetPackage package, bool add)
    {
        var (ok, message) = await Winget.PinAsync(package, add, CancellationToken.None);
        if (!ok)
        {
            Message = F("Operation failed: {0}", message);
            return;
        }
        if (add) _lastPins.Add(package.Id);
        else _lastPins.Remove(package.Id);
        RebuildPackages();
        Message = add ? F("{0} is now ignored; winget upgrade --all will skip it too.", package.Name) : F("{0} is no longer ignored.", package.Name);
    }

    void SkipVersion(WingetPackage package)
    {
        var key = $"{package.Id}|{package.Available}";
        if (!_settings.SkippedVersions.Contains(key, StringComparer.OrdinalIgnoreCase)) _settings.SkippedVersions.Add(key);
        _settings.Save();
        RebuildPackages();
        Message = F("{0} {1} skipped; it will show up again when a newer version is released.", package.Name, package.Available);
    }

    async Task UnignoreAsync(IgnoredRow row)
    {
        if (row.Pinned)
        {
            await PinAsync(row.Package, add: false);
            return;
        }
        _settings.SkippedVersions.RemoveAll(s => s.Equals($"{row.Id}|{row.Package.Available}", StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildPackages();
    }

    async Task ShowDetailsAsync(WingetPackage package)
    {
        try
        {
            var text = await Winget.ShowAsync(package, CancellationToken.None);
            Dialogs.Text(package.Name, text);
        }
        catch (Exception ex)
        {
            Message = F("Could not get details: {0}", ex.Message);
        }
    }

    void RefreshHistory()
    {
        HistoryRows.Clear();
        foreach (var e in _history.Where(h => !h.Hidden).OrderByDescending(h => h.At).Take(30)) HistoryRows.Add(new HistoryRow(e));
        Raise(nameof(HasHistory));
        CommandManager.InvalidateRequerySuggested();
    }

    // The history is cleared from the list; the entries are kept hidden, so packages winget cannot update
    // (apps that update themselves) and those waiting for a restart still stay out of bulk updates.
    void ClearHistory()
    {
        if (!Dialogs.Confirm(T("Clear history"), T("Clear the update and uninstall history from the list?"), T("Clear history"))) return;
        for (int i = 0; i < _history.Count; i++) _history[i] = _history[i] with { Hidden = true };
        UpdateHistory.Save(_history);
        RefreshHistory();
        Message = T("History cleared.");
    }

    // --- Drivers (Windows Update) ---

    async Task CheckDriversAsync()
    {
        if (Demo.On)
        {
            DriverStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture)) + " · " + T("no pending driver updates");
            return;
        }
        if (CheckingDrivers) return;
        CheckingDrivers = true;
        DriverStatus = T("Searching Windows Update for drivers (may take 1-2 minutes)…");
        try
        {
            int before = Drivers.Count;
            var found = await Task.Run(DriverUpdates.Search);
            var selected = Drivers.Where(d => d.IsSelected).Select(d => d.Id).ToHashSet();
            bool first = Drivers.Count == 0;
            Drivers.Clear();
            foreach (var d in found)
            {
                var row = new DriverRow(d) { IsSelected = first ? !d.IsDisplay : selected.Contains(d.Id) };
                row.PropertyChanged += (_, _) => SelectedDriverCount = Drivers.Count(x => x.IsSelected);
                Drivers.Add(row);
            }
            SelectedDriverCount = Drivers.Count(x => x.IsSelected);
            Raise(nameof(HasDrivers));
            RaiseUpdateTitles();
            DriverStatus = F("Last check {0}", DateTime.Now.ToString("t", L.Culture)) + " · "
                           + (found.Count == 0 ? T("no pending driver updates") : F("{0} driver update|{0} driver updates", found.Count));
            if (found.Count > before) UpdatesFound?.Invoke(PendingTotal);
        }
        catch (Exception ex)
        {
            DriverStatus = F("Could not search for drivers: {0}", ex.Message);
        }
        finally
        {
            CheckingDrivers = false;
        }
    }

    async Task InstallDriversAsync()
    {
        var chosen = Drivers.Where(d => d.IsSelected).ToList();
        if (chosen.Count == 0) return;
        if (chosen.Any(d => d.Update.IsDisplay)
            && !Dialogs.Confirm(T("Graphics driver"),
                T("The selection includes a graphics driver. The Windows Update version may be older than the one in the manufacturer's app (NVIDIA App, AMD Adrenalin). Install it anyway?"),
                T("Install anyway"), danger: true))
            return;
        var outcome = await RunElevatedAsync(["--wu-install", string.Join(",", chosen.Select(d => d.Id))], T("Driver updates"));
        if (outcome is null) return;
        _history.Add(new HistoryEntry(DateTime.Now, F("{0} driver|{0} drivers", chosen.Count), "Windows Update", outcome.ExitCode == 0,
            string.Join(" · ", outcome.Log.Take(3))));
        UpdateHistory.Save(_history);
        RefreshHistory();
        await CheckDriversAsync();
    }
}
