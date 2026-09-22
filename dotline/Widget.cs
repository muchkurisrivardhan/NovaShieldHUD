using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Dotline;

/// <summary>
/// One movable desktop widget. Its tab strip switches it between the life timer and any goal,
/// so a single widget can show everything; "Open in new widget" puts another view beside it.
/// </summary>
sealed class Widget : Window
{
    public readonly WidgetData W;
    public GoalData? Goal => Hub.S.Goals.FirstOrDefault(g => g.Id == W.View);

    readonly Grid root = new();
    readonly ChamferPlate plate = new() { CutTopLeft = 16, CutBottomRight = 16 };
    readonly StackPanel body = new() { Margin = new Thickness(18, 12, 20, 14), MinWidth = 300 };
    readonly ScaleTransform scale = new(1, 1);
    readonly TranslateTransform slide = new();
    readonly Border[] brackets = new Border[4];
    readonly Border sweep;
    bool engaged, dragging, resizing;
    Action? tick;   // per-second update for the focus view

    static bool Anim => SystemParameters.ClientAreaAnimation;
    static readonly IEasingFunction EaseOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

    public Widget(WidgetData w, int slot)
    {
        W = w;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Dotline";

        // Chrome built once: plate, amber edge, content, scan sweep, lock-on brackets, resize grip.
        root.LayoutTransform = scale;
        root.Margin = new Thickness(14, 12, 18, 22);   // room for the shadow and the brackets' outward travel
        // Shadow on the static plate, not the root: an effect on the root re-blurs the whole widget
        // (every dot) on each frame of any animation inside it.
        plate.Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.55, Color = Colors.Black };
        root.Children.Add(plate);
        root.Children.Add(new Border
        {
            Height = 3, Width = 120, Background = Theme.Accent, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 16, 0),
        });
        body.RenderTransform = slide;
        root.Children.Add(body);

        var scan = new Grid { ClipToBounds = true, IsHitTestVisible = false };
        sweep = new Border
        {
            Height = 44, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, RenderTransform = new TranslateTransform(),
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x00, 0xF0, 0xA0, 0x30), 0), new(Color.FromArgb(0x26, 0xF0, 0xA0, 0x30), 0.9),
                new(Color.FromArgb(0xFF, 0xF0, 0xA0, 0x30), 0.96), new(Color.FromArgb(0x00, 0xF0, 0xA0, 0x30), 1),
            }, 90),
        };
        scan.Children.Add(sweep);
        root.Children.Add(scan);

        var corners = new[] { (HorizontalAlignment.Left, VerticalAlignment.Top), (HorizontalAlignment.Right, VerticalAlignment.Top),
                              (HorizontalAlignment.Right, VerticalAlignment.Bottom), (HorizontalAlignment.Left, VerticalAlignment.Bottom) };
        for (var i = 0; i < 4; i++)
        {
            var (h, v) = corners[i];
            brackets[i] = new Border
            {
                Width = 20, Height = 20, HorizontalAlignment = h, VerticalAlignment = v, Opacity = 0, IsHitTestVisible = false,
                BorderBrush = Theme.Accent, RenderTransform = new TranslateTransform(),
                BorderThickness = new Thickness(h == HorizontalAlignment.Left ? 2 : 0, v == VerticalAlignment.Top ? 2 : 0,
                                                h == HorizontalAlignment.Right ? 2 : 0, v == VerticalAlignment.Bottom ? 2 : 0),
            };
            root.Children.Add(brackets[i]);
        }
        root.Children.Add(Grip());
        Content = root;

        if (w.X is double px && w.Y is double py && OnScreen(px, py))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = px; Top = py;
        }
        else Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 24 - slot * 32;
            Top = wa.Top + 24 + slot * 32;
            SavePos();
        };

        SourceInitialized += (_, _) => Native.PinToDesktop(this);
        LocationChanged += (_, _) => { if (dragging && !engaged) Engage(); };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Hub.Edit(this); else Drag(); };   // chips mark clicks handled, so these never fire from a tab
        MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(); };
        MouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetScale(W.Scale * (e.Delta > 0 ? 1.08 : 1 / 1.08));
            Hub.Save();
        };
        Loaded += (_, _) => Sweep();
        Refresh();
    }

    // A monitor that was unplugged would otherwise strand the widget off-screen.
    static bool OnScreen(double x, double y) =>
        x >= SystemParameters.VirtualScreenLeft - 50 && y >= SystemParameters.VirtualScreenTop - 20 &&
        x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;

    void SavePos() { W.X = Left; W.Y = Top; Hub.Save(); }

    void SetScale(double s) => scale.ScaleX = scale.ScaleY = W.Scale = Math.Clamp(s, 0.6, 2.5);

    public void Refresh()
    {
        SetScale(W.Scale);
        plate.Fill = Theme.Plate(Hub.S.Glass);
        plate.Stroke = engaged ? Theme.Accent : Hub.S.Glass == 0 ? null : Theme.Line;
        body.Effect = Hub.S.Glass == 0 ? new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9, Color = Colors.Black } : null;
        body.Children.Clear();
        var goal = Goal;
        tick = null;
        if (W.View == "focus") BuildFocus(body);
        else if (goal is null) BuildLife(body);
        else BuildGoal(body, goal);
        body.Children.Insert(1, Tabs());
    }

    /// <summary>LIFE, one tab per goal, and + GOAL.</summary>
    WrapPanel Tabs()
    {
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Tab(string label, bool on, Action click)
        {
            var c = Theme.Chip(label, on, click);
            c.Margin = new Thickness(0, 0, 4, 4);
            tabs.Children.Add(c);
        }
        Tab("LIFE", W.View == "life" || (Goal is null && W.View != "focus"), () => Show("life"));
        Tab("FOCUS", W.View == "focus", () => Hub.OpenFocus(this));
        foreach (var g in Hub.S.Goals)
            Tab((g.Name.Length > 14 ? g.Name[..13] + "…" : g.Name).ToUpperInvariant(), g.Id == W.View, () => Show(g.Id));
        Tab("+ GOAL", false, () => Hub.NewGoal(this));
        return tabs;
    }

    /// <summary>Switch this widget's view: content slides out, the new view slides in under a scan sweep.</summary>
    public void Show(string view)
    {
        if (W.View == view) return;
        W.View = view;
        Hub.Save();
        if (!Anim) { Refresh(); return; }
        var outDur = TimeSpan.FromMilliseconds(120);
        var fadeOut = new DoubleAnimation(0, outDur);
        fadeOut.Completed += (_, _) =>
        {
            Refresh();
            var inDur = TimeSpan.FromMilliseconds(320);
            body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, inDur));
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, inDur) { EasingFunction = EaseOut });
            Sweep();
        };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-18, outDur));
        body.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ---- motion: lock-on brackets, scan sweep, eased glide ---------------------------------

    /// <summary>Target lock: amber brackets jump out to the corners and the outline lights up.</summary>
    void Engage()
    {
        engaged = true;
        plate.Stroke = Theme.Accent;
        for (var i = 0; i < 4; i++)
        {
            var t = (TranslateTransform)brackets[i].RenderTransform;
            double dx = i is 0 or 3 ? -8 : 8, dy = i < 2 ? -8 : 8;
            if (!Anim) { brackets[i].Opacity = 1; t.X = dx; t.Y = dy; continue; }
            var d = TimeSpan.FromMilliseconds(180);
            brackets[i].BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, d) { EasingFunction = EaseOut });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, d) { EasingFunction = EaseOut });
        }
    }

    /// <summary>Lock released: brackets pull back in and fade, then a scan line passes over.</summary>
    void Disengage()
    {
        engaged = false;
        plate.Stroke = Hub.S.Glass == 0 ? null : Theme.Line;
        for (var i = 0; i < 4; i++)
        {
            var t = (TranslateTransform)brackets[i].RenderTransform;
            if (!Anim) { brackets[i].Opacity = 0; t.X = t.Y = 0; continue; }
            var d = TimeSpan.FromMilliseconds(260);
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = EaseOut });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = EaseOut });
            brackets[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(160) });
        }
        Sweep();
    }

    void Sweep()
    {
        if (!Anim || root.ActualHeight <= 0) return;
        var d = TimeSpan.FromMilliseconds(650);
        ((TranslateTransform)sweep.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-sweep.Height, root.ActualHeight, d) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        sweep.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)),
                new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.8)),
                new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)),
            },
            Duration = d,
        });
    }

    void Drag()
    {
        if (Hub.S.Locked || resizing) return;
        dragging = true;
        try { DragMove(); } catch (InvalidOperationException) { }
        dragging = false;
        if (!engaged) return;   // it was a click, not a move

        // Snap to the edges of whichever monitor it was dropped on.
        var wa = Native.WorkArea(this);
        const double gap = 8, pull = 36;
        double x = Left, y = Top;
        if (Math.Abs(Left - wa.Left) < pull) x = wa.Left + gap;
        if (Math.Abs(wa.Right - (Left + ActualWidth)) < pull) x = wa.Right - ActualWidth - gap;
        if (Math.Abs(Top - wa.Top) < pull) y = wa.Top + gap;
        if (Math.Abs(wa.Bottom - (Top + ActualHeight)) < pull) y = wa.Bottom - ActualHeight - gap;
        if (x != Left || y != Top) Glide(x, y);
        else { SavePos(); Disengage(); }
    }

    /// <summary>Fly to a spot with a strong ease-out, locked on while in flight.</summary>
    void Glide(double x, double y)
    {
        double fromX = Left, fromY = Top;
        Left = x; Top = y;
        SavePos();
        if (!Anim) { if (engaged) Disengage(); return; }
        if (!engaged) Engage();
        var d = TimeSpan.FromMilliseconds(560);
        var ax = new DoubleAnimation(fromX, x, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        var ay = new DoubleAnimation(fromY, y, d) { EasingFunction = EaseOut, FillBehavior = FillBehavior.Stop };
        ax.Completed += (_, _) => Disengage();
        BeginAnimation(LeftProperty, ax);
        BeginAnimation(TopProperty, ay);
    }

    void MoveTo(string where)
    {
        var wa = Native.WorkArea(this);
        const double m = 16;
        double l = wa.Left + m, r = wa.Right - ActualWidth - m, cx = wa.Left + (wa.Width - ActualWidth) / 2;
        double t = wa.Top + m, b = wa.Bottom - ActualHeight - m, cy = wa.Top + (wa.Height - ActualHeight) / 2;
        var (x, y) = where switch
        {
            "tl" => (l, t), "tr" => (r, t), "bl" => (l, b), "br" => (r, b),
            "left" => (l, cy), "right" => (r, cy), _ => (cx, cy),
        };
        Glide(x, y);
    }

    /// <summary>Bottom-right grip: drag to resize. The whole widget scales, so the dots stay crisp.</summary>
    FrameworkElement Grip()
    {
        var g = new Canvas
        {
            Width = 16, Height = 16, Background = Theme.Hit, Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize (Ctrl+scroll works too)",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 1, 6),
        };
        foreach (var (px, py) in new[] { (12, 2), (7, 7), (12, 7), (2, 12), (7, 12), (12, 12) })
        {
            var dot = new Ellipse { Width = 2.5, Height = 2.5, Fill = Theme.Accent };
            Canvas.SetLeft(dot, px);
            Canvas.SetTop(dot, py);
            g.Children.Add(dot);
        }
        double startScale = 1, startX = 0;
        g.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            resizing = true;
            startScale = W.Scale;
            startX = e.GetPosition(this).X;
            g.CaptureMouse();
            Engage();
        };
        g.MouseMove += (_, e) =>
        {
            // root.ActualWidth is the unscaled width, so this maps pointer travel to scale 1:1.
            if (resizing) SetScale(startScale + (e.GetPosition(this).X - startX) / root.ActualWidth);
        };
        g.MouseLeftButtonUp += (_, e) =>
        {
            if (!resizing) return;
            e.Handled = true;
            resizing = false;
            g.ReleaseMouseCapture();
            Hub.Save();
            Disengage();
        };
        return g;
    }

    /// <summary>Machined disc with a turning dashed amber ring around Dotline's 3x3 dot mark.</summary>
    static Viewbox Emblem()
    {
        var g = new Grid { Width = 52, Height = 52 };
        g.Children.Add(new Ellipse
        {
            StrokeThickness = 1.5, Stroke = Theme.B(0xFF6A6E74),
            Fill = new RadialGradientBrush(Color.FromRgb(0x45, 0x48, 0x4D), Color.FromRgb(0x1B, 0x1C, 0x1F)) { GradientOrigin = new Point(0.35, 0.3) },
        });
        var ring = new Ellipse
        {
            Margin = new Thickness(4), Stroke = Theme.Accent, StrokeThickness = 2.5,
            StrokeDashArray = [3, 1.6], RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(),
        };
        // One turn whenever the content is rebuilt, then rest: a ring spinning forever repaints
        // the whole see-through widget on every frame, all day.
        if (SystemParameters.ClientAreaAnimation)
            ring.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { EasingFunction = EaseOut });
        g.Children.Add(ring);
        g.Children.Add(new Ellipse { Margin = new Thickness(10), Stroke = Theme.B(0xFF55585E), StrokeThickness = 1 });
        var mark = new UniformGrid { Rows = 3, Columns = 3, Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 9; i++)
            mark.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = i < 5 ? Theme.Accent : Theme.Hollow });
        g.Children.Add(mark);
        return new Viewbox { Width = 34, Height = 34, Child = g, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Emblem, "DOTLINE / VER 1.0 // X MODULE" title block, and a status tag on the right.</summary>
    static DockPanel Header(string module, string? tag, bool warn = false)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
        var emblem = Emblem();
        DockPanel.SetDock(emblem, Dock.Left);
        dp.Children.Add(emblem);
        if (tag is not null)
        {
            var t = new Border
            {
                Child = Theme.T(tag, 12, warn ? Theme.Warn : Theme.Ink, bold: true),
                Background = warn ? Theme.B(0x33FF6B5B) : Theme.Accent,
                BorderBrush = warn ? Theme.Warn : Theme.Accent, BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 2, 0, 0),
            };
            DockPanel.SetDock(t, Dock.Right);
            dp.Children.Add(t);
        }
        var block = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        block.Children.Add(Theme.T("DOTLINE", 18, Theme.B(0xFFE8E8E8), bold: true));
        var sub = new TextBlock { FontFamily = Theme.Font, FontSize = 11.5, Foreground = Theme.Faint, Margin = new Thickness(0, -3, 0, 0) };
        sub.Inlines.Add(new System.Windows.Documents.Run("VER 1.0  //  "));
        sub.Inlines.Add(new System.Windows.Documents.Run(module + " MODULE") { Foreground = Theme.Accent });
        block.Children.Add(sub);
        dp.Children.Add(block);
        return dp;
    }

    // Machined metal, not light: a plain drop shadow under big numbers instead of a glow.
    static TextBlock Big(string s, double size, Brush fg)
    {
        var t = Theme.T(s, size, fg, bold: true);
        t.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black };
        return t;
    }

    /// <summary>Readouts styled like Beacon's CPU/RAM: dim label, bold amber value.</summary>
    static StackPanel Stats(params (string k, string v)[] rows)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (k, v) in rows)
        {
            var line = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontFamily = Theme.Font, FontSize = 13 };
            line.Inlines.Add(new System.Windows.Documents.Run(k + "  ") { Foreground = Theme.Faint });
            line.Inlines.Add(new System.Windows.Documents.Run(v) { Foreground = Theme.Accent, FontWeight = FontWeights.Bold, FontSize = 15 });
            sp.Children.Add(line);
        }
        return sp;
    }

    static DockPanel Row(UIElement left, UIElement right)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(right, Dock.Right);
        dp.Children.Add(right);
        dp.Children.Add(left);
        return dp;
    }

    static (int cols, double dot, double gap) Layout(string unit, bool life) => (unit, life) switch
    {
        ("year", _) => (10, 14, 5),
        ("month", true) => (36, 6, 3),
        ("month", false) => (12, 12, 4),
        ("week", true) => (52, 4, 2),
        ("week", false) => (26, 8, 3),
        _ => (30, 6, 3),
    };

    /// <summary>The dot field plus a pulsing ring on the "now" dot.</summary>
    static Grid Field(int total, int done, string unit, bool life, Func<int, string> describe)
    {
        var (cols, dot, gap) = Layout(unit, life);
        var grid = new DotGrid(total, done, cols, dot, gap, describe);
        var host = new Grid { Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(grid);
        if (done < total && SystemParameters.ClientAreaAnimation)
        {
            var c = grid.Center(done);
            var d = Math.Max(dot * 2.6, 10);
            var ring = new Ellipse
            {
                Width = d, Height = d, Stroke = Theme.Accent, StrokeThickness = 1.2, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(c.X - d / 2, c.Y - d / 2, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
            };
            var pulse = Theme.Loop(0.3, 1.3, 1.6, fps: 8);
            var fade = Theme.Loop(1, 0, 1.6, fps: 8);
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            ring.BeginAnimation(OpacityProperty, fade);
            host.Children.Add(ring);
        }
        return host;
    }

    static DockPanel Footer(string current, string[] units, Action<string> pick, string legendDone)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (brush, label) in new[] { (Theme.Steel, legendDone), (Theme.Accent, "NOW"), (Theme.Empty, "LEFT") })
        {
            legend.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, Margin = new Thickness(0, 0, 5, 0) });
            legend.Children.Add(Theme.T(label, 12, Theme.Faint));
            ((FrameworkElement)legend.Children[^1]).Margin = new Thickness(0, 0, 12, 0);
        }
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var u in units) chips.Children.Add(Theme.Chip(Units.Plural(u).ToUpperInvariant(), u == current, () => pick(u)));
        var dp = Row(legend, chips);
        dp.Margin = new Thickness(0);
        return dp;
    }

    void BuildLife(StackPanel body)
    {
        var s = Hub.S;
        var b = s.Birthday.Date;
        var end = b.AddYears(s.Lifespan);
        var today = DateTime.Today;
        var u = s.LifeUnit;
        var total = Units.Span(b, end, u);
        var done = Math.Clamp(Units.Between(b, today, u), 0, total);
        var pct = Math.Clamp((DateTime.Now - b).TotalDays / (end - b).TotalDays * 100, 0, 100);
        var U = Units.Plural(u).ToUpperInvariant();

        body.Children.Add(Header("LIFE", s.Example ? "EXAMPLE · DOUBLE-CLICK TO SET" : $"{s.Lifespan} YR SPAN"));
        body.Children.Add(Row(
            Big(pct.ToString("0.0") + "%", 48, Theme.B(0xFFEDEDED)),
            Stats(("AGE", Units.Between(b, today, "year").ToString()),
                  (U + " LIVED", done.ToString("N0")),
                  (U + " LEFT", Math.Max(0, total - done).ToString("N0")),
                  ("DAYS LEFT", Math.Max(0, (end - today).Days).ToString("N0")))));
        body.Children.Add(Field(total, done, u, life: true, i =>
        {
            var from = Units.Add(b, i, u);
            var state = i < done ? "LIVED" : i == done ? "YOU ARE HERE" : "AHEAD";
            return $"{u.ToUpperInvariant()} {i + 1:N0}  ·  {from:dd MMM yyyy}  ·  AGE {Units.Between(b, from, "year")}  ·  {state}";
        }));
        body.Children.Add(Footer(u, ["year", "month", "week"], nu => { s.LifeUnit = nu; Hub.Save(); Refresh(); }, "LIVED"));
    }

    void BuildGoal(StackPanel body, GoalData g)
    {
        var a = g.Start.Date;
        var e = g.End.Date;
        var today = DateTime.Today;
        var u = g.Unit;
        var total = Units.Span(a, e, u);
        var passed = today >= e ? total : Math.Clamp(Units.Between(a, today, u), 0, total);
        var timePct = Math.Clamp((DateTime.Now - a).TotalDays / Math.Max(1, (e - a).TotalDays) * 100, 0, 100);
        var diff = g.Pct - timePct;
        var (tag, warn) =
            g.Pct >= 100 ? ("ACHIEVED", false) :
            today > e ? ("OVERDUE", true) :
            diff >= 0 ? ("ON TRACK", false) :
            ($"BEHIND {-diff:0}%", true);

        body.Children.Add(Header("GOAL", tag, warn));
        var name = Theme.T(g.Name.ToUpperInvariant(), 24, Theme.Text, bold: true);
        name.TextWrapping = TextWrapping.Wrap;
        name.MaxWidth = 340;
        body.Children.Add(name);
        body.Children.Add(Theme.T($"{a:dd MMM yyyy}  →  {e:dd MMM yyyy}", 13, Theme.Dim));

        var U = Units.Plural(u).ToUpperInvariant();
        var big = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        big.Children.Add(Big(g.Pct + "%", 42, Theme.B(0xFFEDEDED)));
        big.Children.Add(Theme.T("  DONE", 13, Theme.Faint));
        body.Children.Add(Row(big, Stats(("TIME USED", timePct.ToString("0") + "%"),
                                         (U + " LEFT", Math.Max(0, total - passed).ToString("N0")))));

        // Progress: 20 clickable segments of 5% each; clicking the top filled one steps back.
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
        for (var i = 0; i < 20; i++)
        {
            var step = (i + 1) * 5;
            var seg = new Border
            {
                Width = 13, Height = 9, Margin = new Thickness(0, 0, 2, 0),
                Background = step <= g.Pct ? Theme.Accent : Theme.Empty,
                ToolTip = step + "%",
            };
            seg.MouseLeftButtonDown += (_, ev) => ev.Handled = true;
            seg.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                g.Pct = g.Pct == step ? step - 5 : step;
                Hub.Save();
                Refresh();
            };
            bar.Children.Add(seg);
        }
        body.Children.Add(bar);

        if (total > 1500)
            body.Children.Add(Theme.T($"{total:N0} {U} IS TOO MANY DOTS — PICK A BIGGER UNIT", 13, Theme.Warn));
        else
            body.Children.Add(Field(total, passed, u, life: false, i =>
            {
                var from = Units.Add(a, i, u);
                var state = i < passed ? "PASSED" : i == passed ? "NOW" : "AHEAD";
                return $"{u.ToUpperInvariant()} {i + 1:N0} OF {total:N0}  ·  {from:dd MMM yyyy}  ·  {state}";
            }));
        body.Children.Add(Footer(u, ["day", "week", "month", "year"], nu => { g.Unit = nu; Hub.Save(); Refresh(); }, "PASSED"));
    }

    public void Tick() => tick?.Invoke();

    /// <summary>Draw the eye: lock-on flash and a scan over the widget (a session ended, or it was asked for).</summary>
    public void Flash()
    {
        Refresh();
        Engage();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) => { t.Stop(); Disengage(); };
        t.Start();
    }

    static string Clock(double seconds)
    {
        var s = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return s.TotalHours >= 1 ? s.ToString(@"h\:mm\:ss") : s.ToString(@"mm\:ss");
    }

    void BuildFocus(StackPanel body)
    {
        var f = Hub.S.Focus;
        var (todayCount, todayMin) = FocusTimer.Day(DateTime.Today);
        body.Children.Add(Header("FOCUS", f.Active ? (f.Paused ? "PAUSED" : "IN SESSION") : $"TODAY {todayCount} · {todayMin} MIN", warn: f.Paused));

        // The dial: 60 dots, one per sixtieth of the session, filling clockwise from the top.
        var dial = new FocusDial();
        var time = Big("", 50, Theme.B(0xFFEDEDED));
        time.HorizontalAlignment = HorizontalAlignment.Center;
        var sub = Theme.T("", 13, Theme.Faint);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(time);
        center.Children.Add(sub);
        var face = new Grid { Width = 232, Height = 232, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 10) };
        face.Children.Add(dial);
        face.Children.Add(center);
        body.Children.Add(face);

        void Update()
        {
            var left = f.SecondsLeft;
            time.Text = Clock(left);
            sub.Text = f.Active ? "OF " + FocusTimer.Length(f.Seconds) + (f.Paused ? "  ·  PAUSED" : "") : "READY";
            dial.Set(f.Active ? 1 - left / f.Seconds : 0, running: f.Active && !f.Paused);
        }
        Update();
        tick = Update;

        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        Border Btn(string label, bool on, Action act)
        {
            var c = Theme.Chip(label, on, () => { act(); Hub.RefreshFocus(); Sweep(); });
            c.Margin = new Thickness(2, 0, 2, 4);
            controls.Children.Add(c);
            return c;
        }
        if (!f.Active)
        {
            foreach (var p in FocusTimer.Presets) Btn($"{p / 60} MIN", f.Seconds == p, () => FocusTimer.SetLength(p));

            // CUSTOM keeps its own saved length: once set it shows that time, one click picks it,
            // and a click while it's picked opens the editor to change it.
            var saved = f.CustomSeconds;
            var picked = saved > 0 && f.Seconds == saved && !FocusTimer.Presets.Contains(saved);
            var chip = Btn(saved > 0 ? Clock(saved) : "CUSTOM", picked, () =>
            {
                if (saved > 0 && !picked) FocusTimer.SetLength(saved);
                else if (EditWindow.EditFocusLength(saved > 0 ? saved : f.Seconds) is int s) FocusTimer.SetCustom(s);
            });
            chip.ToolTip = saved > 0 ? (picked ? "Click to change your custom length" : "Your saved custom length") : "Set hours, minutes and seconds";
            Btn("START", true, FocusTimer.Start);
        }
        else
        {
            if (f.Paused) Btn("RESUME", true, FocusTimer.Resume);
            else Btn("PAUSE", false, FocusTimer.Pause);
            Btn("+5 MIN", false, FocusTimer.AddFive);
            Btn("STOP", false, FocusTimer.Stop);
        }
        body.Children.Add(controls);

        // Session log: the last seven days, one dot per 25 minutes focused.
        var week = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        int weekCount = 0, weekMin = 0;
        for (var i = 6; i >= 0; i--)
        {
            var day = DateTime.Today.AddDays(-i);
            var (c, m) = FocusTimer.Day(day);
            weekCount += c;
            weekMin += m;
            var lit = Math.Min(6, (int)Math.Ceiling(m / 25.0));
            var col = new StackPanel { Margin = new Thickness(0, 0, 9, 0), ToolTip = $"{day:ddd dd MMM}: {c} sessions, {m} min" };
            for (var k = 5; k >= 0; k--)
                col.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 0, 3), Fill = k < lit ? Theme.Accent : Theme.Empty });
            var label = Theme.T(day.ToString("ddd")[..1], 12, i == 0 ? Theme.Accent : Theme.Faint);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(label);
            week.Children.Add(col);
        }
        body.Children.Add(Row(week, Stats(("TODAY", $"{todayCount} · {todayMin} MIN"),
                                         ("7 DAYS", $"{weekCount} · {weekMin / 60}H {weekMin % 60:00}M"))));
    }

    void ShowMenu()
    {
        var m = new ContextMenu
        {
            Background = Theme.Field, Foreground = Theme.Text, BorderBrush = Theme.Accent,
            FontFamily = Theme.Font, FontSize = 15,
        };
        MenuItem Item(ItemsControl parent, string header, Action? act = null, bool check = false)
        {
            var mi = new MenuItem { Header = header, IsChecked = check, Foreground = Theme.Text, Background = Theme.Field };
            if (act is not null) mi.Click += (_, _) => act();
            parent.Items.Add(mi);
            return mi;
        }

        var goal = Goal;
        if (W.View != "focus") Item(m, goal is null ? "Set birthday & lifespan…" : "Edit goal…", () => Hub.Edit(this));
        Item(m, "New goal…", () => Hub.NewGoal(this));
        if (goal is not null) Item(m, "Delete this goal", () => Hub.Delete(goal));
        var show = Item(m, "Show");
        Item(show, "Life", () => Show("life"), W.View == "life");
        Item(show, "Focus timer", () => Show("focus"), W.View == "focus");
        foreach (var g in Hub.S.Goals) Item(show, g.Name, () => Show(g.Id), g.Id == W.View);
        m.Items.Add(new Separator());

        Item(m, $"World map ({Hub.Map.Status})…", Hub.EditMap);
        if (FlightTracker.Flight is { } tracked) Item(m, $"Stop tracking flight {tracked}", FlightTracker.Stop);
        Item(m, "Open in new widget", () => Hub.OpenNew(W.View));
        if (Hub.Count > 1) Item(m, "Close this widget", () => Hub.CloseWidget(this));
        var move = Item(m, "Move to");
        foreach (var (n, k) in new[] { ("Top left", "tl"), ("Top right", "tr"), ("Left middle", "left"), ("Center", "c"),
                                       ("Right middle", "right"), ("Bottom left", "bl"), ("Bottom right", "br") })
            Item(move, n, () => MoveTo(k));
        var sz = Item(m, "Size");
        foreach (var (n, v) in new[] { ("Small", 0.8), ("Medium", 1.0), ("Large", 1.25), ("Extra large", 1.5) })
            Item(sz, n, () => { SetScale(v); Hub.Save(); Disengage(); }, Math.Abs(W.Scale - v) < 0.01);
        var bg = Item(m, "Background");
        foreach (var (n, v) in new[] { ("Clear", 0), ("Glass", 1), ("Solid", 2) })
            Item(bg, n, () => { Hub.S.Glass = v; Hub.SaveAndRefresh(); }, Hub.S.Glass == v);
        Item(m, "Lock positions", () => { Hub.S.Locked = !Hub.S.Locked; Hub.Save(); }, Hub.S.Locked);
        Item(m, "Start with Windows", Hub.ToggleStartup, Hub.StartsWithWindows);
        m.Items.Add(new Separator());
        Item(m, "Exit Dotline", () => Application.Current.Shutdown());

        m.PlacementTarget = this;
        m.IsOpen = true;
    }
}


