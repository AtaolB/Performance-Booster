using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerformanceBooster.Core;

namespace PerformanceBooster.UI;

// Small windows that match the app's dark theme (instead of MessageBox).
public static class Dialogs
{
    public enum Kind { Info, Warning, Error, Question }

    // Returns the index of the chosen button, or -1 if the window is closed. The first button is highlighted.
    public static int Choice(string title, string message, IEnumerable<string> items, params string[] buttons) =>
        Show(title, message, items, Kind.Question, buttons);

    public static void Info(string title, string message, IEnumerable<string>? items = null, bool warning = false) =>
        Show(title, message, items ?? [], warning ? Kind.Warning : Kind.Info, T("OK"));

    public static bool Confirm(string title, string message, string yes, bool danger = false) =>
        Show(title, message, [], danger ? Kind.Warning : Kind.Question, yes, T("Cancel")) == 0;

    public static bool Confirm(string title, string message, IEnumerable<string> items, string yes, bool danger = false) =>
        Show(title, message, items, danger ? Kind.Warning : Kind.Question, yes, T("Cancel")) == 0;

    // Leftover list: nothing is preselected. "Delete selected" returns the selected items; closing returns an empty list.
    public static List<Leftover> Leftovers(string title, string message, IReadOnlyList<LeftoverRow> rows)
    {
        var window = Create(title);
        window.SizeToContent = SizeToContent.Height;
        window.Width = 860;
        var chosen = new List<Leftover>();
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        head.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("Sub"), Margin = new Thickness(0, 0, 0, 12) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var delete = new Button { MinWidth = 140, Margin = new Thickness(8, 0, 0, 0), Style = Resource<Style>("DangerButton"), IsEnabled = false };
        void Refresh()
        {
            int n = rows.Count(r => r.IsSelected);
            delete.Content = F("Delete selected ({0})", n);
            delete.IsEnabled = n > 0;
        }

        var list = new StackPanel();
        foreach (var row in rows)
        {
            var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(LeftoverRow.IsSelected)) { Source = row, Mode = System.Windows.Data.BindingMode.TwoWay });
            // The counter updates whether the change comes from the mouse, keyboard or a screen reader (UI Automation).
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LeftoverRow.IsSelected)) Refresh(); };
            System.Windows.Automation.AutomationProperties.SetName(check, row.Path);
            var glyph = new TextBlock { Text = row.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = Resource<Brush>("Sub"), Margin = new Thickness(0, 2, 10, 0), ToolTip = row.KindText };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = row.Path, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = row.KindText + (rows.Select(r => r.Program).Distinct().Count() > 1 ? " · " + row.Program : "") + (row.Item.NeedsAdmin ? " · " + T("needs administrator permission") : ""), FontSize = 11.5, Foreground = Resource<Brush>("Sub") });
            var size = new TextBlock { Text = row.SizeText, Foreground = Resource<Brush>("Sub"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(glyph, 1);
            Grid.SetColumn(text, 2);
            Grid.SetColumn(size, 3);
            line.Children.Add(check);
            line.Children.Add(glyph);
            line.Children.Add(text);
            line.Children.Add(size);
            list.Children.Add(line);
        }

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var all = new Button { Content = T("Select all"), Style = Resource<Style>("SmallButton") };
        all.Click += (_, _) =>
        {
            bool select = rows.Any(r => !r.IsSelected);
            foreach (var r in rows) r.IsSelected = select;
        };
        DockPanel.SetDock(all, Dock.Left);
        buttons.Children.Add(all);
        var close = new Button { Content = T("Close"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => window.Close();
        delete.Click += (_, _) =>
        {
            chosen.AddRange(rows.Where(r => r.IsSelected).Select(r => r.Item));
            window.Close();
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(delete);
        right.Children.Add(close);
        buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new Border
        {
            Style = Resource<Style>("Card"), Padding = new Thickness(12, 8, 12, 8),
            Child = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        Refresh();
        window.Content = root;
        window.ShowDialog();
        return chosen;
    }

    static int Show(string title, string message, IEnumerable<string> items, Kind kind, params string[] buttons)
    {
        var window = Create(title);
        int result = -1;
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 18), MaxWidth = 640, LastChildFill = true };

        var glyph = new TextBlock
        {
            Text = kind switch { Kind.Warning => "", Kind.Error => "", Kind.Question => "", _ => "" },
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 26,
            Foreground = kind switch { Kind.Warning => Palette.Warn, Kind.Error => Palette.Bad, _ => Palette.Info },
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(glyph, Dock.Left);
        root.Children.Add(glyph);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("Sub"), Margin = new Thickness(0, 0, 0, 12) });

        var list = items.ToList();
        if (list.Count > 0)
        {
            var lines = new StackPanel();
            foreach (var item in list) lines.Children.Add(new TextBlock { Text = "•  " + item, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
            panel.Children.Add(new Border
            {
                Style = Resource<Style>("Card"), Padding = new Thickness(12, 8, 12, 8),
                Child = new ScrollViewer { Content = lines, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            if (i == 0) button.Style = Resource<Style>(kind == Kind.Warning && buttons.Length > 1 ? "DangerButton" : "AccentButton");
            button.Click += (_, _) => { result = index; window.Close(); };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        root.Children.Add(panel);
        window.Content = root;
        window.ShowDialog();
        return result;
    }

    // Version notes: each version with its date, then its groups (New, Changed, Fixed) as lists. "link" adds a button to the release page.
    public static void Changes(string title, string? intro, IReadOnlyList<ChangelogEntry> entries, string? link = null)
    {
        var window = Create(title);
        var root = new DockPanel { Margin = new Thickness(24, 22, 24, 18), Width = 620, LastChildFill = true };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(intro))
            header.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Foreground = Resource<Brush>("Sub"), Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (link is not null)
        {
            var page = new Button { Content = T("Release page"), MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
            page.Click += (_, _) => Shell.Open(link);
            buttons.Children.Add(page);
        }
        var close = new Button { Content = T("Close"), MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true, Style = Resource<Style>("AccentButton") };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var list = new StackPanel();
        foreach (var entry in entries)
        {
            var head = new DockPanel { Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 18, 0, 2) };
            if (entry.Date is { } date)
            {
                var when = new TextBlock { Text = date.ToString("d MMMM yyyy", L.Culture), Foreground = Resource<Brush>("Sub"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(when, Dock.Right);
                head.Children.Add(when);
            }
            if (entry.Version > new Version(0, 0))
                head.Children.Add(new TextBlock { Text = SelfUpdate.Format(entry.Version), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Resource<Brush>("Accent") });
            list.Children.Add(head);
            foreach (var section in entry.Sections)
            {
                if (section.Title.Length > 0)
                    list.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) });
                foreach (var item in section.Items)
                {
                    var line = new DockPanel { Margin = new Thickness(2, 2, 0, 2) };
                    var bullet = new TextBlock { Text = "•", Foreground = Resource<Brush>("Accent"), Margin = new Thickness(0, 0, 8, 0) };
                    DockPanel.SetDock(bullet, Dock.Left);
                    line.Children.Add(bullet);
                    line.Children.Add(new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap });
                    list.Children.Add(line);
                }
            }
        }
        if (entries.Count == 0) list.Children.Add(new TextBlock { Text = T("No notes for this version."), Foreground = Resource<Brush>("Sub") });

        root.Children.Add(new Border
        {
            Style = Resource<Style>("Card"), Padding = new Thickness(16, 12, 16, 12),
            Child = new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        window.Content = root;
        window.ShowDialog();
    }

    public static void Text(string title, string text)
    {
        var window = Create(title);
        window.SizeToContent = SizeToContent.Manual;
        window.Width = 800;
        window.Height = 580;
        window.ResizeMode = ResizeMode.CanResize;
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10),
        };
        grid.Children.Add(box);
        var close = new Button { Content = T("Close"), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        close.Click += (_, _) => window.Close();
        Grid.SetRow(close, 1);
        grid.Children.Add(close);
        window.Content = grid;
        window.ShowDialog();
    }

    static Window Create(string title)
    {
        var owner = Application.Current.MainWindow;
        bool usable = owner is { IsLoaded: true, IsVisible: true };
        var window = new Window
        {
            Title = title,
            Owner = usable ? owner : null,
            WindowStartupLocation = usable ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = !usable,
            Background = Resource<Brush>("Bg"),
            Foreground = Resource<Brush>("Fg"),
            FontFamily = Resource<FontFamily>("UiFont"),
            FontSize = 13,
            Icon = owner?.Icon,
        };
        WindowTheme.Apply(window);
        return window;
    }

    static TResource Resource<TResource>(string key) => (TResource)Application.Current.Resources[key];
}
