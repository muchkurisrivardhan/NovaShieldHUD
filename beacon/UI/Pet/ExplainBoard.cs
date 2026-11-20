using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Beacon.Core;
using Beacon.Teach;
using Visual = Beacon.Teach.Visual;

namespace Beacon.UI.Pet;

/// <summary>
/// Kai's sketchboard: takes over the screen with a dimmed, gridded board and draws a picture
/// on it (a mind map, timeline, cutaway, 3D illustration...) as Kai explains, step by step.
/// Each step's parts draw themselves in (lines sketch along their length, cards pop in), the
/// parts being talked about glow while the rest dim, and what Kai says runs as a caption.
///
/// It wears the Beacon style: the accent and its family of colours, the page and surface
/// colours, the font and the corners. Esc, right-click or the × closes it; a click skips to the
/// next step.
/// </summary>
public sealed class ExplainBoard : Window
{
    readonly Settings _settings;
    readonly Scene _scene;
    readonly List<VStep> _steps;
    readonly Canvas _canvas = new();
    readonly TextBlock _caption = new();
    readonly TextBlock _counter = new();
    readonly Border _captionPlate = new();
    readonly List<(FrameworkElement Element, Mark Mark, bool Line)> _items = [];
    readonly Color _accent, _page, _surface, _text, _muted;
    readonly Color[] _family;
    readonly double _corner;
    readonly FontFamily _font;
    int _shown = -1;

    /// <summary>The board was closed (Esc, right-click, ×).</summary>
    public event Action? Dismissed;

    /// <summary>Clicked: move on to the next step now.</summary>
    public event Action? Skipped;

    readonly bool _overScreen;

