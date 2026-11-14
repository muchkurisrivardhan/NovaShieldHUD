using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Dotline;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--selftest"]) { Units.SelfTest(); Geo.SelfTest(); return 0; }
        Theme.Load();

        using var mutex = new Mutex(true, "Dotline.SingleInstance", out var first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) => Hub.Start();
        return app.Run();
    }
}

public sealed class GoalData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public DateTime Start { get; set; } = DateTime.Today;
    public DateTime End { get; set; } = DateTime.Today.AddMonths(6);
    public int Pct { get; set; }
    public string Unit { get; set; } = "month";
    // Legacy (v1 had one widget per goal); read once by Migrate, never written again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? X { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Y { get; set; }
}

/// <summary>One widget on the desktop: what it shows ("life" or a goal id), where, and how big.</summary>
public sealed class WidgetData
{
    public string View { get; set; } = "life";
    public double? X { get; set; }
    public double? Y { get; set; }
    public double Scale { get; set; } = 1;
}

public sealed class State
{
    public DateTime Birthday { get; set; } = new(2000, 1, 1);
    public int Lifespan { get; set; } = 70;
    public string LifeUnit { get; set; } = "week";
    public bool Example { get; set; } = true;
    public int Glass { get; set; } = 1;      // 0 clear, 1 glass, 2 solid
    public bool Locked { get; set; }
    public List<GoalData> Goals { get; set; } = [];
    public List<WidgetData> Widgets { get; set; } = [];
    public FocusState Focus { get; set; } = new();
    public MapState Map { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? LifeX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? LifeY { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? Scale { get; set; }

    /// <summary>v1 stored positions on the life settings and on each goal; v2 keeps them per widget.</summary>
    public void Migrate()
    {
        if (Widgets.Count == 0)
        {
            Widgets.Add(new WidgetData { X = LifeX, Y = LifeY, Scale = Scale ?? 1 });
            foreach (var g in Goals) Widgets.Add(new WidgetData { View = g.Id, X = g.X, Y = g.Y, Scale = Scale ?? 1 });
        }
        LifeX = LifeY = Scale = null;
        foreach (var g in Goals) g.X = g.Y = null;
    }
}

/// <summary>Owns the state file and the set of open widgets.</summary>
static class Hub
{
    public static State S = new();
    static readonly List<Widget> widgets = [];
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dotline");
    static readonly string FilePath = Path.Combine(Dir, "state.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Count => widgets.Count;

    /// <summary>The wallpaper overlay; every widget is owned by it, so widgets always sit above the map.</summary>
    public static WorldMap Map { get; private set; } = null!;

    public static void Start()
    {
        var firstRun = !File.Exists(FilePath);
        S = Load();
        S.Migrate();
        if (S.Focus.Minutes > 0) { S.Focus.Seconds = S.Focus.Minutes * 60; S.Focus.Minutes = 0; }   // v1 focus length
        if (S.Focus.CustomSeconds == 0 && !FocusTimer.Presets.Contains(S.Focus.Seconds))
            S.Focus.CustomSeconds = S.Focus.Seconds;   // a custom length set before it had its own slot

        Map = new WorldMap();
        Map.Show();
        foreach (var w in S.Widgets) Open(w);
        FocusTimer.Publish();

        // One heartbeat: the focus countdown every second, the map's zone dots every
        // 30 s, and the dot grids when the date rolls over.
        var day = DateTime.Today;
        var ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            FocusTimer.Tick();
            foreach (var w in widgets) w.Tick();
            if (++ticks % 30 == 0) Map.Redraw();
            if (DateTime.Today != day) { day = DateTime.Today; RefreshAll(); }
        };
        timer.Start();

        Save();
        if (firstRun) Edit(widgets[0]);
    }

    static State Load()
    {
        if (!File.Exists(FilePath)) return new State();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), Json) ?? new State(); }
        catch (Exception e)
        {
            // Keep the unreadable file rather than overwrite the user's data with defaults.
            var bad = Path.Combine(Dir, $"state.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try { File.Move(FilePath, bad); } catch { }
            MessageBox.Show($"Dotline couldn't read its saved data ({e.Message}).\nIt was kept as {bad} and Dotline started fresh.", "Dotline");
            return new State();
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(S, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) { MessageBox.Show($"Dotline couldn't save: {e.Message}", "Dotline"); }
    }

    static void Open(WidgetData d)
    {
        var w = new Widget(d, widgets.Count);
        widgets.Add(w);
        w.Show();
    }

    public static void OpenNew(string view)
    {
        var d = new WidgetData { View = view };
        S.Widgets.Add(d);
        Save();
        Open(d);
    }

    public static void CloseWidget(Widget w)
    {
        if (widgets.Count <= 1) return;
        widgets.Remove(w);
        S.Widgets.Remove(w.W);
        w.Close();
        Save();
    }

    public static void RefreshAll() { foreach (var w in widgets) w.Refresh(); }
    public static void SaveAndRefresh() { Save(); RefreshAll(); }

    /// <summary>Rebuild the widgets showing the focus timer (its buttons change with its state).</summary>
    public static void RefreshFocus() { foreach (var w in widgets.Where(w => w.W.View == "focus")) w.Refresh(); }

    public static void FocusFinished() { foreach (var w in widgets.Where(w => w.W.View == "focus")) w.Flash(); }

    /// <summary>
    /// The FOCUS tab opens the timer in a widget of its own beside the one clicked,
    /// or flashes the focus widget that is already open.
    /// </summary>
    public static void OpenFocus(Widget from)
    {
        if (from.W.View == "focus") return;
        if (widgets.FirstOrDefault(w => w.W.View == "focus") is { } open) { open.Flash(); return; }

        const double width = 380;   // roughly the focus widget at medium size
        var wa = Native.WorkArea(from);
        var x = from.Left - width - 12 >= wa.Left ? from.Left - width - 12 : Math.Min(from.Left + from.ActualWidth + 12, wa.Right - width);
        var d = new WidgetData { View = "focus", X = x, Y = from.Top, Scale = from.W.Scale };
        S.Widgets.Add(d);
        Save();
        Open(d);
    }

    public static void EditMap()
    {
        if (!EditWindow.EditMap(S.Map)) return;
        Save();
        Map.Redraw();
    }

    public static void Edit(Widget w)
    {
        if (w.W.View == "focus") return;   // the focus view is operated from its own buttons
        var goal = w.Goal;
        var ok = goal is null ? EditWindow.EditLife(S) : EditWindow.EditGoal(goal, isNew: false);
        if (ok) SaveAndRefresh();   // a renamed goal changes every widget's tabs
    }

    /// <summary>Create a goal and switch the widget it was asked from over to it.</summary>
    public static void NewGoal(Widget from)
    {
        var g = new GoalData();
        if (!EditWindow.EditGoal(g, isNew: true)) return;
        S.Goals.Add(g);
        SaveAndRefresh();
        from.Show(g.Id);
    }

    public static void Delete(GoalData g)
    {
        if (MessageBox.Show($"Delete the goal \"{g.Name}\"?", "Dotline", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        S.Goals.Remove(g);
        foreach (var w in widgets.Where(w => w.W.View == g.Id)) w.W.View = "life";
        SaveAndRefresh();
    }


    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Dotline") is not null; }
    }

    public static void ToggleStartup()
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartsWithWindows) k.DeleteValue("Dotline", false);
        else k.SetValue("Dotline", $"\"{Environment.ProcessPath}\"");
    }
}

