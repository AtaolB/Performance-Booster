using System.Management;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

// Note: changed in the BIOS or with the hardware itself (memory profile, Resizable BAR, modules, slots); shown as information, not as an issue.
public sealed record InfoItem(string Group, string Label, string Value, CheckState State = CheckState.Ok, string? Hint = null, string? Uri = null, bool Note = false);

// Hardware and system state, with hints for problems that affect game performance.
public static class HardwareInfo
{
    public static string GroupWindows => T("Windows");
    public static string GroupCpu => T("Processor");
    public static string GroupMemory => T("Memory");
    public static string GroupGpu => T("Graphics card");
    public static string GroupDisk => T("Disk");
    public static string GroupPower => T("Power and security");
    public static string GroupGames => T("Games");
    public static string LabelDisplay => T("Display");
    public static string LabelShader => T("Shader cache");

    // gamePaths: the exe files of the known games, to check the drives they are on.
    public static List<InfoItem> Collect() => Collect([]);

    public static List<InfoItem> Collect(IReadOnlyCollection<string> gamePaths)
    {
        var items = new List<InfoItem>();
        Safe(items, Windows);
        Safe(items, Processor);
        Safe(items, Memory);
        Safe(items, Graphics);
        Safe(items, Disks);
        Safe(items, i => Games(i, gamePaths));
        Safe(items, PowerAndSecurity);
        return items;
    }

    static void Safe(List<InfoItem> items, Action<List<InfoItem>> collect)
    {
        try { collect(items); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            items.Add(new(T("System"), T("Could not read"), ex.Message, CheckState.NotApplicable));
        }
    }

