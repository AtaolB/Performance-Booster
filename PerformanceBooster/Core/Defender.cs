using System.Management;

namespace PerformanceBooster.Core;

// Instead of turning Windows Defender off (Tamper Protection blocks that anyway), the listed games are
// excluded as processes: the files a game opens are not scanned in real time. Full paths only, so another program with
// the same file name is still scanned. The paths are learned when a game runs; changes need administrator rights.
public static class Defender
{
    const string Scope = @"\\.\root\Microsoft\Windows\Defender";

    // Defender is the active antivirus with real-time protection on; null if it cannot be read.
    public static bool? Active()
    {
        try
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT AMRunningMode, RealTimeProtectionEnabled FROM MSFT_MpComputerStatus"));
            foreach (ManagementObject o in search.Get())
                using (o)
                    return o["RealTimeProtectionEnabled"] is true && (o["AMRunningMode"] as string ?? "Normal") == "Normal";
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        return null;
    }

    // Exclusions to add and to remove so that exactly the listed games with a known path are excluded.
    public static (List<string> Add, List<string> Remove) Plan(AppSettings settings)
    {
        var wanted = settings.DefenderExclude
            ? settings.Games.Select(g => settings.GamePaths.GetValueOrDefault(g)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var done = settings.DefenderExcluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (wanted.Except(done, StringComparer.OrdinalIgnoreCase).Order().ToList(), done.Except(wanted, StringComparer.OrdinalIgnoreCase).Order().ToList());
    }

    // Listed games whose path is not known yet (they have not run while the app was open).
    public static List<string> Unknown(AppSettings settings) => settings.Games.Where(g => !settings.GamePaths.ContainsKey(g)).ToList();

    public sealed record Change(List<string> Add, List<string> Remove);

    // In the elevated copy (Add-MpPreference / Remove-MpPreference -ExclusionProcess).
    internal static int Apply(string file, List<string> log)
    {
        var change = AppPaths.ReadJson(file, AppJson.Default.Change);
        if (change is null) return 1;
        try
        {
            using var preference = new ManagementClass(new ManagementScope(Scope), new ManagementPath("MSFT_MpPreference"), null);
            if (change.Add.Count > 0) Invoke(preference, "Add", change.Add);
            if (change.Remove.Count > 0) Invoke(preference, "Remove", change.Remove);
            foreach (var path in change.Add) log.Add($"{T("OK   ")} {F("Not scanned by Defender: {0}", path)}");
            foreach (var path in change.Remove) log.Add($"{T("OK   ")} {F("Scanned by Defender again: {0}", path)}");
            return 0;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log.Add($"{T("ERROR")} {F("Defender exclusions: {0}", ex.Message)}");
            return 1;
        }
    }

    static void Invoke(ManagementClass preference, string method, List<string> processes)
    {
        using var input = preference.GetMethodParameters(method);
        input["ExclusionProcess"] = processes.ToArray();
        using var output = preference.InvokeMethod(method, input, null);
        if (output?["ReturnValue"] is uint code && code != 0) throw new InvalidOperationException(F("{0} returned {1:X8}", method, code));
    }
}
