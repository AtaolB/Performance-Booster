using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

internal static class Reg
{
    static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static object? Get(RegistryHive hive, string path, string name)
    {
        try
        {
            using var root = Root(hive);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Set(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var root = Root(hive);
        using var key = root.CreateSubKey(path, true);
        key.SetValue(name, value, kind);
    }

    public static void Delete(RegistryHive hive, string path, string name)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(path, true);
        key?.DeleteValue(name, false);
    }

    public static void DeleteTree(RegistryHive hive, string path)
    {
        using var root = Root(hive);
        root.DeleteSubKeyTree(path, false);
    }

    // DWORDs are shown unsigned (0xFFFFFFFF = 4294967295).
    public static string? Format(object? value) => value switch
    {
        null => null,
        int i => unchecked((uint)i).ToString(),
        string[] a => string.Join(",", a),
        byte[] b => Convert.ToHexString(b),
        _ => value.ToString(),
    };

    public static string[] SubKeyNames(RegistryHive hive, string path)
    {
        try
        {
            using var root = Root(hive);
            using var key = root.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static string[] ValueNames(RegistryHive hive, string path)
    {
        try
        {
            using var root = Root(hive);
            using var key = root.OpenSubKey(path);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal static class Power
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    public static readonly Guid SubWireless = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

    public static Guid? Active()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { Native.LocalFree(ptr); }
    }

    public static List<(Guid Id, string Name)> Schemes()
    {
        var list = new List<(Guid, string)>();
        for (uint i = 0; ; i++)
        {
            var guid = Guid.Empty;
            uint size = (uint)Marshal.SizeOf<Guid>();
            if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.ACCESS_SCHEME, i, ref guid, ref size) != 0) break;
            list.Add((guid, FriendlyName(guid)));
        }
        return list;
    }

    public static string FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0)
            return scheme.ToString();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return Native.PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                ? Marshal.PtrToStringUni(buffer) ?? scheme.ToString()
                : scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Hidden settings (like core parking) are read too.
    public static uint? ReadAC(Guid sub, Guid setting)
    {
        if (Active() is not Guid scheme) return null;
        return Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value) == 0 ? value : null;
    }

    public static void WriteAC(Guid sub, Guid setting, uint value)
    {
        var scheme = Active() ?? throw new InvalidOperationException(T("Could not read the active power plan"));
        var result = Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (result != 0) throw new Win32Exception((int)result);
    }

