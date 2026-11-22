using System.Windows;
using System.Windows.Media;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>
/// One launcher style as plain colours, fonts and frame shape, for the smaller windows that
/// must look like the launcher: Kai's drawing panel, the quick-ask box and the style preview
/// on the Appearance page. The colours are the launcher's own (SearchWindow.ApplyStyle).
/// </summary>
public sealed record StyleLook(
    string Style, Color Accent, Color Page, Color Surface, Color SurfaceAlt, Color Border,
    Color Text, Color Muted, Color Secondary, double Radius, FontFamily Font, FontFamily Tag,
    bool Chamfer, bool Scanlines, bool Header)
{
    /// <summary>The look of a style; a custom accent replaces the style's own, as in the launcher.</summary>
    public static StyleLook For(Settings settings) => For(settings.OverlayStyle, settings.AccentColor, settings);

    public static StyleLook For(string? style, string? accentText, Settings? s = null)
    {
        var key = (style ?? "tactical").ToLowerInvariant();
        var p = Suite.PaletteFor(new Settings { OverlayStyle = key, AccentColor = accentText ?? "", Theme = s?.Theme ?? "dark" });
        var accent = Parse(p.Accent, Color.FromRgb(0xF0, 0xA0, 0x30));
        Color C(string name, string fallback) =>
            Parse(p.Colors is { } c && c.TryGetValue(name, out var v) ? v : fallback, Colors.Gray);
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var segoe = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var bahn = new FontFamily("Bahnschrift, Segoe UI Variable Text, Segoe UI");

        return key switch
        {
            "hud" => new(key, accent, C("page", "#040B15"), C("surface", "#0A1622"), C("surfaceAlt", "#10233A"), C("border", "#1D4E63"),
                C("text", "#E8FBFF"), C("textSecondary", "#9FDCE8"), Mix(accent, C("page", "#040B15"), 0.35), 2, bahn, mono, true, true, true),
            "cyberpunk" => new(key, accent, C("page", "#0E0220"), C("surface", "#1A0733"), C("surfaceAlt", "#28104A"), C("border", "#5A1F6E"),
                C("text", "#F7ECFF"), C("textSecondary", "#6FF6FF"), Color.FromRgb(0x6F, 0xF6, 0xFF), 2, bahn, mono, true, true, true),
            "crt" => new(key, accent, C("page", "#020A04"), C("surface", "#061409"), C("surfaceAlt", "#0B2312"), Mix(accent, Colors.Black, 0.55),
                Mix(accent, Colors.White, 0.6), Mix(accent, Colors.Black, 0.2), Mix(accent, Colors.Black, 0.35), 0, mono, mono, true, true, true),
            "mono" => new(key, accent, C("page", "#101010"), C("surface", "#1A1A1A"), C("surfaceAlt", "#262626"), C("border", "#3A3A3A"),
                C("text", "#F2F2F2"), C("textSecondary", "#A0A0A0"), Color.FromRgb(0xA0, 0xA0, 0xA0), 6, segoe, segoe, false, false, false),
            "classic" => new(key, accent, Color.FromRgb(0x14, 0x14, 0x17), Color.FromRgb(0x1C, 0x1C, 0x1F), Color.FromRgb(0x27, 0x27, 0x2B),
                Color.FromRgb(0x3A, 0x3A, 0x40), Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0x9A, 0x9A, 0xA0), accent, 10, segoe, segoe, false, false, false),
            _ => new("tactical", accent, C("page", "#17181A"), C("surface", "#2C2E31"), C("surfaceAlt", "#3E4145"), C("border", "#55585E"),
                C("text", "#E6E6E6"), C("textSecondary", "#A4A7AC"), Mix(accent, Color.FromRgb(0x2A, 0x2C, 0x2F), 0.35), 0,
                new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Segoe UI"), mono, true, false, true),
        };
    }

    /// <summary>The words in the header strip ("KAI // EXPLAINS"): terminal-like for CRT.</summary>
    public string HeaderText(string what) => Style == "crt" ? $"C:\\KAI> {what.ToLowerInvariant()}_" : $"KAI  //  {what.ToUpperInvariant()}";

    public SolidColorBrush Brush(Color c, byte alpha = 0xFF)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>A plate in this style: cut corners for the framed looks, rounded for Classic and Mono.</summary>
    public FrameworkElement Plate(Color fill, byte fillAlpha, Color stroke, double thickness = 1, double cut = 12)
    {
        if (Chamfer)
            return new ChamferPlate
            {
                Fill = Brush(fill, fillAlpha), Stroke = Brush(stroke), StrokeThickness = thickness,
                CutTopLeft = cut, CutBottomRight = cut, CutTopRight = Style == "tactical" ? 0 : cut / 2, CutBottomLeft = Style == "tactical" ? 0 : cut / 2,
            };
        return new System.Windows.Controls.Border
        {
            Background = Brush(fill, fillAlpha), BorderBrush = Brush(stroke), BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(Radius + 4),
        };
    }

    /// <summary>Faint horizontal scanlines, as on the HUD, Cyberpunk and CRT launchers.</summary>
    public Brush ScanlineBrush()
    {
        var lines = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 4, 4), ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(Brush(Accent, Style == "crt" ? (byte)0x16 : (byte)0x0E), null, new RectangleGeometry(new Rect(0, 0, 4, 1.4))),
        };
        lines.Freeze();
        return lines;
    }

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    static Color Parse(string? text, Color fallback)
    {
        try { return text is { Length: > 0 } ? (Color)ColorConverter.ConvertFromString(text) : fallback; }
        catch (FormatException) { return fallback; }
    }
}
