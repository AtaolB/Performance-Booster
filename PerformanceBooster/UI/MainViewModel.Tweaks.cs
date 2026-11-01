using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Settings tab: choose and apply one by one, apply the selected or recommended ones in bulk, undo to the original value.
public sealed partial class MainViewModel
{
    public ObservableCollection<TweakRow> Tweaks { get; } = [];
    public ICollectionView TweaksView { get; private set; } = null!;

    public ICommand ApplyRecommendedCommand { get; private set; } = null!;
    public ICommand ApplySelectedCommand { get; private set; } = null!;
    public ICommand ApplyOneCommand { get; private set; } = null!;
    public ICommand FixOneCommand { get; private set; } = null!;
    public ICommand RevertOneCommand { get; private set; } = null!;
    public ICommand RevertAllCommand { get; private set; } = null!;
    public ICommand RefreshCommand { get; private set; } = null!;
    public ICommand SelectProblemsCommand { get; private set; } = null!;
    public ICommand ClearSelectionCommand { get; private set; } = null!;
    public ICommand ClearTweakSearchCommand { get; private set; } = null!;

    Task? _tweaksTask;
    string _viewSignature = "";
    bool _restorePoint = true, _showUnavailable, _onlyDifferent;
    int _fixableCount, _selectedCount, _backupCount, _selectedTab;
    string _lastCheck = "", _tweakSearch = "", _tweakSearchResult = "", _tweaksEmptyText = "";
    string[] _tweakWords = [];

    public bool RestorePointBeforeBulk { get => _restorePoint; set => Set(ref _restorePoint, value); }
    public bool ShowUnavailable { get => _showUnavailable; set { if (Set(ref _showUnavailable, value)) RefreshTweaksView(); } }
    public bool OnlyDifferent { get => _onlyDifferent; set { if (Set(ref _onlyDifferent, value)) RefreshTweaksView(); } }
    public string LastCheck { get => _lastCheck; private set => Set(ref _lastCheck, value); }

    public string TweakSearch
    {
        get => _tweakSearch;
        set
        {
            if (!Set(ref _tweakSearch, value ?? "")) return;
            _tweakWords = TextSearch.Words(_tweakSearch);
            RefreshTweaksView();
        }
    }

    public string TweakSearchResult { get => _tweakSearchResult; private set => Set(ref _tweakSearchResult, value); }

