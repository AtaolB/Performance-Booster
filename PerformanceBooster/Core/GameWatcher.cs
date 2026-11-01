using System.Diagnostics;

namespace PerformanceBooster.Core;

public sealed record GameMatch(int Id, string Name, string Entry, string? Path, bool FromWindows);

// Switches the power plan while a game runs and switches back when it closes.
// A game is one from the app's list or, if allowed, one Windows recognizes as a game (WindowsGames).
// While the game runs it can also keep the other apps on low CPU (and off the game's cores), empty the standby list
// and pause Windows' Wi-Fi network search; all of it is undone when the game closes. Settings changed during the game apply at the next check (every 3 s).
// Works while the window is in the tray too; one process snapshot per check.
public sealed class GameWatcher
{
    Guid? _previousPlan;
    int _gameId;
    bool _wifiTried;
    string _baseStatus = "";
    readonly BackgroundLimiter _limiter = new();
    static readonly int Session = System.Diagnostics.Process.GetCurrentProcess().SessionId;

    public string? ActiveGame { get; private set; }
    public string? ActiveEntry { get; private set; } // the game list entry ("cs2.exe")
    public string? ActivePath { get; private set; }
    public bool ActiveFromWindows { get; private set; }
    public string Status { get; private set; } = T("Waiting for a game");
    public CleanerSession? Cleaner { get; private set; }
    public int LimitedApps => _limiter.Count;

    public void Tick(AppSettings settings)
    {
        var known = settings.DetectWindowsGames ? WindowsGames.Known() : [];
        if (!settings.GameBoost || settings.Games.Count == 0 && known.Count == 0)
        {
            if (ActiveGame is not null) Stop();
            Status = settings.Games.Count == 0 && known.Count == 0 ? T("No games in the list") : T("Off");
            return;
        }

        var game = Find(ProcessSnapshot.Take(), settings.Games, known, _gameId, AppProcesses.ImagePath);
        if (ActiveGame is not null && game?.Id != _gameId) Stop(); // the game closed (another one may have started)
        if (game is not null && ActiveGame is null) Start(game, settings);
        if (ActiveGame is not null) Boost(settings);
        else Status = T("Waiting for a game");
    }

