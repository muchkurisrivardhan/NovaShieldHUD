using System.IO;
using System.Text.Json;
using System.Windows.Media;

namespace Vantage.Lockscreen;

/// <param name="Dim">Opacity of the secondary elements (subtitles, thin rings).</param>
/// <param name="Glow">Strength of the blurred copy under every stroke; 0 turns it off.</param>
public sealed record Theme(string Name, Color Accent, Color Background, double Dim = 0.55, double Glow = 1.0)
{
    public Color A(double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), Accent.R, Accent.G, Accent.B);
}

public static class Themes
{
    public static readonly Theme[] BuiltIn =
    [
        new("amber", Hex("#FFB000"), Hex("#0E0F11")),
        new("cyan", Hex("#00E5FF"), Hex("#0A0F14")),
        new("phosphor", Hex("#33FF66"), Hex("#0A0F0B")),
        new("redalert", Hex("#FF3B3B"), Hex("#120A0A")),
        new("mono", Hex("#E8E8E8"), Hex("#0C0C0C")),
        new("neon", Hex("#FF2E97"), Hex("#12041F")),
    ];

    public static bool IsKnown(string name) => BuiltIn.Any(t => t.Name == name);

    // Beacon's "no accent chosen" value; each style then uses its own colour.
    const string BeaconDefaultAccent = "#5B8DEF";

    /// <summary>Beacon's style → lock screen theme, so the whole suite wears one look.</summary>
    public static Theme FromBeacon(string style, string accent)
    {
        var name = style switch
        {
            "hud" => "cyan",
            "crt" => "phosphor",
            "mono" => "mono",
            "cyberpunk" => "neon",
            _ => "amber",   // tactical, and classic's frame
        };
        var t = BuiltIn.First(b => b.Name == name);
        var custom = !string.Equals(accent, BeaconDefaultAccent, StringComparison.OrdinalIgnoreCase);
        if ((custom || style == "classic") && TryParse(accent, out var c)) t = t with { Name = "beacon-" + style, Accent = c };
        return t;
    }

    /// <summary>The theme the config asks for; <paramref name="overrideName"/> is --theme.</summary>
    public static Theme Resolve(Config cfg, string? overrideName, List<string> warnings)
    {
        var name = overrideName ?? cfg.Theme;
        if (BuiltIn.FirstOrDefault(t => t.Name == name) is { } builtIn) return builtIn;
        if (name == "custom" && cfg.Custom is { } c)
            return new Theme("custom", Hex(c.Accent), Hex(c.Background), c.DimOpacity, c.Glow);
        if (name != "beacon") warnings.Add($"Unknown theme \"{name}\"; following Beacon.");
        var (style, accent) = ReadBeacon();
        return FromBeacon(style, accent);
    }

    /// <summary>Beacon's style and accent from its settings file; Tactical when Beacon isn't set up.</summary>
    static (string Style, string Accent) ReadBeacon()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon", "settings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var style = r.TryGetProperty("OverlayStyle", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.ToLowerInvariant() : "tactical";
            var accent = r.TryGetProperty("AccentColor", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()!.Trim() : BeaconDefaultAccent;
            return (style, accent);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return ("tactical", BeaconDefaultAccent); }
    }

    public static bool TryParse(string? s, out Color c)
    {
        c = default;
        if (s is null || !System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$")) return false;
        c = Color.FromRgb(Convert.ToByte(s[1..3], 16), Convert.ToByte(s[3..5], 16), Convert.ToByte(s[5..7], 16));
        return true;
    }

    public static Color Hex(string s) => TryParse(s, out var c) ? c : throw new FormatException($"Not a #RRGGBB colour: {s}");

    /// <summary>Moves a colour toward white by <paramref name="amount"/> (0..1).</summary>
    public static Color Lighten(Color c, double amount)
    {
        byte L(byte v) => (byte)Math.Round(v + (255 - v) * Math.Clamp(amount, 0, 1));
        return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
    }
}
