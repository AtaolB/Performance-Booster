using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

// The kinds of hardware on the Sensors tab, in the order they are shown.
public enum DeviceKind { Processor, Graphics, Motherboard, Controller, Memory, Storage, Network, Battery, Cooler, PowerSupply, Other }

// Same names as the sensor types of the hardware library, so they map by name.
public enum SensorKind
{
    Temperature, Voltage, Current, Power, Clock, Load, Frequency, Fan, Flow, Control, Level, Factor, Data, SmallData, Throughput,
    TimeSpan, Timing, Energy, Noise, Conductivity, Humidity, Other,
}

// A device on the Sensors tab, known before any of its values is read.
public sealed record SensorDeviceInfo(string Id, string Name, DeviceKind Kind);

// One sensor value. Id stays the same between readings and runs; Value is null while the sensor reports nothing.
public sealed record SensorReading(string Id, string DeviceId, string Device, DeviceKind DeviceKind, SensorKind Kind, string Name, double? Value);

// Reads every sensor of the computer that Windows makes available without a driver of its own: the graphics card, disks,
// memory, battery, network, processor load, and on Uniwill/TongFang laptops the processor temperature and fans (LaptopSensors).
// Opening takes about half a second and happens on the first reading, which runs on a background thread. The processor's
// own temperature, clock and power sensors and the motherboard chips need a kernel driver; the app installs none, but they
// appear when the PawnIO driver is already on the computer (another program installed it).
public sealed class SensorHub : IDisposable
{
    readonly object _lock = new();
    readonly LaptopSensors _laptop = new();
    Computer? _computer;
    HashSet<string>? _activeNics;
    DateTime _nicsAt;

    // Tests and the demo give their own readings.
    internal Func<bool, IReadOnlyList<SensorReading>>? Override { get; set; }

    // Another program installed the PawnIO driver: the processor's own sensors are read through it.
    public static bool DriverInstalled =>
        Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", "DisplayName") is not null;

    public const string LaptopFansId = "/laptop/fans";

    // The devices that have sensors, without reading any value: the Sensors tab lists them folded and reads only the open ones.
    public IReadOnlyList<SensorDeviceInfo> Devices()
    {
        if (Override is { } fake)
            return [.. fake(false).GroupBy(r => r.DeviceId).Select(g => new SensorDeviceInfo(g.Key, g.First().Device, g.First().DeviceKind))];
        lock (_lock)
        {
            _computer ??= Open();
            var list = new List<SensorDeviceInfo>();
            void Add(IHardware hardware, DeviceKind kind)
            {
                if (hardware.Sensors.Length > 0) list.Add(new(hardware.Identifier.ToString(), hardware.Name, kind));
                foreach (var sub in hardware.SubHardware) Add(sub, kind == DeviceKind.Motherboard ? DeviceKind.Controller : kind);
            }
            foreach (var hardware in _computer.Hardware)
            {
                var kind = Kind(hardware.HardwareType);
                if (kind == DeviceKind.Network && !ActiveNic(hardware.Name)) continue;
                Add(hardware, kind);
            }
            if (_laptop.Available) list.Add(new(LaptopFansId, _laptop.Model, DeviceKind.Cooler));
            return list;
        }
    }

    // light: only the processor and graphics cards (the background check of the temperature warnings, also while a game runs),
    // plus the disks when storage is set (the Live tab shows their temperature). devices: only these are read (the open ones).
    public IReadOnlyList<SensorReading> Read(bool light = false, bool storage = false, IReadOnlySet<string>? devices = null)
    {
        if (Override is { } fake) return devices is null ? fake(light) : [.. fake(light).Where(r => devices.Contains(r.DeviceId))];
        lock (_lock)
        {
            _computer ??= Open();
            var list = new List<SensorReading>();
            foreach (var hardware in _computer.Hardware)
            {
                var kind = Kind(hardware.HardwareType);
                if (light && kind is not (DeviceKind.Processor or DeviceKind.Graphics) && !(storage && kind == DeviceKind.Storage)) continue;
                if (kind == DeviceKind.Network && !ActiveNic(hardware.Name)) continue;
                Collect(hardware, kind, list, devices);
            }
            AddLaptop(list, light, devices);
            return list;
        }
    }

