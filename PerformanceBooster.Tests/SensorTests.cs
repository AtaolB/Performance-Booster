namespace PerformanceBooster.Tests;

// Sensors tab: units, the temperature warning, and one real (read-only) reading.
static class SensorTests
{
    static SensorReading R(DeviceKind device, SensorKind kind, string name, double? value) =>
        new($"/{device}/{kind}/{name}", "/" + device, device.ToString(), device, kind, name, value);

    public static void Run()
    {
        Runner.Test("Sensor values get their units", () =>
        {
            Check.Equal("65.5 °C", SensorFormat.Value(SensorKind.Temperature, 65.46), "temperature");
            Check.Equal("1.215 V", SensorFormat.Value(SensorKind.Voltage, 1.2149), "voltage");
            Check.Equal("4950 MHz", SensorFormat.Value(SensorKind.Clock, 4949.7), "clock");
            Check.Equal("1250 RPM", SensorFormat.Value(SensorKind.Fan, 1250.2), "fan");
            Check.Equal("12.5 MB/s", SensorFormat.Value(SensorKind.Throughput, 12_500_000), "throughput in MB/s");
            Check.Equal("850.0 KB/s", SensorFormat.Value(SensorKind.Throughput, 850_000), "throughput in KB/s");
            Check.Equal("01:01:05", SensorFormat.Value(SensorKind.TimeSpan, 3665), "time");
        });

        Runner.Test("Device temperature: the graphics core, never a drive's warning limit", () =>
        {
            SensorReading[] readings =
            [
                R(DeviceKind.Graphics, SensorKind.Temperature, "GPU Core", 70),
                R(DeviceKind.Graphics, SensorKind.Temperature, "GPU Hot Spot", 82),
                R(DeviceKind.Processor, SensorKind.Temperature, "Core #1", 71),
                R(DeviceKind.Processor, SensorKind.Temperature, "Core #2", 78),
                R(DeviceKind.Processor, SensorKind.Temperature, "Core #1 Distance to TjMax", 29),
                R(DeviceKind.Storage, SensorKind.Temperature, "Warning Temperature", 84),
                R(DeviceKind.Storage, SensorKind.Temperature, "Composite Temperature", 40),
            ];
            Check.Equal(70.0, SensorFormat.DeviceTemperature(readings, DeviceKind.Graphics), "graphics core, not the hot spot");
            Check.Equal(78.0, SensorFormat.DeviceTemperature(readings, DeviceKind.Processor), "hottest core");
            Check.Equal(40.0, SensorFormat.DeviceTemperature(readings, DeviceKind.Storage), "the drive's own temperature");
            Check.True(SensorFormat.DeviceTemperature(readings, DeviceKind.Battery) is null, "no readings, no temperature");
        });

        Runner.Test("Temperature warning: two readings over the limit, then quiet until it cools down and 15 minutes pass", () =>
        {
            var alert = new TemperatureAlert();
            var t = new DateTime(2026, 1, 1, 12, 0, 0);
            Check.True(!alert.Check(96, 95, t), "one reading is not enough");
            Check.True(alert.Check(97, 95, t.AddSeconds(6)), "two in a row warn");
            Check.True(!alert.Check(98, 95, t.AddSeconds(12)), "not again while it stays hot");
            Check.True(!alert.Check(92, 95, t.AddMinutes(1)), "cooled a little");
            Check.True(!alert.Check(96, 95, t.AddMinutes(2)) && !alert.Check(96, 95, t.AddMinutes(2.1)), "not cooled 5 °C: still quiet");
            Check.True(!alert.Check(88, 95, t.AddMinutes(3)), "cooled down");
            Check.True(!alert.Check(96, 95, t.AddMinutes(4)) && !alert.Check(96, 95, t.AddMinutes(4.1)), "within 15 minutes: quiet");
            Check.True(!alert.Check(80, 95, t.AddMinutes(16)) && !alert.Check(96, 95, t.AddMinutes(17)) && alert.Check(96, 95, t.AddMinutes(17.1)), "later: warns again");
            Check.True(!alert.Check(null, 95, t.AddMinutes(40)), "no value, no warning");
        });

        Runner.Test("Reading interval: 0.5 to 60 s, whole custom seconds from 1 to 120", () =>
        {
            var interval = new PerformanceBooster.UI.IntervalSetting(0.1);
            double changed = 0;
            interval.Changed += s => changed = s;
            Check.True(!interval.IsCustom && interval.Seconds == 0.5, "0.1 s is no longer offered: 0.5 s instead");
            interval.Selected = interval.Choices[^1];
            Check.True(interval.IsCustom, "Custom chosen");
            interval.CustomText = "35";
            Check.Equal(35.0, changed, "35 s");
            interval.CustomText = "35.5";
            Check.Equal(35.0, interval.Seconds, "decimals are not taken");
            interval.CustomText = "500";
            Check.Equal(120.0, interval.Seconds, "at most 120 s");
            interval.CustomText = "0";
            Check.Equal(1.0, interval.Seconds, "at least 1 s");
            Check.True(new PerformanceBooster.UI.IntervalSetting(45).IsCustom && new PerformanceBooster.UI.IntervalSetting(300).Seconds == 120, "saved values come back in range");
        });

        Runner.Test("Sensors of this computer are read (read-only); no filter drivers among the network devices", () =>
        {
            using var hub = new SensorHub();
            var readings = hub.Read();
            Check.True(readings.Count > 0, "sensors found");
            Check.True(readings.Any(r => r.DeviceKind == DeviceKind.Processor && r.Kind == SensorKind.Load && r.Value is not null), "processor load");
            Check.True(!readings.Any(r => r.DeviceKind == DeviceKind.Network && r.Device.Contains("Filter", StringComparison.OrdinalIgnoreCase)), "only connected adapters");
            var light = hub.Read(light: true);
            Check.True(light.All(r => r.DeviceKind is DeviceKind.Processor or DeviceKind.Graphics), "the light reading: processor and graphics only");
            Runner.Note($"{readings.Count} sensors, {readings.Count(r => r.Value is not null)} with a value; laptop controller: {string.Join(", ", readings.Where(r => r.Id.StartsWith("/laptop/", StringComparison.Ordinal)).Select(r => $"{r.Name}={r.Value}"))}");
        });
    }
}
