using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public sealed class ProgramRow(InstalledProgram program) : ObservableObject
{
    public InstalledProgram Program { get; } = program;
    public string Name => Program.Name;
    public string Detail => string.Join(" · ", new[] { Program.Publisher, Program.Version }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public bool IsStore => Program.Source == ProgramSource.Store;
    public string SourceText => IsStore ? "Store" : T("Desktop");
    public bool IsSystemLike => Program.IsSystemLike;
    public string? SystemHint => IsSystemLike ? T("Other programs may need this; if you uninstall it, some games or apps may stop working.") : null;
    public string InstallDateText => Program.InstallDate?.ToString("d", L.Culture) ?? "–";
    public bool HasFolder => Program.Dirs.Count > 0;
    public bool IsBroken => Program.IsBroken;
    public string BrokenText => Program.Health == ProgramHealth.MissingUninstaller ? T("uninstaller deleted") : T("folder deleted");
    public string? BrokenHint => Program.Health switch
    {
        ProgramHealth.MissingUninstaller => T("The program's files were deleted by hand; without its uninstaller it cannot be removed in Windows either. \"Remove from list\" also deletes the entry from Windows' list."),
        ProgramHealth.MissingFolder => T("The install folder was deleted. The uninstaller can still be tried; if it does not work, delete the entry with \"Remove from list\"."),
        _ => null,
    };

    long _size = program.SizeBytes;
    DateTime? _lastUsed;
    bool _selected, _busy, _iconLoaded;
    string _status = "";
    Brush _statusBrush = Palette.Muted;
    ImageSource? _icon;

    public long SizeBytes { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeText)); } }
    public string SizeText => FormatSize(_size);

    public DateTime? LastUsed
    {
        get => _lastUsed;
        set { if (Set(ref _lastUsed, value)) { Raise(nameof(LastUsedText)); Raise(nameof(LastUsedBrush)); } }
    }

    public string LastUsedText => _lastUsed is { } t ? Relative(t, DateTime.Now) : T("unknown");
    public Brush LastUsedBrush => _lastUsed is null ? Palette.Muted : DateTime.Now - _lastUsed.Value > TimeSpan.FromDays(180) ? Palette.Warn : Palette.Ok;

    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { Raise(nameof(CanUninstall)); Raise(nameof(CanRemoveEntry)); } } }
    public bool CanUninstall => Program.CanUninstall && !_busy;
    public bool CanRemoveEntry => IsBroken && !_busy;
    public bool ShowUninstall => Program.CanUninstall;
    public string StatusText { get => _status; private set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    // The icon is loaded when it first appears (the list is virtualized: only the rows on screen).
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = ProgramIcons.Get(Program);
            }
            return _icon;
        }
    }

    public void SetBusy(string text)
    {
        Busy = true;
        StatusBrush = Palette.Info;
        StatusText = text;
    }

    public void Finish(UninstallResult r)
    {
        Busy = false;
        StatusBrush = r.Removed ? Palette.Ok : r.Cancelled ? Palette.Muted : Palette.Bad;
        StatusText = r.Message;
    }

    internal static string FormatSize(long bytes) => bytes <= 0 ? "–" : bytes < 1048576 ? "< 1 MB" : Maintenance.Size(bytes);

    // "today", "yesterday", "3 days ago", "2 months ago"...
    internal static string Relative(DateTime time, DateTime now)
    {
        var span = now - time;
        if (span < TimeSpan.FromMinutes(2)) return T("open now");
        if (time.Date == now.Date) return T("today");
        if (time.Date == now.Date.AddDays(-1)) return T("yesterday");
        int days = (int)(now.Date - time.Date).TotalDays;
        if (days < 30) return F("{0} day ago|{0} days ago", days);
        if (days < 365) return F("{0} month ago|{0} months ago", Math.Max(1, days / 30));
        return F("{0} year ago|{0} years ago", days / 365);
    }
}

public sealed partial class LeftoverRow(Leftover item) : ObservableObject
{
    bool _selected;

    public Leftover Item { get; } = item;
    public string Path => Item.Path;
    public string Glyph => Item.Kind switch { LeftoverKind.Folder => "", LeftoverKind.File => "", _ => "" };
    public string KindText => Item.ListEntry ? T("Program list entry (also shown in Windows)")
        : Item.Kind switch { LeftoverKind.Folder => T("Folder"), LeftoverKind.File => T("Shortcut"), _ => T("Registry key") };
    public string SizeText => Item.Kind == LeftoverKind.Registry ? "" : ProgramRow.FormatSize(Item.SizeBytes);
    public string Program => Item.Program;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

// An app on the Live tab; kept between refreshes and updated in place.
public sealed class LiveAppRow(string name) : ObservableObject
{
    string _count = "", _cpu = "", _ram = "", _read = "", _write = "", _gpu = "", _down = "", _up = "";
    bool _canKill, _pinned;
    ImageSource? _icon;

