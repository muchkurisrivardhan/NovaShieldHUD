using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Core;

/// <summary>
/// Beacon's side of Filee, the drag-and-drop file converter in the suite (a fork of the MIT-licensed
/// KnifeLemon/Filee, in filee/). Hold Ctrl and drag files: a donut of formats opens at the cursor.
///
/// Look: Beacon writes its style as a theme.json in Filee's data folder, with a "suite" section
/// (accent, mode, corners, font) the fork reads on top of Filee's own Theme settings. The fork
/// watches that file, so a new look reaches a running Filee in place, without a restart, and
/// Filee's settings.json is never touched for it.
///
/// Settings: the few Filee options on Beacon's Filee page live in Filee's settings.json, which
/// Filee rewrites from memory; so, as with Dotline, a running Filee is stopped before they are
/// written and started again after.
/// </summary>
public static partial class Suite
{
    static readonly string FileeData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");

    static string FileeSettings => Path.Combine(FileeData, "settings.json");
    static string FileeTheme => Path.Combine(FileeData, "theme.json");

    public static bool FileeRunning => Process.GetProcessesByName("Filee").Length > 0;

    /// <summary>
    /// Smart App Control is on (enforcing). It blocks the unsigned parts of an app one file at a
    /// time ("Part of this app has been blocked"), which leaves Filee without its input hook (no
    /// ring) and closes it when a page needs a blocked file. Until releases are code-signed,
    /// Beacon doesn't start Filee by itself while it is on. 0 = off, 1 = on, 2 = evaluation.
    /// </summary>
    public static bool SmartAppControlOn
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
                return key?.GetValue("VerifiedAndReputablePolicyState") is int state && state == 1;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    public const string SmartAppControlNote =
        "Smart App Control is on, and it blocks the parts of Filee that aren't code-signed yet, so the ring can't open " +
        "and Filee may close. Beacon won't start Filee by itself while it's on.";