    // Shown in place of the list when nothing is left to show.
    public string TweaksEmptyText
    {
        get => _tweaksEmptyText;
        private set { if (Set(ref _tweaksEmptyText, value)) Raise(nameof(ShowTweaksEmpty)); }
    }
    public bool ShowTweaksEmpty => TweaksEmptyText.Length > 0;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            OnTabOpened(value);
        }
    }

    public int FixableCount
    {
        get => _fixableCount;
        private set { if (Set(ref _fixableCount, value)) { Raise(nameof(ApplyRecommendedText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set { if (Set(ref _selectedCount, value)) { Raise(nameof(ApplySelectedText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int BackupCount
    {
        get => _backupCount;
        private set { if (Set(ref _backupCount, value)) { Raise(nameof(RevertAllText)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string ApplyRecommendedText => FixableCount > 0 ? F("Apply recommended ({0})", FixableCount) : T("All at recommended values");
    public string ApplySelectedText => F("Apply selected ({0})", SelectedCount);
    public string RevertAllText => BackupCount > 0 ? F("Undo all ({0})", BackupCount) : T("Nothing to undo");

    void InitTweaks()
    {
        foreach (var tweak in Catalog.All)
        {
            var row = new TweakRow(tweak);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(TweakRow.IsSelected) or nameof(TweakRow.SelectedOption) or nameof(TweakRow.CanApply))
                    SelectedCount = Tweaks.Count(r => r.IsSelected && r.CanApply);
            };
            Tweaks.Add(row);
        }
        TweaksView = CollectionViewSource.GetDefaultView(Tweaks);
        TweaksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakRow.Category)));
        TweaksView.Filter = o => o is TweakRow r && PassesOptions(r) && r.Matches(_tweakWords);

        ApplyRecommendedCommand = new RelayCommand(async _ =>
            await ApplyAsync(Tweaks.Where(r => r.CanFix).Select(r => (r, r.Recommended!)).ToList()), _ => !Busy && FixableCount > 0);
        ApplySelectedCommand = new RelayCommand(async _ =>
            await ApplyAsync(Tweaks.Where(r => r.IsSelected && r.CanApply).Select(r => (r, r.SelectedOption!)).ToList()), _ => !Busy && SelectedCount > 0);
        ApplyOneCommand = new RelayCommand(async p =>
        {
            if (p is TweakRow { CanApply: true } r) await ApplyAsync([(r, r.SelectedOption!)]);
        }, _ => !Busy);
        FixOneCommand = new RelayCommand(async p =>
        {
            if (p is MouseIssueId)
            {
                FixMouse();
                return;
            }
            if (p is string id && Tweaks.FirstOrDefault(r => r.Id == id) is { Recommended: { } rec } row) await ApplyAsync([(row, rec)]);
        }, _ => !Busy);
        RevertOneCommand = new RelayCommand(async p =>
        {
            if (p is TweakRow { HasBackup: true } r) await RunElevatedAsync(["--restore", r.Id], F("{0}: undo", r.Title));
        }, _ => !Busy);
        RevertAllCommand = new RelayCommand(async _ => await RevertAllAsync(), _ => !Busy && BackupCount > 0);
        RefreshCommand = new RelayCommand(async _ => { await RefreshTweaksAsync(); await RefreshSystemAsync(); }, _ => !Busy);
        SelectProblemsCommand = new RelayCommand(_ => { foreach (var r in Tweaks) r.IsSelected = r.State == CheckState.Problem && r.CanFix; });
        ClearSelectionCommand = new RelayCommand(_ => { foreach (var r in Tweaks) r.IsSelected = false; });
        ClearTweakSearchCommand = new RelayCommand(_ => TweakSearch = "");
    }

    bool PassesOptions(TweakRow r) => (ShowUnavailable || r.IsAvailable) && (!OnlyDifferent || r.State == CheckState.Problem);

    void RefreshTweaksView()
    {
        TweaksView.Refresh();
        int shown = TweaksView.Cast<object>().Count();
        TweakSearchResult = _tweakWords.Length > 0 && shown > 0 ? F("{0} setting found|{0} settings found", shown) : "";
        if (shown > 0 || !Tweaks.Any(r => r.IsAvailable)) // nothing read yet: the list is still loading
        {
            TweaksEmptyText = "";
            return;
        }
        if (_tweakWords.Length == 0)
        {
            TweaksEmptyText = T("No setting differs from the recommended value.");
            return;
        }
        int hidden = Tweaks.Count(r => r.Matches(_tweakWords));
        TweaksEmptyText = hidden > 0
            ? F("{0} setting matches, but the options above hide it.|{0} settings match, but the options above hide them.", hidden)
            : F("No setting matches \"{0}\".", TweakSearch.Trim());
    }

    Task RefreshTweaksAsync()
    {
        if (_tweaksTask is { IsCompleted: false }) return _tweaksTask;
        return _tweaksTask = RefreshTweaksCoreAsync();
    }

    async Task RefreshTweaksCoreAsync()
    {
        var (results, backups) = await Task.Run(() =>
        {
            Catalog.RefreshNic();
            return (Catalog.All.Select(t => t.Evaluate()).ToList(), Backups.Load());
        });
        for (int i = 0; i < Tweaks.Count; i++) Tweaks[i].Update(results[i], backups.ContainsKey(Tweaks[i].Id));

        FixableCount = Tweaks.Count(r => r.CanFix);
        BackupCount = backups.Count(b => Catalog.Find(b.Key) is not null);
        SelectedCount = Tweaks.Count(r => r.IsSelected && r.CanApply);
        LastCheck = F("Last check {0}", DateTime.Now.ToString("t", L.Culture));
        if (Tweaks.FirstOrDefault(r => r.Id == "power.plan") is { IsAvailable: true } plan) SyncGamePlans(plan.Options);
        await RefreshInterruptsAsync();
        RefreshPingTargets();

        // The view is refreshed only when something that affects the filter changes, so open drop-downs stay open.
        var signature = string.Concat(Tweaks.Select(r => (int)r.State));
        if (signature != _viewSignature)
        {
            _viewSignature = signature;
            RefreshTweaksView();
        }
    }

    async Task ApplyAsync(List<(TweakRow Row, TweakOption Option)> items)
    {
        if (items.Count == 0) return;
        var args = new List<string> { "--set", string.Join(",", items.Select(i => $"{i.Row.Id}={i.Option.Key}")) };
        if (RestorePointBeforeBulk && items.Count > 1) args.Add("--rp");
        await RunElevatedAsync(args.ToArray(), items.Count == 1 ? items[0].Row.Title : F("{0} setting|{0} settings", items.Count));
        foreach (var (row, _) in items)
        {
            row.ResetPick();
            row.IsSelected = false;
        }
        await RefreshTweaksAsync();
    }

    async Task RevertAllAsync()
    {
        if (Dialogs.Confirm(T("Undo all"), F("{0} setting changed by the app will return to its previous value.|{0} settings changed by the app will return to their previous values.", BackupCount), T("Undo")))
            await RunElevatedAsync(["--restore", "all"], T("Revert"));
    }

    // null if the user declines or an error occurs.
    async Task<ElevatedOutcome?> RunElevatedAsync(string[] args, string title)
    {
        Busy = true;
        StatusText = T("Waiting for administrator permission…");
        StatusBrush = Palette.Info;
        try
        {
            var outcome = await Elevated.RunAsync(args);
            if (!outcome.Started)
            {
                Message = T("Cancelled: administrator permission was not granted.");
                return null;
            }
            if (_tweaksTask is { IsCompleted: false }) await _tweaksTask;
            await RefreshTweaksAsync();

            bool ok = outcome.ExitCode == 0;
            Message = ok ? T("Done.") + (outcome.NeedsReboot ? " " + T("You need to restart.") : "") : T("Some operations failed.");
            var summary = ok ? T("Done.") : T("Some operations failed.");
            if (outcome.NeedsReboot) summary += " " + T("Some settings take effect after the computer restarts.");
            Dialogs.Info(title, summary, outcome.Log, warning: !ok);
            return outcome;
        }
        catch (Exception ex)
        {
            Message = F("Error: {0}", ex.Message);
            return null;
        }
        finally
        {
            Busy = false;
        }
    }
}
