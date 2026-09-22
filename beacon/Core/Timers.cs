using System.IO;
using System.Text.Json;

namespace Beacon.Core;

/// <summary>
/// Timers and reminders. "timer 25 min", "remind me at 5 pm to call mom".
///
/// Saved to disk on every change, because a reminder that silently vanishes when the PC
/// restarts is worse than none - you stop watching the clock precisely because you set it.
/// Anything that fell due while Beacon was not running fires as soon as it starts again,
/// marked as missed rather than pretending it was on time.
/// </summary>
public static class Timers
{
    public sealed record Entry(DateTime Due, string Message);

    /// <summary>Raised on a thread-pool thread; subscribers marshal to the UI themselves.</summary>
    public static event Action<string>? Due;

    private static readonly Lock Gate = new();
    private static readonly List<(Entry Entry, Timer Timer)> Active = [];

    private static string FilePath => Path.Combine(Settings.Directory, "timers.json");

    public static IReadOnlyList<Entry> Pending
    {
        get
        {
            lock (Gate) return Active.Select(a => a.Entry).OrderBy(e => e.Due).ToList();
        }
    }

    public static void Add(DateTime due, string message)
    {
        lock (Gate)
        {
            Schedule(new Entry(due, message));
            Save();
        }
    }

    public static int CancelAll()
    {
        lock (Gate)
        {
            var count = Active.Count;
            foreach (var (_, timer) in Active) timer.Dispose();
            Active.Clear();
            Save();
            return count;
        }
    }

    /// <summary>Reloads saved timers at startup. Call once, after subscribing to <see cref="Due"/>.</summary>
    public static void Restore()
    {
        List<Entry>? saved = null;
        try
        {
            if (File.Exists(FilePath))
                saved = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
        }
        catch
        {
            // A corrupt file costs the saved reminders, not the launcher.
        }

        lock (Gate)
        {
            foreach (var entry in saved ?? [])
            {
                if (entry.Due <= DateTime.Now)
                {
                    var late = entry with { Message = $"{entry.Message} (missed at {entry.Due:t} while Beacon was off)" };
                    ThreadPool.QueueUserWorkItem(_ => Due?.Invoke(late.Message));
                }
                else
                {
                    Schedule(entry);
                }
            }

            Save();
        }
    }

    private static void Schedule(Entry entry)
    {
        // System.Threading.Timer caps a single wait at ~49 days; nobody sets a launcher
        // reminder further out than that, so clamping is simpler than chaining.
        var delay = entry.Due - DateTime.Now;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromDays(45)) delay = TimeSpan.FromDays(45);

        Timer? timer = null;
        timer = new Timer(_ =>
        {
            lock (Gate)
            {
                Active.RemoveAll(a => ReferenceEquals(a.Timer, timer));
                Save();
            }

            timer?.Dispose();
            Due?.Invoke(entry.Message);
        }, null, delay, Timeout.InfiniteTimeSpan);

        Active.Add((entry, timer));
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Directory);
            Settings.WriteAtomic(FilePath, JsonSerializer.Serialize(Active.Select(a => a.Entry).ToList()));
        }
        catch
        {
            // Read-only profile: timers still work this session, they just won't survive it.
        }
    }
}
