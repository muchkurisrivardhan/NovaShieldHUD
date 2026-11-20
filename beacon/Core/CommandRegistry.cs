using System.Diagnostics;
using Beacon.Interop;
using Beacon.Providers;

namespace Beacon.Core;

/// <summary>
/// Every verb Beacon knows, in one list.
///
/// This is intentionally not a plugin system - no assembly loading, no manifests, no
/// sandboxing. Adding a capability means adding an entry below. The one concession to
/// structure is that commands are *declarative*, which is what lets the AI planner expose
/// the same list to the model as tools without a second, drift-prone copy of "what this
/// app can do".
/// </summary>
public sealed partial class CommandRegistry
{
    private readonly Dictionary<string, BeaconCommand> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<BeaconCommand> Commands { get; }

    public CommandRegistry(
        AppIndex apps, IFileSearch files, Action rebuildIndex, Action openSettings, Action startVoice,
        Func<IntPtr, Task> promptRing, Action<IntPtr> assistant, Action dismissAssistant)
    {
        var commands = new List<BeaconCommand>();

        // Tagged by group so the Settings command library can show them by category.
        void Add(IEnumerable<BeaconCommand> group, string category)
        {
            foreach (var command in group)
            {
                command.Category = category;
                commands.Add(command);
            }
        }

        Add(WindowCommands(), "Window");
        Add(SystemCommands(), "System");
        Add(MediaCommands(), "Media");
        Add(ControlCommands(), "System");
        Add(ProductivityCommands(), "Productivity");
        Add(SearchCommands(apps, files), "Claude");
        Add(IndexCommands(rebuildIndex, openSettings), "Beacon");
        Add(SuiteCommands(), "NovaShield HUD");

        // Bindable in Settings > Shortcuts: one chord that opens the bar already listening.
        commands.Add(new BeaconCommand
        {
            Id = "voice_search",
            Title = "Voice Search",
            Subtitle = "Speak your search - transcribed on this PC",
            Description = "Open Beacon and start listening for a spoken search.",
            Glyph = "",
            Keywords = ["speak", "dictate", "microphone", "mic", "talk", "voice"],
            ExposeToAi = false,
            Category = "Beacon",
            RunAsync = _ =>
            {
                startVoice();
                return Task.FromResult("Listening.");
            },
        });

        // Ctrl+M by default: Nova, the 3D pet, appears beside the cursor and listens.
        commands.Add(new BeaconCommand
        {
            Id = Settings.AssistantCommandId,
            Title = "Call Nova",
            Subtitle = "Your 3D pet appears beside the cursor and listens",
            Description = "Bring Nova up beside the mouse pointer, listening. Pressed again: done talking.",
            Glyph = "\uE720",
            Keywords = ["nova", "assistant", "talk", "jarvis", "pet", "ask", "voice assistant", "hey nova", "call nova"],
            ExposeToAi = false,
            Category = "Beacon",
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                assistant(ctx.TargetWindow);
                return Task.FromResult("Nova.");
            },
        });

        // Ctrl+Alt+N by default: Nova leaves the screen.
        commands.Add(new BeaconCommand
        {
            Id = Settings.NovaDismissCommandId,
            Title = "Send Nova Away",
            Subtitle = "The pet leaves the screen",
            Description = "Remove Nova, the 3D pet, from the screen.",
            Glyph = "\uE711",
            Keywords = ["nova", "dismiss", "go away", "hide pet", "send nova away"],
            ExposeToAi = false,
            Category = "Beacon",
            RunAsync = ctx =>
            {
                dismissAssistant();
                return Task.FromResult("Nova left.");
            },
        });

        // Bindable in Settings > Shortcuts (Ctrl+Alt+P by default): rewrite the selected text as a prompt.
        commands.Add(new BeaconCommand
        {
            Id = Settings.PromptRingCommandId,
            Title = "Shape Prompt",
            Subtitle = "Select dictated text, pick Build, Fix, Research... rewritten in place, on this PC",
            Description = "Rewrite the selected text as a well-structured AI prompt, using a free model on this PC.",
            Glyph = "\uE8BD",
            Keywords = ["prompt", "rewrite", "shape", "improve prompt", "prompt ring", "dictation", "clean up"],
            ExposeToAi = false,
            Category = "Beacon",
            RunAsync = async ctx =>
            {
                await promptRing(ctx.TargetWindow);
                return "Prompt ring.";
            },
        });

