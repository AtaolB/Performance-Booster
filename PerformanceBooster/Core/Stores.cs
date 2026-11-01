using Microsoft.Win32;

namespace PerformanceBooster.Core;

// Reads/writes a setting's value as text. null = no value (Windows default).
// Backups are kept as this text too, so every kind of setting is undone the same way.
public interface ISettingStore
{
    bool Available { get; }
    string? Read();
    void Write(string? value);
}

internal sealed class RegistryStore(RegistryHive hive, Func<string?> path, string name, RegistryValueKind kind) : ISettingStore
{
    public bool Available => path() is not null;

    public string? Read() => path() is string p ? Reg.Format(Reg.Get(hive, p, name)) : null;

    public void Write(string? value)
    {
        var p = path() ?? throw new InvalidOperationException(T("Registry path not found"));
        if (value is null)
        {
            Reg.Delete(hive, p, name);
            return;
        }
        object data = kind == RegistryValueKind.DWord ? unchecked((int)uint.Parse(value)) : value;
        Reg.Set(hive, p, name, data, kind);
    }
}

// Advanced property of the network adapter (EEE, interrupt moderation...). Only shown if the adapter has it.
internal sealed class NicPropertyStore(string keyword) : ISettingStore
{
    static string? Path => Catalog.Nic?.ClassKeyPath;

    public bool Available => Path is string p && Reg.Get(RegistryHive.LocalMachine, p, keyword) is not null;

    public string? Read() => Path is string p ? Reg.Format(Reg.Get(RegistryHive.LocalMachine, p, keyword)) : null;

    public void Write(string? value)
    {
        var p = Path ?? throw new InvalidOperationException(T("Network adapter not found"));
        if (value is null) throw new InvalidOperationException(T("An adapter property cannot be deleted"));
        Reg.Set(RegistryHive.LocalMachine, p, keyword, value, RegistryValueKind.String);
    }
}

internal sealed class PowerStore(Guid sub, Guid setting) : ISettingStore
{
    public bool Available => Power.ReadAC(sub, setting) is not null;

    public string? Read() => Power.ReadAC(sub, setting)?.ToString();

    public void Write(string? value) => Power.WriteAC(sub, setting, uint.Parse(value ?? throw new ArgumentNullException(nameof(value))));
}

internal sealed class PowerPlanStore : ISettingStore
{
    public const string CreatePrefix = "create:";

    public bool Available => Power.Active() is not null;

    public string? Read() => Power.Active()?.ToString();

    public void Write(string? value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var scheme = value.StartsWith(CreatePrefix, StringComparison.Ordinal) ? Power.Duplicate(Guid.Parse(value[CreatePrefix.Length..])) : Guid.Parse(value);
        Power.SetActive(scheme);
    }
}

internal sealed class PowerModeStore : ISettingStore
{
    public bool Available => Power.Active() == Power.Balanced && Power.Overlay() is not null;

    public string? Read() => Power.Overlay()?.ToString();

    public void Write(string? value) => Power.SetOverlay(Guid.Parse(value ?? throw new ArgumentNullException(nameof(value))));
}

// One of the "Name=Value;" pairs in HKCU\...\DirectX\UserGpuPreferences\DirectXUserGlobalSettings.
internal sealed class DxFlagStore(string flag, string path = DxFlagStore.DefaultPath) : ISettingStore
{
    public const string DefaultPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    const string Name = "DirectXUserGlobalSettings";

    public bool Available => true;

    public string? Read() => Flags().TryGetValue(flag, out var v) ? v : null;

    public void Write(string? value)
    {
        var flags = Flags();
        if (value is null) flags.Remove(flag);
        else flags[flag] = value;
        if (flags.Count == 0) Reg.Delete(RegistryHive.CurrentUser, path, Name);
        else Reg.Set(RegistryHive.CurrentUser, path, Name, string.Concat(flags.Select(kv => $"{kv.Key}={kv.Value};")), RegistryValueKind.String);
    }

    Dictionary<string, string> Flags() =>
        ((Reg.Get(RegistryHive.CurrentUser, path, Name) as string) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0])
            .ToDictionary(g => g.Key, g => g.Last()[1]);
}

// Manages several values as one setting. Text format: "a|b|c", "-" = no value.
internal sealed class CompositeStore(params ISettingStore[] parts) : ISettingStore
{
    public bool Available => parts.All(p => p.Available);

    public string? Read() => string.Join("|", parts.Select(p => p.Read() ?? "-"));

    public void Write(string? value)
    {
        var values = (value ?? "").Split('|');
        if (values.Length != parts.Length) throw new ArgumentException(T("Value count does not match"));
        for (int i = 0; i < parts.Length; i++) parts[i].Write(values[i] == "-" ? null : values[i]);
    }
}

// "Enhance pointer precision". SPI instead of the registry: the change takes effect immediately.
internal sealed class MouseAccelStore : ISettingStore
{
    public bool Available => true;

    public string? Read()
    {
        var mouse = new int[3];
        return Native.SystemParametersInfo(Native.SPI_GETMOUSE, 0, mouse, 0) ? (mouse[2] == 0 ? "0" : "1") : null;
    }

    public void Write(string? value)
    {
        int[] mouse = value == "1" ? [6, 10, 1] : [0, 0, 0];
        if (!Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, mouse, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
            throw new System.ComponentModel.Win32Exception();
    }
}

// Keyboard repeat delay and rate: "delay,rate" (0-3, 0-31).
internal sealed class KeyRepeatStore : ISettingStore
{
    public bool Available => true;

