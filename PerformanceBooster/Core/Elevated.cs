using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text;

namespace PerformanceBooster.Core;

// The original value of each setting is saved before the app changes it for the first time. "Undo" returns to this value.
public static class Backups
{
    public sealed record Entry(string? Value, DateTime At);

    public static Dictionary<string, Entry> Load() => AppPaths.ReadJson(AppPaths.Backups, AppJson.Default.Backups) ?? [];

    public static void Save(Dictionary<string, Entry> entries) => AppPaths.WriteJson(AppPaths.Backups, entries, AppJson.Default.Backups);
}

public sealed record ElevatedOutcome(bool Started, int ExitCode, IReadOnlyList<string> Log, bool NeedsReboot);

// System changes are made in a second copy of the app started as administrator:
//   --set id=option,id=option [--rp]   --restore all|id,id   --startup location nameBase64 1|0
//   --restorepoint   --wu-install updateId,updateId   --report file.txt   --replace-exe downloaded.exe target.exe
//   --memory-task install|remove   --purge-standby   --defender change.json   --latency seconds result.json [stopEvent]
public static class Elevated
{
    public const string RebootMarker = "#REBOOT";

    // The app itself runs as administrator: the published exe asks for it at start; a development build does not.
    // Then the copies it starts are elevated without another permission prompt.
    public static bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    // "runas" only when the app is not elevated yet; an elevated app starts elevated copies as they are.
    internal static string ElevateVerb => IsAdmin ? "" : "runas";

    public static readonly string[] Commands = ["--set", "--restore", "--startup", "--restorepoint", "--wu-install", "--report", "--leftovers", "--replace-exe", "--memory-task", "--purge-standby", "--defender", "--latency"];

    static string Ok => T("OK   ");
    static string Fail => T("ERROR");
    static string Skip => T("SKIPPED");

    public static int RunCli(string[] args)
    {
        Catalog.RefreshNic();
        if (args[0] == "--report")
        {
            if (args.Length < 2) return 2;
            File.WriteAllText(args[1], Report(), new UTF8Encoding(true));
            return 0;
        }

        var log = new List<string>();
        var effects = new Effects();
        int failures = args[0] switch
        {
            "--set" => Set(args.Length > 1 ? args[1] : "", args.Contains("--rp"), log, effects),
            "--restore" => Restore(args.Length > 1 ? args[1] : "all", log, effects),
            "--startup" when args.Length >= 4 => StartupToggle(args[1], args[2], args[3] == "1", log),
            "--restorepoint" => CreateRestorePoint(log) ? 0 : 1,
            "--wu-install" when args.Length >= 2 => InstallDrivers(args[1], log, effects),
            "--leftovers" when args.Length >= 2 => DeleteLeftovers(args[1], log),
            "--replace-exe" when args.Length >= 3 => ReplaceExe(args[1], args[2], log),
            "--memory-task" when args.Length >= 2 => StandbyCleaner.Task(args[1], log),
            "--purge-standby" => StandbyCleaner.PurgeOnce(log),
            "--defender" when args.Length >= 2 => Defender.Apply(args[1], log),
            "--latency" when args.Length >= 3 => LatencyTrace.RunCli(args[1], args[2], args.Length >= 4 ? args[3] : null, log),
            _ => 1,
        };
        failures += effects.Finish(log);
        if (log.Count == 0) log.Add(T("There was nothing to change."));
        WriteLog(log);
        return failures == 0 ? 0 : 1;
    }

    // App update when the exe is in a folder that needs administrator rights (like Program Files).
    static int ReplaceExe(string downloaded, string target, List<string> log)
    {
        try
        {
            SelfUpdate.Replace(downloaded, target);
            log.Add($"{Ok} {target}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Add($"{Fail} {F("The update could not be installed: {0}", ex.Message)}");
            return 1;
        }
    }

    static void WriteLog(List<string> log)
    {
        Directory.CreateDirectory(AppPaths.Folder);
        File.WriteAllLines(AppPaths.Log, log, new UTF8Encoding(true));
    }

    internal sealed class Effects
    {
        public bool Power, Nic, Reboot;

        public void Add(Tweak t)
        {
            Power |= t.ChangesPower;
            Nic |= t.RestartsNic;
            Reboot |= t.NeedsReboot;
        }

        public int Finish(List<string> log)
        {
            int failures = 0;
            if (Power)
            {
                try { Core.Power.ReapplyActive(); }
                catch (Exception ex) { failures++; log.Add($"{Fail} {F("Could not re-activate the power plan: {0}", ex.Message)}"); }
            }
            if (Nic && Catalog.Nic is { } card)
            {
                try { card.Restart(); log.Add($"{Ok} {F("Network adapter restarted ({0})", card.Name)}"); }
                catch (Exception ex) { failures++; log.Add($"{Fail} {F("Could not restart the network adapter: {0}", ex.Message)}"); }
            }
            if (Reboot) log.Add(RebootMarker);
            return failures;
        }
    }

    // Power plan first: the other power settings are then written into the new plan.
    static IEnumerable<(Tweak Tweak, T Item)> PlanFirst<T>(IEnumerable<(Tweak Tweak, T Item)> items) =>
        items.OrderBy(x => x.Tweak.Id == "power.plan" ? 0 : 1);

    internal static int Set(string spec, bool restorePoint, List<string> log, Effects effects)
    {
        if (restorePoint) CreateRestorePoint(log);
        var backups = Backups.Load();
        int failures = 0;
        var requests = spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && Catalog.Find(p[0]) is not null)
            .Select(p => (Tweak: Catalog.Find(p[0])!, Item: p[1]));

        foreach (var (tweak, key) in PlanFirst(requests))
        {
            try
            {
                var option = tweak.Options().FirstOrDefault(o => o.Key == key) ?? throw new InvalidOperationException(F("no such option: {0}", key));
                var store = tweak.Store();
                if (!store.Available) { log.Add($"{Skip} {tweak.Title}: {T("not present on this computer")}"); continue; }
                if (!backups.ContainsKey(tweak.Id)) backups[tweak.Id] = new(store.Read(), DateTime.Now);
                store.Write(option.Value);
                Backups.Save(backups);
                effects.Add(tweak);
                log.Add($"{Ok} {tweak.Title} → {option.Label}");
            }
            catch (Exception ex)
            {
                failures++;
                log.Add($"{Fail} {tweak.Title}: {ex.Message}");
            }
        }
        return failures;
    }

