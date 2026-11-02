using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

public static class AppPaths
{
    public const string Name = "Performance Booster";
    public const string Id = "PerformanceBooster";

    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Tests use their own temporary folder.
    internal static string? FolderOverride { get; set; }

    // Name of one of the app's kernel objects in the signed-in user's session (instance lock, signals). A copy started with
    // a separate data folder (tests) gets its own names, so it never meets the user's running app.
    public static string LocalName(string name) => $@"Local\{Id}.{name}{Suffix}";

    static string Suffix => FolderOverride is { } folder
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder.ToLowerInvariant())))[..12]
        : "";

    public static string Folder => FolderOverride ?? Path.Combine(Local, Id);
    public static string Backups => Path.Combine(Folder, "backups.json");
    public static string Settings => Path.Combine(Folder, "settings.json");
    public static string History => Path.Combine(Folder, "update-history.json");
    public static string Log => Path.Combine(Folder, "last-operation.log");

    // One-time requests for the elevated helper: written just before it starts, deleted as soon as it has finished.
    public static string DefenderRequest => Path.Combine(Folder, "defender-change.json");
    public static string LeftoverRequest => Path.Combine(Folder, "leftovers.json");

    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Requests left behind by a copy that was closed while the helper ran (or by older versions, which kept them).
    internal static void DeleteRequests()
    {
        Delete(DefenderRequest);
        Delete(LeftoverRequest);
    }

    // null when the file is missing or cannot be read.
    internal static T? ReadJson<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written to a temporary file first, then swapped in, so an interrupted write cannot corrupt the file.
    internal static void WriteJson<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp)) JsonSerializer.Serialize(stream, value, type);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public List<string> Games { get; set; } = [];
    public bool GameBoost { get; set; } = true;
    public string? GamePlan { get; set; }
    public bool RaisePriority { get; set; }
    public bool DetectWindowsGames { get; set; } = true; // games Windows recognizes (Game Bar's list) start game mode too
    public int SpeedTestSeconds { get; set; } = 10; // length of the download and of the upload phase
    public bool IncludeUnknown { get; set; } = true;
    public bool IncludeStore { get; set; } = true;
    public bool SilentInstall { get; set; } = true;
    public List<string> SkippedVersions { get; set; } = []; // "Id|Version"
    public bool TrayHintShown { get; set; }
    public bool NetMonitorAllowed { get; set; } // starts automatically once per-app monitoring was approved in the network details
    public bool AutoUpdateApp { get; set; } = true; // new versions of the app install themselves (at start, or in the tray while idle)
    public string? LastRunVersion { get; set; } // the version that ran last; a newer one shows what changed since then

    // The settings came from the file (not the first start).
    [System.Text.Json.Serialization.JsonIgnore] public bool FromFile { get; private set; }

    // A finer Windows timer, held the whole time the app runs (from sign-in when it starts with Windows).
    public bool TimerOn { get; set; } = true;
    public double TimerMs { get; set; } = 0.5;

    // Game mode extras while a game runs: background apps on low CPU, emptying the standby list.
    public bool LimitBackground { get; set; } = true;
    public List<string> BackgroundExempt { get; set; } = [.. DefaultExempt];
    public bool CleanStandby { get; set; }
    public int StandbyAtLeastMb { get; set; } = 1024;
    public int FreeBelowMb { get; set; } = StandbyCleaner.DefaultFreeBelowMb();
    public string? CleanerTaskExe { get; set; } // the exe the scheduled task of the RAM cleaner starts
    public bool KeepMouse { get; set; } // mouse acceleration off and speed 10, set back automatically when another program changes them
    public bool BackgroundOffGameCores { get; set; } = true; // the slowed-down apps run on the cores the game does not need (CpuTopology)
    public bool PauseWifiScan { get; set; } // Windows' Wi-Fi network search is paused while a game runs on Wi-Fi

    // With two graphics cards, games run on the high-performance one; GpuPreferenceSet: the exe files the app set (undone when turned off).
    public bool GamesOnFastGpu { get; set; } = true;
    public List<string> GpuPreferenceSet { get; set; } = [];

    // Listed games excluded from Defender's real-time scanning; the paths are learned when a game runs.
    public bool DefenderExclude { get; set; }
    public Dictionary<string, string> GamePaths { get; set; } = []; // game list entry ("cs2.exe") -> full path
    public List<string> DefenderExcluded { get; set; } = []; // exclusions the app added (removed again when turned off)

    // Sensors tab: seconds between readings while it is open, and the temperature warnings (checked in the background too).
    public double SensorIntervalSeconds { get; set; } = 1;
    public bool TemperatureWarnings { get; set; } // off until turned on on the Sensors tab
    public double LiveIntervalSeconds { get; set; } = 1;
    public List<string> PinnedApps { get; set; } = [];
    public int CpuTempLimit { get; set; } = 95;
    public int GpuTempLimit { get; set; } = 87;

    // Voice chat, streaming, overlays and anti-cheat clients keep their CPU share while a game runs.
    public static readonly string[] DefaultExempt =
    [
        "Discord", "DiscordPTB", "DiscordCanary", "obs64", "obs32", "Streamlabs OBS", "ts3client_win64", "TeamSpeak", "Mumble",
        "NVIDIA Overlay", "GameOverlayUI", "RTSS", "MSIAfterburner", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "vgtray",
    ];

    static AppSettings? _current;

    // The app's single settings object: two copies must not write the same file and erase each other's changes.
    public static AppSettings Current => _current ??= Upgraded(Load());

    internal static void ResetCurrent() => _current = null;

    public static AppSettings Load()
    {
        bool exists = File.Exists(AppPaths.Settings);
        var settings = AppPaths.ReadJson(AppPaths.Settings, AppJson.Default.AppSettings) ?? new AppSettings();
        settings.FromFile = exists;
        return settings;
    }

    static AppSettings Upgraded(AppSettings settings)
    {
        if (settings.FromFile && settings.Upgrade()) settings.Save();
        return settings;
    }

    // Settings saved by an earlier version: an empty list of apps kept at full speed gets the usual apps back, and the RAM
    // cleaner's earlier suggested limit (half of the memory) becomes the new one. Only the main app upgrades (Current).
    internal bool Upgrade()
    {
        bool changed = false;
        if (BackgroundExempt.Count == 0)
        {
            BackgroundExempt = [.. DefaultExempt];
            changed = true;
        }
        if (FreeBelowMb == StandbyCleaner.HalfOfMemoryMb())
        {
            FreeBelowMb = StandbyCleaner.DefaultFreeBelowMb();
            changed = true;
        }
        return changed;
    }

    public void Save()
    {
        try { AppPaths.WriteJson(AppPaths.Settings, this, AppJson.Default.AppSettings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// Starts in the tray at sign-in: through the HKCU Run key, or, when the app runs as administrator, through a scheduled task
// (Windows does not start a program that asks for administrator rights from the Run key).
public static class AutoStart
{
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskName = AppPaths.Id;
    static string? _taskExe;
    static bool _taskRead;

    static bool RunEntry => Reg.Get(RegistryHive.CurrentUser, RunPath, AppPaths.Id) is string;

    // The exe the sign-in task starts (read once, then kept up to date).
    static string? TaskExe
    {
        get
        {
            if (!_taskRead) (_taskExe, _taskRead) = (ScheduledTasks.Command(TaskName), true);
            return _taskExe;
        }
    }

    public static bool Enabled
    {
        get => RunEntry || (Elevated.IsAdmin && TaskExe is not null);
        set
        {
            if (Elevated.IsAdmin)
            {
                Reg.Delete(RegistryHive.CurrentUser, RunPath, AppPaths.Id);
                if (value && ScheduledTasks.Create(TaskName, ScheduledTasks.Xml("Performance Booster: starts in the tray at sign-in.",
                        Environment.ProcessPath!, "--minimized", ScheduledTasks.CurrentUserSid, ScheduledTasks.CurrentUserName)))
                    _taskExe = Environment.ProcessPath;
                else if (!value && TaskExe is not null && ScheduledTasks.Delete(TaskName))
                    _taskExe = null;
                _taskRead = true;
                return;
            }
            if (value) Reg.Set(RegistryHive.CurrentUser, RunPath, AppPaths.Id, $"\"{Environment.ProcessPath}\" --minimized", RegistryValueKind.String);
            else Reg.Delete(RegistryHive.CurrentUser, RunPath, AppPaths.Id);
        }
    }

    // If the exe was moved, the entry is updated to the new location; an elevated app moves a Run entry to the task.
    public static void Refresh()
    {
        if (Elevated.IsAdmin)
        {
            if (RunEntry || (TaskExe is { } exe && !exe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))) Enabled = true;
            return;
        }
        if (Reg.Get(RegistryHive.CurrentUser, RunPath, AppPaths.Id) is string command && !command.Contains(Environment.ProcessPath ?? "", StringComparison.OrdinalIgnoreCase))
            Enabled = true;
    }
}
