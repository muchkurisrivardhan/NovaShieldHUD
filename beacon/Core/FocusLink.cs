using System.IO;
using System.Text.Json;

namespace Beacon.Core;

/// <summary>
/// Reads the focus session Dotline publishes to %APPDATA%\NovaShieldHUD\focus.json, so the amber bar
/// under the command input can show it. Missing, stale or unreadable file = no session.
/// </summary>
public static class FocusLink
{
    public sealed record Session(int Seconds, DateTime EndsUtc, int? PausedLeftSeconds);

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaShieldHUD", "focus.json");

    static DateTime _stamp;
    static Session? _cached;

    public static Session? Current()
    {
        try
        {
            var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : default;
            if (stamp != _stamp)
            {
                _stamp = stamp;
                _cached = stamp == default ? null : JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _cached = null;   // Dotline mid-write or file damaged; try again next tick
            _stamp = default;
        }

        var s = _cached;
        if (s is null || s.Seconds <= 0) return null;
        if (s.PausedLeftSeconds is null && s.EndsUtc <= DateTime.UtcNow) return null;   // finished
        return s;
    }

    /// <summary>Seconds left in the session (frozen while paused).</summary>
    public static double SecondsLeft(Session s) =>
        s.PausedLeftSeconds ?? Math.Max(0, (s.EndsUtc - DateTime.UtcNow).TotalSeconds);
}
