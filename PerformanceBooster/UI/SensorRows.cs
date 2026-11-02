using System.Collections.ObjectModel;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// One sensor on the Sensors tab: the current value and the lowest, highest and average since the tab opened (or Reset).
public sealed class SensorRow(SensorReading reading) : ObservableObject
{
    public const int HistoryLength = 300;

    readonly History _history = new(HistoryLength);
    double _min = double.NaN, _max = double.NaN, _sum;
    long _count;
    string _current = "–", _minText = "–", _maxText = "–", _average = "–";
    bool _isSelected;

    public string Id { get; } = reading.Id;
    public string Name { get; } = reading.Name;
    public string Device { get; } = reading.Device;
    public SensorKind Kind { get; } = reading.Kind;
    public double? Value { get; private set; }

    public string Current { get => _current; private set => Set(ref _current, value); }
    public string Min { get => _minText; private set => Set(ref _minText, value); }
    public string Max { get => _maxText; private set => Set(ref _maxText, value); }
    public string Average { get => _average; private set => Set(ref _average, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    // Charted as it was read; a missing value leaves a gap.
    public double[] Series => _history.Snapshot();

    public void Add(double? value)
    {
        Value = value;
        _history.Add(value ?? double.NaN);
        if (value is not { } v)
        {
            Current = "–";
            return;
        }
        _min = double.IsNaN(_min) ? v : Math.Min(_min, v);
        _max = double.IsNaN(_max) ? v : Math.Max(_max, v);
        _sum += v;
        _count++;
        Current = SensorFormat.Value(Kind, v);
        Min = SensorFormat.Value(Kind, _min);
        Max = SensorFormat.Value(Kind, _max);
        Average = SensorFormat.Value(Kind, _sum / _count);
    }

    public void ResetStats()
    {
        (_min, _max, _sum, _count) = (double.NaN, double.NaN, 0, 0);
        Min = Max = Average = "–";
        if (Value is { } v) Add(v);
    }
}

// The sensors of one kind within a device ("Temperatures", "Fans"...).
public sealed class SensorSection(SensorKind kind)
{
    public SensorKind Kind { get; } = kind;
    public string Title { get; } = SensorLabels.Section(kind);
    public ObservableCollection<SensorRow> Rows { get; } = [];
}

// A device (processor, graphics card, disk...) with its sensors grouped by kind; it can be folded.
public sealed class SensorDevice(string id, string name, DeviceKind kind) : ObservableObject
{
    bool _expanded; // folded until clicked: a folded device is not read

    public string Id { get; } = id;
    public string Name { get; } = name;
    public DeviceKind Kind { get; } = kind;
    public string KindText { get; } = SensorLabels.Device(kind);
    public string Icon { get; } = SensorLabels.Icon(kind);
    public ObservableCollection<SensorSection> Sections { get; } = [];
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    public SensorSection Section(SensorKind kind)
    {
        if (Sections.FirstOrDefault(s => s.Kind == kind) is { } found) return found;
        var section = new SensorSection(kind);
        int index = Sections.TakeWhile(s => s.Kind < kind).Count();
        Sections.Insert(index, section);
        return section;
    }
}

public static class SensorLabels
{
    public static string Device(DeviceKind kind) => kind switch
    {
        DeviceKind.Processor => T("Processor"),
        DeviceKind.Graphics => T("Graphics card"),
        DeviceKind.Motherboard => T("Motherboard"),
        DeviceKind.Controller => T("Motherboard sensors"),
        DeviceKind.Memory => T("Memory"),
        DeviceKind.Storage => T("Disk"),
        DeviceKind.Network => T("Network"),
        DeviceKind.Battery => T("Battery"),
        DeviceKind.Cooler => T("Cooling"),
        DeviceKind.PowerSupply => T("Power supply"),
        _ => T("Other"),
    };

    public static string Icon(DeviceKind kind) => kind switch
    {
        DeviceKind.Processor => "",
        DeviceKind.Graphics => "",
        DeviceKind.Motherboard or DeviceKind.Controller => "",
        DeviceKind.Memory => "",
        DeviceKind.Storage => "",
        DeviceKind.Network => "",
        DeviceKind.Battery => "",
        DeviceKind.Cooler => "",
        DeviceKind.PowerSupply => "",
        _ => "",
    };

    public static string Section(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => T("Temperatures"),
        SensorKind.Voltage => T("Voltages"),
        SensorKind.Current => T("Currents"),
        SensorKind.Power => T("Power"),
        SensorKind.Clock => T("Clocks"),
        SensorKind.Load => T("Load"),
        SensorKind.Frequency => T("Frequencies"),
        SensorKind.Fan => T("Fans"),
        SensorKind.Flow => T("Flow"),
        SensorKind.Control => T("Fan speed"),
        SensorKind.Level => T("Levels"),
        SensorKind.Factor => T("Counters"),
        SensorKind.Data or SensorKind.SmallData => T("Data"),
        SensorKind.Throughput => T("Throughput"),
        SensorKind.TimeSpan => T("Time"),
        SensorKind.Timing => T("Timings"),
        SensorKind.Energy => T("Energy"),
        SensorKind.Noise => T("Noise"),
        SensorKind.Conductivity => T("Conductivity"),
        SensorKind.Humidity => T("Humidity"),
        _ => T("Other"),
    };
}
