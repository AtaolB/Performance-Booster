using System.Windows.Media;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed class TweakRow(Tweak tweak) : ObservableObject
{
    public Tweak Tweak { get; } = tweak;
    public string Id => Tweak.Id;
    public string Category => Tweak.Category;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string? Note => Tweak.Note;
    public bool HasNote => !string.IsNullOrEmpty(Tweak.Note);
    public string ImpactText => Tweak.Impact switch { Impact.High => T("high impact"), Impact.Medium => T("medium impact"), _ => T("low impact") };
    public Brush ImpactBrush => Tweak.Impact switch { Impact.High => Palette.Warn, Impact.Medium => Palette.Info, _ => Palette.Muted };
    public string Flags => string.Join(" · ", new[] { Tweak.NeedsReboot ? T("needs a restart") : null, Tweak.RestartsNic ? T("network drops for a few seconds") : null }.Where(x => x is not null));
    public bool HasFlags => Tweak.NeedsReboot || Tweak.RestartsNic;

    // The whole text for the tooltip (the row shows it cut to two lines).
    public string FullText => string.Join("\n\n", new[] { Title, Description, Note, HasFlags ? Flags : null }.Where(x => !string.IsNullOrEmpty(x)));

    // The search box also finds a setting by its group ("network" lists the whole Network group).
    public bool Matches(string[] words) => TextSearch.Matches(words, Title, Description, Note, Category);

    TweakStatus _status = TweakStatus.Unavailable("…");
    IReadOnlyList<TweakOption> _options = [];
    TweakOption? _selected;
    bool _isSelected, _hasBackup, _userPicked;

    public CheckState State => _status.State;
    public string CurrentLabel => _status.CurrentLabel;
    public string? CurrentKey => _status.Current?.Key;
    public bool IsAvailable => State != CheckState.NotApplicable;
    public IReadOnlyList<TweakOption> Options { get => _options; private set => Set(ref _options, value); }

    public TweakOption? SelectedOption
    {
        get => _selected;
        set
        {
            // The drop-down sends an empty selection while its list is being replaced; the selection is kept and shown again.
            if (value is null)
            {
                if (_selected is not null) RaiseLater(nameof(SelectedOption));
                return;
            }
            if (!Set(ref _selected, value)) return;
            _userPicked = true;
            Raise(nameof(CanApply));
        }
    }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool HasBackup { get => _hasBackup; private set { if (Set(ref _hasBackup, value)) Raise(nameof(UndoVisibility)); } }
    public System.Windows.Visibility UndoVisibility => HasBackup ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;

    public TweakOption? Recommended => Options.FirstOrDefault(o => o.Recommended);
    public bool CanApply => IsAvailable && SelectedOption is not null && !SelectedOption.Matches(_status.Raw);
    public bool CanFix => State == CheckState.Problem && Recommended is not null;

    public Brush StatusBrush => State switch
    {
        CheckState.Ok => Palette.Ok,
        CheckState.Problem => Palette.Bad,
        CheckState.Info => Palette.Info,
        _ => Palette.Muted,
    };

    public string StatusText => State switch
    {
        CheckState.Ok => T("Recommended"),
        CheckState.Problem => T("Different"),
        CheckState.Info => T("Choice"),
        _ => T("N/A"),
    };

    public void Update(TweakStatus status, bool hasBackup)
    {
        _status = status;
        HasBackup = hasBackup;
        // Labels count too: "On for 3 of 5 devices", "(recommended)" when the recommendation changes.
        bool optionsChanged = !status.Options.Select(o => (o.Key, o.ToString())).SequenceEqual(Options.Select(o => (o.Key, o.ToString())));
        if (optionsChanged)
        {
            var keep = _userPicked ? _selected : null;
            _selected = null; // the old object is not offered any more
            Options = status.Options;
            _userPicked = keep is not null && Options.Contains(keep);
            if (_userPicked) _selected = Options.First(o => o.Equals(keep));
        }
        if (!_userPicked || _selected is null || !Options.Contains(_selected))
        {
            // The object from the list shown, not the one the check read (an equal copy).
            var first = Options.Count > 0 ? Options[0] : null;
            var wanted = status.Current ?? Recommended ?? first;
            _selected = wanted is null ? null : Options.FirstOrDefault(o => o.Equals(wanted)) ?? first;
            _userPicked = false;
        }
        Raise(nameof(SelectedOption));
        foreach (var name in new[] { nameof(State), nameof(CurrentLabel), nameof(IsAvailable), nameof(StatusBrush), nameof(StatusText), nameof(CanApply), nameof(CanFix), nameof(Recommended) })
            Raise(name);
    }

    // After applying, the selection snaps back to the current value.
    public void ResetPick() => _userPicked = false;
}

