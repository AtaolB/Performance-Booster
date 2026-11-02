using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed record DriverLatencyRow(string Module, string Description, string Dpcs, string Isrs, string Highest, string Total, Brush Brush);

public sealed record InterruptRow(string Name, string Mode, string Detail, Brush Brush);

// One processor in the per-core grid: its share of the busiest processor's driver time (the bar), time and counts.
public sealed record CpuLatencyRow(string Name, double Share, string Total, string Counts, Brush Brush);

// One bar of the DPC time distribution.
public sealed record HistogramRow(string Range, double Share, string Percent, Brush Brush);

// Latency tab: driver latency, mouse polling and the interrupts of the devices that matter in games (MSI mode, shared IRQs).
public sealed partial class MainViewModel
{
    public const int TabLatency = 9;

    public ObservableCollection<DriverLatencyRow> DriverLatencies { get; } = [];
    public ObservableCollection<InterruptRow> Interrupts { get; } = [];
    public ObservableCollection<CpuLatencyRow> CpuLatencies { get; } = [];
    public ObservableCollection<HistogramRow> DpcHistogramRows { get; } = [];
    public IReadOnlyList<int> LatencyDurations { get; } = LatencyTrace.Durations;

    public ICommand MeasureLatencyCommand { get; private set; } = null!;
    public ICommand MouseTestCommand { get; private set; } = null!;
    public ICommand OpenInterruptSettingsCommand { get; private set; } = null!;

    int _latencyDuration = 30;
    double _latencyProgress;
    bool _measuring, _hasLatency, _mouseTesting;
    string _latencyStatus = T("Not measured yet."), _maxDpcText = "–", _maxDpcModule = "", _maxIsrText = "–", _maxIsrModule = "", _latencyVerdict = "",
        _mouseRate = "–", _mouseResult = "", _mousePorts = "";
    EventWaitHandle? _latencyStop;
    Brush _latencyBrush = Palette.Muted, _mouseBrush = Palette.Muted;
    IReadOnlyList<double> _mouseSeries = [];
    IReadOnlyList<SharedIrq> _sharedIrqs = [];
    IReadOnlyList<Devices.MousePort> _mice = [];
    HashSet<string> _gameDevices = [];

