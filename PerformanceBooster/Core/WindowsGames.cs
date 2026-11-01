using Microsoft.Win32;

namespace PerformanceBooster.Core;

// Games Windows itself recognizes: Game Bar and Game Mode keep a "known games" list under
// HKCU\System\GameConfigStore\Children, one key per game with its exe path (MatchedExeFullPath). Windows adds a game there
// the first time it sees it running (from its own list of PC games, or when the user marks it in Game Bar).
// The official game list API (Windows.Gaming.Preview.GamesEnumeration) is open only to apps Microsoft provisions.
public static class WindowsGames
{
    const string Children = @"System\GameConfigStore\Children";

    static (DateTime At, IReadOnlyList<string> Paths)? _cache;

    // Programs Game Bar sometimes takes for games: browsers, launchers, chat and recording apps, the app itself.
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore", "explorer",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EADesktop", "Origin", "Battle.net", "RiotClientServices", "RiotClientUx",
        "UbisoftConnect", "upc", "GalaxyClient", "XboxPcApp", "GameBar", "Discord", "obs64", "obs32", "Spotify", "Teams", "ms-teams",
        "PerformanceBooster", "PerformanceBooster.Tests",
    };

    // Tests and the demo use their own list instead of this computer's.
    internal static Func<IReadOnlyList<string>>? Override { get; set; }

    // Full paths of the games Windows knows, read again at most every 5 minutes.
    public static IReadOnlyList<string> Known()
    {
        if (Override is { } fake) return fake();
        if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Paths;
        var paths = Reg.SubKeyNames(RegistryHive.CurrentUser, Children)
            .Select(key => Reg.Get(RegistryHive.CurrentUser, $@"{Children}\{key}", "MatchedExeFullPath") as string)
            .Where(IsGame)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        _cache = (DateTime.UtcNow, paths);
        return paths;
    }

    internal static bool IsGame(string? path)
    {
        if (path is null || string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (NotGames.Contains(Path.GetFileNameWithoutExtension(path))) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length == 0 || !path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // A game's name for the user: the folder it is installed in when the exe name says little ("Counter-Strike 2" for cs2.exe).
    public static string DisplayName(string path)
    {
        var exe = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        // Engines put the exe deep in the tree (Binaries\Win64, bin\win64): use the first meaningful folder above.
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && IsBuildFolder(Path.GetFileName(dir)))
        {
            dir = Path.GetDirectoryName(dir);
            folder = Path.GetFileName(dir ?? "");
        }
        return folder.Length > 0 && !folder.Equals(exe, StringComparison.OrdinalIgnoreCase) ? $"{folder} ({exe})" : exe;
    }

    static bool IsBuildFolder(string name) =>
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("binaries", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("win64", StringComparison.OrdinalIgnoreCase) || name.StartsWith("win32", StringComparison.OrdinalIgnoreCase)
        || name.Equals("x64", StringComparison.OrdinalIgnoreCase) || name.Equals("game", StringComparison.OrdinalIgnoreCase)
        || name.Equals("retail", StringComparison.OrdinalIgnoreCase) || name.Equals("shipping", StringComparison.OrdinalIgnoreCase);
}
