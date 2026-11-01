using System.Net.NetworkInformation;

namespace PerformanceBooster.Core;

// Windows looks for other Wi-Fi networks about once a minute, also while connected; during each scan the adapter leaves
// its channel for a moment and the ping jumps. While a game runs on Wi-Fi, the automatic configuration of that adapter is
// paused (the connection stays; Windows does not search or switch networks) and turned back on when the game closes.
// The paused adapter is saved, so a crash of the app does not leave it paused. Needs administrator rights.
public static class WifiScan
{
    static string StateFile => Path.Combine(AppPaths.Folder, "wifi-paused.json");

    public static string? Paused { get; private set; }

    // The Wi-Fi adapter carrying the connection, or null on a cable.
    public static string? ActiveAdapter() =>
        NetInfo.Active() is { NetworkInterfaceType: NetworkInterfaceType.Wireless80211 } n ? n.Name : null;

    public static bool Pause(string adapter)
    {
        if (!Elevated.IsAdmin || Paused is not null) return Paused == adapter;
        if (!Set(adapter, false)) return false;
        Paused = adapter;
        try { AppPaths.WriteJson(StateFile, adapter, AppJson.Default.String); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return true;
    }

    public static void Resume()
    {
        if (Paused is not { } adapter) return;
        Set(adapter, true);
        Paused = null;
        try { File.Delete(StateFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // An adapter left paused by a previous run that ended unexpectedly.
    public static void RestoreLeftovers()
    {
        if (AppPaths.ReadJson(StateFile, AppJson.Default.String) is not { Length: > 0 } adapter || !Elevated.IsAdmin) return;
        Paused = adapter;
        Resume();
    }

    static bool Set(string adapter, bool on)
    {
        try
        {
            var (code, _) = Shell.Output("netsh.exe", $"wlan set autoconfig enabled={(on ? "yes" : "no")} interface=\"{adapter.Replace("\"", "")}\"");
            return code == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
