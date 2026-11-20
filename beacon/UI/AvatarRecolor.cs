namespace Beacon.UI;

/// <summary>
/// The pixel work behind <see cref="AvatarArt"/>, kept free of WPF so it can be tested on its
/// own: blue and cyan glow turns to the accent's hue, everything else stays as drawn.
/// </summary>
public static class AvatarRecolor
{
    /// <summary>Recolours BGRA pixels in place.</summary>
    public static void ToAccent(byte[] bgra, byte r, byte g, byte b)
    {
        var (targetHue, targetSat, _) = ToHsv(r, g, b);
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var (h, s, v) = ToHsv(bgra[i + 2], bgra[i + 1], bgra[i]);
            // Glow only: saturated, bright enough, and blue/cyan. Hair, suit, skin and the
            // status colours (red, green, pink, yellow) are left alone.
            if (s < 0.3 || v < 0.22 || h < 170 || h > 265) continue;
            // A neutral accent (Minimal mono's grey-white) drains the colour instead.
            var sat = targetSat < 0.15 ? s * 0.12 : Math.Min(1, s * (0.6 + 0.4 * targetSat / Math.Max(s, 0.01)));
            var (nr, ng, nb) = FromHsv(targetHue, sat, v);
            bgra[i + 2] = nr; bgra[i + 1] = ng; bgra[i] = nb;
        }
    }

    public static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
