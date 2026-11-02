using System.Management;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

// Device and service settings: NVIDIA driver options, MSI mode, device power saving, unused devices and services.
public static partial class Catalog
{
    static IEnumerable<Tweak> NvidiaTweaks()
    {
        string nvidia = T("NVIDIA");
        Func<bool> present = () => NvApi.Available;
        yield return new Tweak
        {
            Id = "nv.power", Category = nvidia, Title = T("Power management mode"), Impact = Impact.Medium, Applicable = present,
            Info = T("The same setting as Power management mode in the NVIDIA Control Panel (Manage 3D settings, global). Normal and Adaptive let the card lower its clocks when a game is light, for example in menus or at an FPS limit; raising them again takes a moment, which shows up as uneven frame times. Prefer maximum performance holds the clocks while a game runs; it uses more power."),
            Description = T("Prefer maximum performance keeps the clocks up while a game runs, so frame times stay even; the card still slows down on the desktop."),
            Store = () => new NvidiaStore(NvApi.PowerManagementMode),
            Options = Fixed(Rec("max", T("Prefer maximum performance"), "1"), Opt("normal", T("Normal (driver default)"), null, "5"), Opt("adaptive", T("Adaptive"), "0")),
        };
        yield return new Tweak
        {
            Id = "nv.latency", Category = nvidia, Title = T("Low latency mode"), Impact = Impact.Medium, Applicable = present,
            Info = T("Normally the processor prepares a few frames ahead, so the graphics card never has to wait; every queued frame adds input lag. On allows only one frame in the queue. It helps most when the card runs near 100%; when the processor is the limit, it changes little. In games with NVIDIA Reflex, turn on Reflex in the game instead."),
            Description = T("On keeps the processor at most one frame ahead of the card: less input lag when the card is the limit. Games with Reflex use Reflex instead."),
            Store = () => new NvidiaStore(NvApi.PreRenderedFrames),
            Options = Fixed(Rec("on", T("On (1 frame)"), "1"), Opt("off", T("Off (the game decides)"), null, "0")),
        };
        yield return new Tweak
        {
            Id = "nv.shader", Category = nvidia, Title = T("Shader cache size"), Impact = Impact.Low, Applicable = present,
            Info = T("Shaders are the small programs that draw a game's effects. The driver compiles them for your card the first time they are needed and keeps them in a cache on the disk. When the cache is full the oldest are deleted, and the game has to compile them again, which shows up as stutter. Unlimited uses more disk space, usually a few GB."),
            Description = T("When the cache is full, old shaders are deleted and games stutter while compiling them again."),
            Store = () => new NvidiaStore(NvApi.ShaderCacheSize),
            Options = Fixed(Rec("unlimited", T("Unlimited"), "4294967295"), Opt("default", T("Driver default"), null), Opt("10", "10 GB", "10240"), Opt("100", "100 GB", "102400")),
        };
        yield return new Tweak
        {
            Id = "nv.refresh", Category = nvidia, Title = T("Preferred refresh rate"), Impact = Impact.Low, Applicable = present,
            Info = T("Some games, mostly older ones in exclusive fullscreen, set the display to 60 Hz even on a 144 Hz or faster monitor. Highest available makes the driver always use the display's highest refresh rate for games. The game decides follows what each game asks for."),
            Description = T("Highest available runs games at the display's highest refresh rate, also when a game asks for a lower one (some older games start at 60 Hz)."),
            Store = () => new NvidiaStore(NvApi.RefreshRateOverride),
            Options = Fixed(Rec("highest", T("Highest available"), "1"), Opt("app", T("The game decides (driver default)"), null, "0")),
        };
    }

    // MSI mode for the devices whose interrupts matter in games: graphics card, USB 3 (xHCI) controllers, audio and network.
    // Not for older USB (EHCI) and storage controllers: some of their drivers fail to start with MSI.
    internal static bool InterruptRelevant(PnpDevice d) =>
        d.IsPci && !d.Disabled && (d.ClassCode is "0300" or "0302" or "0380" or "0403" or "0200" or "0280" || d.HasId(@"PCI\CC_0C0330"));

