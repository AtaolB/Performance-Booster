using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Programs tab: installed desktop programs and Store apps; last use, size, uninstall and leftover cleanup.
public sealed partial class MainViewModel
{
    readonly ObservableCollection<ProgramRow> _programs = [];
    List<InstalledProgram> _inventory = [];
    bool _programsLoaded, _programsLoading, _uninstalling;
    string _programSearch = "", _programsStatus = T("Not loaded yet.");
    string[] _programWords = [];
    int _programFilter, _selectedProgramCount, _brokenCount;
    ProgramColumn _sortColumn = ProgramColumn.Name;
    bool _sortDescending;
    CancellationTokenSource? _sizeCts;

    public ListCollectionView ProgramsView { get; private set; } = null!;
    public IReadOnlyList<string> ProgramFilters { get; } = [T("All"), T("Desktop programs"), T("Store apps"), T("Not used for 6+ months"), T("Broken entries")];

    public ICommand RefreshProgramsCommand { get; private set; } = null!;
    public ICommand UninstallProgramCommand { get; private set; } = null!;
    public ICommand UninstallSelectedCommand { get; private set; } = null!;
    public ICommand OpenProgramFolderCommand { get; private set; } = null!;
    public ICommand UninstallPackageCommand { get; private set; } = null!;
    public ICommand RemoveEntryCommand { get; private set; } = null!;
    public ICommand CleanBrokenCommand { get; private set; } = null!;

