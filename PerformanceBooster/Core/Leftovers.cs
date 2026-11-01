using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

public enum LeftoverKind { Folder, File, Registry }

// A folder, shortcut or registry key left behind by an uninstalled program. Registry paths look like "HKCU\Software\Example".
// ListEntry: the program's entry in the Programs list (the same entry Windows shows).
public sealed record Leftover(LeftoverKind Kind, string Path, long SizeBytes, string Program, bool ListEntry = false)
{
    public bool NeedsAdmin => Kind == LeftoverKind.Registry ? Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) : Leftovers.NeedsAdmin(Path);
}

// Leftovers of an uninstalled program. Only places that exactly match the program name or publisher\name are found;
// folders/keys used by another installed program are never suggested. Deleting: files go to the Recycle Bin, registry keys
// are backed up as .reg first.
public static partial class Leftovers
{
    internal sealed record Places(IReadOnlyList<string> Folders, IReadOnlyList<string> ShortcutFolders, IReadOnlyList<(RegistryHive Hive, string Path)> Registry);

    static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    internal static Places Default => new(
        [
            Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86),
            Folder(Environment.SpecialFolder.CommonApplicationData), Folder(Environment.SpecialFolder.ApplicationData),
            Folder(Environment.SpecialFolder.LocalApplicationData), Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        ],
        [
            Folder(Environment.SpecialFolder.Programs), Folder(Environment.SpecialFolder.CommonPrograms),
            Folder(Environment.SpecialFolder.DesktopDirectory), Folder(Environment.SpecialFolder.CommonDesktopDirectory),
        ],
        [(RegistryHive.CurrentUser, "Software"), (RegistryHive.LocalMachine, "SOFTWARE"), (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node")]);

    // Generic names that are never matched on their own (only accepted as publisher\program).
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Google", "Mozilla", "Apple", "Adobe",
        "Classes", "Policies", "Clients", "RegisteredApplications", "WOW6432Node", "Wow6432Node", "Packages", "Programs", "Temp",
        "Steam", "Valve", "Epic Games", "Riot Games", "Ubisoft", "EA", "Electronic Arts", "Realtek", "Logitech", "Razer", "Corsair",
        "Python", "Java", "Oracle", "Node", "npm", "Git", "Docker", "JetBrains", "Unity", "Mono", "dotnet", ".NET",
        "Performance Booster", AppPaths.Id,
    };

    public static List<Leftover> Scan(InstalledProgram p, IReadOnlyList<InstalledProgram> remaining) => Scan(p, remaining, Default);

    internal static List<Leftover> Scan(InstalledProgram p, IReadOnlyList<InstalledProgram> remaining, Places places)
    {
        var names = Names(p);
        var publishers = Publishers(p);
        var otherNames = remaining.Where(r => r.Key != p.Key).Select(r => Programs.CleanName(r.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherDirs = remaining.Where(r => r.Key != p.Key).SelectMany(r => r.Dirs).ToList();
        var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);

        bool Shared(string dir)
        {
            var d = dir.TrimEnd('\\') + "\\";
            return otherDirs.Any(o => o.StartsWith(d, StringComparison.OrdinalIgnoreCase) || d.StartsWith(o, StringComparison.OrdinalIgnoreCase));
        }

        void AddFolder(string dir)
        {
            if (!Directory.Exists(dir) || InstallDirs.IsGeneric(dir) || Shared(dir) || IsOwn(dir)) return;
            if (places.Folders.Any(f => string.Equals(f.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
            found.TryAdd(dir.TrimEnd('\\'), new Leftover(LeftoverKind.Folder, dir.TrimEnd('\\'), Maintenance.FolderSize(dir, TimeSpan.Zero), p.Name));
        }

        // 1) The program's own folders if they still exist
        foreach (var dir in p.Dirs) AddFolder(dir);

        // 2) Name or publisher\name under Program Files, ProgramData, AppData...
        foreach (var root in places.Folders)
        {
            foreach (var name in names.Where(n => !GenericNames.Contains(n))) AddFolder(Path.Combine(root, name));
            foreach (var publisher in publishers)
                foreach (var name in names)
                    if (!publisher.Equals(name, StringComparison.OrdinalIgnoreCase)) AddFolder(Path.Combine(root, publisher, name));
        }

        // 3) Start menu folder and shortcuts
        foreach (var root in places.ShortcutFolders)
        {
            foreach (var name in names.Where(n => !GenericNames.Contains(n)))
            {
                AddFolder(Path.Combine(root, name));
                var lnk = Path.Combine(root, name + ".lnk");
                if (File.Exists(lnk)) found.TryAdd(lnk, new Leftover(LeftoverKind.File, lnk, new FileInfo(lnk).Length, p.Name));
            }
        }

        // 4) Registry: Software\Name, Software\Publisher\Name
        foreach (var (hive, root) in places.Registry)
        {
            foreach (var name in names.Where(n => !GenericNames.Contains(n) && !otherNames.Contains(n)))
                AddKey(found, hive, root + "\\" + name, p.Name);
            foreach (var publisher in publishers)
                foreach (var name in names.Where(n => !otherNames.Contains(n)))
                    if (!publisher.Equals(name, StringComparison.OrdinalIgnoreCase)) AddKey(found, hive, root + "\\" + publisher + "\\" + name, p.Name);
        }

        // 5) The uninstaller ran but did not remove the entry from the Programs list
        if (p.Source == ProgramSource.Desktop)
        {
            var hive = p.Key.StartsWith("HKLM", StringComparison.Ordinal) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            AddKey(found, hive, p.Key[(p.Key.IndexOf('|') + 1)..], p.Name, listEntry: true);
        }

        return found.Values.OrderByDescending(l => l.ListEntry).ThenBy(l => l.Kind).ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void AddKey(Dictionary<string, Leftover> found, RegistryHive hive, string path, string program, bool listEntry = false)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            if (key is null) return;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }
        var full = (hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + path;
        found.TryAdd(full, new Leftover(LeftoverKind.Registry, full, 0, program, listEntry));
    }

    static bool IsOwn(string dir) =>
        dir.TrimEnd('\\').Equals(AppPaths.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        || (Environment.ProcessPath is { } exe && exe.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    // Names to search for: "7-Zip 24.08 (x64)" -> 7-Zip, 7Zip
    internal static List<string> Names(InstalledProgram p)
    {
        var clean = Programs.CleanName(p.Name);
        var list = new List<string> { clean, clean.Replace(" ", "") };
        if (!p.Name.Any(char.IsDigit)) list.Add(p.Name.Trim());
        return list.Where(n => n.Length >= 3 && n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static List<string> Publishers(InstalledProgram p)
    {
        if (string.IsNullOrWhiteSpace(p.Publisher)) return [];
        var list = new List<string> { p.Publisher.Trim(), Programs.CleanPublisher(p.Publisher) };
        return list.Where(n => n.Length >= 2 && n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static bool NeedsAdmin(string path)
    {
        string[] protectedRoots =
        [
            Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86), Folder(Environment.SpecialFolder.Windows),
            Folder(Environment.SpecialFolder.CommonApplicationData), Folder(Environment.SpecialFolder.CommonPrograms), Folder(Environment.SpecialFolder.CommonDesktopDirectory),
        ];
        return protectedRoots.Any(r => r.Length > 0 && path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    // --- Deleting ---

    unsafe struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public char* From; // double null-terminated list
        public char* To;
        public ushort Flags;
        public int AnyAborted;
        public IntPtr NameMappings;
        public char* Title;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOp op);

    const uint FoDelete = 3;
    // With "want nuke warning", Windows asks before deleting for good an item that cannot go to the Recycle Bin (too big
    // for it, a drive without one) even with "no confirmation"; without it, such an item was deleted silently.
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    // Sends to the Recycle Bin (does not delete permanently).
    internal static unsafe bool Recycle(string path)
    {
        fixed (char* from = path + "\0") // the string's own terminator makes the second null
        {
            var op = new ShFileOp { Func = FoDelete, From = from, Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning };
            return SHFileOperation(ref op) == 0 && op.AnyAborted == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
    }

    public static (List<string> Log, int Failures) Delete(IEnumerable<Leftover> items, string backupFolder)
    {
        var log = new List<string>();
        int failures = 0, index = 0;
        foreach (var item in items)
        {
            index++;
            try
            {
                if (item.Kind == LeftoverKind.Registry)
                {
                    Directory.CreateDirectory(backupFolder);
                    var backup = System.IO.Path.Combine(backupFolder, $"{index:00}-{Safe(item.Program)}.reg");
                    if (!Export(item.Path, backup)) throw new IOException(T("Could not back up; not deleted."));
                    var hive = item.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
                    using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    root.DeleteSubKeyTree(item.Path[5..], throwOnMissingSubKey: false);
                }
                else if (File.Exists(item.Path) || Directory.Exists(item.Path))
                {
                    if (!Recycle(item.Path)) throw new IOException(T("Could not move to the Recycle Bin (it may be in use)."));
                }
                log.Add($"{T("OK   ")} {item.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                failures++;
                log.Add($"{T("ERROR")} {item.Path}: {ex.Message}");
            }
        }
        return (log, failures);
    }

    static string Safe(string name) => string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // .reg backup with reg.exe (64-bit view).
    static bool Export(string key, string file)
    {
        var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "reg.exe"), $"export \"{key}\" \"{file}\" /y /reg:64")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Shell.ConsoleEncoding, StandardErrorEncoding = Shell.ConsoleEncoding,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.WaitForExit(30000);
        return p.HasExited && p.ExitCode == 0 && File.Exists(file);
    }
}