    static IEnumerable<Tweak> InterruptTweaks()
    {
        string category = T("Interrupts (MSI)");
        string note = T("If the device stops working after the restart, choose Off (or Undo) and restart again.");
        string info = T("When a device has work for the processor, it signals with an interrupt. With the old line-based method, several devices can share one line, and the processor has to check which of them called. With MSI (Message Signaled Interrupts) each device sends its own message. Many drivers use MSI already; Current shows which one is in use.");
        foreach (var device in PresentDevices().Where(InterruptRelevant))
        {
            string? description = device.ClassCode switch
            {
                "0300" or "0302" or "0380" => T("Its own interrupt message instead of a shared line, so the processor handles it sooner. NVIDIA turns it on only for some cards."),
                "0C03" => T("The controller of the mouse, keyboard and headset gets its own interrupt message instead of a shared line."),
                "0403" => T("Its own interrupt message instead of a shared line, so sound is less likely to crackle."),
                "0200" or "0280" => T("Its own interrupt message instead of a shared line."),
                _ => null,
            };
            if (description is null) continue;
            string instance = device.InstanceId;
            yield return new Tweak
            {
                Id = "msi." + StableId(instance), Category = category, Title = device.Name, Description = description, Note = note, Info = info,
                Impact = device.ClassCode is "0300" or "0302" or "0380" ? Impact.Medium : Impact.Low,
                Store = Single(new RegistryStore(RegistryHive.LocalMachine, () => Devices.Find(instance) is null ? null : Devices.MsiKey(instance), "MSISupported", RegistryValueKind.DWord)),
                // Without the value the driver decides: the interrupts the device got tell which mode it runs in.
                Summarize = raw => raw ?? (Devices.Find(instance)?.Mode switch { InterruptMode.Msi => "1", InterruptMode.Line => "0", _ => null }),
                Options = Fixed(Rec("on", T("On (MSI)"), "1"), Opt("off", T("Off (line-based interrupt)"), "0")),
                NeedsReboot = true,
            };
        }
    }

