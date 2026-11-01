using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace PerformanceBooster.Core;

public enum ProgramSource { Desktop, Store }

// Broken entry: the program folder was deleted by hand but the entry stayed in the Programs list (in Windows too).
public enum ProgramHealth { Ok, MissingUninstaller, MissingFolder }

// Installed program. Key: "HKLM|<registry path>", "HKCU|<registry path>" or "MSIX|<package full name>".
public sealed record InstalledProgram(
    string Key, string Name, string Publisher, string Version, DateTime? InstallDate, long SizeBytes,
    string? IconPath, string? InstallLocation, string? UninstallCommand, string? QuietCommand,
    ProgramSource Source, bool MachineWide)
{
    // Folders the program runs from (install, icon and uninstaller folders); generic folders are skipped.
    public IReadOnlyList<string> Dirs { get; init; } = [];

    // Package family for Store packages (Name_PublisherId); Start menu records match on it.
    public string? PackageFamily { get; init; }

    public ProgramHealth Health { get; init; }
    public bool IsBroken => Health != ProgramHealth.Ok;

    // A program whose uninstaller was deleted cannot be uninstalled; its entry is removed from the list instead.
    public bool CanUninstall => (Source == ProgramSource.Store || UninstallCommand is not null || QuietCommand is not null) && Health != ProgramHealth.MissingUninstaller;
    public bool CanUninstallQuietly => Source == ProgramSource.Store || QuietCommand is not null;
    public bool IsSystemLike => Programs.IsSystemLike(this);
}

