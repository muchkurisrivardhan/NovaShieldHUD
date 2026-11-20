namespace Beacon.UI.Pet.Kai;

/// <summary>
/// Recolours Kai to a Beacon style. Kai was drawn in blues: the glow, the suit's navy, the
/// hair, the eyes. Every blue is turned to the accent's hue, keeping its lightness, so the
/// suit stays dark, the glow stays bright and the eyes keep their shading. Skin, blush, white
/// and grey are left alone. A grey accent (the mono style) makes the blues grey.
/// </summary>
public sealed class KaiTint
{
    const double SourceHue = 212;   // Kai's own blue
    readonly double _hue, _satScale;

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public KaiTint(byte r, byte g, byte b)
    {
        (R, G, B) = (r, g, b);
        var (h, s, _) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
        _hue = h;
        _satScale = s < 0.12 ? 0.06 : 1;
    }

    /// <summary>One colour, sRGB bytes.</summary>
    public (byte R, byte G, byte B) Apply(byte r, byte g, byte b)
    {
        var (h, s, l) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
        if (s < 0.06) return (r, g, b);
        // How blue it is: fully within 55° of Kai's blue, fading out by 75°.
        var d = Math.Abs(((h - SourceHue + 540) % 360) - 180);
        var w = d <= 55 ? 1 : d >= 75 ? 0 : 1 - (d - 55) / 20;
        if (w <= 0) return (r, g, b);
        var nh = (_hue + (h - SourceHue) * 0.35 + 360) % 360;
        var ns = s * _satScale * (l < 0.15 ? 0.7 : 1);
        var (nr, ng, nb) = FromHsl(nh, Math.Min(1, ns), l);
        return ((byte)Math.Round(r + (nr * 255 - r) * w), (byte)Math.Round(g + (ng * 255 - g) * w), (byte)Math.Round(b + (nb * 255 - b) * w));
    }

    /// <summary>A whole image, BGRA bytes, in place.</summary>
    public void ApplyBgra(byte[] px)
    {
        var cache = new Dictionary<int, (byte, byte, byte)>();
        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i + 3] == 0) continue;
            var key = px[i] | px[i + 1] << 8 | px[i + 2] << 16;
            if (!cache.TryGetValue(key, out var c)) cache[key] = c = Apply(px[i + 2], px[i + 1], px[i]);
            (px[i + 2], px[i + 1], px[i]) = c;
        }
    }

    /// <summary>A glTF linear colour factor as sRGB bytes.</summary>
    public static (byte R, byte G, byte B) FromLinear(float r, float g, float b) => (Srgb(r), Srgb(g), Srgb(b));

    static byte Srgb(float v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255);
    }

    static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    static (double R, double G, double B) FromHsl(double h, double s, double l)
    {
        if (s <= 0) return (l, l, l);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Channel(double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        var hh = h / 360;
        return (Channel(hh + 1 / 3.0), Channel(hh), Channel(hh - 1 / 3.0));
    }
}
