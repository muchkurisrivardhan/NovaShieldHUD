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
/// Kai's sketchboard: a panel in the corner of the screen (Settings > Drawing says where and
/// how big) dressed exactly like the launcher in the chosen style - cut corners, scanlines and
/// a "KAI // EXPLAINS" header for the framed looks, soft rounded plates for Classic and Mono.
/// It draws a picture (a mind map, timeline, cutaway, 3D illustration...) as Kai explains,
/// step by step: each step's parts draw themselves in, the parts being talked about glow while
/// the rest dim, and what Kai says runs as a caption.
///
/// Drag it by the header, resize it from the grip: the picture is laid out again to fit. Esc,
/// right-click or the × closes it; a click moves on to the next step.
///
/// Over the screen (pointing things out), it covers the whole monitor, see-through and
/// click-through, with only the callouts and a caption.
/// </summary>
public sealed class ExplainBoard : Window
{
    readonly Settings _settings;
    readonly StyleLook _look;
    readonly Visual _visual;
    readonly Size? _image;
    Scene _scene;
    readonly List<VStep> _steps;
    readonly Canvas _canvas = new();
    readonly Border _host = new() { ClipToBounds = true };
    readonly TextBlock _caption = new();
    readonly TextBlock _counter = new();
    readonly FrameworkElement _captionPlate;
    readonly List<(FrameworkElement Element, Mark Mark, bool Line)> _items = [];
    readonly Color _accent, _page, _surface, _text, _muted;
    readonly Color[] _family;
    readonly double _corner, _speed;
    readonly bool _sketchy;
    readonly FontFamily _font;
    int _shown = -1;
    string[] _focus = [];
    Size _laidOut;
    readonly System.Windows.Threading.DispatcherTimer _relayout;

    /// <summary>The board was closed (Esc, right-click, ×).</summary>
    public event Action? Dismissed;

    /// <summary>Clicked: move on to the next step now.</summary>
    public event Action? Skipped;

    readonly bool _overScreen;

    /// <param name="screen">The monitor the pointer is on.</param>
    /// <param name="work">That monitor's work area (without the taskbar), where the panel goes.</param>
    /// <param name="image">For pointing at things on the screen: the screenshot's size, which callouts are measured in.</param>
    public ExplainBoard(Settings settings, Visual visual, Rect screen, Size? image = null, Rect? work = null)
    {
        _overScreen = visual.Kind == VisualKind.Screen;
        _settings = settings;
        _visual = visual;
        _image = image;
        _look = StyleLook.For(settings);
        _accent = _look.Accent;
        _page = _look.Page;
        _surface = _look.Surface;
        _text = _look.Text;
        _muted = _look.Muted;
        _family = Family(_accent);
        _corner = Math.Min(12, _look.Radius * 1.2);
        _font = _look.Font;
        _speed = settings.DrawSpeed switch { "slow" => 1.6, "fast" => 0.55, _ => 1.0 };
        _sketchy = settings.DrawStyle == "sketch";

        Title = "Kai explains: " + visual.Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        FontFamily = _font;

        var area = _overScreen ? screen : Place(settings, work ?? screen);
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
        ResizeMode = _overScreen || settings.DrawPlace == "full" ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        MinWidth = 380;
        MinHeight = 300;

        _steps = visual.Steps;
        _captionPlate = BuildCaption();
        if (_overScreen)
        {
            _scene = Sketch.Layout(visual, screen.Width, screen.Height, image?.Width ?? 0, image?.Height ?? 0);
            Content = BuildOverScreen(new Grid(), visual.Title);
            foreach (var m in _scene.Marks) Add(m);
        }
        else
        {
            _scene = Sketch.Layout(visual, 1200, 700, framed: true);   // replaced as soon as the panel has its size
            Content = BuildPanel(visual);
            _host.SizeChanged += (_, e) =>
            {
                if (_laidOut == default) Lay(e.NewSize);
                else { _relayout!.Stop(); _relayout.Start(); }
            };
        }
        _relayout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _relayout.Tick += (_, _) => { _relayout.Stop(); Lay(new Size(_host.ActualWidth, _host.ActualHeight)); };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dismiss();
            else if (e.Key is Key.Right or Key.Space or Key.Enter) Skipped?.Invoke();
        };
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

