using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed record AppKind(bool Game, string Label)
{
    public override string ToString() => Label;
}

// One entry of the game mode list: a game (starts game mode, never slowed down) or an app kept at full speed
// while a game runs (voice chat, streaming, overlays, anti-cheat). A game whose location is known has a menu with
// its own Windows settings.
public sealed class AppEntryRow(string name, bool game, string? path, Action<AppEntryRow, bool> kindChanged) : ObservableObject
{
    public static readonly AppKind GameKind = new(true, T("Game"));
    public static readonly AppKind KeepKind = new(false, T("Keep at full speed"));
    public static IReadOnlyList<AppKind> Kinds { get; } = [GameKind, KeepKind];

    bool _game = game;
    bool _fullscreenOff = path is not null && !Demo.On && GameCompat.FullscreenOptimizationsOff(path);

    public string Name => name;
    public string? Path => path;
    public bool IsGame => _game;
    public bool HasMenu => _game && path is not null;

    // "Disable fullscreen optimizations" of the exe (its Compatibility tab).
    public bool FullscreenOptimizationsOff
    {
        get => _fullscreenOff;
        set
        {
            if (value == _fullscreenOff || path is null) return;
            try
            {
                if (!Demo.On) GameCompat.SetFullscreenOptimizationsOff(path, value);
                _fullscreenOff = value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            Raise(nameof(FullscreenOptimizationsOff));
        }
    }
    public string Glyph => _game ? "" : "";

    public AppKind Kind
    {
        get => _game ? GameKind : KeepKind;
        set
        {
            if (value is null || value.Game == _game) return;
            _game = value.Game;
            Raise(nameof(Kind));
            Raise(nameof(IsGame));
            Raise(nameof(HasMenu));
            Raise(nameof(Glyph));
            kindChanged(this, _game);
        }
    }
}

// Game mode tab: one list for the games and for the apps that keep their speed while a game runs, plus the games
// Windows itself recognizes.
public sealed partial class MainViewModel
{
    string _newGame = "", _windowsGamesText = "", _windowsGamesList = "";
    string? _selectedApp;

    public ObservableCollection<AppEntryRow> AppEntries { get; } = [];
    public ObservableCollection<string> RunningApps { get; } = [];

    public ICommand AddGameCommand { get; private set; } = null!;
    public ICommand AddKeepCommand { get; private set; } = null!;
    public ICommand BrowseGameCommand { get; private set; } = null!;
    public ICommand RemoveAppEntryCommand { get; private set; } = null!;
    public ICommand RefreshAppsCommand { get; private set; } = null!;

    public string NewGame { get => _newGame; set => Set(ref _newGame, value); }
    public string? SelectedApp { get => _selectedApp; set { if (Set(ref _selectedApp, value) && value is not null) NewGame = value; } }
    public string WindowsGamesText { get => _windowsGamesText; private set => Set(ref _windowsGamesText, value); }
    public string WindowsGamesList { get => _windowsGamesList; private set => Set(ref _windowsGamesList, value); }

    public bool DetectWindowsGames
    {
        get => _settings.DetectWindowsGames;
        set
        {
            if (_settings.DetectWindowsGames == value) return;
            _settings.DetectWindowsGames = value;
            _settings.Save();
            Raise(nameof(DetectWindowsGames));
            RefreshWindowsGames();
            TickGameWatcher();
            SyncGpuPreference();
        }
    }

    void InitGameList()
    {
        RebuildEntries();
        AddGameCommand = new RelayCommand(_ => AddEntry(NewGame, game: true, path: null), _ => !string.IsNullOrWhiteSpace(NewGame));
        AddKeepCommand = new RelayCommand(_ => AddEntry(NewGame, game: false, path: null), _ => !string.IsNullOrWhiteSpace(NewGame));
        BrowseGameCommand = new RelayCommand(_ => BrowseGame());
        RemoveAppEntryCommand = new RelayCommand(p => { if (p is AppEntryRow row) RemoveEntry(row); });
        RefreshAppsCommand = new RelayCommand(async _ => await RefreshRunningAppsAsync());
        RefreshWindowsGames();
    }

    static string NameOf(string entry) => Path.GetFileNameWithoutExtension(entry.Trim());

    // Games first, then the apps kept at full speed, each alphabetically.
    void RebuildEntries()
    {
        AppEntries.Clear();
        foreach (var g in _settings.Games.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase))
            AppEntries.Add(new AppEntryRow(NameOf(g), true, _settings.GamePaths.GetValueOrDefault(g), OnKindChanged));
        foreach (var a in _settings.BackgroundExempt.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) AppEntries.Add(new AppEntryRow(NameOf(a), false, null, OnKindChanged));
    }

    void AddEntry(string text, bool game, string? path)
    {
        var name = NameOf(Path.GetFileName(text.Trim()));
        if (name.Length == 0) return;
        // An entry is either a game or an app kept at full speed, never both.
        _settings.Games.RemoveAll(g => NameOf(g).Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.BackgroundExempt.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (game) _settings.Games.Add(name + ".exe");
        else _settings.BackgroundExempt.Add(name);
        NewGame = "";
        SelectedApp = null;
        SaveEntries();
        if (!game) return;
        path ??= RunningPath(name);
        if (path is not null) RememberGamePath(name + ".exe", path);
    }

    static string? RunningPath(string name)
    {
        var running = Process.GetProcessesByName(name);
        int? id = running.Length > 0 ? running[0].Id : null;
        foreach (var p in running) p.Dispose();
        return id is int pid ? AppProcesses.ImagePath(pid) : null;
    }

    void BrowseGame()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("Choose the game's exe"),
            Filter = T("Programs") + " (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) AddEntry(dialog.FileName, game: true, path: dialog.FileName);
    }

    void RemoveEntry(AppEntryRow row)
    {
        _settings.Games.RemoveAll(g => NameOf(g).Equals(row.Name, StringComparison.OrdinalIgnoreCase));
        _settings.BackgroundExempt.RemoveAll(a => a.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
        SaveEntries();
    }

    // After the drop-down that changed it has finished: the list is rebuilt, which removes that drop-down.
    void OnKindChanged(AppEntryRow row, bool game)
    {
        var path = _settings.GamePaths.GetValueOrDefault(row.Name + ".exe");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => AddEntry(row.Name, game, path));
    }

    void SaveEntries()
    {
        _settings.Save();
        RebuildEntries();
        TickGameWatcher();
        SyncGpuPreference();
        _ = SyncDefenderAsync(silent: true); // a removed game is scanned again
    }

    void RefreshWindowsGames()
    {
        if (!DetectWindowsGames)
        {
            WindowsGamesText = T("Off: only the games in the list start game mode.");
            WindowsGamesList = "";
            return;
        }
        var known = WindowsGames.Known();
        WindowsGamesText = known.Count == 0
            ? T("Windows has not recognized any game yet; it adds a game the first time it runs.")
            : F("Windows recognizes {0} game:|Windows recognizes {0} games:", known.Count);
        WindowsGamesList = string.Join(", ", known.Select(WindowsGames.DisplayName));
    }

    async Task RefreshRunningAppsAsync()
    {
        _appsLoaded = true;
        var names = Demo.On ? ["ArenaLegends.exe", "Browser.exe", "GameLauncher.exe", "VoiceChat.exe"] : await Task.Run(() =>
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) set.Add(p.ProcessName + ".exe"); }
                    catch (InvalidOperationException) { }
                }
            return set.ToList();
        });
        RunningApps.Clear();
        foreach (var n in names) RunningApps.Add(n);
    }
}
