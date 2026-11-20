namespace Beacon.UI.Pet.Kai;

/// <summary>
/// Kai's expressions, painted onto the face texture: blinks, happy ^ ^ eyes, wide listening
/// eyes, half-lidded thinking eyes, worried brows, and mouths for smiling, grinning, talking
/// and "oh". The original eyes are kept (squashed or scaled) wherever they can be, so Kai
/// still looks like Kai.
///
/// The texture is stored upside down (that's how it was exported), so drawing happens in face
/// coordinates: 1024 units square, top of the face at 0, and is flipped onto the image.
/// Plain .NET over BGRA bytes, no WPF.
/// </summary>
public static class KaiFace
{
    public enum Eyes { Open, Half, Closed, Happy, Wide, Lidded }
    public enum Mouth { Smile, Grin, TalkSmall, TalkBig, Oh, Frown, Wavy }

    // Where things are, in face units (measured from the texture).
    static readonly (double X, double Y)[] EyeCentres = [(343, 561), (681, 561)];
    const double EyeRx = 98, EyeRy = 88;       // the eye itself
    const double CoverRx = 114, CoverRy = 90;  // the eye with its outer lash flick
    const double BrowY = 448;
    const double MouthX = 512, MouthY = 757;

    static readonly (byte R, byte G, byte B) Skin = (248, 210, 193);
    /// <summary>The lash and brow colour as drawn (navy); pass the tinted one to Paint.</summary>
    public static readonly (byte R, byte G, byte B) Ink = (14, 22, 48);
    static readonly (byte R, byte G, byte B) Lip = (104, 48, 59);
    static readonly (byte R, byte G, byte B) Inside = (92, 30, 44);
    static readonly (byte R, byte G, byte B) Tongue = (226, 112, 124);

    /// <summary>A new BGRA image: the face with the given expression.</summary>
    public static byte[] Paint(byte[] face, int width, int height, Eyes eyes, Mouth mouth, bool worried, (byte R, byte G, byte B)? ink = null)
    {
        var line = ink ?? Ink;
        var c = new Canvas((byte[])face.Clone(), width, height);
        var src = new Canvas(face, width, height);

        foreach (var (x, y) in EyeCentres)
        {
            var left = x < 512;
            switch (eyes)
            {
                case Eyes.Half: Squash(c, src, x, y, 0.42); break;
                case Eyes.Lidded: Squash(c, src, x, y, 0.68); break;
                case Eyes.Wide: Grow(c, src, x, y, 1.08); break;
                case Eyes.Closed:
                    c.FillEllipse(x, y, CoverRx, CoverRy, Skin, 8);
                    // A relaxed closed eye: a soft downward curve with a little lash at the outside.
                    c.Stroke(Curve(x - 66, x + 66, y + 4, 20), 13, line);
                    c.Stroke([(left ? x - 66 : x + 66, y + 4), (left ? x - 86 : x + 86, y - 10)], 9, line);
                    break;
                case Eyes.Happy:
                    c.FillEllipse(x, y, CoverRx, CoverRy, Skin, 8);
                    // ^ ^
                    c.Stroke(Curve(x - 58, x + 58, y + 14, -34), 15, line);
                    break;
            }
            if (worried)
            {
                // Lift the inner end of each brow.
                c.FillEllipse(x, BrowY, 70, 26, Skin, 6);
                var inner = left ? x + 46 : x - 46;
                var outer = left ? x - 52 : x + 52;
                c.Stroke([(outer, BrowY + 10), (inner, BrowY - 16)], 10, line);
            }
        }

        if (mouth != Mouth.Smile)
        {
            c.FillEllipse(MouthX, MouthY, 34, 16, Skin, 4);
            switch (mouth)
            {
                case Mouth.Grin:
                    // A wide open smile: flat top, round bottom, tongue showing.
                    c.FillShape((x, y) => y >= MouthY - 10 && Sq((x - MouthX) / 46) + Sq((y - (MouthY - 10)) / 34) <= 1, Inside, MouthX - 48, MouthY - 12, MouthX + 48, MouthY + 26);
                    c.FillShape((x, y) => y >= MouthY - 10 && Sq((x - MouthX) / 46) + Sq((y - (MouthY - 10)) / 34) <= 1 && Sq((x - MouthX) / 26) + Sq((y - (MouthY + 22)) / 16) <= 1, Tongue, MouthX - 30, MouthY + 4, MouthX + 30, MouthY + 26);
                    break;
                case Mouth.TalkSmall: OpenMouth(c, 17, 12); break;
                case Mouth.TalkBig: OpenMouth(c, 22, 22); break;
                case Mouth.Oh: OpenMouth(c, 11, 13); break;
                case Mouth.Frown: c.Stroke(Curve(MouthX - 22, MouthX + 22, MouthY + 6, -12), 8, Lip); break;
                case Mouth.Wavy:
                    var pts = new List<(double, double)>();
                    for (var i = 0; i <= 16; i++) pts.Add((MouthX - 22 + i * 44 / 16.0, MouthY + 4 * Math.Sin(i / 16.0 * Math.PI * 2)));
                    c.Stroke(pts, 7, Lip);
                    break;
            }
        }
        return c.Pixels;
    }