    public int SelectedLatencyDuration { get => _latencyDuration; set => Set(ref _latencyDuration, value); }
    public double LatencyProgress { get => _latencyProgress; private set => Set(ref _latencyProgress, value); }
    public bool Measuring
    {
        get => _measuring;
        private set
        {
            if (!Set(ref _measuring, value)) return;
            Raise(nameof(MeasureButtonText));
            Raise(nameof(NotMeasuring));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool NotMeasuring => !Measuring;
    public string MeasureButtonText => Measuring ? T("Stop and show the results") : T("Start measuring");
    public bool HasLatency { get => _hasLatency; private set => Set(ref _hasLatency, value); }
    public string LatencyStatus { get => _latencyStatus; private set => Set(ref _latencyStatus, value); }
    public string MaxDpcText { get => _maxDpcText; private set => Set(ref _maxDpcText, value); }
    public string MaxDpcModule { get => _maxDpcModule; private set => Set(ref _maxDpcModule, value); }
    public string MaxIsrText { get => _maxIsrText; private set => Set(ref _maxIsrText, value); }
    public string MaxIsrModule { get => _maxIsrModule; private set => Set(ref _maxIsrModule, value); }
    public string LatencyVerdict { get => _latencyVerdict; private set => Set(ref _latencyVerdict, value); }
    public Brush LatencyBrush { get => _latencyBrush; private set => Set(ref _latencyBrush, value); }

    public bool MouseTesting { get => _mouseTesting; private set { if (Set(ref _mouseTesting, value)) Raise(nameof(MouseTestButton)); } }
    public string MouseTestButton => MouseTesting ? T("Stop") : T("Start the test");
    public string MouseRateText { get => _mouseRate; private set => Set(ref _mouseRate, value); }
    public string MouseResultText { get => _mouseResult; private set => Set(ref _mouseResult, value); }
    public Brush MouseResultBrush { get => _mouseBrush; private set => Set(ref _mouseBrush, value); }
    public IReadOnlyList<double> MouseSeries { get => _mouseSeries; private set => Set(ref _mouseSeries, value); }
    public string MousePortsText { get => _mousePorts; private set => Set(ref _mousePorts, value); }

    void InitLatency()
    {
        // The same button starts the measurement and stops it early (the results so far are shown).
        MeasureLatencyCommand = new RelayCommand(async _ =>
        {
            if (Measuring) StopLatency();
            else await MeasureLatencyAsync();
        }, _ => Measuring || !Busy);
        MouseTestCommand = new RelayCommand(_ =>
        {
            if (MouseTesting) StopMouseTest();
            else StartMouseTest();
        });
        OpenInterruptSettingsCommand = new RelayCommand(_ =>
        {
            OnlyDifferent = false; // MSI rows already on are shown too
            SelectedTab = TabTweaks;
        });
    }

    // --- Driver latency ---

    async Task MeasureLatencyAsync()
    {
        if (Demo.On)
        {
            Measuring = true;
            await Task.Delay(1500);
            ShowLatency(Demo.Latency());
            Measuring = false;
            return;
        }
        int seconds = SelectedLatencyDuration;
        var file = Path.Combine(AppPaths.Folder, "latency.json");
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        Measuring = Busy = true;
        LatencyStatus = F("Measuring for up to {0} s… Use the computer as usual or play a game meanwhile; Stop shows the results so far.", seconds);
        var clock = Stopwatch.StartNew();
        var progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progress.Tick += (_, _) => LatencyProgress = Math.Min(100, clock.Elapsed.TotalSeconds * 100 / (seconds + 2));
        progress.Start();
        _latencyStop = new EventWaitHandle(false, EventResetMode.ManualReset, LatencyTrace.StopEventName);
        _latencyStop.Reset();
        try
        {
            var outcome = await Elevated.RunAsync("--latency", seconds.ToString(CultureInfo.InvariantCulture), file, LatencyTrace.StopEventName);
            if (!outcome.Started)
            {
                LatencyStatus = T("Cancelled: administrator permission was not granted.");
                return;
            }
            var result = AppPaths.ReadJson(file, AppJson.Default.LatencyResult);
            if (result?.Error is { } error) LatencyStatus = error;
            else if (result is null) LatencyStatus = outcome.Log.Count > 0 ? outcome.Log[^1] : T("The measurement failed.");
            else ShowLatency(result);
        }
        finally
        {
            progress.Stop();
            LatencyProgress = 0;
            _latencyStop.Dispose();
            _latencyStop = null;
            Measuring = Busy = false;
        }
    }

    void StopLatency()
    {
        LatencyStatus = T("Stopping…");
        _latencyStop?.Set();
    }

    static string Us(double us) => F("{0} µs", us.ToString(us < 10 ? "0.0" : "0", L.Culture));

    // 1234567 -> "1.2M", 12345 -> "12.3k".
    static string Compact(long n) => n >= 1_000_000 ? (n / 1e6).ToString("0.#", L.Culture) + "M" : n >= 10_000 ? (n / 1e3).ToString("0.#", L.Culture) + "k" : n.ToString("N0", L.Culture);

    static Brush LatencyLevel(double us) => us >= 1000 ? Palette.Bad : us >= 500 ? Palette.Warn : Palette.Ok;

    void ShowLatency(LatencyResult r)
    {
        HasLatency = true;
        MaxDpcText = Us(r.MaxDpcUs);
        MaxDpcModule = r.MaxDpcModule ?? "";
        MaxIsrText = Us(r.MaxIsrUs);
        MaxIsrModule = r.MaxIsrModule ?? "";
        double worst = Math.Max(r.MaxDpcUs, r.MaxIsrUs);
        string worstModule = (r.MaxDpcUs >= r.MaxIsrUs ? r.MaxDpcModule : r.MaxIsrModule) ?? "?";
        LatencyBrush = LatencyLevel(worst);
        LatencyVerdict = worst >= 1000
            ? F("{0} ran for {1} at once. This can cause stutter, crackling sound or late input. Update or reinstall this driver; turning off its device's power saving often helps.", worstModule, Us(worst))
            : worst >= 500
                ? T("Some driver work ran longer than 0.5 ms. Usually fine, but measure again while gaming to see if it grows.")
                : T("Good: no driver ran long enough to cause stutter.");
        LatencyStatus = F("Measured for {0} s: {1} DPCs and {2} interrupts.", r.Seconds.ToString("0", L.Culture), r.Dpcs.ToString("N0", L.Culture), r.Isrs.ToString("N0", L.Culture))
                        + (r.EventsLost > 0 ? " " + F("({0} events were dropped, the results may miss some.)", r.EventsLost) : "");

        // DPC time distribution: one bar per range, the longer ranges in warning colors. Bars are scaled to the largest range;
        // the long ranges are rare, so a range with any DPCs always shows a sliver.
        long total = Math.Max(1, r.DpcHistogram.Sum());
        long largest = Math.Max(1, r.DpcHistogram.DefaultIfEmpty(0).Max());
        var bounds = LatencyTrace.Buckets;
        DpcHistogramRows.Clear();
        for (int i = 0; i < r.DpcHistogram.Length; i++)
        {
            string range = i == 0 ? F("under {0}", Us(bounds[0])) : i == bounds.Length ? F("{0} and more", Us(bounds[^1])) : $"{bounds[i - 1]:0}–{Us(bounds[i])}";
            long count = r.DpcHistogram[i];
            double share = count * 100.0 / total;
            string percent = count == 0 ? "0%" : share < 0.01 ? "<0.01%" : share.ToString(share < 1 ? "0.##" : "0.#", L.Culture) + "%";
            double upper = i < bounds.Length ? bounds[i] : bounds[^1] * 2;
            DpcHistogramRows.Add(new HistogramRow(range, count == 0 ? 0 : Math.Max(2, count * 100.0 / largest), percent, LatencyLevel(upper)));
        }

        // Per processor: a tile with a bar relative to the busiest processor.
        double busiest = Math.Max(0.001, r.Cpus.Select(c => c.TotalMs).DefaultIfEmpty(0).Max());
        CpuLatencies.Clear();
        foreach (var c in r.Cpus)
            CpuLatencies.Add(new CpuLatencyRow(F("CPU {0}", c.Cpu), c.TotalMs * 100 / busiest, F("{0} ms", c.TotalMs.ToString("0.#", L.Culture)),
                F("{0} DPC · {1} ISR", Compact(c.Dpcs), Compact(c.Isrs)), c.TotalMs >= busiest * 0.999 && r.Cpus.Count > 1 ? Palette.Warn : Palette.Info));

        DriverLatencies.Clear();
        foreach (var d in r.Drivers.Take(20))
            DriverLatencies.Add(new DriverLatencyRow(d.Module, d.Description ?? (d.Module == "?" ? T("unknown code") : ""),
                d.Dpcs.ToString("N0", L.Culture), d.Isrs.ToString("N0", L.Culture), Us(d.MaxUs), F("{0} ms", d.TotalMs.ToString("0.##", L.Culture)), LatencyLevel(d.MaxUs)));
    }

    // --- Interrupts of the devices that matter in games ---

    async Task RefreshInterruptsAsync()
    {
        if (Demo.On)
        {
            DemoInterrupts();
            return;
        }
        try
        {
            var (rows, shared, mice, relevant) = await Task.Run(() =>
            {
                var all = Devices.Snapshot();
                var relevantDevices = all.Where(Catalog.InterruptRelevant).ToList();
                var sharing = Devices.SharedIrqs(all);
                var list = relevantDevices.Select(d =>
                {
                    var irqs = d.Irqs;
                    if (d.Mode == InterruptMode.Msi)
                        return new InterruptRow(d.Name, T("MSI"), F("{0} message|{0} messages", irqs.Count(i => i < 0)), Palette.Ok);
                    if (d.Mode == InterruptMode.None) return new InterruptRow(d.Name, "–", T("no interrupt"), Palette.Muted);
                    var others = sharing.Where(s => irqs.Contains(s.Irq)).SelectMany(s => s.Devices).Where(o => o != d).Select(o => o.Name).Distinct().ToList();
                    return new InterruptRow(d.Name, F("IRQ {0}", string.Join(", ", irqs.Where(i => i >= 0).Distinct())),
                        others.Count > 0 ? F("shared with {0}", string.Join(", ", others)) : T("line-based, not shared"),
                        others.Count > 0 ? Palette.Warn : Palette.Info);
                }).ToList();
                return (list, sharing, Devices.Mice(), relevantDevices.Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase));
            });
            Interrupts.Clear();
            foreach (var row in rows) Interrupts.Add(row);
            _sharedIrqs = shared;
            _gameDevices = relevant;
            _mice = mice;
            MousePortsText = string.Join("\n", mice.Select(PortText));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            MousePortsText = "";
        }
    }

    // Windows turns power saving on again for a device plugged into a new port. Once it was turned off here (and not undone),
    // the app turns it off for the new device too, without asking, when it runs as administrator. Every 5 minutes.
    bool _devicePowerDeferred;

    async Task KeepDevicePowerSavingOffAsync()
    {
        if (!Elevated.IsAdmin || Busy || Tweaks.FirstOrDefault(r => r.Id == "dev.powersaving") is not { HasBackup: true, CurrentKey: "on" }) return;
        if (_watcher.ActiveGame is not null)
        {
            _devicePowerDeferred = true; // reading every device takes a moment; after the game (RunDeferredChecks)
            return;
        }
        try
        {
            bool changed = await Task.Run(() =>
            {
                var list = Devices.PowerSaving();
                if (!list.Any(x => x.Enabled) || !list.Any(x => !x.Enabled)) return false; // all on: the user chose On
                Devices.SetPowerSaving(_ => false);
                return true;
            });
            if (changed) await RefreshTweaksAsync();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }

    static string PortText(Devices.MousePort m)
    {
        if (!m.Usb) return F("{0}: not connected over USB (Bluetooth or another bus).", m.Mouse);
        string controller = m.Controller ?? T("unknown controller");
        string vendor = Devices.VendorName(m.Vendor) ?? "";
        string text = F("{0}: {1}", m.Mouse, controller) + (vendor.Length > 0 && !controller.Contains(vendor, StringComparison.OrdinalIgnoreCase) ? $" ({vendor})" : "");
        if (m.ThirdParty) text += " · " + T("add-on controller chip; a port of the processor's or chipset's own controller is usually better");
        if (m.ThroughHub) text += " · " + T("through a USB hub (in a monitor, keyboard or on the motherboard)");
        return text;
    }

    // --- Mouse polling ---

    RawMouseRecorder? _recorder;
    DispatcherTimer? _mouseTimer;
    readonly List<double> _mouseRates = [];
    long _firstMove;
    const int MouseTestSeconds = 6;

    void StartMouseTest()
    {
        if (Application.Current.MainWindow is not { } window) return;
        try
        {
            _recorder = new RawMouseRecorder(window);
        }
        catch (InvalidOperationException ex)
        {
            MouseResultText = ex.Message;
            return;
        }
        _mouseRates.Clear();
        _firstMove = 0;
        MouseSeries = [];
        MouseRateText = "–";
        MouseResultBrush = Palette.Muted;
        MouseResultText = T("Move the mouse quickly in circles over this window…");
        MouseTesting = true;
        _mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _mouseTimer.Tick += (_, _) => MouseTick();
        _mouseTimer.Start();
    }

    // Reports in the last 100 ms, as a rate; the test ends 6 seconds after the first movement.
    void MouseTick()
    {
        if (_recorder is null) return;
        var stamps = _recorder.Timestamps;
        if (stamps.Count == 0) return;
        if (_firstMove == 0) _firstMove = stamps[0];
        long now = Stopwatch.GetTimestamp(), window = Stopwatch.Frequency / 10;
        int count = 0;
        for (int i = stamps.Count - 1; i >= 0 && now - stamps[i] <= window; i--) count++;
        _mouseRates.Add(count * 10.0);
        if (_mouseRates.Count > 60) _mouseRates.RemoveAt(0);
        MouseSeries = _mouseRates.ToArray();
        MouseRateText = count > 0 ? F("{0} Hz", (count * 10).ToString("N0", L.Culture)) : "–";
        if ((now - _firstMove) / (double)Stopwatch.Frequency >= MouseTestSeconds) StopMouseTest();
    }

    void StopMouseTest()
    {
        _mouseTimer?.Stop();
        _mouseTimer = null;
        var stamps = _recorder?.Timestamps.ToList() ?? [];
        _recorder?.Dispose();
        _recorder = null;
        MouseTesting = false;
        var r = MousePolling.Analyze(stamps, Stopwatch.Frequency / 1000.0);
        if (r is null)
        {
            MouseResultBrush = Palette.Muted;
            MouseResultText = T("Not enough movement was recorded. Start again and keep moving the mouse quickly.");
            return;
        }
        MouseRateText = F("{0} Hz", r.AverageHz.ToString("N0", L.Culture));
        bool even = r.StablePercent >= 90;
        MouseResultBrush = even ? Palette.Ok : Palette.Warn;
        MouseResultText = F("{0} Hz on average (polling rate {1} Hz) · interval {2} ms, spread ±{3} ms · {4}% of the reports on time.",
                              r.AverageHz.ToString("N0", L.Culture), r.NominalHz, r.MeanMs.ToString("0.###", L.Culture), r.StdDevMs.ToString("0.###", L.Culture), r.StablePercent.ToString("0", L.Culture))
                          + " " + (even
                              ? T("Even reports.")
                              : T("Uneven reports: move faster and in wide circles, try another USB port and close background programs. Wireless mice and busy systems report less evenly."))
                          + (r.NominalHz < 1000 ? " " + T("Gaming mice can usually be set to 1000 Hz or more in their software.") : "");
    }
}

// Movement reports of the mouse to the main window, timed with the high-resolution clock as they arrive.
// Only the device that sent the most reports counts (a second mouse or a touchpad is ignored).
internal sealed class RawMouseRecorder : IDisposable
{
    readonly HwndSource _source;
    readonly IntPtr _window, _buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(RawInput.BufferSize);
    readonly Dictionary<IntPtr, List<long>> _devices = [];

    public RawMouseRecorder(Window window)
    {
        _window = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_window) ?? throw new InvalidOperationException(T("The window is not ready."));
        if (!RawInput.Register(_window))
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_buffer);
            throw new InvalidOperationException(T("Raw mouse input could not be registered."));
        }
        _source.AddHook(Hook);
    }

    public IReadOnlyList<long> Timestamps => _devices.Values.MaxBy(l => l.Count) ?? [];

    IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != RawInput.WM_INPUT) return IntPtr.Zero;
        long now = Stopwatch.GetTimestamp();
        if (RawInput.ReadMouse(lParam, _buffer) is { } m && (m.Dx != 0 || m.Dy != 0))
        {
            if (!_devices.TryGetValue(m.Device, out var list)) _devices[m.Device] = list = [];
            list.Add(now);
        }
        return IntPtr.Zero; // DefWindowProc still runs, as raw input requires
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        RawInput.Register(_window, remove: true);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(_buffer);
    }
}
