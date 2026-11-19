// A user colour theme: theme.json next to settings.json overrides the surface, text and status colours of the light
// and/or dark variant. It is a separate file (not part of settings.json) so a theme can be shared and replaced on
// its own; the accent colour stays in ThemeSettings, where the Theme page edits it.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Settings;

/// <summary>Colours from <c>theme.json</c>, by variant. Missing keys keep Filee's own colours.</summary>
public sealed partial class CustomTheme
{
    /// <summary>File name inside the data directory (<see cref="UserDataStore.Directory"/>).</summary>
    public const string FileName = "theme.json";

    /// <summary>
    /// Larger files are skipped: Load runs on the UI thread at start-up and on every settings save (the Softness
    /// slider saves on each tick), and a real theme is well under 1 KB.
    /// </summary>
    public const long MaxBytes = 64 * 1024;

    /// <summary>Colour names a theme may set.</summary>
    public static readonly IReadOnlyList<string> Keys =
        ["page", "surface", "surfaceAlt", "border", "text", "textSecondary", "danger", "success", "warning"];

    /// <summary>Colours for the light variant, as name → <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public Dictionary<string, string> Light { get; set; } = [];

    /// <summary>Colours for the dark variant, as name → <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public Dictionary<string, string> Dark { get; set; } = [];

    /// <summary>
    /// NovaShield HUD: the look Beacon hands over (its style, accent, mode, corners and font). Beacon writes it into
    /// theme.json along with the colours whenever its Appearance changes, and Filee re-applies it live.
    /// </summary>
    public SuiteLook? Suite { get; set; }

    /// <summary>True when neither variant sets a colour and there is no suite look.</summary>
    public bool IsEmpty => Light.Count == 0 && Dark.Count == 0 && Suite is null;

    /// <summary>
    /// Reads <c>theme.json</c> from <paramref name="directory"/>. Unknown names and values that are not hex colours
    /// are dropped (and logged); a missing, empty or unreadable file gives null, so Filee's own colours apply.
    /// The file is the user's: unlike settings.json it is never renamed or rewritten.
    /// </summary>
    public static CustomTheme? Load(string directory, ILogger? log = null)
    {
        log ??= NullLogger.Instance;
        var path = Path.Combine(directory, FileName);
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        if (info.Length > MaxBytes)
        {
            log.LogWarning("Custom theme {Path} is larger than {Max} bytes; using the built-in colours", path, MaxBytes);
            return null;
        }

        CustomTheme? theme;
        try
        {
            using var stream = File.OpenRead(path);
            theme = JsonSerializer.Deserialize(stream, FileeJsonContext.Default.CustomTheme);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Custom theme {Path} could not be read; using the built-in colours", path);
            return null;
        }
        if (theme is not null)
        {
            theme.Light = Clean(theme.Light, "light", path, log);
            theme.Dark = Clean(theme.Dark, "dark", path, log);
            theme.Suite = theme.Suite?.Cleaned(path, log);
        }
        if (theme is null || theme.IsEmpty)
        {
            // Also covers "Dark" or "Light" with a capital: variant names are case-sensitive, colour names are not.
            log.LogWarning("Custom theme {Path} sets no colours Filee can use (expected \"light\"/\"dark\" objects of #RRGGBB values)", path);
            return null;
        }
        return theme;
    }

    private static Dictionary<string, string> Clean(Dictionary<string, string>? colors, string variant, string path, ILogger log)
    {
        var clean = new Dictionary<string, string>();
        foreach (var (name, value) in colors ?? [])
        {
            // Names are matched case-insensitively and stored in their canonical spelling.
            var key = Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is null)
                log.LogWarning("Custom theme {Path}: unknown colour {Variant}.{Name} ignored", path, variant, name);
            else if (value is null || !HexColor().IsMatch(value))
                log.LogWarning("Custom theme {Path}: {Variant}.{Name} is not #RRGGBB or #AARRGGBB, ignored", path, variant, name);
            else
                clean[key] = value;
        }
        return clean;
    }

    [GeneratedRegex("^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColor();

    internal static bool IsHexColor(string? value) => value is not null && HexColor().IsMatch(value);
}

/// <summary>
/// NovaShield HUD: the look of the suite Filee runs in. Every field is optional; what is set replaces the matching
/// field of Filee's own Theme settings while the suite look is in theme.json, without changing settings.json.
/// </summary>
public sealed class SuiteLook
{
    /// <summary>Beacon's style name ("tactical", "hud", "cyberpunk", "crt", "classic", "mono"), for the log.</summary>
    public string? Name { get; set; }

    /// <summary>Accent colour as <c>#RRGGBB</c>.</summary>
    public string? Accent { get; set; }

    /// <summary>"dark", "light" or "system".</summary>
    public string? Mode { get; set; }

    /// <summary>Base corner radius in pixels (0 gives the HUD's square-cut plates).</summary>
    public double? Radius { get; set; }

    /// <summary>Font families to try first, comma separated (e.g. "Bahnschrift"); Filee's own fonts follow.</summary>
    public string? Font { get; set; }

    /// <summary>A copy of <paramref name="theme"/> with this look laid over it.</summary>
    public ThemeSettings Over(ThemeSettings theme)
    {
        var result = theme.Clone();
        result.Style = ThemeStyle.Soft;
        if (Accent is not null)
        {
            result.Accent = Accent;
            result.PresetKey = null;
        }
        if (Radius is { } radius)
            result.CornerRadius = radius;
        if (Mode is not null)
            result.Mode = Mode.ToLowerInvariant() switch
            {
                "light" => ThemeMode.Light,
                "dark" => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
        return result;
    }

    internal SuiteLook Cleaned(string path, ILogger log)
    {
        if (Accent is not null && !CustomTheme.IsHexColor(Accent))
        {
            log.LogWarning("Custom theme {Path}: suite.accent is not #RRGGBB, ignored", path);
            Accent = null;
        }
        if (Radius is { } radius)
            Radius = double.IsFinite(radius) ? Math.Clamp(radius, 0, 32) : null;
        if (Font is not null && (string.IsNullOrWhiteSpace(Font) || Font.Length > 200))
            Font = null;
        return this;
    }
}
