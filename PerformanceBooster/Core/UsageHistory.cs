using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

// When programs were last used. Two sources are merged:
//  1) Windows' UserAssist record: programs opened from the Start menu, desktop, taskbar and File Explorer (stored with ROT13).
//  2) The app's own observation: the paths of running programs and when they were last seen (usage.json).
// Programs started another way (like a game launcher) are only seen while the app is running.
public static partial class UsageHistory
{
    public sealed record Entry(string Key, DateTime LastRun, int Runs);

    const string UserAssist = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    static string StorePath => Path.Combine(AppPaths.Folder, "usage.json");
    static readonly Lock Gate = new();

    static Dictionary<string, DateTime>? _seen;
    static DateTime _savedAt;

    // --- UserAssist ---

    public static List<Entry> ReadUserAssist()
    {
        var list = new List<Entry>();
        foreach (var guid in Reg.SubKeyNames(RegistryHive.CurrentUser, UserAssist))
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var count = root.OpenSubKey($@"{UserAssist}\{guid}\Count");
            if (count is null) continue;
            foreach (var name in count.GetValueNames())
            {
                if (count.GetValue(name) is not byte[] { Length: >= 68 } data) continue;
                long time = BitConverter.ToInt64(data, 60);
                if (time <= 0) continue;
                var key = ExpandKnownFolder(Rot13(name));
                if (key.StartsWith("UEME_", StringComparison.Ordinal)) continue;
                DateTime last;
                try { last = DateTime.FromFileTimeUtc(time).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { continue; }
                list.Add(new Entry(key, last, BitConverter.ToInt32(data, 4)));
            }
        }
        return list;
    }

    internal static string Rot13(string s)
    {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            chars[i] = c is >= 'a' and <= 'z' ? (char)('a' + (c - 'a' + 13) % 26)
                : c is >= 'A' and <= 'Z' ? (char)('A' + (c - 'A' + 13) % 26)
                : c;
        }
        return new string(chars);
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    // "{6D809377-6AF0-444B-8957-A3773F02200E}\App\app.exe" -> "C:\Program Files\App\app.exe"
    internal static string ExpandKnownFolder(string path)
    {
        if (path.Length < 39 || path[0] != '{' || path[37] != '}' || !Guid.TryParse(path[..38], out var id)) return path;
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) != 0) return path;
        try { return Marshal.PtrToStringUni(ptr) + path[38..]; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    // --- The app's own observation ---

    public static IReadOnlyDictionary<string, DateTime> Seen()
    {
        lock (Gate) return new Dictionary<string, DateTime>(Load(), StringComparer.OrdinalIgnoreCase);
    }

    static Dictionary<string, DateTime> Load() =>
        _seen ??= new Dictionary<string, DateTime>(AppPaths.ReadJson(StorePath, AppJson.Default.LastSeen) ?? [], StringComparer.OrdinalIgnoreCase);

    // Called every few minutes in the background: the paths of running programs in this session are recorded (except the Windows folder).
    public static void RecordRunning()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var now = DateTime.Now;
        var paths = AppProcesses.Snapshot().Select(p => p.Path)
            .Where(p => !p.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (Gate)
        {
            var seen = Load();
            foreach (var path in paths) seen[path] = now;
            if (seen.Count > 3000)
                foreach (var old in seen.OrderBy(kv => kv.Value).Take(seen.Count - 3000).Select(kv => kv.Key).ToList()) seen.Remove(old);
            if (now - _savedAt < TimeSpan.FromMinutes(4)) return;
            _savedAt = now;
            try { AppPaths.WriteJson(StorePath, seen, AppJson.Default.LastSeen); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void ResetForTests() => _seen = null;

    // --- Matching ---

    // A program's last use: an exe in its folders was started, a Start menu shortcut (same name),
    // the app ID of the Store package, or it is running now. null when unknown.
    public static DateTime? LastUsed(InstalledProgram p, IReadOnlyList<Entry> userAssist, IReadOnlyDictionary<string, DateTime> seen, IReadOnlyCollection<string> running)
    {
        DateTime? best = null;
        void Take(DateTime t) { if (best is null || t > best) best = t; }

        bool InDirs(string path) => p.Dirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase));
        var clean = Programs.CleanName(p.Name);

        foreach (var path in running)
            if (InDirs(path)) return DateTime.Now;

        foreach (var e in userAssist)
        {
            if (p.PackageFamily is { } family)
            {
                if (e.Key.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase) || e.Key.Contains(@"\WindowsApps\" + family.Split('_')[0] + "_", StringComparison.OrdinalIgnoreCase))
                    Take(e.LastRun);
                continue;
            }
            if (e.Key.Length > 3 && e.Key[1] == ':')
            {
                if (InDirs(e.Key)) Take(e.LastRun);
                else if (e.Key.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(e.Key).Equals(clean, StringComparison.OrdinalIgnoreCase))
                    Take(e.LastRun);
            }
            else if (AumidMatches(e.Key, p.Name)) Take(e.LastRun);
        }

        foreach (var (path, time) in seen)
            if (InDirs(path)) Take(time);
        return best;
    }

    static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "squirrel", "client", "app", "apps", "desktop", "microsoft", "windows", "launcher", "inc", "corp", "stable", "main", "beta", "setup",
    };

    // Desktop app ID ("Valve.Steam.Client", "com.squirrel.Discord.Discord", "Chrome"): matches when a meaningful part of the ID
    // equals a word in the program name.
    internal static bool AumidMatches(string aumid, string programName)
    {
        if (aumid.Contains('\\') || aumid.Contains('!')) return false;
        var words = Programs.CleanName(programName).Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && !GenericTokens.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return aumid.Split('.', StringSplitOptions.RemoveEmptyEntries).Any(t => t.Length >= 4 && !GenericTokens.Contains(t) && words.Contains(t));
    }
}
