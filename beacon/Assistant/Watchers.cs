using System.Text.RegularExpressions;
using Beacon.Core;

namespace Beacon.Assistant;

/// <summary>A button on a watcher's card. Runs only when the user clicks it.</summary>
public sealed record WatcherOffer(string Label, Action Run, bool Primary = false);

/// <summary>What a watcher wants to tell the user: shown as a card, never acted on by itself.</summary>
/// <param name="Silent">No sound: the user is busy or it's quiet hours.</param>
public sealed record WatcherAlert(string Kind, string Title, string Body, IReadOnlyList<WatcherOffer> Offers, bool Silent);

/// <summary>Everything the watchers read from the PC, swappable so the logic can be tested with a fake clock.</summary>
public sealed class WatcherSignals
{
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Func<string?> Busy { get; init; } = Interop.UserPresence.BusyReason;
    public Func<TimeSpan> Idle { get; init; } = Interop.UserPresence.Idle;
    /// <summary>A Dotline focus session is running (not paused).</summary>
    public Func<bool> Focusing { get; init; } = () => FocusLink.Current() is { PausedLeftSeconds: null };
    public Func<IReadOnlyList<Timers.Entry>> Reminders { get; init; } = () => Timers.Pending;
    /// <summary>Unread count and senders, or null when email isn't set up. May throw when the server can't be reached.</summary>
    public Func<CancellationToken, Task<(int Count, IReadOnlyList<string> From)?>> Mail { get; init; } =
        _ => Task.FromResult<(int, IReadOnlyList<string>)?>(null);
    public Action<DateTime, string> AddReminder { get; init; } = Timers.Add;
    public Action<Settings> Save { get; init; } = s => s.Save();
}

