using Microsoft.Win32;

namespace PerformanceBooster.Core;

// Windows settings of one game's exe, the same ones Settings > Display > Graphics and the exe's Compatibility tab write
// (for the signed-in user, keyed by the full path).
public static class GameCompat
{
    const string GpuPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string FullscreenFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public const int GpuDefault = 0, GpuPowerSaving = 1, GpuHighPerformance = 2;

    // "GpuPreference=2;" among the other "Name=Value;" pairs of the exe; null when Windows decides.
    public static int? GpuPreference(string exe) =>
        Pairs(Reg.Get(RegistryHive.CurrentUser, GpuPath, exe) as string).TryGetValue("GpuPreference", out var v) && int.TryParse(v, out var n) ? n : null;

    public static void SetGpuPreference(string exe, int? preference)
    {
        var pairs = Pairs(Reg.Get(RegistryHive.CurrentUser, GpuPath, exe) as string);
        if (preference is int p) pairs["GpuPreference"] = p.ToString();
        else pairs.Remove("GpuPreference");
        if (pairs.Count == 0) Reg.Delete(RegistryHive.CurrentUser, GpuPath, exe);
        else Reg.Set(RegistryHive.CurrentUser, GpuPath, exe, string.Concat(pairs.Select(kv => $"{kv.Key}={kv.Value};")), RegistryValueKind.String);
    }

    internal static Dictionary<string, string> Pairs(string? text) =>
        (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);

    // "Disable fullscreen optimizations": the flag in the exe's compatibility layers ("~ FLAG FLAG").
    public static bool FullscreenOptimizationsOff(string exe) =>
        Flags(Reg.Get(RegistryHive.CurrentUser, LayersPath, exe) as string).Contains(FullscreenFlag, StringComparer.OrdinalIgnoreCase);

    public static void SetFullscreenOptimizationsOff(string exe, bool off)
    {
        var text = Reg.Get(RegistryHive.CurrentUser, LayersPath, exe) as string;
        var flags = Flags(text).Where(f => !f.Equals(FullscreenFlag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (off) flags.Add(FullscreenFlag);
        // The Compatibility tab writes "~ " in front; a value written another way keeps its own form.
        string prefix = string.IsNullOrWhiteSpace(text) || text!.TrimStart().StartsWith('~') ? "~ " : "";
        if (flags.Count == 0) Reg.Delete(RegistryHive.CurrentUser, LayersPath, exe);
        else Reg.Set(RegistryHive.CurrentUser, LayersPath, exe, prefix + string.Join(" ", flags), RegistryValueKind.String);
    }

    internal static List<string> Flags(string? text) =>
        (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(f => f != "~").ToList();

    // More than one graphics card (a laptop's built-in and dedicated one): only then does the choice matter.
    public static bool HasSeveralGpus() =>
        Devices.Snapshot().Count(d => d.IsPci && !d.Disabled && d.ClassCode is "0300" or "0302" or "0380") > 1;
}
