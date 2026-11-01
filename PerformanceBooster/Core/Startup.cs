using Microsoft.Win32;

namespace PerformanceBooster.Core;

public enum StartupLocation { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed record StartupItem(StartupLocation Location, string Name, string Command, bool Enabled)
{
    public bool NeedsAdmin => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.CommonFolder;

    public string Scope => NeedsAdmin ? T("All users") : T("This user");
}

// Same method as the Startup tab of Task Manager: a 12-byte state is written to the StartupApproved key.
public static class StartupManager
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        AddRun(items, RegistryHive.CurrentUser, RunPath, StartupLocation.UserRun);
        AddRun(items, RegistryHive.LocalMachine, RunPath, StartupLocation.MachineRun);
        AddRun(items, RegistryHive.LocalMachine, Run32Path, StartupLocation.MachineRun32);
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserFolder);
        AddFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonFolder);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupLocation location, string name, bool enabled)
    {
        var (hive, path) = ApprovedKey(location);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        Reg.Set(hive, path, name, data, RegistryValueKind.Binary);
    }

    static void AddRun(List<StartupItem> items, RegistryHive hive, string path, StartupLocation location)
    {
        foreach (var name in Reg.ValueNames(hive, path))
        {
            if (string.IsNullOrEmpty(name)) continue;
            var command = Reg.Get(hive, path, name)?.ToString() ?? "";
            items.Add(new StartupItem(location, name, command, IsEnabled(location, name)));
        }
    }

    static void AddFolder(List<StartupItem> items, string folder, StartupLocation location)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new StartupItem(location, name, file, IsEnabled(location, name)));
        }
    }

    // Enabled when there is no value or the first byte is even (02, 06); disabled when it is odd (03, 07).
    static bool IsEnabled(StartupLocation location, string name)
    {
        var (hive, path) = ApprovedKey(location);
        return Reg.Get(hive, path, name) is not byte[] { Length: > 0 } data || data[0] % 2 == 0;
    }

    internal static (RegistryHive Hive, string Path) ApprovedKey(StartupLocation location) => location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, Approved + @"\Run"),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, Approved + @"\Run"),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, Approved + @"\Run32"),
        StartupLocation.UserFolder => (RegistryHive.CurrentUser, Approved + @"\StartupFolder"),
        _ => (RegistryHive.LocalMachine, Approved + @"\StartupFolder"),
    };
}