    /// <summary>Where the panel goes in the work area, from Settings > Drawing.</summary>
    public static Rect Place(Settings s, Rect work)
    {
        const double gap = 14;
        var w = Math.Max(380, work.Width * Math.Clamp(s.DrawWidth, 0.3, 1) - gap * 1.5);
        var h = Math.Max(300, work.Height * Math.Clamp(s.DrawHeight, 0.3, 1) - gap * 1.5);
        var half = Math.Max(380, work.Width / 2 - gap * 1.5);
        return s.DrawPlace switch
        {
            "full" => work,
            "left" => new Rect(work.Left + gap, work.Top + gap, half, work.Height - gap * 2),
            "right" => new Rect(work.Right - gap - half, work.Top + gap, half, work.Height - gap * 2),
            "bottom-right" => new Rect(work.Right - gap - w, work.Bottom - gap - h, w, h),
            _ => new Rect(work.Left + gap, work.Bottom - gap - h, w, h),
        };
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

    // ------------------------------------------------------------------ the panel

    UIElement BuildPanel(Visual visual)
    {
        var root = new Grid { Margin = new Thickness(6) };
        root.Effect = new DropShadowEffect { Color = _look.Chamfer ? _accent : Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = _look.Chamfer ? 0.35 : 0.55 };
        var alpha = (byte)Math.Round(255 * Math.Clamp(_settings.DrawOpacity, 0.6, 1));
        root.Children.Add(_look.Plate(_page, alpha, _look.Chamfer ? _accent : _look.Border, 1, 16));
        if (_look.Scanlines)
            root.Children.Add(new Rectangle { Fill = _look.ScanlineBrush(), Margin = new Thickness(2, 16, 2, 16), IsHitTestVisible = false });

        var dock = new DockPanel { Margin = new Thickness(18, 10, 18, 12) };
        root.Children.Add(dock);

        // The header: the style's tag line, the kind, and the close button. Drag here to move.
        var header = new Grid { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tag = _look.Header
            ? new TextBlock { Text = _look.HeaderText("explains"), FontFamily = _look.Tag, FontSize = 12, Foreground = _look.Brush(_accent), VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { Text = "Kai explains", FontSize = 12.5, Foreground = _look.Brush(_muted), VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(tag);
        var kind = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        kind.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = _look.Brush(_accent), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var kindName = KindLabel(visual.Kind);
        kind.Children.Add(new TextBlock { Text = _look.Header ? kindName.ToUpperInvariant() : kindName, FontFamily = _look.Header ? _look.Tag : _font, FontSize = 11.5, Foreground = _look.Brush(_look.Secondary) });
        Grid.SetColumn(kind, 1);
        header.Children.Add(kind);
        var close = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(_look.Chamfer ? 0 : 8), Cursor = Cursors.Hand,
            Background = _look.Brush(_surface), BorderBrush = _look.Brush(_accent, 0x90), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "✕", FontSize = 12, Foreground = _look.Brush(_text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "Close (Esc)",
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_settings.DrawPlace != "full") try { DragMove(); } catch (InvalidOperationException) { }
        };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        if (_look.Header)
        {
            var rule = new Rectangle { Height = 1, Fill = _look.Brush(_accent, 0x60), Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(rule, Dock.Top);
            dock.Children.Add(rule);
        }

        // The title, underlined in the accent.
        var head = new StackPanel { Margin = new Thickness(4, 2, 0, 4) };
        head.Children.Add(new TextBlock { Text = visual.Title, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = _look.Brush(_text), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(new Rectangle { Height = 3, Width = Math.Min(560, Sketch.TextWidth(visual.Title, 22) + 10), Fill = _look.Brush(_accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        // The caption and the hint, at the bottom.
        var foot = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foot.Children.Add(_captionPlate);
        foot.Children.Add(new TextBlock
        {
            Text = "Click: next  ·  drag the header to move  ·  Esc: close", FontSize = 11, Foreground = _look.Brush(_muted),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
        });
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        // The drawing area, with a faint drafting grid.
        if (_settings.DrawGrid)
            _host.Background = new DrawingBrush
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 28, 28), ViewportUnits = BrushMappingMode.Absolute,
                Drawing = new GeometryDrawing(null, new Pen(_look.Brush(_accent, 0x18), 1),
                    new GeometryGroup { Children = { new LineGeometry(new Point(0, 0), new Point(28, 0)), new LineGeometry(new Point(0, 0), new Point(0, 28)) } }),
            };
        else _host.Background = Brushes.Transparent;
        _canvas.HorizontalAlignment = HorizontalAlignment.Center;
        _canvas.VerticalAlignment = VerticalAlignment.Center;
        _host.Child = _canvas;
        _host.MouseLeftButtonUp += (_, _) => Skipped?.Invoke();
        _host.MouseRightButtonUp += (_, _) => Dismiss();
        dock.Children.Add(_host);
        return root;
    }

    static string KindLabel(VisualKind k) => k switch
    {
        VisualKind.MindMap => "Mind map",
        VisualKind.ConceptMap => "Concept map",
        VisualKind.Timeline => "Timeline",
        VisualKind.Flow => "Process",
        VisualKind.Cycle => "Cycle",
        VisualKind.Hierarchy => "Hierarchy",
        VisualKind.Compare => "Comparison",
        VisualKind.Venn => "Venn diagram",
        VisualKind.Cutaway => "Cutaway",
        VisualKind.Iso3D => "3D illustration",
        _ => "On screen",
    };

    /// <summary>
    /// Lays the picture out for the drawing area: at a comfortable size of its own (never
    /// smaller than the layouts are made for), scaled to fit. Whatever was already shown is
    /// shown again at once.
    /// </summary>
    void Lay(Size size)
    {
        if (size.Width < 40 || size.Height < 40) return;
        if (_laidOut != default && Math.Abs(size.Width - _laidOut.Width) < 2 && Math.Abs(size.Height - _laidOut.Height) < 2) return;
        _laidOut = size;
        var aspect = size.Width / size.Height;
        var w = Math.Max(size.Width, 1000);
        var h = w / aspect;
        if (h < 620) { h = 620; w = h * aspect; }
        _scene = Sketch.Layout(_visual, w, h, framed: true);
        _canvas.Children.Clear();
        _items.Clear();
        _canvas.Width = w;
        _canvas.Height = h;
        var k = size.Width / w;
        _canvas.LayoutTransform = new ScaleTransform(k, k);
        foreach (var m in _scene.Marks) Add(m);
        if (_shown < 0) return;
        foreach (var (el, m, _) in _items)
            if (m.Order <= _shown) el.Opacity = 1;
        Highlight(_focus, _shown);
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
        line.Inlines.Add(new System.Windows.Documents.Run(_look.Header ? _look.HeaderText("looking") + "   " : "Kai is looking: ") { Foreground = new SolidColorBrush(_accent), FontFamily = _look.Header ? _look.Tag : _font });
        line.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run("   ·   say “got it” to close") { Foreground = new SolidColorBrush(_muted), FontSize = 12.5 });
        tag.Child = line;
        root.Children.Add(tag);
        _captionPlate.HorizontalAlignment = HorizontalAlignment.Center;
        _captionPlate.VerticalAlignment = VerticalAlignment.Bottom;
        _captionPlate.Margin = new Thickness(0, 0, 0, 22);
        _captionPlate.MaxWidth = Math.Min(980, Width - 200);
        root.Children.Add(_captionPlate);
        return root;
    }

    /// <summary>The caption: what Kai is saying, in a plate of the style's surface colour.</summary>
    FrameworkElement BuildCaption()
    {
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.FontSize = _overScreen ? 19 : 15.5;
        _caption.Foreground = new SolidColorBrush(_text);
        _caption.TextAlignment = TextAlignment.Center;
        _counter.FontSize = 11.5;
        _counter.FontFamily = _look.Header ? _look.Tag : _font;
        _counter.Foreground = new SolidColorBrush(_accent);
        _counter.HorizontalAlignment = HorizontalAlignment.Center;
        _counter.Margin = new Thickness(0, 0, 0, 3);
        var stack = new StackPanel { Margin = new Thickness(18, 7, 18, 10) };
        stack.Children.Add(_counter);
        stack.Children.Add(_caption);
        var plate = new Grid { Visibility = Visibility.Collapsed };
        plate.Children.Add(_look.Plate(_surface, 0xF2, _look.Chamfer ? _accent : _look.Border, 1, 8));
        plate.Children.Add(stack);
        return plate;
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
                    Points = Points(m.Pts, false),
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
                    Points = Points(m.Pts, true),
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
                if (_sketchy)
                {
                    // Hand-drawn: a second, slightly askew pass of the pen round the card.
                    var again = new Rectangle
                    {
                        Width = m.Pts[1].X, Height = m.Pts[1].Y, RadiusX = _corner + 2, RadiusY = _corner + 2,
                        Stroke = new SolidColorBrush(Color.FromArgb(0x90, ink.R, ink.G, ink.B)), StrokeThickness = Math.Max(1, m.Stroke * 0.6),
                        RenderTransform = new RotateTransform(Noise(m.Pts[0].X + m.Pts[0].Y, 0) * 0.9, m.Pts[1].X / 2, m.Pts[1].Y / 2),
                    };
                    Canvas.SetLeft(again, m.Pts[0].X + 1.5);
                    Canvas.SetTop(again, m.Pts[0].Y - 1);
                    Place(again, m, false);
                }
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

    /// <summary>A mark's points; in the sketch style, with a hand's small wobble along each stroke.</summary>
    PointCollection Points(P[] pts, bool closed)
    {
        if (!_sketchy || pts.Length < 2) return new PointCollection(pts.Select(p => new Point(p.X, p.Y)));
        var list = new PointCollection();
        var n = closed ? pts.Length : pts.Length - 1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            var d = b - a;
            var len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            var parts = Math.Max(1, (int)(len / 36));
            var normal = len > 0 ? new P(-d.Y / len, d.X / len) : new P(0, 0);
            for (var k = 0; k < parts; k++)
            {
                var t = k / (double)parts;
                var wobble = k == 0 ? 0 : Noise(a.X * 0.37 + a.Y * 0.71, k) * 1.7;
                var p = a + d * t + normal * wobble;
                list.Add(new Point(p.X, p.Y));
            }
        }
        if (!closed) list.Add(new Point(pts[^1].X, pts[^1].Y));
        return list;
    }

    /// <summary>The same small wiggle every time for the same stroke, so a re-layout doesn't shimmer.</summary>
    static double Noise(double seed, int k)
    {
        var x = Math.Sin(seed * 12.9898 + k * 78.233) * 43758.5453;
        return (x - Math.Floor(x)) * 2 - 1;
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
        _focus = _steps[i].Focus;
        Highlight(_focus, before);
        _counter.Text = $"{i + 1} / {_steps.Count}";
        _caption.Text = _steps[i].Say;
        ShowCaption();
    }

    /// <summary>The whole picture, nothing singled out, with a closing caption.</summary>
    public void Finish(string say)
    {
        var before = _shown;
        Reveal(int.MaxValue);
        _focus = [];
        Highlight([], before);
        _counter.Text = "";
        _caption.Text = say;
        ShowCaption();
    }

    /// <summary>Captions can be turned off in Settings > Drawing; over the screen they always show.</summary>
    void ShowCaption() => _captionPlate.Visibility = _overScreen || _settings.DrawCaptions ? Visibility.Visible : Visibility.Collapsed;

    void Reveal(int upTo)
    {
        var delay = 0.0;
        foreach (var (el, m, line) in _items)
        {
            if (m.Order > upTo || m.Order <= _shown) continue;
            Appear(el, line, TimeSpan.FromMilliseconds(delay), _speed);
            delay = Math.Min(delay + 35 * _speed, 900 * _speed);
        }
        _shown = Math.Max(_shown, Math.Min(upTo, _scene.Steps));
    }

    static void Appear(FrameworkElement el, bool line, TimeSpan delay, double speed)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds((line ? 120 : 320) * speed)) { BeginTime = delay };
        el.BeginAnimation(OpacityProperty, fade);
        if (line && el is Polyline pl)
        {
            // Sketched in along its length.
            var length = 0.0;
            for (var k = 1; k < pl.Points.Count; k++) length += (pl.Points[k] - pl.Points[k - 1]).Length;
            var units = Math.Max(1, length / Math.Max(0.5, pl.StrokeThickness));
            pl.StrokeDashArray = [units, units];
            pl.StrokeDashOffset = units;
            var draw = new DoubleAnimation(units, 0, TimeSpan.FromMilliseconds(Math.Clamp(length * 1.6, 250, 900) * speed))
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
            var grow = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(320 * speed)) { BeginTime = delay, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
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
