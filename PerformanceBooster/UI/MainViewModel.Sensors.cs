using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Sensors tab: every sensor of the computer grouped by device, read at the chosen interval while the tab is open; a click on
// a row charts it. The temperature warnings also run in the background (processor and graphics card only, every 6 s).
public sealed partial class MainViewModel
{
    public const int TabSensors = 2;

    readonly SensorHub _sensors = new();
    readonly DispatcherTimer _sensorTimer = new();
    readonly Dictionary<string, SensorRow> _sensorRows = [];
    readonly TemperatureAlert _cpuAlert = new(), _gpuAlert = new();
    bool _sensorBusy;
    string _sensorStatus = "";
    double _cpuMhz = double.NaN;
    double? _cpuTemp, _gpuTemp, _diskTemp; // the latest reading, shown on the Live tab's cards
    SensorRow? _selectedSensor;
    double[] _sensorChart = [];

    public ObservableCollection<SensorDevice> SensorDevices { get; } = [];
    public ICommand SelectSensorCommand { get; private set; } = null!;
    public ICommand ResetSensorStatsCommand { get; private set; } = null!;
    public ICommand CloseSensorChartCommand { get; private set; } = null!;
    public ICommand ToggleSensorDeviceCommand { get; private set; } = null!;

    // A warning for the tray: title and text.
    public event Action<string, string>? TemperatureWarning;

    void InitSensors()
    {
        if (Demo.On) _sensors.Override = Demo.Sensors;
        SensorInterval = new IntervalSetting(_settings.SensorIntervalSeconds);
        SensorInterval.Changed += seconds =>
        {
            _settings.SensorIntervalSeconds = seconds;
            _settings.Save();
            _sensorTimer.Interval = TimeSpan.FromSeconds(seconds);
            Raise(nameof(SensorChartSpan));
        };
        _sensorTimer.Interval = TimeSpan.FromSeconds(SensorInterval.Seconds);
        _sensorTimer.Tick += (_, _) => SensorTick();
        SelectSensorCommand = new RelayCommand(p => { if (p is SensorRow row) SelectedSensor = row == SelectedSensor ? null : row; });
        ResetSensorStatsCommand = new RelayCommand(_ =>
        {
            foreach (var row in _sensorRows.Values) row.ResetStats();
            RaiseChart();
        });
        CloseSensorChartCommand = new RelayCommand(_ => SelectedSensor = null);
        // A device is read only while it is open; opening it reads it at once.
        ToggleSensorDeviceCommand = new RelayCommand(p =>
        {
            if (p is not SensorDevice d) return;
            d.IsExpanded = !d.IsExpanded;
            if (d.IsExpanded && ReadingAllowed) _ = ReadSensorsAsync(light: false);
        });
    }

    public string SensorStatus { get => _sensorStatus; private set => Set(ref _sensorStatus, value); }

    // --- Interval ---

    public IntervalSetting SensorInterval { get; private set; } = null!;

    // --- Warnings ---

    public IReadOnlyList<int> TempLimits { get; } = [70, 75, 80, 85, 87, 90, 95, 100, 105];

    public bool TemperatureWarnings
    {
        get => _settings.TemperatureWarnings;
        set { _settings.TemperatureWarnings = value; _settings.Save(); Raise(nameof(TemperatureWarnings)); }
    }

    public int CpuTempLimit
    {
        get => _settings.CpuTempLimit;
        set { if (value <= 0) return; _settings.CpuTempLimit = value; _settings.Save(); Raise(nameof(CpuTempLimit)); }
    }

    public int GpuTempLimit
    {
        get => _settings.GpuTempLimit;
        set { if (value <= 0) return; _settings.GpuTempLimit = value; _settings.Save(); Raise(nameof(GpuTempLimit)); }
    }

    // --- Chart ---

