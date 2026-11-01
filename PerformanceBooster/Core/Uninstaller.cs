using System.ComponentModel;
using System.Diagnostics;

namespace PerformanceBooster.Core;

public sealed record UninstallResult(bool Removed, bool Cancelled, bool RebootRequired, int ExitCode, string Message);

// Uninstalls a program: desktop programs with their own uninstaller (the silent command if there is one), Store packages with Remove-AppxPackage.
// The uninstaller of programs installed for all users runs as administrator (UAC).
public static class Uninstaller
{
    public static async Task<UninstallResult> RunAsync(InstalledProgram p, bool quiet, CancellationToken ct)
    {
        if (p.Source == ProgramSource.Store) return await RemoveStoreAsync(p, ct);

        var command = quiet ? p.QuietCommand ?? p.UninstallCommand : p.UninstallCommand ?? p.QuietCommand;
        if (command is null) return new(false, false, false, -1, T("This program cannot be uninstalled (no uninstall command)."));
        var (file, args) = Programs.SplitCommand(command);
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        }
        catch (ArgumentException) { }
        if (p.MachineWide) psi.Verb = "runas";

        Process? process;
        long startedAt = DateTime.UtcNow.ToFileTimeUtc();
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, true, false, 1223, T("Cancelled: administrator permission was not granted."));
        }
        catch (Win32Exception ex)
        {
            return new(false, false, false, ex.NativeErrorCode, ex.Message);
        }
        if (process is null) return new(false, false, false, -1, T("Could not start the uninstaller."));

        using (process)
        {
            int exit = await WaitTreeAsync(process, startedAt, () => Programs.Exists(p), ct);
            bool gone = await WaitGoneAsync(p, TimeSpan.FromSeconds(6), ct);
            return Interpret(exit, gone);
        }
    }

    internal static UninstallResult Interpret(int exit, bool gone)
    {
        bool reboot = exit is 3010 or 1641;
        if (gone || exit == 1605) // 1605: the product is not installed anymore
            return new(true, false, reboot, exit, reboot ? T("Uninstalled · restart required") : T("Uninstalled"));
        if (exit is 1602 or 1223) return new(false, true, false, exit, T("Cancelled"));
        if (exit == 1618) return new(false, false, false, exit, T("Another installation is in progress. Try again when it finishes."));
        return new(false, false, false, exit, exit == 0
            ? T("Uninstall did not finish; the program still appears to be installed.")
            : F("Could not uninstall (exit code {0})", exit));
    }

    // Waits until the uninstaller and the child processes it started finish. Some uninstallers (NSIS, Inno Setup) copy themselves
    // to a temporary folder and restart, and the first process exits at once; the rest of the work continues in the child.
    // Processes opened after the uninstall (like a survey page opened by the uninstaller) are not waited for: waiting ends once
    // the root has exited and the entry is gone.
    static async Task<int> WaitTreeAsync(Process root, long startedAt, Func<bool> stillInstalled, CancellationToken ct)
    {
        int rootId = root.Id;
        var tracked = new Dictionary<int, long>(); // process -> creation time
        bool first = true;
        while (true)
        {
            var snapshot = ProcessSnapshot.Take();
            if (first)
            {
                // If the root process exited before the first sample, the children started after it are still tracked.
                tracked[rootId] = snapshot.FirstOrDefault(s => s.Id == rootId) is { Id: > 0 } r ? r.CreateTime : startedAt;
                first = false;
            }
            bool added;
            do
            {
                added = false;
                foreach (var s in snapshot)
                    if (!tracked.ContainsKey(s.Id) && tracked.TryGetValue(s.ParentId, out var parentCreate) && s.CreateTime >= parentCreate)
                    {
                        tracked[s.Id] = s.CreateTime;
                        added = true;
                    }
            }
            while (added);

            bool rootExited = root.HasExited;
            bool alive = !rootExited || snapshot.Any(s => s.Id != rootId && tracked.TryGetValue(s.Id, out var create) && create == s.CreateTime);
            if (!alive || rootExited && !stillInstalled()) break;
            await Task.Delay(500, ct);
        }
        try { return root.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    static async Task<bool> WaitGoneAsync(InstalledProgram p, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (Programs.Exists(p))
        {
            if (clock.Elapsed > timeout) return false;
            await Task.Delay(500, ct);
        }
        return true;
    }

    static async Task<UninstallResult> RemoveStoreAsync(InstalledProgram p, CancellationToken ct)
    {
        var fullName = p.Key[5..].Replace("'", "''");
        var script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; "
                     + $"try {{ Remove-AppxPackage -Package '{fullName}' -ErrorAction Stop }} catch {{ Write-Output ('ERR ' + $_.Exception.Message); exit 1 }}";
        var (code, output) = await Programs.PowerShellAsync(script, ct);
        if (!Programs.Exists(p)) return new(true, false, false, code, T("Uninstalled"));
        var reason = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERR ", StringComparison.Ordinal))?[4..];
        return new(false, false, false, code, string.IsNullOrWhiteSpace(reason) ? F("Could not uninstall (exit code {0})", code) : reason);
    }
}