    static void Windows(List<InfoItem> items)
    {
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var build = Reg.Get(RegistryHive.LocalMachine, cv, "CurrentBuild") as string ?? "";
        var ubr = Reg.Format(Reg.Get(RegistryHive.LocalMachine, cv, "UBR"));
        var name = (int.TryParse(build, out var b) && b >= 22000 ? "Windows 11 " : "Windows 10 ") + (Reg.Get(RegistryHive.LocalMachine, cv, "EditionID") as string);
        var version = Reg.Get(RegistryHive.LocalMachine, cv, "DisplayVersion") as string;
        items.Add(new(GroupWindows, T("Version"), F("{0} {1} (build {2}.{3})", name, version, build, ubr)));

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        bool long_ = uptime.TotalDays >= 7;
        items.Add(new(GroupWindows, T("Uptime"), F("{0}d {1}h", (int)uptime.TotalDays, uptime.Hours),
            long_ ? CheckState.Problem : CheckState.Ok,
            long_ ? T("Not restarted for a long time. Restart; with fast startup on, \"Shut down\" is not a full shutdown.") : null));

        bool pending = Reg.SubKeyNames(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update").Contains("RebootRequired")
                       || Reg.SubKeyNames(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing").Contains("RebootPending");
        if (pending)
            items.Add(new(GroupWindows, T("Pending restart"), T("yes"), CheckState.Problem,
                T("Updates are waiting for a restart and may use resources in the background. Restart.")));
    }

    static void Processor(List<InfoItem> items)
    {
        int cores = 0;
        using (var search = new ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
            foreach (ManagementObject o in search.Get()) using (o) cores += Convert.ToInt32(o["NumberOfCores"]);
        items.Add(new(GroupCpu, T("Model"), Cpu.Name()));
        var detail = F("{0} / {1}", cores, Environment.ProcessorCount)
                     + (Cpu.IsHybrid ? " · " + T("hybrid (P + E cores)") : "")
                     + (Cpu.BaseMhz() > 0 ? " · " + F("base {0} GHz", (Cpu.BaseMhz() / 1000.0).ToString("0.0#", L.Culture)) : "");
        items.Add(new(GroupCpu, T("Cores / threads"), detail));
    }

    static void Memory(List<InfoItem> items)
    {
        var modules = new List<(ulong Size, uint Speed, uint Type)>();
        using (var search = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
            foreach (ManagementObject o in search.Get())
                using (o)
                {
                    uint clock = Convert.ToUInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0u);
                    modules.Add((Convert.ToUInt64(o["Capacity"]), clock, Convert.ToUInt32(o["SMBIOSMemoryType"] ?? 0u)));
                }
        if (modules.Count == 0) return;

        ulong total = modules.Aggregate(0UL, (s, m) => s + m.Size);
        uint type = modules[0].Type, speed = modules.Min(m => m.Speed);
        string kind = type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => "" };
        items.Add(new(GroupMemory, T("Total"), F("{0} GB {1} · {2} MT/s · {3} module|{0} GB {1} · {2} MT/s · {3} modules", (total / 1073741824.0).ToString("0", L.Culture), kind, speed, modules.Count)));

        if (modules.Count == 1 && type is 26 or 34 or 24)
            items.Add(new(GroupMemory, T("Channel"), T("most likely single channel"), CheckState.Info,
                T("Only one memory module is installed. Adding a second identical module (dual channel) noticeably raises FPS in games."), Note: true));

        bool jedec = (type == 26 && speed <= 2666) || (type == 34 && speed <= 4800);
        if (IsDesktop() && jedec)
            items.Add(new(GroupMemory, T("Speed profile"), F("{0} MT/s (standard speed)", speed), CheckState.Info,
                T("On this desktop the memory runs at standard speed. Turn on the XMP (Intel) or EXPO (AMD) profile in the BIOS."), Note: true));
    }

    static void Graphics(List<InfoItem> items)
    {
        using (var search = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
            foreach (ManagementObject o in search.Get())
                using (o)
                {
                    var name = o["Name"]?.ToString() ?? "GPU";
                    if (name.Contains("Remote", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                    if (L.HasWindowsName(name, "Basic Display"))
                    {
                        items.Add(new(GroupGpu, name, T("no driver"), CheckState.Problem, T("No graphics driver is installed. Install it from the manufacturer's website.")));
                        continue;
                    }
                    DateTime? date = o["DriverDate"] is string d && d.Length >= 8 ? ManagementDateTimeConverter.ToDateTime(d) : null;
                    int age = date is DateTime dt ? (int)(DateTime.Now - dt).TotalDays : 0;
                    bool old = age > 180 && !name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
                    items.Add(new(GroupGpu, name, F("driver {0}", o["DriverVersion"]) + (date is DateTime x ? " · " + x.ToString("d", L.Culture) : ""),
                        old ? CheckState.Problem : CheckState.Ok,
                        old ? F("The driver is {0} days old. Update it for new games: NVIDIA App, AMD Adrenalin or Intel Arc Control.", age) : null));
                }

        // The PCIe lanes of each graphics card (the generation drops while idle, so only the lane count is judged).
        var cards = Devices.Snapshot().Where(d => d.IsPci && !d.Disabled && d.ClassCode is "0300" or "0302" or "0380")
            .Select(d => (Device: d, Link: Devices.Link(d))).Where(c => c.Link is not null).ToList();
        foreach (var (device, link) in cards)
        {
            items.Add(new(GroupGpu, cards.Count > 1 ? F("PCIe link ({0})", device.Name) : T("PCIe link"),
                F("x{0} · up to PCIe {1}.0", link!.Width, link.MaxSpeed) + (link.FewerLanes ? " " + F("(the card supports x{0})", link.MaxWidth) : ""),
                link.FewerLanes ? CheckState.Info : CheckState.Ok,
                link.FewerLanes ? T("The card runs with fewer PCIe lanes than it supports. Put it in the top x16 slot; on some boards an M.2 drive or a second card shares its lanes.") : null, Note: link.FewerLanes));
        }

        if (ResizableBar() is { } rebar)
            items.Add(new(GroupGpu, T("Resizable BAR"), rebar ? T("on") : T("off"), rebar ? CheckState.Ok : CheckState.Info,
                rebar ? null : T("The processor can reach only 256 MB of the video memory at once. Turn on Above 4G Decoding and Re-Size BAR Support in the BIOS (CSM off); some games gain a few percent."), Note: !rebar));

        var (cur, max, w, h) = SystemInfo.RefreshRate();
        if (cur > 0)
        {
            var exact = SystemInfo.ExactRefreshRate(cur);
            bool lower = SystemInfo.BelowMaxRefresh(cur, exact, max);
            items.Add(new(GroupGpu, LabelDisplay, $"{w}x{h} · {(exact ?? cur).ToString("0.##", L.Culture)} Hz" + (lower ? " " + F("(up to {0} Hz)", max) : ""),
                lower ? CheckState.Problem : CheckState.Ok,
                lower ? T("The display is not running at its highest supported refresh rate. Pick the highest value in advanced display settings.") : null,
                lower ? "ms-settings:display-advanced" : null));
        }

        long shader = Maintenance.ShaderCacheSize();
        if (shader > 0)
            items.Add(new(GroupGpu, LabelShader, Maintenance.Size(shader), CheckState.Info,
                T("When the cache hits the driver's limit, old shaders are deleted and games stutter while recompiling. On NVIDIA: Settings tab > NVIDIA > Shader cache size: Unlimited.")));
    }

    static void Disks(List<InfoItem> items)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        string? systemDisk = null;
        using (var parts = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = 'C'")))
            foreach (ManagementObject o in parts.Get()) using (o) systemDisk = o["DiskNumber"]?.ToString();

        using (var disks = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk")))
            foreach (ManagementObject o in disks.Get())
                using (o)
                {
                    var media = Convert.ToUInt16(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => T("disk") };
                    var bus = Convert.ToUInt16(o["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => "" };
                    bool isSystem = o["DeviceId"]?.ToString() == systemDisk;
                    int health = Convert.ToUInt16(o["HealthStatus"] ?? 0);
                    var size = Convert.ToUInt64(o["Size"] ?? 0UL) / 1e9;
                    var state = health != 0 || (isSystem && media == "HDD") ? CheckState.Problem : CheckState.Ok;
                    string? hint = health != 0 ? T("The disk reports a health warning. Back up your data.")
                        : isSystem && media == "HDD" ? T("Windows is on an HDD. Moving to an SSD makes the biggest difference in boot, loading and stutter.") : null;
                    items.Add(new(GroupDisk, o["FriendlyName"]?.ToString() ?? T("Disk"),
                        $"{media} {bus} · {size.ToString("0", L.Culture)} GB" + (isSystem ? " · " + T("Windows is on this disk") : "") + (health != 0 ? " · " + T("health warning") : ""),
                        state, hint));
                }

        var c = new DriveInfo("C");
        double free = c.AvailableFreeSpace * 100.0 / c.TotalSize;
        items.Add(new(GroupDisk, T("C: free space"), $"{(c.AvailableFreeSpace / 1073741824.0).ToString("0.0", L.Culture)} GB ({Pct(free)})",
            free < 15 ? CheckState.Problem : CheckState.Ok,
            free < 15 ? T("The system drive has less than 15% free space. Clean temporary files from Tools or move games to another drive.") : null,
            free < 15 ? "ms-settings:storagesense" : null));
    }

    // NVIDIA cards: BAR1 larger than the classic 256 MB window means Resizable BAR is on. Unknown (null) for other cards.
    static bool? ResizableBar()
    {
        if (GpuMonitor.FindNvidiaSmi() is not { } smi) return null;
        try
        {
            var (code, output) = Shell.Output(smi, "-q -d MEMORY");
            return code == 0 ? ParseBar1Mb(output) is { } mb ? mb > 256 : null : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // The "Total" line of the "BAR1 Memory Usage" section of nvidia-smi -q (MiB).
    internal static double? ParseBar1Mb(string output)
    {
        var lines = output.Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("BAR1 Memory Usage", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var total = lines.Skip(start + 1).Take(3).FirstOrDefault(l => l.TrimStart().StartsWith("Total", StringComparison.OrdinalIgnoreCase));
        var number = total?.Split(':').ElementAtOrDefault(1)?.Trim().Split(' ')[0];
        return double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : null;
    }

    // The drives the games are on: an HDD or a nearly full drive makes loading slow and streaming stutter.
    static void Games(List<InfoItem> items, IReadOnlyCollection<string> gamePaths)
    {
        var byDrive = gamePaths.Where(p => Path.IsPathRooted(p) && File.Exists(p))
            .GroupBy(p => Path.GetPathRoot(p)!.ToUpperInvariant())
            .Where(g => g.Key.Length >= 2 && char.IsLetter(g.Key[0]));
        foreach (var drive in byDrive.OrderBy(g => g.Key))
        {
            var info = new DriveInfo(drive.Key);
            if (!info.IsReady) continue;
            double free = info.AvailableFreeSpace * 100.0 / info.TotalSize;
            var (media, bus) = DriveMedia(drive.Key[0]);
            bool hdd = media == "HDD", full = free < 10;
            var names = drive.Select(p => Path.GetFileNameWithoutExtension(p)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            items.Add(new(GroupGames, F("Games on {0}", drive.Key.TrimEnd('\\')),
                string.Join(" · ", new[] { $"{media} {bus}".Trim(), F("{0} GB free ({1})", (info.AvailableFreeSpace / 1073741824.0).ToString("0", L.Culture), Pct(free)),
                    string.Join(", ", names.Take(4)) + (names.Count > 4 ? "…" : "") }.Where(s => s.Length > 0)),
                hdd || full ? CheckState.Problem : CheckState.Ok,
                hdd ? T("These games are on an HDD: they load slowly and stutter while new areas load. Moving them to an SSD makes the biggest difference.")
                    : full ? T("Less than 10% of this drive is free; the game slows down while it writes caches and updates. Free some space or move games to another drive.") : null));
        }
    }

    // Media and bus of the physical disk a drive letter is on ("SSD", "NVMe"), empty when unknown.
    static (string Media, string Bus) DriveMedia(char letter)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        string? disk = null;
        using (var parts = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'")))
            foreach (ManagementObject o in parts.Get()) using (o) disk = o["DiskNumber"]?.ToString();
        if (disk is null) return ("", "");
        using var disks = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"));
        foreach (ManagementObject o in disks.Get())
            using (o)
                return (MediaName(o["MediaType"]), BusName(o["BusType"]));
        return ("", "");
    }

    static string MediaName(object? type) => Convert.ToUInt16(type ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => "" };

    static string BusName(object? type) => Convert.ToUInt16(type ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", _ => "" };

    static void PowerAndSecurity(List<InfoItem> items)
    {
        var ac = SystemInfo.OnAcPower();
        if (ac is not null)
            items.Add(new(GroupPower, T("Power source"), ac == true ? T("plugged in") : T("on battery"),
                ac == true ? CheckState.Ok : CheckState.Problem, ac == true ? null : T("On battery, GPU and CPU power drop sharply. Plug in the charger while gaming.")));

        try
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard"),
                new ObjectQuery("SELECT SecurityServicesRunning FROM Win32_DeviceGuard"));
            foreach (ManagementObject o in search.Get())
                using (o)
                {
                    bool hvci = o["SecurityServicesRunning"] is uint[] running && running.Contains(2u);
                    items.Add(new(GroupPower, T("Memory integrity (HVCI)"), hvci ? T("on") : T("off"), CheckState.Info,
                        T("Turning it off can gain a few % FPS in CPU-heavy games, but removes protection against kernel attacks. Your call."),
                        "windowsdefender://coreisolation"));
                }
        }
        catch (ManagementException) { }
    }

    static bool IsDesktop()
    {
        using var search = new ManagementObjectSearcher("SELECT PCSystemType FROM Win32_ComputerSystem");
        foreach (ManagementObject o in search.Get())
            using (o) return Convert.ToUInt16(o["PCSystemType"] ?? 0) is 1 or 3;
        return false;
    }
}
