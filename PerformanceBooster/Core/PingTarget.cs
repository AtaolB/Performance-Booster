using System.Net.NetworkInformation;

namespace PerformanceBooster.Core;

public enum PingKind { Gateway, Internet }

public sealed class PingTarget(string name, string host, PingKind kind)
{
    public const int Window = 60;

    readonly History _rtt = new(Window);
    readonly Ping _ping = new();
    bool _busy;

    public string Name { get; } = name;
    public string Host { get; set; } = host;
    public PingKind Kind { get; } = kind;

    public double? Last { get; private set; }
    public double Average { get; private set; } = double.NaN;
    public double Min { get; private set; } = double.NaN;
    public double Max { get; private set; } = double.NaN;
    public double Jitter { get; private set; } = double.NaN;
    public double LossPercent { get; private set; }
    public int Samples => _rtt.Count;

    // Lost packets are kept as NaN (gap in the chart).
    public double[] Series() => _rtt.Snapshot();

    public async Task SampleAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(Host)) return;
        _busy = true;
        double value = double.NaN;
        try
        {
            var reply = await _ping.SendPingAsync(Host, 1000);
            if (reply.Status == IPStatus.Success) value = reply.RoundtripTime;
        }
        catch (PingException)
        {
            // Name could not be resolved or no network: count as lost.
        }
        catch (InvalidOperationException)
        {
            // Previous request still running: skip.
            _busy = false;
            return;
        }
        finally
        {
            _busy = false;
        }
        Add(value);
    }

    // A reply time (NaN: lost).
    public void Add(double value)
    {
        _rtt.Add(value);
        Last = double.IsNaN(value) ? null : value;
        Recalculate();
    }

    void Recalculate()
    {
        var all = _rtt.Snapshot();
        var ok = all.Where(v => !double.IsNaN(v)).ToArray();
        LossPercent = all.Length == 0 ? 0 : (all.Length - ok.Length) * 100.0 / all.Length;
        if (ok.Length == 0)
        {
            Average = Min = Max = Jitter = double.NaN;
            return;
        }
        Average = ok.Average();
        Min = ok.Min();
        Max = ok.Max();
        // Jitter: average difference between consecutive successful samples (as in RFC 3550).
        Jitter = ok.Length < 2 ? 0 : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
    }
}