public sealed class PingRow(PingTarget target) : ObservableObject
{
    public PingTarget Target { get; } = target;
    public string Name => Target.Name;

    string _host = target.Host, _last = "–", _avg = "–", _range = "", _jitter = "–", _loss = "–";
    double[] _series = [];
    Brush _status = Palette.Muted;

    public string Host { get => _host; private set => Set(ref _host, value); }
    public string Last { get => _last; private set => Set(ref _last, value); }
    public string Average { get => _avg; private set => Set(ref _avg, value); }
    public string Range { get => _range; private set => Set(ref _range, value); }
    public string Jitter { get => _jitter; private set => Set(ref _jitter, value); }
    public string Loss { get => _loss; private set => Set(ref _loss, value); }
    public double[] Series { get => _series; private set => Set(ref _series, value); }
    public Brush Status { get => _status; private set => Set(ref _status, value); }

    public async Task SampleAsync()
    {
        await Target.SampleAsync();
        Show();
    }

    // A made-up reply time (demo).
    public void Add(double ms)
    {
        Target.Add(ms);
        Show();
    }

    void Show()
    {
        var t = Target;
        Host = t.Host;
        Last = t.Last is double l ? $"{l:0} ms" : T("lost");
        Average = double.IsNaN(t.Average) ? "–" : $"{t.Average:0.0} ms";
        Range = double.IsNaN(t.Min) ? "" : $"{t.Min:0}–{t.Max:0} ms";
        Jitter = double.IsNaN(t.Jitter) ? "–" : $"{t.Jitter:0.0} ms";
        Loss = Pct(t.LossPercent);
        Series = t.Series();
        Status = Health(t);
    }

    static Brush Health(PingTarget t)
    {
        if (t.Samples < 3) return Palette.Muted;
        if (double.IsNaN(t.Average)) return Palette.Bad;
        bool local = t.Kind == PingKind.Gateway;
        if (t.LossPercent >= 5 || t.Jitter >= (local ? 5 : 10) || t.Average >= (local ? 10 : 100)) return Palette.Bad;
        if (t.LossPercent > 0 || t.Jitter >= (local ? 2 : 5) || t.Average >= (local ? 5 : 60)) return Palette.Warn;
        return Palette.Ok;
    }
}

public sealed class IssueRow(string key) : ObservableObject
{
    public string Key { get; } = key;

    string _title = "", _detail = "", _suggestion = "", _tabLabel = "";
    Brush _severity = Palette.Warn;
    string? _tweakId, _uri;
    int? _tab;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Detail { get => _detail; set { if (Set(ref _detail, value)) Raise(nameof(HasDetail)); } }
    public string Suggestion { get => _suggestion; set => Set(ref _suggestion, value); }
    public Brush Severity { get => _severity; set => Set(ref _severity, value); }
    public string? TweakId { get => _tweakId; set { if (Set(ref _tweakId, value)) { Raise(nameof(CanFix)); Raise(nameof(HasUri)); } } }
    public string? Uri { get => _uri; set { if (Set(ref _uri, value)) Raise(nameof(HasUri)); } }
    public int? TabTarget { get => _tab; set { if (Set(ref _tab, value)) Raise(nameof(HasTab)); } }
    public string TabLabel { get => _tabLabel; set => Set(ref _tabLabel, value); }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool CanFix => TweakId is not null;
    public bool HasUri => Uri is not null && TweakId is null;
    public bool HasTab => TabTarget is not null;
}

public sealed class StartupRow(StartupItem item)
{
    public StartupItem Item { get; } = item;
    public string Name => Item.Name;
    public string Command => Item.Command;
    public string Scope => Item.Scope + (Item.NeedsAdmin ? " · " + T("needs administrator permission") : "");
    public bool Enabled => Item.Enabled;
    public string ToggleText => Enabled ? T("Disable") : T("Enable");
    public Brush StatusBrush => Enabled ? Palette.Ok : Palette.Muted;
    public string StatusText => Enabled ? T("On") : T("Off");
}