// Programs and Features list (registry) and Microsoft Store packages.
public static partial class Programs
{
    const string UninstallSuffix = @"Microsoft\Windows\CurrentVersion\Uninstall";
    const string StoreRepository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    internal static readonly (RegistryHive Hive, string Root)[] Roots =
    [
        (RegistryHive.LocalMachine, @"SOFTWARE\" + UninstallSuffix),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\" + UninstallSuffix),
        (RegistryHive.CurrentUser, @"Software\" + UninstallSuffix),
        (RegistryHive.CurrentUser, @"Software\WOW6432Node\" + UninstallSuffix),
    ];

    // Fake PowerShell in tests: (exe, arguments prepended).
    internal static (string Exe, string Prefix)? PowerShellOverride { get; set; }

    public static async Task<List<InstalledProgram>> ListAsync(bool includeStore, CancellationToken ct)
    {
        var desktop = Task.Run(Desktop, ct);
        var store = includeStore ? StoreAsync(ct) : Task.FromResult(new List<InstalledProgram>());
        await Task.WhenAll(desktop, store);
        return desktop.Result.Concat(store.Result).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // --- Desktop programs ---

    public static List<InstalledProgram> Desktop()
    {
        var list = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, root) in Roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var uninstall = Open(baseKey, root);
            if (uninstall is null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var key = Open(uninstall, sub);
                if (key is null || Read(key, hive, root + "\\" + sub, sub) is not { } p) continue;
                // The same program may be registered in both views (64/32-bit).
                if (seen.Add(p.Name + "|" + p.Version)) list.Add(p);
            }
        }
        return list;
    }

    static RegistryKey? Open(RegistryKey parent, string path)
    {
        try { return parent.OpenSubKey(path); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // What Control Panel shows: entries with a name, not system components or updates, with an uninstall command.
    internal static InstalledProgram? Read(RegistryKey key, RegistryHive hive, string path, string keyName)
    {
        if (key.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) return null;
        if (key.GetValue("SystemComponent") is int system && system == 1) return null;
        if (key.GetValue("ParentKeyName") is string) return null;
        if (key.GetValue("ReleaseType") is string release && (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;
        var uninstall = Blank(key.GetValue("UninstallString") as string);
        var quiet = Blank(key.GetValue("QuietUninstallString") as string);
        bool msi = key.GetValue("WindowsInstaller") is int wi && wi == 1;
        if (uninstall is null && quiet is null && !msi) return null;

        var code = ProductCode(keyName, uninstall, msi);
        string? interactive = code is not null ? $"MsiExec.exe /X{code}" : uninstall;
        string? silent = QuietCommand(keyName, quiet, uninstall, code);
        if (key.GetValue("NoRemove") is int noRemove && noRemove == 1) interactive = silent = null;

        var location = Blank(key.GetValue("InstallLocation") as string)?.Trim('"');
        var icon = Blank(key.GetValue("DisplayIcon") as string);
        var dirs = new List<string>();
        InstallDirs.Add(dirs, location);
        InstallDirs.Add(dirs, InstallDirs.ExeDir(icon));
        if (uninstall is not null && code is null)
        {
            var file = SplitCommand(uninstall).File;
            if (!Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(file).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
                InstallDirs.Add(dirs, InstallDirs.ExeDir(file));
        }

        // Without the uninstaller file (folder deleted by hand) uninstalling fails everywhere; if the install folder is gone,
        // the uninstaller may run but not find the program. A folder on a drive that is not connected (external disk) is not counted as broken.
        var health = ProgramHealth.Ok;
        if (code is null && (interactive ?? silent) is not null && !CommandExists(interactive) && !CommandExists(silent))
            health = ProgramHealth.MissingUninstaller;
        else if (code is null && location is not null && FolderDeleted(location)) // MSI is uninstalled from the cached package even when the folder is gone
            health = ProgramHealth.MissingFolder;

        long size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0;
        DateTime? date = key.GetValue("InstallDate") is string d && DateTime.TryParseExact(d.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return new InstalledProgram(
            (hive == RegistryHive.LocalMachine ? "HKLM|" : "HKCU|") + path, name.Trim(), (key.GetValue("Publisher") as string ?? "").Trim(),
            (key.GetValue("DisplayVersion") as string ?? "").Trim(), date, size, icon, location, interactive, silent,
            ProgramSource.Desktop, hive == RegistryHive.LocalMachine)
        { Dirs = dirs, Health = health };
    }

    // A command without a path (MsiExec.exe, rundll32) is resolved by Windows and assumed to exist; a full path is checked.
    internal static bool CommandExists(string? command)
    {
        if (command is null) return false;
        var file = SplitCommand(command).File;
        try { return !Path.IsPathRooted(file) || File.Exists(file); }
        catch (ArgumentException) { return false; }
    }

    internal static bool FolderDeleted(string location)
    {
        try
        {
            var full = Environment.ExpandEnvironmentVariables(location);
            if (!Path.IsPathRooted(full)) return false;
            var root = Path.GetPathRoot(full);
            return root is not null && Directory.Exists(root) && !Directory.Exists(full);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // MSI product code: when the key name is a GUID and uninstalling goes through msiexec.
    internal static string? ProductCode(string keyName, string? uninstall, bool msi) =>
        Guid().IsMatch(keyName) && (msi || uninstall?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) == true) ? keyName : null;

    // Silent uninstall: the program's own silent command, MSI (/qn) or Inno Setup (/VERYSILENT). None for unknown uninstallers.
    internal static string? QuietCommand(string keyName, string? quiet, string? uninstall, string? productCode)
    {
        if (quiet is not null) return quiet;
        if (productCode is not null) return $"MsiExec.exe /X{productCode} /qn /norestart";
        if (uninstall is not null && keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
            return uninstall + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
        return null;
    }

    // "C:\Program Files\A B\unins000.exe" /x  ->  (file, arguments). Unquoted paths with spaces are read up to ".exe".
    internal static (string File, string Args) SplitCommand(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (command[..(exe + 4)], command[(exe + 4)..].Trim());
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    public static bool Exists(InstalledProgram p)
    {
        if (p.Source == ProgramSource.Store)
            return Reg.SubKeyNames(RegistryHive.CurrentUser, StoreRepository).Contains(p.Key[5..], StringComparer.OrdinalIgnoreCase);
        int bar = p.Key.IndexOf('|');
        var hive = p.Key.StartsWith("HKLM", StringComparison.Ordinal) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = Open(root, p.Key[(bar + 1)..]);
        return key is not null;
    }

    [GeneratedRegex(@"CN=([^,]+)")]
    private static partial Regex CommonName();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex Guid();

    // --- Components whose removal may break other programs (Store: winget, Store, Xbox/Game Bar runtime, driver panels) ---

    // Driver names can be in the Windows language (an NVIDIA driver on Turkish Windows); those names are in the language files.
    static readonly Lazy<Regex> _systemComponent = new(() =>
    {
        var driver = string.Join("|", L.WindowsNames("Driver").Select(Regex.Escape));
        return new Regex(@"^(Microsoft Visual C\+\+ .*Redistributable|Microsoft \.NET|Microsoft ASP\.NET|Microsoft Windows Desktop Runtime|Microsoft Edge( WebView2.*)?$|Microsoft Update Health Tools|Microsoft GameInput|Windows Software Development Kit|Windows App Runtime|Windows Driver Package|"
            + $@"NVIDIA .*({driver}|PhysX|HD Audio|USBC|FrameView|Container)|Realtek .*({driver}|Audio|Ethernet|Card Reader)|Intel\(R\) .*({driver}|Management Engine|Chipset|Serial IO|Rapid Storage|Graphics)|AMD .*({driver}|Chipset|Software)|DirectX)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    });

    static Regex SystemComponent() => _systemComponent.Value;

    [GeneratedRegex(@"^(Microsoft\.DesktopAppInstaller|Microsoft\.WindowsStore|Microsoft\.StorePurchaseApp|Microsoft\.GamingServices|Microsoft\.XboxIdentityProvider|Microsoft\.XboxGamingOverlay|Microsoft\.Xbox\.TCUI|Microsoft\.SecHealthUI|Microsoft\.VCLibs|Microsoft\.UI\.Xaml|Microsoft\.NET|MicrosoftCorporationII\.WinAppRuntime|Microsoft\.WindowsAppRuntime|NVIDIACorp\.NVIDIAControlPanel|RealtekSemiconductorCorp\.|AppUp\.Intel)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemPackage();

    internal static bool IsSystemLike(InstalledProgram p) =>
        p.Source == ProgramSource.Store ? SystemPackage().IsMatch(p.Key[5..]) : SystemComponent().IsMatch(p.Name);

    // --- Name cleanup (for leftover and usage matching) ---

    [GeneratedRegex(@"\((?:x64|x86|64[- ]?bit|32[- ]?bit|amd64|arm64|[^)]*\d+\.\d+[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Qualifier();

    [GeneratedRegex(@"\s+-?\s*v?\d+(?:\.\d+)+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@",?\s+(?:Inc\.?|Ltd\.?|LLC|L\.L\.C\.|GmbH|Corporation|Corp\.?|Co\.,?\s*Ltd\.?|Limited|S\.A\.|s\.r\.o\.|AB|AG|B\.V\.|Pty\.? Ltd\.?|Software)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    // "7-Zip 24.08 (x64)" -> "7-Zip", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40" -> "...Redistributable"
    internal static string CleanName(string name)
    {
        var s = Qualifier().Replace(name.Replace("…", ""), "");
        s = TrailingVersion().Replace(s.Trim(), "");
        return Spaces().Replace(s, " ").Trim(' ', '-', '_', '.', ',');
    }

    // "Valve Corporation" -> "Valve"
    internal static string CleanPublisher(string publisher)
    {
        var s = publisher.Trim();
        for (int i = 0; i < 2; i++) s = CompanySuffix().Replace(s, "").Trim(' ', ',', '.');
        return s;
    }

    // --- Microsoft Store packages ---

    // Removable app packages: excluding frameworks, resource packages and Windows system packages.
    const string StoreScript = """
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $ErrorActionPreference = 'SilentlyContinue'
        $ProgressPreference = 'SilentlyContinue'
        @(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable -and [string]$_.SignatureKind -ne 'System' } |
          ForEach-Object { [pscustomobject]@{ FullName = $_.PackageFullName; Family = $_.PackageFamilyName; Name = $_.Name; Publisher = $_.Publisher; Version = [string]$_.Version; Location = $_.InstallLocation } }) |
          ConvertTo-Json -Compress
        """;

    internal sealed record StorePackage(string FullName, string Family, string Name, string Publisher, string Version, string Location);

    static async Task<List<InstalledProgram>> StoreAsync(CancellationToken ct)
    {
        try
        {
            var (code, output) = await PowerShellAsync(StoreScript, ct);
            var json = output.Trim();
            if (code != 0 || json.Length == 0) return [];
            var packages = json.StartsWith('[')
                ? JsonSerializer.Deserialize(json, AppJson.Default.StorePackages) ?? []
                : [JsonSerializer.Deserialize(json, AppJson.Default.StorePackage)!];
            var list = new List<InstalledProgram>();
            foreach (var pkg in packages)
                if (FromPackage(pkg) is { } p) list.Add(p);
            return list;
        }
        catch (Exception ex) when (ex is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    // Only packages with an app visible in the Start menu (codecs, extensions and background components are not listed).
    static InstalledProgram? FromPackage(StorePackage pkg)
    {
        if (string.IsNullOrEmpty(pkg.Location)) return null;
        XDocument manifest;
        try { manifest = XDocument.Load(Path.Combine(pkg.Location, "AppxManifest.xml")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }

        var visible = manifest.Descendants().Where(e => e.Name.LocalName == "Application")
            .Select(a => a.Elements().FirstOrDefault(v => v.Name.LocalName == "VisualElements"))
            .Where(v => v is not null && !string.Equals(Attr(v, "AppListEntry"), "none", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (visible.Count == 0) return null;

        var properties = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Properties");
        string Prop(string name) => properties?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

        var name = Resolve(Reg.Get(RegistryHive.CurrentUser, StoreRepository + "\\" + pkg.FullName, "DisplayName") as string, pkg)
                   ?? Resolve(Prop("DisplayName"), pkg) ?? pkg.Name;
        var publisher = Resolve(Prop("PublisherDisplayName"), pkg) ?? CommonName().Match(pkg.Publisher).Groups[1].Value;
        var logo = Logo(pkg.Location, Attr(visible[0]!, "Square44x44Logo") ?? Prop("Logo"));
        DateTime? installed = null;
        try { installed = Directory.GetCreationTime(pkg.Location); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new InstalledProgram("MSIX|" + pkg.FullName, name, publisher, pkg.Version, installed, 0, logo, pkg.Location,
            null, null, ProgramSource.Store, MachineWide: false)
        { Dirs = [pkg.Location.TrimEnd('\\') + "\\"], PackageFamily = pkg.Family };
    }

    static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* output, int capacity, IntPtr reserved);

    // "@{Package?ms-resource://...}" and "ms-resource:Key" texts are read from the package's language resources.
    static unsafe string? Resolve(string? value, StorePackage pkg)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string source;
        if (value.StartsWith("@{", StringComparison.Ordinal)) source = value;
        else if (value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            var key = value["ms-resource:".Length..];
            var uri = key.StartsWith("//", StringComparison.Ordinal) ? "ms-resource:" + key
                : key.Contains('/') ? $"ms-resource://{pkg.Name}/{key.TrimStart('/')}"
                : $"ms-resource://{pkg.Name}/Resources/{key}";
            source = $"@{{{pkg.FullName}?{uri}}}";
        }
        else return value.Trim();
        const int Capacity = 512;
        char* buffer = stackalloc char[Capacity];
        if (SHLoadIndirectString(source, buffer, Capacity, IntPtr.Zero) != 0) return null;
        var text = new string(buffer);
        return text.Length > 0 && !text.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) ? text.Trim() : null;
    }

    // The logo file is looked up by scale qualifiers: Logo.targetsize-32_altform-unplated.png, Logo.scale-100.png...
    internal static string? Logo(string root, string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;
        var path = Path.Combine(root, relative);
        if (File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        string[] order = ["targetsize-32_altform-unplated", "targetsize-48_altform-unplated", "targetsize-32", "targetsize-48", "scale-100", "scale-200"];
        try
        {
            var files = Directory.GetFiles(dir, stem + ".*" + ext).Where(f => !f.Contains("contrast-", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var q in order)
                if (files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith("." + q, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
            return files.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // PowerShell script (-EncodedCommand: no quoting problems). Only standard output is returned (UTF-8); when the error stream
    // is redirected, PowerShell writes progress and error records as CLIXML, so scripts write their messages to standard output.
    internal static async Task<(int Code, string Output)> PowerShellAsync(string script, CancellationToken ct)
    {
        var exe = PowerShellOverride?.Exe ?? Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var args = PowerShellOverride?.Prefix
                   ?? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        await error;
        return (p.ExitCode, await output);
    }
}

// Program folders: generic folders (Program Files, Windows, installer caches) are skipped.
internal static class InstallDirs
{
    public static string? ExeDir(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        int comma = v.LastIndexOf(',');
        if (comma > 0 && v[(comma + 1)..].Trim().TrimStart('-').All(char.IsDigit)) v = v[..comma].Trim().Trim('"');
        try { return File.Exists(v) ? Path.GetDirectoryName(v) : null; }
        catch (ArgumentException) { return null; }
    }

    public static void Add(List<string> dirs, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'))).TrimEnd('\\') + "\\"; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (!Directory.Exists(full) || IsGeneric(full) || dirs.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
        dirs.Add(full);
    }

    public static bool IsGeneric(string full)
    {
        full = full.TrimEnd('\\') + "\\";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (full.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) || full.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase)) return true;
        if (full.TrimEnd('\\').Count(c => c == '\\') < 2) return true; // C:\ or C:\Folder
        return Generic.Any(g => g.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    static readonly string[] Generic = new[]
    {
        Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.Desktop, Environment.SpecialFolder.CommonDesktopDirectory,
    }.Select(f => Environment.GetFolderPath(f).TrimEnd('\\') + "\\")
     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") + "\\")
     .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\")
     .Where(g => g.Length > 1)
     .ToArray();
}
