using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PerformanceBooster.Core;

public sealed record SpeedResult(
    double IdleMs, double DownMbps, double DownLoadedMs, double UpMbps, double UpLoadedMs,
    string Grade, string Verdict);

// Speed and latency under load (bufferbloat) test. Uses the Cloudflare speed test endpoints:
// GET /__down?bytes=N downloads random data, POST /__up uploads data. Latency is measured with ICMP to 1.1.1.1.
public static class SpeedTest
{
    const string Server = "https://speed.cloudflare.com";
    const string PingHost = "1.1.1.1";
    const int Streams = 4;
    const long CapPerSecond = 50L * 1024 * 1024; // a phase stops after 50 MB per second of its length (400 MB for 8 s)

    public static readonly int[] Durations = [10, 30, 60];

    // seconds: length of the download and of the upload phase each.
    public static async Task<SpeedResult> RunAsync(IProgress<string> progress, int seconds, CancellationToken ct)
    {
        var phase = TimeSpan.FromSeconds(seconds);
        using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Streams * 2 }) { Timeout = Timeout.InfiniteTimeSpan };

        progress.Report(T("Measuring idle latency…"));
        var idle = Median(await PingSeriesAsync(TimeSpan.FromSeconds(4), ct));
        if (double.IsNaN(idle)) throw new InvalidOperationException(T("1.1.1.1 does not answer ping; check your internet connection."));

        progress.Report(F("Download test ({0} s)…", seconds));
        var (down, downLatency) = await RunPhaseAsync((count, token) => DownloadAsync(http, count, token), phase, ct);

        progress.Report(F("Upload test ({0} s)…", seconds));
        var (up, upLatency) = await RunPhaseAsync((count, token) => UploadAsync(http, count, token), phase, ct);

        if (down == 0 && up == 0) throw new InvalidOperationException(T("Could not reach the test server."));

        double added = Math.Max(Nz(downLatency) - idle, Nz(upLatency) - idle);
        var (grade, verdict) = Grade(added);
        return new(idle, down, downLatency, up, upLatency, grade, verdict);

        static double Nz(double v) => double.IsNaN(v) ? 0 : v;
    }

    // Grade by the latency added under load (close to the Waveform bufferbloat scale).
    internal static (string Grade, string Verdict) Grade(double addedMs) => addedMs switch
    {
        < 5 => ("A+", T("Latency does not rise under load. No bufferbloat.")),
        < 30 => ("A", T("Latency rises a little under load. Good for gaming.")),
        < 60 => ("B", T("Ping rises noticeably while downloading.")),
        < 200 => ("C", T("In-game ping rises noticeably while someone downloads or uploads. Turn on QoS/SQM on the router.")),
        < 400 => ("D", T("Serious bufferbloat. Turn on QoS/SQM on the router or set the speed limit to 90% of your line.")),
        _ => ("F", T("Very serious bufferbloat. Turn on QoS/SQM on the router; if it does not support it, consider a router with SQM.")),
    };

    static async Task<(double Mbps, double LatencyMs)> RunPhaseAsync(Func<Func<int, bool>, CancellationToken, Task> worker, TimeSpan phase, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long bytes = 0, cap = (long)(CapPerSecond * phase.TotalSeconds);
        bool Count(int n)
        {
            if (Interlocked.Add(ref bytes, n) >= cap) cts.Cancel();
            return !cts.IsCancellationRequested;
        }

        var clock = Stopwatch.StartNew();
        var pings = PingSeriesAsync(phase, ct);
        cts.CancelAfter(phase);
        await Task.WhenAll(Enumerable.Range(0, Streams).Select(_ => Quiet(() => worker(Count, cts.Token))));
        double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.1);
        ct.ThrowIfCancellationRequested();
        return (Interlocked.Read(ref bytes) * 8 / seconds / 1e6, Median(await pings));
    }

    // The server may reject some sizes (403); the first accepted size is used.
    static readonly long[] DownloadSizes = [50_000_000, 25_000_000, 10_000_000];
    static int _downloadSize;

    static async Task DownloadAsync(HttpClient http, Func<int, bool> count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (!ct.IsCancellationRequested)
        {
            int size = _downloadSize;
            using var response = await http.GetAsync($"{Server}/__down?bytes={DownloadSizes[size]}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode && size + 1 < DownloadSizes.Length)
            {
                Interlocked.CompareExchange(ref _downloadSize, size + 1, size);
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                if (!count(n)) return;
        }
    }

    static async Task UploadAsync(HttpClient http, Func<int, bool> count, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var response = await http.PostAsync($"{Server}/__up", new GeneratedContent(25_000_000, count), ct);
        }
    }

    sealed class GeneratedContent(long length, Func<int, bool> count) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var chunk = new byte[65536];
            Random.Shared.NextBytes(chunk);
            for (long sent = 0; sent < length; sent += chunk.Length)
            {
                int n = (int)Math.Min(chunk.Length, length - sent);
                await stream.WriteAsync(chunk.AsMemory(0, n), ct);
                if (!count(n)) throw new OperationCanceledException();
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }

    static async Task<List<double>> PingSeriesAsync(TimeSpan duration, CancellationToken ct)
    {
        var results = new List<double>();
        using var ping = new Ping();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration && !ct.IsCancellationRequested)
        {
            try
            {
                var reply = await ping.SendPingAsync(PingHost, TimeSpan.FromSeconds(1), cancellationToken: ct);
                if (reply.Status == IPStatus.Success) results.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            await Task.Delay(250, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        return results;
    }

    static async Task Quiet(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException) { }
    }

    static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}

// MedianMs: repeated queries the server answers from its cache (the time to reach it); FirstMs: the first query of each name,
// which the server may have to look up first.
public sealed record DnsResult(string Name, string Server, double MedianMs, double FirstMs, int Failures, int Queries, double EncryptedMs = double.NaN)
{
    // What the server gives when it is chosen in the app: encrypted when it could be measured that way.
    public double EffectiveMs => double.IsNaN(EncryptedMs) ? MedianMs : EncryptedMs;
}

public sealed record DnsServer(string Key, string Name, string[] Servers, string DohTemplate);

// Asks every server the same domains and measures the median response time, the normal way (UDP port 53) and encrypted
// (DNS over HTTPS). A provider may answer every normal query itself; encrypted queries cannot be redirected, so they
// show the server's real speed.
// Each server gets one socket for all its queries: a new socket per query also measured the setup of the path through the
// firewall and security software (often around 100 ms) instead of the server. Each name is asked three times; the first answer
// can include a lookup the server still had to do, the repeated ones show how quickly the server itself answers.
public static class DnsBenchmark
{
    const int Repeats = 2;

    public static readonly DnsServer[] Known =
    [
        new("cloudflare", "Cloudflare", ["1.1.1.1", "1.0.0.1"], "https://cloudflare-dns.com/dns-query"),
        new("google", "Google", ["8.8.8.8", "8.8.4.4"], "https://dns.google/dns-query"),
        new("quad9", "Quad9", ["9.9.9.9", "149.112.112.112"], "https://dns.quad9.net/dns-query"),
        new("opendns", "OpenDNS", ["208.67.222.222", "208.67.220.220"], "https://doh.opendns.com/dns-query"),
        new("adguard", "AdGuard", ["94.140.14.14", "94.140.15.15"], "https://dns.adguard-dns.com/dns-query"),
    ];

    public static DnsServer? Find(string ip) => Known.FirstOrDefault(s => s.Servers.Contains(ip));

    static readonly string[] Domains =
    [
        "google.com", "youtube.com", "cloudflare.com", "steampowered.com", "riotgames.com",
        "epicgames.com", "discord.com", "microsoft.com", "twitch.tv", "ea.com",
    ];

    // The IPv4 DNS server the connection uses now.
    public static string? Current()
    {
        try
        {
            return NetInfo.Active()?.GetIPProperties().DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Name, string Server)> Servers()
    {
        var list = new List<(string, string)>();
        if (Current() is { } current) list.Add((Find(current)?.Name ?? T("Current DNS"), current));
        list.AddRange(Known.Select(s => (s.Name, s.Servers[0])));
        return list.DistinctBy(s => s.Item2).ToList();
    }

    // 192.0.2.1 is a documentation address (RFC 5737); no DNS server ever lives there. If it answers, the ISP or router
    // redirects every query on port 53 to its own server: the chosen DNS server has no effect.
    public static async Task<bool> InterceptedAsync(CancellationToken ct) => await QueryAsync("192.0.2.1", "example.com", ct) is not null;

    public static async Task<List<DnsResult>> RunAsync(IProgress<string> progress, CancellationToken ct)
    {
        var servers = Servers();
        progress.Report(T("Testing DNS servers…"));
        // Servers are measured in parallel; each server gets the domains one after another (without loading it).
        var results = await Task.WhenAll(servers.Select(s => MeasureAsync(s.Name, s.Server, ct)));
        return results.OrderBy(r => double.IsNaN(r.EffectiveMs) ? double.MaxValue : r.EffectiveMs).ToList();
    }

    static async Task<DnsResult> MeasureAsync(string name, string server, CancellationToken ct)
    {
        var first = new List<double>();
        var cached = new List<double>();
        int failures = 0;
        using var channel = DnsChannel.Open(server);
        if (channel is null) return new(name, server, double.NaN, double.NaN, Domains.Length, Domains.Length);
        await channel.QueryAsync("example.com", ct); // opens the path; not counted
        foreach (var domain in Domains)
        {
            ct.ThrowIfCancellationRequested();
            if (await channel.QueryAsync(domain, ct) is not double t)
            {
                failures++;
                continue;
            }
            first.Add(t);
            for (int i = 0; i < Repeats; i++)
                if (await channel.QueryAsync(domain, ct) is double again) cached.Add(again);
        }
        double encrypted = Find(server) is { } known ? await MeasureEncryptedAsync(known.DohTemplate, ct) : double.NaN;
        return new(name, server, Median(cached), Median(first), failures, Domains.Length, encrypted);
    }

    // DNS over HTTPS on one kept-open connection, HTTP/2 where the server offers it (as Windows and browsers use it); the first
    // query (connection and TLS setup) is not counted.
    static async Task<double> MeasureEncryptedAsync(string template, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        if (await QueryDohAsync(http, template, "example.com", ct) is null) return double.NaN;
        var times = new List<double>();
        foreach (var domain in Domains)
        {
            ct.ThrowIfCancellationRequested();
            if (await QueryDohAsync(http, template, domain, ct) is double t) times.Add(t);
        }
        return Median(times);
    }

    internal static async Task<double?> QueryDohAsync(HttpClient http, string template, string domain, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{template}?dns={Base64Url.EncodeToString(Build(0, domain))}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.ParseAdd("application/dns-message");
        try
        {
            var clock = Stopwatch.StartNew();
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            return response.IsSuccessStatusCode && body.Length >= 4 && (body[2] & 0x80) != 0 ? clock.Elapsed.TotalMilliseconds : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    // One-off query on its own socket (the interception check).
    internal static async Task<double?> QueryAsync(string server, string domain, CancellationToken ct)
    {
        using var channel = DnsChannel.Open(server);
        return channel is null ? null : await channel.QueryAsync(domain, ct);
    }

    // One UDP socket to a DNS server for several queries, with one receive buffer. A late answer to an earlier query that
    // timed out is read by the next query and skipped by its ID.
    internal sealed class DnsChannel : IDisposable
    {
        readonly Socket _socket;
        readonly byte[] _buffer = new byte[512];

        DnsChannel(Socket socket) => _socket = socket;

        public static DnsChannel? Open(string server)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Connect(IPAddress.Parse(server), 53);
                return new DnsChannel(socket);
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
        }

        // Time from sending to the matching answer; null after 1.5 s or on a socket error.
        public async Task<double?> QueryAsync(string domain, CancellationToken ct)
        {
            var id = (ushort)Random.Shared.Next(ushort.MaxValue);
            var packet = Build(id, domain);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var clock = Stopwatch.StartNew();
            try
            {
                await _socket.SendAsync(packet, SocketFlags.None, timeout.Token);
                while (true)
                {
                    int length = await _socket.ReceiveAsync(_buffer, SocketFlags.None, timeout.Token);
                    if (length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(_buffer) == id && (_buffer[2] & 0x80) != 0)
                        return clock.Elapsed.TotalMilliseconds;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // no answer within 1.5 s
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return null; // e.g. "port unreachable" from the server; the socket can be used again
            }
        }

        public void Dispose() => _socket.Dispose();
    }

    // Standard DNS query: A record, recursion desired. Each label is its length and its letters; the name ends with a zero.
    internal static byte[] Build(ushort id, string domain)
    {
        var packet = new byte[12 + domain.Length + 2 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        packet[2] = 0x01; // recursion desired
        packet[5] = 1;    // one question
        int at = 12;
        foreach (var range in domain.AsSpan().Split('.'))
        {
            var label = domain.AsSpan(range);
            packet[at++] = (byte)label.Length;
            at += Encoding.ASCII.GetBytes(label, packet.AsSpan(at));
        }
        at++;                // the zero that ends the name
        packet[at + 1] = 1;  // type A
        packet[at + 3] = 1;  // class IN
        return packet;
    }
}