    internal static int Restore(string spec, List<string> log, Effects effects)
    {
        var backups = Backups.Load();
        var ids = spec == "all" ? backups.Keys.ToList() : spec.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        int failures = 0;
        var items = ids
            .Where(id => backups.ContainsKey(id) && Catalog.Find(id) is not null)
            .Select(id => (Tweak: Catalog.Find(id)!, Item: backups[id]));

        foreach (var (tweak, entry) in PlanFirst(items))
        {
            try
            {
                tweak.Store().Write(entry.Value);
                backups.Remove(tweak.Id);
                Backups.Save(backups);
                effects.Add(tweak);
                log.Add($"{Ok} {F("{0} returned to its original value", tweak.Title)}");
            }
            catch (Exception ex)
            {
                failures++;
                log.Add($"{Fail} {tweak.Title}: {ex.Message}");
            }
        }
        return failures;
    }

    static int StartupToggle(string location, string nameBase64, bool enable, List<string> log)
    {
        try
        {
            var name = Encoding.UTF8.GetString(Convert.FromBase64String(nameBase64));
            StartupManager.SetEnabled(Enum.Parse<StartupLocation>(location), name, enable);
            log.Add($"{Ok} {(enable ? F("{0} enabled at startup", name) : F("{0} disabled at startup", name))}");
            return 0;
        }
        catch (Exception ex)
        {
            log.Add($"{Fail} {F("Startup item: {0}", ex.Message)}");
            return 1;
        }
    }

    static int InstallDrivers(string spec, List<string> log, Effects effects)
    {
        try
        {
            var ids = spec.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (lines, reboot, failures) = DriverUpdates.Install(ids);
            log.AddRange(lines);
            effects.Reboot |= reboot;
            return failures;
        }
        catch (Exception ex)
        {
            log.Add($"{Fail} {F("Driver installation: {0}", ex.Message)}");
            return 1;
        }
    }

    public sealed record LeftoverRequest(List<Leftover> Items, string BackupFolder);

    // Leftovers under Program Files, ProgramData and HKLM need admin rights; the list arrives in a JSON file.
    internal static int DeleteLeftovers(string file, List<string> log)
    {
        var request = AppPaths.ReadJson(file, AppJson.Default.LeftoverRequest);
        if (request is null) return 1;
        var (lines, failures) = Leftovers.Delete(request.Items, request.BackupFolder);
        log.AddRange(lines);
        return failures;
    }

    static bool CreateRestorePoint(List<string> log)
    {
        try
        {
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = AppPaths.Name;
            input["RestorePointType"] = 12; // MODIFY_SETTINGS
            input["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output["ReturnValue"]);
            log.Add(code == 0
                ? $"{Ok} {T("Restore point requested (Windows skips it if one was created in the last 24 hours)")}"
                : $"{T("WARNING")} {F("Could not create a restore point (code {0}); System Protection may be off", code)}");
            return code == 0;
        }
        catch (Exception ex)
        {
            log.Add($"{T("WARNING")} {F("Could not create a restore point: {0}", ex.Message)}");
            return false;
        }
    }

    public static string Report()
    {
        var sb = new StringBuilder();
        foreach (var group in Catalog.All.GroupBy(t => t.Category))
        {
            sb.AppendLine($"=== {group.Key} ===");
            foreach (var t in group)
            {
                var s = t.Evaluate();
                var tag = s.State switch
                {
                    CheckState.Ok => T("OK   "),
                    CheckState.Problem => T("ISSUE"),
                    CheckState.Info => T("CHOICE"),
                    _ => T("N/A  "),
                };
                var rec = s.Options.FirstOrDefault(o => o.Recommended);
                sb.AppendLine($"[{tag}] {t.Title} = {s.CurrentLabel}" + (s.State == CheckState.Problem && rec is not null ? $"  ({F("recommended: {0}", rec.Label)})" : ""));
            }
        }
        return sb.ToString();
    }

    // Started=false if the user declines UAC.
    public static async Task<ElevatedOutcome> RunAsync(params string[] arguments)
    {
        if (Demo.On) return new(true, 0, [], false); // the demo changes nothing
        var args = string.Join(" ", arguments.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));
        var psi = new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = ElevateVerb, WindowStyle = ProcessWindowStyle.Hidden };
        try
        {
            if (File.Exists(AppPaths.Log)) File.Delete(AppPaths.Log);
            using var p = Process.Start(psi);
            if (p is null) return new(false, -1, [], false);
            await p.WaitForExitAsync();
            var lines = File.Exists(AppPaths.Log) ? await File.ReadAllLinesAsync(AppPaths.Log) : [];
            return new(true, p.ExitCode, lines.Where(l => l != RebootMarker).ToList(), lines.Contains(RebootMarker));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new(false, -1, [], false);
        }
    }
}
