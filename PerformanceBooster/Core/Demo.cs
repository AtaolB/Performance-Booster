namespace PerformanceBooster.Core;

// A made-up computer for screenshots: PERFORMANCEBOOSTER_DEMO=1 together with a separate data folder (PERFORMANCEBOOSTER_DATA).
// Every value on the screen comes from here instead of this computer, and nothing on the computer is read for display or
// changed: no settings, no game mode, no timer, no network tests, no elevated copies.
public static class Demo
{
    public static bool On { get; private set; }

    public const string Gpu = "GeForce RTX 4080";
    public const string Router = "192.168.0.1";
    public const string DnsServer = "1.1.1.1";
    public const int Threads = 16;
    public const double RamTotalGb = 32;

    public static readonly string[] WindowsGamePaths =
    [
        @"C:\Games\Sky Raiders\SkyRaiders.exe",
        @"D:\Games\Velocity Rush\VelocityRush.exe",
        @"D:\Games\Iron Frontier\IronFrontier.exe",
    ];

    public static readonly string[] Games = ["ArenaLegends.exe", "NightCircuit.exe"];

    public static void Start()
    {
        On = true;
        WindowsGames.Override = () => WindowsGamePaths;
        Catalog.Override = Tweaks();
    }

    // The real settings with stand-in values: almost all at their recommended value, a few not, so the screens show both.
    // Settings that depend on this computer's devices are left out; the power plans have fixed names.
    static List<Tweak> Tweaks()
    {
        string[] different = ["power.plan", "gfx.windowed", "net.p2p", "sched.responsiveness"];
        var plans = new TweakOption[]
        {
            new("balanced", "Balanced", "381b4222-f694-41f0-9685-ff5bb260df2e"),
            new("high", "High performance", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"),
            new("ultimate", "Ultimate Performance", "e9a42b02-d5df-448d-aa00-03f14749eb61", recommended: true),
        };
        var list = new List<Tweak>();
        foreach (var t in Catalog.Build())
        {
            if (t.Id.StartsWith("nic.", StringComparison.Ordinal) || t.Id.StartsWith("msi.", StringComparison.Ordinal) || t.Id.StartsWith("dev.", StringComparison.Ordinal)) continue;
            IReadOnlyList<TweakOption> options;
            try { options = t.Id == "power.plan" ? plans : t.Options(); }
            catch (Exception) { continue; }
            var pick = different.Contains(t.Id)
                ? options.FirstOrDefault(o => !o.Recommended) ?? options[0]
                : options.FirstOrDefault(o => o.Recommended) ?? (options.Count > 0 ? options[0] : null);
            if (pick is null) continue;
            var store = new DemoStore(pick.Value);
            list.Add(new Tweak
            {
                Id = t.Id, Category = t.Category, Title = t.Title, Description = t.Description, Impact = t.Impact, Note = t.Note,
                Store = () => store, Options = () => options, RestartsNic = t.RestartsNic, NeedsReboot = t.NeedsReboot,
            });
        }
        return list;
    }

    // Holds its value in memory; "applying" in the demo only changes this.
    sealed class DemoStore(string? value) : ISettingStore
    {
        string? _value = value;
        public bool Available => true;
        public string? Read() => _value;
        public void Write(string? value) => _value = value;
    }

    public static List<WingetPackage> Packages() =>
    [
        new("Game Launcher", "Example.GameLauncher", "2.14.0", "2.15.1", "winget", false),
        new("Voice Chat", "Example.VoiceChat", "1.0.9180", "1.0.9201", "winget", false),
        new("Media Player", "Example.MediaPlayer", "3.0.20", "3.0.21", "winget", false),
    ];

    public static List<InfoItem> Hardware() =>
    [
        new(HardwareInfo.GroupWindows, T("Version"), F("{0} {1} (build {2}.{3})", "Windows 11 Professional", "24H2", "26100", "4061")),
        new(HardwareInfo.GroupWindows, T("Uptime"), F("{0}d {1}h", 0, 3)),
        new(HardwareInfo.GroupCpu, T("Model"), "AMD Ryzen 7 7800X3D 8-Core Processor"),
        new(HardwareInfo.GroupCpu, T("Cores / threads"), F("{0} / {1}", 8, Threads) + " · " + F("base {0} GHz", 4.2.ToString("0.0#", L.Culture))),
        new(HardwareInfo.GroupMemory, T("Total"), F("{0} GB {1} · {2} MT/s · {3} module|{0} GB {1} · {2} MT/s · {3} modules", RamTotalGb, "DDR5", 6000, 2)),
        new(HardwareInfo.GroupGpu, "NVIDIA " + Gpu, F("driver {0}", "32.0.15.6614") + " · " + new DateTime(2026, 9, 18).ToString("d", L.Culture)),
        new(HardwareInfo.GroupGpu, T("PCIe link"), F("x{0} · up to PCIe {1}.0", 16, 4)),
        new(HardwareInfo.GroupGpu, T("Resizable BAR"), T("on")),
        new(HardwareInfo.GroupGpu, HardwareInfo.LabelDisplay, "2560x1440 · 165 Hz"),
        new(HardwareInfo.GroupDisk, "NVMe SSD 2TB", "SSD NVMe · 2000 GB · " + T("Windows is on this disk")),
        new(HardwareInfo.GroupDisk, T("C: free space"), $"{612.4.ToString("0.0", L.Culture)} GB ({Pct(33)})"),
        new(HardwareInfo.GroupGames, F("Games on {0}", "D:"), "SSD NVMe · " + F("{0} GB free ({1})", 1240, Pct(62)) + " · Arena Legends, Iron Frontier, Velocity Rush"),
        new(HardwareInfo.GroupPower, T("Power source"), T("plugged in")),
        new(HardwareInfo.GroupPower, T("Memory integrity (HVCI)"), T("off"), CheckState.Info),
    ];

    // The last game: 42 minutes, the graphics card hot for a while and the video memory full for a moment.
    public static GameSession Session()
    {
        var end = DateTime.Today.AddHours(21).AddMinutes(14);
        return new GameSession
        {
            Game = "ArenaLegends", Start = end.AddMinutes(-42), End = end, Samples = 840, GpuSamples = 840, GpuSlowed = 46, MaxGpuTemp = 88,
            VramFull = 7, MaxVramPercent = 97, MinRamMb = 9800, MinCpuLimit = 100,
        };
    }

    public static SpeedResult Speed()
    {
        var (grade, verdict) = SpeedTest.Grade(9);
        return new SpeedResult(12, 486, 18, 47, 23, grade, verdict);
    }

    public static List<DnsResult> Dns() =>
    [
        new("Cloudflare", "1.1.1.1", 14, 19, 0, 10, 9),
        new("Quad9", "9.9.9.9", 21, 27, 0, 10, 15),
        new("Google", "8.8.8.8", 19, 24, 0, 10, 17),
        new("AdGuard", "94.140.14.14", 26, 33, 0, 10, 22),
        new("OpenDNS", "208.67.222.222", 31, 38, 0, 10, 28),
    ];

    public static LatencyResult Latency()
    {
        var drivers = new List<DriverLatency>
        {
            new("dxgkrnl.sys", "DirectX Graphics Kernel", 8120, 6210, 212, 248, 61.4),
            new("ndis.sys", "Network Driver Interface", 15300, 0, 168, 0, 92.2),
            new("storport.sys", "Storage Port Driver", 6420, 1880, 141, 96, 33.8),
            new("USBXHCI.SYS", "USB XHCI Driver", 4210, 3920, 74, 51, 18.6),
            new("HDAudBus.sys", "High Definition Audio Bus Driver", 2980, 2950, 58, 39, 12.1),
            new("ntoskrnl.exe", "NT Kernel & System", 21500, 0, 46, 0, 40.5),
            new("tcpip.sys", "TCP/IP Driver", 5140, 0, 39, 0, 14.9),
            new("ACPI.sys", "ACPI Driver", 610, 12, 31, 18, 2.4),
        };
        var cpus = Enumerable.Range(0, Threads).Select(i => new CpuLatency(i, i % 2 == 0 ? 6200 - i * 300 : 900 - i * 30, i % 4 == 0 ? 1600 : 220,
            Math.Round(i % 2 == 0 ? 38 - i * 1.7 : 4.2 - i * 0.15, 1))).ToList();
        long dpcs = drivers.Sum(d => d.Dpcs), isrs = drivers.Sum(d => d.Isrs);
        return new LatencyResult(30, dpcs, isrs, 212, "dxgkrnl.sys", 248, "dxgkrnl.sys",
            [(long)(dpcs * 0.71), (long)(dpcs * 0.255), (long)(dpcs * 0.031), (long)(dpcs * 0.0038), (long)(dpcs * 0.0002), 0, 0], drivers, cpus, 0, null);
    }
}