static class Units
{
    public static string Plural(string u) => u + "s";

    /// <summary>Whole units from a to b; months and years follow the calendar, like birthdays do.</summary>
    public static int Between(DateTime a, DateTime b, string u)
    {
        if (u == "day") return (int)Math.Floor((b - a).TotalDays);
        if (u == "week") return (int)Math.Floor((b - a).TotalDays / 7);
        var m = (b.Year - a.Year) * 12 + b.Month - a.Month;
        if (b.Day < a.Day) m--;
        return u == "month" ? m : (int)Math.Floor(m / 12.0);
    }

    public static DateTime Add(DateTime d, int n, string u) => u switch
    {
        "day" => d.AddDays(n),
        "week" => d.AddDays(7 * n),
        "month" => d.AddMonths(n),
        _ => d.AddYears(n),
    };

    /// <summary>Dots needed to cover a..b: a partial last unit still gets its dot.</summary>
    public static int Span(DateTime a, DateTime b, string u)
    {
        var n = Math.Max(0, Between(a, b, u));
        return Add(a, n, u) < b ? n + 1 : Math.Max(1, n);
    }

    public static void SelfTest()
    {
        static void Eq(int got, int want, string what) { if (got != want) throw new Exception($"{what}: got {got}, want {want}"); }
        var jan31 = new DateTime(2000, 1, 31);
        Eq(Between(jan31, new(2000, 2, 29), "month"), 0, "short month");
        Eq(Between(jan31, new(2000, 3, 31), "month"), 2, "two months");
        Eq(Between(jan31, new(2001, 1, 30), "year"), 0, "day before birthday");
        Eq(Between(jan31, new(2001, 1, 31), "year"), 1, "birthday");
        Eq(Between(new(2026, 1, 1), new(2026, 1, 15), "week"), 2, "weeks");
        Eq(Span(new(2026, 1, 1), new(2026, 12, 31), "month"), 12, "year in months");
        Eq(Span(new(2000, 1, 1), new(2070, 1, 1), "year"), 70, "lifespan");
        Eq(Span(new(2000, 1, 1), new(2070, 1, 1), "month"), 840, "lifespan months");
    }
}

