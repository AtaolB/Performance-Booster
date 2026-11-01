using Microsoft.Win32;

namespace PerformanceBooster.Core;

public enum CheckState { Ok, Problem, Info, NotApplicable }

public enum Impact { High, Medium, Low }

// A selectable value of a setting. "also": other raw values that mean the same (for example, on when there is no value).
// Equal by key and value: the options are read again at every check (power plans, services...), and a drop-down must
// still find its selection among the new objects; otherwise it shows nothing.
public sealed class TweakOption(string key, string label, string? value, bool recommended = false, params string?[] also) : IEquatable<TweakOption>
{
    public string Key => key;
    public string Label => label;
    public string? Value => value;
    public bool Recommended => recommended;

    public bool Matches(string? raw) => Same(value, raw) || also.Any(a => Same(a, raw));

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => recommended ? F("{0} (recommended)", label) : label;

    public bool Equals(TweakOption? other) => other is not null && key == other.Key && Same(value, other.Value);

    public override bool Equals(object? obj) => Equals(obj as TweakOption);

    public override int GetHashCode() => HashCode.Combine(key, value?.ToUpperInvariant());
}

public sealed record TweakStatus(CheckState State, string? Raw, TweakOption? Current, IReadOnlyList<TweakOption> Options, string CurrentLabel)
{
    public static TweakStatus Unavailable(string why) => new(CheckState.NotApplicable, null, null, [], why);
}

public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public Impact Impact { get; init; } = Impact.Medium;
    public string? Note { get; init; }
    public required Func<ISettingStore> Store { get; init; }
    public required Func<IReadOnlyList<TweakOption>> Options { get; init; }
    public Func<bool>? Applicable { get; init; }
    public bool RestartsNic { get; init; }
    public bool NeedsReboot { get; init; }
    public bool ChangesPower { get; init; }

    // The stored text kept for undo can hold more than the options compare (one value per device, for example), or nothing
    // when the driver decides; this turns it into the value the options are matched against.
    public Func<string?, string?>? Summarize { get; init; }

    public TweakStatus Evaluate()
    {
        try
        {
            if (Applicable?.Invoke() == false) return TweakStatus.Unavailable(T("not applicable to this computer"));
            var store = Store();
            if (!store.Available) return TweakStatus.Unavailable(T("not present on this computer"));
            var raw = store.Read();
            if (Summarize is not null) raw = Summarize(raw);
            var options = Options();
            var current = options.FirstOrDefault(o => o.Matches(raw));
            var recommended = options.FirstOrDefault(o => o.Recommended);
            var state = recommended is null ? CheckState.Info : current == recommended ? CheckState.Ok : CheckState.Problem;
            var label = current?.Label ?? (raw is null ? T("not set (Windows default)") : F("custom value ({0})", raw));
            return new(state, raw, current, options, label);
        }
        catch (Exception ex)
        {
            return TweakStatus.Unavailable(F("could not be read: {0}", ex.Message));
        }
    }
}

public static partial class Catalog
{
    const RegistryHive HKLM = RegistryHive.LocalMachine;
    const RegistryHive HKCU = RegistryHive.CurrentUser;
    const RegistryValueKind DWord = RegistryValueKind.DWord;
    const RegistryValueKind Text = RegistryValueKind.String;
    const string MMCSS = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    // Means "this option also when there is no value"; named so that a lone null argument does not replace the params array.
    const string? Absent = null;

    static NicInfo? _nic;
    static readonly Lazy<IReadOnlyList<Tweak>> _all = new(Build);

    internal static NicInfo? Nic => _nic;

    // Tests use their own fake settings.
    internal static IReadOnlyList<Tweak>? Override { get; set; }

    public static void RefreshNic() => _nic = NicInfo.Detect();

    public static IReadOnlyList<Tweak> All => Override ?? _all.Value;

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    static string Cat(string name) => T(name);