public sealed class InfoRow(InfoItem item)
{
    public InfoItem Item { get; } = item;
    public string Group => Item.Group;
    public string Label => Item.Label;
    public string Value => Item.Value;
    public string? Hint => Item.Hint;
    public string? Uri => Item.Uri;
    public bool HasHint => !string.IsNullOrEmpty(Item.Hint);
    public bool HasUri => Item.Uri is not null;
    public Brush StatusBrush => Item.State switch
    {
        CheckState.Ok => Palette.Ok,
        CheckState.Problem => Palette.Bad,
        CheckState.Info => Palette.Info,
        _ => Palette.Muted,
    };
}

// Encrypted: the time over DNS over HTTPS; Use then turns encryption on for the server (OptionKey is the encrypted option).
public sealed record DnsRow(string Name, string Server, string Time, string First, string Encrypted, string Failures, bool Best, string? OptionKey, bool InUse)
{
    public bool CanUse => OptionKey is not null;
    public string BestText => Best ? T("fastest") : "";
    public string InUseText => InUse ? T("in use") : "";
    public string UseTip => OptionKey?.EndsWith("-doh", StringComparison.Ordinal) == true
        ? T("Switches to this server with encrypted DNS and clears the DNS cache.")
        : T("Switches to this server and clears the DNS cache.");
}

// Held: kept out of bulk updates and the counter (just updated and waiting for a restart, or winget cannot update it).
public enum PackageState { Available, Waiting, Running, Done, Current, Failed, Skipped, Held }

public sealed class PackageRow(WingetPackage package) : ObservableObject
{
    public WingetPackage Package { get; } = package;
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Source => Package.Source;
    public string VersionText => $"{(Package.UnknownVersion ? T("version unknown") : Package.Installed)} → {Package.Available}";
    public string? Note => Package.IsWinget ? T("winget itself; updated last.")
        : Package.RequireExplicit ? T("winget upgrade --all skips this; it can be updated from here.")
        : Package.UnknownVersion ? T("The installed version could not be detected; the same version may be reinstalled.") : null;
    public bool HasNote => Note is not null;

    PackageState _state = PackageState.Available;
    string _status = T("Update available");
    double _progress = -1;
    bool _selected, _estimated, _stopping;
    UpgradeStage _stage;
    DateTime _stageStarted;

