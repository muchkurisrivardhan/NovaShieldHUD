using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beacon.Core;

/// <summary>
/// Beacon's side of Dotline, the desktop widgets app that lives beside it: starting and
/// stopping it, and editing its settings from Beacon's Settings window.
///
/// Dotline reads Beacon's look (style and accent) from Beacon's settings file when it starts,
/// and saves its own state on every change, so a restart is how a change made here reaches it.
/// </summary>
public static class Suite
{
    static readonly string DotlineState = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dotline", "state.json");

    public static bool DotlineRunning => Process.GetProcessesByName("Dotline").Length > 0;

    /// <summary>
    /// Dotline's exe: beside Beacon when installed, otherwise found by walking up from Beacon's
    /// folder to the source tree (the newest of its published build and its Release build).
    /// </summary>
    public static string? FindDotline()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Dotline.exe");
        if (File.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var root = Path.Combine(dir.FullName, "dotline");
            if (!Directory.Exists(root)) continue;
            return new[]
                {
                    Path.Combine(root, "dist", "Dotline.exe"),
                    Path.Combine(root, "bin", "Release", "net10.0-windows", "Dotline.exe"),
                }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    public static string OpenDotline()
    {
        if (DotlineRunning) return "Dotline is already running.";
        if (FindDotline() is not { } exe) return "Couldn't find Dotline next to Beacon.";
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        return "Opened Dotline.";
    }

    /// <summary>Dotline saves every change the moment it happens, so ending it loses nothing.</summary>
    public static string CloseDotline()
    {
        var running = Process.GetProcessesByName("Dotline");
        if (running.Length == 0) return "Dotline isn't running.";
        foreach (var p in running)
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return DotlineRunning ? "Windows refused to close Dotline." : "Closed Dotline.";
    }

    /// <summary>Restart Dotline if it is running, so it picks up a new look or settings.</summary>
    public static void RefreshDotline()
    {
        if (!DotlineRunning) return;
        CloseDotline();
        OpenDotline();
    }

    // ------------------------------------------------------------------ settings

    /// <summary>The Dotline settings Beacon's Settings window shows.</summary>
    public sealed record DotlineOptions(DateTime? Birthday, int Lifespan, string LifeUnit, int Background, bool Locked, bool MapOn);

    /// <summary>Dotline's saved settings, or its defaults if it has never run.</summary>
    public static DotlineOptions ReadDotline()
    {
        var s = LoadState();
        try
        {
            var example = s?["Example"]?.GetValue<bool>() ?? true;   // still the placeholder birthday
            return new DotlineOptions(
                example ? null : s?["Birthday"]?.GetValue<DateTime>(),
                s?["Lifespan"]?.GetValue<int>() ?? 70,
                s?["LifeUnit"]?.GetValue<string>() ?? "week",
                s?["Glass"]?.GetValue<int>() ?? 1,
                s?["Locked"]?.GetValue<bool>() ?? false,
                s?["Map"]?["On"]?.GetValue<bool>() ?? true);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return new DotlineOptions(null, 70, "week", 1, false, true);   // hand-edited file with a wrong type
        }
    }

    /// <summary>
    /// Writes changed settings into Dotline's state file, leaving its goals, widgets and focus
    /// log untouched. A running Dotline is stopped first, so it can't save over the change, then
    /// started again to show it. Returns an error message, or null when saved or unchanged.
    /// </summary>
    public static string? WriteDotline(DotlineOptions o)
    {
        if (o == ReadDotline()) return null;
        // Never replace a file we couldn't read: it holds the user's goals and focus history.
        if (File.Exists(DotlineState) && LoadState() is null)
            return "Dotline's saved data couldn't be read, so its settings weren't changed.";
        var wasRunning = DotlineRunning;
        if (wasRunning) CloseDotline();

        var s = LoadState() ?? new JsonObject();
        if (o.Birthday is { } b) { s["Birthday"] = b; s["Example"] = false; }
        s["Lifespan"] = o.Lifespan;
        s["LifeUnit"] = o.LifeUnit;
        s["Glass"] = o.Background;
        s["Locked"] = o.Locked;
        if (s["Map"] is not JsonObject map) s["Map"] = map = new JsonObject();
        map["On"] = o.MapOn;

        Directory.CreateDirectory(Path.GetDirectoryName(DotlineState)!);
        Settings.WriteAtomic(DotlineState, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (wasRunning) OpenDotline();
        return null;
    }

    // ---------------------------------------------------------------- lock screen

    static readonly string LockDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockscreenWidget");
    static readonly string LockConfig = Path.Combine(LockDir, "config.json");

    /// <summary>The HUD is on the lock screen: Lockscreen keeps a backup of your picture until --restore.</summary>
    public static bool LockscreenApplied => File.Exists(Path.Combine(LockDir, "original", "backup.json"));

    static readonly string[] CornerKeys = ["topLeft", "topRight", "bottomLeft", "bottomRight"];

    /// <summary>The lock screen settings Beacon's Appearance page shows. Corners: TL, TR, BL, BR as [title, subtitle].</summary>
    public sealed record LockscreenOptions(string Theme, string[][] Corners, bool Grid, bool Scanlines, bool Vignette, bool Glow)
    {
        // Records compare arrays by reference; compare the text.
        public bool Same(LockscreenOptions o) =>
            (Theme, Grid, Scanlines, Vignette, Glow) == (o.Theme, o.Grid, o.Scanlines, o.Vignette, o.Glow) &&
            Corners.SelectMany(c => c).SequenceEqual(o.Corners.SelectMany(c => c));
    }

    static readonly string[][] DefaultCorners =
    [
        ["SYSTEM", "SECURE SHELL // v1.0"], ["{DATE}", "{WEEKDAY}"],
        ["NODE-01", "LINK: LOCAL // ENCRYPTED"], ["STATUS: SEALED", "{UPTIME_DAYS}D UPTIME"],
    ];

    /// <summary>Lockscreen's config as the form shows it; missing or odd values read as Lockscreen's defaults.</summary>
    public static LockscreenOptions ReadLockscreen()
    {
        var c = LoadJson(LockConfig);
        string Str(JsonNode? n, string fallback) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
        bool Bool(JsonNode? n) => n is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        var corners = CornerKeys.Select((k, i) => c?["corners"]?[k] is JsonArray { Count: 2 } a
            ? new[] { Str(a[0], DefaultCorners[i][0]), Str(a[1], DefaultCorners[i][1]) }
            : DefaultCorners[i].ToArray()).ToArray();
        var fx = c?["effects"];
        return new LockscreenOptions(Str(c?["theme"], "beacon"), corners,
            Bool(fx?["grid"]), Bool(fx?["scanlines"]), Bool(fx?["vignette"]), Bool(fx?["glow"]));
    }

    /// <summary>Writes the form's values into Lockscreen's config, keeping every other field. Returns an error or null.</summary>
    public static string? WriteLockscreen(LockscreenOptions o)
    {
        // Never replace a file we couldn't read: it may hold settings this page doesn't show.
        if (File.Exists(LockConfig) && LoadJson(LockConfig) is null)
            return "The lock screen's config.json couldn't be read, so it wasn't changed.";
        var c = LoadJson(LockConfig) ?? new JsonObject();
        c["theme"] = o.Theme;
        c["corners"] = new JsonObject(CornerKeys.Select((k, i) =>
            KeyValuePair.Create(k, (JsonNode?)new JsonArray(o.Corners[i][0], o.Corners[i][1]))));
        if (c["effects"] is not JsonObject fx) c["effects"] = fx = new JsonObject();
        fx["grid"] = o.Grid; fx["scanlines"] = o.Scanlines; fx["vignette"] = o.Vignette; fx["glow"] = o.Glow;
        Directory.CreateDirectory(LockDir);
        Settings.WriteAtomic(LockConfig, c.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    /// <summary>Lockscreen.exe: beside Beacon when installed, otherwise in the source tree's Release build.</summary>
    public static string? FindLockscreen()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Lockscreen.exe");
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = Path.Combine(dir.FullName, "lockscreen", "bin", "Release");
            if (!Directory.Exists(bin)) continue;
            return Directory.EnumerateFiles(bin, "Lockscreen.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    /// <summary>Runs Lockscreen.exe with the given arguments, hidden. Returns (ok, what it said).</summary>
    public static async Task<(bool Ok, string Message)> RunLockscreenAsync(params string[] args)
    {
        if (FindLockscreen() is not { } exe) return (false, "Couldn't find Lockscreen.exe next to Beacon.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        // The useful line is the last one Lockscreen prints (the first is the file it rendered).
        var said = ((await stderr).Trim() is { Length: > 0 } err ? err : (await stdout).Trim())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        var line = said.FirstOrDefault(l => !l.StartsWith("warning:") && !l.Contains(".png  ")) ?? said.LastOrDefault() ?? "";
        return (p.ExitCode == 0, line);
    }

    static JsonObject? LoadJson(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static JsonObject? LoadState()
    {
        try { return File.Exists(DotlineState) ? JsonNode.Parse(File.ReadAllText(DotlineState)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }
}
