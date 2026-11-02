namespace PerformanceBooster.Tests;

// Network: DNS packet, ping, DNS speed test, speed and latency under load test (real network; skipped with no-net).
static class NetworkTests
{
    sealed class Messages : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) { lock (Items) Items.Add(value); }
    }

    public static void Run()
    {
        Runner.Test("DNS query packet", () =>
        {
            var b = DnsBenchmark.Build(0x1234, "example.com");
            Check.Equal(29, b.Length, "length");
            Check.True(b[0] == 0x12 && b[1] == 0x34 && b[2] == 0x01 && b[5] == 1, "header: ID, recursion, 1 question");
            Check.True(b[12] == 7 && b[20] == 3 && b[24] == 0 && b[26] == 1 && b[28] == 1, "labels and A/IN");
        });

        Runner.Test("Encrypted DNS: query to a fake DNS over HTTPS server, options and known servers", async () =>
        {
            string? path = null, accept = null;
            bool fail = false;
            using var server = new FakeHttp((p, headers) =>
            {
                path = p;
                accept = headers.GetValueOrDefault("Accept");
                return fail ? (500, null, []) : (200, null, [0, 0, 0x81, 0x80, 0, 1, 0, 1]);
            });
            using var http = new System.Net.Http.HttpClient();
            var template = $"http://127.0.0.1:{server.Port}/dns-query";
            Check.True(await DnsBenchmark.QueryDohAsync(http, template, "example.com", CancellationToken.None) is > 0, "answered");
            var expected = Convert.ToBase64String(DnsBenchmark.Build(0, "example.com")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Check.Equal("/dns-query?dns=" + expected, path, "query in the URL (base64url, no padding)");
            Check.Equal("application/dns-message", accept, "asks for a DNS message");
            fail = true;
            Check.Equal<double?>(null, await DnsBenchmark.QueryDohAsync(http, template, "example.com", CancellationToken.None), "an error is no answer");

            Check.Equal(4.0, new DnsResult("x", "1.1.1.1", 90, 95, 0, 10, 4).EffectiveMs, "the encrypted time counts when it was measured");
            Check.Equal(90.0, new DnsResult("x", "10.0.0.1", 90, 95, 0, 10).EffectiveMs, "otherwise the normal time");
            Check.Equal("Cloudflare", DnsBenchmark.Find("1.0.0.1")?.Name, "known server by its second address");
            var options = Catalog.Find("net.dns")!.Options();
            Check.True(options.Any(o => o.Key == "cloudflare" && o.Value == "1.1.1.1,1.0.0.1"), "the normal option stays");
            Check.Equal(DnsStore.EncryptionSupported, options.Any(o => o.Key == "cloudflare-doh" && o.Value == "1.1.1.1,1.0.0.1" + DnsStore.EncryptedSuffix),
                "an encrypted option on Windows 11");
        });

        Runner.Test("Speed test grade (bufferbloat scale)", () =>
        {
            (double Ms, string Grade)[] cases = [(0, "A+"), (4.9, "A+"), (5, "A"), (29.9, "A"), (30, "B"), (59, "B"), (60, "C"), (199, "C"), (200, "D"), (399, "D"), (400, "F"), (2000, "F")];
            foreach (var (ms, grade) in cases) Check.Equal(grade, SpeedTest.Grade(ms).Grade, $"{ms} ms");
        });

        if (!Runner.Network)
        {
            Runner.Test("Real network tests", () => Check.Skip("--no-net"));
            return;
        }

        Runner.Test("Active network adapter and router", () =>
        {
            var n = NetInfo.Active() ?? throw new TestFailure("no active adapter");
            var gateway = NetInfo.Gateway(n);
            Check.True(gateway is not null, "router address");
            var nic = NicInfo.Detect();
            Runner.Note($"{n.Name} ({n.NetworkInterfaceType}) · {n.Speed / 1_000_000} Mbps · router {gateway} · adapter: {nic?.Description} · gigabit capable: {nic?.SupportsGigabit}");
        });

        Runner.Test("DNS query: answer and timeout", async () =>
        {
            var ms = await DnsBenchmark.QueryAsync("1.1.1.1", "cloudflare.com", CancellationToken.None);
            Check.True(ms is > 0 and < 1500, "1.1.1.1 answer: " + ms);
            // There is no DNS server on the local address: the ISP cannot intercept, the query stays unanswered.
            var clock = Stopwatch.StartNew();
            var none = await DnsBenchmark.QueryAsync("127.0.0.1", "cloudflare.com", CancellationToken.None);
            if (none is not null) Check.Skip("a local DNS server runs on this computer");
            Check.True(clock.ElapsedMilliseconds < 3000, "a silent server is given up after 1.5 s at most: " + clock.ElapsedMilliseconds);
        });

        Runner.Test("DNS redirection detection", async () =>
        {
            bool intercepted = await DnsBenchmark.InterceptedAsync(CancellationToken.None);
            Runner.Note(intercepted
                ? "192.0.2.1 (an address with no DNS server) answered: the ISP/router redirects DNS queries; the app shows this as a warning"
                : "DNS queries are not redirected");
        });

        Runner.Test("DNS speed test", async () =>
        {
            var progress = new Messages();
            var clock = Stopwatch.StartNew();
            var results = await DnsBenchmark.RunAsync(progress, CancellationToken.None);
            Check.True(results.Count >= 5, "server count");
            Check.True(results.Count(r => !double.IsNaN(r.MedianMs)) >= 3, "most servers answered");
            Check.True(results.Count(r => !double.IsNaN(r.EncryptedMs)) >= 3, "most servers answered encrypted");
            var times = results.Select(r => double.IsNaN(r.EffectiveMs) ? double.MaxValue : r.EffectiveMs).ToList();
            Check.True(times.Zip(times.Skip(1)).All(p => p.First <= p.Second), "sorted fastest first");
            Runner.Note($"{clock.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", results.Select(r => $"{r.Name} {r.MedianMs:0} ms / encrypted {r.EncryptedMs:0} ms ({r.Failures}/{r.Queries} unanswered)")));
        });

        Runner.Test("The speed test can be cancelled", async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                await SpeedTest.RunAsync(new Messages(), 10, cts.Token);
                throw new TestFailure("not cancelled");
            }
            catch (OperationCanceledException)
            {
            }
        });

        Runner.Test("Speed and latency under load test (Cloudflare)", async () =>
        {
            var progress = new Messages();
            var r = await SpeedTest.RunAsync(progress, 10, CancellationToken.None);
            Check.True(r.DownMbps > 0 && r.UpMbps > 0 && r.IdleMs > 0, "measurement");
            Check.True(new[] { "A+", "A", "B", "C", "D", "F" }.Contains(r.Grade) && r.Verdict.Length > 0, "grade");
            Check.True(progress.Items.Count == 3, "three stages reported");
            Runner.Note($"idle {r.IdleMs:0} ms · download {r.DownMbps:0.0} Mbps ({r.DownLoadedMs:0} ms) · upload {r.UpMbps:0.0} Mbps ({r.UpLoadedMs:0} ms) · grade {r.Grade}");
        });
    }
}