    public PackageState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            foreach (var n in new[] { nameof(CanUpdate), nameof(InBulk), nameof(CanCancel), nameof(StatusBrush), nameof(ShowProgress), nameof(IsIndeterminate), nameof(ShowPercent), nameof(PercentText) }) Raise(n);
        }
    }

    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public double ProgressValue => Math.Max(0, _progress);
    public bool IsIndeterminate => State == PackageState.Running && _progress < 0;
    public bool ShowProgress => State == PackageState.Running;
    public bool ShowPercent => State == PackageState.Running && _progress >= 0;
    public string PercentText => ShowPercent ? Pct(_progress) : "";
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool CanUpdate => State is PackageState.Available or PackageState.Failed or PackageState.Skipped or PackageState.Held;
    public bool InBulk => CanUpdate && State != PackageState.Held;
    public bool CanCancel => State is PackageState.Waiting or PackageState.Running;
    public bool IsInstalling => State == PackageState.Running && _stage == UpgradeStage.Installing && !_stopping;
    public bool IsStopping => State == PackageState.Running && _stopping;
    public bool IsFinished => State is PackageState.Done or PackageState.Current or PackageState.Failed or PackageState.Skipped;

    // This package's part of the overall bar (0-1): downloading is the first half, installing the second.
    public double Share => State switch
    {
        PackageState.Running => _stage switch
        {
            UpgradeStage.Downloading => ProgressValue / 200,
            UpgradeStage.Verifying => 0.5,
            UpgradeStage.Installing => 0.5 + ProgressValue / 200,
            _ => 0,
        },
        _ => IsFinished ? 1 : 0,
    };

    public Brush StatusBrush => State switch
    {
        PackageState.Done or PackageState.Current => Palette.Ok,
        PackageState.Failed => Palette.Bad,
        PackageState.Skipped => Palette.Warn,
        PackageState.Waiting or PackageState.Held => Palette.Muted,
        _ => Palette.Info,
    };

    public void Hold(string why)
    {
        State = PackageState.Held;
        StatusText = why;
        IsSelected = false;
    }

    void SetProgress(double value)
    {
        _progress = value;
        foreach (var n in new[] { nameof(ProgressValue), nameof(IsIndeterminate), nameof(ShowPercent), nameof(PercentText) }) Raise(n);
    }

    public void SetWaiting()
    {
        _stopping = false;
        State = PackageState.Waiting;
        StatusText = T("Queued");
        SetProgress(-1);
    }

    public void Report(UpgradeProgress p) => Report(p, DateTime.UtcNow);

    internal void Report(UpgradeProgress p, DateTime now)
    {
        // Late progress that arrives after the finish message, or after the user stopped the package.
        if (State is not (PackageState.Waiting or PackageState.Running) || _stopping) return;
        bool stageChanged = State == PackageState.Waiting || p.Stage != _stage;
        State = PackageState.Running;
        StatusText = p.Text;
        if (stageChanged)
        {
            _stage = p.Stage;
            _stageStarted = now;
            _estimated = p.Stage == UpgradeStage.Installing && p.Percent < 0;
        }
        if (p.Percent >= 0)
        {
            _estimated = false;
            SetProgress(p.Percent);
        }
        else if (_estimated) SetProgress(Winget.EstimatedInstallPercent(now - _stageStarted));
        else if (stageChanged) SetProgress(-1);
    }

    // Called regularly while the package runs: the estimated install percentage advances with time.
    public void Tick(DateTime now)
    {
        if (State == PackageState.Running && _estimated && !_stopping) SetProgress(Winget.EstimatedInstallPercent(now - _stageStarted));
    }

    // The user stopped the running package; it stays "running" until winget has exited.
    public void Stopping()
    {
        if (State != PackageState.Running) return;
        _stopping = true;
        StatusText = T("Stopping…");
        SetProgress(-1);
    }

    public void Finish(UpgradeResult r)
    {
        _stopping = false;
        State = r.Success ? PackageState.Done : r.NotApplicable ? PackageState.Current : PackageState.Failed;
        StatusText = r.Message;
        SetProgress(-1);
    }

    public void Skip(string why)
    {
        _stopping = false;
        State = PackageState.Skipped;
        StatusText = why;
        SetProgress(-1);
    }

    public void Reset()
    {
        _stopping = false;
        State = PackageState.Available;
        StatusText = T("Update available");
        SetProgress(-1);
    }

    // The error/skipped info must survive a re-check.
    public void CopyStatusFrom(PackageRow previous)
    {
        if (previous.State is not (PackageState.Failed or PackageState.Skipped)) return;
        State = previous.State;
        StatusText = previous.StatusText;
    }
}

public sealed class IgnoredRow(WingetPackage package, bool pinned)
{
    public WingetPackage Package { get; } = package;
    public bool Pinned { get; } = pinned;
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string VersionText => $"{(Package.UnknownVersion ? T("version unknown") : Package.Installed)} → {Package.Available}";
    public string Kind => Pinned ? T("ignored with winget pin") : F("version {0} skipped", Package.Available);
}

public sealed class DriverRow(DriverUpdate update) : ObservableObject
{
    public DriverUpdate Update { get; } = update;
    public string Id => Update.Id;
    public string Title => Update.Title;
    public string Detail => string.Join(" · ", new[]
    {
        Update.Class, Update.Manufacturer, Update.Date?.ToString("d", L.Culture), Update.SizeMb > 0 ? $"{Update.SizeMb:0.#} MB" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
    public bool IsOptional => Update.Optional;
    public string? Warning => Update.IsDisplay
        ? T("Graphics driver: updating from NVIDIA App, AMD Adrenalin or Intel's own app is better; the Windows Update version may be older.")
        : null;
    public bool HasWarning => Warning is not null;

    bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class HistoryRow(HistoryEntry entry)
{
    public string When => entry.At.ToString("g", L.Culture);
    public string Name => entry.Name;
    public string Change => entry.Change;
    public string Message => entry.Message;
    public Brush Brush => entry.Ok ? Palette.Ok : Palette.Bad;
}
