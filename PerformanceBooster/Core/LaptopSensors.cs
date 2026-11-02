using System.Management;

namespace PerformanceBooster.Core;

// Laptops built on the Uniwill/TongFang platform (sold as Monster, TUXEDO, XMG, Eluktronics and others) report the
// processor temperature and the fan levels of their embedded controller through an ACPI WMI method that Windows provides
// without any driver. The method can also write controller memory, so only the read function is ever sent (byte 5 = 1),
// and only on a computer that has all six WMI interfaces of that platform and the maker is a known Uniwill/TongFang brand:
// the interface names come from a Windows sample that other makers may use with a different meaning.
public sealed class LaptopSensors
{
    const string Namespace = @"root\WMI";
    const ulong ReadFunction = 1UL << 40;
    const uint Failed = 0xFEFEFEFE;

    // Register addresses of the controller's memory (the same as the Linux driver of these laptops uses).
    const ushort CpuTemp = 0x043E, GpuTemp = 0x044F, Fan1 = 0x1804, Fan2 = 0x1809;

    static readonly string[] Interfaces =
    [
        "ABBC0F6D", "ABBC0F6E", "ABBC0F6F", // methods
        "ABBC0F70", "ABBC0F71", "ABBC0F72", // events
    ];

    static readonly string[] Makers =
        ["Uniwill", "TongFang", "Monster", "TUXEDO", "Schenker", "XMG", "Eluktronics", "Mechrevo", "Machenike", "Avell", "PCSpecialist", "Thunderobot"];

    ManagementObject? _method;
    bool _checked;

    public string Model { get; private set; } = "";

    // Present when this computer is one of these laptops; checked once.
    public bool Available
    {
        get
        {
            if (!_checked)
            {
                _checked = true;
                try { _method = Detect(); }
                catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { _method = null; }
            }
            return _method is not null;
        }
    }

    ManagementObject? Detect()
    {
        string maker = "";
        using (var search = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in search.Get())
                using (o)
                {
                    maker = o["Manufacturer"] as string ?? "";
                    Model = (o["Model"] as string ?? "").Trim();
                }
        using (var board = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_BaseBoard"))
            foreach (ManagementObject o in board.Get())
                using (o) maker += " " + (o["Manufacturer"] as string ?? "");
        if (!Makers.Any(m => maker.Contains(m, StringComparison.OrdinalIgnoreCase))) return null;

        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var classes = new ManagementObjectSearcher(Namespace, "SELECT * FROM meta_class WHERE __CLASS LIKE 'AcpiTest_%'"))
            foreach (ManagementClass c in classes.Get())
                using (c)
                    foreach (QualifierData q in c.Qualifiers)
                        if (q.Name.Equals("guid", StringComparison.OrdinalIgnoreCase) && q.Value is string g && g.Length >= 9)
                            guids.Add(g.Trim('{')[..8]);
        if (!Interfaces.All(guids.Contains)) return null;

        using var instances = new ManagementObjectSearcher(Namespace, "SELECT * FROM AcpiTest_MULong");
        var method = instances.Get().Cast<ManagementObject>().OrderBy(o => o["InstanceName"] as string, StringComparer.Ordinal).FirstOrDefault();
        // The first reading must look like a temperature, or the interface is not used.
        if (method is null || Read(method, CpuTemp) is not (> 0 and < 125)) return null;
        return method;
    }

    static uint? Read(ManagementObject method, ushort address)
    {
        using var input = method.GetMethodParameters("GetSetULong");
        input["Data"] = address | ReadFunction;
        using var output = method.InvokeMethod("GetSetULong", input, null);
        return output?["Return"] is uint value && value != Failed ? value & 0xFF : null;
    }

    double? Value(ushort address, uint max)
    {
        try { return Read(_method!, address) is { } v && v <= max ? v : null; }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException) { return null; }
    }

    // The processor temperature (°C) and both fans as a share of full speed (the controller counts 0 to 200); only the
    // values asked for are read.
    public (double? CpuTemp, double? GpuTemp, double? Fan1Percent, double? Fan2Percent) Read(bool cpu = true, bool gpu = true, bool fans = true)
    {
        if (!Available) return (null, null, null, null);
        return (cpu ? Value(CpuTemp, 125) : null, gpu ? Value(GpuTemp, 125) : null, fans ? Value(Fan1, 200) / 2 : null, fans ? Value(Fan2, 200) / 2 : null);
    }
}