    /// <param name="image">For pointing at things on the screen: the screenshot's size, which callouts are measured in.</param>
    public ExplainBoard(Settings settings, Visual visual, Rect screen, Size? image = null)
    {
        _overScreen = visual.Kind == VisualKind.Screen;
        _settings = settings;
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback); }
            catch (FormatException) { return (Color)ColorConverter.ConvertFromString(fallback); }
        }
        try { _accent = (Color)ColorConverter.ConvertFromString(p.Accent); }
        catch (FormatException) { _accent = Color.FromRgb(0xF0, 0xA0, 0x30); }
        _page = C("page", "#17181A");
        _surface = C("surface", "#2C2E31");
        _text = C("text", "#E6E6E6");
        _muted = C("textSecondary", "#A4A7AC");
        _family = Family(_accent);
        _corner = Math.Min(12, p.Radius * 1.5);
        _font = new FontFamily(p.Font + ", Segoe UI");

        Title = "Kai explains: " + visual.Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Left = screen.Left;
        Top = screen.Top;
        Width = screen.Width;
        Height = screen.Height;
        FontFamily = _font;

        _scene = Sketch.Layout(visual, screen.Width, screen.Height, image?.Width ?? 0, image?.Height ?? 0);
        _steps = visual.Steps;
        Content = Build(visual.Title);
        foreach (var m in _scene.Marks) Add(m);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dismiss();
            else if (e.Key is Key.Right or Key.Space or Key.Enter) Skipped?.Invoke();
        };
        MouseRightButtonUp += (_, _) => Dismiss();
        MouseLeftButtonUp += (_, _) => Skipped?.Invoke();
        if (_overScreen)
        {
            // Over the real screen: see-through and click-through, so you can do what Kai shows
            // while Kai shows it. It never takes the keyboard either; "got it" or Ctrl+Alt+N closes it.
            ShowActivated = false;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var ex = Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
                ex |= Interop.NativeMethods.WS_EX_TOOLWINDOW | Interop.NativeMethods.WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */;
                Interop.NativeMethods.SetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            };
        }
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    /// <summary>The picture's colours: the accent and five companions around the colour wheel (greys for a grey accent).</summary>
    static Color[] Family(Color accent)
    {
        var (h, s, l) = ToHsl(accent);
        if (s < 0.12)
            return [.. new[] { 0.86, 0.62, 0.74, 0.52, 0.94, 0.68 }.Select(g => FromHsl(0, 0, g))];
        double[] offsets = [0, 150, 45, 210, 290, 95];
        return [.. offsets.Select(o => FromHsl((h + o) % 360, Math.Max(0.55, s), Math.Clamp(l, 0.5, 0.62)))];
    }

    UIElement Build(string title)
    {
        var root = new Grid();
        if (_overScreen) return BuildOverScreen(root, title);

        // The board: the style's page colour over the screen, with a faint drafting grid.
        root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xEB, _page.R, _page.G, _page.B)) });
        var grid = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 40, 40), ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x16, _accent.R, _accent.G, _accent.B)), 1),
                new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(40, 0)), new LineGeometry(new Point(0, 0), new Point(0, 40)) } }),
        };
        root.Children.Add(new Rectangle { Fill = grid });

        // Title, underlined in the accent.
        var head = new StackPanel { Margin = new Thickness(64, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        head.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_text) });
        head.Children.Add(new Rectangle { Height = 3, Width = Math.Min(Width - 200, Sketch.TextWidth(title, 30) + 10), Fill = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(head);

        root.Children.Add(_canvas);

        // The close button and hint, top right.
        var close = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(_corner), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(_surface), BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, _accent.R, _accent.G, _accent.B)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "✕", FontSize = 15, Foreground = new SolidColorBrush(_text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); };
        var corner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 30, 0) };
        corner.Children.Add(new TextBlock { Text = "Click: next  ·  Esc: close", Foreground = new SolidColorBrush(_muted), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        corner.Children.Add(close);
        root.Children.Add(corner);

        AddCaption(root);
        return root;
    }

    /// <summary>Over the real screen: barely dimmed, the title in a small tag at the top, and the caption.</summary>
    UIElement BuildOverScreen(Grid root, string title)
    {
        root.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x24, _page.R, _page.G, _page.B)) });
        root.Children.Add(_canvas);
        var tag = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(16, 6, 16, 7), CornerRadius = new CornerRadius(_corner),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, _surface.R, _surface.G, _surface.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xB0, _accent.R, _accent.G, _accent.B)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = _accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.4 },
        };
        var line = new TextBlock { FontSize = 14.5, Foreground = new SolidColorBrush(_text) };
        line.Inlines.Add(new System.Windows.Documents.Run("Kai is looking: ") { Foreground = new SolidColorBrush(_accent) });
        line.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run("   ·   say “got it” to close") { Foreground = new SolidColorBrush(_muted), FontSize = 12.5 });
        tag.Child = line;
        root.Children.Add(tag);
        AddCaption(root);
        return root;
    }

    void AddCaption(Grid root)
    {
        // The caption: what Kai is saying, at the bottom.
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.FontSize = 19;
        _caption.Foreground = new SolidColorBrush(_text);
        _caption.TextAlignment = TextAlignment.Center;
        _counter.FontSize = 12;
        _counter.Foreground = new SolidColorBrush(_accent);
        _counter.HorizontalAlignment = HorizontalAlignment.Center;
        _counter.Margin = new Thickness(0, 0, 0, 3);
        var stack = new StackPanel();
        stack.Children.Add(_counter);
        stack.Children.Add(_caption);
        _captionPlate.Child = stack;
        _captionPlate.MaxWidth = Math.Min(980, Width - 200);
        _captionPlate.Padding = new Thickness(22, 8, 22, 11);
        _captionPlate.CornerRadius = new CornerRadius(_corner);
        _captionPlate.Background = new SolidColorBrush(Color.FromArgb(0xF2, _surface.R, _surface.G, _surface.B));
        _captionPlate.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, _accent.R, _accent.G, _accent.B));
        _captionPlate.BorderThickness = new Thickness(1);
        _captionPlate.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.35 };
        _captionPlate.HorizontalAlignment = HorizontalAlignment.Center;
        _captionPlate.VerticalAlignment = VerticalAlignment.Bottom;
        _captionPlate.Margin = new Thickness(0, 0, 0, 22);
        _captionPlate.Visibility = Visibility.Collapsed;
        root.Children.Add(_captionPlate);
    }

    // ------------------------------------------------------------------ marks to shapes

    Color Ink(int ink) => ink switch
    {
        Sketch.Accent => _accent,
        Sketch.Ink => _text,
        Sketch.Muted => _muted,
        Sketch.OnAccent => _page,
        _ => _family[ink % _family.Length],
    };

    Brush FillFor(Mark m)
    {
        var ink = Ink(m.Ink);
        var solid = m.Kind == MarkKind.Box || m.Fill >= 0.5 || m.Shade != 0;
        if (!solid) return new SolidColorBrush(Color.FromArgb((byte)(255 * m.Fill), ink.R, ink.G, ink.B));
        var c = Mix(_surface, ink, m.Fill);
        c = m.Shade >= 0 ? Mix(c, Colors.White, m.Shade) : Mix(c, Colors.Black, -m.Shade);
        return new SolidColorBrush(c);
    }

    void Add(Mark m)
    {
        var ink = Ink(m.Ink);
        var stroke = new SolidColorBrush(m.Kind == MarkKind.Polygon && m.Fill > 0.8 ? Mix(ink, Colors.Black, 0.45) : ink);
        switch (m.Kind)
        {
            case MarkKind.Line:
            {
                var line = new Polyline
                {
                    Points = new PointCollection(m.Pts.Select(p => new Point(p.X, p.Y))),
                    Stroke = stroke, StrokeThickness = m.Stroke,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                };
                Place(line, m, true);
                if (m.Arrow && m.Pts.Length >= 2)
                {
                    var a = m.Pts[^1];
                    var b = m.Pts[^2];
                    var d = a - b;
                    if (d.Length > 0.1)
                    {
                        d *= 1 / d.Length;
                        var n = new P(-d.Y, d.X);
                        var size = 7 + m.Stroke * 2.4;
                        var p1 = a - d * size + n * (size * 0.5);
                        var p2 = a - d * size - n * (size * 0.5);
                        Place(new Polygon { Points = [new(a.X, a.Y), new(p1.X, p1.Y), new(p2.X, p2.Y)], Fill = new SolidColorBrush(ink) }, m with { Kind = MarkKind.Polygon }, false);
                    }
                }
                break;
            }
            case MarkKind.Polygon:
                Place(new Polygon
                {
                    Points = new PointCollection(m.Pts.Select(p => new Point(p.X, p.Y))),
                    Fill = m.Fill > 0 ? FillFor(m) : null, Stroke = m.Stroke > 0 ? stroke : null, StrokeThickness = m.Stroke,
                    StrokeLineJoin = PenLineJoin.Round, StrokeDashArray = m.Dashed ? [4, 3] : null,
                }, m, false);
                break;
            case MarkKind.Ellipse:
            {
                var e = new Ellipse
                {
                    Width = m.Pts[1].X * 2, Height = m.Pts[1].Y * 2,
                    Fill = m.Fill > 0 ? FillFor(m) : null, Stroke = m.Stroke > 0 ? stroke : null, StrokeThickness = m.Stroke,
                };
                Canvas.SetLeft(e, m.Pts[0].X - m.Pts[1].X);
                Canvas.SetTop(e, m.Pts[0].Y - m.Pts[1].Y);
                Place(e, m, false);
                break;
            }
            case MarkKind.Box:
            {
                var r = new Rectangle
                {
                    Width = m.Pts[1].X, Height = m.Pts[1].Y, RadiusX = _corner, RadiusY = _corner,
                    Fill = FillFor(m), Stroke = stroke, StrokeThickness = m.Stroke,
                };
                Canvas.SetLeft(r, m.Pts[0].X);
                Canvas.SetTop(r, m.Pts[0].Y);
                Place(r, m, false);
                break;
            }
            case MarkKind.Text:
            {
                var t = new TextBlock
                {
                    Text = m.Text, FontSize = m.Size, FontWeight = m.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(ink), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    MaxWidth = m.MaxWidth > 0 ? m.MaxWidth : 900, FontFamily = _font,
                };
                FrameworkElement el = t;
                if (m.Plate)
                    el = new Border
                    {
                        Child = t, Padding = new Thickness(7, 2, 7, 3), CornerRadius = new CornerRadius(_corner),
                        Background = new SolidColorBrush(_page), BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, _accent.R, _accent.G, _accent.B)), BorderThickness = new Thickness(1),
                    };
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = el.DesiredSize;
                Canvas.SetLeft(el, m.Align == Align.Left ? m.Pts[0].X : m.Pts[0].X - size.Width / 2);
                Canvas.SetTop(el, m.Align == Align.Left ? m.Pts[0].Y : m.Pts[0].Y - size.Height / 2);
                Place(el, m, false);
                break;
            }
        }
    }

    void Place(FrameworkElement el, Mark m, bool line)
    {
        el.Opacity = 0;
        el.IsHitTestVisible = false;
        _canvas.Children.Add(el);
        _items.Add((el, m, line));
    }

    // ------------------------------------------------------------------ steps

    /// <summary>Shows step i: draws in everything up to it, lights up what it's about, and captions it.</summary>
    public void Step(int i)
    {
        if (i < 0 || i >= _steps.Count) return;
        var before = _shown;
        Reveal(i);
        Highlight(_steps[i].Focus, before);
        _counter.Text = $"{i + 1} / {_steps.Count}";
        _caption.Text = _steps[i].Say;
        _captionPlate.Visibility = Visibility.Visible;
    }

    /// <summary>The whole picture, nothing singled out, with a closing caption.</summary>
    public void Finish(string say)
    {
        var before = _shown;
        Reveal(int.MaxValue);
        Highlight([], before);
        _counter.Text = "";
        _caption.Text = say;
        _captionPlate.Visibility = Visibility.Visible;
    }

    void Reveal(int upTo)
    {
        var delay = 0.0;
        foreach (var (el, m, line) in _items)
        {
            if (m.Order > upTo || m.Order <= _shown) continue;
            Appear(el, line, TimeSpan.FromMilliseconds(delay));
            delay = Math.Min(delay + 35, 900);
        }
        _shown = Math.Max(_shown, Math.Min(upTo, _scene.Steps));
    }

    static void Appear(FrameworkElement el, bool line, TimeSpan delay)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(line ? 120 : 320)) { BeginTime = delay };
        el.BeginAnimation(OpacityProperty, fade);
        if (line && el is Polyline pl)
        {
            // Sketched in along its length.
            var length = 0.0;
            for (var k = 1; k < pl.Points.Count; k++) length += (pl.Points[k] - pl.Points[k - 1]).Length;
            var units = Math.Max(1, length / Math.Max(0.5, pl.StrokeThickness));
            pl.StrokeDashArray = [units, units];
            pl.StrokeDashOffset = units;
            var draw = new DoubleAnimation(units, 0, TimeSpan.FromMilliseconds(Math.Clamp(length * 1.6, 250, 900)))
            {
                BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            draw.Completed += (_, _) => { pl.BeginAnimation(Shape.StrokeDashOffsetProperty, null); pl.StrokeDashArray = null; };
            pl.BeginAnimation(Shape.StrokeDashOffsetProperty, draw);
        }
        else
        {
            var pop = new ScaleTransform(0.94, 0.94);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = pop;
            var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>
    /// The parts being talked about glow at full strength; what was drawn before steps back.
    /// Parts drawn in just now (after `before`) are left to finish appearing.
    /// </summary>
    void Highlight(string[] focus, int before)
    {
        var set = new HashSet<string>(focus, StringComparer.OrdinalIgnoreCase);
        foreach (var (el, m, _) in _items)
        {
            if (m.Order > _shown) continue;
            var on = set.Count == 0 || m.Node is null || set.Contains(m.Node);
            if (m.Order > before && on) { el.Effect = set.Count > 0 && m.Node is not null && m.Kind is MarkKind.Box or MarkKind.Ellipse or MarkKind.Polygon ? Glow() : null; continue; }
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1.0 : 0.32, TimeSpan.FromMilliseconds(300)));
            el.Effect = set.Count > 0 && m.Node is not null && set.Contains(m.Node) && m.Kind is MarkKind.Box or MarkKind.Ellipse or MarkKind.Polygon
                ? Glow()
                : null;
        }
    }

    DropShadowEffect Glow() => new() { Color = _accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.75 };

    public void Dismiss()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        Dismissed?.Invoke();
        Dismissed = null;
    }

    // ------------------------------------------------------------------ colour helpers

    static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0) { var v = (byte)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Ch(double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var hh = h / 360;
        return Color.FromRgb((byte)Math.Round(Ch(hh + 1 / 3.0) * 255), (byte)Math.Round(Ch(hh) * 255), (byte)Math.Round(Ch(hh - 1 / 3.0) * 255));
    }
}