/// <summary>Draws a grid of dots: filled up to <c>done</c>, amber at <c>done</c>, hollow after. Hover shows what each dot is.</summary>
sealed class DotGrid : FrameworkElement
{
    static readonly Pen Hollow = MakePen();
    static readonly Brush NowGlow = Theme.B(0x55F0A030);
    readonly int total, done, cols;
    readonly double d, g;
    readonly Func<int, string>? describe;
    readonly ToolTip tip = new()
    {
        Placement = PlacementMode.Relative, Background = Theme.Field, Foreground = Theme.Text,
        BorderBrush = Theme.Accent, FontFamily = Theme.Font, FontSize = 14,
    };
    int last = -1;

    static Pen MakePen() { var p = new Pen(Theme.Hollow, 1); p.Freeze(); return p; }

    public DotGrid(int total, int done, int cols, double dot, double gap, Func<int, string>? describe)
    {
        this.total = total; this.done = done; this.cols = Math.Min(cols, Math.Max(1, total));
        d = dot; g = gap; this.describe = describe;
        tip.PlacementTarget = this;
    }

    int Rows => (total + cols - 1) / cols;
    public Point Center(int i) => new(i % cols * (d + g) + d / 2, i / cols * (d + g) + d / 2);

    protected override Size MeasureOverride(Size _) => new(cols * (d + g) - g, Rows * (d + g) - g);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.Hit, null, new Rect(RenderSize));   // hover works between dots too
        var r = d / 2;
        for (var i = 0; i < total; i++)
        {
            var c = Center(i);
            if (i < done) dc.DrawEllipse(Theme.Steel, null, c, r, r);
            else if (i == done)
            {
                dc.DrawEllipse(NowGlow, null, c, r * 1.9, r * 1.9);
                dc.DrawEllipse(Theme.Accent, null, c, r, r);
            }
            else if (d >= 6) dc.DrawEllipse(null, Hollow, c, r - 0.5, r - 0.5);
            else dc.DrawEllipse(Theme.Empty, null, c, r, r);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (describe is null) return;
        var p = e.GetPosition(this);
        int col = (int)(p.X / (d + g)), row = (int)(p.Y / (d + g)), i = row * cols + col;
        if (col >= cols || i < 0 || i >= total) { tip.IsOpen = false; last = -1; return; }
        if (i != last) { last = i; tip.Content = describe(i); }
        tip.HorizontalOffset = p.X + 16;
        tip.VerticalOffset = p.Y + 18;
        tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e) { tip.IsOpen = false; last = -1; }
}