    static void OpenMouth(Canvas c, double rx, double ry)
    {
        c.FillEllipse(MouthX, MouthY + 2, rx, ry, Inside, 1.5);
        c.FillShape((x, y) => Sq((x - MouthX) / rx) + Sq((y - (MouthY + 2)) / ry) <= 1 && Sq((x - MouthX) / (rx * 0.7)) + Sq((y - (MouthY + 2 + ry * 0.75)) / (ry * 0.5)) <= 1,
            Tongue, MouthX - rx, MouthY, MouthX + rx, MouthY + 2 + ry);
    }

    /// <summary>The eye squashed toward its lower lid, as when blinking.</summary>
    static void Squash(Canvas c, Canvas src, double cx, double cy, double k)
    {
        var pivot = cy + 34;
        c.Remap(cx, cy, CoverRx, CoverRy, (x, y) => (x, pivot + (y - pivot) / k), src);
    }

    /// <summary>The eye a little larger, as when listening closely.</summary>
    static void Grow(Canvas c, Canvas src, double cx, double cy, double k) =>
        c.Remap(cx, cy, CoverRx * k, EyeRy * k, (x, y) => (cx + (x - cx) / k, cy + (y - cy) / k), src);

    /// <summary>A curve from x0 to x1 that sags by `sag` in the middle (negative: arches up).</summary>
    static List<(double, double)> Curve(double x0, double x1, double y, double sag)
    {
        var pts = new List<(double, double)>();
        for (var i = 0; i <= 20; i++)
        {
            var t = i / 20.0;
            pts.Add((x0 + (x1 - x0) * t, y + sag * (1 - Sq(2 * t - 1))));
        }
        return pts;
    }

    static double Sq(double v) => v * v;

    /// <summary>Drawing in face units onto the (upside-down, any-size) texture.</summary>
    sealed class Canvas(byte[] pixels, int width, int height)
    {
        public byte[] Pixels { get; } = pixels;
        readonly double _scale = width / 1024.0;

        int Px(double x) => (int)Math.Round(x * _scale);
        int Row(double y) => height - 1 - (int)Math.Round(y * height / 1024.0);

        (double X, double Y) Unit(int px, int row) => ((px + 0.5) / _scale, (height - 1 - row + 0.5) * 1024.0 / height);

        void Blend(int px, int row, (byte R, byte G, byte B) col, double a)
        {
            if (a <= 0 || px < 0 || row < 0 || px >= width || row >= height) return;
            a = Math.Min(1, a);
            var i = (row * width + px) * 4;
            Pixels[i] = (byte)(Pixels[i] + (col.B - Pixels[i]) * a);
            Pixels[i + 1] = (byte)(Pixels[i + 1] + (col.G - Pixels[i + 1]) * a);
            Pixels[i + 2] = (byte)(Pixels[i + 2] + (col.R - Pixels[i + 2]) * a);
            Pixels[i + 3] = 255;
        }

