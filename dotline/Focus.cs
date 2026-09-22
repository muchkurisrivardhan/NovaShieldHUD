using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Dotline;

public sealed class FocusLog
{
    public DateTime Start { get; set; }
    public int Minutes { get; set; }
}

public sealed class FocusState
{
    public int Seconds { get; set; } = 25 * 60;     // length of the current or next session, up to 24 h
    public int CustomSeconds { get; set; }          // the last custom length chosen; 0 = never set
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Minutes { get; set; }                // v1 stored the length in minutes; read once, see Hub.Start
    public DateTime? EndsUtc { get; set; }          // set while running
    public int? PausedLeftSeconds { get; set; }     // set while paused
    public DateTime? StartedUtc { get; set; }
    public List<FocusLog> Log { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public bool Active => EndsUtc is not null || PausedLeftSeconds is not null;
    [System.Text.Json.Serialization.JsonIgnore] public bool Paused => PausedLeftSeconds is not null;

    [System.Text.Json.Serialization.JsonIgnore] public double SecondsLeft => PausedLeftSeconds ?? (EndsUtc is DateTime e ? Math.Max(0, (e - DateTime.UtcNow).TotalSeconds) : Seconds);
}

/// <summary>
/// The focus timer. Runs on the Hub's one-second tick whether or not a widget shows it, logs
/// finished sessions, and publishes the live session to %APPDATA%\Vantage\focus.json for Beacon.
/// </summary>
static class FocusTimer
{
    static FocusState F => Hub.S.Focus;

    static readonly string SharedFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vantage", "focus.json");

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);

    public const int MaxSeconds = 24 * 60 * 60;

    /// <summary>The fixed lengths offered beside CUSTOM: 15, 25, 50 and 90 minutes.</summary>
    public static readonly int[] Presets = [15 * 60, 25 * 60, 50 * 60, 90 * 60];

    public static void SetLength(int seconds)
    {
        if (F.Active) return;
        F.Seconds = Math.Clamp(seconds, 1, MaxSeconds);
        Changed();
    }

    /// <summary>Use a custom length and remember it as the CUSTOM choice.</summary>
    public static void SetCustom(int seconds)
    {
        if (F.Active) return;
        F.CustomSeconds = Math.Clamp(seconds, 1, MaxSeconds);
        SetLength(F.CustomSeconds);
    }

    public static void Start()
    {
        F.StartedUtc = DateTime.UtcNow;
        F.EndsUtc = DateTime.UtcNow.AddSeconds(F.Seconds);
        F.PausedLeftSeconds = null;
        Changed();
    }

    public static void Pause()
    {
        if (F.EndsUtc is null) return;
        F.PausedLeftSeconds = (int)Math.Ceiling(F.SecondsLeft);
        F.EndsUtc = null;
        Changed();
    }

    public static void Resume()
    {
        if (F.PausedLeftSeconds is not int left) return;
        F.EndsUtc = DateTime.UtcNow.AddSeconds(left);
        F.PausedLeftSeconds = null;
        Changed();
    }

    public static void AddFive()
    {
        if (!F.Active) return;
        var add = Math.Min(300, MaxSeconds - F.Seconds);
        if (add <= 0) return;
        F.Seconds += add;
        if (F.EndsUtc is DateTime e) F.EndsUtc = e.AddSeconds(add);
        else F.PausedLeftSeconds += add;
        Changed();
    }

    /// <summary>Stop early; the minutes actually spent still count if there was at least one.</summary>
    public static void Stop()
    {
        var spent = (int)((F.Seconds - F.SecondsLeft) / 60);
        if (spent >= 1) Record(spent);
        Clear();
    }

    /// <summary>Called every second by the Hub.</summary>
    public static void Tick()
    {
        if (F.EndsUtc is DateTime e && DateTime.UtcNow >= e)
        {
            Record((int)Math.Round(F.Seconds / 60.0));
            Clear();
            MessageBeep(0x40);   // MB_ICONASTERISK: the system "information" sound
            Hub.FocusFinished();
        }
    }

    static void Record(int minutes)
    {
        F.Log.Add(new FocusLog { Start = (F.StartedUtc ?? DateTime.UtcNow).ToLocalTime(), Minutes = minutes });
        // ponytail: keep ~a year of history; the widget only shows the last week.
        if (F.Log.Count > 2000) F.Log.RemoveRange(0, F.Log.Count - 2000);
    }

    static void Clear()
    {
        F.EndsUtc = null;
        F.PausedLeftSeconds = null;
        F.StartedUtc = null;
        Changed();
    }

    static void Changed()
    {
        Hub.Save();
        Publish();
    }

    /// <summary>Beacon reads this; Seconds = 0 means no session.</summary>
    public static void Publish()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SharedFile)!);
            var tmp = SharedFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new
            {
                Seconds = F.Active ? F.Seconds : 0,
                EndsUtc = F.EndsUtc ?? DateTime.UtcNow,
                F.PausedLeftSeconds,
            }));
            File.Move(tmp, SharedFile, overwrite: true);
        }
        catch (IOException) { }   // Beacon just shows the day bar until the next write
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>"25 MIN", "1 H 30 MIN", "12 MIN 30 SEC", "24 H".</summary>
    public static string Length(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        if (t.TotalHours >= 1) parts.Add($"{(int)t.TotalHours} H");
        if (t.Minutes > 0) parts.Add($"{t.Minutes} MIN");
        if (t.Seconds > 0) parts.Add($"{t.Seconds} SEC");
        return string.Join(" ", parts);
    }

    public static (int count, int minutes) Day(DateTime day)
    {
        var logs = F.Log.Where(l => l.Start.Date == day.Date).ToList();
        return (logs.Count, logs.Sum(l => l.Minutes));
    }
}