/// <summary>Circular focus timer: 60 dots round a hairline ring; elapsed in amber, the leading dot glowing.</summary>
sealed class FocusDial : FrameworkElement
{
    const int Dots = 60;
    static readonly Pen Ring = Frozen(new Pen(Theme.B(0x30FFFFFF), 1));
    static readonly Pen Hollow = Frozen(new Pen(Theme.Hollow, 1));
    static readonly Brush Glow = Theme.B(0x55F0A030);
    double progress;
    bool running;

    static Pen Frozen(Pen p) { p.Freeze(); return p; }

    public void Set(double p, bool running)
    {
        p = Math.Clamp(p, 0, 1);
        if (p == progress && running == this.running) return;
        progress = p;
        this.running = running;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - 10;
        dc.DrawEllipse(null, Ring, c, r - 12, r - 12);
        var done = (int)Math.Floor(progress * Dots);
        for (var i = 0; i < Dots; i++)
        {
            var a = i / (double)Dots * 2 * Math.PI - Math.PI / 2;
            var p = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
            var big = i % 5 == 0 ? 3.6 : 2.8;   // every fifth dot marks a twelfth, like a watch face
            if (i < done) dc.DrawEllipse(Theme.Accent, null, p, big, big);
            else if (i == done && progress > 0)
            {
                dc.DrawEllipse(Glow, null, p, 8, 8);
                dc.DrawEllipse(running ? Theme.Accent : Theme.Now, null, p, 4, 4);
            }
            else dc.DrawEllipse(null, Hollow, p, big - 0.5, big - 0.5);
        }
    }
}