    public int BrokenCount
    {
        get => _brokenCount;
        private set { if (Set(ref _brokenCount, value)) { Raise(nameof(HasBroken)); Raise(nameof(CleanBrokenText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool HasBroken => BrokenCount > 0;
    public string CleanBrokenText => F("Clean broken entries ({0})", BrokenCount);

    public string ProgramSearch
    {
        get => _programSearch;
        set
        {
            if (!Set(ref _programSearch, value)) return;
            _programWords = TextSearch.Words(value);
            ProgramsView.Refresh();
        }
    }
    public int ProgramFilter { get => _programFilter; set { if (Set(ref _programFilter, value)) ProgramsView.Refresh(); } }
    public ICommand SortProgramsCommand { get; private set; } = null!;

    // Column headers sort the list; the active one shows the direction. Clicking it again turns the order around.
    public string NameHeader => Header(ProgramColumn.Name, T("Program"));
    public string TypeHeader => Header(ProgramColumn.Type, T("Type"));
    public string SizeHeader => Header(ProgramColumn.Size, T("Size"));
    public string InstalledHeader => Header(ProgramColumn.Installed, T("Installed"));
    public string LastUsedHeader => Header(ProgramColumn.LastUsed, T("Last used"));

    string Header(ProgramColumn column, string text) => column == _sortColumn ? text + (_sortDescending ? " ▼" : " ▲") : text;

    void SortPrograms(ProgramColumn column)
    {
        // The first click on a column uses the order that usually matters: largest, newest, least recently used first.
        _sortDescending = column == _sortColumn ? !_sortDescending : column is ProgramColumn.Size or ProgramColumn.Installed;
        _sortColumn = column;
        ProgramsView.CustomSort = new ProgramOrder(column, _sortDescending);
        foreach (var name in new[] { nameof(NameHeader), nameof(TypeHeader), nameof(SizeHeader), nameof(InstalledHeader), nameof(LastUsedHeader) }) Raise(name);
    }
    public string ProgramsStatus { get => _programsStatus; private set => Set(ref _programsStatus, value); }
    public bool ProgramsLoading { get => _programsLoading; private set { if (Set(ref _programsLoading, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool Uninstalling { get => _uninstalling; private set { if (Set(ref _uninstalling, value)) CommandManager.InvalidateRequerySuggested(); } }

    public int SelectedProgramCount
    {
        get => _selectedProgramCount;
        private set { if (Set(ref _selectedProgramCount, value)) { Raise(nameof(UninstallSelectedText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string UninstallSelectedText => F("Uninstall selected ({0})", SelectedProgramCount);

    void InitPrograms()
    {
        ProgramsView = (ListCollectionView)CollectionViewSource.GetDefaultView(_programs);
        ProgramsView.Filter = o => o is ProgramRow r && Visible(r);
        ProgramsView.CustomSort = new ProgramOrder(ProgramColumn.Name, false);
        SortProgramsCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<ProgramColumn>(s, out var column)) SortPrograms(column); });

        bool Idle() => !Uninstalling && !ProgramsLoading;
        RefreshProgramsCommand = new RelayCommand(async _ => await LoadProgramsAsync(), _ => Idle());
        UninstallProgramCommand = new RelayCommand(async p => { if (p is ProgramRow { CanUninstall: true } r) await UninstallProgramsAsync([r]); }, _ => Idle());
        UninstallSelectedCommand = new RelayCommand(async _ => await UninstallProgramsAsync(_programs.Where(r => r.IsSelected && r.CanUninstall).ToList()),
            _ => Idle() && SelectedProgramCount > 0);
        OpenProgramFolderCommand = new RelayCommand(p => { if (p is ProgramRow { HasFolder: true } r) Shell.Open(r.Program.Dirs[0]); });
        UninstallPackageCommand = new RelayCommand(async p => { if (p is PackageRow r) await UninstallPackageAsync(r); }, _ => Idle() && !Updating && !CheckingUpdates);
        RemoveEntryCommand = new RelayCommand(async p => { if (p is ProgramRow { CanRemoveEntry: true } r) await RemoveEntriesAsync([r]); }, _ => Idle());
        CleanBrokenCommand = new RelayCommand(async _ => await RemoveEntriesAsync(_programs.Where(r => r.CanRemoveEntry).ToList()), _ => Idle() && BrokenCount > 0);
    }

    bool Visible(ProgramRow r)
    {
        bool filter = _programFilter switch
        {
            1 => !r.IsStore,
            2 => r.IsStore,
            3 => r.LastUsed is { } t && DateTime.Now - t > TimeSpan.FromDays(180),
            4 => r.IsBroken,
            _ => true,
        };
        return filter && TextSearch.Matches(_programWords, r.Name, r.Detail);
    }

    public enum ProgramColumn { Name, Type, Size, Installed, LastUsed }

    // Sorting by a column in either direction; unknown sizes and dates go last either way, equal ones by name.
    internal sealed class ProgramOrder(ProgramColumn column, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ProgramRow a || y is not ProgramRow b) return 0;
            int c = column switch
            {
                ProgramColumn.Size => Unknown(a.SizeBytes == 0, b.SizeBytes == 0) ?? Direction(a.SizeBytes.CompareTo(b.SizeBytes)),
                ProgramColumn.Installed => Dates(a.Program.InstallDate, b.Program.InstallDate),
                ProgramColumn.LastUsed => Dates(a.LastUsed, b.LastUsed),
                ProgramColumn.Type => Direction(string.Compare(a.SourceText, b.SourceText, L.Culture, System.Globalization.CompareOptions.IgnoreCase)),
                _ => Direction(Name(a, b)),
            };
            return c != 0 ? c : Name(a, b);
        }

        int Direction(int c) => descending ? -c : c;

        int Dates(DateTime? a, DateTime? b) => Unknown(a is null, b is null) ?? Direction(a!.Value.CompareTo(b!.Value));

        static int? Unknown(bool a, bool b) => (a, b) switch { (true, true) => 0, (true, false) => 1, (false, true) => -1, _ => null };

        static int Name(ProgramRow a, ProgramRow b) => string.Compare(a.Name, b.Name, L.Culture, System.Globalization.CompareOptions.IgnoreCase);
    }

    async Task LoadProgramsAsync()
    {
        if (Demo.On) return;
        if (ProgramsLoading) return;
        ProgramsLoading = true;
        ProgramsStatus = T("Reading programs…");
        _sizeCts?.Cancel();
        try
        {
            _inventory = await Programs.ListAsync(includeStore: true, CancellationToken.None);
            var (userAssist, seen, running) = await Task.Run(() =>
                (UsageHistory.ReadUserAssist(), UsageHistory.Seen(), (IReadOnlyCollection<string>)AppProcesses.Snapshot().Select(p => p.Path).ToList()));
            var usage = await Task.Run(() => _inventory.ToDictionary(p => p.Key, p => UsageHistory.LastUsed(p, userAssist, seen, running)));

            var selected = _programs.Where(r => r.IsSelected).Select(r => r.Program.Key).ToHashSet();
            _programs.Clear();
            foreach (var p in _inventory)
            {
                var row = new ProgramRow(p) { LastUsed = usage[p.Key], IsSelected = selected.Contains(p.Key) };
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgramRow.IsSelected)) SelectedProgramCount = _programs.Count(x => x.IsSelected); };
                _programs.Add(row);
            }
            SelectedProgramCount = _programs.Count(x => x.IsSelected);
            _programsLoaded = true;
            UpdateProgramsStatus();
            _ = FillSizesAsync();
        }
        catch (Exception ex)
        {
            ProgramsStatus = F("Could not read programs: {0}", ex.Message);
        }
        finally
        {
            ProgramsLoading = false;
        }
    }

    void UpdateProgramsStatus()
    {
        long total = _programs.Sum(r => r.SizeBytes);
        int store = _programs.Count(r => r.IsStore);
        BrokenCount = _programs.Count(r => r.IsBroken);
        ProgramsStatus = F("{0} program|{0} programs", _programs.Count) + (store > 0 ? " · " + F("{0} Store app|{0} Store apps", store) : "")
                         + (total > 0 ? " · " + F("{0} in total", Maintenance.Size(total)) : "")
                         + (BrokenCount > 0 ? " · " + F("{0} broken entry|{0} broken entries", BrokenCount) : "");
    }

    // Folders of programs with no recorded size are measured in the background.
    async Task FillSizesAsync()
    {
        _sizeCts = new CancellationTokenSource();
        var ct = _sizeCts.Token;
        foreach (var row in _programs.Where(r => r.SizeBytes == 0 && r.HasFolder).ToList())
        {
            if (ct.IsCancellationRequested) return;
            var dir = row.Program.Dirs[0];
            long size = await Task.Run(() => Maintenance.FolderSize(dir, TimeSpan.Zero), ct).ConfigureAwait(true);
            row.SizeBytes = size;
        }
        if (!ct.IsCancellationRequested)
        {
            UpdateProgramsStatus();
            if (_sortColumn == ProgramColumn.Size) ProgramsView.Refresh();
        }
    }

    // --- Uninstalling ---

    async Task UninstallProgramsAsync(List<ProgramRow> rows)
    {
        rows = rows.Where(r => r.CanUninstall).ToList();
        if (rows.Count == 0 || Uninstalling) return;

        string hint = rows.Any(r => r.IsSystemLike) ? "\n\n" + T("Warning: the list contains a component other programs may need.") : "";
        var message = (rows.Count == 1 ? F("{0} will be uninstalled.", rows[0].Name) : F("{0} program will be uninstalled.|{0} programs will be uninstalled.", rows.Count)) + " " + T("This cannot be undone.") + hint;
        var names = rows.Select(r => r.IsSystemLike ? $"{r.Name}  ({T("system component")})" : r.Name);
        if (!Dialogs.Confirm(T("Uninstall programs"), message, names, T("Yes, uninstall"), danger: true)) return;

        Uninstalling = true;
        var removed = new List<InstalledProgram>();
        var stuck = new List<ProgramRow>();
        try
        {
            rows = await CloseProgramsAsync(rows);
            foreach (var row in rows)
            {
                var p = row.Program;
                row.SetBusy(T("Uninstalling…"));
                Message = F("Uninstalling {0}…", row.Name);
                var result = await Uninstaller.RunAsync(p, quiet: p.CanUninstallQuietly, CancellationToken.None);
                if (!result.Removed && !result.Cancelled && p.Source == ProgramSource.Desktop && p.QuietCommand is not null && p.UninstallCommand is not null
                    && Dialogs.Confirm(T("Silent uninstall failed"),
                        F("{0} could not be uninstalled silently: {1}", row.Name, result.Message) + "\n\n" + T("Open the program's own uninstaller?"),
                        T("Open uninstaller")))
                {
                    row.SetBusy(T("The uninstaller is open; complete its steps…"));
                    result = await Uninstaller.RunAsync(p, quiet: false, CancellationToken.None);
                }
                row.Finish(result);
                _history.Add(new HistoryEntry(DateTime.Now, row.Name, T("uninstall"), result.Removed, result.Message));
                if (result.Removed) removed.Add(p);
                else if (!result.Cancelled && p.Source == ProgramSource.Desktop) stuck.Add(row);
            }
            UpdateHistory.Save(_history);
            RefreshHistory();
        }
        finally
        {
            Uninstalling = false;
        }

        Message = removed.Count == 0 ? T("No programs were uninstalled.") : F("{0} program uninstalled.|{0} programs uninstalled.", removed.Count);

        // If the uninstaller did not run or did not remove the entry: the entry can also be removed from Windows' list.
        if (stuck.Count > 0 && Dialogs.Confirm(T("Entry is still listed"),
                T("The uninstaller of these programs did not run or did not remove their entry; they keep showing in Windows' list too. Remove the entries from the list? (If an uninstaller window is still open, finish it first.)"),
                stuck.Select(r => $"{r.Name}: {r.StatusText}"), T("Remove from list")))
            await RemoveEntriesAsync(stuck);
        if (removed.Count == 0) return;

        await LoadProgramsAsync();
        if (_updatableCount > 0 || _lastPackages.Count > 0) _ = CheckUpdatesAsync();
        await OfferLeftoversAsync(removed.Where(p => p.Source == ProgramSource.Desktop).ToList());
    }

    // If programs to be uninstalled are open, permission to close them is asked (as for updates).
    async Task<List<ProgramRow>> CloseProgramsAsync(List<ProgramRow> rows)
    {
        var running = await Task.Run(() => rows.ToDictionary(r => r, r => AppProcesses.FindRunningIn(r.Program.Dirs)));
        var open = rows.Where(r => running[r].Count > 0).ToList();
        if (open.Count == 0) return rows;

        int choice = Dialogs.Choice(T("Open apps"),
            T("These programs are open right now. They have to be closed before uninstalling; save your work first."),
            open.Select(r => $"{r.Name} ({string.Join(", ", running[r].Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase))})"),
            T("Close and uninstall"), T("Skip these"), T("Cancel"));
        if (choice is 2 or -1) return [];
        if (choice == 1) return rows.Except(open).ToList();

        var left = await AppProcesses.CloseGracefullyAsync(open.SelectMany(r => running[r]).ToList(), TimeSpan.FromSeconds(15));
        if (left.Count == 0) return rows;
        var leftIds = left.Select(p => p.Id).ToHashSet();
        var stuck = open.Where(r => running[r].Any(p => leftIds.Contains(p.Id))).ToList();
        int force = Dialogs.Choice(T("Apps that did not close"),
            T("These apps did not close within 15 seconds (a save prompt may be open, or they minimize to the tray). Forcing them closed loses unsaved data."),
            stuck.Select(r => r.Name), T("Force close"), T("Skip these"));
        if (force == 0)
        {
            AppProcesses.Kill(left);
            await Task.Delay(1000);
            return rows;
        }
        return rows.Except(stuck).ToList();
    }

    async Task OfferLeftoversAsync(List<InstalledProgram> removed)
    {
        if (removed.Count == 0) return;
        Message = T("Looking for leftovers…");
        var remaining = _inventory;
        var found = await Task.Run(() => removed.SelectMany(p => Leftovers.Scan(p, remaining)).ToList());
        if (found.Count == 0)
        {
            Message = F("{0} program uninstalled.|{0} programs uninstalled.", removed.Count) + " " + T("No leftovers found.");
            return;
        }
        var rows = found.Select(f => new LeftoverRow(f)).ToList();
        var chosen = Dialogs.Leftovers(T("Leftover files and registry entries"),
            T("What the uninstalled program left behind. Your settings or saves (like game saves) may be here; select only what you are sure about. Files go to the Recycle Bin, registry keys are backed up before deletion."),
            rows);
        if (chosen.Count > 0) await DeleteLeftoversAsync(chosen);
        else Message = T("Leftovers were left untouched.");
    }

    // Broken entry (the program's files were deleted by hand): removes the entry in the Programs list (the same entry Windows shows) and,
    // if wanted, the leftovers. The entry comes preselected, other leftovers do not; the entry is backed up before it is deleted.
    async Task RemoveEntriesAsync(List<ProgramRow> rows)
    {
        if (rows.Count == 0 || Uninstalling) return;
        Uninstalling = true;
        try
        {
            Message = T("Looking for leftovers…");
            var remaining = _inventory;
            var programs = rows.Select(r => r.Program).ToList();
            var found = await Task.Run(() => programs.SelectMany(p => Leftovers.Scan(p, remaining)).ToList());
            if (!found.Any(f => f.ListEntry))
            {
                Message = T("The entry is already gone.");
                await LoadProgramsAsync();
                return;
            }
            var items = found.Select(f => new LeftoverRow(f) { IsSelected = f.ListEntry }).ToList();
            var chosen = Dialogs.Leftovers(T("Remove from list"),
                T("The selected entries are removed from Windows' program list and from here; they are backed up first. The program's remaining files and settings are listed too; select them only if you are sure."),
                items);
            if (chosen.Count == 0)
            {
                Message = T("Leftovers were left untouched.");
                return;
            }
            await DeleteLeftoversAsync(chosen);
        }
        finally
        {
            Uninstalling = false;
        }
        await LoadProgramsAsync();
    }

    async Task DeleteLeftoversAsync(List<Leftover> items)
    {
        var backup = Path.Combine(AppPaths.Folder, "Backups", "Leftovers", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        if (items.Any(i => i.NeedsAdmin))
        {
            var file = Path.Combine(AppPaths.Folder, "leftovers.json");
            AppPaths.WriteJson(file, new Elevated.LeftoverRequest(items, backup), AppJson.Default.LeftoverRequest);
            await RunElevatedAsync(["--leftovers", file], T("Leftover files and registry entries"));
            return;
        }
        var (log, failures) = await Task.Run(() => Leftovers.Delete(items, backup));
        Message = failures == 0 ? F("{0} leftover cleaned.|{0} leftovers cleaned.", items.Count) : T("Some operations failed.");
        Dialogs.Info(T("Leftover files and registry entries"), failures == 0 ? F("{0} leftover cleaned.|{0} leftovers cleaned.", items.Count) : T("Some operations failed."), log, warning: failures > 0);
    }

    // --- Uninstalling from the Updates tab ---

    // The match of a winget package in the Programs list: Store package ID, the same name or (for truncated names) the start of the name.
    internal static InstalledProgram? Match(WingetPackage pkg, IReadOnlyList<InstalledProgram> programs)
    {
        if (programs.FirstOrDefault(p => p.Source == ProgramSource.Store && p.Key.StartsWith("MSIX|" + pkg.Id + "_", StringComparison.OrdinalIgnoreCase)) is { } byId)
            return byId;
        var name = pkg.Name.Replace("…", "").Trim();
        var preferred = pkg.IsStore ? ProgramSource.Store : ProgramSource.Desktop;
        var exact = programs.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.FirstOrDefault(p => p.Source == preferred) ?? exact[0];
        if (pkg.Name.EndsWith('…') && name.Length >= 5)
        {
            var prefix = programs.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefix.Count == 1) return prefix[0];
        }
        return null;
    }

    async Task UninstallPackageAsync(PackageRow row)
    {
        if (!_programsLoaded) await LoadProgramsAsync();
        if (Match(row.Package, _inventory) is { } program && _programs.FirstOrDefault(r => r.Program.Key == program.Key) is { } programRow)
        {
            await UninstallProgramsAsync([programRow]);
            return;
        }
        if (!Dialogs.Confirm(T("Uninstall programs"), F("{0} will be uninstalled.", row.Name) + " " + T("This cannot be undone."), T("Yes, uninstall"), danger: true)) return;
        Uninstalling = true;
        try
        {
            Message = F("Uninstalling {0}…", row.Name);
            var result = await Winget.UninstallAsync(row.Package, SilentInstall, CancellationToken.None);
            Message = $"{row.Name}: {result.Message}";
            _history.Add(new HistoryEntry(DateTime.Now, row.Name, T("uninstall"), result.Removed, result.Message));
            UpdateHistory.Save(_history);
            RefreshHistory();
        }
        finally
        {
            Uninstalling = false;
        }
        await CheckUpdatesAsync();
        if (_programsLoaded) await LoadProgramsAsync();
    }
}