    public static void SetActive(Guid scheme)
    {
        var result = Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != 0) throw new Win32Exception((int)result);
    }

    // Written values only apply after the plan is reactivated.
    public static void ReapplyActive()
    {
        if (Active() is Guid scheme) SetActive(scheme);
    }

    // Creates a copy of a hidden template plan (High/Ultimate performance).
    public static Guid Duplicate(Guid template)
    {
        var copy = Guid.NewGuid();
        Shell.Run("powercfg.exe", $"/duplicatescheme {template} {copy}");
        return copy;
    }

    public static Guid? Overlay()
    {
        try { return Native.PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public static void SetOverlay(Guid overlay)
    {
        var result = Native.PowerSetActiveOverlayScheme(overlay);
        if (result != 0) throw new Win32Exception((int)result);
    }
}

internal static class Cpu
{
    static readonly Lazy<bool> _hybrid = new(DetectHybrid);

    // Processor with P and E cores (Intel 12th gen and later).
    public static bool IsHybrid => _hybrid.Value;

    static bool DetectHybrid()
    {
        Native.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var length, IntPtr.Zero, 0);
        if (length == 0) return false;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return false;
            var classes = new HashSet<byte>();
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0) break;
                classes.Add(Marshal.ReadByte(buffer, offset + 18)); // SYSTEM_CPU_SET_INFORMATION.CpuSet.EfficiencyClass
                offset += size;
            }
            return classes.Count > 1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string Name() =>
        (Reg.Get(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") as string)?.Trim() ?? T("Processor");

    public static int BaseMhz() =>
        Reg.Get(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz") is int mhz ? mhz : 0;
}

internal static class Gpu
{
    // Hardware-accelerated GPU scheduling: support and actual (current) state of the adapters.
    // D3DKMT_WDDM_2_7_CAPS: bit0 HwSchSupported, bit1 HwSchEnabled.
    public static (bool Supported, bool Enabled) HardwareScheduling()
    {
        bool supported = false, enabled = false;
        var e = new Native.D3DKMT_ENUMADAPTERS2();
        if (Native.D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return (false, false);
        const int infoSize = 20; // D3DKMT_ADAPTERINFO: handle + LUID + source count + BOOL
        e.Adapters = Marshal.AllocHGlobal(infoSize * (int)e.NumAdapters);
        var caps = Marshal.AllocHGlobal(4);
        try
        {
            if (Native.D3DKMTEnumAdapters2(ref e) != 0) return (false, false);
            for (int i = 0; i < e.NumAdapters; i++)
            {
                uint handle = (uint)Marshal.ReadInt32(e.Adapters, i * infoSize);
                Marshal.WriteInt32(caps, 0);
                var q = new Native.D3DKMT_QUERYADAPTERINFO { Adapter = handle, Type = Native.KMTQAITYPE_WDDM_2_7_CAPS, PrivateDriverData = caps, PrivateDriverDataSize = 4 };
                if (Native.D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    int value = Marshal.ReadInt32(caps);
                    supported |= (value & 1) != 0;
                    enabled |= (value & 2) != 0;
                }
                var close = new Native.D3DKMT_CLOSEADAPTER { Adapter = handle };
                Native.D3DKMTCloseAdapter(ref close);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(caps);
            Marshal.FreeHGlobal(e.Adapters);
        }
        return (supported, enabled);
    }
}

internal sealed class NicInfo
{
    const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string InterfaceGuid { get; init; }
    public required NetworkInterfaceType Type { get; init; }
    public required long SpeedBps { get; init; }
    public string? ClassKeyPath { get; init; }

    // *SpeedDuplex options: 6 = 1 Gbps full duplex, larger values 2.5/5/10 Gbps.
    public bool SupportsGigabit { get; init; }

    public string TcpipPath => $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{InterfaceGuid}";
    public bool IsEthernet => Type == NetworkInterfaceType.Ethernet;

    public static NicInfo? Detect()
    {
        var n = NetInfo.Active();
        if (n is null) return null;
        string? classPath = null;
        foreach (var sub in Reg.SubKeyNames(RegistryHive.LocalMachine, ClassRoot))
        {
            var path = ClassRoot + "\\" + sub;
            if (string.Equals(Reg.Get(RegistryHive.LocalMachine, path, "NetCfgInstanceId") as string, n.Id, StringComparison.OrdinalIgnoreCase))
            {
                classPath = path;
                break;
            }
        }
        return new NicInfo
        {
            Name = n.Name,
            Description = n.Description,
            InterfaceGuid = n.Id,
            Type = n.NetworkInterfaceType,
            SpeedBps = n.Speed,
            ClassKeyPath = classPath,
            SupportsGigabit = classPath is not null && Reg.ValueNames(RegistryHive.LocalMachine, classPath + @"\Ndi\Params\*SpeedDuplex\enum")
                .Any(v => int.TryParse(v, out var mode) && mode >= 6),
        };
    }

    public static bool HasWireless()
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211); }
        catch (NetworkInformationException) { return false; }
    }

    public void Restart() =>
        Shell.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"Restart-NetAdapter -Name '{Name.Replace("'", "''")}' -Confirm:$false\"");
}

internal static class Privilege
{
    // Enables a privilege the process token holds (administrators hold the ones the app uses, disabled by default).
    public static bool Enable(string name)
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, name, out var luid)) return false;
            var state = new Native.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Native.SE_PRIVILEGE_ENABLED };
            // Succeeds even when the privilege is not held; the last error tells (ERROR_NOT_ALL_ASSIGNED = 1300).
            return Native.AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }
}

internal static class Shell
{
    // Console tools (bcdedit, sc, schtasks, pnputil, netsh, reg, nvidia-smi) write in Windows' console code page (OEM: 857 on Turkish
    // Windows, 437 on English). Without it set, .NET would read their output as UTF-8.
    public static Encoding ConsoleEncoding { get; } = CodePage(Native.GetOEMCP());

    // Windows' ANSI code page (1254 on Turkish Windows, 1252 on English): the byte strings of older Windows APIs.
    public static Encoding AnsiEncoding { get; } = CodePage(Native.GetACP());

    static Encoding CodePage(uint page)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)page);
    }

    public static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(F("Could not start {0}", exe));
        if (!p.WaitForExit(60_000)) throw new TimeoutException(F("{0} did not respond", exe));
        if (p.ExitCode != 0) throw new InvalidOperationException(F("{0} exit code {1}", exe, p.ExitCode));
    }

    // Exit code and standard output (-1 when it did not finish in time).
    public static (int Code, string Output) Output(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = ConsoleEncoding };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(F("Could not start {0}", exe));
        var output = p.StandardOutput.ReadToEnd();
        return (p.WaitForExit(30_000) ? p.ExitCode : -1, output);
    }

    // Opens the folder of a file in Explorer with the file selected.
    public static void ShowInFolder(string path)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Win32Exception) { }
    }

    // When the app runs as administrator, links and files are opened through Explorer, so the browser or viewer does not run as administrator too.
    public static void Open(string uri)
    {
        try
        {
            var psi = Elevated.IsAdmin
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{uri}\"") { UseShellExecute = false }
                : new ProcessStartInfo(uri) { UseShellExecute = true };
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception) { }
    }
}
