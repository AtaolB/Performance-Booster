using System.Security.Principal;

namespace PerformanceBooster.Tests;

// Latency tools: driver latency, mouse polling, interrupts and devices, services, NVIDIA settings, Defender.
// Fake data for the logic; the real devices, services and driver settings are only read, never changed.
static class LatencyTests
{
    static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static void Run()
    {
        Runner.Test("Driver latency: DPCs and interrupts added up per driver and processor (fake modules)", () =>
        {
            KernelModule[] modules =
            [
                new(0x1000_0000, 0x1000, "storport.sys", @"\SystemRoot\System32\drivers\storport.sys"),
                new(0x2000_0000, 0x10000, "nvlddmkm.sys", @"\SystemRoot\System32\drivers\nvlddmkm.sys"),
                new(0x3000_0000, 0x800, "ndis.sys", @"\SystemRoot\System32\drivers\ndis.sys"),
            ];
            var a = new LatencyAggregator(modules, 10); // 10 ticks per microsecond
            a.Add(isr: false, cpu: 0, routine: 0x2000_0100, ticks: 2_000);  // 200 µs in the graphics driver
            a.Add(isr: false, cpu: 0, routine: 0x2000_FFFF, ticks: 15_000); // 1500 µs, last byte of the image
            a.Add(isr: true, cpu: 2, routine: 0x2000_0200, ticks: 80);      // 8 µs ISR
            a.Add(isr: false, cpu: 1, routine: 0x1000_0010, ticks: 30);     // 3 µs storage
            a.Add(isr: false, cpu: 1, routine: 0x3000_0900, ticks: 50);     // after ndis.sys ends: unknown
            a.Add(isr: false, cpu: 0, routine: 0x2000_0000, ticks: -5);     // broken time stamp: ignored
            a.Add(isr: false, cpu: 0, routine: 0x2000_0000, ticks: 20_000_000); // over a second: ignored
            var r = a.Result(10, 3, name => name == "nvlddmkm.sys" ? "NVIDIA Windows Kernel Mode Driver" : null);
            Check.Equal(4L, r.Dpcs, "DPCs");
            Check.Equal(1L, r.Isrs, "interrupts");
            Check.Equal(1500.0, r.MaxDpcUs, "longest DPC");
            Check.Equal("nvlddmkm.sys", r.MaxDpcModule, "longest DPC's driver");
            Check.Equal(8.0, r.MaxIsrUs, "longest ISR");
            Check.Equal("nvlddmkm.sys", r.Drivers[0].Module, "worst driver first");
            Check.Equal("NVIDIA Windows Kernel Mode Driver", r.Drivers[0].Description, "file description");
            Check.True(r.Drivers[0] is { Dpcs: 2, Isrs: 1, TotalMs: 1.708 }, "graphics driver totals: " + r.Drivers[0]);
            Check.True(r.Drivers.Any(d => d.Module == "?" && d.Dpcs == 1) && r.Drivers.Any(d => d.Module == "storport.sys"), "unknown address and storage");
            Check.True(!r.Drivers.Any(d => d.Module == "ndis.sys"), "no events, not listed");
            Check.Equal(string.Join(",", new long[] { 2, 0, 0, 1, 0, 0, 1 }), string.Join(",", r.DpcHistogram), "histogram (<10, 10-50, ... , 1000+ µs)");
            Check.True(r.Cpus.Count == 3 && r.Cpus[0].Dpcs == 2 && r.Cpus[2].Isrs == 1 && r.Cpus[1].Dpcs == 2, "per processor");
            Check.Equal(3L, r.EventsLost, "dropped events");
        });

        Runner.Test("Driver latency: real measurement (administrator only)", () =>
        {
            if (!IsAdmin) Check.Skip("not running as administrator");
            var r = LatencyTrace.Measure(TimeSpan.FromSeconds(3));
            Check.Equal<string?>(null, r.Error, "error");
            Check.True(r.Dpcs + r.Isrs > 100, $"events in 3 s: {r.Dpcs} DPC, {r.Isrs} ISR");
            Check.True(r.Drivers.Count(d => d.Module != "?") >= 2, "drivers found: " + string.Join(", ", r.Drivers.Select(d => d.Module)));
            Check.True(r.MaxDpcUs is > 0 and < 1_000_000, "longest DPC: " + r.MaxDpcUs);
            Runner.Note($"longest DPC {r.MaxDpcUs:0} µs ({r.MaxDpcModule}), longest ISR {r.MaxIsrUs:0} µs ({r.MaxIsrModule}), lost {r.EventsLost}; " +
                        string.Join(", ", r.Drivers.Take(5).Select(d => $"{d.Module} {d.MaxUs:0} µs")));
        });

        Runner.Test("Mouse polling: rate, nearest polling rate and evenness (fake reports)", () =>
        {
            const double ticksPerMs = 10_000;
            var even = new List<long>();
            long t = 0;
            for (int i = 0; i < 1000; i++) even.Add(t += 10_000); // exactly 1 ms
            var r = MousePolling.Analyze(even, ticksPerMs) ?? throw new TestFailure("no result");
            Check.True(r is { NominalHz: 1000, StablePercent: 100, Reports: 1000 } && Math.Abs(r.AverageHz - 1000) < 0.1 && r.StdDevMs < 0.0001, "even 1000 Hz: " + r);

            var rnd = new Random(7);
            var uneven = new List<long>();
            t = 0;
            for (int i = 0; i < 2000; i++)
            {
                t += i % 400 == 399 ? 3_000_000 : rnd.Next(1_000, 4_000); // 0.1-0.4 ms, with a 300 ms pause every 400 reports
                uneven.Add(t);
            }
            r = MousePolling.Analyze(uneven, ticksPerMs) ?? throw new TestFailure("no result");
            Check.Equal(4000, r.NominalHz, "nearest polling rate (pauses left out)");
            Check.True(r.StablePercent < 90 && r.MaxMs < 1, "uneven: " + r);
            Check.Equal<PollingResult?>(null, MousePolling.Analyze([1, 2, 3], ticksPerMs), "too little movement");
        });

        Runner.Test("Devices: list, interrupts and where the mouse is connected (read only)", () =>
        {
            var all = Devices.Snapshot();
            Check.True(all.Count > 30 && all.All(d => d.Name.Length > 0 && d.InstanceId.Length > 0), "devices: " + all.Count);
            var pci = all.Where(d => d.IsPci).ToList();
            Check.True(pci.Count > 3 && pci.All(d => d.Vendor is { Length: 4 }), "PCI devices with a vendor");
            var gpu = pci.FirstOrDefault(d => d.ClassCode is "0300" or "0302") ?? throw new TestFailure("no display adapter on the PCI bus");
            Check.True(gpu.Mode != InterruptMode.None, $"{gpu.Name} has interrupts: {string.Join(",", gpu.Irqs)}");
            var shared = Devices.SharedIrqs(all);
            Check.True(shared.All(s => s.Irq >= 0 && s.Devices.Count > 1), "shared lines");
            foreach (var mouse in Devices.Mice()) Runner.Note($"mouse {mouse.Mouse}: {mouse.Controller} ({Devices.VendorName(mouse.Vendor)}) hub={mouse.ThroughHub}");
            Runner.Note(string.Join(" · ", pci.Where(Catalog.InterruptRelevant).Select(d => $"{d.Name}: {d.Mode} {string.Join(",", d.Irqs)}")));
            Runner.Note(shared.Count == 0 ? "no shared interrupt lines" : string.Join("; ", shared.Select(s => $"IRQ {s.Irq}: " + string.Join(", ", s.Devices.Select(d => d.Name)))));
        });

        Runner.Test("Device and service settings: rows, IDs and the summarized value", () =>
        {
            Check.Equal(Catalog.StableId(@"PCI\VEN_10DE&DEV_2684&SUBSYS_1"), Catalog.StableId(@"pci\ven_10de&dev_2684&subsys_1"), "ID ignores case");
            Check.True(Catalog.StableId("a") != Catalog.StableId("b") && Catalog.StableId("x").Length == 8, "ID");
            Check.True(Catalog.All.Count(t => t.Id.StartsWith("msi.", StringComparison.Ordinal)) >= 1, "MSI row for at least the graphics card");
            foreach (var id in new[] { "dev.powersaving", "dev.speaker", "dev.wan", "svc.spooler", "svc.diagtrack", "svc.sysmain", "nv.power", "nv.latency", "nv.shader" })
                Check.True(Catalog.Find(id) is not null, "row " + id);
            Check.Equal(Catalog.All.Count, Catalog.All.Select(t => t.Id).Distinct().Count(), "IDs are unique");

            // The undo text can hold one value per device; the options compare the summary.
            var tweak = new Tweak
            {
                Id = "t", Category = "T", Title = "T", Description = "-", Store = () => new FakeStore("a=0;b=1"),
                Summarize = DevicePowerStore.Summary, Options = Catalog.Fixed(Catalog.Rec("off", "Off", "0"), Catalog.Opt("on", "On", "1")),
            };
            var s = tweak.Evaluate();
            Check.True(s is { State: CheckState.Problem, Raw: "1" } && s.Current?.Key == "on", "partly on counts as on: " + s.Raw);
            Check.Equal("0", DevicePowerStore.Summary("a=0;b=0"), "off everywhere");
            Check.True(DevicePowerStore.Parse(@"USB\ROOT_HUB30\4&1&0_0=1;HID\VID_1&PID_2\7&3&0_0=0") is { Count: 2 } map && map[@"usb\root_hub30\4&1&0_0"] && !map[@"HID\VID_1&PID_2\7&3&0_0"], "per device, case insensitive");
            Check.True(DevicePowerStore.Summary(null) is null && DevicePowerStore.Summary("1") == "1", "plain values");

            Check.True(Printers.IsVirtual("Microsoft Print To PDF", "PORTPROMPT:") && Printers.IsVirtual("Send to Microsoft OneNote 16 Driver", "nul:")
                       && Printers.IsVirtual("Microsoft Shared Fax Driver", "SHRFAX:") && !Printers.IsVirtual("HP LaserJet M1132", "USB001"), "virtual printers");
        });

        Runner.Test("Services and NVIDIA settings are read (nothing is changed)", () =>
        {
            foreach (var id in new[] { "svc.spooler", "svc.sysmain", "svc.wsearch", "svc.diagtrack" })
            {
                var s = Catalog.Find(id)!.Evaluate();
                Check.True(s.State == CheckState.NotApplicable || s.Raw is "2" or "2d" or "3" or "4", $"{id}: {s.Raw} ({s.CurrentLabel})");
            }
            var power = Catalog.Find("nv.power")!.Evaluate();
            if (!NvApi.Available) Check.Skip("no NVIDIA driver");
            Check.True(power.State != CheckState.NotApplicable, "power management mode read: " + power.CurrentLabel);
            Runner.Note($"NVIDIA: {power.CurrentLabel} · {Catalog.Find("nv.latency")!.Evaluate().CurrentLabel} · {Catalog.Find("nv.shader")!.Evaluate().CurrentLabel}");
        });

        Runner.Test("Defender: exactly the listed games with a known path are excluded", () =>
        {
            var settings = new AppSettings
            {
                DefenderExclude = true, Games = ["cs2.exe", "valorant.exe", "new.exe"],
                GamePaths = new() { ["cs2.exe"] = @"D:\Steam\cs2.exe", ["valorant.exe"] = @"C:\Riot\VALORANT.exe", ["old.exe"] = @"C:\Old\old.exe" },
                DefenderExcluded = [@"C:\Riot\valorant.exe", @"C:\Old\old.exe"],
            };
            var (add, remove) = Defender.Plan(settings);
            Check.Equal(@"D:\Steam\cs2.exe", string.Join("|", add), "added: the game not excluded yet");
            Check.Equal(@"C:\Old\old.exe", string.Join("|", remove), "removed: no longer in the list (case-insensitive paths)");
            Check.Equal("new.exe", string.Join("|", Defender.Unknown(settings)), "path not known yet");
            settings.DefenderExclude = false;
            (add, remove) = Defender.Plan(settings);
            Check.True(add.Count == 0 && remove.Count == 2, "turned off: everything the app added is removed");
            Runner.Note("Defender active: " + (Defender.Active()?.ToString() ?? "unknown"));
        });
    }
}