    static IReadOnlyList<PnpDevice> PresentDevices()
    {
        try { return Devices.Snapshot(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    // FNV-1a: the same device gets the same ID in the elevated copy and in later runs (undo is stored under it).
    internal static string StableId(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text.ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash.ToString("x8");
    }

    static IEnumerable<Tweak> DeviceTweaks()
    {
        yield return new Tweak
        {
            Id = "dev.powersaving", Category = T("Devices"), Title = T("Power saving of USB and input devices"), Impact = Impact.Medium,
            Description = T("When on, idle USB ports of the mouse, keyboard or headset may sleep and react late (\"Allow the computer to turn off this device\")."),
            Note = T("Windows turns it on again for a device plugged into a new port; the app turns it off again while it runs as administrator."),
            Store = Single(new DevicePowerStore()), Summarize = DevicePowerStore.Summary, Options = DevicePowerStore.Options,
        };

        string unused = T("Unused devices");
        Tweak Switch(string id, string title, string description, Func<PnpDevice, bool> match, string? note = null) => new()
        {
            Id = "dev." + id, Category = unused, Title = title, Description = description, Note = note, Impact = Impact.Low,
            Store = Single(new DeviceSwitchStore(match)),
            Options = Fixed(Opt("on", T("Enabled"), "1"), Opt("off", T("Disabled"), "0")),
        };
        static bool Under(PnpDevice d, params string[] prefixes) => prefixes.Any(p => d.InstanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        yield return Switch("speaker", T("System speaker"), T("The motherboard beeper for error sounds. Nothing in games uses it."),
            d => d.HasId(@"ACPI\PNP0800") || d.HasId("*PNP0800"));
        yield return Switch("midi", T("Microsoft GS Wavetable Synth"), T("Windows' software MIDI synthesizer. Only old games and music apps that play MIDI files use it."),
            d => Under(d, @"SWD\MMDEVAPI\MicrosoftGSWavetableSynth"));
        yield return Switch("rdp", T("Remote Desktop devices"), T("Remote Desktop Device Redirector Bus and the Terminal Server keyboard and mouse drivers. Used only when someone connects to this computer with Remote Desktop."),
            d => Under(d, @"ROOT\RDPBUS\", @"ROOT\RDP_KBD\", @"ROOT\RDP_MOU\"));
        yield return Switch("iscsi", T("Microsoft iSCSI Initiator"), T("Connects to network storage over iSCSI; home computers rarely use it."),
            d => Under(d, @"ROOT\ISCSIPRT\"));
        yield return Switch("vdrive", T("Microsoft Virtual Drive Enumerator"), T("Opens ISO and VHD files as drives. Disable it only if you never open such files."),
            d => Under(d, @"ROOT\VDRVROOT\"));
        yield return Switch("ndisvirtual", T("NDIS Virtual Network Adapter Enumerator"), T("Creates virtual network adapters for some VPN and virtualization programs."),
            d => Under(d, @"ROOT\NDISVIRTUALBUS\"));
        yield return Switch("wan", T("WAN Miniport adapters"), T("Windows' built-in VPN and dial-up connections."),
            d => Under(d, @"SWD\MSRRAS\"),
            T("If this computer connects to the internet with a PPPoE username and password (not through the modem), keep them enabled."));
        yield return Switch("mei", T("Intel Management Engine Interface"), T("Interface to Intel's management engine. Firmware updates and some vendor tools need it."),
            d => d.Service.Equals("MEIx64", StringComparison.OrdinalIgnoreCase) || d.Service.StartsWith("HECI", StringComparison.OrdinalIgnoreCase),
            T("Keep it on laptops; power and thermal management may use it."));
        yield return Switch("amdpsp", T("AMD PSP driver"), T("Driver of AMD's Platform Security Processor. Some security features and vendor tools use it."),
            d => d.Service.Equals("amdpsp", StringComparison.OrdinalIgnoreCase));
        yield return Switch("smbus", T("SMBus controller"), T("Hardware monitoring and RGB lighting programs read sensors through it."),
            d => d.IsPci && d.ClassCode == "0C05",
            T("Fan control, sensor and RGB programs stop working while it is off."));
        yield return Switch("gpuaudio", T("Graphics card audio (HDMI/DisplayPort)"), T("Sends sound to the monitor or TV over the display cable. Disable it if your sound does not go through the display."),
            d => d.IsPci && d.ClassCode == "0403" && d.Vendor is "10DE" or "1002",
            T("A driver update may enable it again."));
    }

    // Services that run without being needed on most gaming computers. Disabled is recommended only when the computer
    // clearly does not need the service (no printer, no Bluetooth adapter); the rest are a choice.
    static IEnumerable<Tweak> ServiceTweaks()
    {
        string services = T("Services");
        Tweak Service(string name, string title, string description, string auto, Func<bool> unneeded) => new()
        {
            Id = "svc." + name.ToLowerInvariant(), Category = services, Title = title, Description = description, Impact = Impact.Low,
            Store = Single(new ServiceStore(name)),
            Options = () =>
            [
                new TweakOption("auto", T("Automatic"), auto, false, "2", "2d"),
                new TweakOption("manual", T("Manual"), "3"),
                new TweakOption("off", T("Disabled"), "4", unneeded()),
            ],
        };

        yield return Service("Spooler", T("Print Spooler"), T("Needed only for printing. With no printer installed it runs for nothing; Print to PDF also stops while it is off."),
            "2", () => !Printers.HasReal());
        yield return Service("DiagTrack", T("Connected User Experiences and Telemetry"), T("Sends usage and diagnostic data to Microsoft in the background."),
            "2", () => true);
        yield return Service("MapsBroker", T("Downloaded Maps Manager"), T("Keeps the offline maps of the Maps app up to date."),
            "2d", () => true);
        yield return Service("bthserv", T("Bluetooth Support Service"), T("Finds and connects Bluetooth devices. Not needed when the computer has no Bluetooth adapter."),
            "2", () => !PresentDevices().Any(d => d.ClassGuid == Devices.ClassBluetooth && (d.IsPci || d.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))));
        yield return Service("SysMain", T("SysMain (Superfetch)"), T("Loads the apps you use often into memory in advance. With only SSDs it can be turned off to cut background disk and memory activity; keep it on if Windows or games are on an HDD."),
            "2", () => false);
        yield return Service("WSearch", T("Windows Search (indexing)"), T("Indexes files for fast searches. When off, searching files in Start and Explorer is slower, but the background disk activity stops."),
            "2d", () => false);
        yield return Service("PcaSvc", T("Program Compatibility Assistant"), T("Watches the programs you start for known compatibility problems and shows suggestions."),
            "2", () => false);
    }
}

// "Allow the computer to turn off this device to save power". Stored as "instance=1;instance=0" so undo restores each device.
internal sealed class DevicePowerStore : ISettingStore
{
    static (int On, int Total) _last;
    IReadOnlyList<(string Instance, bool Enabled)>? _list;
    DateTime _at;

    IReadOnlyList<(string Instance, bool Enabled)> List()
    {
        if (_list is null || DateTime.UtcNow - _at > TimeSpan.FromSeconds(2))
        {
            _list = Devices.PowerSaving();
            _at = DateTime.UtcNow;
        }
        return _list;
    }

    public bool Available => List().Count > 0;

    public string? Read()
    {
        var list = List();
        _last = (list.Count(x => x.Enabled), list.Count);
        return list.Count == 0 ? null : string.Join(";", list.Select(x => $"{x.Instance}={(x.Enabled ? 1 : 0)}"));
    }

    public void Write(string? value)
    {
        if (value is null) return;
        if (value is "0" or "1") Devices.SetPowerSaving(_ => value == "1");
        else
        {
            var wanted = Parse(value);
            Devices.SetPowerSaving(instance => wanted.TryGetValue(instance, out var on) ? on : null);
        }
        _list = null;
    }

    internal static Dictionary<string, bool> Parse(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (Index: p.LastIndexOf('='), Text: p))
            .Where(x => x.Index > 0)
            .GroupBy(x => x.Text[..x.Index], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Text[(g.Last().Index + 1)..] == "1", StringComparer.OrdinalIgnoreCase);

    // Off only when it is off for every device.
    internal static string? Summary(string? raw) =>
        raw is null or "0" or "1" ? raw : Parse(raw).Values.Any(on => on) ? "1" : "0";

    internal static IReadOnlyList<TweakOption> Options()
    {
        var (on, total) = _last;
        string onLabel = on > 0 && on < total ? F("On for {0} of {1} devices", on, total) : T("On (Windows default)");
        return [Catalog.Rec("off", T("Off for all devices"), "0"), Catalog.Opt("on", onLabel, "1")];
    }
}

// Devices of one kind, enabled or disabled together with pnputil. "1" = enabled, "0" = all disabled.
internal sealed class DeviceSwitchStore(Func<PnpDevice, bool> match) : ISettingStore
{
    List<PnpDevice> Matches() => Devices.Snapshot().Where(match).ToList();

    public bool Available => Matches().Count > 0;

    public string? Read() => Matches() is { Count: > 0 } list ? list.All(d => d.Disabled) ? "0" : "1" : null;

    public void Write(string? value)
    {
        bool enable = value != "0";
        try
        {
            foreach (var device in Matches().Where(d => d.Disabled == enable)) Devices.SetEnabled(device.InstanceId, enable);
        }
        finally
        {
            Devices.Invalidate();
        }
    }
}

// Start type of a service: "2" automatic, "2d" automatic (delayed start), "3" manual, "4" disabled.
// Changed with sc.exe, which also handles services whose registry key only the service manager may write.
internal sealed class ServiceStore(string name) : ISettingStore
{
    string Key => $@"SYSTEM\CurrentControlSet\Services\{name}";

    public bool Available => Reg.Get(RegistryHive.LocalMachine, Key, "Start") is int;

    public string? Read() => Reg.Get(RegistryHive.LocalMachine, Key, "Start") is int start
        ? start == 2 && Reg.Get(RegistryHive.LocalMachine, Key, "DelayedAutostart") is 1 ? "2d" : start.ToString()
        : null;

    public void Write(string? value)
    {
        string mode = value switch
        {
            "2" => "auto", "2d" => "delayed-auto", "3" => "demand", "4" => "disabled",
            _ => throw new ArgumentException(F("no such option: {0}", value ?? "-")),
        };
        int code = Sc($"config \"{name}\" start= {mode}");
        if (code != 0) throw new InvalidOperationException(F("{0} exit code {1}", "sc.exe", code));
        // The change applies now, not only after a restart; a service that cannot stop or start is left as it is.
        if (value == "4") Sc($"stop \"{name}\"");
        else if (value is "2" or "2d") Sc($"start \"{name}\"");
    }

    static int Sc(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Shell.ConsoleEncoding };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException(F("Could not start {0}", "sc.exe"));
        p.StandardOutput.ReadToEnd();
        return p.WaitForExit(30_000) ? p.ExitCode : -1;
    }
}

internal static class Printers
{
    static (DateTime At, bool Value)? _cache;

    // A printer other than the virtual ones Windows and Office install (Print to PDF, XPS, Fax, OneNote).
    public static bool HasReal()
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Value;
        bool found = false;
        try
        {
            using var search = new ManagementObjectSearcher("SELECT DriverName, PortName FROM Win32_Printer");
            foreach (ManagementObject o in search.Get())
                using (o)
                    found |= !IsVirtual(o["DriverName"] as string ?? "", o["PortName"] as string ?? "");
        }
        catch (ManagementException)
        {
            found = true; // unknown: no recommendation
        }
        _cache = (DateTime.UtcNow, found);
        return found;
    }

    internal static bool IsVirtual(string driver, string port) =>
        port.Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase) || port.Equals("SHRFAX:", StringComparison.OrdinalIgnoreCase)
        || port.Equals("nul:", StringComparison.OrdinalIgnoreCase) || port.StartsWith("Microsoft.Office.OneNote", StringComparison.OrdinalIgnoreCase)
        || driver.Contains("Print To PDF", StringComparison.OrdinalIgnoreCase) || driver.Contains("XPS Document Writer", StringComparison.OrdinalIgnoreCase)
        || driver.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || driver.Contains("Fax", StringComparison.OrdinalIgnoreCase);
}