        Commands = commands;
        foreach (var c in commands) _byId[c.Id] = c;
    }

    public BeaconCommand? Find(string id) =>
        _byId.TryGetValue(id, out var c) ? c : null;

    public IEnumerable<BeaconCommand> AiExposed => Commands.Where(c => c.ExposeToAi);

    // ------------------------------------------------------------------ windows

    private static IEnumerable<BeaconCommand> WindowCommands()
    {
        // Every one of these acts on ctx.TargetWindow - the window that was in front before
        // Beacon opened. Using GetForegroundWindow() here would target our own search bar.

        yield return new BeaconCommand
        {
            Id = "snap_window",
            Title = "Snap Window",
            Subtitle = "Position the window you were just in",
            Description = "Move and resize the user's previously focused window to a region of its " +
                          "monitor. Use for requests like 'snap this left', 'put it on the right half', " +
                          "'center this window', 'make it fill the screen'.",
            Glyph = "",
            Keywords = ["left", "right", "half", "tile", "position", "arrange"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "zone",
                    Description = "Which region of the screen to place the window in.",
                    Choices = ["left", "right", "top-left", "top-right", "bottom-left", "bottom-right", "center", "full"],
                },
            ],
            RunAsync = ctx =>
            {
                var zone = ctx.Arg("zone", "left").ToLowerInvariant() switch
                {
                    "right" => SnapZone.Right,
                    "top-left" or "topleft" => SnapZone.TopLeft,
                    "top-right" or "topright" => SnapZone.TopRight,
                    "bottom-left" or "bottomleft" => SnapZone.BottomLeft,
                    "bottom-right" or "bottomright" => SnapZone.BottomRight,
                    "center" or "centre" => SnapZone.Center,
                    "full" or "fill" or "maximize" => SnapZone.FullWorkArea,
                    _ => SnapZone.Left,
                };

                ctx.Dismiss();
                var name = DescribeWindow(ctx.TargetWindow);
                var ok = WindowManager.Snap(ctx.TargetWindow, zone);

                return Task.FromResult(ok
                    ? $"Snapped {name} to {zone}."
                    : $"Could not move {name} - it may be an elevated window.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "maximize_window",
            Title = "Maximize Window",
            Subtitle = "Maximize the window you were just in",
            Description = "Maximize the user's previously focused window.",
            Glyph = "",
            Keywords = ["fullscreen", "enlarge", "grow"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var name = DescribeWindow(ctx.TargetWindow);
                var ok = WindowManager.Maximize(ctx.TargetWindow);
                return Task.FromResult(ok ? $"Maximized {name}." : $"Could not maximize {name}.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "minimize_window",
            Title = "Minimize Window",
            Subtitle = "Minimize the window you were just in",
            Description = "Minimize the user's previously focused window to the taskbar.",
            Glyph = "",
            Keywords = ["hide", "shrink"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var name = DescribeWindow(ctx.TargetWindow);
                var ok = WindowManager.Minimize(ctx.TargetWindow);
                return Task.FromResult(ok ? $"Minimized {name}." : $"Could not minimize {name}.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "restore_window",
            Title = "Restore Window",
            Subtitle = "Un-maximize the window you were just in",
            Description = "Restore the user's previously focused window to its pre-maximized size.",
            Glyph = "",
            Keywords = ["unmaximize", "normal"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var ok = WindowManager.Restore(ctx.TargetWindow);
                return Task.FromResult(ok ? "Restored window." : "Could not restore window.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "close_window",
            Title = "Close Window",
            Subtitle = "Close the window you were just in",
            Description = "Close the user's previously focused window. This sends the same polite " +
                          "close request as clicking the X, so the app can still prompt about unsaved work.",
            Glyph = "",
            Keywords = ["quit", "dismiss", "exit"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var name = DescribeWindow(ctx.TargetWindow);
                var ok = WindowManager.Close(ctx.TargetWindow);
                return Task.FromResult(ok ? $"Asked {name} to close." : $"Could not close {name}.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "move_window_next_monitor",
            Title = "Move Window to Next Monitor",
            Subtitle = "Send the window you were just in to another display",
            Description = "Move the user's previously focused window to the next monitor, keeping its " +
                          "relative position and size. No effect with a single display.",
            Glyph = "",
            Keywords = ["display", "screen", "monitor", "second"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var name = DescribeWindow(ctx.TargetWindow);
                var ok = WindowManager.MoveToNextMonitor(ctx.TargetWindow);
                return Task.FromResult(ok
                    ? $"Moved {name} to the next monitor."
                    : "No second monitor available.");
            },
        };
    }

    // ------------------------------------------------------------------- system

    private static IEnumerable<BeaconCommand> SystemCommands()
    {
        yield return new BeaconCommand
        {
            Id = "lock_workstation",
            Title = "Lock PC",
            Subtitle = "Lock this computer",
            Description = "Lock the workstation, requiring the user's password to get back in.",
            Glyph = "",
            Keywords = ["lock", "secure", "away"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                NativeMethods.LockWorkStation();
                return Task.FromResult("Locked the workstation.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "sleep",
            Title = "Sleep",
            Subtitle = "Put this computer to sleep",
            Description = "Put the computer into sleep (suspend) mode.",
            Glyph = "",
            Keywords = ["suspend", "standby"],
            IsDestructive = true,
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Shell("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                return Task.FromResult("Going to sleep.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "shutdown",
            Title = "Shut Down",
            Subtitle = "Power off this computer",
            Description = "Shut the computer down. Destructive: unsaved work in other applications may be lost.",
            Glyph = "",
            Keywords = ["power", "off", "turn off"],
            IsDestructive = true,
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Shell("shutdown.exe", "/s /t 0");
                return Task.FromResult("Shutting down.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "restart",
            Title = "Restart",
            Subtitle = "Reboot this computer",
            Description = "Restart the computer. Destructive: unsaved work in other applications may be lost.",
            Glyph = "",
            Keywords = ["reboot"],
            IsDestructive = true,
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Shell("shutdown.exe", "/r /t 0");
                return Task.FromResult("Restarting.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "sign_out",
            Title = "Sign Out",
            Subtitle = "Log off the current user",
            Description = "Sign the current user out of Windows. Destructive: closes all their apps.",
            Glyph = "",
            Keywords = ["logoff", "log out", "logout"],
            IsDestructive = true,
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                Shell("shutdown.exe", "/l");
                return Task.FromResult("Signing out.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "empty_recycle_bin",
            Title = "Empty Recycle Bin",
            Subtitle = "Permanently delete everything in the bin",
            Description = "Permanently empty the Recycle Bin. Destructive and irreversible.",
            Glyph = "",
            Keywords = ["trash", "delete", "clean"],
            IsDestructive = true,
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                var hr = NativeMethods.SHEmptyRecycleBinW(
                    IntPtr.Zero, null,
                    NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI);

                // S_OK, or the "already empty" code which is not a real failure.
                return Task.FromResult(hr is 0 or unchecked((int)0x8000FFFF)
                    ? "Recycle Bin emptied."
                    : $"Could not empty the Recycle Bin (0x{hr:X8}).");
            },
        };

        yield return new BeaconCommand
        {
            Id = "kill_process",
            Title = "Kill Process",
            Subtitle = "Force-terminate a running process by name",
            Description = "Forcibly terminate all processes matching a name (e.g. 'chrome', 'notepad'). " +
                          "Destructive: the process is killed outright and any unsaved work in it is lost.",
            Glyph = "",
            Keywords = ["terminate", "end task", "force quit"],
            IsDestructive = true,
            Parameters =
            [
                new CommandParameter
                {
                    Name = "name",
                    Description = "Process name without the .exe extension, for example 'chrome' or 'code'.",
                },
            ],
            RunAsync = ctx =>
            {
                var name = ctx.Arg("name").Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim();
                ctx.Dismiss();

                if (name.Length == 0) return Task.FromResult("No process name given.");

                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) return Task.FromResult($"No running process named '{name}'.");

                int killed = 0;
                foreach (var p in procs)
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                        killed++;
                    }
                    catch
                    {
                        // Protected or elevated process; we are asInvoker so this is expected.
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }

                return Task.FromResult(killed > 0
                    ? $"Killed {killed} '{name}' process(es)."
                    : $"Could not kill '{name}' - it may require administrator rights.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "open_path",
            Title = "Open Path",
            Subtitle = "Open a file, folder or URL",
            Description = "Open or run anything you could type into the Windows Run box: a file, " +
                          "folder or URL, a program by name ('notepad'), a full command line " +
                          "('cmd /c dir'), a protocol ('ms-settings:display') or a shell alias " +
                          "('shell:startup'). Environment variables like %APPDATA% are expanded.",
            Glyph = "",
            Keywords = ["launch", "reveal", "explorer"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "path",
                    Description = "Absolute path, URL, or shell alias to open.",
                },
            ],
            RunAsync = ctx =>
            {
                var path = Environment.ExpandEnvironmentVariables(ctx.Arg("path"));
                ctx.Dismiss();

                if (path.Length == 0) return Task.FromResult("No path given.");

                try
                {
                    // Same resolution as the Windows Run box, so a bare program name, a
                    // protocol or a command line all work here too.
                    ShellRun.Start(path);
                    return Task.FromResult($"Opened {path}.");
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Could not open {path}: {ex.Message}");
                }
            },
        };

        // Shell shortcuts worth having as one-keystroke entries.
        foreach (var (id, title, target, glyph, keywords) in new[]
        {
            ("open_settings", "Windows Settings", "ms-settings:", "", new[] { "preferences", "control panel" }),
            ("open_task_manager", "Task Manager", "taskmgr.exe", "", new[] { "processes", "performance" }),
            ("open_network_settings", "Network Settings", "ms-settings:network", "", new[] { "wifi", "internet" }),
            ("open_bluetooth_settings", "Bluetooth Settings", "ms-settings:bluetooth", "", new[] { "pair", "device" }),
            ("open_sound_settings", "Sound Settings", "ms-settings:sound", "", new[] { "audio", "speaker" }),
            ("open_display_settings", "Display Settings", "ms-settings:display", "", new[] { "monitor", "resolution" }),
            ("open_downloads", "Downloads Folder", "%USERPROFILE%\\Downloads", "", new[] { "files" }),
            ("open_startup_folder", "Startup Folder", "shell:startup", "", new[] { "boot", "autostart" }),
        })
        {
            var capturedTarget = target;
            var capturedTitle = title;

            yield return new BeaconCommand
            {
                Id = id,
                Title = capturedTitle,
                Subtitle = "Open " + capturedTitle,
                Description = $"Open {capturedTitle}.",
                Glyph = glyph,
                Keywords = keywords,
                RunAsync = ctx =>
                {
                    ctx.Dismiss();
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Environment.ExpandEnvironmentVariables(capturedTarget),
                            UseShellExecute = true,
                        });
                        return Task.FromResult($"Opened {capturedTitle}.");
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult($"Could not open {capturedTitle}: {ex.Message}");
                    }
                },
            };
        }
    }

    // -------------------------------------------------------------------- media

    private static IEnumerable<BeaconCommand> MediaCommands()
    {
        yield return new BeaconCommand
        {
            Id = "set_volume",
            Title = "Volume",
            Subtitle = "Raise, lower or mute system volume",
            Description = "Adjust the system volume. Direction is 'up', 'down' or 'mute'. Each step is " +
                          "roughly 2% of the range, so use steps of 5-10 for a noticeable change.",
            Glyph = "",
            Keywords = ["sound", "louder", "quieter", "mute", "audio"],
            Parameters =
            [
                new CommandParameter
                {
                    Name = "direction",
                    Description = "Which way to change the volume.",
                    Choices = ["up", "down", "mute"],
                },
                new CommandParameter
                {
                    Name = "steps",
                    Description = "How many volume steps to apply. Ignored for mute. Default 5.",
                    Type = ParameterType.Number,
                    Required = false,
                },
            ],
            RunAsync = ctx =>
            {
                var direction = ctx.Arg("direction", "up").ToLowerInvariant();
                var steps = Math.Clamp(ctx.ArgInt("steps", 5), 1, 50);
                ctx.Dismiss();

                switch (direction)
                {
                    case "mute":
                        NativeMethods.TapKey(NativeMethods.VK_VOLUME_MUTE);
                        return Task.FromResult("Toggled mute.");
                    case "down":
                        NativeMethods.TapKey(NativeMethods.VK_VOLUME_DOWN, steps);
                        return Task.FromResult($"Volume down {steps} steps.");
                    default:
                        NativeMethods.TapKey(NativeMethods.VK_VOLUME_UP, steps);
                        return Task.FromResult($"Volume up {steps} steps.");
                }
            },
        };

        yield return new BeaconCommand
        {
            Id = "media_play_pause",
            Title = "Play / Pause",
            Subtitle = "Toggle media playback",
            Description = "Send the play/pause media key to whatever is currently playing.",
            Glyph = "",
            Keywords = ["music", "video", "spotify", "pause", "resume"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                NativeMethods.TapKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
                return Task.FromResult("Toggled playback.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "media_next",
            Title = "Next Track",
            Subtitle = "Skip to the next track",
            Description = "Send the next-track media key.",
            Glyph = "",
            Keywords = ["skip", "forward", "music"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                NativeMethods.TapKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
                return Task.FromResult("Skipped to next track.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "media_previous",
            Title = "Previous Track",
            Subtitle = "Go back to the previous track",
            Description = "Send the previous-track media key.",
            Glyph = "",
            Keywords = ["back", "rewind", "music"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                NativeMethods.TapKey(NativeMethods.VK_MEDIA_PREV_TRACK);
                return Task.FromResult("Went to previous track.");
            },
        };
    }

    // ------------------------------------------------------- search/launch verbs

    private static IEnumerable<BeaconCommand> SearchCommands(AppIndex apps, IFileSearch files)
    {
        // These exist mainly so the AI has a way to reach the same capabilities the user
        // gets by typing. ShowInSearch is false because the providers already surface apps
        // and files directly, and a duplicate row would just be noise.

        yield return new BeaconCommand
        {
            Id = "launch_app",
            Title = "Launch Application",
            Subtitle = "Start an installed application by name",
            Description = "Launch an installed application by name. Matching is fuzzy, so 'vs code' " +
                          "finds 'Visual Studio Code'. Prefer this over open_path for applications.",
            Glyph = "",
            ShowInSearch = false,
            Parameters =
            [
                new CommandParameter { Name = "name", Description = "Name of the application to launch." },
            ],
            RunAsync = ctx =>
            {
                var name = ctx.Arg("name");
                ctx.Dismiss();

                if (name.Length == 0) return Task.FromResult("No application name given.");

                var best = apps.Entries
                    .Select(a => (App: a, Score: FuzzyMatcher.Score(a.Name, name)))
                    .Where(x => x.Score != FuzzyMatcher.NoMatch)
                    .OrderByDescending(x => x.Score)
                    .Select(x => x.App)
                    .FirstOrDefault();

                if (best is null) return Task.FromResult($"No installed app matching '{name}'.");

                try
                {
                    AppIndex.Launch(best);
                    return Task.FromResult($"Launched {best.Name}.");
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"Could not launch {best.Name}: {ex.Message}");
                }
            },
        };

        yield return new BeaconCommand
        {
            Id = "search_files",
            Title = "Search Files",
            Subtitle = "Find files and folders by name",
            Description = "Search the filesystem for files and folders whose name matches a term. " +
                          "Returns a list of paths. Use this to locate a file before opening it with open_path.",
            Glyph = "",
            ShowInSearch = false,
            Parameters =
            [
                new CommandParameter { Name = "query", Description = "Filename or fragment to search for." },
                new CommandParameter
                {
                    Name = "max",
                    Description = "Maximum results to return. Default 10.",
                    Type = ParameterType.Number,
                    Required = false,
                },
            ],
            RunAsync = async ctx =>
            {
                var query = ctx.Arg("query");
                var max = Math.Clamp(ctx.ArgInt("max", 10), 1, 50);

                if (query.Length == 0) return "No search term given.";

                var hits = await files.SearchAsync(query, max, CancellationToken.None).ConfigureAwait(false);

                if (hits.Count == 0) return $"No files matching '{query}'.";

                var lines = hits.Select(h => $"{(h.IsFolder ? "[dir] " : "")}{h.FullPath}");
                return $"Found {hits.Count} match(es):\n" + string.Join('\n', lines);
            },
        };

        yield return new BeaconCommand
        {
            Id = "web_search",
            Title = "Search the Web",
            Subtitle = "Open a web search in the default browser",
            Description = "Open a web search in the user's default browser.",
            Glyph = "",
            ShowInSearch = false,
            Parameters =
            [
                new CommandParameter { Name = "query", Description = "What to search for." },
                new CommandParameter
                {
                    Name = "engine",
                    Description = "Which site to search. Use the site's name, e.g. 'YouTube', " +
                                  "'Gmail', 'GitHub', 'Amazon', 'Google Maps'. Defaults to Google.",
                    Required = false,
                },
            ],
            RunAsync = ctx =>
            {
                var query = ctx.Arg("query");
                var engineName = ctx.Arg("engine", "google");
                ctx.Dismiss();

                if (query.Length == 0) return Task.FromResult("No search term given.");

                var site = SiteCatalog.FindByEngineName(engineName);
                if (!site.CanSearch) site = SiteCatalog.Google;

                WebProvider.OpenUrl(site.SearchUrl(query), ctx.Notify);
                return Task.FromResult($"Opened a {site.Name} search for '{query}'.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "open_website",
            Title = "Open Website",
            Subtitle = "Open a site by name or address",
            Description = "Open a website in the default browser. Accepts a known site name " +
                          "('YouTube', 'Gmail', 'GitHub') or a bare address ('example.com', " +
                          "'localhost:3000'). Prefer this over open_path for web addresses.",
            Glyph = "",
            ShowInSearch = false,
            Parameters =
            [
                new CommandParameter
                {
                    Name = "site",
                    Description = "Site name or address to open.",
                },
            ],
            RunAsync = ctx =>
            {
                var input = ctx.Arg("site");
                ctx.Dismiss();

                if (input.Length == 0) return Task.FromResult("No site given.");

                if (WebProvider.TryParseUrl(input, out var url, out var display))
                {
                    WebProvider.OpenUrl(url, ctx.Notify);
                    return Task.FromResult($"Opened {display}.");
                }

                var known = SiteCatalog.FindByEngineName(input);
                WebProvider.OpenUrl(known.OpenUrl, ctx.Notify);
                return Task.FromResult($"Opened {known.Name} ({known.Domain}).");
            },
        };

        yield return new BeaconCommand
        {
            Id = "list_windows",
            Title = "List Open Windows",
            Subtitle = "Enumerate visible top-level windows",
            Description = "List every visible top-level window with its title and owning process. " +
                          "Use this first when the user refers to a window by app name rather than " +
                          "to the one they were last in.",
            Glyph = "",
            ShowInSearch = false,
            RunAsync = _ =>
            {
                var windows = EnumerateWindows();
                if (windows.Count == 0) return Task.FromResult("No visible windows.");

                var lines = windows.Select(w => $"{w.Process}: {w.Title}");
                return Task.FromResult("Open windows:\n" + string.Join('\n', lines));
            },
        };
    }

    // ------------------------------------------------------------------- index

    private static IEnumerable<BeaconCommand> IndexCommands(Action rebuildIndex, Action openSettings)
    {
        yield return new BeaconCommand
        {
            Id = "open_beacon_settings",
            Title = "Beacon Settings",
            Subtitle = "Shortcuts, custom commands, indexing and appearance",
            Description = "Open Beacon's own settings window.",
            Glyph = "",
            Keywords = ["preferences", "options", "configure", "hotkey", "shortcut", "beacon"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                openSettings();
                return Task.FromResult("Opened Beacon settings.");
            },
        };

        yield return new BeaconCommand
        {
            Id = "rebuild_file_index",
            Title = "Rebuild File Index",
            Subtitle = "Re-scan your drives for new files",
            Description = "Rebuild the filename index by re-walking the configured drives. " +
                          "Use when a file was created recently and does not appear in search yet.",
            Glyph = "",
            Keywords = ["reindex", "refresh", "rescan", "index"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                rebuildIndex();
                return Task.FromResult("Rebuilding the file index in the background.");
            },
        };
    }

    // ------------------------------------------------------------------ helpers

    private static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "the window";

        var title = ForegroundTracker.GetTitle(hWnd);
        if (!string.IsNullOrWhiteSpace(title))
            return title.Length > 40 ? title[..37] + "..." : title;

        return ForegroundTracker.GetProcessName(hWnd) ?? "the window";
    }

    public static List<(string Title, string Process, IntPtr Handle)> EnumerateWindows()
    {
        var found = new List<(string, string, IntPtr)>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsCloaked(hWnd)) return true;

            // Tool windows are palettes and overlays, not things a user thinks of as windows.
            var ex = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            var title = ForegroundTracker.GetTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            found.Add((title, ForegroundTracker.GetProcessName(hWnd) ?? "?", hWnd));
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static void Shell(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Nothing sensible to do if the shell itself refuses.
        }
    }
}
