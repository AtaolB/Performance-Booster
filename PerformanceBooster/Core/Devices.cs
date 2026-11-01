using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace PerformanceBooster.Core;

public enum InterruptMode { None, Line, Msi }

// A device as Device Manager shows it, read through the configuration manager (cfgmgr32); reading needs no administrator rights.
public sealed partial class PnpDevice(uint devInst, string instanceId, string name, Guid classGuid, string service, string[] hardwareIds, string[] compatibleIds, bool disabled)
{
    IReadOnlyList<int>? _irqs;

    internal uint DevInst => devInst;
    public string InstanceId => instanceId;
    public string Name => name;
    public Guid ClassGuid => classGuid;
    public string Service => service;
    public IReadOnlyList<string> HardwareIds => hardwareIds;
    public IReadOnlyList<string> CompatibleIds => compatibleIds;
    public bool Disabled => disabled;

    public bool IsPci => InstanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);

    // "PCI\VEN_10DE&DEV_2684&..." -> "10DE".
    public string? Vendor => IsPci && InstanceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase) is int i and >= 0 && InstanceId.Length >= i + 8
        ? InstanceId.Substring(i + 4, 4).ToUpperInvariant()
        : null;

    // PCI class and subclass from the compatible IDs ("PCI\CC_030000" -> "0300").
    public string? ClassCode => CompatibleIds.Select(c => c.ToUpperInvariant()).FirstOrDefault(c => c.StartsWith(@"PCI\CC_", StringComparison.Ordinal) && c.Length >= 11)?.Substring(7, 4);

    // Interrupts Windows gave the device: line-based IRQ numbers, MSI messages as negative numbers (as in Device Manager).
    public IReadOnlyList<int> Irqs => _irqs ??= Devices.ReadIrqs(devInst);

    public InterruptMode Mode => Irqs.Any(i => i < 0) ? InterruptMode.Msi : Irqs.Count > 0 ? InterruptMode.Line : InterruptMode.None;

    public bool HasId(string id) => HardwareIds.Concat(CompatibleIds).Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record SharedIrq(int Irq, IReadOnlyList<PnpDevice> Devices);

public sealed record PcieLink(uint Speed, uint Width, uint MaxSpeed, uint MaxWidth)
{
    public bool FewerLanes => Width > 0 && Width < MaxWidth;
}

public static partial class Devices
{
    public static readonly Guid ClassUsb = new("36fc9e60-c465-11cf-8056-444553540000");
    public static readonly Guid ClassMouse = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    public static readonly Guid ClassBluetooth = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    static (DateTime At, IReadOnlyList<PnpDevice> List)? _cache;
    static readonly Lock Gate = new();

