using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Beacon.Core;
using Beacon.Interop;

namespace Beacon.UI;

public partial class SettingsWindow : Window
{
    private sealed record NavPage(string Title, string Glyph, UIElement Panel);

    private sealed record VoiceModelChoice(Voice.WhisperModel Model)
    {
        public override string ToString() => $"{Model.Label}  —  {Model.SizeLabel}";
    }

    private sealed record DeviceChoice(int Index, string Name)
    {
        public override string ToString() => Name;
    }

    private CancellationTokenSource? _voiceDownloadCts;

    private sealed record ModelChoice(string Id, string Label, string Note)
    {
        /// <summary>
        /// A record's generated ToString() prints every property. Anywhere a template is
        /// missing, that would spill the whole record into the combo box, so give it a
        /// sensible fallback rather than relying on templating being correct everywhere.
        /// </summary>
        public override string ToString() => Label;
    }

    /// <summary>
    /// Wraps a custom command for the list, so the type shows under the name.
    ///
    /// Notifies instead of being rebuilt: replacing the list's items on every keystroke
    /// re-selected the row, which reloaded the editor, which reset the Name box's text and
    /// threw the caret back to the start - making the field effectively untypeable.
    /// </summary>
    private sealed class CustomRow(CustomCommand command) : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new(nameof(Name)));
            PropertyChanged?.Invoke(this, new(nameof(KindLabel)));
        }

        public CustomCommand Command { get; } = command;

        public string Name => string.IsNullOrWhiteSpace(Command.Name) ? "(unnamed)" : Command.Name;

        public string KindLabel => Command.Kind switch
        {
            CustomCommandKind.WebSearch => "Web search",
            CustomCommandKind.Launch => "Program",
            CustomCommandKind.PowerShell => "PowerShell",
            _ => "Path",
        } + (Command.HasHotkey
            ? "  ·  " + HotkeyManager.Describe(Command.HotkeyModifiers, Command.HotkeyVirtualKey)
            : string.Empty);
    }

    private static readonly string[] AccentPresetColors =
        ["#5B8DEF", "#7C6CF0", "#E06C9F", "#E8A33D", "#3FB98C", "#4CC2E0", "#E5554F", "#9AA5B1"];   // #5B8DEF = each style's own colour

    private readonly Settings _settings;
    private readonly CommandRegistry _registry;
    private readonly Action _onSaved;
    private readonly FaviconCache? _favicons;

    /// <summary>
    /// Edited in place and only copied back on Save, so Close genuinely discards changes.
    /// </summary>
    private readonly List<CustomCommand> _draftCommands;
    private readonly Dictionary<string, HotkeyBinding> _draftBuiltInHotkeys;

    private readonly System.Collections.ObjectModel.ObservableCollection<CustomRow> _customRows = [];

    private CustomRow? _editing;
    private bool _suppressEditorEvents;

    public SettingsWindow(
        Settings settings,
        CommandRegistry registry,
        Action onSaved,
        FaviconCache? favicons)
    {
        _settings = settings;
        _registry = registry;
        _onSaved = onSaved;
        _favicons = favicons;

        _draftCommands = settings.CustomCommands.Select(c => c.Clone()).ToList();
        _draftBuiltInHotkeys = settings.BuiltInHotkeys.ToDictionary(
            kv => kv.Key,
            kv => new HotkeyBinding { Modifiers = kv.Value.Modifiers, VirtualKey = kv.Value.VirtualKey });

        InitializeComponent();

        BuildNavigation();
        BuildAccentPresets();
        BuildCommandShortcutList();
        WireEvents();
        LoadFromSettings();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // WPF styles the client area only; without this the dark window gets a white
        // title bar bolted on top.
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    // ------------------------------------------------------------- navigation

    private List<NavPage> _pages = [];

    private void BuildNavigation()
    {
        var pages = _pages = new List<NavPage>
        {
            new("General", "", PageGeneral),
            new("All Commands", "", PageLibrary),
            new("Appearance", "", PageAppearance),
            new("Dotline", "", PageDotline),   // Segoe Fluent Icons: TVMonitor
            new("Filee", "\uE8AB", PageFilee),   // Segoe Fluent Icons: Switch (convert)
            new("Search & Index", "", PageSearch),
            new("Shortcuts", "", PageShortcuts),
            new("Custom Commands", "", PageCustom),
            new("Plain English", "", PageAi),
            new("Voice", "", PageVoice),
            new("Prompt Ring", "\uE8BD", PageLocalAi),
            new("Advanced", "", PageAdvanced),
        };

        Nav.ItemsSource = pages;
        Nav.SelectionChanged += (_, _) =>
        {
            foreach (var page in pages)
                page.Panel.Visibility = Visibility.Collapsed;

            if (Nav.SelectedItem is NavPage selected)
            {
                selected.Panel.Visibility = Visibility.Visible;
                if (ReferenceEquals(selected.Panel, PageSearch)) RefreshIndexStatus();

                // Rebuilt on every visit so it reflects shortcuts and custom commands
                // changed elsewhere in this window, before they are saved.
                if (ReferenceEquals(selected.Panel, PageLibrary)) RefreshLibrary();
            }
        };

        Nav.SelectedIndex = 0;
    }

    private void BuildAccentPresets()
    {
        var swatches = new List<Button>();

        foreach (var hex in AccentPresetColors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();

            var button = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 0),
                Background = brush,
                BorderBrush = brush,
                Padding = new Thickness(0),
                ToolTip = hex,
                Tag = hex,
            };

            button.Click += (_, _) =>
            {
                AccentColor.Text = (string)button.Tag;
                UpdateAccentSwatch();
            };

            swatches.Add(button);
        }

        AccentPresets.ItemsSource = swatches;
    }

    // ---------------------------------------------------------------- wiring

    private void WireEvents()
    {
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();

        // Two recorders, one value: the launcher chord appears on both General and
        // Shortcuts, so keep them in step rather than letting them disagree.
        LauncherHotkey.ChordChanged += () =>
        {
            LauncherHotkeyMirror.Modifiers = LauncherHotkey.Modifiers;
            LauncherHotkeyMirror.VirtualKey = LauncherHotkey.VirtualKey;
            ValidateLauncherHotkey();
        };

        LauncherHotkeyMirror.ChordChanged += () =>
        {
            LauncherHotkey.Modifiers = LauncherHotkeyMirror.Modifiers;
            LauncherHotkey.VirtualKey = LauncherHotkeyMirror.VirtualKey;
            ValidateLauncherHotkey();
        };

        // Same arrangement for the listen chord, which is really the Voice Search command's
        // binding shown on General so it sits next to the chord it partners.
        VoiceHotkey.ChordChanged += () =>
        {
            _draftBuiltInHotkeys[Settings.VoiceCommandId] = new HotkeyBinding
            {
                Modifiers = VoiceHotkey.Modifiers,
                VirtualKey = VoiceHotkey.VirtualKey,
            };

            BuildCommandShortcutList();
            FooterStatus.Text = "Shortcut changes apply when you save.";
        };

        MaxResults.ValueChanged += (_, _) =>
            MaxResultsValue.Text = ((int)MaxResults.Value).ToString();

        TopOffset.ValueChanged += (_, _) =>
            TopOffsetValue.Text = $"{(int)TopOffset.Value}% from the top";

        WindowWidth.ValueChanged += (_, _) =>
            WindowWidthValue.Text = $"{(int)WindowWidth.Value} px";

        FontScale.ValueChanged += (_, _) =>
            FontScaleValue.Text = $"{FontScale.Value * 100:N0}%";

        AccentColor.TextChanged += (_, _) => UpdateAccentSwatch();
        InitFileePage();
        InitLocalAiPage();

        ModelPicker.SelectionChanged += (_, _) =>
        {
            if (ModelPicker.SelectedItem is ModelChoice choice) ModelNote.Text = choice.Note;
        };

        ShortcutFilter.TextChanged += (_, _) =>
        {
            ShortcutFilterHint.Visibility = ShortcutFilter.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            BuildCommandShortcutList(ShortcutFilter.Text);
        };

        RebuildIndexButton.Click += (_, _) =>
        {
            var rebuild = _registry.Find("rebuild_file_index");
            if (rebuild is null) return;

            _ = rebuild.RunAsync(new ActionContext
            {
                TargetWindow = IntPtr.Zero,
                Dismiss = () => { },
                Notify = _ => { },
            });

            FooterStatus.Text = "Rebuilding the index in the background…";
        };

        // ---- custom commands ----
        CustomList.ItemsSource = _customRows;
        CustomList.SelectionChanged += (_, _) =>
        {
            // Only a genuine change of row may reload the editor; anything else would
            // overwrite what is being typed.
            var selected = CustomList.SelectedItem as CustomRow;
            if (!ReferenceEquals(selected, _editing)) LoadEditor(selected);
        };
        AddCustom.Click += (_, _) => AddCustomCommand();
        DeleteCustom.Click += (_, _) => DeleteCustomCommand();

        CustomName.TextChanged += (_, _) => CommitEditor();
        CustomTarget.TextChanged += (_, _) => CommitEditor();
        CustomArgs.TextChanged += (_, _) => CommitEditor();
        CustomKeywords.TextChanged += (_, _) => CommitEditor();
        CustomConfirm.Click += (_, _) => CommitEditor();
        CustomExposeAi.Click += (_, _) => CommitEditor();
        CustomHotkey.ChordChanged += CommitEditor;
        CustomKind.SelectionChanged += (_, _) =>
        {
            UpdateEditorLabels();
            CommitEditor();
        };

        // ---- advanced ----
        FolderMode.SelectionChanged += (_, _) =>
            CustomFileManager.IsEnabled = TagOf(FolderMode) == "custom";

        ClearFavicons.Click += (_, _) =>
        {
            _favicons?.Clear();
            FooterStatus.Text = "Icon cache cleared.";
        };

        OpenConfigFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Settings.Directory);
            Process.Start(new ProcessStartInfo { FileName = Settings.Directory, UseShellExecute = true });
        };

        ResetUsage.Click += (_, _) => ResetUsageHistory();
        ResetAll.Click += (_, _) => ResetEverything();

        // ---- voice ----
        VoiceModel.SelectionChanged += (_, _) => UpdateVoiceModelStatus();
        VoiceDownload.Click += async (_, _) => await DownloadVoiceModelAsync();
        VoiceDelete.Click += (_, _) => DeleteVoiceModel();
        Closed += (_, _) => _voiceDownloadCts?.Cancel();
        Closed += (_, _) => _localDownloadCts?.Cancel();

        UpdateStatus.Text = $"This is version {Updater.Current}.";
        CheckUpdates.Click += async (_, _) => await CheckForUpdateAsync();
        InstallUpdate.Click += async (_, _) => await InstallUpdateAsync();
    }

    // ---------------------------------------------------------------- updates

    private Updater.Release? _update;

    private async Task CheckForUpdateAsync()
    {
        CheckUpdates.IsEnabled = false;
        UpdateStatus.Text = "Checking GitHub…";
        try
        {
            _update = await Updater.CheckAsync(CancellationToken.None);
            UpdateStatus.Text = _update is null
                ? $"You're up to date (version {Updater.Current})."
                : $"Version {_update.Version} is available (you have {Updater.Current}).";
            InstallUpdate.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
            if (_update is not null) InstallUpdate.Content = $"Update to {_update.Version}";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            UpdateStatus.Text = e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? "No release has been published yet."
                : "Couldn't check for updates: " + e.Message;
        }
        finally { CheckUpdates.IsEnabled = true; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is null) return;
        CheckUpdates.IsEnabled = InstallUpdate.IsEnabled = false;
        try
        {
            var setup = await Updater.DownloadAsync(_update,
                new Progress<double>(p => UpdateStatus.Text = $"Downloading {_update.Version}… {p:P0}"), CancellationToken.None);
            UpdateStatus.Text = "Installing. Beacon will restart by itself.";
            Updater.Install(setup);
            Suite.CloseDotline();
            Suite.CloseFilee();
            ((App)Application.Current).Quit();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus.Text = "Update failed, nothing was changed: " + e.Message;
            CheckUpdates.IsEnabled = InstallUpdate.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ voice

    private Voice.WhisperModel? SelectedVoiceModel => (VoiceModel.SelectedItem as VoiceModelChoice)?.Model;

    private void LoadVoiceSettings()
    {
        VoiceEnabled.IsChecked = _settings.VoiceEnabled;
        VoiceAutoStop.IsChecked = _settings.VoiceAutoStop;
        VoiceAutoRun.IsChecked = _settings.VoiceAutoRun;

        var models = Voice.WhisperModels.All.Select(m => new VoiceModelChoice(m)).ToList();
        VoiceModel.ItemsSource = models;
        VoiceModel.SelectedItem = models.FirstOrDefault(c => c.Model.Id == Voice.WhisperModels.Find(_settings.VoiceModel).Id);

        var devices = new List<DeviceChoice> { new(-1, "Windows default microphone") };
        try
        {
            devices.AddRange(Voice.MicRecorder.Devices().Select((name, i) => new DeviceChoice(i, name)));
        }
        catch
        {
            // No audio stack available; the default entry is still meaningful.
        }

        VoiceDevice.ItemsSource = devices;
        VoiceDevice.SelectedItem = devices.FirstOrDefault(d => d.Index == _settings.VoiceDevice) ?? devices[0];

        UpdateVoiceModelStatus();
    }

    private void UpdateVoiceModelStatus()
    {
        var model = SelectedVoiceModel;
        if (model is null) return;

        VoiceModelNote.Text = model.Note;

        var downloaded = Voice.WhisperModels.IsDownloaded(model);
        VoiceModelStatus.Text = downloaded
            ? "✓ Downloaded — ready to use"
            : $"Not downloaded ({model.SizeLabel})";

        VoiceDownload.IsEnabled = !downloaded && _voiceDownloadCts is null;
        VoiceDelete.IsEnabled = downloaded;
    }

    private async Task DownloadVoiceModelAsync()
    {
        var model = SelectedVoiceModel;
        if (model is null || _voiceDownloadCts is not null) return;

        _voiceDownloadCts = new CancellationTokenSource();
        VoiceDownload.IsEnabled = false;
        VoiceModel.IsEnabled = false;

        var progress = new Progress<double>(p => VoiceModelStatus.Text = $"Downloading… {p:P0}");

        try
        {
            await Voice.WhisperModels.DownloadAsync(model, progress, _voiceDownloadCts.Token);
            FooterStatus.Text = $"{model.Label} downloaded and verified.";
        }
        catch (OperationCanceledException)
        {
            FooterStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _voiceDownloadCts = null;
            VoiceModel.IsEnabled = true;
            UpdateVoiceModelStatus();
        }
    }

    private void DeleteVoiceModel()
    {
        var model = SelectedVoiceModel;
        if (model is null) return;

        try
        {
            Voice.WhisperModels.Delete(model);
            FooterStatus.Text = $"{model.Label} removed.";
        }
        catch (Exception ex)
        {
            // Most likely still loaded by the launcher; it is released on restart.
            FooterStatus.Text = $"Couldn't remove it yet: {ex.Message} Restart Beacon and try again.";
        }

        UpdateVoiceModelStatus();
    }

    // ------------------------------------------------------------------ load

    private void LoadFromSettings()
    {
        RunAtStartup.IsChecked = _settings.RunAtStartup;

        LauncherHotkey.Modifiers = _settings.HotkeyModifiers;
        LauncherHotkey.VirtualKey = _settings.HotkeyVirtualKey;
        LauncherHotkeyMirror.Modifiers = _settings.HotkeyModifiers;
        LauncherHotkeyMirror.VirtualKey = _settings.HotkeyVirtualKey;

        _draftBuiltInHotkeys.TryGetValue(Settings.VoiceCommandId, out var voiceChord);
        VoiceHotkey.Modifiers = voiceChord?.Modifiers ?? 0;
        VoiceHotkey.VirtualKey = voiceChord?.VirtualKey ?? 0;

        MaxResults.Value = _settings.MaxResults;
        TopOffset.Value = _settings.TopOffsetPercent;
        WindowWidth.Value = _settings.WindowWidth;
        FontScale.Value = _settings.FontScale;

        SelectByTag(ThemePicker, _settings.OverlayStyle);
        AccentColor.Text = _settings.AccentColor;
        UpdateAccentSwatch();

        ShowFileIcons.IsChecked = _settings.ShowFileIcons;
        ShowSubtitles.IsChecked = _settings.ShowResultSubtitles;

        LoadFileePage();
        LoadLocalAiPage();
        LaunchDotline.IsChecked = _settings.LaunchDotline;
        var dot = Suite.ReadDotline();
        DotBirthday.Text = dot.Birthday?.ToString("yyyy-MM-dd") ?? "";
        DotLifespan.Text = dot.Lifespan.ToString();
        SelectByTag(DotLifeUnit, dot.LifeUnit);
        SelectByTag(DotBackground, dot.Background.ToString());
        DotLocked.IsChecked = dot.Locked;
        DotMap.IsChecked = dot.MapOn;

        IndexRoots.Text = string.Join(Environment.NewLine, _settings.IndexRoots);
        ExcludedFolders.Text = string.Join(Environment.NewLine, _settings.ExcludedFolders);
        IncludeHidden.IsChecked = _settings.IncludeHiddenAndSystem;
        RefreshMinutes.Text = _settings.IndexRefreshMinutes.ToString();
        EverythingPort.Text = _settings.EverythingPort.ToString();

        NaturalLanguage.IsChecked = _settings.NaturalLanguage;
        AiEnabled.IsChecked = _settings.AiEnabled;
        ConfirmDestructive.IsChecked = _settings.ConfirmDestructiveAiActions;
        AiMaxTurns.Text = _settings.AiMaxTurns.ToString();

        ModelPicker.ItemsSource = AiModels.All
            .Select(m => new ModelChoice(m.Id, m.Label, m.Note))
            .ToList();

        var wanted = AiModels.All.Any(m => m.Id == _settings.AiModel) ? _settings.AiModel : AiModels.Default;
        ModelPicker.SelectedItem = (ModelPicker.ItemsSource as IEnumerable<ModelChoice>)
            ?.FirstOrDefault(c => c.Id == wanted);

        // The stored key is never rendered back into a control - only its presence is
        // reported. Putting a decrypted secret on screen is a needless way to leak it.
        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        KeyStatus.Text = !string.IsNullOrWhiteSpace(fromEnv)
            ? "Using ANTHROPIC_API_KEY from your environment. Anything typed here is ignored while that is set."
            : _settings.ApiKeyProtected is not null
                ? "A key is saved, encrypted for this Windows account. Type a new one to replace it."
                : "No key saved. Create one at console.anthropic.com.";

        SelectByTag(FaviconMode, _settings.FaviconMode);
        SelectByTag(FolderMode, _settings.FolderOpenMode);
        CustomFileManager.Text = _settings.CustomFileManager;
        CustomFileManager.IsEnabled = TagOf(FolderMode) == "custom";

        RefreshCustomList();
        RefreshIndexStatus();
        ValidateLauncherHotkey();
        LoadVoiceSettings();
    }

    private void RefreshIndexStatus()
    {
        var roots = _settings.IndexRoots.Count > 0
            ? string.Join(", ", _settings.IndexRoots)
            : string.Join(", ", FileIndexRootsForDisplay());

        IndexStatus.Text = $"Indexing: {roots}\n" +
                           "Rebuilds run in the background; the launcher stays usable throughout.";
    }

    private static IEnumerable<string> FileIndexRootsForDisplay()
    {
        try
        {
            return Providers.FileIndex.FixedDriveRoots();
        }
        catch
        {
            return ["all fixed drives"];
        }
    }

    private void ValidateLauncherHotkey()
    {
        var unset = LauncherHotkey.Modifiers == 0 || LauncherHotkey.VirtualKey == 0;

        HotkeyWarning.Text = unset
            ? "Beacon cannot be opened by keyboard without a shortcut."
            : string.Empty;

        HotkeyWarning.Visibility = unset ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------- command shortcuts

    private void BuildCommandShortcutList(string filter = "")
    {
        var rows = new List<UIElement>();

        foreach (var command in _registry.Commands.OrderBy(c => c.Title))
        {
            // A chord carries no arguments, so a command needing free text cannot be bound.
            if (command.Parameters.Any(p => p.Required && p.Choices is null)) continue;

            if (filter.Length > 0 &&
                FuzzyMatcher.ScoreAny(filter, command.Title, string.Join(' ', command.Keywords))
                    == FuzzyMatcher.NoMatch)
            {
                continue;
            }

            rows.Add(BuildShortcutRow(command));
        }

        CommandShortcuts.ItemsSource = rows;
    }

    private UIElement BuildShortcutRow(BeaconCommand command)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = command.Title,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary"),
        });
        label.Children.Add(new TextBlock
        {
            Text = command.Subtitle,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextTertiary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        _draftBuiltInHotkeys.TryGetValue(command.Id, out var existing);

        var box = new HotkeyBox
        {
            MinWidth = 170,
            Modifiers = existing?.Modifiers ?? 0,
            VirtualKey = existing?.VirtualKey ?? 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        box.ChordChanged += () =>
        {
            if (box.Modifiers == 0 || box.VirtualKey == 0)
            {
                // Stored as an empty binding rather than removed: an absent entry means
                // "never set", which is what lets a command carry a default chord. Clearing
                // one has to stick, so it has to be written down.
                _draftBuiltInHotkeys[command.Id] = new HotkeyBinding();
            }
            else
                _draftBuiltInHotkeys[command.Id] = new HotkeyBinding
                {
                    Modifiers = box.Modifiers,
                    VirtualKey = box.VirtualKey,
                };

            // The listen chord also has a recorder on the General page.
            if (command.Id == Settings.VoiceCommandId)
            {
                VoiceHotkey.Modifiers = box.Modifiers;
                VoiceHotkey.VirtualKey = box.VirtualKey;
            }

            FooterStatus.Text = "Shortcut changes apply when you save.";
        };

        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        return grid;
    }

    // -------------------------------------------------------- custom commands

    /// <summary>
    /// Rebuilds the rows. Only for adds, deletes and loads - never while typing.
    /// </summary>
    private void RefreshCustomList()
    {
        var selectedId = _editing?.Command.Id;

        _customRows.Clear();
        foreach (var command in _draftCommands) _customRows.Add(new CustomRow(command));

        var restore = selectedId is null ? null : _customRows.FirstOrDefault(r => r.Command.Id == selectedId);
        _editing = null;
        CustomList.SelectedItem = restore ?? _customRows.FirstOrDefault();

        var any = _draftCommands.Count > 0;
        CustomEditor.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        CustomEmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddCustomCommand()
    {
        var command = new CustomCommand
        {
            Name = "New command",
            Kind = CustomCommandKind.WebSearch,
            Target = "https://example.com/search?q={query}",
        };

        _draftCommands.Add(command);
        RefreshCustomList();

        CustomList.SelectedItem = (CustomList.ItemsSource as IEnumerable<CustomRow>)
            ?.FirstOrDefault(r => r.Command.Id == command.Id);

        CustomName.Focus();
        CustomName.SelectAll();
    }

    private void DeleteCustomCommand()
    {
        if (_editing is null) return;

        _draftCommands.RemoveAll(c => c.Id == _editing.Command.Id);
        _editing = null;
        RefreshCustomList();

        if (_draftCommands.Count == 0) ClearEditor();
    }

    private void LoadEditor(CustomRow? row)
    {
        _editing = row;
        if (row is null)
        {
            ClearEditor();
            return;
        }

        _suppressEditorEvents = true;
        try
        {
            var command = row.Command;

            CustomName.Text = command.Name;
            SelectByTag(CustomKind, command.Kind.ToString());
            CustomTarget.Text = command.Target;
            CustomArgs.Text = command.Arguments;
            CustomKeywords.Text = command.Keywords;
            CustomHotkey.Modifiers = command.HotkeyModifiers;
            CustomHotkey.VirtualKey = command.HotkeyVirtualKey;
            CustomConfirm.IsChecked = command.RequiresConfirmation;
            CustomExposeAi.IsChecked = command.ExposeToAi;

            UpdateEditorLabels();
            ShowEditorError(command.Validate());
        }
        finally
        {
            _suppressEditorEvents = false;
        }
    }

    private void ClearEditor()
    {
        _suppressEditorEvents = true;
        try
        {
            CustomName.Text = string.Empty;
            CustomTarget.Text = string.Empty;
            CustomArgs.Text = string.Empty;
            CustomKeywords.Text = string.Empty;
            CustomHotkey.Modifiers = 0;
            CustomHotkey.VirtualKey = 0;
            ShowEditorError(null);
        }
        finally
        {
            _suppressEditorEvents = false;
        }
    }

    private void CommitEditor()
    {
        if (_suppressEditorEvents || _editing is null) return;

        var command = _editing.Command;

        // Stored exactly as typed while editing. Trimming here swallowed the space the
        // moment it was typed, so a two-word name could never be entered; values are
        // trimmed once, on save.
        command.Name = CustomName.Text;
        command.Target = CustomTarget.Text;
        command.Arguments = CustomArgs.Text;
        command.Keywords = CustomKeywords.Text;
        command.HotkeyModifiers = CustomHotkey.Modifiers;
        command.HotkeyVirtualKey = CustomHotkey.VirtualKey;
        command.RequiresConfirmation = CustomConfirm.IsChecked == true;
        command.ExposeToAi = CustomExposeAi.IsChecked == true;

        if (Enum.TryParse<CustomCommandKind>(TagOf(CustomKind), out var kind))
            command.Kind = kind;

        ShowEditorError(command.Validate());

        // Update the one row in place - see CustomRow for why the list is not rebuilt.
        _editing.Refresh();
    }

    /// <summary>The target field means something different per type, so relabel it.</summary>
    private void UpdateEditorLabels()
    {
        var kind = Enum.TryParse<CustomCommandKind>(TagOf(CustomKind), out var parsed)
            ? parsed
            : CustomCommandKind.WebSearch;

        var argumentsVisible = kind is CustomCommandKind.Launch;

        CustomArgsLabel.Visibility = argumentsVisible ? Visibility.Visible : Visibility.Collapsed;
        CustomArgs.Visibility = argumentsVisible ? Visibility.Visible : Visibility.Collapsed;

        // The line above can carry its own arguments now, so this field is a convenience.
        CustomArgsLabel.Text = "Arguments (optional, added after the line above)";

        // Launch takes several lines too: one per program, which is what makes a workspace.
        var multiLine = kind is CustomCommandKind.PowerShell or CustomCommandKind.Launch;
        CustomTarget.Height = multiLine ? 140 : double.NaN;
        CustomTarget.AcceptsReturn = multiLine;

        (CustomTargetLabel.Text, CustomTargetHint.Text) = kind switch
        {
            CustomCommandKind.WebSearch => (
                "URL",
                "Use {query} where the typed text goes, e.g. https://github.com/search?q={query}. It is URL-escaped automatically."),
            CustomCommandKind.Launch => (
                "Program, command or address",
                "Anything you could type into the Windows Run box (Win+R): a program by name (notepad), " +
                "a full path, a whole command line (cmd /c dir C:\\), a folder, a URL, a protocol " +
                "(ms-settings:display) or a shell alias (shell:startup). %APPDATA%-style variables are " +
                "expanded, .ps1 files are run rather than opened, and {query} is replaced by what you type. " +
                "Put several on separate lines to open a whole workspace at once — e.g. name it “start work” " +
                "with code D:\\projects\\beacon, D:\\projects and https://mail.google.com on three lines."),
            CustomCommandKind.PowerShell => (
                "PowerShell",
                "Runs with -NoProfile -NonInteractive. Output is shown in the launcher. {query} is substituted literally, so treat typed input as untrusted and quote it yourself."),
            _ => (
                "File or folder",
                "Full path to open. Folders honour the “Opening folders” setting under Advanced."),
        };

        // PowerShell can do arbitrary damage, so default it to asking first.
        if (kind == CustomCommandKind.PowerShell && CustomConfirm.IsChecked != true && !_suppressEditorEvents)
            CustomConfirm.IsChecked = true;
    }

    private void ShowEditorError(string? error)
    {
        CustomError.Text = error ?? string.Empty;
        CustomError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------ maintenance

    private void ResetUsageHistory()
    {
        var answer = MessageBox.Show(
            "Forget which results you pick most often? Search will stop favouring them until it relearns.",
            "Beacon", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            if (File.Exists(Settings.StatsPath)) File.Delete(Settings.StatsPath);
            FooterStatus.Text = "Ranking history cleared. Restart Beacon to apply.";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = $"Could not clear history: {ex.Message}";
        }
    }

    private void ResetEverything()
    {
        var answer = MessageBox.Show(
            "Restore all settings to their defaults?\n\n" +
            "This discards your custom commands, every shortcut you have bound, and your saved " +
            "API key. It cannot be undone.",
            "Beacon", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        var fresh = new Settings();
        fresh.ApplyDefaultShortcuts();

        _settings.HotkeyModifiers = fresh.HotkeyModifiers;
        _settings.HotkeyVirtualKey = fresh.HotkeyVirtualKey;
        _settings.MaxResults = fresh.MaxResults;
        _settings.RunAtStartup = fresh.RunAtStartup;
        _settings.LaunchDotline = fresh.LaunchDotline;
        _settings.LaunchFilee = fresh.LaunchFilee;
        _settings.FileeFollowsLook = fresh.FileeFollowsLook;
        _settings.Theme = fresh.Theme;
        _settings.AccentColor = fresh.AccentColor;
        _settings.WindowWidth = fresh.WindowWidth;
        _settings.TopOffsetPercent = fresh.TopOffsetPercent;
        _settings.FontScale = fresh.FontScale;
        _settings.ShowFileIcons = fresh.ShowFileIcons;
        _settings.ShowResultSubtitles = fresh.ShowResultSubtitles;
        _settings.IndexRoots = fresh.IndexRoots;
        _settings.ExcludedFolders = fresh.ExcludedFolders;
        _settings.IncludeHiddenAndSystem = fresh.IncludeHiddenAndSystem;
        _settings.IndexRefreshMinutes = fresh.IndexRefreshMinutes;
        _settings.EverythingPort = fresh.EverythingPort;
        _settings.FaviconMode = fresh.FaviconMode;
        _settings.FolderOpenMode = fresh.FolderOpenMode;
        _settings.CustomFileManager = fresh.CustomFileManager;
        _settings.NaturalLanguage = fresh.NaturalLanguage;
        _settings.VoiceAutoRun = fresh.VoiceAutoRun;
        _settings.AiEnabled = fresh.AiEnabled;
        _settings.AiModel = fresh.AiModel;
        _settings.ConfirmDestructiveAiActions = fresh.ConfirmDestructiveAiActions;
        _settings.AiMaxTurns = fresh.AiMaxTurns;
        _settings.CustomCommands = [];
        _settings.BuiltInHotkeys = new Dictionary<string, HotkeyBinding>(fresh.BuiltInHotkeys);
        _settings.SetApiKey(null);

        _settings.Save();
        _onSaved();
        Suite.RefreshDotline();   // the accent colour may have changed
        Suite.SyncFileeLook(_settings);

        _draftCommands.Clear();
        _draftBuiltInHotkeys.Clear();

        // Defaults include Alt+M for voice, so restoring defaults must bring it back.
        foreach (var (id, binding) in fresh.BuiltInHotkeys) _draftBuiltInHotkeys[id] = binding;

        LoadFromSettings();
        BuildCommandShortcutList();
        FooterStatus.Text = "Settings restored to defaults.";
    }

    // ------------------------------------------------------------------ save

    /// <summary>Applies and saves everything; the window stays open (Close leaves).</summary>
    private void Save()
    {
        var invalid = _draftCommands.FirstOrDefault(c => c.Validate() is not null);
        if (invalid is not null)
        {
            ShowPage(PageCustom);
            CustomList.SelectedItem = (CustomList.ItemsSource as IEnumerable<CustomRow>)
                ?.FirstOrDefault(r => r.Command.Id == invalid.Id);

            ShowEditorError(invalid.Validate());
            FooterStatus.Text = "Fix the highlighted command before saving.";
            return;
        }

        if (LauncherHotkey.Modifiers == 0 || LauncherHotkey.VirtualKey == 0)
        {
            var answer = MessageBox.Show(
                "No launcher shortcut is set. You will only be able to open Beacon from its tray icon.\n\n" +
                "Save anyway?",
                "Beacon", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                Nav.SelectedIndex = 0;
                return;
            }
        }

        if (ReadDotlineForm(out var dotline) is { } dotlineError)
        {
            ShowPage(PageDotline);
            FooterStatus.Text = dotlineError;
            return;
        }

        var oldLook = (_settings.OverlayStyle, _settings.AccentColor);

        _settings.RunAtStartup = RunAtStartup.IsChecked == true;
        _settings.LaunchDotline = LaunchDotline.IsChecked == true;
        _settings.LaunchFilee = LaunchFilee.IsChecked == true;
        _settings.FileeFollowsLook = FileeFollowsLook.IsChecked == true;
        _settings.HotkeyModifiers = LauncherHotkey.Modifiers;
        _settings.HotkeyVirtualKey = LauncherHotkey.VirtualKey;

        _settings.MaxResults = (int)MaxResults.Value;
        _settings.TopOffsetPercent = TopOffset.Value;
        _settings.WindowWidth = WindowWidth.Value;
        _settings.FontScale = FontScale.Value;

        _settings.OverlayStyle = TagOf(ThemePicker) ?? "tactical";
        _settings.AccentColor = string.IsNullOrWhiteSpace(AccentColor.Text) ? "#5B8DEF" : AccentColor.Text.Trim();
        _settings.ShowFileIcons = ShowFileIcons.IsChecked == true;
        _settings.ShowResultSubtitles = ShowSubtitles.IsChecked == true;

        _settings.IndexRoots = SplitLines(IndexRoots.Text);
        _settings.ExcludedFolders = SplitLines(ExcludedFolders.Text);
        _settings.IncludeHiddenAndSystem = IncludeHidden.IsChecked == true;

        if (int.TryParse(RefreshMinutes.Text, out var refresh))
            _settings.IndexRefreshMinutes = Math.Clamp(refresh, 0, 1440);

        if (int.TryParse(EverythingPort.Text, out var port) && port is > 0 and <= 65535)
            _settings.EverythingPort = port;

        _settings.NaturalLanguage = NaturalLanguage.IsChecked == true;
        _settings.AiEnabled = AiEnabled.IsChecked == true;
        _settings.ConfirmDestructiveAiActions = ConfirmDestructive.IsChecked == true;

        if (int.TryParse(AiMaxTurns.Text, out var turns))
            _settings.AiMaxTurns = Math.Clamp(turns, 1, 20);

        if (ModelPicker.SelectedItem is ModelChoice model) _settings.AiModel = model.Id;

        if (ApiKeyBox.Password.Length > 0)
        {
            _settings.SetApiKey(ApiKeyBox.Password);
            ApiKeyBox.Clear();
        }

        _settings.VoiceEnabled = VoiceEnabled.IsChecked == true;
        _settings.VoiceAutoStop = VoiceAutoStop.IsChecked == true;
        _settings.VoiceAutoRun = VoiceAutoRun.IsChecked == true;
        if (SelectedVoiceModel is { } voiceModel) _settings.VoiceModel = voiceModel.Id;
        if (LocalModelPicker.SelectedItem is LocalModelChoice localModel) _settings.LocalModel = localModel.Model.Id;
        if (VoiceDevice.SelectedItem is DeviceChoice device) _settings.VoiceDevice = device.Index;

        _settings.FaviconMode = TagOf(FaviconMode) ?? "direct";
        _settings.FolderOpenMode = TagOf(FolderMode) ?? "reuse";
        _settings.CustomFileManager = CustomFileManager.Text.Trim();

        _settings.CustomCommands = _draftCommands.Select(c =>
        {
            var copy = c.Clone();
            copy.Name = copy.Name.Trim();
            copy.Target = copy.Target.Trim();
            copy.Arguments = copy.Arguments.Trim();
            copy.Keywords = copy.Keywords.Trim();
            return copy;
        }).ToList();
        _settings.BuiltInHotkeys = new Dictionary<string, HotkeyBinding>(_draftBuiltInHotkeys);

        _settings.Save();
        _onSaved();

        // Dotline reads Beacon's look when it starts, so it is restarted after the save above:
        // by WriteDotline when its own settings changed, otherwise here if only the look did.
        var dotlineChanged = dotline != Suite.ReadDotline();
        if (Suite.WriteDotline(dotline) is { } writeError)
            MessageBox.Show(writeError, "Beacon", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!dotlineChanged && oldLook != (_settings.OverlayStyle, _settings.AccentColor))
            Suite.RefreshDotline();

        // Filee follows the look by itself (it watches theme.json); its ring options need a restart.
        if (Suite.SyncFileeLook(_settings) is { } lookError)
            MessageBox.Show(lookError, "Beacon", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (Suite.WriteFilee(ReadFileeForm()) is { } fileeError)
            MessageBox.Show(fileeError, "Beacon", MessageBoxButton.OK, MessageBoxImage.Warning);

        FooterStatus.Text = $"Saved at {DateTime.Now:t}.";
    }

    /// <summary>The Dotline page as options, or a message saying what to fix.</summary>
    private string? ReadDotlineForm(out Suite.DotlineOptions options)
    {
        options = Suite.ReadDotline();
        var birthday = options.Birthday;   // left blank: keep what Dotline has
        var text = DotBirthday.Text.Trim();
        if (text.Length > 0)
        {
            if (!DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) || d > DateTime.Today || d.Year < 1900)
                return "Enter the Dotline birthday as YYYY-MM-DD, e.g. 2001-08-14.";
            birthday = d;
        }

        if (!int.TryParse(DotLifespan.Text.Trim(), out var lifespan) || lifespan is < 1 or > 120)
            return "Dotline lifespan must be a whole number from 1 to 120.";

        options = new Suite.DotlineOptions(
            birthday,
            lifespan,
            TagOf(DotLifeUnit) ?? "week",
            int.TryParse(TagOf(DotBackground), out var background) ? background : 1,
            DotLocked.IsChecked == true,
            DotMap.IsChecked == true);
        return null;
    }

    // --------------------------------------------------------------- helpers

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? TagOf(Selector selector) =>
        (selector.SelectedItem as FrameworkElement)?.Tag as string;

    private static void SelectByTag(Selector selector, string? tag)
    {
        if (tag is null) return;

        foreach (var item in selector.Items)
        {
            if (item is FrameworkElement element &&
                string.Equals(element.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                selector.SelectedItem = item;
                return;
            }
        }

        if (selector.SelectedIndex < 0 && selector.Items.Count > 0) selector.SelectedIndex = 0;
    }

    private void UpdateAccentSwatch()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(AccentColor.Text.Trim());
            AccentSwatch.Background = new SolidColorBrush(color);
        }
        catch
        {
            // Half-typed hex; leave the previous swatch until it parses again.
        }
    }
}
