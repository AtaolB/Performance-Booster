using System.Net.Http;
using System.Net.Sockets;

namespace PerformanceBooster.Tests;

// Live card details: CPU, RAM, disk, GPU and network measurement per process.
static class DetailTests
{
    public static void Run()
    {
        Runner.Test("All processes in one call (the Task Manager source)", () =>
        {
            var clock = Stopwatch.StartNew();
            var all = ProcessSnapshot.Take();
            long ms = clock.ElapsedMilliseconds;
            using var self = Process.GetCurrentProcess();
            var me = all.FirstOrDefault(p => p.Id == self.Id);
            Check.True(all.Count > 50, "process count: " + all.Count);
            Check.Equal(self.ProcessName, me.Name, "our own process by name");
            Check.True(all.Any(p => p.Id == 4 && p.Name == "System") && all.Any(p => p.Id == 0 && p.Name == "Idle"), "System and Idle");
            self.Refresh();
            double ratio = me.WorkingSet / (double)self.WorkingSet64;
            Check.True(ratio is > 0.5 and < 1.5, $"working set agrees with the Process class ({me.WorkingSet} / {self.WorkingSet64})");
            Check.True(me.PrivateWorkingSet > 0 && me.PrivateWorkingSet <= me.WorkingSet, "private working set");
            Check.True(me.SessionId == self.SessionId && me.ParentId > 0, "session and parent process");
            Runner.Note($"{all.Count} processes, {ms} ms");
        });

        Runner.Test("Per-process CPU and disk rate", () =>
        {
            var rates = new ProcessRates();
            rates.Sample();
            int id = Environment.ProcessId;
            var file = Path.Combine(Runner.Data, "io.bin");
            var busy = Task.Run(() =>
            {
                var until = Environment.TickCount64 + 1200;
                var buffer = new byte[1 << 20];
                using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
                    for (int i = 0; i < 40; i++) fs.Write(buffer);
                double x = 0;
                while (Environment.TickCount64 < until) x += Math.Sqrt(x + 1);
                return x;
            });
            busy.Wait();
            Thread.Sleep(100);
            var me = rates.Sample().First(r => r.Info.Id == id);
            double oneCore = 100.0 / Environment.ProcessorCount;
            Check.True(me.CpuPercent > oneCore * 0.5, $"busy thread seen: {me.CpuPercent:0.0}% (one core {oneCore:0.0}%)");
            Check.True(me.WritePerSec > 10 * 1048576, $"write rate: {me.WritePerSec / 1048576:0} MB/s");
            var groups = ProcessSampler.Group(rates.Sample());
            Check.True(groups.First(g => g.Name == "System").CanKill == false && groups.All(g => g.Ids.Count == g.Count), "groups");
            File.Delete(file);
        });

        Runner.Test("Video memory per process: one adapter, never more than the adapter uses (fake counters)", () =>
        {
            const double Gb = 1073741824;
            const string card = "luid_0x00000000_0x0000AAAA", cardAgain = "luid_0x00000000_0x0000BBBB", igpu = "luid_0x00000000_0x0000CCCC";
            (string, double)[] engines =
            [
                ($"pid_100_{card}_phys_0_eng_0_engtype_3D", 40), ($"pid_100_{card}_phys_0_eng_1_engtype_3D", 30), ($"pid_100_{card}_phys_0_eng_2_engtype_Copy", 5),
                ("_Total", 99),
            ];
            // A game listed under two IDs of the same card and on the integrated GPU (the sum, 14.3 GB, was shown before),
            // a process reporting more than its adapter uses in total, and one on an adapter without a total.
            (string, double)[] memory =
            [
                ($"pid_100_{card}_phys_0", 7 * Gb), ($"pid_100_{cardAgain}_phys_0", 7 * Gb), ($"pid_100_{igpu}_phys_0", 0.3 * Gb),
                ($"pid_200_{card}_phys_0", 20 * Gb),
                ("pid_300_luid_0x00000000_0x0000DDDD_phys_0", 2 * Gb),
            ];
            (string, double)[] adapters = [($"{card}_phys_0", 8 * Gb), ($"{cardAgain}_phys_0", 8 * Gb), ($"{igpu}_phys_0", 1 * Gb)];
            var result = GpuProcessSampler.Combine(engines, memory, adapters);
            Check.Equal(70.0, result[100].Percent, "busiest engine type (two 3D engines added)");
            Check.Equal((long)(7 * Gb), result[100].Vram, "the adapter it uses most, not the sum");
            Check.Equal((long)(8 * Gb), result[200].Vram, "not more than the adapter uses in total");
            Check.Equal((long)(2 * Gb), result[300].Vram, "adapter without a total");
            Check.Equal(card, GpuProcessSampler.Adapter($"pid_100_{card}_phys_0_eng_0_engtype_3D"), "adapter ID");
        });

        Runner.Test("Graphics process counters", () =>
        {
            Check.Equal<int?>(1234, GpuProcessSampler.Pid("pid_1234_luid_0x00000000_0x0000C5E3_phys_0_eng_0_engtype_3D"), "process ID");
            Check.Equal<int?>(null, GpuProcessSampler.Pid("luid_0x0_phys_0"), "instance without an ID");
            using var gpu = new GpuProcessSampler();
            Thread.Sleep(500);
            var sample = gpu.Sample();
            Check.True(sample.Values.All(v => v.Percent is >= 0 and <= 100 && v.Vram >= 0), "value range");
            Runner.Note($"{sample.Count} processes use the graphics card; busiest: " + string.Join(", ", sample.OrderByDescending(kv => kv.Value.Percent).Take(3).Select(kv => $"{kv.Key} {kv.Value.Percent:0.0}% {kv.Value.Vram / 1048576} MB")));
        });

        Runner.Test("Network monitor line format", () =>
        {
            var parsed = NetMonitor.Parse("1234,5000,200;88,0,1500;broken;9,x,1");
            Check.True(parsed.Count == 2 && parsed[1234] == (5000, 200) && parsed[88] == (0, 1500), "parsing");
            Check.Equal(0, NetMonitor.Parse("").Count, "empty line");
        });

        if (!Runner.Network)
        {
            Runner.Test("Real network measurements", () => Check.Skip("--no-net"));
            return;
        }

        Runner.Test("TCP connection count per process", () =>
        {
            using var client = new TcpClient();
            client.Connect("1.1.1.1", 443);
            var counts = NetConnections.CountByProcess();
            Check.True(counts.GetValueOrDefault(Environment.ProcessId) >= 1, "our own connection seen");
            Runner.Note($"{counts.Count} processes have connections; most: " + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key}:{kv.Value}")));
        });

        Runner.Test("Per-app network traffic (elevated helper + ETW)", async () =>
        {
            if (!File.Exists(Runner.Exe)) Check.Skip("no exe");
            NetMonitorClient.HelperExe = Runner.Exe;
            using var monitor = new NetMonitorClient();
            try
            {
                Check.True(await monitor.StartAsync(), "helper started: " + monitor.Error);
                using var http = new HttpClient();
                long received = 0;
                var download = Task.Run(async () =>
                {
                    using var stream = await http.GetStreamAsync("https://speed.cloudflare.com/__down?bytes=25000000");
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await stream.ReadAsync(buffer)) > 0) received += n;
                });
                bool sawRate = false;
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(15))
                {
                    await Task.Delay(500);
                    sawRate |= monitor.Rates.TryGetValue(Environment.ProcessId, out var r) && r.Down > 100_000;
                    if (download.IsCompleted && clock.Elapsed > TimeSpan.FromSeconds(4)) break;
                }
                await download;
                await Task.Delay(1500); // the line of the last second
                long seenDown = monitor.Totals().GetValueOrDefault(Environment.ProcessId).Down;
                Check.True(monitor.Running, "helper running");
                Check.True(sawRate, "live download rate seen");
                Check.True(seenDown > received * 0.8, $"most of the traffic was measured: {seenDown / 1048576.0:0.0} MB / {received / 1048576.0:0.0} MB");
                Runner.Note($"measured by ETW: {seenDown / 1048576.0:0.0} MB · downloaded: {received / 1048576.0:0.0} MB");
            }
            finally
            {
                monitor.Stop();
                NetMonitorClient.HelperExe = null;
            }
            // Once the pipe closes, the helper exits within 1-2 s (the parent of the elevated process is the test).
            await Task.Delay(3000);
            var helpers = ProcessSnapshot.Take().Where(p => p.Name == "PerformanceBooster" && p.ParentId == Environment.ProcessId).Select(p => p.Id.ToString()).ToList();
            Check.Empty(helpers, "helper that did not exit");
        });
    }
}