    public string? Read()
    {
        uint delay = 0, speed = 0;
        return Native.SystemParametersInfo(Native.SPI_GETKEYBOARDDELAY, 0, ref delay, 0)
               && Native.SystemParametersInfo(Native.SPI_GETKEYBOARDSPEED, 0, ref speed, 0)
            ? $"{delay},{speed}"
            : null;
    }

    public void Write(string? value)
    {
        var parts = (value ?? "1,31").Split(',');
        const uint flags = Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE;
        if (!Native.SystemParametersInfo(Native.SPI_SETKEYBOARDDELAY, uint.Parse(parts[0]), IntPtr.Zero, flags)
            || !Native.SystemParametersInfo(Native.SPI_SETKEYBOARDSPEED, uint.Parse(parts[1]), IntPtr.Zero, flags))
            throw new System.ComponentModel.Win32Exception();
    }
}

// IPv4 DNS servers of the active adapter: "dhcp", "1.1.1.1,1.0.0.1", or "1.1.1.1,1.0.0.1|doh" for encrypted DNS (DNS over HTTPS):
// Windows 11 then sends every query to these servers over HTTPS ("auto-upgrade"), so a provider cannot answer them itself.
// The DNS cache is cleared after every change.
internal sealed class DnsStore : ISettingStore
{
    public const string EncryptedSuffix = "|doh";

    // netsh dns ... encryption is there from Windows 11 on.
    public static bool EncryptionSupported => Environment.OSVersion.Version.Build >= 22000;

    public bool Available => Catalog.Nic is not null;

    public string? Read()
    {
        if (Catalog.Nic is not { } nic) return null;
        var servers = (Reg.Get(RegistryHive.LocalMachine, nic.TcpipPath, "NameServer") as string ?? "")
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (servers.Length == 0) return "dhcp";
        var value = string.Join(",", servers);
        if (!EncryptionSupported || !servers.All(s => DnsBenchmark.Find(s) is not null)) return value;
        var upgraded = DohServers().Where(s => s.AutoUpgrade).Select(s => s.Address).ToHashSet();
        return servers.All(upgraded.Contains) ? value + EncryptedSuffix : value;
    }

    public void Write(string? value)
    {
        var nic = Catalog.Nic ?? throw new InvalidOperationException(T("Network adapter not found"));
        string name = nic.Name.Replace("\"", "");
        if (value is null or "dhcp")
        {
            Shell.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{name}\" source=dhcp");
            Maintenance.FlushDns();
            return;
        }
        bool encrypted = value.EndsWith(EncryptedSuffix, StringComparison.Ordinal);
        var servers = value.Replace(EncryptedSuffix, "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        Shell.Run("netsh.exe", $"interface ipv4 set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no");
        for (int i = 1; i < servers.Length; i++)
            Shell.Run("netsh.exe", $"interface ipv4 add dnsservers name=\"{name}\" address={servers[i]} index={i + 1} validate=no");
        if (EncryptionSupported)
        {
            var known = DohServers().ToDictionary(s => s.Address, s => s.Template);
            foreach (var server in servers) SetEncryption(server, encrypted, known);
        }
        Maintenance.FlushDns(); // answers of the old server are not used any more
    }

    // Encrypted: the server's DoH template with auto-upgrade, falling back to normal DNS only if HTTPS fails.
    // Not encrypted: auto-upgrade off again, so the server is used the normal way.
    static void SetEncryption(string server, bool on, Dictionary<string, string> known)
    {
        var template = known.TryGetValue(server, out var t) && t.Length > 0 ? t : DnsBenchmark.Find(server)?.DohTemplate;
        if (template is null) return;
        var args = $"encryption server={server} dohtemplate={template} autoupgrade={(on ? "yes" : "no")} udpfallback=yes";
        if (known.ContainsKey(server)) Shell.Run("netsh.exe", "dns set " + args);
        else if (on) Shell.Run("netsh.exe", "dns add " + args);
    }

    // The DoH servers Windows knows (MSFT_DNSClientDohServerAddress).
    static List<(string Address, string Template, bool AutoUpgrade)> DohServers()
    {
        var list = new List<(string, string, bool)>();
        try
        {
            using var search = new System.Management.ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT ServerAddress, DohTemplate, AutoUpgrade FROM MSFT_DNSClientDohServerAddress");
            foreach (System.Management.ManagementObject o in search.Get())
                using (o)
                    if (o["ServerAddress"] is string address)
                        list.Add((address, o["DohTemplate"] as string ?? "", o["AutoUpgrade"] is true));
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        return list;
    }
}

// Without a HwSchMode registry value, the driver's current state is read (2 = on, 1 = off).
internal sealed class HagsStore : ISettingStore
{
    const string Path = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    public bool Available => Gpu.HardwareScheduling().Supported;

    public string? Read() =>
        Reg.Format(Reg.Get(RegistryHive.LocalMachine, Path, "HwSchMode")) ?? (Gpu.HardwareScheduling().Enabled ? "2" : "1");

    public void Write(string? value)
    {
        if (value is null) Reg.Delete(RegistryHive.LocalMachine, Path, "HwSchMode");
        else Reg.Set(RegistryHive.LocalMachine, Path, "HwSchMode", int.Parse(value), RegistryValueKind.DWord);
    }
}

internal sealed class HibernateStore : ISettingStore
{
    const string Path = @"SYSTEM\CurrentControlSet\Control\Power";

    public bool Available => true;

    public string? Read() =>
        Reg.Format(Reg.Get(RegistryHive.LocalMachine, Path, "HibernateEnabled") ?? Reg.Get(RegistryHive.LocalMachine, Path, "HibernateEnabledDefault"));

    public void Write(string? value) => Shell.Run("powercfg.exe", value == "0" ? "/hibernate off" : "/hibernate on");
}
