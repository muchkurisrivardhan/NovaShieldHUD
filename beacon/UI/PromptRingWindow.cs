using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Beacon.Core;
using Beacon.LocalAi;

namespace Beacon.UI;

/// <summary>
/// The prompt ring: opens at the cursor over the selected text, one slice per prompt style.
/// Hover and click, or press 1-8; Esc or a click elsewhere cancels. After a pick it stays up
/// with a spinner while the prompt is rewritten, then closes.
///
/// Drawn in code in the current Beacon style: the same colours, font and corners Filee's
/// ring gets, so the two rings look like one family.
/// </summary>
public sealed class PromptRingWindow : Window
{
    const double Size = 400, Outer = 178, Inner = 74;

    readonly Canvas _canvas = new() { Width = Size, Height = Size };
    readonly TaskCompletionSource<PromptStyle?> _choice = new();
    readonly List<(Path Slice, PromptStyle Style)> _slices = [];
    readonly TextBlock _centerTitle = new(), _centerSub = new();
    readonly Ellipse _hole = new();
    Path? _spinner;
    bool _working;

    readonly Color _accent, _page, _surface, _border, _text, _secondary;
    readonly FontFamily _font;
    readonly bool _glow;

    readonly PromptStyle[] _styles;
    readonly string _centerDefault;
    readonly string _workingVerb;

    /// <param name="styles">The slices of the ring; defaults to the prompt styles.</param>
    /// <param name="centerTitle">The idle label in the hole; "Shape prompt" for the prompt ring.</param>
    /// <param name="workingVerb">What the spinner says it is doing ("rewriting", "working").</param>
    public PromptRingWindow(Settings settings, string selection, bool hasModel,
        PromptStyle[]? styles = null, string centerTitle = "Shape prompt", string workingVerb = "rewriting")
    {
        _styles = styles ?? PromptStyles.All;
        _centerDefault = centerTitle;
        _workingVerb = workingVerb;
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");
        _font = new FontFamily(p.Font + ", Segoe UI");
        _glow = settings.OverlayStyle is "hud" or "cyberpunk" or "crt";

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = Size;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;   // placed at the cursor once its DPI is known
        Content = _canvas;
        FontFamily = _font;

        Build(selection, hasModel);

        Loaded += (_, _) => PlaceAtCursor();
        Deactivated += (_, _) => { if (!_working) Finish(null); };
        KeyDown += OnKey;
    }

    /// <summary>The style picked, or null when cancelled.</summary>
    public Task<PromptStyle?> Choice => _choice.Task;

