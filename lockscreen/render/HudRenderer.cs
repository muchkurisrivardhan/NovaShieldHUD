using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Vantage.Lockscreen;

/// <summary>
/// Draws the lock screen HUD as layers, in this order: background, grid, scanlines and
/// vignette, the ring assembly (with a blurred copy underneath as its glow), then the corner
/// brackets and text. Sizes in "u" are pixels at 1080p and grow with the screen, so a 4K
/// screen gets the same picture, not a tiny one. The middle of the ring stays empty: the
/// account picture sits there on the sign-in screen.
/// </summary>
public static class HudRenderer
{
    /// <summary>Areas Windows draws over: the clock (top centre) and sign-in box and widgets (bottom centre).</summary>
    public static (Rect Clock, Rect SignIn) Zones(int w, int h) =>
        (new Rect(w * 0.35, h * 0.06, w * 0.30, h * 0.14), new Rect(w * 0.30, h * 0.72, w * 0.40, h * 0.25));

    public static BitmapSource Render(int w, int h, Config cfg, Theme t, DateTime now, TimeSpan uptime, bool debugZones = false)
    {
        var u = Math.Min(w, h) / 1080.0;
        var root = new ContainerVisual();
        void Layer(Action<DrawingContext> draw, Effect? effect = null)
        {
            var v = new DrawingVisual { Effect = effect };
            using (var dc = v.RenderOpen()) draw(dc);
            root.Children.Add(v);
        }

        Layer(dc => Background(dc, w, h, cfg, t, u));
        Layer(dc => CoreFill(dc, w, h, cfg, t));

        var rings = new DrawingGroup();
        using (var dc = rings.Open()) Rings(dc, w, h, cfg, t, u);
        rings.Freeze();
        if (cfg.Effects.Glow && t.Glow > 0)
            Layer(dc => dc.DrawDrawing(rings), new BlurEffect { Radius = 6 * u * t.Glow, RenderingBias = RenderingBias.Quality });
        Layer(dc => dc.DrawDrawing(rings));

        Layer(dc => Corners(dc, w, h, cfg, t, u, now, uptime));
        if (debugZones) Layer(dc => DebugZones(dc, w, h, t, u));

        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        enc.Save(f);
    }

    // ---- layers 1-3 --------------------------------------------------------------------

