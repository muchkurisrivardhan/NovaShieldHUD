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
public static partial class Suite
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

    static JsonObject? LoadState()
    {
        try { return File.Exists(DotlineState) ? JsonNode.Parse(File.ReadAllText(DotlineState)) as JsonObject : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }
}
