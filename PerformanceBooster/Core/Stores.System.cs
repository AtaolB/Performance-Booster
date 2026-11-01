using System.Management;

namespace PerformanceBooster.Core;

// One element of the running Windows entry in the boot settings ("useplatformclock", "disabledynamictick", "tscsyncpolicy").
// Windows sets none of them itself, so a value there was left by a guide or a tool. null = not set (Windows decides).
// bcdedit needs administrator rights, also for reading; its element names are the same in every language.
internal sealed class BcdStore(string element) : ISettingStore
{
    static (DateTime At, Dictionary<string, string> Values)? _cache;

    public bool Available => Elevated.IsAdmin;

    public string? Read() => Values().TryGetValue(element, out var v) ? v.ToLowerInvariant() : null;

    public void Write(string? value)
    {
        bool present = Values().ContainsKey(element);
        _cache = null;
        if (value is null && !present) return;
        var (code, _) = Shell.Output("bcdedit.exe", value is null ? $"/deletevalue {{current}} {element}" : $"/set {{current}} {element} {value}");
        if (code != 0) throw new InvalidOperationException(F("{0} exit code {1}", "bcdedit.exe", code));
    }

    // Read once for the three settings of a check.
    static Dictionary<string, string> Values()
    {
        if (_cache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5)) return c.Values;
        var (code, output) = Shell.Output("bcdedit.exe", "/enum {current}");
        if (code != 0) throw new InvalidOperationException(F("{0} exit code {1}", "bcdedit.exe", code));
        var values = Parse(output);
        _cache = (DateTime.UtcNow, values);
        return values;
    }

    // "name    value" lines after the heading and its dashes.
    internal static Dictionary<string, string> Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            int gap = line.IndexOf(' ');
            if (gap <= 0 || line.StartsWith('-')) continue;
            var value = line[gap..].Trim();
            if (value.Length > 0) values[line[..gap]] = value;
        }
        return values;
    }
}

// The page file: "auto" (Windows manages its size), "off" (none), or "custom:C:\pagefile.sys=4096-8192;..." (sizes in MB,
// 0-0 = Windows decides the size on that drive). The custom text is kept whole, so Undo puts the same sizes back.
internal sealed class PageFileStore : ISettingStore
{
    public const string CustomPrefix = "custom:";

    static ManagementScope Scope => new(@"\\.\root\cimv2", new ConnectionOptions { EnablePrivileges = true });

    public bool Available => true;

    public string? Read()
    {
        if (AutomaticManaged()) return "auto";
        var files = new List<string>();
        using (var search = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting")))
            foreach (ManagementObject o in search.Get())
                using (o)
                    files.Add($"{o["Name"]}={Convert.ToUInt32(o["InitialSize"] ?? 0u)}-{Convert.ToUInt32(o["MaximumSize"] ?? 0u)}");
        return files.Count == 0 ? "off" : CustomPrefix + string.Join(";", files);
    }

    public void Write(string? value)
    {
        bool auto = value is null or "auto";
        using (var search = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem")))
            foreach (ManagementObject o in search.Get())
                using (o)
                {
                    o["AutomaticManagedPagefile"] = auto;
                    o.Put();
                }
        if (auto) return;

        using (var search = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM Win32_PageFileSetting")))
            foreach (ManagementObject o in search.Get())
                using (o)
                    o.Delete();
        if (value == "off") return;
        if (!value!.StartsWith(CustomPrefix, StringComparison.Ordinal)) throw new ArgumentException(F("no such option: {0}", value));
        using var cls = new ManagementClass(Scope, new ManagementPath("Win32_PageFileSetting"), null);
        foreach (var (name, initial, maximum) in Files(value))
            using (var file = cls.CreateInstance())
            {
                file["Name"] = name;
                file["InitialSize"] = initial;
                file["MaximumSize"] = maximum;
                file.Put(new PutOptions { Type = PutType.CreateOnly });
            }
    }

    static bool AutomaticManaged()
    {
        using var search = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"));
        foreach (ManagementObject o in search.Get())
            using (o)
                return o["AutomaticManagedPagefile"] is true;
        return false;
    }

    internal static IEnumerable<(string Name, uint Initial, uint Maximum)> Files(string value) =>
        value[CustomPrefix.Length..].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (Index: p.LastIndexOf('='), Text: p))
            .Where(x => x.Index > 0)
            .Select(x =>
            {
                var sizes = x.Text[(x.Index + 1)..].Split('-');
                return (x.Text[..x.Index], uint.Parse(sizes[0]), uint.Parse(sizes.Length > 1 ? sizes[1] : sizes[0]));
            });

    // The custom sizes as the options show them: "C: 4096–8192 MB".
    internal static string? Summary(string? raw) =>
        raw is not null && raw.StartsWith(CustomPrefix, StringComparison.Ordinal)
            ? string.Join(", ", Files(raw).Select(f => $"{Path.GetPathRoot(f.Name)?.TrimEnd('\\')} "
                                                      + (f.Initial == 0 && f.Maximum == 0 ? T("size managed by Windows") : $"{f.Initial}–{f.Maximum} MB")))
            : raw;
}

// Memory compression (MMAgent): "1" on, "0" off. Read like Get-MMAgent (the class's static Get method), changed with
// Enable-MMAgent / Disable-MMAgent. It works through the SysMain service; with SysMain disabled it is always off.
internal sealed class MemoryCompressionStore : ISettingStore
{
    public bool Available => Elevated.IsAdmin;

    public string? Read()
    {
        using var agent = new ManagementClass(@"\\.\root\Microsoft\Windows\PS_MMAgent", "PS_MMAgent", null);
        using var result = agent.InvokeMethod("Get", null, null);
        return result?["cmdletOutput"] is ManagementBaseObject output ? output["MemoryCompression"] is true ? "1" : "0" : null;
    }

    public static bool SysMainDisabled() =>
        Reg.Get(Microsoft.Win32.RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\SysMain", "Start") is 4;

    public void Write(string? value) =>
        Shell.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{(value == "0" ? "Disable" : "Enable")}-MMAgent -MemoryCompression\"");
}