    // The laptop's controller: the processor temperature with the processor, the fans as a device of their own, and the
    // graphics card's temperature only when the card itself reports none (switched off while idle on a laptop). Only the
    // values of devices being read are asked for.
    void AddLaptop(List<SensorReading> list, bool light, IReadOnlySet<string>? devices)
    {
        if (!_laptop.Available) return;
        var processor = list.FirstOrDefault(r => r.DeviceKind == DeviceKind.Processor);
        var graphics = list.FirstOrDefault(r => r.DeviceKind == DeviceKind.Graphics);
        bool fans = !light && (devices is null || devices.Contains(LaptopFansId));
        bool gpuTemp = !light && graphics is not null && !list.Any(r => r.DeviceKind == DeviceKind.Graphics && r.Kind == SensorKind.Temperature && r.Value is not null);
        var (cpu, gpu, fan1, fan2) = _laptop.Read(cpu: processor is not null || devices is null, gpu: gpuTemp, fans: fans);
        if (cpu is not null)
            list.Add(new("/laptop/temperature/cpu", processor?.DeviceId ?? "/laptop/cpu", processor?.Device ?? _laptop.Model, DeviceKind.Processor, SensorKind.Temperature, "CPU", cpu));
        if (gpu is not null)
            list.Add(new("/laptop/temperature/gpu", graphics!.DeviceId, graphics.Device, DeviceKind.Graphics, SensorKind.Temperature, "GPU", gpu));
        if (!fans) return;
        list.Add(new("/laptop/fan/1", LaptopFansId, _laptop.Model, DeviceKind.Cooler, SensorKind.Control, "Fan #1", fan1));
        list.Add(new("/laptop/fan/2", LaptopFansId, _laptop.Model, DeviceKind.Cooler, SensorKind.Control, "Fan #2", fan2));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _computer?.Close();
            _computer = null;
        }
    }

    public void Dispose() => Reset();

    static Computer Open()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true,
            IsStorageEnabled = true, IsNetworkEnabled = true, IsBatteryEnabled = true, IsPsuEnabled = true,
        };
        computer.Open();
        return computer;
    }

    static void Collect(IHardware hardware, DeviceKind kind, List<SensorReading> list, IReadOnlySet<string>? devices)
    {
        string deviceId = hardware.Identifier.ToString();
        // Chips below the motherboard (sensor chip, embedded controller) and the like: shown as devices of their own.
        foreach (var sub in hardware.SubHardware) Collect(sub, kind == DeviceKind.Motherboard ? DeviceKind.Controller : kind, list, devices);
        if (devices is not null && !devices.Contains(deviceId)) return; // folded on the Sensors tab: not read
        hardware.Update();
        var seen = new Dictionary<string, int>();
        foreach (var s in hardware.Sensors)
        {
            double? value = s.Value is float v && float.IsFinite(v) ? v : null;
            var sensorKind = Kind(s.SensorType);
            // Without the driver the processor's energy counters cannot be read and report 0 W instead of nothing.
            if (kind == DeviceKind.Processor && sensorKind == SensorKind.Power && value == 0) value = null;
            // Several engines of the graphics card share a name ("D3D Copy"): numbered, so the rows can be told apart.
            int n = seen[s.Name] = seen.GetValueOrDefault(s.Name) + 1;
            string name = hardware.Sensors.Count(x => x.Name == s.Name) > 1 ? $"{s.Name} #{n}" : s.Name;
            list.Add(new(s.Identifier.ToString(), deviceId, hardware.Name, kind, sensorKind, name, value));
        }
    }

    static SensorKind Kind(SensorType type) => Enum.TryParse<SensorKind>(type.ToString(), out var k) ? k : SensorKind.Other;

    internal static DeviceKind Kind(HardwareType type) => type switch
    {
        HardwareType.Cpu => DeviceKind.Processor,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => DeviceKind.Graphics,
        HardwareType.Motherboard => DeviceKind.Motherboard,
        HardwareType.SuperIO or HardwareType.EmbeddedController => DeviceKind.Controller,
        HardwareType.Memory => DeviceKind.Memory,
        HardwareType.Storage => DeviceKind.Storage,
        HardwareType.Network => DeviceKind.Network,
        HardwareType.Battery => DeviceKind.Battery,
        HardwareType.Cooler => DeviceKind.Cooler,
        HardwareType.Psu => DeviceKind.PowerSupply,
        _ => DeviceKind.Other,
    };

    // Windows also lists filter drivers and virtual adapters as network devices; only connected adapters with a gateway count.
    bool ActiveNic(string name)
    {
        if (_activeNics is null || DateTime.UtcNow - _nicsAt > TimeSpan.FromSeconds(30))
        {
            _activeNics = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                    if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
                        _activeNics.Add(n.Name);
            }
            catch (NetworkInformationException) { }
            _nicsAt = DateTime.UtcNow;
        }
        return _activeNics.Contains(name);
    }
}