    public SensorRow? SelectedSensor
    {
        get => _selectedSensor;
        private set
        {
            if (_selectedSensor is { } old) old.IsSelected = false;
            if (!Set(ref _selectedSensor, value)) return;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(HasSensorChart));
            RaiseChart();
        }
    }

    public bool HasSensorChart => SelectedSensor is not null;
    public double[] SensorChart { get => _sensorChart; private set => Set(ref _sensorChart, value); }
    public string SensorChartTitle => SelectedSensor is { } s ? $"{s.Device} · {s.Name}" : "";
    public string SensorChartValues => SelectedSensor is { } s ? F("Now {0} · lowest {1} · highest {2} · average {3}", s.Current, s.Min, s.Max, s.Average) : "";

    public string SensorChartSpan
    {
        get
        {
            double seconds = SensorRow.HistoryLength * _settings.SensorIntervalSeconds;
            return seconds >= 7200 ? F("Last {0} hours", (seconds / 3600).ToString("0.#", L.Culture))
                : seconds >= 120 ? F("Last {0} minutes", (seconds / 60).ToString("0", L.Culture))
                : F("Last {0} seconds", seconds.ToString("0", L.Culture));
        }
    }

    void RaiseChart()
    {
        SensorChart = SelectedSensor?.Series ?? [];
        Raise(nameof(SensorChartTitle));
        Raise(nameof(SensorChartValues));
    }

    // --- Reading ---

    // The tab opened (or the window came back): read at once, then at the interval.
    void StartSensors()
    {
        if (!_visible || SelectedTab != TabSensors) return;
        _sensorTimer.Start();
        if (!_devicesListed) _ = ListSensorDevicesAsync();
        else if (ReadingAllowed) _ = ReadSensorsAsync(light: false);
    }

    bool _devicesListed;

    // The devices are listed folded, without reading their values.
    async Task ListSensorDevicesAsync()
    {
        _devicesListed = true;
        try
        {
            foreach (var d in await Task.Run(_sensors.Devices))
                if (SensorDevices.All(x => x.Id != d.Id))
                    SensorDevices.Insert(SensorDevices.TakeWhile(x => x.Kind <= d.Kind).Count(), new SensorDevice(d.Id, d.Name, d.Kind));
            SensorStatus = SensorNote();
        }
        catch (Exception ex)
        {
            _devicesListed = false;
            SensorStatus = F("Could not read the sensors: {0}", ex.Message);
        }
    }

    void SensorTick()
    {
        if (!_visible || SelectedTab != TabSensors)
        {
            _sensorTimer.Stop();
            return;
        }
        if (ReadingAllowed) _ = ReadSensorsAsync(light: false); // in the background or while a game runs: nothing is read
    }

    // The open devices of the Sensors tab (null: everything, for the background readings).
    HashSet<string>? OpenDevices(bool light) => light ? null : SensorDevices.Where(d => d.IsExpanded).Select(d => d.Id).ToHashSet();

    // From the 3-second background timer: every other tick, unless the open tab already reads everything.
    void CheckTemperaturesInBackground()
    {
        if (!_settings.TemperatureWarnings || (_visible && SelectedTab == TabSensors) || _backgroundTick % 2 != 0) return;
        _ = ReadSensorsAsync(light: true);
    }

    // Live tab: the temperatures at most every second, the disks' every 10 s (they answer more slowly).
    void ReadTemperaturesForLive()
    {
        var now = DateTime.UtcNow;
        if (SelectedTab != TabLive || now - _tempsAt < TimeSpan.FromSeconds(1)) return;
        _tempsAt = now;
        bool disks = now - _diskTempAt >= TimeSpan.FromSeconds(10);
        if (disks) _diskTempAt = now;
        _ = ReadSensorsAsync(light: true, storage: disks);
    }

    static string Celsius(double? t) => t is { } v ? $"{v:0}°C" : "";

    async Task ReadSensorsAsync(bool light, bool storage = false)
    {
        if (_sensorBusy) return;
        var open = OpenDevices(light);
        if (open is { Count: 0 }) return; // every device folded: nothing to read
        _sensorBusy = true;
        try
        {
            var readings = await Task.Run(() => _sensors.Read(light, storage, open));
            _cpuTemp = SensorFormat.DeviceTemperature(readings, DeviceKind.Processor) ?? _cpuTemp;
            _gpuTemp = SensorFormat.DeviceTemperature(readings, DeviceKind.Graphics) ?? _gpuTemp;
            if (storage) _diskTemp = SensorFormat.DeviceTemperature(readings, DeviceKind.Storage);
            if (!light) ApplySensors(WithAverageClock(readings));
            CheckTemperatures(readings);
        }
        catch (Exception ex)
        {
            if (!light) SensorStatus = F("Could not read the sensors: {0}", ex.Message);
        }
        finally
        {
            _sensorBusy = false;
        }
    }

    void ApplySensors(IReadOnlyList<SensorReading> readings)
    {
        foreach (var r in readings)
        {
            if (!_sensorRows.TryGetValue(r.Id, out var row))
            {
                if (r.Value is null) continue; // shown once it reports something
                row = new SensorRow(r);
                _sensorRows[r.Id] = row;
                Device(r).Section(r.Kind == SensorKind.SmallData ? SensorKind.Data : r.Kind).Rows.Add(row);
            }
            row.Add(r.Value);
        }
        if (SelectedSensor is not null) RaiseChart();
        SensorStatus = SensorNote();
    }

    // Without the processor's own clock sensors: the clocks Windows counts, on average and per logical processor (the Live
    // tab samples them every second).
    IReadOnlyList<SensorReading> WithAverageClock(IReadOnlyList<SensorReading> readings)
    {
        if (Demo.On || double.IsNaN(_cpuMhz) || readings.Any(r => r.DeviceKind == DeviceKind.Processor && r.Kind == SensorKind.Clock && r.Value is not null))
            return readings;
        if (readings.FirstOrDefault(r => r.DeviceKind == DeviceKind.Processor) is not { } processor) return readings;
        var list = readings.ToList();
        list.Add(new("/counters/cpu/clock", processor.DeviceId, processor.Device, DeviceKind.Processor, SensorKind.Clock, "CPU (average)", _cpuMhz));
        var threads = _counters.IsValueCreated ? _counters.Value.ThreadMhz : [];
        for (int i = 0; i < threads.Length; i++)
            list.Add(new($"/counters/cpu/clock/{i}", processor.DeviceId, processor.Device, DeviceKind.Processor, SensorKind.Clock, $"CPU Thread #{i + 1}", threads[i]));
        return list;
    }

    SensorDevice Device(SensorReading r)
    {
        if (SensorDevices.FirstOrDefault(d => d.Id == r.DeviceId) is { } found) return found;
        var device = new SensorDevice(r.DeviceId, r.Device, r.DeviceKind);
        SensorDevices.Insert(SensorDevices.TakeWhile(d => d.Kind <= r.DeviceKind).Count(), device);
        return device;
    }

    // Under the table: how many devices there are.
    string SensorNote() => F("{0} device|{0} devices", SensorDevices.Count);

    void CheckTemperatures(IReadOnlyList<SensorReading> readings)
    {
        if (!_settings.TemperatureWarnings) return;
        var now = DateTime.UtcNow;
        Warn(DeviceKind.Processor, _cpuAlert, _settings.CpuTempLimit);
        Warn(DeviceKind.Graphics, _gpuAlert, _settings.GpuTempLimit);

        void Warn(DeviceKind kind, TemperatureAlert alert, int limit)
        {
            var temp = SensorFormat.DeviceTemperature(readings, kind);
            if (!alert.Check(temp, limit, now)) return;
            string device = readings.First(r => r.DeviceKind == kind).Device;
            TemperatureWarning?.Invoke(T("High temperature"), F("{0} reached {1} (limit {2} °C).", device, SensorFormat.Value(SensorKind.Temperature, temp!.Value), limit));
        }
    }
}
