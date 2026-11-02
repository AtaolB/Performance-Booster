using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PerformanceBooster.Core;
using PerformanceBooster.UI;

namespace PerformanceBooster;

public partial class MainWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO info);

    public MainViewModel ViewModel { get; } = new();

    // Raised when the close button hides the window to the tray (for the first-time info note).
    public event Action? HiddenToTray;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        WindowTheme.Apply(this);
        StateChanged += (_, _) => UpdateVisibility();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        Activated += (_, _) => ViewModel.SetActive(true);
        Deactivated += (_, _) => ViewModel.SetActive(false);
    }

    void UpdateVisibility()
    {
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        ViewModel.SetVisible(visible, inTray: !IsVisible);
        if (!visible)
        {
            // The working set is released in the background; Task Manager shows low memory, pages come back when needed.
            using var self = Process.GetCurrentProcess();
            Native.EmptyWorkingSet(self.Handle);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }

    // When a new update is found and the window is in the background, the taskbar button flashes until the window is activated.
    public void FlashIfInactive()
    {
        if (IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref info);
    }
}
