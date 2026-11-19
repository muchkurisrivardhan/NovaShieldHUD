using System.Text.RegularExpressions;
using Beacon.Core;
using Beacon.Providers;

namespace Beacon.Nl;

/// <summary>
/// Turns a spoken or typed sentence into commands, on this machine, with no network call.
///
/// Why rules rather than a model: the phrasings people actually use on a launcher are a small,
/// closed set - open a thing, search a thing somewhere, set a level, close things, power off.
/// Matching those with rules answers in microseconds, works offline, costs nothing, and is
/// predictable: the same sentence always does the same thing. Anything the rules cannot place
/// is handed to Claude instead, which is where the open-ended phrasings belong.
///
/// The parser only ever *describes* work. It resolves nothing, launches nothing, and touches
/// no state - which is what makes it safe to run on every keystroke.
/// </summary>
public sealed class NlParser
{
    private readonly CommandRegistry _registry;
    private readonly AppIndex _apps;
    private readonly Settings _settings;

    public NlParser(CommandRegistry registry, AppIndex apps, Settings settings)
    {
        _registry = registry;
        _apps = apps;
        _settings = settings;
    }

    // Words that make a phrase an instruction rather than a search term. Without this gate,
    // typing "chrome bookmarks backup" would be read as a command instead of a file search.
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "open", "launch", "start", "run", "execute", "go", "goto", "bring", "fire", "show",
        "close", "quit", "exit", "kill", "end", "shut", "shutdown", "restart", "reboot",
        "lock", "sleep", "suspend", "hibernate", "sign", "log", "logoff", "logout",
        "set", "turn", "put", "make", "change", "adjust", "increase", "decrease", "raise",
        "lower", "dim", "brighten", "mute", "unmute", "silence",
        "search", "find", "look", "locate", "where", "google",
        "play", "pause", "resume", "stop", "skip", "next", "previous",
        "snap", "maximize", "maximise", "minimize", "minimise", "restore", "move", "list",
        "empty", "clear", "clean", "rebuild", "refresh", "reindex",
        "connect", "disconnect", "enable", "disable", "switch", "toggle", "jump", "cancel",
        "volume", "brightness", "wifi", "bluetooth",
        "remind", "timer", "alarm", "countdown",
    };

    /// <summary>
    /// Whether the text reads like an order rather than a name. Used by the Claude fallback
    /// to decide how prominently to offer itself when the rules here came up empty.
    /// </summary>
    public static bool LooksLikeCommandSentence(string raw)
    {
        var words = Normalize(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && Verbs.Contains(words[0]);
    }

    /// <summary>Null when this is not a sentence Beacon understands - the common case.</summary>
    public NlUnderstanding? Parse(string raw)
    {
        var text = Normalize(raw);
        if (text.Length < 3) return null;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;   // single words stay instant search

        // A custom command's exact name wins outright: "start work" is the user's own
        // workspace, even though "start" would otherwise read as "launch an app called work".
        var own = _settings.CustomCommands.FirstOrDefault(c =>
            string.Equals(c.Name.Trim(), text, StringComparison.OrdinalIgnoreCase));
        if (own is not null)
            return Plan([new NlStep(CustomStepId + own.Id, [], $"run “{own.Name}”")], 1);

        // Split first: "open notepad and search for cats on google" is two instructions, and
        // reading the whole line as one site search would swallow the first half.
        var fragments = SplitSteps(text);

        if (fragments.Count == 1)
        {
            // "latest telugu songs on youtube" and "25 minute timer" name no verb up front,
            // so they are tried before the verb gate below.
            if (SiteSearch(text) is { } siteStep) return Plan([siteStep], 1);
            if (Timer(text) is { } timerStep) return Plan([timerStep], 1);

            if (!Verbs.Contains(words[0])) return null;

            // A description of something to look for, rather than something to do.
            if (Find(text) is { } find) return find;
        }
        else if (!Verbs.Contains(words[0]) && SiteSearch(fragments[0]) is null)
        {
            return null;
        }

        var steps = new List<NlStep>();
        var confidence = 1d;

        foreach (var fragment in fragments)
        {
            var (step, stepConfidence) = Step(fragment);

            // All or nothing: half-understanding "mute and shut down" would be worse than
            // admitting we did not follow it and offering Claude.
            if (step is null)
            {
                // Except for a lone "open <something we have never heard of>", which is
                // better served by searching for it than by shrugging.
                return fragments.Count == 1 ? OpenAsSearch(fragment) : null;
            }

            steps.Add(step);
            confidence = Math.Min(confidence, stepConfidence);
        }

        return Plan(steps, confidence);
    }

    private NlUnderstanding Plan(IReadOnlyList<NlStep> steps, double confidence) => new()
    {
        Kind = NlKind.Actions,
        Summary = string.Join(", then ", steps.Select(s => s.Description)),
        Steps = steps,
        Confidence = confidence,
        IsDestructive = steps.Any(s => _registry.Find(s.CommandId)?.IsDestructive == true),
    };

    // ------------------------------------------------------------------ one step

    private (NlStep? Step, double Confidence) Step(string f)
    {
        // Order matters: the specific readings have to win. "turn off wifi" and "turn off the
        // volume" both start the same way, and neither means "turn off the computer".
        var step =
            Timer(f) ??
            SiteSearch(f) ??
            Volume(f) ??
            Brightness(f) ??
            Radios(f) ??
            Power(f) ??
            CloseAll(f) ??
            Kill(f) ??
            Window(f) ??
            Media(f) ??
            WebSearch(f) ??
            Open(f);

        if (step is not null) return (step, 1);

        // Finally: anything in the command list or the user's own commands, by name.
        return NamedCommand(f);
    }

    // --------------------------------------------------------------- site search

    private static readonly Regex TermThenSite = new(
        @"^(?:search|find|look\s*up|look\s*for|play|watch|listen\s*to|stream|show(?:\s*me)?|get|open)?\s*" +
        @"(?:for\s+)?(?<term>.+?)\s+(?:on|in|at|from|using|with)\s+(?<site>[a-z0-9 .]{2,25})$",
        RegexOptions.Compiled);

    private static readonly Regex SiteThenTerm = new(
        @"^(?:open|go\s*to|launch|start|search|find)\s+(?<site>[a-z0-9 .]{2,25}?)\s+" +
        @"(?:and\s+)?(?:search|look|find|play|watch|for)\s*(?:for\s+)?(?<term>.+)$",
        RegexOptions.Compiled);

    /// <summary>"mr beast on youtube", "open youtube and search for mr beast".</summary>
    private static NlStep? SiteSearch(string f)
    {
        foreach (var pattern in new[] { SiteThenTerm, TermThenSite })
        {
            var m = pattern.Match(f);
            if (!m.Success) continue;

            var site = SiteCatalog.FindByWords(m.Groups["site"].Value);
            var term = Clean(m.Groups["term"].Value);
            if (site is null || term.Length == 0) continue;

            if (!site.CanSearch)
            {
                return new NlStep("open_website", new() { ["site"] = site.Name },
                    $"open {site.Name} ({site.Name} has no search)");
            }

            return new NlStep(
                "web_search",
                new() { ["query"] = term, ["engine"] = site.Name },
                $"search {site.Name} for “{term}”");
        }

        return null;
    }

    // -------------------------------------------------------------------- timers

    private static readonly Regex ReminderPattern = new(
        @"^remind\s+me\s+(?:" +
        @"(?<when>(?:in|at|after|tomorrow)\b.+?)\s+(?:to|about|that)\s+(?<what>.+)" +
        @"|(?:to|about|that)\s+(?<what>.+?)\s+(?<when>(?:in|at|after|tomorrow)\b.+)" +
        @"|(?<when>(?:in|at|after|tomorrow)\b.+))$",
        RegexOptions.Compiled);

    /// <summary>
    /// "remind me in 10 minutes to call mom", "remind me to stretch at 4 pm",
    /// "timer 25 min", "set a timer for an hour", "5 minute timer".
    /// </summary>
    private static NlStep? Timer(string f)
    {
        var reminder = ReminderPattern.Match(f);
        if (reminder.Success)
        {
            var due = When(reminder.Groups["when"].Value);
            if (due is null) return null;

            var what = Clean(reminder.Groups["what"].Value);
            if (what.Length == 0) what = "Reminder";

            // Spoken reminders arrive in the first person; the notification speaks to you.
            what = Regex.Replace(what, @"\bmy\b", "your");
            var message = char.ToUpperInvariant(what[0]) + what[1..];

            return TimerStep(due.Value, message, $"remind you at {due.Value:t} to {what}");
        }

        if (!Regex.IsMatch(f, @"\b(timer|alarm|countdown)\b")) return null;

        var when = When(f);

        // "timer 25" - a bare number on a timer means minutes.
        if (when is null && Regex.Match(f, @"\b(\d{1,3})\b") is { Success: true } bare)
            when = DateTime.Now.AddMinutes(int.Parse(bare.Groups[1].Value));

        if (when is null) return null;

        var span = when.Value - DateTime.Now;
        var label = ClockTime.IsMatch(f)
            ? $"set an alarm for {when.Value:t}"
            : $"set a timer for {CommandRegistry.Describe(Round(span))}";

        return TimerStep(when.Value, message: "", label);
    }

    private static NlStep TimerStep(DateTime due, string message, string description)
    {
        var seconds = Math.Max(1, (int)Math.Round((due - DateTime.Now).TotalSeconds));
        var args = new Dictionary<string, string> { ["seconds"] = seconds.ToString() };
        if (message.Length > 0) args["message"] = message;

        return new NlStep("set_timer", args, description);
    }

    /// <summary>Parsing takes a moment; don't let "25 minutes" read as "24 minutes 59 seconds".</summary>
    private static TimeSpan Round(TimeSpan span) => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds));

    // "at 5", "at 5:30 pm" - and "for 7:30 am", but only with a colon or am/pm, so that
    // "timer for 5" stays a five-minute timer rather than becoming an alarm at five o'clock.
    private static readonly Regex ClockTime = new(
        @"\b(?:at\s+(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>[ap]\.?\s?m\.?)?" +
        @"|for\s+(?<h>\d{1,2})(?:(?::(?<m>\d{2}))\s*(?<ampm>[ap]\.?\s?m\.?)?|\s*(?<ampm>[ap]\.?\s?m\.?)))(?!\w)",
        RegexOptions.Compiled);

    // Digits may touch their unit ("5m"); a spelled amount may not, or the "a" and "m" of
    // "7:30 am" read as "a minute".
    private static readonly Regex DurationPart = new(
        @"(?:(?<n>\d+(?:\.\d+)?)\s*" +
        @"|\b(?<n>an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty|forty-five|fifty|sixty)\s+)" +
        @"(?<u>hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)\b",
        RegexOptions.Compiled);

    /// <summary>A moment in time from "in 10 minutes", "at 5:30 pm", "tomorrow at 9", "for an hour".</summary>
    private static DateTime? When(string text)
    {
        var now = DateTime.Now;

        var clock = ClockTime.Match(text);
        if (clock.Success)
        {
            var hour = int.Parse(clock.Groups["h"].Value);
            var minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value) : 0;
            var ampm = clock.Groups["ampm"].Value.Replace(".", "").Replace(" ", "");

            if (ampm == "pm" && hour < 12) hour += 12;
            if (ampm == "am" && hour == 12) hour = 0;
            if (hour > 23 || minute > 59) return null;

            var at = now.Date.AddHours(hour).AddMinutes(minute);

            // "at 9" with no am/pm said at 2 pm almost certainly means 9 pm, not tomorrow morning.
            if (ampm.Length == 0 && hour < 12 && at <= now && at.AddHours(12) > now) at = at.AddHours(12);
            if (at <= now || text.Contains("tomorrow", StringComparison.Ordinal)) at = at.AddDays(1);

            return at;
        }

        if (Regex.IsMatch(text, @"\bhalf\s+an?\s+hour\b|\ba\s+half\s+hour\b")) return now.AddMinutes(30);

        var total = TimeSpan.Zero;
        foreach (Match part in DurationPart.Matches(text))
        {
            var amount = Amount(part.Groups["n"].Value);
            if (amount is null) continue;

            total += part.Groups["u"].Value[0] switch
            {
                'h' => TimeSpan.FromHours(amount.Value),
                'm' => TimeSpan.FromMinutes(amount.Value),
                _ => TimeSpan.FromSeconds(amount.Value),
            };
        }

        if (Regex.IsMatch(text, @"\band\s+a\s+half\b") && total > TimeSpan.Zero)
            total += total >= TimeSpan.FromHours(1) ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30);

        return total > TimeSpan.Zero ? now + total : null;

        static double? Amount(string word) =>
            double.TryParse(word, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
            : word is "a" or "an" ? 1
            : word == "forty-five" ? 45
            : NumberWords.TryGetValue(word, out var v) ? v
            : null;
    }

    // -------------------------------------------------------------------- levels

    private static readonly Regex VolumeWord = new(@"\b(volume|sound|audio)\b", RegexOptions.Compiled);
    private static readonly Regex BrightnessWord = new(@"\b(brightness|screen|display|backlight)\b", RegexOptions.Compiled);

    private static NlStep? Volume(string f)
    {
        var mentionsVolume = VolumeWord.IsMatch(f);

        if (Regex.IsMatch(f, @"\b(unmute|un-mute)\b"))
            return new NlStep("set_mute", new() { ["state"] = "off" }, "unmute");

        if (Regex.IsMatch(f, @"\b(mute|silence)\b"))
            return new NlStep("set_mute", new() { ["state"] = "on" }, "mute the audio");

        if (!mentionsVolume) return null;

        if (Level(f) is { } percent)
        {
            return new NlStep("set_volume_level", new() { ["percent"] = percent.ToString() },
                $"set volume to {percent}%");
        }

        if (Regex.IsMatch(f, @"\b(up|louder|raise|increase|higher)\b"))
            return new NlStep("set_volume", new() { ["direction"] = "up", ["steps"] = "5" }, "turn the volume up");

        if (Regex.IsMatch(f, @"\b(down|quieter|lower|decrease|reduce)\b"))
            return new NlStep("set_volume", new() { ["direction"] = "down", ["steps"] = "5" }, "turn the volume down");

        // "turn off the volume" is mute, not shutdown.
        if (Regex.IsMatch(f, @"\b(off|zero)\b"))
            return new NlStep("set_mute", new() { ["state"] = "on" }, "mute the audio");

        return null;
    }

    private static NlStep? Brightness(string f)
    {
        var mentionsBrightness = BrightnessWord.IsMatch(f);
        var dim = Regex.IsMatch(f, @"\b(dim|darker|darken)\b");
        var brighten = Regex.IsMatch(f, @"\b(brighten|brighter)\b");

        if (!mentionsBrightness && !dim && !brighten) return null;

        // "screen" alone could be about a window; require a brightness verb with it.
        if (!Regex.IsMatch(f, @"\bbrightness|backlight\b") && !dim && !brighten) return null;

        if (Level(f) is { } percent)
        {
            return new NlStep("set_brightness", new() { ["percent"] = percent.ToString() },
                $"set brightness to {percent}%");
        }

        if (brighten || Regex.IsMatch(f, @"\b(up|raise|increase|higher)\b"))
            return new NlStep("adjust_brightness", new() { ["direction"] = "up" }, "turn the brightness up");

        if (dim || Regex.IsMatch(f, @"\b(down|lower|decrease|reduce)\b"))
            return new NlStep("adjust_brightness", new() { ["direction"] = "down" }, "dim the screen");

        return null;
    }

    // -------------------------------------------------------------------- radios

    private static NlStep? Radios(string f)
    {
        if (Regex.IsMatch(f, @"\bnight\s*light\b"))
            return new NlStep("night_light_settings", [], "open night light settings");

        if (Regex.IsMatch(f, @"\bbluetooth\b"))
            return new NlStep("bluetooth_settings", [], "open Bluetooth settings");

        if (!Regex.IsMatch(f, @"\b(wi\s*-?\s*fi|wifi|wireless)\b")) return null;

        var off = Regex.IsMatch(f, @"\b(off|disconnect|disable|stop)\b");

        return new NlStep(
            "wifi_connection",
            new() { ["state"] = off ? "off" : "on" },
            off ? "disconnect Wi-Fi" : "reconnect Wi-Fi");
    }

    // --------------------------------------------------------------------- power

    private static NlStep? Power(string f)
    {
        var machine = Regex.IsMatch(f, @"\b(pc|computer|laptop|machine|system|everything)\b");

        if (Regex.IsMatch(f, @"\b(shut\s*down|shutdown|power\s*off)\b") ||
            (machine && Regex.IsMatch(f, @"\bturn\s+(it\s+)?off\b")))
        {
            return new NlStep("shutdown", [], "shut down the PC");
        }

        if (Regex.IsMatch(f, @"\b(restart|reboot)\b"))
            return new NlStep("restart", [], "restart the PC");

        if (Regex.IsMatch(f, @"\b(sign\s*out|signout|log\s*off|logoff|log\s*out|logout)\b"))
            return new NlStep("sign_out", [], "sign out");

        if (Regex.IsMatch(f, @"\block\b") && !Regex.IsMatch(f, @"\block\s*screen\s*(wallpaper|image)\b"))
            return new NlStep("lock_workstation", [], "lock the PC");

        if (Regex.IsMatch(f, @"\b(sleep|suspend|hibernate)\b"))
            return new NlStep("sleep", [], "put the PC to sleep");

        if (Regex.IsMatch(f, @"\b(recycle\s*bin|trash)\b") && Regex.IsMatch(f, @"\b(empty|clear|clean)\b"))
            return new NlStep("empty_recycle_bin", [], "empty the Recycle Bin");

        return null;
    }

    private static NlStep? CloseAll(string f)
    {
        if (!Regex.IsMatch(f, @"\b(close|quit|exit|shut)\b")) return null;

        if (Regex.IsMatch(f, @"\b(everything|all|every)\b") &&
            Regex.IsMatch(f, @"\b(everything|apps?|applications?|windows?|programs?|tabs?)\b"))
        {
            return new NlStep("close_all_windows", [], "close all apps");
        }

        return null;
    }

    private static readonly Regex KillPattern = new(
        @"^(?:kill|force\s*(?:close|quit|stop)|end\s*task(?:\s*for)?|terminate)\s+(?<name>.+)$",
        RegexOptions.Compiled);

    private static NlStep? Kill(string f)
    {
        var m = KillPattern.Match(f);
        if (!m.Success) return null;

        var name = Clean(m.Groups["name"].Value)
            .Replace("the ", "", StringComparison.Ordinal)
            .Replace(" process", "", StringComparison.Ordinal)
            .Trim();

        if (name.Length == 0 || name is "all" or "everything") return null;

        return new NlStep("kill_process", new() { ["name"] = name }, $"force-close {name}");
    }

    // ------------------------------------------------------------------- windows

    private static readonly Regex SwitchPattern = new(
        @"^(?:switch|jump|go\s+back)\s+to\s+(?:the\s+)?(?<name>.+?)(?:\s+window)?$", RegexOptions.Compiled);

    private static NlStep? Window(string f)
    {
        var switchTo = SwitchPattern.Match(f);
        if (switchTo.Success)
        {
            var name = Clean(switchTo.Groups["name"].Value);
            return new NlStep("switch_to_window", new() { ["name"] = name }, $"switch to {name}");
        }

        if (Regex.IsMatch(f, @"\b(next|second|other|another)\s+(monitor|screen|display)\b"))
            return new NlStep("move_window_next_monitor", [], "move the window to the next monitor");

        if (Regex.IsMatch(f, @"\b(list|show|what'?s)\b") && Regex.IsMatch(f, @"\b(windows|open apps?)\b"))
            return new NlStep("list_windows", [], "list open windows");

        var zone = ZoneIn(f);
        if (zone is not null && Regex.IsMatch(f, @"\b(snap|move|put|pin|dock|send|place)\b"))
            return new NlStep("snap_window", new() { ["zone"] = zone }, $"snap the window {zone}");

        var thisWindow = Regex.IsMatch(f, @"\b(window|this|it)\b");

        if (Regex.IsMatch(f, @"\b(maximi[sz]e|full\s*screen)\b"))
            return new NlStep("maximize_window", [], "maximize the window");

        if (Regex.IsMatch(f, @"\bminimi[sz]e\b"))
            return new NlStep("minimize_window", [], "minimize the window");

        if (Regex.IsMatch(f, @"\brestore\b") && thisWindow)
            return new NlStep("restore_window", [], "restore the window");

        if (Regex.IsMatch(f, @"^close\b") && thisWindow)
            return new NlStep("close_window", [], "close the window");

        return null;
    }

    private static string? ZoneIn(string f)
    {
        if (Regex.IsMatch(f, @"\btop\s*left\b")) return "top-left";
        if (Regex.IsMatch(f, @"\btop\s*right\b")) return "top-right";
        if (Regex.IsMatch(f, @"\bbottom\s*left\b")) return "bottom-left";
        if (Regex.IsMatch(f, @"\bbottom\s*right\b")) return "bottom-right";
        if (Regex.IsMatch(f, @"\b(left|left\s*half)\b")) return "left";
        if (Regex.IsMatch(f, @"\b(right|right\s*half)\b")) return "right";
        if (Regex.IsMatch(f, @"\b(cent(er|re))\b")) return "center";
        return null;
    }

    // --------------------------------------------------------------------- media

    private static NlStep? Media(string f)
    {
        if (Regex.IsMatch(f, @"^(next|skip)\b.*\b(track|song|video)\b") ||
            Regex.IsMatch(f, @"^(next|skip)\s+(track|song)$"))
        {
            return new NlStep("media_next", [], "skip to the next track");
        }

        if (Regex.IsMatch(f, @"^(previous|prev|last|go\s*back)\b.*\b(track|song)\b"))
            return new NlStep("media_previous", [], "go to the previous track");

        // Only a bare "play"/"pause" - "play X on youtube" was handled long before here.
        if (Regex.IsMatch(f, @"^(play|pause|resume|stop)(\s+(the\s+)?(music|song|video|media|playback|track|it|this))?$"))
            return new NlStep("media_play_pause", [], "toggle playback");

        return null;
    }

    // ---------------------------------------------------------------- web search

    private static readonly Regex ExplicitWebSearch = new(
        @"^(?:google|web\s*search|search\s+the\s+web)\s+(?:for\s+)?(?<q>.+)$", RegexOptions.Compiled);

    private static NlStep? WebSearch(string f)
    {
        var m = ExplicitWebSearch.Match(f);
        if (!m.Success) return null;

        var term = Clean(m.Groups["q"].Value);
        return term.Length == 0
            ? null
            : new NlStep("web_search", new() { ["query"] = term }, $"search Google for “{term}”");
    }

    // ---------------------------------------------------------------------- open

    private static readonly Regex OpenPattern = new(
        @"^(?:open|launch|start|run|go\s*to|bring\s*up|fire\s*up)\s+(?<what>.+)$", RegexOptions.Compiled);

    private NlStep? Open(string f)
    {
        var m = OpenPattern.Match(f);
        if (!m.Success) return null;

        var what = Regex.Replace(Clean(m.Groups["what"].Value),
            @"\b(the|a|my|app|application|program|website|site|page)\b", " ").Trim();

        what = Regex.Replace(what, @"\s+", " ");
        if (what.Length == 0) return null;

        // A known site beats an installed app: "open gmail" means the inbox in the browser,
        // which is what the request literally asked for.
        if (SiteCatalog.FindByWords(what) is { } site)
            return new NlStep("open_website", new() { ["site"] = site.Name }, $"open {site.Name}");

        if (LooksLikeUrl(what))
            return new NlStep("open_website", new() { ["site"] = what }, $"open {what}");

        if (LooksLikePath(what))
            return new NlStep("open_path", new() { ["path"] = what }, $"open {what}");

        if (BestApp(what) is { } app)
            return new NlStep("launch_app", new() { ["name"] = app }, $"launch {app}");

        return null;
    }

    private static bool LooksLikeUrl(string text) =>
        text.StartsWith("http", StringComparison.Ordinal) ||
        (!text.Contains(' ') && text.Contains('.') && !text.EndsWith('.'));

    private static bool LooksLikePath(string text) =>
        text.StartsWith('%') || text.StartsWith("\\\\", StringComparison.Ordinal) ||
        (text.Length > 2 && char.IsLetter(text[0]) && text[1] == ':');

    private string? BestApp(string name)
    {
        // Spoken names, spacing, misheard letters: see AppNames. Nothing found may mean the app
        // was installed a moment ago (a Store app has no shortcut to watch): rescan now, so
        // saying it again finds it.
        var app = AppNames.Best(_apps.Entries, name);
        if (app is null) _apps.RequestRefresh(TimeSpan.Zero);
        return app;
    }

    // ------------------------------------------------------- commands by name

    /// <summary>
    /// The catch-all that makes every command reachable by voice: say roughly what the
    /// command is called - built in or one of the user's own - and it matches.
    /// </summary>
    private (NlStep?, double) NamedCommand(string f)
    {
        var text = Regex.Replace(f, @"^(run|do|execute|use|activate|trigger)\s+", "").Trim();

        // "rebuild the file index" has to reach a command called "Rebuild File Index".
        text = Regex.Replace(text, @"\b(the|a|an|my)\b", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");

        if (text.Length < 3) return (null, 0);

        var bestScore = 0;
        NlStep? best = null;

        foreach (var custom in _settings.CustomCommands)
        {
            var score = Match(text, custom.Name, custom.Keywords);
            if (score > bestScore)
            {
                bestScore = score;
                best = new NlStep(CustomStepId + custom.Id, [], $"run “{custom.Name}”");
            }
        }

        foreach (var command in _registry.Commands)
        {
            if (command.TakesArguments) continue;   // nothing here fills arguments

            var score = Match(text, command.Title, string.Join(' ', command.Keywords));
            if (score > bestScore)
            {
                bestScore = score;
                best = new NlStep(command.Id, [], command.Title.ToLowerInvariant());
            }
        }

        // Fuzzy, so hold it to the word-start tier and report lower confidence: this is the
        // one path where voice could plausibly pick the wrong command.
        return bestScore >= 860 ? (best, 0.8) : (null, 0);

        // Title and keywords are scored as separate fields rather than one concatenated
        // string: "rebuild file index" is an exact hit on the title, but only a diluted
        // prefix of "Rebuild File Index reindex refresh rescan", which scored below the bar.
        static int Match(string needle, params string?[] fields)
        {
            var score = FuzzyMatcher.ScoreAny(needle, fields);
            return score == FuzzyMatcher.NoMatch ? 0 : score;
        }
    }

    /// <summary>Prefix marking a step that runs one of the user's own commands.</summary>
    public const string CustomStepId = "custom:";

    // -------------------------------------------------------------------- search

    private static readonly Regex FindPattern = new(
        @"^(?:find|search\s*for|search|look\s*for|locate|where\s*is|show\s*me)\s+" +
        @"(?:the\s+|a\s+|my\s+)?(?<kind>file|folder|document|pdf|photo|picture|image|video|app|application|program)?\s*" +
        @"(?:named\s+|called\s+)?(?<term>.+?)" +
        @"(?:\s+(?:file|folder|document|on\s+my\s+(?:pc|computer|laptop|drive|drives)|in\s+my\s+files))?$",
        RegexOptions.Compiled);

    /// <summary>
    /// "open blender" when no such app, site or path exists. Rather than refusing, treat it
    /// as a search for blender: the results list can still offer the file, the bookmark or
    /// the web page, which is nearly always what was meant.
    /// </summary>
    private static NlUnderstanding? OpenAsSearch(string f)
    {
        var m = OpenPattern.Match(f);
        if (!m.Success) return null;

        var what = Clean(m.Groups["what"].Value);
        if (what.Length < 2) return null;

        return new NlUnderstanding
        {
            Kind = NlKind.Search,
            Summary = $"looking for “{what}”",
            SearchTerm = what,
            Confidence = 0.8,
        };
    }

    /// <summary>"find the file wifi_scan" - a search to run, not an action to take.</summary>
    private static NlUnderstanding? Find(string f)
    {
        var m = FindPattern.Match(f);
        if (!m.Success) return null;

        var term = Clean(m.Groups["term"].Value);
        if (term.Length < 2) return null;

        var kind = m.Groups["kind"].Value;
        var what = kind.Length > 0 ? kind + "s" : "matches";

        return new NlUnderstanding
        {
            Kind = NlKind.Search,
            Summary = $"searching for {what} named “{term}”",
            SearchTerm = term,
            // An explicit "file"/"folder" is unambiguous; a bare "find X" is a fair guess.
            Confidence = kind.Length > 0 ? 1 : 0.8,
        };
    }

    // ------------------------------------------------------------------- helpers

    private static readonly Regex Filler = new(
        @"^(?:hey\s+|ok\s+|okay\s+)?(?:beacon[, ]+)?(?:please\s+|can\s+you\s+|could\s+you\s+|would\s+you\s+|" +
        @"i\s+want\s+(?:you\s+)?to\s+|i\s+need\s+(?:you\s+)?to\s+|let'?s\s+|just\s+)+",
        RegexOptions.Compiled);

    private static readonly Regex Trailing = new(
        @"\s+(?:please|for\s+me|now|thanks|thank\s+you)\W*$", RegexOptions.Compiled);

    private static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = raw.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ',');
        text = Regex.Replace(text, @"\s+", " ");
        text = Filler.Replace(text, "");
        text = Trailing.Replace(text, "");

        return text.Trim();
    }

    private static string Clean(string term) =>
        term.Trim().Trim('"', '\'', ',', '.').Trim();

    /// <summary>
    /// Splits "close all apps and shut down" into two steps, but leaves "search for cats and
    /// dogs on youtube" alone: a fragment only becomes a step when it starts like one.
    /// </summary>
    private static List<string> SplitSteps(string text)
    {
        var parts = Regex.Split(text, @"\s*(?:,\s*)?\b(?:and\s+then|then|after\s+that|also|and)\b\s*");
        var steps = new List<string>();

        foreach (var part in parts)
        {
            var fragment = part.Trim();
            if (fragment.Length == 0) continue;

            var first = fragment.Split(' ')[0];

            if (steps.Count == 0 || Verbs.Contains(first))
            {
                steps.Add(fragment);
            }
            else
            {
                // Not a new instruction - it belonged to the previous one all along.
                steps[^1] = steps[^1] + " and " + fragment;
            }
        }

        return steps;
    }

    /// <summary>
    /// A percentage, from "75", "75%", "seventy five", "half" or "max". Only counts when the
    /// sentence points at it ("to 75", "75%") so that "volume up 5 steps" stays a nudge.
    /// </summary>
    private static int? Level(string text)
    {
        var explicitNumber = Regex.Match(text, @"(?:\bto\b|\bat\b|=)\s*(\d{1,3})|(\d{1,3})\s*(?:%|percent)");
        if (explicitNumber.Success)
        {
            var digits = explicitNumber.Groups[1].Success
                ? explicitNumber.Groups[1].Value
                : explicitNumber.Groups[2].Value;

            if (int.TryParse(digits, out var value)) return Math.Clamp(value, 0, 100);
        }

        if (Regex.IsMatch(text, @"\b(max|maximum|full|all\s+the\s+way)\b")) return 100;
        if (Regex.IsMatch(text, @"\bhalf(\s*way)?\b")) return 50;
        if (Regex.IsMatch(text, @"\bthree\s*quarters\b")) return 75;
        if (Regex.IsMatch(text, @"\b(a\s+)?quarter\b")) return 25;
        if (Regex.IsMatch(text, @"\b(min|minimum|lowest)\b")) return 0;

        var spelled = Regex.Match(text, @"(?:\bto\b|\bat\b)\s+(?<words>[a-z\- ]{3,20})$");
        return spelled.Success ? SpelledNumber(spelled.Groups["words"].Value) : null;
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18,
        ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90, ["hundred"] = 100,
    };

    /// <summary>"seventy five" - Whisper spells numbers out about as often as it digitises them.</summary>
    private static int? SpelledNumber(string words)
    {
        var total = 0;
        var matched = false;

        foreach (var word in words.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "percent" or "%") continue;
            if (!NumberWords.TryGetValue(word, out var value)) return null;

            total += value;
            matched = true;
        }

        return matched && total is >= 0 and <= 100 ? total : null;
    }
}
