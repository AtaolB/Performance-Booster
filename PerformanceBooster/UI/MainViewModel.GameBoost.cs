using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed record TimerChoice(double Ms)
{
    public override string ToString() => F("{0} ms", Ms.ToString("0.0##", L.Culture));
}

// The Game mode tab's extras: a finer timer held the whole time the app runs, and while a game runs, background apps on low CPU
// and RAM cleaning (standby list). GameWatcher applies the game parts; this part holds the settings
// and refreshes the live values every second while the Game mode tab is open.
public sealed partial class MainViewModel
{
    MemorySampler? _memorySampler;
    string _timerInfo = "", _standbyText = "–", _freeText = "–", _totalText = "–", _cleanerStatusText = "";
    bool _globalTimerNeeded;

    public IReadOnlyList<TimerChoice> TimerChoices { get; } = [new(0.5), new(1), new(2)];

    public ICommand EnableGlobalTimerCommand { get; private set; } = null!;
    public ICommand PurgeNowCommand { get; private set; } = null!;
    public ICommand RemoveCleanerTaskCommand { get; private set; } = null!;

    public string TimerInfo { get => _timerInfo; private set => Set(ref _timerInfo, value); }
    public bool GlobalTimerNeeded { get => _globalTimerNeeded; private set => Set(ref _globalTimerNeeded, value); }
    public string StandbyText { get => _standbyText; private set => Set(ref _standbyText, value); }
    public string FreeText { get => _freeText; private set => Set(ref _freeText, value); }
    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }
    public string CleanerStatusText { get => _cleanerStatusText; private set => Set(ref _cleanerStatusText, value); }
    public bool CleanerInstalled => StandbyCleaner.TaskReady(_settings);
    public bool CleanerReady => StandbyCleaner.Ready(_settings);

    public string CleanerHint => Elevated.IsAdmin
        ? T("The app runs as administrator, so it empties the list itself. Emptied files load from the disk again the next time they are needed.")
        : T("Emptying needs administrator rights: the first time, one approval creates a scheduled task, so the cleaner starts without asking when a game starts. Emptied files load from the disk again the next time they are needed.");

    public bool TimerOn
    {
        get => _settings.TimerOn;
        set => SaveGameSetting(value, _settings.TimerOn, v => _settings.TimerOn = v);
    }

    public TimerChoice SelectedTimer
    {
        get => TimerChoices.FirstOrDefault(c => Math.Abs(c.Ms - _settings.TimerMs) < 0.001) ?? TimerChoices[0];
        set
        {
            if (value is null)
            {
                RaiseLater(nameof(SelectedTimer)); // an empty selection from the drop-down: show the chosen value again
                return;
            }
            SaveGameSetting(value.Ms, _settings.TimerMs, v => _settings.TimerMs = v);
        }
    }

    public bool LimitBackground
    {
        get => _settings.LimitBackground;
        set => SaveGameSetting(value, _settings.LimitBackground, v => _settings.LimitBackground = v);
    }

    public int StandbyAtLeastMb
    {
        get => _settings.StandbyAtLeastMb;
        set => SaveGameSetting(Math.Max(0, value), _settings.StandbyAtLeastMb, v => _settings.StandbyAtLeastMb = v);
    }

    public int FreeBelowMb
    {
        get => _settings.FreeBelowMb;
        set => SaveGameSetting(Math.Max(0, value), _settings.FreeBelowMb, v => _settings.FreeBelowMb = v);
    }

    // Without administrator rights, the scheduled task is created the first time (one approval); the box stays off until then.
    public bool CleanStandby
    {
        get => _settings.CleanStandby;
        set
        {
            if (value == _settings.CleanStandby) return;
            if (value && !CleanerReady)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => _ = InstallCleanerAsync());
                return;
            }
            SaveGameSetting(value, _settings.CleanStandby, v => _settings.CleanStandby = v);
        }
    }

    void SaveGameSetting<TValue>(TValue value, TValue current, Action<TValue> store, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(value, current)) return;
        store(value);
        _settings.Save();
        Raise(name!);
        TimerResolution.Apply(_settings);
        TickGameWatcher();
        RefreshGameBoost();
    }

    // --- Windows Defender: the listed games are not scanned (process exclusions with full paths) ---

    bool? _defenderActive;
    string _defenderStatus = "";
    readonly SemaphoreSlim _defenderGate = new(1, 1);

    public string DefenderStatusText { get => _defenderStatus; private set => Set(ref _defenderStatus, value); }

    public bool DefenderExclude
    {
        get => _settings.DefenderExclude;
        set
        {
            if (_settings.DefenderExclude == value) return;
            _settings.DefenderExclude = value;
            _settings.Save();
            Raise(nameof(DefenderExclude));
            _ = SyncDefenderAsync(silent: false);
        }
    }

    // Silent (a game's path was learned, a game was removed): only when the app runs as administrator, so nothing asks.
    async Task SyncDefenderAsync(bool silent)
    {
        await _defenderGate.WaitAsync();
        try
        {
            await SyncDefenderCoreAsync(silent);
        }
        finally
        {
            _defenderGate.Release();
        }
    }

    async Task SyncDefenderCoreAsync(bool silent)
    {
        if (Demo.On)
        {
            RefreshDefender();
            return;
        }
        var (add, remove) = Defender.Plan(_settings);
        if (add.Count + remove.Count > 0 && (!silent || (Elevated.IsAdmin && !Busy)))
        {
            var file = Path.Combine(AppPaths.Folder, "defender-change.json");
            AppPaths.WriteJson(file, new Defender.Change(add, remove), AppJson.Default.Change);
            var outcome = silent ? await Elevated.RunAsync("--defender", file) : await RunElevatedAsync(["--defender", file], T("Windows Defender"));
            if (outcome is { Started: true, ExitCode: 0 })
            {
                _settings.DefenderExcluded = _settings.DefenderExcluded.Concat(add)
                    .Where(p => !remove.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _settings.Save();
            }
            else if (!silent)
            {
                // Declined or failed: the box shows what is actually in effect.
                _settings.DefenderExclude = _settings.DefenderExcluded.Count > 0;
                _settings.Save();
                Raise(nameof(DefenderExclude));
            }
        }
        RefreshDefender();
    }

    void RefreshDefender()
    {
        var parts = new List<string>();
        if (_defenderActive == false) parts.Add(T("Defender is not the active antivirus on this computer, so there is nothing to exclude."));
        if (!DefenderExclude) parts.Add(T("Off: games are scanned like every other program."));
        else
        {
            var excluded = _settings.DefenderExcluded.Select(Path.GetFileName).ToList();
            parts.Add(excluded.Count > 0 ? F("Not scanned: {0}", string.Join(", ", excluded)) : T("No game is excluded yet."));
            var unknown = Defender.Unknown(_settings);
            if (unknown.Count > 0) parts.Add(F("Start these once while the app runs, so their location is known: {0}", string.Join(", ", unknown)));
        }
        DefenderStatusText = string.Join(" ", parts);
    }

    // A game's full path, when the game runs now (added from the running apps) or later (game mode sees it start).
    void RememberGamePath(string entry, string? path)
    {
        if (path is null || string.Equals(_settings.GamePaths.GetValueOrDefault(entry), path, StringComparison.OrdinalIgnoreCase)) return;
        _settings.GamePaths[entry] = path;
        _settings.Save();
        RebuildEntries(); // the row's menu needs the location
        SyncGpuPreference();
        _ = SyncDefenderAsync(silent: true);
    }

    void InitGameBoost()
    {
        TimerResolution.Apply(_settings); // at once, also when Windows starts the app in the tray
        BackgroundLimiter.RestoreLeftovers(); // apps left slowed down if the app was closed unexpectedly during a game
        WifiScan.RestoreLeftovers(); // the same for a paused Wi-Fi network search
        InitGameList();
        EnableGlobalTimerCommand = new RelayCommand(async _ =>
        {
            if (Tweaks.FirstOrDefault(r => r.Id == "sched.timer") is { } row && row.Options.FirstOrDefault(o => o.Key == "on") is { } on)
                await ApplyAsync([(row, on)]);
            RefreshGameBoost();
        }, _ => !Busy);
        PurgeNowCommand = new RelayCommand(async _ => await PurgeNowAsync(), _ => !Busy);
        RemoveCleanerTaskCommand = new RelayCommand(async _ => await RemoveCleanerTaskAsync(), _ => !Busy && CleanerInstalled);
        FixMouseCommand = new RelayCommand(_ => FixMouse());
        CheckMouse();
        RefreshDefender();
        _ = LoadDefenderStateAsync();
    }

    async Task LoadDefenderStateAsync()
    {
        if (Demo.On)
        {
            _defenderActive = true;
            RefreshDefender();
            return;
        }
        _defenderActive = await Task.Run(Defender.Active);
        RefreshDefender();
    }


    async Task InstallCleanerAsync()
    {
        Raise(nameof(CleanStandby));
        if (!Dialogs.Confirm(T("RAM cleaning"),
                T("Emptying the standby list needs administrator rights. With your approval a scheduled task is created once, so the cleaner starts without asking whenever a game starts.\n\nA program that can change the app's exe could use this task to get administrator rights; keep the app in a folder only you can write to."),
                T("Create the task")))
            return;
        var outcome = await RunElevatedAsync(["--memory-task", "install"], T("RAM cleaning"));
        if (outcome is not { ExitCode: 0 }) return;
        _settings.CleanerTaskExe = Environment.ProcessPath;
        _settings.CleanStandby = true;
        _settings.Save();
        Raise(nameof(CleanStandby));
        Raise(nameof(CleanerInstalled));
        TickGameWatcher();
        RefreshGameBoost();
    }

    async Task RemoveCleanerTaskAsync()
    {
        if (!Dialogs.Confirm(T("RAM cleaning"), T("Remove the scheduled task of the RAM cleaner? RAM cleaning turns off."), T("Remove"))) return;
        var outcome = await RunElevatedAsync(["--memory-task", "remove"], T("RAM cleaning"));
        if (outcome is null) return;
        _settings.CleanerTaskExe = null;
        _settings.CleanStandby = false;
        _settings.Save();
        Raise(nameof(CleanStandby));
        Raise(nameof(CleanerInstalled));
        TickGameWatcher();
        RefreshGameBoost();
    }

    // During a game the running cleaner empties the list; otherwise the task empties it once, or without the task the elevated copy does.
    async Task PurgeNowAsync()
    {
        if (_watcher.Cleaner is { } session)
        {
            session.PurgeNow();
            Message = T("Emptying the standby list…");
            return;
        }
        if (Elevated.IsAdmin)
        {
            bool ok = await Task.Run(StandbyCleaner.PurgeNow);
            Message = ok ? F("Standby list emptied: {0} freed.", SizeText(StandbyCleaner.ReadStatus()?.LastFreedMb ?? 0)) : StandbyCleaner.ReadStatus()?.Error ?? "";
            RefreshGameBoost();
            return;
        }
        if (CleanerInstalled)
        {
            bool started = await Task.Run(StandbyCleaner.RunTask);
            Message = started ? T("Emptying the standby list…") : T("The scheduled task could not be started; turn RAM cleaning off and on to create it again.");
            return;
        }
        await RunElevatedAsync(["--purge-standby"], T("RAM cleaning"));
        RefreshGameBoost();
    }

    void RefreshGameBoost()
    {
        if (Demo.On)
        {
            StandbyText = SizeText(6420);
            FreeText = SizeText(14100);
            TotalText = SizeText(Demo.RamTotalGb * 1024);
            TimerInfo = F("Now {0} · finest {1} · Windows default {2}", MsText(0.5), MsText(0.5), MsText(15.625));
            CleanerStatusText = CleanerText();
            return;
        }
        _memorySampler ??= new MemorySampler();
        var m = _memorySampler.Sample();
        StandbyText = SizeText(m.StandbyMb);
        FreeText = SizeText(m.FreeMb);
        TotalText = SizeText(m.TotalMb);

        TimerInfo = TimerResolution.Query() is { } t
            ? F("Now {0} · finest {1} · Windows default {2}", MsText(t.CurrentMs), MsText(t.FinestMs), MsText(t.CoarsestMs))
              + (TimerOn && !TimerResolution.HelperRunning ? " · " + T("Windows refused the request") : "")
            : T("Windows does not report the timer resolution.");
        GlobalTimerNeeded = TimerOn && Environment.OSVersion.Version.Build >= 22000
                            && Tweaks.FirstOrDefault(r => r.Id == "sched.timer") is { IsAvailable: true, CurrentKey: not "on" };

        CleanerStatusText = CleanerText();
        Raise(nameof(CleanerInstalled));
    }

    string CleanerText()
    {
        if (!CleanerReady) return T("Off. Turning it on asks for administrator approval once and creates a scheduled task.");
        var s = StandbyCleaner.ReadStatus();
        bool running = s is { Running: true } && DateTime.Now - s.At < TimeSpan.FromSeconds(5);
        var parts = new List<string>
        {
            !CleanStandby ? T("Off") : running ? T("Running") : _watcher.Cleaner is not null ? T("Starting…") : T("Waits for a game"),
        };
        if (s is { Purges: > 0, LastPurge: { } last })
            parts.Add(F("emptied {0} time, last at {1} ({2} freed)|emptied {0} times, last at {1} ({2} freed)",
                s.Purges, last.ToString("t", L.Culture), SizeText(s.LastFreedMb)));
        if (s?.Error is { } error && (running || s.Purges == 0)) parts.Add(error);
        return string.Join(" · ", parts);
    }

    // --- Mouse: acceleration off and speed 10, shown as an issue until fixed; once fixed, set back when another program changes them ---

    public const string MouseIssueId = "mouse.gaming";
    MouseState? _mouse;

    public ICommand FixMouseCommand { get; private set; } = null!;

    public string MouseStatusText => _mouse is { } m
        ? F("Enhance pointer precision: {0} · pointer speed: {1} (recommended {2})", m.Acceleration ? T("on") : T("off"), m.Speed, MouseSettings.RecommendedSpeed)
        : T("The mouse settings could not be read.");

    public bool MouseNeedsFix => _mouse is { } m && !MouseSettings.IsRecommended(m);

    public bool KeepMouse
    {
        get => _settings.KeepMouse;
        set
        {
            if (_settings.KeepMouse == value) return;
            _settings.KeepMouse = value;
            _settings.Save();
            Raise(nameof(KeepMouse));
            CheckMouse();
        }
    }

    // Every 3 s, also in the tray.
    void CheckMouse()
    {
        if (Demo.On)
        {
            _mouse = new MouseState(false, MouseSettings.RecommendedSpeed);
            return;
        }
        var state = MouseSettings.Read();
        if (state is { } m && !MouseSettings.IsRecommended(m) && KeepMouse && MouseSettings.ApplyRecommended())
        {
            Message = F("Another program changed the mouse settings; set back to acceleration off and speed {0}.", MouseSettings.RecommendedSpeed);
            state = MouseSettings.Read();
        }
        if (state == _mouse) return;
        _mouse = state;
        foreach (var n in new[] { nameof(MouseStatusText), nameof(MouseNeedsFix) }) Raise(n);
    }

    void FixMouse()
    {
        if (!MouseSettings.ApplyRecommended())
        {
            Message = T("The mouse settings could not be changed.");
            return;
        }
        KeepMouse = true;
        CheckMouse();
        Message = F("Mouse acceleration off, pointer speed {0}; kept this way from now on.", MouseSettings.RecommendedSpeed);
    }

    static string SizeText(double mb) => double.IsNaN(mb) ? "–" : Maintenance.Size((long)(mb * 1048576));

    static string MsText(double ms) => F("{0} ms", ms.ToString("0.0##", L.Culture));
}