/// <summary>
/// Dotline wears Beacon's look: Tactical (gunmetal and amber), HUD (cyan holographic) or Classic
/// (clean and minimal), with Beacon's accent colour. Read once at start from Beacon's settings;
/// Beacon restarts Dotline when you change the look there.
/// </summary>
static class Theme
{
    public static string Look = "tactical";
    public static Color AccentColor;
    public static FontFamily Font = null!;
    public static SolidColorBrush Accent = null!, Line = null!, Steel = null!, Empty = null!, Hollow = null!, Now = null!,
        Text = null!, Dim = null!, Faint = null!, Ink = null!, Field = null!, ChipFill = null!, Glow = null!;
    public static readonly SolidColorBrush Warn = B(0xFFFF8A7A), Hit = B(0x01000000);
    static (uint Top, uint Bottom) plate;   // panel gradient, RGB only

    // Beacon's "no accent chosen" value: each look then uses its own colour.
    const string BeaconDefaultAccent = "#5B8DEF";

    public static void Load()
    {
        string style = "tactical", accent = BeaconDefaultAccent;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon", "settings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("OverlayStyle", out var s) && s.ValueKind == JsonValueKind.String) style = s.GetString()!.ToLowerInvariant();
                if (doc.RootElement.TryGetProperty("AccentColor", out var a) && a.ValueKind == JsonValueKind.String) accent = a.GetString()!.Trim();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }   // no Beacon: Tactical

        Look = style is "hud" or "classic" or "cyberpunk" or "crt" or "mono" ? style : "tactical";
        Color? custom = null;
        if (!string.Equals(accent, BeaconDefaultAccent, StringComparison.OrdinalIgnoreCase) || Look == "classic")
            try { custom = (Color)ColorConverter.ConvertFromString(accent); } catch (FormatException) { }

        uint A(uint alpha) => (alpha << 24) | ((uint)AccentColor.R << 16) | ((uint)AccentColor.G << 8) | AccentColor.B;
        switch (Look)
        {
            case "hud":
                AccentColor = custom ?? Color.FromRgb(0x35, 0xE6, 0xFF);
                Font = new("Bahnschrift, Segoe UI");
                Accent = B(A(0xFF)); Line = B(A(0x45)); Steel = B(A(0x80)); Empty = B(A(0x22)); Hollow = B(A(0x55));
                Now = B(0xFFE8FBFF); Text = B(0xFFE8FBFF); Dim = B(0xC0B8E6EE); Faint = B(A(0x9C));
                Ink = B(0xFF040B15); Field = B(0xFF06101C); ChipFill = B(0x800A1622);
                plate = (0x0C1A28, 0x040B15);
                break;
            case "classic":
                AccentColor = custom ?? Color.FromRgb(0x5B, 0x8D, 0xEF);
                Font = new("Segoe UI Variable Text, Segoe UI");
                Accent = B(A(0xFF)); Line = B(0x2AFFFFFF); Steel = B(0xFF8C8C92); Empty = B(0xFF3A3A3F); Hollow = B(0xFF55555B);
                Now = B(0xFFF2F2F2); Text = B(0xF2FFFFFF); Dim = B(0x99FFFFFF); Faint = B(0x70FFFFFF);
                Ink = B(0xFFFFFFFF); Field = B(0xFF1C1C1F); ChipFill = B(0x80303034);
                plate = (0x26262A, 0x1C1C1F);
                break;
            case "cyberpunk":   // hot pink on deep violet, cyan secondary
                AccentColor = custom ?? Color.FromRgb(0xFF, 0x2E, 0x97);
                Font = new("Bahnschrift, Segoe UI");
                Accent = B(A(0xFF)); Line = B(A(0x50)); Steel = B(0xFF00B8C4); Empty = B(0xFF2A1440); Hollow = B(0xFF4A2A66);
                Now = B(0xFFF7ECFF); Text = B(0xFFF7ECFF); Dim = B(0xC000F0FF); Faint = B(A(0xA8));
                Ink = B(0xFF12041F); Field = B(0xFF14062A); ChipFill = B(0x8022093A);
                plate = (0x1E0838, 0x0E0220);
                break;
            case "crt":   // green-screen terminal
                AccentColor = custom ?? Color.FromRgb(0x33, 0xFF, 0x66);
                Font = new("Cascadia Mono, Consolas");
                Accent = B(A(0xFF)); Line = B(A(0x40)); Steel = B(A(0x90)); Empty = B(A(0x1C)); Hollow = B(A(0x48));
                Now = B(0xFFC8FFD6); Text = B(0xFFC8FFD6); Dim = B(A(0xC8)); Faint = B(A(0x88));
                Ink = B(0xFF021006); Field = B(0xFF030C05); ChipFill = B(0x80061A0C);
                plate = (0x061409, 0x020A04);
                break;
            case "mono":   // black, white and grey
                AccentColor = custom ?? Color.FromRgb(0xE8, 0xE8, 0xE8);
                Font = new("Segoe UI Variable Text, Segoe UI");
                Accent = B(A(0xFF)); Line = B(0x38FFFFFF); Steel = B(0xFF7A7A7A); Empty = B(0xFF2E2E2E); Hollow = B(0xFF4A4A4A);
                Now = B(0xFFFFFFFF); Text = B(0xF2FFFFFF); Dim = B(0x99FFFFFF); Faint = B(0x66FFFFFF);
                Ink = B(0xFF101010); Field = B(0xFF141414); ChipFill = B(0x80262626);
                plate = (0x1A1A1A, 0x101010);
                break;
            default:   // Tactical: machined gunmetal, one amber accent, no glow
                AccentColor = custom ?? Color.FromRgb(0xF0, 0xA0, 0x30);
                Font = new("Agency FB, Bahnschrift, Segoe UI");
                Accent = B(A(0xFF)); Line = B(0x40FFFFFF); Steel = B(0xFF8E9298); Empty = B(0xFF3E4146); Hollow = B(0xFF55585E);
                Now = B(0xFFEDEDED); Text = B(0xFFE6E6E6); Dim = B(0xFFA4A7AC); Faint = B(0xFF808388);
                Ink = B(0xFF17181A); Field = B(0xFF141518); ChipFill = B(0x802A2C2F);
                plate = (0x2C2E31, 0x18191C);
                break;
        }
        Glow = B(A(0x55));
    }

    public static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    /// <summary>The accent at a given opacity, for glows and sweeps.</summary>
    public static Color AccentAlpha(byte alpha) => Color.FromArgb(alpha, AccentColor.R, AccentColor.G, AccentColor.B);

    // "Clear" keeps 1/255 alpha: fully transparent pixels of a layered window let clicks fall through.
    public static Brush Plate(int glass) => glass switch { 0 => B(0x01000000), 1 => Panel(0x8C), _ => Panel(0xF6) };

    static Brush Panel(byte alpha)
    {
        static Color C(byte a, uint rgb) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var g = new LinearGradientBrush(C(alpha, plate.Top), C(alpha, plate.Bottom), 90);
        g.Freeze();
        return g;
    }

    /// <summary>
    /// An endless ambient animation at a low frame rate. Each frame repaints a see-through window
    /// in software, so at WPF's default 60 fps a slow spin or pulse costs a large share of a core.
    /// </summary>
    public static System.Windows.Media.Animation.DoubleAnimation Loop(double from, double to, double seconds, int fps)
    {
        var a = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a, fps);
        return a;
    }

    public static TextBlock T(string s, double size, Brush fg, bool bold = false) => new()
    {
        Text = s, FontFamily = Font, FontSize = size, Foreground = fg,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Chip(string s, bool on, Action click)
    {
        var b = new Border
        {
            Child = T(s, 13, on ? Ink : Dim, bold: true),
            Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0),
            BorderBrush = on ? Accent : Line, BorderThickness = new Thickness(1),
            Background = on ? Accent : ChipFill, Cursor = Cursors.Hand,
        };
        b.MouseEnter += (_, _) => b.BorderBrush = Accent;
        b.MouseLeave += (_, _) => b.BorderBrush = on ? Accent : Line;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;   // don't start a window drag
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, WM_WINDOWPOSCHANGING = 0x0046;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_BOTTOM = new(1);

    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    /// <summary>Work area (minus taskbar) of the monitor the window is on, in WPF units.</summary>
    public static Rect WorkArea(Window w)
    {
        var mon = MonitorFromWindow(new WindowInteropHelper(w).Handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(w);
        var r = mi.rcWork;
        return new Rect(r.L / dpi.DpiScaleX, r.T / dpi.DpiScaleY, (r.R - r.L) / dpi.DpiScaleX, (r.B - r.T) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Keeps a window on the desktop layer: hidden from Alt+Tab and forced under every other
    /// window. The map is owned by the desktop window (Progman) so Show Desktop leaves it visible;
    /// widgets are owned by the map, which keeps them above it. If Progman can't be found the
    /// windows still stay bottom-most, just not through Win+D. A no-activate window never takes
    /// focus from the app you are using.
    /// </summary>
    public static void PinToDesktop(Window w, bool noActivate = false)
    {
        if (PresentationSource.FromVisual(w) is not HwndSource src) return;
        var h = src.Handle;
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;   // fully transparent pixels already let clicks through
        SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)(ex & ~WS_EX_APPWINDOW));

        var owner = w is WorldMap ? IntPtr.Zero : Handle(Hub.Map);
        if (owner == IntPtr.Zero) owner = FindWindow("Progman", null);
        if (owner != IntPtr.Zero) SetWindowLongPtr(h, GWLP_HWNDPARENT, owner);
        src.AddHook(StayAtBottom);
        SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    static IntPtr Handle(Window? w) => w is null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;


    static IntPtr StayAtBottom(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var p = Marshal.PtrToStructure<WINDOWPOS>(l);
            p.hwndInsertAfter = HWND_BOTTOM;
            p.flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(p, l, false);
        }
        return IntPtr.Zero;
    }
}