    /// <summary>After a pick: dims the other slices and spins while the rewrite runs.</summary>
    public void ShowWorking(PromptStyle style, bool withModel)
    {
        _working = true;
        foreach (var (slice, s) in _slices) slice.Opacity = s == style ? 1 : 0.35;
        _centerTitle.Text = style.Label;
        _centerSub.Text = withModel ? _workingVerb + " on this PC…" : "building the guide…";
        _spinner = new Path
        {
            Data = Arc(Size / 2, Size / 2, Inner - 9, -90, 30),
            Stroke = new SolidColorBrush(_accent), StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = new RotateTransform(0, Size / 2, Size / 2),
        };
        _canvas.Children.Add(_spinner);
        _spinner.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    void Build(string selection, bool hasModel)
    {
        const double cx = Size / 2, cy = Size / 2;

        if (_glow)
            _canvas.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.45 };

        // A soft disc behind the slices so the ring reads over any window.
        var back = new Ellipse
        {
            Width = Outer * 2 + 16, Height = Outer * 2 + 16,
            Fill = new RadialGradientBrush(WithAlpha(_page, 0xE6), WithAlpha(_page, 0x00)) { GradientOrigin = new Point(0.5, 0.5) },
        };
        Canvas.SetLeft(back, cx - back.Width / 2); Canvas.SetTop(back, cy - back.Height / 2);
        _canvas.Children.Add(back);

        var styles = _styles;
        var step = 360.0 / styles.Length;
        for (var i = 0; i < styles.Length; i++)
        {
            var style = styles[i];
            var a0 = -90 - step / 2 + i * step + 1.4;
            var a1 = a0 + step - 2.8;
            var slice = new Path
            {
                Data = Slice(cx, cy, Inner, Outer, a0, a1),
                Fill = new SolidColorBrush(WithAlpha(_surface, 0xF2)),
                Stroke = new SolidColorBrush(WithAlpha(_border, 0xD0)),
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
                ToolTip = style.Hint,
            };
            slice.MouseEnter += (_, _) => Highlight(slice, true);
            slice.MouseLeave += (_, _) => Highlight(slice, false);
            slice.MouseLeftButtonUp += (_, _) => Finish(style);
            _canvas.Children.Add(slice);
            _slices.Add((slice, style));

            var mid = (a0 + a1) / 2 * Math.PI / 180;
            var r = (Inner + Outer) / 2 + 4;
            var label = new StackPanel { IsHitTestVisible = false };
            label.Children.Add(new TextBlock
            {
                Text = style.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18,
                Foreground = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Center,
            });
            label.Children.Add(new TextBlock
            {
                Text = style.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_text), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
            });
            label.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(), FontSize = 9.5,
                Foreground = new SolidColorBrush(_secondary), HorizontalAlignment = HorizontalAlignment.Center,
            });
            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, cx + r * Math.Cos(mid) - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, cy + r * Math.Sin(mid) - label.DesiredSize.Height / 2);
            _canvas.Children.Add(label);
        }

        _hole.Width = _hole.Height = Inner * 2 - 10;
        _hole.Fill = new SolidColorBrush(WithAlpha(_page, 0xF5));
        _hole.Stroke = new SolidColorBrush(WithAlpha(_accent, 0x99));
        _hole.StrokeThickness = 1.2;
        Canvas.SetLeft(_hole, cx - _hole.Width / 2); Canvas.SetTop(_hole, cy - _hole.Height / 2);
        _canvas.Children.Add(_hole);

        var words = selection.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var center = new StackPanel { Width = Inner * 2 - 22, IsHitTestVisible = false };
        _centerTitle.Text = _centerDefault;
        _centerTitle.FontSize = 13; _centerTitle.FontWeight = FontWeights.SemiBold;
        _centerTitle.Foreground = new SolidColorBrush(_text);
        _centerTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _centerSub.Text = $"{words} words" + (hasModel ? "" : "\nno AI model: guide only");
        _centerSub.FontSize = 10; _centerSub.TextAlignment = TextAlignment.Center;
        _centerSub.TextWrapping = TextWrapping.Wrap;
        _centerSub.Foreground = new SolidColorBrush(_secondary);
        _centerSub.HorizontalAlignment = HorizontalAlignment.Center;
        center.Children.Add(_centerTitle);
        center.Children.Add(_centerSub);
        center.Measure(new System.Windows.Size(center.Width, double.PositiveInfinity));
        Canvas.SetLeft(center, cx - center.Width / 2); Canvas.SetTop(center, cy - center.DesiredSize.Height / 2);
        _canvas.Children.Add(center);
    }

    void Highlight(Path slice, bool on)
    {
        if (_working) return;
        slice.Fill = new SolidColorBrush(on ? WithAlpha(Mix(_surface, _accent, 0.35), 0xF5) : WithAlpha(_surface, 0xF2));
        slice.Stroke = new SolidColorBrush(on ? _accent : WithAlpha(_border, 0xD0));
        var hint = _slices.First(s => s.Slice == slice).Style;
        _centerTitle.Text = on ? hint.Label : _centerDefault;
        _centerSub.Text = on ? hint.Hint : _centerSub.Text;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (_working) return;
        if (e.Key == Key.Escape) { Finish(null); return; }
        var n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (n >= 0 && n < _slices.Count) Finish(_slices[n].Style);
    }

    void Finish(PromptStyle? style)
    {
        if (_choice.Task.IsCompleted) return;
        _choice.TrySetResult(style);
        if (style is null) Close();
    }

    void PlaceAtCursor()
    {
        var px = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(px).WorkingArea;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        var at = toDip.Transform(new Point(px.X, px.Y));
        var tl = toDip.Transform(new Point(screen.Left, screen.Top));
        var br = toDip.Transform(new Point(screen.Right, screen.Bottom));
        Left = Math.Clamp(at.X - Size / 2, tl.X, br.X - Size);
        Top = Math.Clamp(at.Y - Size / 2, tl.Y, br.Y - Size);
        Activate();
        Focus();
    }

    static Geometry Slice(double cx, double cy, double r0, double r1, double a0, double a1)
    {
        Point P(double r, double a) => new(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var large = a1 - a0 > 180;
        var f = new PathFigure { StartPoint = P(r0, a0), IsClosed = true };
        f.Segments.Add(new LineSegment(P(r1, a0), true));
        f.Segments.Add(new ArcSegment(P(r1, a1), new System.Windows.Size(r1, r1), 0, large, SweepDirection.Clockwise, true));
        f.Segments.Add(new LineSegment(P(r0, a1), true));
        f.Segments.Add(new ArcSegment(P(r0, a0), new System.Windows.Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true));
        return new PathGeometry([f]);
    }

    static Geometry Arc(double cx, double cy, double r, double a0, double a1)
    {
        Point P(double a) => new(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var f = new PathFigure { StartPoint = P(a0) };
        f.Segments.Add(new ArcSegment(P(a1), new System.Windows.Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([f]);
    }

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
