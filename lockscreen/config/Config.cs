using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vantage.Lockscreen;

public sealed record CustomTheme(string Accent, string Background, double DimOpacity, double Glow);
public sealed record Effects(bool Grid, bool Scanlines, bool Vignette, bool Glow);
public sealed record Countdown(string Label, DateTime Date);

/// <summary>
/// %LOCALAPPDATA%\LockscreenWidget\config.json. Every field is checked on its own: a bad value
/// falls back to its default and adds a warning, so one typo never loses the rest of the file.
/// </summary>
public sealed record Config
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockscreenWidget");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    /// <summary>"beacon" follows Beacon's style; or a built-in name; or "custom".</summary>
    public string Theme { get; init; } = "beacon";
    public CustomTheme? Custom { get; init; }
    public double RingX { get; init; } = 0.5;
    public double RingY { get; init; } = 0.42;
    public double RingScale { get; init; } = 1.0;
    public string[][] Corners { get; init; } = DefaultCorners;   // TL, TR, BL, BR; [title, subtitle]
    public Effects Effects { get; init; } = new(true, true, true, true);
    public string NodeLabel { get; init; } = "NODE-01";
    public Countdown Countdown { get; init; } = new("TARGET", new DateTime(2027, 1, 1));
    public int RefreshMinutes { get; init; } = 60;

    static readonly string[][] DefaultCorners =
    [
        ["SYSTEM", "SECURE SHELL // v1.0"],
        ["{DATE}", "{WEEKDAY}"],
        ["NODE-01", "LINK: LOCAL // ENCRYPTED"],
        ["STATUS: SEALED", "{UPTIME_DAYS}D UPTIME"],
    ];

    static readonly string[] CornerKeys = ["topLeft", "topRight", "bottomLeft", "bottomRight"];

    /// <summary>Loads the file, writing one with the defaults if there is none.</summary>
    public static Config Load(List<string> warnings)
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new Config();
            try { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, fresh.ToJson()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add($"Couldn't create {FilePath}: {e.Message}"); }
            return fresh;
        }
        try { return Parse(File.ReadAllText(FilePath), warnings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Couldn't read {FilePath} ({e.Message}); using defaults.");
            return new Config();
        }
    }

    public static Config Parse(string json, List<string> warnings)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("not a JSON object"); }
        catch (JsonException e) { warnings.Add($"config.json isn't valid JSON ({e.Message}); using defaults."); return new Config(); }

        var d = new Config();
        T Get<T>(JsonNode? node, string name, T fallback, Func<T, bool>? ok = null)
        {
            if (node is null) return fallback;
            try
            {
                var v = node.GetValue<T>();
                if (ok is null || ok(v)) return v;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { }
            warnings.Add($"\"{name}\" has an invalid value ({node.ToJsonString()}); using {fallback}.");
            return fallback;
        }

        var theme = Get(root["theme"], "theme", d.Theme, t => Themes.IsKnown(t) || t is "beacon" or "custom");
        CustomTheme? custom = null;
        if (root["customTheme"] is JsonObject ct)
        {
            custom = new CustomTheme(
                Get(ct["accent"], "customTheme.accent", "#FFB000", s => Themes.TryParse(s, out _)),
                Get(ct["background"], "customTheme.background", "#0E0F11", s => Themes.TryParse(s, out _)),
                Get(ct["dimOpacity"], "customTheme.dimOpacity", 0.55, v => v is >= 0.1 and <= 1),
                Get(ct["glow"], "customTheme.glow", 1.0, v => v is >= 0 and <= 2));
        }
        if (theme == "custom" && custom is null)
        {
            warnings.Add("\"theme\" is \"custom\" but there's no \"customTheme\"; following Beacon instead.");
            theme = "beacon";
        }

        var corners = d.Corners.Select(c => c.ToArray()).ToArray();
        if (root["corners"] is JsonObject cs)
        {
            for (var i = 0; i < 4; i++)
            {
                if (cs[CornerKeys[i]] is not { } node) continue;
                if (node is JsonArray { Count: 2 } a && a.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
                    corners[i] = [a[0]!.GetValue<string>(), a[1]!.GetValue<string>()];
                else warnings.Add($"\"corners.{CornerKeys[i]}\" must be two strings, like [\"TITLE\", \"subtitle\"]; using the default.");
            }
        }

        var fx = root["effects"] as JsonObject;
        var w = root["widgets"] as JsonObject;
        var cd = w?["countdown"] as JsonObject;
        var cdDate = Get(cd?["date"], "widgets.countdown.date", "2027-01-01",
            s => DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _));

        return new Config
        {
            Theme = theme,
            Custom = custom,
            RingX = Get(root["ringCenter"]?["x"], "ringCenter.x", d.RingX, v => v is >= 0.2 and <= 0.8),
            RingY = Get(root["ringCenter"]?["y"], "ringCenter.y", d.RingY, v => v is >= 0.25 and <= 0.6),
            RingScale = Get(root["ringScale"], "ringScale", d.RingScale, v => v is >= 0.5 and <= 1.5),
            Corners = corners,
            Effects = new Effects(
                Get(fx?["grid"], "effects.grid", true),
                Get(fx?["scanlines"], "effects.scanlines", true),
                Get(fx?["vignette"], "effects.vignette", true),
                Get(fx?["glow"], "effects.glow", true)),
            NodeLabel = Get(w?["nodeLabel"], "widgets.nodeLabel", d.NodeLabel, s => s.Length is > 0 and <= 24),
            Countdown = new Countdown(
                Get(cd?["label"], "widgets.countdown.label", d.Countdown.Label, s => s.Length is > 0 and <= 24),
                DateTime.ParseExact(cdDate, "yyyy-MM-dd", null)),
            RefreshMinutes = Get(root["refreshMinutes"], "refreshMinutes", d.RefreshMinutes, v => v is >= 15 and <= 1440),
        };
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["theme"] = Theme,
            ["ringCenter"] = new JsonObject { ["x"] = RingX, ["y"] = RingY },
            ["ringScale"] = RingScale,
            ["corners"] = new JsonObject(CornerKeys.Select((k, i) =>
                KeyValuePair.Create(k, (JsonNode?)new JsonArray(Corners[i][0], Corners[i][1])))),
            ["effects"] = new JsonObject { ["grid"] = Effects.Grid, ["scanlines"] = Effects.Scanlines, ["vignette"] = Effects.Vignette, ["glow"] = Effects.Glow },
            ["widgets"] = new JsonObject
            {
                ["nodeLabel"] = NodeLabel,
                ["countdown"] = new JsonObject { ["label"] = Countdown.Label, ["date"] = Countdown.Date.ToString("yyyy-MM-dd") },
            },
            ["refreshMinutes"] = RefreshMinutes,
        };
        if (Custom is { } c)
            o["customTheme"] = new JsonObject { ["accent"] = c.Accent, ["background"] = c.Background, ["dimOpacity"] = c.DimOpacity, ["glow"] = c.Glow };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