        public (byte R, byte G, byte B)? Sample(double x, double y)
        {
            int px = Px(x - 0.5 / _scale), row = Row(y);
            if (px < 0 || row < 0 || px >= width || row >= height) return null;
            var i = (row * width + px) * 4;
            return (Pixels[i + 2], Pixels[i + 1], Pixels[i]);
        }

        void Each(double x0, double y0, double x1, double y1, Action<int, int, double, double> f)
        {
            int c0 = Math.Max(0, Px(x0) - 1), c1 = Math.Min(width - 1, Px(x1) + 1);
            int r0 = Math.Max(0, Row(y1) - 1), r1 = Math.Min(height - 1, Row(y0) + 1);
            for (var row = r0; row <= r1; row++)
                for (var px = c0; px <= c1; px++)
                {
                    var (x, y) = Unit(px, row);
                    f(px, row, x, y);
                }
        }

        /// <summary>A filled ellipse with a soft edge `feather` units wide.</summary>
        public void FillEllipse(double cx, double cy, double rx, double ry, (byte, byte, byte) col, double feather) =>
            Each(cx - rx - feather, cy - ry - feather, cx + rx + feather, cy + ry + feather, (px, row, x, y) =>
            {
                var d = (Math.Sqrt(Sq((x - cx) / rx) + Sq((y - cy) / ry)) - 1) * Math.Min(rx, ry);
                Blend(px, row, col, 0.5 - d / Math.Max(feather, 1));
            });

        public void FillShape(Func<double, double, bool> inside, (byte, byte, byte) col, double x0, double y0, double x1, double y1) =>
            Each(x0, y0, x1, y1, (px, row, x, y) =>
            {
                // 2×2 samples per pixel for smooth edges.
                var step = 0.25 / _scale;
                var hits = (inside(x - step, y - step) ? 1 : 0) + (inside(x + step, y - step) ? 1 : 0) +
                           (inside(x - step, y + step) ? 1 : 0) + (inside(x + step, y + step) ? 1 : 0);
                Blend(px, row, col, hits / 4.0);
            });

        /// <summary>A round-capped line through the points, `thickness` units wide.</summary>
        public void Stroke(IReadOnlyList<(double X, double Y)> pts, double thickness, (byte, byte, byte) col)
        {
            var r = thickness / 2;
            double x0 = pts.Min(p => p.X) - r, x1 = pts.Max(p => p.X) + r, y0 = pts.Min(p => p.Y) - r, y1 = pts.Max(p => p.Y) + r;
            var soft = 0.75 / _scale;
            Each(x0, y0, x1, y1, (px, row, x, y) =>
            {
                var best = double.MaxValue;
                for (var i = 0; i + 1 < pts.Count; i++) best = Math.Min(best, SegmentDistance(x, y, pts[i], pts[i + 1]));
                Blend(px, row, col, (r - best) / soft + 0.5);
            });
        }

        /// <summary>Redraws an elliptical area from the source through a mapping (outside the source eye: skin).</summary>
        public void Remap(double cx, double cy, double rx, double ry, Func<double, double, (double X, double Y)> map, Canvas source) =>
            Each(cx - rx - 4, cy - ry - 4, cx + rx + 4, cy + ry + 4, (px, row, x, y) =>
            {
                var edge = (1 - Math.Sqrt(Sq((x - cx) / rx) + Sq((y - cy) / ry))) * Math.Min(rx, ry);
                if (edge < -4) return;
                var (sx, sy) = map(x, y);
                var inEye = Sq((sx - cx) / EyeRx) + Sq((sy - cy) / EyeRy) <= 1;
                var col = inEye ? source.Sample(sx, sy) ?? Skin : Skin;
                Blend(px, row, col, edge / 4 + 0.5);
            });

        static double SegmentDistance(double x, double y, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            var len = dx * dx + dy * dy;
            var t = len < 1e-9 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len, 0, 1);
            return Math.Sqrt(Sq(x - a.X - t * dx) + Sq(y - a.Y - t * dy));
        }
    }
}
