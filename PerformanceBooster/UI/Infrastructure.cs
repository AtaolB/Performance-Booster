using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<TValue>(ref TValue field, TValue value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // After the current binding update: a drop-down that just sent an empty selection takes the kept value again.
    protected void RaiseLater(string name) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Raise(name), System.Windows.Threading.DispatcherPriority.DataBind);
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

// Translated text in XAML: {ui:T 'Power plan'}. The language changes with a restart, so no binding is needed.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}

// ui:Menus.OpenOnClick="True" on a button: a click opens the context menu of the row the button is in (the "⋯" button).
public static class Menus
{
    public static readonly DependencyProperty OpenOnClickProperty =
        DependencyProperty.RegisterAttached("OpenOnClick", typeof(bool), typeof(Menus), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject d) => (bool)d.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject d, bool value) => d.SetValue(OpenOnClickProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Button button) return;
        button.Click -= Open;
        if ((bool)e.NewValue) button.Click += Open;
    }

    // The menu is placed at the mouse; its target stays the row, so the row's bindings (Tag) still work.
    static void Open(object sender, RoutedEventArgs e)
    {
        DependencyObject? at = (DependencyObject)sender;
        while (at is not null && (at as FrameworkElement)?.ContextMenu is null) at = VisualTreeHelper.GetParent(at);
        if (at is not FrameworkElement { ContextMenu: { } menu } owner) return;
        menu.DataContext = owner.DataContext;
        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }
}

public static class Palette
{
    public static readonly Brush Ok = Make("#3FB950");
    public static readonly Brush Warn = Make("#F0B429");
    public static readonly Brush Bad = Make("#F85149");
    public static readonly Brush Info = Make("#5B8CFF");
    public static readonly Brush Muted = Make("#6B7280");

    static SolidColorBrush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

// Matches the window frame to the app's dark theme: dark title bar (Windows 10 20H1+),
// and on Windows 11 the caption and border colors also match the background.
public static class WindowTheme
{
    public static void Apply(Window window)
    {
        void Paint()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = 1, caption = 0x00150F0D /* #0D0F15 BGR */, text = 0x00F0EAE7, border = 0x00382C25;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TEXT_COLOR, ref text, sizeof(int));
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Paint();
        else window.SourceInitialized += (_, _) => Paint();
    }
}

// Draws the last 60 samples as a line plus an area fading downward. NaN values (lost pings) leave a gap.
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Sparkline)d)._pens = null));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    // IReadOnlyList instead of double[]: the XAML compiler rejects binding to an array-typed property inside a template (MC4102).
    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public int Capacity { get; set; } = 60;

    (Pen Line, Brush Fill)? _pens;

    // The pen and fill are built once per color change, not on every frame.
    (Pen Line, Brush Fill) Pens()
    {
        if (_pens is { } cached) return cached;
        var color = Stroke is SolidColorBrush s ? s.Color : Colors.SteelBlue;
        var line = new Pen(new SolidColorBrush(color), 1.6) { LineJoin = PenLineJoin.Round };
        line.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();
        _pens = (line, fill);
        return _pens.Value;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Values is not { Count: >= 2 } source || ActualWidth <= 0 || ActualHeight <= 0) return;
        var values = source as double[] ?? source.ToArray();

        double max = Maximum;
        if (double.IsNaN(max))
        {
            max = 1;
            foreach (var v in values) if (!double.IsNaN(v)) max = Math.Max(max, v * 1.15);
        }
        double w = ActualWidth, h = ActualHeight, step = w / (Capacity - 1);
        double x0 = w - (values.Length - 1) * step;
        var (line, fill) = Pens();

        // Each continuous segment is drawn separately.
        int i = 0;
        while (i < values.Length)
        {
            while (i < values.Length && double.IsNaN(values[i])) i++;
            int start = i;
            while (i < values.Length && !double.IsNaN(values[i])) i++;
            if (i - start < 2) continue;

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0 + start * step, h), true, true);
                for (int k = start; k < i; k++) ctx.LineTo(At(k), true, true);
                ctx.LineTo(new Point(x0 + (i - 1) * step, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);

            var stroke = new StreamGeometry();
            using (var ctx = stroke.Open())
            {
                ctx.BeginFigure(At(start), false, false);
                for (int k = start + 1; k < i; k++) ctx.LineTo(At(k), true, true);
            }
            stroke.Freeze();
            dc.DrawGeometry(null, line, stroke);
        }

        Point At(int k) => new(x0 + k * step, h - Math.Clamp(values[k] / max, 0, 1) * (h - 2) - 1);
    }
}