    // The running game: the one already found if it still runs, otherwise a listed game, otherwise one Windows knows
    // (the exe path must match, a name alone is not enough). Only the user's own session counts.
    internal static GameMatch? Find(IReadOnlyList<ProcessInfo> processes, IReadOnlyList<string> listed, IReadOnlyList<string> known,
        int currentId, Func<int, string?> imagePath)
    {
        var mine = processes.Where(p => p.SessionId == Session && p.Id > 4).ToList();
        var listedNames = listed.GroupBy(g => Path.GetFileNameWithoutExtension(g), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var knownByName = known.GroupBy(k => Path.GetFileNameWithoutExtension(k), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        GameMatch? Match(ProcessInfo p)
        {
            if (listedNames.TryGetValue(p.Name, out var entry)) return new GameMatch(p.Id, p.Name, entry, null, false);
            if (!knownByName.TryGetValue(p.Name, out var paths)) return null;
            var path = imagePath(p.Id);
            return path is not null && paths.Contains(path, StringComparer.OrdinalIgnoreCase) ? new GameMatch(p.Id, p.Name, Path.GetFileName(path), path, true) : null;
        }

        if (currentId != 0 && mine.FirstOrDefault(p => p.Id == currentId) is { Id: > 0 } current && Match(current) is { } same) return same;
        return mine.Where(p => listedNames.ContainsKey(p.Name)).Select(Match).FirstOrDefault(m => m is not null)
               ?? mine.Where(p => knownByName.ContainsKey(p.Name)).Select(Match).FirstOrDefault(m => m is not null);
    }

    void Start(GameMatch game, AppSettings settings)
    {
        ActiveGame = game.Name;
        ActiveEntry = game.FromWindows ? null : game.Entry;
        ActiveFromWindows = game.FromWindows;
        ActivePath = game.Path ?? AppProcesses.ImagePath(game.Id);
        _gameId = game.Id;
        var notes = new List<string>();
        _previousPlan = Power.Active();
        var target = TargetPlan(settings);
        if (target is Guid plan && plan != _previousPlan)
        {
            try { Power.SetActive(plan); notes.Add(F("power plan: {0}", Power.FriendlyName(plan))); }
            catch (Exception ex) { notes.Add(F("could not change the power plan: {0}", ex.Message)); }
        }
        if (settings.RaisePriority)
        {
            try
            {
                using var process = Process.GetProcessById(game.Id);
                process.PriorityClass = ProcessPriorityClass.High;
                notes.Add(T("priority: high"));
            }
            catch (Exception) { notes.Add(T("priority could not be changed (anti-cheat protection likely)")); }
        }
        _wifiTried = false;
        _baseStatus = (game.FromWindows ? F("{0} is running (Windows recognizes it as a game)", ActiveGame) : F("{0} is running", ActiveGame))
                      + (notes.Count > 0 ? " · " + string.Join(", ", notes) : "");
        Status = _baseStatus;
    }

    // Background apps and the RAM cleaner follow the settings while the game runs; new background apps are limited too.
    void Boost(AppSettings settings)
    {
        var notes = new List<string>();
        if (settings.LimitBackground)
        {
            var processes = ProcessSnapshot.Take();
            var skipNames = settings.Games.Select(g => Path.GetFileNameWithoutExtension(g) ?? "").Concat(settings.BackgroundExempt).Append(ActiveGame ?? "")
                .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skipIds = processes.Where(p => p.ParentId == _gameId).Select(p => p.Id).Append(_gameId).ToHashSet(); // the game and what it started
            int limited = _limiter.Limit(processes.Where(p => BackgroundLimiter.IsCandidate(p, Session, Environment.ProcessId, skipNames, skipIds)).ToList(),
                settings.BackgroundOffGameCores ? CpuTopology.Current?.BackgroundSets : null);
            if (limited > 0) notes.Add(F("{0} background app on low CPU|{0} background apps on low CPU", limited));
        }
        else if (_limiter.Count > 0) _limiter.RestoreAll();

        if (settings.CleanStandby && StandbyCleaner.Ready(settings))
        {
            Cleaner ??= CleanerSession.Start(settings);
            notes.Add(Cleaner.Started == false ? T("RAM cleaner could not start") : T("RAM cleaning on"));
        }
        else StopCleaner();

        // Tried once per game (on Wi-Fi only); turning the setting off during the game resumes the search.
        if (settings.PauseWifiScan)
        {
            if (!_wifiTried && WifiScan.Paused is null && WifiScan.ActiveAdapter() is { } wifi)
            {
                _wifiTried = true;
                WifiScan.Pause(wifi);
            }
            if (WifiScan.Paused is not null) notes.Add(T("Wi-Fi network search paused"));
            else if (_wifiTried) notes.Add(T("Wi-Fi network search could not be paused"));
        }
        else WifiScan.Resume();

        Status = _baseStatus + (notes.Count > 0 ? " · " + string.Join(", ", notes) : "");
    }

    void StopCleaner()
    {
        Cleaner?.Dispose();
        Cleaner = null;
    }

    public void Stop()
    {
        _limiter.RestoreAll();
        StopCleaner();
        WifiScan.Resume();
        if (_previousPlan is Guid plan && Power.Active() != plan)
        {
            try { Power.SetActive(plan); }
            catch (Exception) { }
        }
        _previousPlan = null;
        ActiveGame = ActiveEntry = ActivePath = null;
        ActiveFromWindows = false;
        _gameId = 0;
        Status = T("Waiting for a game");
    }

    // Recommended plan (High/Ultimate performance) when none was chosen.
    static Guid? TargetPlan(AppSettings settings)
    {
        if (Guid.TryParse(settings.GamePlan, out var chosen)) return chosen;
        var recommended = Catalog.Find("power.plan")?.Options().FirstOrDefault(o => o.Recommended)?.Value;
        return Guid.TryParse(recommended, out var g) ? g : null;
    }
}
