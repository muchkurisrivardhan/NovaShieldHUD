using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dotline;

/// <summary>Small HUD-styled dialog for the life settings or one goal.</summary>
sealed class EditWindow : Window
{
    readonly StackPanel form = new();
    readonly TextBlock error = Theme.T("", 13, Theme.Warn);
    Func<string?> commit = () => null;   // returns an error message, or null when saved

    EditWindow(string title)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Title = "Dotline — " + title;

        var root = new Grid();
        root.Children.Add(new ChamferPlate { Fill = Theme.Plate(2), Stroke = Theme.Line, CutTopLeft = 16, CutBottomRight = 16 });
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 18), Width = 320 };
        panel.Children.Add(Theme.T("DOTLINE // " + title, 16, Theme.Accent, bold: true));
        form.Margin = new Thickness(0, 10, 0, 6);
        panel.Children.Add(form);
        error.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Theme.Chip("CANCEL", false, () => DialogResult = false));
        buttons.Children.Add(Theme.Chip("SAVE", true, Save));
        panel.Children.Add(buttons);
        root.Children.Add(panel);
        Content = root;

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch (InvalidOperationException) { } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) DialogResult = false;
            else if (e.Key == Key.Enter) Save();
        };
    }

    void Save()
    {
        var msg = commit();
        if (msg is null) DialogResult = true;
        else error.Text = msg;
    }

    TextBox Field(string label, string value)
    {
        form.Children.Add(Theme.T(label, 13, Theme.Dim));
        var box = new TextBox
        {
            Text = value, FontFamily = Theme.Font, FontSize = 17, Foreground = Theme.Text, CaretBrush = Theme.Accent,
            Background = Theme.Field, BorderBrush = Theme.Line, Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 2, 0, 10),
        };
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        form.Children.Add(box);
        if (form.Children.Count == 2) Loaded += (_, _) => box.Focus();
        return box;
    }

    static bool Date(string s, out DateTime d) =>
        DateTime.TryParseExact(s.Trim(), ["yyyy-MM-dd", "yyyy-M-d", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    public static bool EditLife(State s)
    {
        var w = new EditWindow("LIFE");
        var bday = w.Field("BIRTHDAY  (YYYY-MM-DD)", s.Example ? "" : s.Birthday.ToString("yyyy-MM-dd"));
        var span = w.Field("LIFESPAN IN YEARS", s.Lifespan.ToString());
        w.commit = () =>
        {
            if (!Date(bday.Text, out var d) || d > DateTime.Today || d.Year < 1900)
                return "Enter your birthday as YYYY-MM-DD, e.g. 2001-08-14.";
            if (!int.TryParse(span.Text.Trim(), out var n) || n < 1 || n > 120)
                return "Lifespan must be a whole number from 1 to 120.";
            s.Birthday = d; s.Lifespan = n; s.Example = false;
            return null;
        };
        return w.ShowDialog() == true;
    }

    public static bool EditGoal(GoalData g, bool isNew)
    {
        var w = new EditWindow(isNew ? "NEW GOAL" : "EDIT GOAL");
        var name = w.Field("GOAL NAME", g.Name);
        var start = w.Field("START  (YYYY-MM-DD)", g.Start.ToString("yyyy-MM-dd"));
        var end = w.Field("ACHIEVE BY  (YYYY-MM-DD)", g.End.ToString("yyyy-MM-dd"));
        var pct = w.Field("HOW MUCH IS DONE  (%)", g.Pct.ToString());

        w.form.Children.Add(Theme.T("SHOW DOTS AS", 13, Theme.Dim));
        var unit = g.Unit;
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 4, 0, 4) };
        void Chips()
        {
            chips.Children.Clear();
            foreach (var u in new[] { "day", "week", "month", "year" })
                chips.Children.Add(Theme.Chip(Units.Plural(u).ToUpperInvariant(), u == unit, () => { unit = u; Chips(); }));
        }
        Chips();
        w.form.Children.Add(chips);

        w.commit = () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) return "Give the goal a name.";
            if (!Date(start.Text, out var a)) return "Start date must look like 2026-09-21.";
            if (!Date(end.Text, out var e)) return "Achieve-by date must look like 2027-03-01.";
            if (e <= a) return "The achieve-by date has to be after the start.";
            if (!int.TryParse(pct.Text.Trim().TrimEnd('%'), out var p) || p < 0 || p > 100) return "Done must be 0 to 100.";
            g.Name = name.Text.Trim(); g.Start = a; g.End = e; g.Pct = p; g.Unit = unit;
            return null;
        };
        return w.ShowDialog() == true;
    }

    /// <summary>Custom focus length as hours, minutes and seconds (up to 24:00:00). Null if cancelled.</summary>
    public static int? EditFocusLength(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var w = new EditWindow("FOCUS LENGTH");
        var h = w.Field("HOURS  (0–24)", ((int)t.TotalHours).ToString());
        var m = w.Field("MINUTES  (0–59)", t.Minutes.ToString());
        var s = w.Field("SECONDS  (0–59)", t.Seconds.ToString());
        int result = 0;
        w.commit = () =>
        {
            static bool Read(TextBox b, int max, out int v) => int.TryParse(b.Text.Trim() is "" ? "0" : b.Text.Trim(), out v) && v >= 0 && v <= max;
            if (!Read(h, 24, out var hh)) return "Hours must be 0 to 24.";
            if (!Read(m, 59, out var mm)) return "Minutes must be 0 to 59.";
            if (!Read(s, 59, out var ss)) return "Seconds must be 0 to 59.";
            result = hh * 3600 + mm * 60 + ss;
            if (result == 0) return "Set at least one second.";
            if (result > FocusTimer.MaxSeconds) return "The longest session is 24 hours (24 : 00 : 00).";
            return null;
        };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Pick up to five time zones to show as dots on the wallpaper map.</summary>
    public static bool EditMap(MapState map)
    {
        const int MaxZones = 5;
        var w = new EditWindow("WORLD MAP");
        var on = map.On;
        var zones = new List<string>(map.Zones);

        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 0, 0, 10) };
        void Toggle()
        {
            toggle.Children.Clear();
            toggle.Children.Add(Theme.Chip("LIVE MAP ON", on, () => { on = true; Toggle(); }));
            toggle.Children.Add(Theme.Chip("OFF", !on, () => { on = false; Toggle(); }));
        }
        Toggle();
        w.form.Children.Add(toggle);

        var count = Theme.T("", 13, Theme.Dim);
        var filter = w.Field("FIND A CITY", "");
        w.form.Children.Add(count);
        var list = new StackPanel();
        w.form.Children.Add(new ScrollViewer
        {
            Content = list, MaxHeight = 300, Margin = new Thickness(0, 6, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        void Fill()
        {
            count.Text = $"TIME ZONES {zones.Count}/{MaxZones}   ·   HOVER A DOT ON THE MAP FOR ITS TIME";
            list.Children.Clear();
            var q = filter.Text.Trim();
            var now = DateTime.UtcNow;
            foreach (var c in Geo.Cities.Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var off = c.Zone.GetUtcOffset(now);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var chips = new StackPanel { Orientation = Orientation.Horizontal };
                chips.Children.Add(Theme.Chip(zones.Contains(c.Name) ? "SHOWN" : "SHOW", zones.Contains(c.Name), () =>
                {
                    if (zones.Remove(c.Name)) w.error.Text = "";
                    else if (zones.Count >= MaxZones) w.error.Text = $"Up to {MaxZones} time zones. Tap one marked SHOWN to remove it first.";
                    else zones.Add(c.Name);
                    Fill();
                }));
                DockPanel.SetDock(chips, Dock.Right);
                row.Children.Add(chips);
                var label = new TextBlock { FontFamily = Theme.Font, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                label.Inlines.Add(new System.Windows.Documents.Run(c.Name) { Foreground = Theme.Text });
                label.Inlines.Add(new System.Windows.Documents.Run($"  UTC{(off < TimeSpan.Zero ? "−" : "+")}{off:hh\\:mm}") { Foreground = Theme.Faint, FontSize = 12 });
                row.Children.Add(label);
                list.Children.Add(row);
            }
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();

        w.commit = () =>
        {
            map.On = on;
            map.Zones = zones;
            return null;
        };
        return w.ShowDialog() == true;
    }
}
