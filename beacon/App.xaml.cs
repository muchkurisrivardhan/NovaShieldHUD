using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using Beacon.Ai;
using Beacon.Core;
using Beacon.Interop;
using Beacon.Nl;
using Beacon.Providers;
using Beacon.UI;
using Forms = System.Windows.Forms;

namespace Beacon;

/// <summary>
/// Composition root and process lifetime.
///
/// Beacon is a resident tray process, not a launch-on-demand app: the overlay window is
/// built once here and only ever hidden, so summoning it is a Show() rather than a
/// process start. That is the whole reason it can feel instant.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutex = "Local\\Beacon.SingleInstance.v1";

    private Mutex? _instanceLock;
    private Forms.NotifyIcon? _tray;
    private SearchWindow? _window;
    private HotkeyManager? _hotkeys;
    private ForegroundTracker? _foreground;
    private Settings? _settings;
    private CommandRegistry? _registry;
    private FaviconCache? _favicons;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            // Another copy already owns the hotkey; a second one would just fail to register.
            MessageBox.Show("Beacon is already running — look for it in the system tray.",
                "Beacon", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Notify($"Something went wrong: {args.Exception.Message}");
            args.Handled = true;
        };

        // Before any window exists, so every scroll area in the app picks it up.
        WheelScrolling.Install();

        _settings = Settings.Load();
        Compose(_settings);
    }

    private void Compose(Settings settings)
    {
        var stats = new UsageStats(Settings.StatsPath);
        _foreground = new ForegroundTracker();

        var apps = new AppIndex();
        var fileIndex = new FileIndex();
        var bookmarks = new BookmarkIndex();
        var everything = new EverythingClient(settings.EverythingPort);
        var files = new FileProvider(everything, fileIndex, stats);

        _registry = new CommandRegistry(
            apps,
            files,
            rebuildIndex: () => _ = fileIndex.BuildAsync(BuildIndexOptions(settings)),
            openSettings: OpenSettings,
            startVoice: StartVoiceSearch,
            promptRing: target => (_smartRing ??= new LocalAi.SmartRing(settings, Notify)).RunAsync(target),
            codeRing: target => (_codeRing ??= new LocalAi.CodeRing(settings, Notify)).RunAsync(target),
            assistant: target => Dispatcher.Invoke(() => Pet().Summon(target)),
            dismissAssistant: () => Dispatcher.Invoke(() => _pet?.Dismiss()));

        _makePet = () => new UI.Pet.PetWindow(settings, _language!, _languageRunner!, () => BuildVoiceVocabulary(apps));

        var planner = new AiPlanner(_registry, settings);
        var ai = new AiProvider(planner, settings);
        // No API key, or a subscription brain: the launcher's "?" goes to Kai and that brain.
        ai.HandOff += (prompt, target) => Dispatcher.BeginInvoke(() => Pet().Ask(prompt, target));

        // The local sentence parser and the model are the same capability at two price
        // points: rules answer the common phrasings instantly and offline, and whatever they
        // cannot place falls through to the AI row underneath them.
        var language = _language = new NlParser(_registry, apps, settings);
        var languageRunner = _languageRunner = new NlRunner(_registry, settings);

        _favicons = new FaviconCache(Path.Combine(Settings.Directory, "favicons"))
        {
            Enabled = settings.ShowFavicons,
        };

        var providers = new List<IResultProvider>
        {
            new NlProvider(language, languageRunner, settings, Notify),
            new CalcProvider(),
            new InfoProvider(),
            new WindowProvider(),
            new CustomCommandProvider(settings, stats),
            new CommandProvider(_registry, stats),
            new AppProvider(apps, stats),
            new BookmarkProvider(bookmarks, stats),
            files,
            new WebProvider(stats),
            ai,
        };

        _window = new SearchWindow(
            providers, _foreground, settings, planner, ai, _favicons, Notify,
            language, languageRunner,
            vocabulary: () => BuildVoiceVocabulary(apps));

        // Style, width, accent and text size from settings - applied at startup, not only
        // after the first Save in Settings.
        _window.ApplyAppearance(settings);

        // The hotkey needs a real HWND to hang off, so realise the window now.
        _window.SourceInitializedOnce();

        _hotkeys = HotkeyManager.Attach(_window);
        _hotkeys.Pressed += OnHotkeyPressed;
        ApplyHotkeys();

        // Push-to-talk for Nova: hold the talk key to speak, let go to send.
        _talkKey = new PushToTalkHook();
        _talkKey.HoldStart += () => Pet().TalkHold(NativeMethods.GetForegroundWindow());
        _talkKey.HoldEnd += () => _pet?.TalkRelease();
        _talkKey.Cancel += () => _pet?.TalkCancel();
        _talkKey.Tap += () => Pet().TalkTap(NativeMethods.GetForegroundWindow());
        _talkKey.Use(settings.PushToTalk);

        BuildTrayIcon(settings);
        ApplyStartupRegistration(settings.RunAtStartup);
        if (settings.LaunchDotline) Suite.OpenDotline();
        // Filee wears Beacon's look; refresh it in case Beacon was updated with new palettes.
        Suite.SyncFileeLook(settings);
        // Not under Smart App Control: it would block Filee's unsigned files, with a notification each.
        if (settings.LaunchFilee && !Suite.FileeRunning && !Suite.SmartAppControlOn) Suite.OpenFilee();

        // Timers fire on a pool thread; the tray icon belongs to this one. Subscribe before
        // restoring, so reminders that fell due while Beacon was off are announced too.
        Timers.Due += message => Dispatcher.BeginInvoke(() =>
        {
            System.Media.SystemSounds.Exclamation.Play();
            Notify("⏰ " + message);
        });
        Timers.Restore();

        // Indexing runs after the UI is live so the hotkey works within milliseconds of
        // launch, even while the app list is still filling in.
        // Once the app list is complete, render every app icon in the background so the
        // first search that shows them is instant rather than drawing icons on demand.
        apps.WatchForInstalls();   // apps installed while Beacon runs open by name too
        _ = apps.BuildAsync().ContinueWith(
            _ => IconCache.Prewarm(apps.Entries.Select(a => a.IconSource)),
            TaskScheduler.Default);
        _ = bookmarks.BuildAsync();
        StartFileIndexing(fileIndex, settings);

        // Deferred to the first idle moment, once the dispatcher loop is actually running.
        // Doing it inline here would block startup before the hotkey is live - and a warm
        // render is worthless if the hotkey that reaches it was never registered.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _window?.PrewarmRender()));
    }

    /// <summary>
    /// Builds the file index now, then rebuilds it periodically.
    ///
    /// The periodic pass is not optional housekeeping: without administrator rights we
    /// cannot read the NTFS USN journal, so there is no way to be told that a file was
    /// created. Re-walking is the only mechanism available for noticing new files, and an
    /// index that silently goes stale is worse than one that is honestly slow.
    /// </summary>
    private static void StartFileIndexing(FileIndex index, Settings settings)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var options = BuildIndexOptions(settings);

                try
                {
                    await index.BuildAsync(options).ConfigureAwait(false);
                }
                catch
                {
                    // A failed pass should not kill the refresh loop.
                }

                if (settings.IndexRefreshMinutes <= 0) return;

                await Task.Delay(TimeSpan.FromMinutes(settings.IndexRefreshMinutes))
                    .ConfigureAwait(false);
            }
        });
    }

    private static IndexOptions BuildIndexOptions(Settings settings) => new()
    {
        Roots = settings.IndexRoots.Count > 0
            ? settings.IndexRoots
            : FileIndex.FixedDriveRoots(),
        ExcludedFolderNames = settings.ExcludedFolders,
        IncludeHiddenAndSystem = settings.IncludeHiddenAndSystem,
        ExcludedPaths = [Settings.Directory],
    };

    // ---------------------------------------------------------------- hotkeys

    private const string BuiltInPrefix = "cmd:";
    private const string CustomPrefix = "custom:";

    /// <summary>
    /// Registers the launcher chord plus every per-command chord, and reports the ones
    /// Windows refused. Conflicts are surfaced rather than swallowed - a hotkey that
    /// silently does nothing is indistinguishable from a broken app.
    /// </summary>
    private LocalAi.SmartRing? _smartRing;
    private LocalAi.CodeRing? _codeRing;
    private UI.Pet.PetWindow? _pet;
    private Func<UI.Pet.PetWindow>? _makePet;
    private PushToTalkHook? _talkKey;

    private UI.Pet.PetWindow Pet() => _pet ??= _makePet!();
    private Nl.NlParser? _language;
    private Nl.NlRunner? _languageRunner;

    private void ApplyHotkeys()
    {
        if (_hotkeys is null || _settings is null) return;

        var bindings = new List<(string Action, uint Modifiers, uint Key)>
        {
            (HotkeyManager.LauncherAction, _settings.HotkeyModifiers, _settings.HotkeyVirtualKey),
        };

        foreach (var (commandId, binding) in _settings.BuiltInHotkeys)
        {
            if (binding.IsSet)
                bindings.Add((BuiltInPrefix + commandId, binding.Modifiers, binding.VirtualKey));
        }

        foreach (var command in _settings.CustomCommands)
        {
            if (command.HasHotkey)
                bindings.Add((CustomPrefix + command.Id, command.HotkeyModifiers, command.HotkeyVirtualKey));
        }

        var failed = _hotkeys.ReplaceAll(bindings);

        if (_tray is not null)
            _tray.Text = "Beacon — " + HotkeyManager.Describe(
                _settings.HotkeyModifiers, _settings.HotkeyVirtualKey);

        if (failed.Count == 0) return;

        if (failed.Contains(HotkeyManager.LauncherAction))
        {
            Notify("Beacon could not claim its hotkey — another app already owns it. " +
                   "Pick a different one in Settings > Shortcuts.");
        }

        var others = failed.Count(a => a != HotkeyManager.LauncherAction);
        if (others > 0)
            Notify($"{others} command shortcut(s) are already taken by another app.");
    }

    private void OnHotkeyPressed(string action)
    {
        if (action == HotkeyManager.LauncherAction)
        {
            SummonLauncher();
            return;
        }

        if (action.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
            _ = RunBuiltInDirectAsync(action[BuiltInPrefix.Length..]);
        else if (action.StartsWith(CustomPrefix, StringComparison.Ordinal))
            _ = RunCustomDirectAsync(action[CustomPrefix.Length..]);
    }

    /// <summary>
    /// Voice Search command / hotkey (Alt+M): open the bar already listening.
    ///
    /// Pressing it again while it is listening means "done talking", so that case must not
    /// go through Summon() - summoning resets the bar, which would cancel the recording and
    /// start a new one instead of transcribing what was just said.
    /// </summary>
    private void StartVoiceSearch()
    {
        if (_window is { IsVisible: true, IsListening: true })
        {
            _window.StartVoice();
            return;
        }

        SummonLauncher();
        _window?.StartVoice();
    }

    /// <summary>
    /// Names Whisper should be ready to spell - known sites and installed apps. Passed as
    /// its initial prompt; kept short because Whisper only reads the last ~224 tokens.
    /// </summary>
    /// <summary>
    /// Whisper's prompt: words it should expect to hear. Site and app names stop "VS Code"
    /// coming back as "vs cod", and the command verbs bias it towards the phrasings the
    /// language parser can actually act on ("brightness", "shut down", "night light").
    /// </summary>
    private static string BuildVoiceVocabulary(AppIndex apps) =>
        string.Join(", ", CommandPhrases
            .Concat(SiteCatalog.All.Select(s => s.Name))
            .Concat(apps.Entries.Select(a => a.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(70));

    private static readonly string[] CommandPhrases =
    [
        "open", "search for", "volume", "brightness", "mute", "unmute", "night light",
        "Wi-Fi", "Bluetooth", "shut down", "restart", "sign out", "lock", "sleep",
        "close all apps", "snap left", "snap right", "maximise", "minimise", "next monitor",
        "percent", "recycle bin",
    ];

    private void SummonLauncher()
    {
        if (_window is null || _foreground is null) return;

        // Order matters: capture the window the user was in BEFORE we take focus.
        // Once Beacon is foreground, that information is gone.
        _foreground.Capture();
        _window.Summon();
    }

    /// <summary>
    /// Runs a command straight from its chord, without showing the bar.
    ///
    /// The foreground window is captured first for exactly the same reason as a normal
    /// summon: "snap this left" bound to a chord must act on what the user is looking at.
    /// Here nothing ever steals focus, so the capture is purely bookkeeping - but it keeps
    /// one rule for how commands learn their target.
    /// </summary>
    private async Task RunBuiltInDirectAsync(string commandId)
    {
        if (_registry is null || _foreground is null || _settings is null) return;

        var command = _registry.Find(commandId);
        if (command is null) return;

        _foreground.Capture();

        // A chord carries no arguments, so a command that needs one cannot run blind.
        if (command.Parameters.Any(p => p.Required && p.Choices is null))
        {
            Notify($"{command.Title} needs input — open Beacon and type it instead.");
            return;
        }

        try
        {
            var context = new ActionContext
            {
                TargetWindow = _foreground.Captured,
                Arguments = DefaultArguments(command),
                Dismiss = () => { },
                Notify = Notify,
            };

            var message = await command.RunAsync(context).ConfigureAwait(true);

            if (message.StartsWith("Could not", StringComparison.Ordinal) ||
                message.StartsWith("No ", StringComparison.Ordinal))
            {
                Notify(message);
            }
        }
        catch (Exception ex)
        {
            Notify($"{command.Title} failed: {ex.Message}");
        }
    }

    /// <summary>Choice parameters fall back to their first option when fired from a chord.</summary>
    private static Dictionary<string, string> DefaultArguments(BeaconCommand command)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in command.Parameters)
        {
            if (parameter.Choices is { Length: > 0 } choices)
                args[parameter.Name] = choices[0];
        }

        return args;
    }

    private async Task RunCustomDirectAsync(string commandId)
    {
        if (_settings is null || _foreground is null) return;

        var command = _settings.CustomCommands.FirstOrDefault(c => c.Id == commandId);
        if (command is null) return;

        _foreground.Capture();

        if (command.TakesQuery)
        {
            Notify($"{command.Name} needs input — open Beacon and type it instead.");
            return;
        }

        var message = await CustomCommandRunner.RunAsync(
            command,
            string.Empty,
            _foreground.Captured,
            _settings.FolderOpenModeValue,
            _settings.CustomFileManager,
            Notify).ConfigureAwait(true);

        if (command.Kind == CustomCommandKind.PowerShell) Notify(message);
    }

    // ------------------------------------------------------------------ tray

    private void BuildTrayIcon(Settings settings)
    {
        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add("Open Beacon", null, (_, _) => SummonLauncher());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var startup = new Forms.ToolStripMenuItem("Run at startup")
        {
            CheckOnClick = true,
            Checked = settings.RunAtStartup,
        };
        startup.CheckedChanged += (_, _) =>
        {
            settings.RunAtStartup = startup.Checked;
            settings.Save();
            ApplyStartupRegistration(startup.Checked);
        };
        menu.Items.Add(startup);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitCleanly());

        _tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "Beacon — " + HotkeyManager.Describe(
                settings.HotkeyModifiers, settings.HotkeyVirtualKey),
            ContextMenuStrip = menu,
        };

        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) SummonLauncher();
        };
    }

    /// <summary>
    /// The Beacon icon at the tray's native size. SmallIconSize is DPI-aware here (the
    /// process is per-monitor v2), so on a 200% display this picks the 32px frame rather
    /// than blurring the 16px one.
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/Beacon.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // Fall through: a missing icon must never stop the tray from appearing.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void OpenSettings()
    {
        if (_settings is null || _registry is null) return;

        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, _registry, OnSettingsChanged, _favicons);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Called when settings are saved. Re-registers every chord and re-applies startup
    /// registration, so changes take effect without a restart.
    /// </summary>
    private void OnSettingsChanged()
    {
        if (_settings is null) return;

        ApplyHotkeys();
        ApplyStartupRegistration(_settings.RunAtStartup);
        _talkKey?.Use(_settings.PushToTalk);

        _window?.ApplyAppearance(_settings);

        if (_favicons is not null) _favicons.Enabled = _settings.ShowFavicons;
    }

    // ------------------------------------------------------------- startup reg

    /// <summary>
    /// HKCU Run key. Deliberately not a scheduled task: that would be needed only to run
    /// elevated, which this build does not do (see app.manifest).
    /// </summary>
    private static void ApplyStartupRegistration(bool enabled)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "Beacon";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue(valueName, $"\"{exe}\"");
        }
        catch
        {
            // Locked-down profile; startup registration simply won't stick.
        }
    }

    // ---------------------------------------------------------------- shutdown

    private void Notify(string message)
    {
        if (_tray is null)
        {
            Debug.WriteLine(message);
            return;
        }

        _tray.BalloonTipTitle = "Beacon";
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(4000);
    }

    /// <summary>For the updater: leave so the installer can replace Beacon's files.</summary>
    public void Quit() => ExitCleanly();

    private void ExitCleanly()
    {
        _hotkeys?.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _window?.ShutdownForReal();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _talkKey?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
