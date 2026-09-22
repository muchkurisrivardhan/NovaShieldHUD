using System.Diagnostics;
using Beacon.Interop;

namespace Beacon.Core;

/// <summary>
/// The controls that take a level rather than a nudge - "volume to 75", "brightness to 20" -
/// plus the bulk and radio actions. Split from the main registry file purely for size.
///
/// Every one of these reports what actually happened. A desktop monitor that ignores
/// brightness, or a Bluetooth radio Windows will not let a normal process touch, has to say
/// so: a launcher that cheerfully claims success while nothing changed is worse than one that
/// admits the limit.
/// </summary>
public sealed partial class CommandRegistry
{
    private static IEnumerable<BeaconCommand> ControlCommands()
    {
        yield return new BeaconCommand
        {
            Id = "set_volume_level",
            Title = "Set Volume Level",
            Subtitle = "Set system volume to an exact percentage",
            Description = "Set the system volume to an exact percentage, 0-100. Use this whenever the " +
                          "user names a level ('volume to 75%', 'set the sound to half'). Use set_volume " +
                          "instead when they only want it louder or quieter.",
            Glyph = "",
            Keywords = ["volume", "sound", "audio", "loudness", "percent"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "percent",
                    Description = "Target volume, 0 to 100.",
                    Type = ParameterType.Number,
                },
            ],
            RunAsync = ctx =>
            {
                var percent = Math.Clamp(ctx.ArgInt("percent", -1), 0, 100);
                ctx.Dismiss();

                if (!AudioEndpoint.TrySetVolumePercent(percent))
                    return Task.FromResult("Could not reach the audio device to set the volume.");

                return Task.FromResult($"Volume set to {percent}%.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "set_mute",
            Title = "Mute / Unmute",
            Subtitle = "Silence or restore system audio",
            Description = "Mute or unmute the system audio. 'state' is 'on' to mute, 'off' to unmute, " +
                          "or 'toggle' to flip it.",
            Glyph = "",
            Keywords = ["mute", "unmute", "silence", "quiet", "sound off"],
            ChoiceTitles = new Dictionary<string, string>
            {
                ["on"] = "Mute",
                ["off"] = "Unmute",
                ["toggle"] = "Toggle Mute",
            },
            Parameters =
            [
                new CommandParameter
                {
                    Name = "state",
                    Description = "Whether to mute.",
                    Choices = ["on", "off", "toggle"],
                    Required = false,
                },
            ],
            RunAsync = ctx =>
            {
                var state = ctx.Arg("state", "toggle").ToLowerInvariant();
                ctx.Dismiss();

                var mute = state switch
                {
                    "on" or "true" or "mute" => true,
                    "off" or "false" or "unmute" => false,
                    _ => !(AudioEndpoint.GetMute() ?? false),
                };

                if (AudioEndpoint.TrySetMute(mute))
                    return Task.FromResult(mute ? "Muted." : "Unmuted.");

                // The mute key is a toggle, so it can still serve the toggle case.
                NativeMethods.TapKey(NativeMethods.VK_VOLUME_MUTE);
                return Task.FromResult("Toggled mute.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "set_brightness",
            Title = "Set Brightness",
            Subtitle = "Set screen brightness to an exact percentage",
            Description = "Set the display brightness to an exact percentage, 0-100. Works on a laptop's " +
                          "built-in panel. Most external monitors do not support software brightness and " +
                          "will report failure.",
            Glyph = "",
            Keywords = ["brightness", "screen", "display", "dim", "bright", "backlight"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "percent",
                    Description = "Target brightness, 0 to 100.",
                    Type = ParameterType.Number,
                },
            ],
            RunAsync = ctx =>
            {
                var percent = Math.Clamp(ctx.ArgInt("percent", -1), 0, 100);
                ctx.Dismiss();

                return Task.FromResult(ScreenBrightness.TrySet(percent)
                    ? $"Brightness set to {percent}%."
                    : "This display does not accept software brightness control. " +
                      "External monitors usually need their own buttons.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "adjust_brightness",
            Title = "Brightness Up / Down",
            Subtitle = "Raise or lower screen brightness",
            Description = "Change the display brightness by ten percentage points. Direction is " +
                          "'up' or 'down'. Use set_brightness when the user names a level.",
            Glyph = "",
            Keywords = ["brighter", "darker", "dim", "brightness"],
            ChoiceTitles = new Dictionary<string, string>
            {
                ["up"] = "Brightness Up",
                ["down"] = "Brightness Down",
            },
            Parameters =
            [
                new CommandParameter
                {
                    Name = "direction",
                    Description = "Which way to move the brightness.",
                    Choices = ["up", "down"],
                },
            ],
            RunAsync = ctx =>
            {
                var up = !ctx.Arg("direction", "up").StartsWith("d", StringComparison.OrdinalIgnoreCase);
                var step = Math.Clamp(ctx.ArgInt("step", 10), 1, 100);
                ctx.Dismiss();

                var current = ScreenBrightness.Get();
                if (current is null)
                    return Task.FromResult("This display does not report its brightness to Windows.");

                var target = Math.Clamp(current.Value + (up ? step : -step), 0, 100);

                return Task.FromResult(ScreenBrightness.TrySet(target)
                    ? $"Brightness {(up ? "up" : "down")} to {target}%."
                    : "This display does not accept software brightness control.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "close_all_windows",
            Title = "Close All Apps",
            Subtitle = "Ask every open app to close",
            Description = "Politely ask every visible application window to close, exactly as clicking " +
                          "each X would. Apps with unsaved work can still prompt, and nothing is killed " +
                          "outright. Beacon and the desktop shell are left alone. Destructive: the user " +
                          "loses their open windows.",
            Glyph = "",
            Keywords = ["close everything", "quit all", "clear desktop", "shut apps"],
            IsDestructive = true,
            Parameters =
            [
                new CommandParameter
                {
                    Name = "except",
                    Description = "Optional comma-separated process or title fragments to leave open.",
                    Required = false,
                },
            ],
            RunAsync = ctx =>
            {
                var keep = ctx.Arg("except")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                ctx.Dismiss();

                var self = Environment.ProcessId;
                var closed = 0;

                foreach (var (title, process, handle) in EnumerateWindows())
                {
                    NativeMethods.GetWindowThreadProcessId(handle, out var pid);
                    if (pid == self) continue;

                    // The desktop, the taskbar and the shell's own windows are not "apps".
                    if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                        IsShellWindow(handle))
                    {
                        continue;
                    }

                    if (keep.Any(k =>
                            title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                            process.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // WM_CLOSE, not TerminateProcess: the app decides what to do about
                    // unsaved work, which is the whole difference between this and a kill.
                    if (NativeMethods.PostMessageW(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                        closed++;
                }

                return Task.FromResult(closed == 0
                    ? "No open app windows to close."
                    : $"Asked {closed} window(s) to close. Anything with unsaved work will prompt.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "wifi_connection",
            Title = "Wi-Fi Connect / Disconnect",
            Subtitle = "Drop or restore the Wi-Fi connection",
            Description = "Disconnect from, or reconnect to, Wi-Fi. Note this is the connection, not " +
                          "the radio: Windows does not let a normal process switch the Wi-Fi adapter " +
                          "itself off, so 'off' disconnects instead.",
            Glyph = "",
            Keywords = ["wifi", "wi-fi", "wireless", "internet", "network", "disconnect"],
            ChoiceTitles = new Dictionary<string, string>
            {
                ["on"] = "Reconnect Wi-Fi",
                ["off"] = "Disconnect Wi-Fi",
            },
            Parameters =
            [
                new CommandParameter
                {
                    Name = "state",
                    Description = "'on' to reconnect, 'off' to disconnect.",
                    Choices = ["on", "off"],
                },
            ],
            RunAsync = ctx =>
            {
                var on = ctx.Arg("state", "on").StartsWith("on", StringComparison.OrdinalIgnoreCase);
                ctx.Dismiss();

                if (!on)
                {
                    return Task.FromResult(NetworkControl.Disconnect()
                        ? "Disconnected from Wi-Fi."
                        : "Could not disconnect - there may be no wireless adapter.");
                }

                if (NetworkControl.TryReconnect(out var profile))
                    return Task.FromResult($"Reconnecting to {profile}.");

                Open("ms-settings:network-wifi");
                return Task.FromResult("No saved network to rejoin - opened Wi-Fi settings.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "bluetooth_settings",
            Title = "Bluetooth",
            Subtitle = "Open the Bluetooth switch",
            Description = "Open the Bluetooth page in Windows Settings, where the radio switch lives. " +
                          "Windows has no way for a non-elevated app to flip that switch directly, so " +
                          "this opens the page rather than claiming to have toggled it.",
            Glyph = "",
            Keywords = ["bluetooth", "pair", "headphones", "wireless device"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Open("ms-settings:bluetooth");
                return Task.FromResult("Opened Bluetooth settings - the switch is at the top.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "night_light_settings",
            Title = "Night Light",
            Subtitle = "Open the night light switch",
            Description = "Open the night light page in Windows Settings. The night light state is not " +
                          "exposed through any supported API, so this opens its page instead of guessing.",
            Glyph = "",
            Keywords = ["night light", "blue light", "warm", "eye strain", "nightlight"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Open("ms-settings:nightlight");
                return Task.FromResult("Opened night light settings.");
            },
        };
    }

    private static IEnumerable<BeaconCommand> ProductivityCommands()
    {
        yield return new BeaconCommand
        {
            Id = "switch_to_window",
            Title = "Switch to Window",
            Subtitle = "Bring an open window to the front",
            Description = "Bring an already-open window to the front, matched by its title or app " +
                          "name ('excel', 'the youtube tab', 'visual studio code'). Prefer this over " +
                          "launch_app when the app is probably already running.",
            Glyph = "",
            Keywords = ["switch", "jump", "focus", "go to window", "alt tab"],
            ShowInSearch = false,   // the Windows provider lists every open window directly
            Parameters =
            [
                new CommandParameter { Name = "name", Description = "Part of the window title or app name." },
            ],
            RunAsync = ctx =>
            {
                var name = ctx.Arg("name");
                ctx.Dismiss();

                var best = EnumerateWindows()
                    .Where(w => !w.Process.Equals("Beacon", StringComparison.OrdinalIgnoreCase))
                    .Select(w => (w, Score: FuzzyMatcher.ScoreAny(name, w.Title, w.Process)))
                    .Where(x => x.Score != FuzzyMatcher.NoMatch)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.w)
                    .FirstOrDefault();

                if (best.Handle == IntPtr.Zero)
                    return Task.FromResult($"No open window matching '{name}'.");

                ForegroundTracker.FocusWindow(best.Handle);
                return Task.FromResult($"Switched to {best.Title}.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "set_timer",
            Title = "Timer / Reminder",
            Subtitle = "Get a notification after a delay",
            Description = "Start a timer, or a reminder with a message. 'seconds' is how long from now. " +
                          "A notification appears when it is due, even if Beacon is closed and " +
                          "restarted in between.",
            Glyph = "",
            Keywords = ["timer", "remind", "reminder", "alarm", "countdown", "pomodoro"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "seconds",
                    Description = "Delay from now, in seconds.",
                    Type = ParameterType.Number,
                },
                new CommandParameter
                {
                    Name = "message",
                    Description = "What to say when it fires. Omit for a plain timer.",
                    Required = false,
                },
            ],
            RunAsync = ctx =>
            {
                var seconds = ctx.ArgInt("seconds", 0);
                var message = ctx.Arg("message");
                ctx.Dismiss();

                if (seconds <= 0) return Task.FromResult("How long should the timer run?");

                var due = DateTime.Now.AddSeconds(seconds);
                var span = TimeSpan.FromSeconds(seconds);

                Timers.Add(due, message.Length > 0 ? message : $"Time's up — {Describe(span)} timer.");

                return Task.FromResult(message.Length > 0
                    ? $"I'll remind you at {due:t}: {message}"
                    : $"Timer set for {Describe(span)} - done at {due:t}.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "list_timers",
            Title = "Show Timers",
            Subtitle = "What is still counting down",
            Description = "List the timers and reminders that have not fired yet.",
            Glyph = "",
            Keywords = ["timers", "reminders", "alarms", "pending"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var pending = Timers.Pending;

                return Task.FromResult(pending.Count == 0
                    ? "No timers running."
                    : string.Join("  ", pending.Select(p => $"{p.Due:t} - {p.Message}")));
            },
        };

        yield return new BeaconCommand
        {
            Id = "cancel_timers",
            Title = "Cancel Timers",
            Subtitle = "Stop every timer and reminder",
            Description = "Cancel all pending timers and reminders.",
            Glyph = "",
            Keywords = ["stop timer", "clear reminders", "cancel alarm"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var count = Timers.CancelAll();
                return Task.FromResult(count == 0 ? "No timers to cancel." : $"Cancelled {count} timer(s).");
            },
        };
    }

    /// <summary>"25 minutes", "1 hour 30 minutes", "45 seconds".</summary>
    internal static string Describe(TimeSpan span)
    {
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days} day{(span.Days == 1 ? "" : "s")}");
        if (span.Hours > 0) parts.Add($"{span.Hours} hour{(span.Hours == 1 ? "" : "s")}");
        if (span.Minutes > 0) parts.Add($"{span.Minutes} minute{(span.Minutes == 1 ? "" : "s")}");
        if (span.Seconds > 0 || parts.Count == 0) parts.Add($"{span.Seconds} second{(span.Seconds == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }

    /// <summary>Progman / WorkerW / Shell_TrayWnd: the desktop itself, not an application.</summary>
    private static bool IsShellWindow(IntPtr handle)
    {
        var className = ForegroundTracker.GetClassName(handle);
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow";
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch
        {
            // The shell refused the protocol; the command's message already covers it.
        }
    }
}