    /// <summary>
    /// Filee's exe: in the filee folder beside Beacon when installed, otherwise its build in the
    /// source tree (filee/src/Filee.App).
    /// </summary>
    public static string? FindFilee()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "filee", "Filee.exe");
        if (File.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "filee", "src", "Filee.App", "bin");
            if (!Directory.Exists(app)) continue;
            return Directory.EnumerateFiles(app, "Filee.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    /// <summary>Starts Filee in the background (tray only), or shows its window with <paramref name="show"/>.</summary>
    public static string OpenFilee(bool show = false)
    {
        if (FindFilee() is not { } exe) return "Couldn't find Filee next to Beacon.";
        if (FileeRunning && !show) return "Filee is already running. Hold Ctrl and drag files to convert them.";
        SyncFileeLook(Settings.Load());
        // A second launch hands its request to the running Filee (single instance), which then shows its window.
        Process.Start(new ProcessStartInfo(exe, show ? "--settings" : "--background")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
        return show ? "Opened Filee." : "Filee is running. Hold Ctrl and drag files to convert them.";
    }

    /// <summary>Asks Filee to quit (it saves first); ends it if it doesn't within a few seconds.</summary>
    public static string CloseFilee()
    {
        if (!FileeRunning) return "Filee isn't running.";
        if (FindFilee() is { } exe)
        {
            try
            {
                using var quit = Process.Start(new ProcessStartInfo(exe, "--quit") { UseShellExecute = false, CreateNoWindow = true });
                quit?.WaitForExit(5000);
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        foreach (var p in Process.GetProcessesByName("Filee"))
        {
            using (p)
            {
                try { if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); } }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return FileeRunning ? "Windows refused to close Filee." : "Closed Filee.";
    }

    // ------------------------------------------------------------------ look

    /// <summary>One Beacon style as Filee colours.</summary>
    internal sealed record FileePalette(string Accent, int Radius, string Font, string Mode, Dictionary<string, string>? Colors);

    const string BeaconDefaultAccent = "#5B8DEF";

    /// <summary>
    /// Beacon's style as a Filee palette: the same signature colours, fonts and corners as the
    /// launcher and Dotline. A custom accent replaces the style's own, as it does in Beacon.
    /// </summary>
    internal static FileePalette PaletteFor(Settings s)
    {
        var style = (s.OverlayStyle ?? "tactical").ToLowerInvariant();
        var custom = string.Equals(s.AccentColor?.Trim(), BeaconDefaultAccent, StringComparison.OrdinalIgnoreCase)
                     || !IsHex(s.AccentColor?.Trim()) ? null : s.AccentColor!.Trim().ToUpperInvariant();

        static Dictionary<string, string> C(string page, string surface, string alt, string border, string text, string secondary,
            string? success = null, string? warning = null, string? danger = null)
        {
            var c = new Dictionary<string, string>
            {
                ["page"] = page, ["surface"] = surface, ["surfaceAlt"] = alt, ["border"] = border,
                ["text"] = text, ["textSecondary"] = secondary,
            };
            if (success is not null) c["success"] = success;
            if (warning is not null) c["warning"] = warning;
            if (danger is not null) c["danger"] = danger;
            return c;
        }

        return style switch
        {
            // Cyan holographic: deep navy plates, glowing cyan, square-cut corners.
            "hud" => new(custom ?? "#35E6FF", 2, "Bahnschrift", "dark",
                C("#040B15", "#0A1622", "#10233A", "#1D4E63", "#E8FBFF", "#9FDCE8")),
            // Hot pink on deep violet, cyan secondary text.
            "cyberpunk" => new(custom ?? "#FF2E97", 2, "Bahnschrift", "dark",
                C("#0E0220", "#1A0733", "#28104A", "#5A1F6E", "#F7ECFF", "#6FF6FF", success: "#3DFFB0", warning: "#FFD23D")),
            // Green-screen terminal: phosphor on black, monospace, no rounding at all.
            "crt" => new(custom ?? "#33FF66", 0, "Cascadia Mono, Consolas", "dark",
                C("#020A04", "#061409", "#0B2312", "#1C5A2C", "#C8FFD6", "#5FD682", success: "#33FF66", warning: "#E8FF4A", danger: "#FF5A3C")),
            // Black, white and grey.
            "mono" => new(custom ?? "#E8E8E8", 6, "Segoe UI Variable Text", "dark",
                C("#101010", "#1A1A1A", "#262626", "#3A3A3A", "#F2F2F2", "#A0A0A0")),
            // Clean and rounded: Filee's own surfaces, Beacon's accent and light/dark choice.
            "classic" => new(custom ?? BeaconDefaultAccent, 12, "Segoe UI Variable Text", s.Theme is "light" or "system" ? s.Theme : "dark", null),
            // Tactical: machined gunmetal with one amber accent.
            _ => new(custom ?? "#F0A030", 2, "Bahnschrift", "dark",
                C("#17181A", "#2C2E31", "#3E4145", "#55585E", "#E6E6E6", "#A4A7AC")),
        };
    }

    static bool IsHex(string? v) =>
        v is { Length: 7 } && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    /// <summary>
    /// Writes Beacon's look into Filee's theme.json (or removes it when the sync is off). A
    /// theme.json the user wrote by hand, without Beacon's "suite" section, is left alone.
    /// A running Filee picks the change up by itself. Returns an error message, or null.
    /// </summary>
    public static string? SyncFileeLook(Settings s)
    {
        try
        {
            var ours = !File.Exists(FileeTheme) || IsBeaconTheme();
            if (!ours) return "Filee has a theme.json of your own, so Beacon's look wasn't applied to it.";

            if (!s.FileeFollowsLook)
            {
                if (File.Exists(FileeTheme)) File.Delete(FileeTheme);
                return null;
            }

            var p = PaletteFor(s);
            var theme = new JsonObject
            {
                ["_comment"] = "Written by Beacon (NovaShield HUD) from its Appearance settings. Turn off \"Filee follows Beacon's look\" in Beacon > Settings > Filee to use your own.",
                ["suite"] = new JsonObject
                {
                    ["name"] = (s.OverlayStyle ?? "tactical").ToLowerInvariant(),
                    ["accent"] = p.Accent,
                    ["mode"] = p.Mode,
                    ["radius"] = p.Radius,
                    ["font"] = p.Font,
                },
            };
            if (p.Colors is { } colors)
            {
                theme["dark"] = ToJson(colors);
                theme["light"] = ToJson(colors);   // the mode is forced dark; this only covers a hand-edited mode
            }

            var text = theme.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(FileeTheme) && File.ReadAllText(FileeTheme) == text) return null;   // no change, no reload
            Directory.CreateDirectory(FileeData);
            Settings.WriteAtomic(FileeTheme, text);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Couldn't write Filee's theme: " + e.Message;
        }

        static JsonObject ToJson(Dictionary<string, string> colors)
        {
            var o = new JsonObject();
            foreach (var (k, v) in colors) o[k] = v;
            return o;
        }
    }

    static bool IsBeaconTheme()
    {
        try { return JsonNode.Parse(File.ReadAllText(FileeTheme)) is JsonObject o && o.ContainsKey("suite"); }
        catch (JsonException) { return false; }   // unreadable: not ours to replace
    }

    // ------------------------------------------------------------------ settings

    /// <summary>
    /// The Filee settings Beacon's Settings window shows. Ring size is the donut's outer radius in
    /// pixels; opacity is 0.1 to 1.
    /// </summary>
    public sealed record FileeOptions(double RingSize, double Opacity, bool Captions, bool Paused, bool CheckUpdates);

    static readonly FileeOptions FileeDefaults = new(150, 0.96, true, false, true);

    /// <summary>Filee's saved settings, or its defaults if it has never run.</summary>
    public static FileeOptions ReadFilee()
    {
        var s = LoadJson(FileeSettings);
        if (s is null) return FileeDefaults;
        try
        {
            var donut = s["donut"] as JsonObject;
            return new FileeOptions(
                donut?["outerRadius"]?.GetValue<double>() ?? 150,
                donut?["opacity"]?.GetValue<double>() ?? 0.96,
                donut?["showCaptions"]?.GetValue<bool>() ?? true,
                s["paused"]?.GetValue<bool>() ?? false,
                s["checkForUpdates"]?.GetValue<bool>() ?? true);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return FileeDefaults;
        }
    }

    /// <summary>
    /// Writes changed options into Filee's settings.json, leaving the rest (presets, triggers,
    /// history) as Filee saved it. Before Filee's first run there is no file yet and nothing is
    /// written: a file Beacon made would make Filee treat a new install as an old one.
    /// Returns an error message, or null when saved or unchanged.
    /// </summary>
    public static string? WriteFilee(FileeOptions o)
    {
        if (o == ReadFilee()) return null;
        if (!File.Exists(FileeSettings))
            return "Filee hasn't run yet, so its settings weren't changed. Start it once, then save again.";
        if (LoadJson(FileeSettings) is null)
            return "Filee's settings couldn't be read, so they weren't changed.";

        var wasRunning = FileeRunning;
        if (wasRunning) CloseFilee();
        if (FileeRunning) return "Filee didn't close, so its settings weren't changed.";

        var s = LoadJson(FileeSettings)!;   // re-read: Filee saved on the way out
        if (s["donut"] is not JsonObject donut) s["donut"] = donut = new JsonObject();
        donut["outerRadius"] = Math.Clamp(o.RingSize, 90, 260);
        donut["opacity"] = Math.Clamp(o.Opacity, 0.1, 1);
        donut["showCaptions"] = o.Captions;
        s["paused"] = o.Paused;
        s["checkForUpdates"] = o.CheckUpdates;

        try { Settings.WriteAtomic(FileeSettings, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Couldn't save Filee's settings: " + e.Message; }
        if (wasRunning) OpenFilee();
        return null;
    }

    static JsonObject? LoadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
