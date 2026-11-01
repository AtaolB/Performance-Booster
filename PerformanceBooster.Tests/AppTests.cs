namespace PerformanceBooster.Tests;

// The built exe: command line (report, empty --set) and single-instance behavior. A separate data folder so the user's data is never touched.
static class AppTests
{
    static string DataDir => Path.Combine(Runner.Data, "app");

    static Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Runner.Exe!) { UseShellExecute = false };
        psi.Arguments = string.Join(" ", args.Select(a => a.Length == 0 || a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
        psi.Environment["PERFORMANCEBOOSTER_DATA"] = DataDir;
        var p = Process.Start(psi)!;
        Fixtures.OnCleanup(() => { if (!p.HasExited) p.Kill(); p.Dispose(); });
        return p;
    }

    static int ParentOf(int pid)
    {
        using var search = new System.Management.ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={pid}");
        foreach (System.Management.ManagementObject o in search.Get())
            using (o) return Convert.ToInt32(o["ParentProcessId"]);
        return -1;
    }

    public static void Run()
    {
        if (!File.Exists(Runner.Exe))
        {
            Runner.Test("App", () => Check.Skip("no exe: " + Runner.Exe));
            return;
        }
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "settings.json"), """{"Language":"en","TrayHintShown":true}""");

        Runner.Test("exe --report: every setting is read", () =>
        {
            var file = Path.Combine(DataDir, "report.txt");
            var p = Start("--report", file);
            Check.True(p.WaitForExit(90_000), "finished within 90 s");
            Check.Equal(0, p.ExitCode, "exit code");
            var lines = File.ReadAllLines(file);
            Check.True(lines.Any(l => l == "=== Power ===") && lines.Any(l => l.StartsWith("[OK   ]", StringComparison.Ordinal) || l.StartsWith("[ISSUE]", StringComparison.Ordinal)), "English report");
            Check.Empty(lines.Where(l => l.Contains("could not be read")), "unreadable setting");
            foreach (var l in lines) Runner.Note(l);
        });

        Runner.Test("exe --set \"\": changes nothing", () =>
        {
            var p = Start("--set", "");
            Check.True(p.WaitForExit(60_000), "finished");
            Check.Equal(0, p.ExitCode, "exit code");
            Check.Equal("There was nothing to change.", File.ReadAllText(Path.Combine(DataDir, "last-operation.log")).Trim(), "log");
        });

        Runner.Test("nvidia-smi closes when the app is killed", () =>
        {
            if (GpuMonitor.FindNvidiaSmi() is null) Check.Skip("no nvidia-smi");
            var app = Start(); // GPU monitoring starts while the window is visible
            Process? smi = null;
            var clock = Stopwatch.StartNew();
            while (smi is null && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                Thread.Sleep(300);
                smi = Process.GetProcessesByName("nvidia-smi").FirstOrDefault(p => ParentOf(p.Id) == app.Id);
            }
            Check.True(smi is not null, "nvidia-smi started");
            app.Kill();
            app.WaitForExit(5000);
            Check.True(smi!.WaitForExit(5000), "nvidia-smi closed with the app (no orphan process left)");
        });

        Runner.Test("Single instance and restart on language change", () =>
        {
            var first = Start("--minimized");
            Thread.Sleep(5000);
            Check.True(!first.HasExited, "the first instance runs in the tray");
            var second = Start("--minimized");
            Check.True(second.WaitForExit(15_000), "the second instance exits at once");
            Check.Equal(0, second.ExitCode, "exit code of the second instance");
            Thread.Sleep(1000);
            Check.True(!first.HasExited, "the first instance stayed open");

            var restarted = Start("--restart", "--minimized");
            Thread.Sleep(2000);
            Check.True(!restarted.HasExited, "the new instance waits for the previous one to exit");
            first.Kill();
            first.WaitForExit(5000);
            Thread.Sleep(5000);
            Check.True(!restarted.HasExited, "the new instance took over when the previous one exited");
            restarted.Kill();
            restarted.WaitForExit(5000);
        });
    }
}