// Units and number formats, and which values the temperature warnings watch.
public static class SensorFormat
{
    public static string Value(SensorKind kind, double v) => kind switch
    {
        SensorKind.Temperature => $"{v.ToString("0.0", L.Culture)} °C",
        SensorKind.Voltage => $"{v.ToString("0.000", L.Culture)} V",
        SensorKind.Current => $"{v.ToString("0.000", L.Culture)} A",
        SensorKind.Power => $"{v.ToString("0.0", L.Culture)} W",
        SensorKind.Clock => $"{v.ToString("0", L.Culture)} MHz",
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => $"{v.ToString("0.0", L.Culture)} %",
        SensorKind.Frequency => $"{v.ToString("0", L.Culture)} Hz",
        SensorKind.Fan => $"{v.ToString("0", L.Culture)} RPM",
        SensorKind.Flow => $"{v.ToString("0.0", L.Culture)} L/h",
        SensorKind.Data => $"{v.ToString("0.00", L.Culture)} GB",
        SensorKind.SmallData => $"{v.ToString("0", L.Culture)} MB",
        SensorKind.Throughput => v >= 1_000_000 ? $"{(v / 1_000_000).ToString("0.0", L.Culture)} MB/s" : $"{(v / 1000).ToString("0.0", L.Culture)} KB/s",
        SensorKind.TimeSpan => System.TimeSpan.FromSeconds(Math.Max(0, v)).ToString(v >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", L.Culture),
        SensorKind.Timing => $"{v.ToString("0.0", L.Culture)} ns",
        SensorKind.Energy => $"{v.ToString("0", L.Culture)} mWh",
        SensorKind.Noise => $"{v.ToString("0", L.Culture)} dBA",
        SensorKind.Conductivity => $"{v.ToString("0.0", L.Culture)} µS/cm",
        _ => v.ToString("0.###", L.Culture),
    };

    // Drives report their warning and critical limits as temperatures; the processor its distance to the maximum.
    public static bool IsLimit(SensorReading r) =>
        r.Kind == SensorKind.Temperature && (r.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                             || r.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                                             || r.Name.Contains("Distance to", StringComparison.OrdinalIgnoreCase)
                                             || r.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase));

    // The temperature of a device that a warning compares with its limit: the graphics core when there is one (the hot spot
    // and memory run hotter by design), otherwise the highest real temperature of the device.
    public static double? DeviceTemperature(IEnumerable<SensorReading> readings, DeviceKind kind)
    {
        var temps = readings.Where(r => r.DeviceKind == kind && r.Kind == SensorKind.Temperature && r.Value is not null && !IsLimit(r)).ToList();
        if (temps.Count == 0) return null;
        if (kind == DeviceKind.Graphics && temps.FirstOrDefault(r => r.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) is { } core) return core.Value;
        return temps.Max(r => r.Value);
    }
}

// A warning fires when a value stays at or above its limit for two readings in a row; it fires again only after the value
// dropped 5 °C below the limit and at least 15 minutes passed.
public sealed class TemperatureAlert
{
    int _above;
    bool _fired;
    DateTime _firedAt = DateTime.MinValue;

    public bool Check(double? value, double limit, DateTime now)
    {
        if (value is not { } v) return false;
        if (v < limit)
        {
            _above = 0;
            if (_fired && v <= limit - 5) _fired = false;
            return false;
        }
        _above++;
        if (_fired || _above < 2 || now - _firedAt < TimeSpan.FromMinutes(15)) return false;
        _fired = true;
        _firedAt = now;
        return true;
    }
}
