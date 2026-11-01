using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

public sealed record RunningProcess(int Id, string Name, string Path);

// Finds the running processes of a package that is about to be updated and closes them if allowed.
// The install folder comes from the Programs and Features entry (InstallLocation / DisplayIcon); for MSIX packages from the WindowsApps folder.
public static partial class AppProcesses
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    internal static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    // winget itself and processes that must keep running during the update.
    static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "winget", "WindowsPackageManagerServer", "AppInstaller", AppPaths.Id, "explorer",
    };

    public static Dictionary<string, List<RunningProcess>> FindRunning(IEnumerable<WingetPackage> packages)
    {
        var installs = ReadInstalls();
        var processes = Snapshot();
        var result = new Dictionary<string, List<RunningProcess>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (package.IsWinget) continue;
            var name = Normalize(package.Name);
            var exact = installs.Where(i => i.Name == name).ToList();
            var matched = exact.Count > 0 ? exact : installs.Where(i => name.Length >= 5 && i.Name.Length >= 5 && (i.Name.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(i.Name, StringComparison.Ordinal))).ToList();
            var dirs = matched.SelectMany(i => i.Dirs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hits = processes.Where(p => dirs.Any(d => p.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) || IsMsixOf(p.Path, package)).ToList();
            if (hits.Count > 0) result[package.Id] = hits;
        }
        return result;
    }

    // Sends "close" to windowed processes (a save prompt may appear) and waits. Returns the ones that did not close.
    public static async Task<List<RunningProcess>> CloseGracefullyAsync(IReadOnlyList<RunningProcess> targets, TimeSpan timeout)
    {
        foreach (var t in targets)
        {
            try
            {
                using var p = Process.GetProcessById(t.Id);
                if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        var clock = Stopwatch.StartNew();
        List<RunningProcess> alive;
        do
        {
            await Task.Delay(500);
            alive = targets.Where(IsAlive).ToList();
        }
        while (alive.Count > 0 && clock.Elapsed < timeout);
        return alive;
    }

    public static void Kill(IEnumerable<RunningProcess> targets)
    {
        foreach (var t in targets)
        {
            try
            {
                using var p = Process.GetProcessById(t.Id);
                p.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    static bool IsAlive(RunningProcess t)
    {
        try
        {
            using var p = Process.GetProcessById(t.Id);
            return !p.HasExited && string.Equals(p.ProcessName, t.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static List<RunningProcess> Snapshot()
    {
        var list = new List<RunningProcess>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (p.Id == self || p.Id <= 4 || NeverClose.Contains(p.ProcessName)) continue;
                if (ImagePath(p.Id) is string path) list.Add(new RunningProcess(p.Id, p.ProcessName, path));
            }
        return list;
    }

    internal static unsafe string? ImagePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            const int Capacity = 1024;
            char* buffer = stackalloc char[Capacity];
            uint size = Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ...\WindowsApps\Microsoft.WindowsTerminal_1.24.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe
    // The package name may match the winget ID (Microsoft.WindowsTerminal), the last part of the ID (Contoso.Notes -> Notes)
    // or the app name without spaces.
    internal static bool IsMsixOf(string path, WingetPackage package)
    {
        const string marker = @"\WindowsApps\";
        int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var folder = path[(i + marker.Length)..].Split('\\')[0];
        int underscore = folder.IndexOf('_');
        if (underscore <= 0) return false;
        var family = folder[..underscore];
        var lastSegment = package.Id.Contains('.') ? package.Id[(package.Id.LastIndexOf('.') + 1)..] : package.Id;
        var compactName = package.Name.Replace(" ", "");
        return new[] { package.Id, lastSegment, compactName }
            .Any(c => c.Length >= 4 && family.Equals(c, StringComparison.OrdinalIgnoreCase));
    }

    sealed record Install(string Name, List<string> Dirs);

    static List<Install> ReadInstalls()
    {
        var list = new List<Install>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var root in UninstallKeys)
                foreach (var sub in Reg.SubKeyNames(hive, root))
                {
                    var path = root + "\\" + sub;
                    if (Reg.Get(hive, path, "DisplayName") is not string name || name.Length == 0) continue;
                    var dirs = new List<string>();
                    InstallDirs.Add(dirs, Reg.Get(hive, path, "InstallLocation") as string);
                    InstallDirs.Add(dirs, InstallDirs.ExeDir(Reg.Get(hive, path, "DisplayIcon") as string));
                    if (dirs.Count > 0) list.Add(new Install(Normalize(name), dirs));
                }
        return list;
    }

    static string Normalize(string name) => name.Replace("…", "").Trim().ToLowerInvariant();

    // Running processes of a program about to be uninstalled: those whose path is inside one of the program's folders.
    public static List<RunningProcess> FindRunningIn(IEnumerable<string> dirs)
    {
        var list = dirs.Where(d => d.Length > 3).ToList();
        if (list.Count == 0) return [];
        return Snapshot().Where(p => list.Any(d => p.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
