using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Vantage.Lockscreen;

/// <summary>
/// The account-picture emblem: a closed padlock inside a ticked ring, in the theme accent with a
/// glow, on a charcoal disc. The disc fills the square's inscribed circle and the corners stay
/// transparent, so Windows' circular crop of the account picture shows a clean disc that also sits
/// cleanly in the empty centre of the sign-in ring. Windows has no API to set the account picture
/// for a desktop app, so this only writes the file; the README says how to set it by hand.
/// </summary>
public static class EmblemRenderer
{
    public static BitmapSource Render(int size, Theme t)
    {
        double S = size, cx = S / 2, cy = S / 2, R = 0.5 * S;
        var root = new ContainerVisual();
        void Layer(Action<DrawingContext> draw, Effect? fx = null)
        {
            var v = new DrawingVisual { Effect = fx };
            using (var dc = v.RenderOpen()) draw(dc);
            root.Children.Add(v);
        }

        // Charcoal disc filling the inscribed circle; the square's corners stay transparent.
        Layer(dc =>
        {
            var lift = Themes.Lighten(t.Background, 0.08);
            var fill = new RadialGradientBrush(lift, t.Background) { Center = new Point(0.5, 0.44), GradientOrigin = new Point(0.5, 0.4) };
            dc.DrawEllipse(fill, null, new Point(cx, cy), R, R);
        });

        var art = new DrawingGroup();
        using (var dc = art.Open()) Draw(dc, S, cx, cy, t);
        art.Freeze();
        if (t.Glow > 0)
            Layer(dc => dc.DrawDrawing(art), new BlurEffect { Radius = 0.016 * S * t.Glow, RenderingBias = RenderingBias.Quality });
        Layer(dc => dc.DrawDrawing(art));

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    static void Draw(DrawingContext dc, double S, double cx, double cy, Theme t)
    {
        Pen P(double w, double a) => new(new SolidColorBrush(t.A(a)), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var c = new Point(cx, cy);

        // Ring: a thin outer circle, 48 ticks (every sixth longer and thicker), and an inner circle.
        dc.DrawEllipse(null, P(0.006 * S, 0.7), c, 0.455 * S, 0.455 * S);
        Pen minor = P(0.006 * S, t.Dim), major = P(0.012 * S, 0.9);
        for (var i = 0; i < 48; i++)
        {
            var big = i % 6 == 0;
            var a = i * (360.0 / 48) * Math.PI / 180;
            double r1 = (big ? 0.40 : 0.415) * S, r2 = 0.435 * S;
            dc.DrawLine(big ? major : minor, new Point(cx + r1 * Math.Cos(a), cy + r1 * Math.Sin(a)), new Point(cx + r2 * Math.Cos(a), cy + r2 * Math.Sin(a)));
        }
        dc.DrawEllipse(null, P(0.004 * S, 0.4), c, 0.38 * S, 0.38 * S);

        var stroke = P(0.022 * S, 1.0);

        // Padlock body: a rounded rectangle, sitting just below centre.
        double bw = 0.30 * S, bh = 0.22 * S, bx = cx - bw / 2, by = cy - 0.01 * S;
        dc.DrawGeometry(null, stroke, new RectangleGeometry(new Rect(bx, by, bw, bh), 0.035 * S, 0.035 * S));

        // Shackle: an open U whose legs enter the top of the body (a closed padlock).
        double legX = 0.078 * S, topY = cy - 0.17 * S, legBottom = by + 0.005 * S;
        var shackle = new StreamGeometry();
        using (var s = shackle.Open())
        {
            s.BeginFigure(new Point(cx - legX, legBottom), false, false);
            s.LineTo(new Point(cx - legX, topY), true, true);
            s.ArcTo(new Point(cx + legX, topY), new Size(legX, legX), 0, true, SweepDirection.Clockwise, true, true);
            s.LineTo(new Point(cx + legX, legBottom), true, true);
        }
        dc.DrawGeometry(null, stroke, shackle);

        // Keyhole: a dot over a tapering slot, filled in the accent.
        var fill = new SolidColorBrush(t.A(1));
        var kc = new Point(cx, by + bh * 0.42);
        dc.DrawEllipse(fill, null, kc, 0.028 * S, 0.028 * S);
        var slot = new StreamGeometry();
        using (var s = slot.Open())
        {
            s.BeginFigure(new Point(kc.X - 0.012 * S, kc.Y), true, true);
            s.LineTo(new Point(kc.X - 0.024 * S, by + bh - 0.03 * S), true, true);
            s.LineTo(new Point(kc.X + 0.024 * S, by + bh - 0.03 * S), true, true);
            s.LineTo(new Point(kc.X + 0.012 * S, kc.Y), true, true);
        }
        dc.DrawGeometry(fill, null, slot);
    }

    public static void Save(int size, Theme t, string path) => HudRenderer.SavePng(Render(size, t), path);
}