    static void Background(DrawingContext dc, int w, int h, Config cfg, Theme t, double u)
    {
        var full = new Rect(0, 0, w, h);
        dc.DrawRectangle(new SolidColorBrush(t.Background), null, full);

        // A subtle lift in the middle, so the charcoal isn't flat.
        var lift = Themes.Lighten(t.Background, 0.07);
        dc.DrawRectangle(new RadialGradientBrush(lift, Color.FromArgb(0, lift.R, lift.G, lift.B)) { RadiusX = 0.6, RadiusY = 0.6 }, null, full);

        if (cfg.Effects.Grid)
        {
            // Lines at 6%, fading out toward the edges.
            dc.PushOpacityMask(new RadialGradientBrush(Colors.White, Colors.Transparent) { RadiusX = 0.62, RadiusY = 0.62 });
            var pen = new Pen(new SolidColorBrush(t.A(0.06)), Math.Max(1, Math.Round(u)));
            var step = 48 * u;
            // Start from the centre so the grid is symmetric on every screen shape.
            for (var x = w / 2.0 % step; x < w; x += step) dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
            for (var y = h / 2.0 % step; y < h; y += step) dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            dc.DrawRectangle(Brushes.Transparent, null, full);   // the mask is sized by what's drawn: cover the screen
            dc.Pop();
        }

        if (cfg.Effects.Scanlines)
        {
            var line = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0));   // 25% black
            for (var y = 0; y < h; y += 3) dc.DrawRectangle(line, null, new Rect(0, y, w, 1));
        }

        if (cfg.Effects.Vignette)
        {
            var v = new RadialGradientBrush { RadiusX = 0.72, RadiusY = 0.72 };
            v.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.45));
            v.GradientStops.Add(new GradientStop(Color.FromArgb(191, 0, 0, 0), 1));   // 75% black at the corners
            dc.DrawRectangle(v, null, full);
        }
    }

    // ---- layer 4: rings ----------------------------------------------------------------

    /// <summary>Ring radius: 22% of the screen height, held inside narrow (portrait) screens.</summary>
    public static double RingRadius(int w, int h, double scale) => Math.Min(0.22 * h, 0.42 * w) * scale;

    static void Rings(DrawingContext dc, int w, int h, Config cfg, Theme t, double u)
    {
        var c = new Point(w * cfg.RingX, h * cfg.RingY);
        var R = RingRadius(w, h, cfg.RingScale);
        var k = R / 237.6;   // 1 at 1080p: lengths "scaled to R" use this
        Pen P(double thickness, double alpha) => new(new SolidColorBrush(t.A(alpha)), thickness);
        Point At(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));

        // Outer dotted ring: 2 px dots, 7 px gaps (a zero-length dash with round caps is a dot).
        var dots = P(2 * k, 0.6);
        dots.DashStyle = new DashStyle([0, 4.5], 0);
        dots.DashCap = PenLineCap.Round;
        dc.DrawEllipse(null, dots, c, R, R);

        dc.DrawEllipse(null, P(Math.Max(1, k), 0.75), c, 0.92 * R, 0.92 * R);

        // Thick dashed ring: 6 px stroke, dash 70 / gap 34. Dash lengths are in stroke widths.
        var thick = P(6 * k, 0.9);
        thick.DashStyle = new DashStyle([70.0 / 6, 34.0 / 6], 0);
        dc.DrawEllipse(null, thick, c, 0.82 * R, 0.82 * R);

        // 72 ticks; every sixth is longer and thicker.
        Pen minor = P(Math.Max(1, 1.2 * k), t.Dim), major = P(2.6 * k, 0.9);
        for (var i = 0; i < 72; i++)
        {
            var big = i % 6 == 0;
            dc.DrawLine(big ? major : minor, At((big ? 0.67 : 0.695) * R, i * 5), At(0.73 * R, i * 5));
        }

        dc.DrawEllipse(null, P(Math.Max(1, k), 0.5), c, 0.61 * R, 0.61 * R);

        // Two opposing quarter arcs, left and right.
        var arcPen = P(2 * k, 0.85);
        foreach (var mid in new[] { 0, 180 })
        {
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(At(0.48 * R, mid - 45), false, false);
                s.ArcTo(At(0.48 * R, mid + 45), new Size(0.48 * R, 0.48 * R), 0, false, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, arcPen, g);
        }

        // The core's outline. Its faint fill is drawn by CoreFill, and nothing else goes inside:
        // the account picture sits here.
        dc.DrawEllipse(null, P(Math.Max(1, k), 0.6), c, 0.33 * R, 0.33 * R);
    }

    /// <summary>The core's 6% fill, kept out of the ring drawing so the glow copy doesn't double it.</summary>
    static void CoreFill(DrawingContext dc, int w, int h, Config cfg, Theme t)
    {
        var r = 0.33 * RingRadius(w, h, cfg.RingScale);
        dc.DrawEllipse(new SolidColorBrush(t.A(0.06)), null, new Point(w * cfg.RingX, h * cfg.RingY), r, r);
    }

    // ---- layer 5: corners --------------------------------------------------------------

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    const double Spacing = 0.18;   // letter spacing, in ems

    static void Corners(DrawingContext dc, int w, int h, Config cfg, Theme t, double u, DateTime now, TimeSpan uptime)
    {
        double m = 44 * u, arm = 18 * u, gap = 10 * u, titleSize = 16 * u, subSize = 12 * u;
        var pen = new Pen(new SolidColorBrush(t.A(0.9)), Math.Max(1, Math.Round(u)));
        Brush title = new SolidColorBrush(t.A(1)), sub = new SolidColorBrush(t.A(t.Dim));
        var bold = new Typeface(Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var normal = new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Text may run from its corner up to the reserved clock / sign-in column, never into it.
        var textX = m + arm * 0.5 + gap;
        var maxWidth = Zones(w, h).SignIn.Left - textX - 12 * u;

        for (var i = 0; i < 4; i++)
        {
            bool right = i % 2 == 1, bottom = i >= 2;
            // Snapped to pixel centres, so the 1 px lines are one crisp pixel, not two blurred ones.
            double cx = Math.Floor(right ? w - m : m) + 0.5, cy = Math.Floor(bottom ? h - m : m) + 0.5;
            double sx = right ? -1 : 1, sy = bottom ? -1 : 1;
            dc.DrawLine(pen, new Point(cx, cy), new Point(cx + sx * arm, cy));
            dc.DrawLine(pen, new Point(cx, cy), new Point(cx, cy + sy * arm));

            var line1 = Fit(Tokens.Expand(cfg.Corners[i][0], now, uptime), bold, titleSize, maxWidth);
            var line2 = Fit(Tokens.Expand(cfg.Corners[i][1], now, uptime), normal, subSize, maxWidth);
            // Two lines stacked inside the bracket's corner: title nearer the top on every corner.
            var blockH = titleSize * 1.25 + subSize * 1.25;
            var top = bottom ? cy - 6 * u - blockH : cy + 6 * u;
            Text(dc, line1, bold, titleSize, title, right ? w - textX : textX, top, right);
            Text(dc, line2, normal, subSize, sub, right ? w - textX : textX, top + titleSize * 1.25, right);
        }
    }

    static FormattedText Glyph(char ch, Typeface tf, double size, Brush b) =>
        new(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, 1.0);

    public static double Width(string s, Typeface tf, double size) =>
        s.Length == 0 ? 0 : s.Sum(ch => Glyph(ch, tf, size, Brushes.Black).WidthIncludingTrailingWhitespace) + (s.Length - 1) * Spacing * size;

    /// <summary>Shortens text that wouldn't fit, ending it with an ellipsis, so nothing is clipped.</summary>
    static string Fit(string s, Typeface tf, double size, double maxWidth)
    {
        if (Width(s, tf, size) <= maxWidth) return s;
        while (s.Length > 1 && Width(s + "…", tf, size) > maxWidth) s = s[..^1];
        return s.TrimEnd() + "…";
    }

    /// <summary>Draws letter-spaced text; <paramref name="x"/> is its left edge, or its right edge when right-aligned.</summary>
    static void Text(DrawingContext dc, string s, Typeface tf, double size, Brush b, double x, double y, bool rightAligned)
    {
        if (rightAligned) x -= Width(s, tf, size);
        foreach (var ch in s)
        {
            var g = Glyph(ch, tf, size, b);
            dc.DrawText(g, new Point(x, y));
            x += g.WidthIncludingTrailingWhitespace + Spacing * size;
        }
    }

    // ---- layer 6: reserved zones (preview only) ----------------------------------------

    static void DebugZones(DrawingContext dc, int w, int h, Theme t, double u)
    {
        var pen = new Pen(new SolidColorBrush(t.A(0.8)), Math.Max(1, u)) { DashStyle = new DashStyle([6, 4], 0) };
        var tf = new Typeface(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var (clock, signIn) = Zones(w, h);
        foreach (var (zone, label) in new[] { (clock, "RESERVED: WINDOWS CLOCK"), (signIn, "RESERVED: SIGN-IN AND WIDGETS") })
        {
            dc.DrawRectangle(null, pen, zone);
            Text(dc, label, tf, 11 * u, pen.Brush, zone.Left + 8 * u, zone.Top + 6 * u, false);
        }
    }
}
