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

// Live card details: a process (same names combined) and its usage of that resource.
public sealed record UsageRow(string Name, int Instances, string Primary, string Secondary, string Third, double Share, bool CanKill)
{
    public string Count => Instances > 1 ? $"x{Instances}" : "";
    public double SharePercent => Math.Clamp(Share * 100, 0, 100);
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
        Cache[key] = result;
        return result;
    }

    static ImageSource? Picture(string file)
    {
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
        Cache[file] = result;
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