    internal static List<Tweak> Build()
    {
        string power = Cat("Power"), graphics = Cat("Graphics"), scheduling = Cat("Scheduling"), input = Cat("Input"),
            network = Cat("Network"), adapter = Cat("Network adapter"), windows = Cat("Windows"), memory = Cat("Memory");
        string on = T("On"), off = T("Off");

        var list = new List<Tweak>
        {
            // --- Power ---
            new()
            {
                Id = "power.plan", Category = power, Title = T("Power plan"), Impact = Impact.High,
                Description = T("The High performance plan delays dropping the CPU and devices into low-power states."),
                Note = T("Shortens battery life on laptops; no difference when plugged in."),
                Store = Single(new PowerPlanStore()), Options = PlanOptions, ChangesPower = true,
            },
            new()
            {
                Id = "power.mode", Category = power, Title = T("Power mode (Windows 11)"), Impact = Impact.High,
                Description = T("The Settings > Power slider on the Balanced plan. Best performance lifts the Balanced plan's limits."),
                Store = Single(new PowerModeStore()),
                Options = Fixed(
                    Rec("best", T("Best performance"), "ded574b5-45a0-4f42-8737-46345c09c238"),
                    Opt("balanced", T("Balanced"), "00000000-0000-0000-0000-000000000000"),
                    Opt("saver", T("Best power efficiency"), "961cc777-2547-4f9d-8174-7d86181b8a7a")),
            },
            new()
            {
                Id = "power.idle", Category = power, Title = T("Processor idle states (C-states)"), Impact = Impact.High,
                Description = T("When off, cores never rest: heat rises and laptops boost for a shorter time. Keep it on."),
                Store = PowS(Power.SubProcessor, "5d76a2ca-e8c0-402f-a133-2158492d58ad"),
                Options = Fixed(Rec("on", on, "0"), Opt("off", off, "1")), ChangesPower = true,
            },
            new()
            {
                Id = "power.parking", Category = power, Impact = Impact.Medium,
                Title = Cpu.IsHybrid ? T("Performance core parking") : T("Core parking"),
                Description = Cpu.IsHybrid
                    ? T("P-cores are not parked, so game threads run without waiting for them to wake up. E-cores are left alone.")
                    : T("Cores are not parked, so game threads run without waiting for them to wake up."),
                Store = PowS(Power.SubProcessor, Cpu.IsHybrid ? "0cc5b647-c1df-4637-891a-dec35c318584" : "0cc5b647-c1df-4637-891a-dec35c318583"),
                Options = Fixed(Rec("100", T("No parking (100% unparked)"), "100"), Opt("50", T("50% unparked"), "50"), Opt("10", T("10% unparked (power saving)"), "10")),
                ChangesPower = true,
            },
            new()
            {
                Id = "power.epp", Category = power, Title = T("Processor energy preference (EPP)"), Impact = Impact.Medium,
                Description = T("How quickly the processor raises its frequency. 0 = fastest response."),
                Store = PowS(Power.SubProcessor, "36687f9e-e3a5-4dbf-b1dc-15eb381c6863"),
                Options = Fixed(Rec("0", T("0 · maximum performance"), "0"), Opt("33", T("33 · balanced"), "33"), Opt("50", "50", "50"), Opt("80", T("80 · power saving"), "80")),
                ChangesPower = true,
            },
            new()
            {
                Id = "power.boost", Category = power, Title = T("Boost mode"), Impact = Impact.Medium,
                Description = T("How the processor boosts to turbo frequency."),
                Store = PowS(Power.SubProcessor, "be337238-0d82-4146-a960-4f3749d470c7"),
                Options = Fixed(Rec("2", T("Aggressive"), "2"), Opt("1", T("Enabled"), "1"), Opt("4", T("Efficient aggressive"), "4"), Opt("3", T("Efficient"), "3"), Opt("0", off, "0")),
                ChangesPower = true,
            },
            new()
            {
                Id = "power.usb", Category = power, Title = T("USB selective suspend"), Impact = Impact.Medium,
                Description = T("When on, idle USB ports go to sleep; the mouse, keyboard or headset can hitch while waking up."),
                Store = PowS(Power.SubUsb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
                Options = Fixed(Rec("off", off, "0"), Opt("on", on, "1")), ChangesPower = true,
            },
            new()
            {
                Id = "power.aspm", Category = power, Title = T("PCIe link state power management"), Impact = Impact.Medium,
                Description = T("When on, the PCIe links of the GPU, NVMe drive and network adapter enter power saving and add latency when waking up."),
                Store = PowS(Power.SubPciExpress, "ee12f906-d277-404b-b6da-e5fa1a576df5"),
                Options = Fixed(Rec("off", off, "0"), Opt("mid", T("Moderate power saving"), "1"), Opt("max", T("Maximum power saving"), "2")),
                ChangesPower = true,
            },
            new()
            {
                Id = "power.wifi", Category = power, Title = T("Wireless adapter power saving"), Impact = Impact.Medium,
                Description = T("Ping fluctuates while the Wi-Fi adapter is in power saving mode."),
                Applicable = NicInfo.HasWireless,
                Store = PowS(Power.SubWireless, "12bbebe6-58d6-4636-95bb-3217ef867c1a"),
                Options = Fixed(Rec("max", T("Maximum performance"), "0"), Opt("low", T("Low power saving"), "1"), Opt("mid", T("Moderate power saving"), "2"), Opt("save", T("Maximum power saving"), "3")),
                ChangesPower = true,
            },
            new()
            {
                Id = "power.faststartup", Category = power, Title = T("Fast startup"), Impact = Impact.Low,
                Description = T("Shutting down does not reset the kernel and drivers, so driver problems can build up."),
                Store = RegS(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", DWord),
                Options = Fixed(Rec("off", off, "0"), Opt("on", on, "1", Absent)), NeedsReboot = true,
            },
            new()
            {
                Id = "power.hibernate", Category = power, Title = T("Hibernation"), Impact = Impact.Low,
                Description = T("Turning it off frees disk space equal to your RAM (hiberfil.sys) and also turns off fast startup."),
                Note = T("On laptops it prevents data loss when the battery runs out; think before turning it off."),
                Store = Single(new HibernateStore()),
                Options = Fixed(Opt("on", on, "1"), Opt("off", off, "0")),
            },

            // --- Graphics ---
            new()
            {
                Id = "gfx.hags", Category = graphics, Title = T("Hardware-accelerated GPU scheduling"), Impact = Impact.Medium,
                Description = T("The GPU manages its own memory and work queue. Required for features such as DLSS Frame Generation."),
                Store = Single(new HagsStore()),
                Options = Fixed(Rec("on", on, "2"), Opt("off", off, "1")), NeedsReboot = true,
            },
            new()
            {
                Id = "gfx.gamemode", Category = graphics, Title = T("Game Mode"), Impact = Impact.Medium,
                Description = T("Windows prioritizes the game; it does not install updates while you play and shows fewer notifications."),
                Store = RegS(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", DWord),
                Options = Fixed(Rec("on", on, "1", Absent), Opt("off", off, "0")),
            },
            new()
            {
                Id = "gfx.gamedvr", Category = graphics, Title = T("Game Bar recording"), Impact = Impact.Medium,
                Description = T("Background recording uses GPU and disk while gaming. NVIDIA/AMD recording tools are not affected."),
                Store = Single(new CompositeStore(
                    new RegistryStore(HKCU, () => @"System\GameConfigStore", "GameDVR_Enabled", DWord),
                    new RegistryStore(HKCU, () => @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", DWord))),
                Options = Fixed(Rec("off", off, "0|0"), Opt("on", on, "1|1", "1|-", "-|-", "-|1")),
            },
            new()
            {
                Id = "gfx.windowed", Category = graphics, Title = T("Optimizations for windowed games"), Impact = Impact.Medium,
                Description = T("DirectX 10/11 windowed and borderless games switch to the low-latency flip model."),
                Store = Single(new DxFlagStore("SwapEffectUpgradeEnable")),
                Options = Fixed(Rec("on", on, "1"), Opt("off", off, "0")),
            },
            new()
            {
                Id = "gfx.vrr", Category = graphics, Title = T("Variable refresh rate optimization"), Impact = Impact.Low,
                Description = T("Applies VRR to games that do not support it on G-Sync/FreeSync displays. No effect on displays without VRR."),
                Store = Single(new DxFlagStore("VRROptimizeEnable")),
                Options = Fixed(Opt("on", on, "1"), Opt("off", off, "0")),
            },
            new()
            {
                Id = "gfx.mpo", Category = graphics, Title = T("MPO (multiplane overlay)"), Impact = Impact.Medium,
                Description = T("When off (OverlayTestMode=5), latency can rise in windowed/borderless games. Turn it off only if the screen flickers."),
                Store = RegS(HKLM, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", DWord),
                Options = Fixed(Rec("on", T("On (Windows default)"), null), Opt("off", off, "5")), NeedsReboot = true,
            },

            // --- Scheduling ---
            new()
            {
                Id = "sched.responsiveness", Category = scheduling, Title = T("Background multimedia share"), Impact = Impact.Low,
                Description = T("SystemResponsiveness: the CPU share reserved for audio/video tasks (%)."),
                Store = RegS(HKLM, MMCSS, "SystemResponsiveness", DWord),
                Options = Fixed(Rec("10", T("10%"), "10"), Opt("20", T("20% (Windows default)"), "20", Absent)),
            },
            new()
            {
                Id = "sched.throttle", Category = scheduling, Title = T("Network throttling"), Impact = Impact.Low,
                Description = T("NetworkThrottlingIndex: limits network packets while audio/video plays (e.g. game + Discord)."),
                Store = RegS(HKLM, MMCSS, "NetworkThrottlingIndex", DWord),
                Options = Fixed(Rec("off", off, "4294967295"), Opt("on", T("On (Windows default)"), "10", Absent)),
            },
            new()
            {
                Id = "sched.mmcss", Category = scheduling, Title = T("MMCSS game priority"), Impact = Impact.Low,
                Description = T("Thread, scheduling and disk priority for games that register with MMCSS."),
                Store = Single(new CompositeStore(
                    new RegistryStore(HKLM, () => MMCSS + @"\Tasks\Games", "Priority", DWord),
                    new RegistryStore(HKLM, () => MMCSS + @"\Tasks\Games", "Scheduling Category", Text),
                    new RegistryStore(HKLM, () => MMCSS + @"\Tasks\Games", "SFIO Priority", Text))),
                Options = Fixed(Rec("high", T("High"), "6|High|High"), Opt("default", T("Windows default"), "2|Medium|Normal")),
            },
            new()
            {
                Id = "sched.timer", Category = scheduling, Title = T("Global timer resolution"), Impact = Impact.Low,
                Description = T("On Windows 11, applies the high timer resolution one app requests to the whole system. Fixes frame times in some older games."),
                Note = T("Reduces battery life. Leave it off unless you have a problem."),
                Applicable = () => Environment.OSVersion.Version.Build >= 22000,
                Store = RegS(HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", DWord),
                Options = Fixed(Opt("off", T("Off (Windows default)"), null, "0"), Opt("on", on, "1")), NeedsReboot = true,
            },
            new()
            {
                Id = "sched.quantum", Category = scheduling, Title = T("Processor time slices"), Impact = Impact.Low,
                Description = T("Win32PrioritySeparation: how long a thread runs before Windows switches to another, and how much longer the app in front gets. Short slices are said to make input feel quicker, but tests show no consistent gain, and background downloads and tasks can slow down."),
                Store = RegS(HKLM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", DWord),
                Options = Fixed(Rec("default", T("Windows default"), "2", Absent), Opt("short", T("Short, variable, app in front ×3"), "38")),
            },

            // Boot settings that guides recommend and Windows never sets itself; the Windows default is the recommended value.
            new()
            {
                Id = "boot.platformclock", Category = scheduling, Title = T("Platform clock (HPET)"), Impact = Impact.Low,
                Description = T("useplatformclock in the boot settings: Windows times everything with the slower HPET instead of the processor's own clock (TSC). On most computers it adds latency and lowers FPS."),
                Store = Single(new BcdStore("useplatformclock")),
                Options = Fixed(Rec("default", T("Off (Windows default)"), null, "no"), Opt("on", on, "yes")), NeedsReboot = true,
            },
            new()
            {
                Id = "boot.dynamictick", Category = scheduling, Title = T("Dynamic tick"), Impact = Impact.Low,
                Description = T("disabledynamictick in the boot settings: with dynamic tick, the timer stops while the processor is idle, which saves power. Turning it off brings no consistent gain; some users report uneven mouse movement."),
                Store = Single(new BcdStore("disabledynamictick")),
                Options = Fixed(Rec("default", T("On (Windows default)"), null, "no"), Opt("off", off, "yes")), NeedsReboot = true,
            },
            new()
            {
                Id = "boot.tscsync", Category = scheduling, Title = T("Clock synchronization of the cores"), Impact = Impact.Low,
                Description = T("tscsyncpolicy in the boot settings: how Windows keeps the clocks of the processor cores in step. Windows picks the right policy itself; forcing one does not help games."),
                Store = Single(new BcdStore("tscsyncpolicy")),
                Options = Fixed(Rec("default", T("Windows default"), null, "default"), Opt("enhanced", T("Enhanced"), "enhanced"), Opt("legacy", T("Legacy"), "legacy")),
                NeedsReboot = true,
            },

            // --- Memory ---
            new()
            {
                Id = "mem.pagefile", Category = memory, Title = T("Page file"), Impact = Impact.Medium,
                Description = T("When RAM runs out, Windows moves data to the page file. Without it, games crash or stutter once memory is full, even with a lot of RAM. Let Windows manage its size."),
                Store = Single(new PageFileStore()), Summarize = PageFileStore.Summary,
                Options = Fixed(Rec("auto", T("Managed by Windows"), "auto"), Opt("off", off, "off")), NeedsReboot = true,
            },
            new()
            {
                Id = "mem.compression", Category = memory, Title = T("Memory compression"), Impact = Impact.Low,
                Description = T("Windows compresses rarely used memory instead of writing it to the disk. It costs a little processor time; with less than 32 GB of RAM, turning it off usually makes stutter worse."),
                // Needs the SysMain service; whoever disabled it (Services, below) chose this too.
                Applicable = () => !MemoryCompressionStore.SysMainDisabled(),
                Store = Single(new MemoryCompressionStore()),
                Options = Fixed(Rec("on", T("On (Windows default)"), "1"), Opt("off", off, "0")), NeedsReboot = true,
            },

            // --- Input ---
            new()
            {
                Id = "input.mouseaccel", Category = input, Title = T("Mouse acceleration"), Impact = Impact.Medium,
                Description = T("\"Enhance pointer precision\". When off, pointer movement does not depend on speed, so aim stays consistent."),
                Store = Single(new MouseAccelStore()),
                Options = Fixed(Rec("off", off, "0"), Opt("on", on, "1")),
            },
            new()
            {
                Id = "input.shortcuts", Category = input, Title = T("Accessibility shortcut pop-ups"), Impact = Impact.Low,
                Description = T("The pop-ups that appear after pressing Shift 5 times or holding right Shift or NumLock interrupt the game. Takes effect at the next sign-in."),
                Store = Single(new CompositeStore(
                    new RegistryStore(HKCU, () => @"Control Panel\Accessibility\StickyKeys", "Flags", Text),
                    new RegistryStore(HKCU, () => @"Control Panel\Accessibility\Keyboard Response", "Flags", Text),
                    new RegistryStore(HKCU, () => @"Control Panel\Accessibility\ToggleKeys", "Flags", Text))),
                Options = Fixed(Rec("off", off, "506|122|58"), Opt("on", T("On (Windows default)"), "510|126|62")),
            },
            new()
            {
                Id = "input.keyrepeat", Category = input, Title = T("Keyboard repeat delay"), Impact = Impact.Low,
                Description = T("How long a held key waits before it starts repeating."),
                Store = Single(new KeyRepeatStore()),
                Options = Fixed(Opt("fast", T("Shortest"), "0,31"), Opt("default", T("Windows default"), "1,31")),
            },

            // --- Network ---
            new()
            {
                Id = "net.dns", Category = network, Title = T("DNS server"), Impact = Impact.Low,
                Description = T("Affects name resolution speed, not in-game ping. Find the fastest one with the DNS test on the Network tab."),
                Store = Single(new DnsStore()),
                // Each known server the normal way and, on Windows 11, encrypted (DNS over HTTPS).
                Options = Fixed([
                    Opt("dhcp", T("Automatic (from router)"), "dhcp"),
                    .. DnsBenchmark.Known.SelectMany(s => DnsStore.EncryptionSupported
                        ? new[] { Opt(s.Key, $"{s.Name} {s.Servers[0]}", string.Join(",", s.Servers)),
                                  Opt(s.Key + "-doh", $"{s.Name} {s.Servers[0]} · " + T("encrypted"), string.Join(",", s.Servers) + DnsStore.EncryptedSuffix) }
                        : [Opt(s.Key, $"{s.Name} {s.Servers[0]}", string.Join(",", s.Servers))]),
                ]),
            },
            new()
            {
                Id = "net.nagle", Category = network, Title = T("Nagle's algorithm"), Impact = Impact.Low,
                Description = T("Batches small TCP packets. Only matters for games that use TCP (some MMOs); most games use UDP."),
                Store = Single(new CompositeStore(
                    new RegistryStore(HKLM, () => Nic?.TcpipPath, "TcpAckFrequency", DWord),
                    new RegistryStore(HKLM, () => Nic?.TcpipPath, "TCPNoDelay", DWord))),
                Options = Fixed(Rec("off", off, "1|1"), Opt("on", T("On (Windows default)"), "-|-")),
            },
            new()
            {
                // A non-admin app cannot read the NetworkService key the Settings app uses; the policy key is readable by everyone.
                Id = "net.p2p", Category = network, Title = T("Sharing updates with other PCs"), Impact = Impact.Low,
                Description = T("Delivery Optimization: can upload Windows updates to other PCs on the internet and keep your line busy."),
                Note = T("Written as a policy; Settings may show \"managed by your organization\". \"Leave it to Windows settings\" removes it."),
                Store = RegS(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", DWord),
                Options = Fixed(Rec("off", off, "0"), Opt("lan", T("Local network only"), "1"), Opt("internet", T("Local network and internet"), "3"),
                    Opt("default", T("Leave it to Windows settings"), null)),
            },
            new()
            {
                Id = "nic.pnp", Category = adapter, Title = T("Turning the adapter off to save power"), Impact = Impact.Low,
                Description = T("\"Allow the computer to turn off this device to save power\". When off, the adapter does not drop the link while idle."),
                Store = Single(new RegistryStore(HKLM, () => Nic?.ClassKeyPath, "PnPCapabilities", DWord)),
                Options = Fixed(Rec("off", T("Do not allow"), "24"), Opt("on", T("Allow (Windows default)"), null, "0")),
                RestartsNic = true, NeedsReboot = true,
            },

            // --- Windows ---
            new()
            {
                Id = "win.cfr", Category = windows, Title = T("\"Get the latest updates as soon as they're available\""), Impact = Impact.Low,
                Description = T("When on, new features arrive before everyone else and the risk of bugs grows. Security updates are not affected."),
                Store = RegS(HKLM, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "IsContinuousInnovationOptedIn", DWord),
                Options = Fixed(Rec("off", off, "0", Absent), Opt("on", on, "1")),
            },
        };

        // Advanced properties of the network adapter: only those present on the adapter are shown (Realtek, Intel, Killer...).
        (string Keyword, string Title, string Description, string On, Impact Impact)[] nicProps =
        [
            ("*EEE", T("Energy-Efficient Ethernet (EEE)"), T("Puts the link to sleep when idle; adds latency when waking up and drops the speed to 100 Mbps on some adapters."), "1", Impact.Medium),
            ("AdvancedEEE", T("Advanced EEE"), T("Realtek's own version of EEE."), "1", Impact.Low),
            ("EEELinkAdvertisement", T("EEE link advertisement"), T("EEE on Intel adapters."), "1", Impact.Low),
            ("EnableGreenEthernet", T("Green Ethernet"), T("Lowers power on short cables; can cause link problems."), "1", Impact.Low),
            ("GigaLite", T("Gigabit Lite"), T("Can drop the link to 500 Mbps mode."), "1", Impact.Low),
            ("PowerSavingMode", T("Power saving mode"), T("Puts the adapter to sleep when idle."), "1", Impact.Low),
            ("AutoDisableGigabit", T("Auto disable gigabit"), T("Drops the speed to 100 Mbps to save power."), "1", Impact.Medium),
            ("ReduceSpeedOnPowerDown", T("Reduce speed on power down"), T("Lowers the speed during sleep or on battery."), "1", Impact.Low),
            ("SipsEnabled", T("System idle power saver"), T("Puts the adapter to sleep when idle."), "1", Impact.Low),
            ("ULPMode", T("Ultra low power mode"), T("Turns the adapter off while no cable is plugged in."), "1", Impact.Low),
            ("*FlowControl", T("Flow control"), T("Pause frames can add network latency."), "3", Impact.Low),
            ("*InterruptModeration", T("Interrupt moderation"), T("Batches packets and processes them together, which adds latency. Turning it off raises CPU usage a little."), "1", Impact.Medium),
        ];
        foreach (var p in nicProps)
        {
            var store = new NicPropertyStore(p.Keyword);
            list.Add(new Tweak
            {
                Id = "nic." + p.Keyword.TrimStart('*').ToLowerInvariant(), Category = adapter, Title = p.Title, Description = p.Description,
                Impact = p.Impact, Store = () => store,
                Options = Fixed(Rec("off", off, "0"), Opt("on", on, p.On, "1", "2", "3")),
                RestartsNic = true,
            });
        }

        // NVIDIA's driver settings next to the graphics settings; devices and services last (Tweaks.Devices.cs).
        list.InsertRange(list.FindIndex(t => t.Id == "sched.responsiveness"), NvidiaTweaks());
        list.AddRange(InterruptTweaks());
        list.AddRange(DeviceTweaks());
        list.AddRange(ServiceTweaks());
        return list;
    }

    static List<TweakOption> PlanOptions()
    {
        var schemes = Power.Schemes();
        // Plan names are in the Windows language.
        static bool IsHigh(string n) => L.HasWindowsName(n, "High performance");
        static bool IsUltimate(string n) => L.HasWindowsName(n, "Ultimate");

        Guid? recommended = schemes.Where(s => s.Id == Power.HighPerformance).Select(s => (Guid?)s.Id).FirstOrDefault()
                            ?? schemes.Where(s => IsHigh(s.Name)).Select(s => (Guid?)s.Id).FirstOrDefault()
                            ?? schemes.Where(s => IsUltimate(s.Name)).Select(s => (Guid?)s.Id).FirstOrDefault();

        var options = schemes.Select(s => new TweakOption("plan:" + s.Id, s.Name, s.Id.ToString(), s.Id == recommended)).ToList();
        if (!schemes.Any(s => s.Id == Power.HighPerformance || IsHigh(s.Name)))
            options.Add(new TweakOption("create-high", T("High performance (create)"), PowerPlanStore.CreatePrefix + Power.HighPerformance, recommended is null));
        if (!schemes.Any(s => IsUltimate(s.Name)))
            options.Add(new TweakOption("create-ultimate", T("Ultimate Performance (create)"), PowerPlanStore.CreatePrefix + Power.UltimateTemplate));
        return options;
    }

    internal static Func<IReadOnlyList<TweakOption>> Fixed(params TweakOption[] options)
    {
        IReadOnlyList<TweakOption> list = options;
        return () => list;
    }

    internal static TweakOption Rec(string key, string label, string? value, params string?[] also) => new(key, label, value, true, also);

    internal static TweakOption Opt(string key, string label, string? value, params string?[] also) => new(key, label, value, false, also);

    static Func<ISettingStore> Single(ISettingStore store) => () => store;

    static Func<ISettingStore> RegS(RegistryHive hive, string path, string name, RegistryValueKind kind) =>
        Single(new RegistryStore(hive, () => path, name, kind));

    static Func<ISettingStore> PowS(Guid sub, string setting) => Single(new PowerStore(sub, new Guid(setting)));
}
