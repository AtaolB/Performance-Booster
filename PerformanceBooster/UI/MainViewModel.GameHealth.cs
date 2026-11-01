using System.Collections.ObjectModel;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed record FindingRow(string Title, string Detail, string Hint);

// Game mode tab: what held the last game back (GameSession), the cores of the slowed-down apps, Wi-Fi network search,
// and the graphics card the games run on.
public sealed partial class MainViewModel
{
    GameSession? _session, _lastSession;
    HealthSampler? _healthSampler;
    DateTime _lastHealth;
    string _sessionTitle = "", _sessionSummary = "";

    public ObservableCollection<FindingRow> SessionFindings { get; } = [];
    public string SessionTitle { get => _sessionTitle; private set => Set(ref _sessionTitle, value); }
    public string SessionSummary { get => _sessionSummary; private set => Set(ref _sessionSummary, value); }

    public ICommand OpenGameLocationCommand { get; private set; } = null!;

    // A game ended with findings; App shows a notification.
    public event Action<GameSession, IReadOnlyList<Finding>>? GameSessionEnded;

    public bool ShowCoreSplit { get; } = !Demo.On && CpuTopology.Current is not null;

    public string CoreSplitText => CpuTopology.Current switch
    {
        { Kind: CpuTopology.Kind.Hybrid } l => F("{0} performance and {1} efficiency cores: the slowed-down apps run on the efficiency cores, the game keeps the performance cores.", l.GameCores, l.BackgroundCores),
        { } l => F("Cores with {0} MB and with {1} MB cache: the slowed-down apps run on the cores with less cache, the game keeps the large cache.", l.GameCacheMb, l.BackgroundCacheMb),
        null => "",
    };

    public bool BackgroundOffGameCores
    {
        get => _settings.BackgroundOffGameCores;
        set => SaveGameSetting(value, _settings.BackgroundOffGameCores, v => _settings.BackgroundOffGameCores = v);
    }

    public bool PauseWifiScan
    {
        get => _settings.PauseWifiScan;
        set => SaveGameSetting(value, _settings.PauseWifiScan, v => _settings.PauseWifiScan = v);
    }

    public bool ShowGpuChoice { get; } = Demo.On || GameCompat.HasSeveralGpus();

    public bool GamesOnFastGpu
    {
        get => _settings.GamesOnFastGpu;
        set
        {
            if (_settings.GamesOnFastGpu == value) return;
            _settings.GamesOnFastGpu = value;
            _settings.Save();
            Raise(nameof(GamesOnFastGpu));
            SyncGpuPreference();
        }
    }

    void InitGameHealth()
    {
        OpenGameLocationCommand = new RelayCommand(p => { if (p is AppEntryRow { Path: { } path }) Shell.ShowInFolder(path); });
        _lastSession = Demo.On ? Demo.Session() : GameSession.Load();
        ShowSession(_lastSession, running: false);
        SyncGpuPreference();
        if (Demo.On) TickGameWatcher(); // the background checks do not run in the demo
    }

    // The exe files of the listed games whose location is known, and of the games Windows recognizes.
    List<string> KnownGamePaths() =>
        _settings.Games.Select(g => _settings.GamePaths.GetValueOrDefault(g)).OfType<string>()
            .Concat(_settings.DetectWindowsGames ? WindowsGames.Known() : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Every 3 s with game mode: one sample while a game runs; when it closes, the summary is kept and shown.
    void TickGameHealth()
    {
        if (_watcher.ActiveGame is { } game)
        {
            if (_session is null)
            {
                _session = new GameSession { Game = game };
                _nvidia.Start(); // graphics card values also while the window is hidden
            }
            if (DateTime.UtcNow - _lastHealth < TimeSpan.FromSeconds(GameSession.SampleSeconds - 0.5)) return;
            _lastHealth = DateTime.UtcNow;
            _healthSampler ??= new HealthSampler();
            _session.Add(_healthSampler.Sample(_nvidiaSample));
            ShowSession(_session, running: true);
            return;
        }
        if (_session is not { } ended) return;
        _session = null;
        ended.End = DateTime.Now;
        _healthSampler?.Dispose();
        _healthSampler = null;
        if (!_visible)
        {
            _nvidia.Stop();
            _nvidiaSample = null;
        }
        if (ended.Samples < 2) // a game that ran for a moment only
        {
            ShowSession(_lastSession, running: false);
            return;
        }
        ended.Save();
        _lastSession = ended;
        ShowSession(ended, running: false);
        var findings = ended.Findings();
        if (findings.Count > 0) GameSessionEnded?.Invoke(ended, findings);
    }

    void ShowSession(GameSession? s, bool running)
    {
        SessionFindings.Clear();
        if (s is null)
        {
            SessionTitle = T("No game has run yet");
            SessionSummary = T("After a game, this shows what held it back: heat or power limits, full video memory, low RAM or battery power.");
            return;
        }
        var length = GameSession.Duration((int)s.Length.TotalSeconds);
        SessionTitle = running
            ? F("{0} is running · {1}", s.Game, length)
            : F("{0} · {1} · ended at {2}", s.Game, length, (s.End ?? DateTime.Now).ToString("t", L.Culture));
        var findings = s.Findings();
        foreach (var f in findings) SessionFindings.Add(new FindingRow(f.Title, f.Detail, f.Hint));
        SessionSummary = findings.Count > 0 ? ""
            : running ? T("Nothing has held the game back so far.")
            : T("Nothing held the game back: no heat or power limit, and enough memory.");
        if (s.GpuSamples == 0) SessionSummary = (SessionSummary + " " + T("The graphics card's temperature and video memory are read on NVIDIA cards only.")).Trim();
    }

    // With two graphics cards: the known games get "High performance" (only where Windows decides now); turning it off,
    // or a game that is no longer known, removes what the app set.
    void SyncGpuPreference()
    {
        if (Demo.On || !ShowGpuChoice) return;
        var set = _settings.GpuPreferenceSet;
        var known = _settings.GamesOnFastGpu ? KnownGamePaths() : [];
        int before = set.Count;
        bool changed = false;
        try
        {
            foreach (var exe in set.Where(e => !known.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                if (GameCompat.GpuPreference(exe) == GameCompat.GpuHighPerformance) GameCompat.SetGpuPreference(exe, null);
                set.Remove(exe);
                changed = true;
            }
            foreach (var exe in known.Where(e => !set.Contains(e, StringComparer.OrdinalIgnoreCase)))
            {
                if (GameCompat.GpuPreference(exe) is not (null or GameCompat.GpuDefault)) continue; // the user chose one
                GameCompat.SetGpuPreference(exe, GameCompat.GpuHighPerformance);
                set.Add(exe);
                changed = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Message = F("The graphics card choice could not be saved: {0}", ex.Message);
        }
        if (changed || set.Count != before) _settings.Save();
    }
}