/// <summary>
/// Nova acting first: things it tells the user without being asked. Two watchers for now:
/// <list type="bullet">
/// <item>Reminders: a reminder or timer that falls due comes as Nova's card instead of a tray balloon.</item>
/// <item>Morning briefing: the first time the user is at the PC after the set time, a card with
/// the date, today's reminders and unread email.</item>
/// </list>
///
/// Watchers have no tools. They can only show a card; everything on it (Tell me, Snooze, Go
/// through my email) runs only when the user clicks it, and then enters Nova as the user's own
/// request, through Sentinel. So nothing a watcher reads, such as a sender name written by a
/// stranger, can make Nova act.
///
/// Quiet hours (night, a full-screen app or game, a presentation, a locked PC, a Dotline focus
/// session) hold the briefing until the user is free. The user's own reminders still come
/// through, silently: they set that time on purpose. With watchers off or paused, reminders
/// fall back to the tray balloon, so one is never lost.
/// </summary>
public sealed partial class WatcherHub(Settings settings, WatcherSignals signals, AuditLog log,
    Action<WatcherAlert> show, Action<string> announce, Action<string> ask)
{
    DateTime _pausedUntil;
    bool _briefing;

    public bool Paused => signals.Now() < _pausedUntil;
    public DateTime PausedUntil => _pausedUntil;

    public void Pause(TimeSpan span)
    {
        _pausedUntil = signals.Now() + span;
        Record("pause", $"watchers paused until {_pausedUntil:h:mm tt}", "You", "");
    }

    public void Resume() => _pausedUntil = default;

    // ------------------------------------------------------------------ quiet

    /// <summary>Why Nova should stay quiet now, in words, or null when it may speak up.</summary>
    public string? QuietReason()
    {
        var now = TimeOnly.FromDateTime(signals.Now());
        if (Clock(settings.QuietFrom) is { } from && Clock(settings.QuietTo) is { } to && InWindow(now, from, to))
            return $"it's quiet hours ({settings.QuietFrom} to {settings.QuietTo})";
        if (signals.Busy() is { } busy) return busy;
        if (signals.Focusing()) return "you're in a focus session";
        return null;
    }

    /// <summary>Is <paramref name="now"/> in [from, to)? The window may run past midnight.</summary>
    public static bool InWindow(TimeOnly now, TimeOnly from, TimeOnly to) =>
        from != to && (from < to ? now >= from && now < to : now >= from || now < to);

    /// <summary>"08:30" or "8:30" to a time; null when it isn't one.</summary>
    public static TimeOnly? Clock(string? text) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), ["HH:mm", "H:mm"], out var t) ? t : null;

    // ------------------------------------------------------------------ reminders

    /// <summary>
    /// A reminder fell due. False when watchers don't handle it (off, paused, or reminders turned
    /// off), so the caller shows the old tray balloon instead.
    /// </summary>
    public bool OnReminder(string message)
    {
        if (!settings.WatchersEnabled || !settings.WatchReminders || Paused) return false;
        var quiet = QuietReason();
        show(new WatcherAlert("reminder", "Reminder", message,
        [
            Offer("reminder", "Tell me", () => announce("Reminder: " + message), primary: true),
            Offer("reminder", "Snooze 10 min", () => signals.AddReminder(signals.Now().AddMinutes(10), message)),
            new WatcherOffer("Done", () => { }),
        ], Silent: quiet is not null));
        Record("reminder", "showed a reminder: " + message, "Watcher", quiet is null ? "" : "silently, because " + quiet);
        return true;
    }

    // ------------------------------------------------------------------ briefing

    /// <summary>Called every half minute: shows the morning briefing once it's due.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (_briefing || !settings.WatchersEnabled || !settings.WatchBriefing || Paused) return;
        var now = signals.Now();
        if (!BriefingDue(now, settings.BriefingTime, settings.LastBriefingDay, signals.Idle(), QuietReason())) return;

        _briefing = true;
        try
        {
            // Marked first: even if the inbox is slow, there's only ever one briefing a day.
            settings.LastBriefingDay = now.ToString("yyyy-MM-dd");
            signals.Save(settings);

            (int Count, IReadOnlyList<string> From)? mail = null;
            var mailFailed = false;
            try { mail = await signals.Mail(ct); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { mailFailed = true; }

            var greeting = Greeting(now);
            var body = ComposeBriefing(now, signals.Reminders(), mail, mailFailed);
            var offers = new List<WatcherOffer> { Offer("briefing", "Read it to me", () => announce(greeting + ". " + body), primary: true) };
            if (mail is { Count: > 0 }) offers.Add(Offer("briefing", "Go through my email", () => ask("check my email")));
            offers.Add(new WatcherOffer("Done", () => { }));
            show(new WatcherAlert("briefing", greeting, body, offers, Silent: false));
            Record("briefing", "showed the morning briefing", "Watcher", "");
        }
        finally
        {
            _briefing = false;
        }
    }

    /// <summary>
    /// Due when: not shown today, it's between the set time and five hours after (a "morning"
    /// briefing at 4 pm would be odd), the user is at the PC (input in the last 2 minutes), and
    /// nothing says to stay quiet.
    /// </summary>
    public static bool BriefingDue(DateTime now, string time, string lastDay, TimeSpan idle, string? quiet)
    {
        if (Clock(time) is not { } at) return false;
        return lastDay != now.ToString("yyyy-MM-dd")
               && InWindow(TimeOnly.FromDateTime(now), at, at.AddHours(5))
               && idle < TimeSpan.FromMinutes(2)
               && quiet is null;
    }

    public static string Greeting(DateTime now) =>
        now.Hour < 12 ? "Good morning" : now.Hour < 17 ? "Good afternoon" : "Good evening";

    /// <summary>The briefing in a few spoken sentences. Sender names are a stranger's text: shortened and cleaned.</summary>
    public static string ComposeBriefing(DateTime now, IReadOnlyList<Timers.Entry> reminders,
        (int Count, IReadOnlyList<string> From)? mail, bool mailFailed)
    {
        var parts = new List<string> { $"It's {now:dddd d MMMM}." };
        var today = reminders.Where(r => r.Due.Date == now.Date && r.Due >= now).OrderBy(r => r.Due).ToList();
        parts.Add(today.Count switch
        {
            0 => "No reminders for the rest of today.",
            1 => $"One reminder today: {Line(today[0])}.",
            _ => $"{today.Count} reminders today: " + string.Join("; ", today.Take(4).Select(Line))
                 + (today.Count > 4 ? $"; and {today.Count - 4} more" : "") + ".",
        });
        if (mailFailed) parts.Add("I couldn't reach your inbox.");
        else if (mail is { } m)
            parts.Add(m.Count == 0 ? "No unread email." : $"{m.Count} unread email{(m.Count == 1 ? "" : "s")}, from {Names(m.From)}.");
        return string.Join(" ", parts);
    }

    static string Line(Timers.Entry e) => $"{e.Due:h:mm tt}, {Clean(e.Message, 80)}";

    static string Names(IReadOnlyList<string> from)
    {
        var names = from.Select(n => Clean(n, 40)).Where(n => n.Length > 0).Distinct().Take(3).ToList();
        return names.Count switch
        {
            0 => "someone",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    /// <summary>Control characters out, whitespace folded, cut to length.</summary>
    internal static string Clean(string text, int max)
    {
        var t = Spaces().Replace(new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()), " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // ------------------------------------------------------------------ audit

    /// <summary>A button that's recorded when clicked: the user's own action.</summary>
    WatcherOffer Offer(string kind, string label, Action run, bool primary = false) =>
        new(label, () => { Record(kind, $"clicked \"{label}\" on the {kind} card", "You", ""); run(); }, primary);

    void Record(string tool, string action, string actor, string note) =>
        log.Add(new AuditEntry(signals.Now(), actor, tool, action, actor == "You" ? "on this PC" : "read",
            actor == "You" ? "Ran" : "Shown", note));
}