    // Present devices, read again at most every 3 seconds (all settings of the Settings tab are checked in one go).
    public static IReadOnlyList<PnpDevice> Snapshot()
    {
        lock (Gate)
        {
            if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(3)) return c.List;
            var list = Enumerate();
            _cache = (DateTime.UtcNow, list);
            return list;
        }
    }

    public static void Invalidate()
    {
        lock (Gate) _cache = null;
    }

    public static PnpDevice? Find(string instanceId) => Snapshot().FirstOrDefault(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));

    static List<PnpDevice> Enumerate()
    {
        var list = new List<PnpDevice>();
        if (CM_Get_Device_ID_List_SizeW(out uint length, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || length == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)length * 2);
        try
        {
            if (CM_Get_Device_ID_ListW(null, buffer, length, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS) return list;
            foreach (var id in Marshal.PtrToStringUni(buffer, (int)length).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNodeW(out uint inst, id, 0) != CR_SUCCESS) continue;
                bool disabled = CM_Get_DevNode_Status(out uint status, out uint problem, inst, 0) == CR_SUCCESS
                                && (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
                list.Add(new PnpDevice(inst, id,
                    Text(inst, NameKey) ?? Text(inst, DescriptionKey) ?? id,
                    Property(inst, ClassGuidKey, out _) is { Length: >= 16 } g ? new Guid(g.Take(16).ToArray()) : Guid.Empty,
                    Text(inst, ServiceKey) ?? "",
                    Texts(inst, HardwareIdsKey), Texts(inst, CompatibleIdsKey), disabled));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    internal static IReadOnlyList<int> ReadIrqs(uint devInst)
    {
        var irqs = new List<int>();
        if (CM_Get_First_Log_Conf(out nuint config, devInst, ALLOC_LOG_CONF) != CR_SUCCESS) return irqs;
        nuint current = config;
        try
        {
            while (CM_Get_Next_Res_Des(out nuint next, current, ResType_IRQ, out _, 0) == CR_SUCCESS)
            {
                if (current != config) CM_Free_Res_Des_Handle(current);
                current = next;
                if (CM_Get_Res_Des_Data_Size(out uint size, current, 0) != CR_SUCCESS || size < 16) continue;
                var data = new byte[size];
                // IRQ_DES: Count, Type, Flags, Alloc_Num (the IRQ; MSI messages are negative).
                if (CM_Get_Res_Des_Data(current, data, size, 0) == CR_SUCCESS) irqs.Add(BitConverter.ToInt32(data, 12));
            }
        }
        finally
        {
            if (current != config) CM_Free_Res_Des_Handle(current);
            CM_Free_Log_Conf_Handle(config);
        }
        return irqs;
    }

    // Line-based IRQs used by more than one device. MSI messages are never shared.
    public static IReadOnlyList<SharedIrq> SharedIrqs(IEnumerable<PnpDevice> devices) =>
        devices.Where(d => !d.Disabled)
            .SelectMany(d => d.Irqs.Where(i => i >= 0).Distinct().Select(i => (Irq: i, Device: d)))
            .GroupBy(x => x.Irq)
            .Where(g => g.Count() > 1)
            .Select(g => new SharedIrq(g.Key, g.Select(x => x.Device).ToList()))
            .OrderBy(s => s.Irq)
            .ToList();

    // --- Changes (administrator) ---

    // pnputil (Windows 10 2004 and later) keeps the device disabled after a restart.
    public static void SetEnabled(string instanceId, bool enable)
    {
        var psi = new ProcessStartInfo("pnputil.exe", $"/{(enable ? "enable" : "disable")}-device \"{instanceId}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Shell.ConsoleEncoding,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(F("Could not start {0}", "pnputil.exe"));
        p.StandardOutput.ReadToEnd(); // only the exit code is used
        if (!p.WaitForExit(60_000)) throw new TimeoutException(F("{0} did not respond", "pnputil.exe"));
        if (p.ExitCode is not (0 or ErrorSuccessRebootRequired)) throw new InvalidOperationException(F("{0} exit code {1}", "pnputil.exe", p.ExitCode));
    }

    // Where MSI mode is switched.
    public static string MsiKey(string instanceId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    // "Allow the computer to turn off this device to save power" of USB and input devices (WMI MSPower_DeviceEnable).
    // The instance name is the device instance ID followed by "_0".
    public static IReadOnlyList<(string Instance, bool Enabled)> PowerSaving()
    {
        var list = new List<(string, bool)>();
        using var search = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT InstanceName, Enable FROM MSPower_DeviceEnable"));
        foreach (ManagementObject o in search.Get())
            using (o)
            {
                if (o["InstanceName"] is string instance && IsInputPath(instance) && o["Enable"] is bool enabled) list.Add((instance, enabled));
            }
        return list.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SetPowerSaving(Func<string, bool?> wanted)
    {
        using var search = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT * FROM MSPower_DeviceEnable"));
        foreach (ManagementObject o in search.Get())
            using (o)
            {
                if (o["InstanceName"] is not string instance || !IsInputPath(instance) || o["Enable"] is not bool enabled) continue;
                if (wanted(instance) is bool value && value != enabled)
                {
                    o["Enable"] = value;
                    o.Put();
                }
            }
    }

    // USB hubs, controllers and devices (mouse, keyboard, headset receivers) and HID devices.
    static bool IsInputPath(string instance) =>
        instance.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || instance.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)
        || (instance.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && Find(instance.EndsWith("_0", StringComparison.Ordinal) ? instance[..^2] : instance)?.ClassGuid == ClassUsb);

    // --- Where the mouse is connected ---

    public sealed record MousePort(string Mouse, string? Controller, string? Vendor, bool ThirdParty, bool ThroughHub, bool Usb);

    // Add-on controller chips, worse than the processor's or chipset's own controller.
    static readonly Dictionary<string, string> UsbVendors = new()
    {
        ["8086"] = "Intel", ["1022"] = "AMD", ["1B21"] = "ASMedia", ["1106"] = "VIA", ["1B6F"] = "Etron", ["1912"] = "Renesas", ["1B73"] = "Fresco Logic", ["104C"] = "Texas Instruments",
    };

    public static bool IsThirdPartyUsb(string? vendor) => vendor is not null and not ("8086" or "1022");

    public static string? VendorName(string? vendor) => vendor is not null && UsbVendors.TryGetValue(vendor, out var name) ? name : vendor;

    // One entry per physical mouse (a gaming mouse often has several mouse functions).
    public static IReadOnlyList<MousePort> Mice()
    {
        var all = Snapshot();
        var byInst = all.ToDictionary(d => d.DevInst);
        var result = new List<MousePort>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mouse in all.Where(d => d.ClassGuid == ClassMouse && !d.Disabled && !d.InstanceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)))
        {
            var chain = new List<PnpDevice>();
            uint inst = mouse.DevInst;
            for (int depth = 0; depth < 12 && CM_Get_Parent(out uint parent, inst, 0) == CR_SUCCESS; depth++)
            {
                if (!byInst.TryGetValue(parent, out var device)) break;
                chain.Add(device);
                inst = parent;
            }
            // The USB device itself: not one of its interfaces (&MI_xx) and not a hub it is plugged into.
            var usbDevice = chain.FirstOrDefault(d => d.InstanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
                                                      && !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase) && !IsHub(d));
            var controller = chain.FirstOrDefault(d => d.IsPci && d.ClassGuid == ClassUsb);
            if (!seen.Add(usbDevice?.InstanceId ?? mouse.InstanceId)) continue;
            bool hub = chain.Any(IsHub);
            result.Add(new MousePort(usbDevice?.Name ?? mouse.Name, controller?.Name, controller?.Vendor, IsThirdPartyUsb(controller?.Vendor), hub, controller is not null));
        }
        return result;
    }

    // An external hub (in a monitor, keyboard or on the motherboard); the root hub of the controller does not count.
    static bool IsHub(PnpDevice d) =>
        d.InstanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && d.Service.StartsWith("usbhub", StringComparison.OrdinalIgnoreCase);

    // The PCIe link of a device: generation (1 = 1.0 ... 5 = 5.0) and lane count, now and at most (what the device supports).
    // Graphics cards lower the generation while idle, so only the lane count says something about the slot.
    public static PcieLink? Link(PnpDevice device)
    {
        uint? Read(uint pid) => Property(device.DevInst, new DEVPROPKEY(PciDeviceProps, pid), out _) is { Length: >= 4 } b ? BitConverter.ToUInt32(b, 0) : null;
        return Read(9) is { } speed && Read(10) is { } width && Read(11) is { } maxSpeed && Read(12) is { } maxWidth && maxWidth > 0
            ? new PcieLink(speed, width, maxSpeed, maxWidth)
            : null;
    }

    static string? Text(uint inst, DEVPROPKEY key) =>
        Property(inst, key, out _) is { } bytes ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;

    static string[] Texts(uint inst, DEVPROPKEY key) =>
        Property(inst, key, out _) is { } bytes ? Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];

    static byte[]? Property(uint inst, DEVPROPKEY key, out uint type)
    {
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(inst, ref key, out type, null, ref size, 0) != CR_BUFFER_SMALL || size == 0) return null;
        var buffer = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out type, buffer, ref size, 0) == CR_SUCCESS ? buffer : null;
    }

    // --- cfgmgr32 ---

    const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A, ErrorSuccessRebootRequired = 3010;
    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100, ALLOC_LOG_CONF = 2, ResType_IRQ = 4, DN_HAS_PROBLEM = 0x400, CM_PROB_DISABLED = 22;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid PciDeviceProps = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62"); // DEVPKEY_PciDevice_*: 9-12 current/max link speed and width
    static readonly DEVPROPKEY NameKey = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    static readonly DEVPROPKEY DescriptionKey = new(DeviceProps, 2);
    static readonly DEVPROPKEY HardwareIdsKey = new(DeviceProps, 3);
    static readonly DEVPROPKEY CompatibleIdsKey = new(DeviceProps, 4);
    static readonly DEVPROPKEY ServiceKey = new(DeviceProps, 6);
    static readonly DEVPROPKEY ClassGuidKey = new(DeviceProps, 10);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string? filter, IntPtr buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_First_Log_Conf(out nuint logConf, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Next_Res_Des(out nuint resDes, nuint current, uint forResource, out uint resourceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Res_Des_Data_Size(out uint size, nuint resDes, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Res_Des_Data(nuint resDes, byte[] buffer, uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Free_Res_Des_Handle(nuint resDes);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Free_Log_Conf_Handle(nuint logConf);
}
