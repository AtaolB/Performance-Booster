using System.Drawing;
using WinForms = System.Windows.Forms;

namespace PerformanceBooster.UI;

// System tray icon and its dark menu. When the window is closed, the app keeps running here.
public sealed class Tray : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripMenuItem _gameMode;
    Action? _balloonClick;

    public event Action? OpenRequested;
    public event Action? CheckUpdatesRequested;
    public event Action<bool>? GameModeToggled;
    public event Action? ExitRequested;

    public Tray(bool gameMode)
    {
        if (Core.Elevated.IsAdmin) Core.Native.ChangeWindowMessageFilter(Core.Native.RegisterWindowMessage("TaskbarCreated"), 1); // MSGFLT_ADD
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f),
        };
        var open = new WinForms.ToolStripMenuItem(T("Open")) { Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        var check = new WinForms.ToolStripMenuItem(T("Check for updates"));
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke();
        _gameMode = new WinForms.ToolStripMenuItem(T("Game mode")) { CheckOnClick = true, Checked = gameMode };
        _gameMode.CheckedChanged += (_, _) => GameModeToggled?.Invoke(_gameMode.Checked);
        var exit = new WinForms.ToolStripMenuItem(T("Exit"));
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange([open, check, _gameMode, new WinForms.ToolStripSeparator(), exit]);
        foreach (WinForms.ToolStripItem item in menu.Items) item.ForeColor = Color.FromArgb(0xE7, 0xEA, 0xF0);

        _icon = new WinForms.NotifyIcon
        {
            // The exe's own icon in the size of the notification area (sharper than a scaled-down large icon).
            Icon = Icon.ExtractIcon(Environment.ProcessPath!, 0, WinForms.SystemInformation.SmallIconSize.Width) ?? SystemIcons.Application,
            Text = Core.AppPaths.Name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => (_balloonClick ?? OpenRequested)?.Invoke();
    }

    public void SetGameMode(bool on)
    {
        if (_gameMode.Checked != on) _gameMode.Checked = on;
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    // Shown as a notification on Windows 10/11.
    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(0x1B, 0x20, 0x2B), Hover = Color.FromArgb(0x2A, 0x33, 0x44), Line = Color.FromArgb(0x30, 0x38, 0x48);
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