    public string Name { get; } = name;
    public AppUsage Usage { get; private set; } = new(name, 1, 0, 0, 0, 0, 0, false);
    public int Instances => Usage.Count;
    public string Count { get => _count; private set => Set(ref _count, value); }
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string Ram { get => _ram; private set => Set(ref _ram, value); }
    public string Read { get => _read; private set => Set(ref _read, value); }
    public string Write { get => _write; private set => Set(ref _write, value); }
    public ImageSource? Icon { get => _icon; set { if (Set(ref _icon, value)) { Raise(nameof(HasIcon)); Raise(nameof(NoIcon)); } } }
    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;
    public bool IsPinned { get => _pinned; set { if (Set(ref _pinned, value)) Raise(nameof(PinText)); } }
    public string PinText => _pinned ? T("Unpin") : T("Pin to the top");
    public string Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public string Down { get => _down; private set => Set(ref _down, value); }
    public string Up { get => _up; private set => Set(ref _up, value); }
    public bool CanKill { get => _canKill; private set => Set(ref _canKill, value); }

    public void Update(AppUsage u, bool hasNet)
    {
        Usage = u;
        Count = u.Count > 1 ? $"x{u.Count}" : "";
        Cpu = Pct(u.CpuPercent, "0.0");
        Ram = u.RamBytes >= 1073741824 ? (u.RamBytes / 1073741824.0).ToString("0.0", L.Culture) + " GB" : (u.RamBytes / 1048576.0).ToString("0", L.Culture) + " MB";
        Read = Bytes(u.ReadPerSec);
        Write = Bytes(u.WritePerSec);
        Gpu = u.GpuPercent > 0.05 ? Pct(u.GpuPercent, "0.0") : "–";
        Down = hasNet ? Bits(u.Down) : "–";
        Up = hasNet ? Bits(u.Up) : "–";
        CanKill = u.CanKill;
    }

    static string Bytes(double perSec) => perSec >= 1048576 ? (perSec / 1048576.0).ToString("0.0", L.Culture) + " MB/s"
        : perSec >= 1024 ? (perSec / 1024.0).ToString("0", L.Culture) + " KB/s" : perSec > 0 ? "< 1 KB/s" : "–";

    static string Bits(long bytesPerSec) => bytesPerSec * 8 >= 1e6 ? (bytesPerSec * 8 / 1e6).ToString("0.0", L.Culture) + " Mbps"
        : bytesPerSec * 8 >= 1e3 ? (bytesPerSec * 8 / 1e3).ToString("0", L.Culture) + " kbps" : bytesPerSec > 0 ? "< 1 kbps" : "–";
}

// Program icons: DisplayIcon for desktop programs (exe/dll/ico + index), the package logo for Store apps.
static partial class ProgramIcons
{
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // The icon of a running app's exe; may be called from a background thread (the result is frozen).
    public static ImageSource? ForExe(string path)
    {
        try { return FromFile(path, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or COMException) { return null; }
    }

    public static ImageSource? Get(InstalledProgram p)
    {
        try
        {
            if (p.Source == ProgramSource.Store) return p.IconPath is { } logo ? Picture(logo) : null;
            if (p.IconPath is { } icon && FromIconPath(icon) is { } image) return image;
            return Fallback(p) is { } exe ? FromFile(exe, 0) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or COMException)
        {
            return null;
        }
    }

    static ImageSource? FromIconPath(string value)
    {
        var v = Environment.ExpandEnvironmentVariables(value.Trim());
        int index = 0;
        int comma = v.LastIndexOf(',');
        if (comma > 0 && int.TryParse(v[(comma + 1)..].Trim(), out var i))
        {
            index = i;
            v = v[..comma];
        }
        v = v.Trim().Trim('"');
        if (!File.Exists(v)) return null;
        var ext = System.IO.Path.GetExtension(v).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".bmp" ? Picture(v) : FromFile(v, index);
    }

    static ImageSource? FromFile(string file, int index)
    {
        var key = $"{file}|{index}";
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? result = null;
        var large = new IntPtr[1];
        if (ExtractIconEx(file, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
        {
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                result = source;
            }
            finally
            {
                DestroyIcon(large[0]);
            }
        }
        lock (Cache) Cache[key] = result;
        return result;
    }

    static ImageSource? Picture(string file)
    {
        lock (Cache)
            if (Cache.TryGetValue(file, out var cached)) return cached;
        ImageSource? result = null;
        if (File.Exists(file))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(file);
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            result = image;
        }
        lock (Cache) Cache[file] = result;
        return result;
    }

    // A program with no registered icon: the exe in its install folder whose name is closest to the program name.
    static string? Fallback(InstalledProgram p)
    {
        if (p.Dirs.Count == 0) return null;
        var exes = Directory.EnumerateFiles(p.Dirs[0], "*.exe").Take(40)
            .Where(f => !System.IO.Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                        && !System.IO.Path.GetFileName(f).Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                        && !System.IO.Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var words = Programs.CleanName(p.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        return exes.FirstOrDefault(e => words.Any(w => System.IO.Path.GetFileNameWithoutExtension(e).Contains(w, StringComparison.OrdinalIgnoreCase)))
               ?? exes.FirstOrDefault();
    }
}
