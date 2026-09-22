using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Beacon.Core;
using Beacon.Interop;
using Beacon.Providers;

namespace Beacon.UI;

/// <summary>
/// The "All Commands" page: a gallery of everything Beacon can do, with what each thing is
/// for, how to call it, and its shortcut.
///
/// Built from the live sources - the command registry, the draft custom commands and draft
/// shortcuts, the site catalog - rather than a hand-written list, so it cannot drift out of
/// date. Draft values are used on purpose: a shortcut assigned on the Shortcuts page shows
/// up here immediately, before Save.
/// </summary>
public partial class SettingsWindow
{
    private sealed record LibraryCategory(string Name, string Label);

    private sealed class CommandCard
    {
        public required string Category { get; init; }
        public required string Glyph { get; init; }
        public required string Title { get; init; }
        public required string Purpose { get; init; }
        public string Detail { get; init; } = string.Empty;
        public required string HowToUse { get; init; }
        public string Hotkey { get; init; } = string.Empty;
        public string NoHotkeyText { get; init; } = "None";
        public bool IsDestructive { get; init; }
        public string ActionLabel { get; init; } = string.Empty;
        public Action? Action { get; init; }
        public string Extra { get; init; } = string.Empty;
        public Brush AccentBrush { get; init; } = Brushes.White;
        public Brush TileBrush { get; init; } = Brushes.Transparent;

        public string SearchText =>
            $"{Title} {Category} {Purpose} {Detail} {HowToUse} {Hotkey} {Extra}";

        public Visibility DetailVisibility => Show(Detail.Length > 0);
        public Visibility HotkeyVisibility => Show(Hotkey.Length > 0);
        public Visibility NoHotkeyVisibility => Show(Hotkey.Length == 0);
        public Visibility ActionVisibility => Show(Action is not null);
        public Visibility DestructiveVisibility => Show(IsDestructive);

        private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Also the display order: features first, the long tail of sites last.</summary>
    private static readonly (string Name, Color Color)[] Categories =
    [
        ("Beacon", Color.FromRgb(0x4C, 0xC2, 0xE0)),
        ("Keyboard", Color.FromRgb(0xA7, 0xB1, 0xBD)),
        ("Window", Color.FromRgb(0x5B, 0x8D, 0xEF)),
        ("System", Color.FromRgb(0xF0, 0x6A, 0x5F)),
        ("Media", Color.FromRgb(0xE0, 0x6C, 0x9F)),
        ("Custom", Color.FromRgb(0xE8, 0xA3, 0x3D)),
        ("Claude", Color.FromRgb(0x9D, 0x8C, 0xF5)),
        ("Web", Color.FromRgb(0x3F, 0xB9, 0x8C)),
    ];

    /// <summary>
    /// Plain-language examples for commands that only Claude can run - they need details
    /// (which process, which path) that a single typed row or a hotkey cannot supply.
    /// </summary>
    private static readonly Dictionary<string, string> ClaudeExamples = new()
    {
        ["kill_process"] = "? kill chrome",
        ["open_path"] = "? open my downloads folder",
        ["launch_app"] = "? open spotify",
        ["search_files"] = "? find my resume pdf",
        ["web_search"] = "? search youtube for lofi music",
        ["open_website"] = "? open github",
        ["list_windows"] = "? what windows are open",
    };

    private List<CommandCard> _library = [];
    private bool _libraryWired;

    public static readonly DependencyProperty LibraryCardWidthProperty =
        DependencyProperty.Register(nameof(LibraryCardWidth), typeof(double), typeof(SettingsWindow),
            new PropertyMetadata(314d));

    /// <summary>Width of one gallery slot, card plus gap; see the WrapPanel in the page.</summary>
    public double LibraryCardWidth
    {
        get => (double)GetValue(LibraryCardWidthProperty);
        set => SetValue(LibraryCardWidthProperty, value);
    }

    private const double MinCardSlot = 290;

    private void UpdateLibraryColumns()
    {
        var available = LibraryGallery.ActualWidth;
        if (available <= 0) return;

        var columns = Math.Max(1, (int)(available / MinCardSlot));
        LibraryCardWidth = Math.Floor(available / columns);
    }

    private void RefreshLibrary()
    {
        if (!_libraryWired)
        {
            _libraryWired = true;

            LibraryFilter.TextChanged += (_, _) =>
            {
                LibraryFilterHint.Visibility = LibraryFilter.Text.Length == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                ApplyLibraryFilter();
            };

            LibraryCategories.SelectionChanged += (_, _) => ApplyLibraryFilter();
            LibraryGallery.SizeChanged += (_, _) => UpdateLibraryColumns();

            // One handler for every card's button, rather than wiring each generated card.
            LibraryGallery.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is Button { DataContext: CommandCard card }) card.Action?.Invoke();
            }));
        }

        var selected = (LibraryCategories.SelectedItem as LibraryCategory)?.Name ?? "All";

        _library = BuildLibrary();

        var chips = new List<LibraryCategory> { new("All", $"All  {_library.Count}") };
        chips.AddRange(Categories
            .Select(c => (c.Name, Count: _library.Count(card => card.Category == c.Name)))
            .Where(c => c.Count > 0)
            .Select(c => new LibraryCategory(c.Name, $"{c.Name}  {c.Count}")));

        LibraryCategories.ItemsSource = chips;
        LibraryCategories.SelectedItem = chips.FirstOrDefault(c => c.Name == selected) ?? chips[0];

        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        var category = (LibraryCategories.SelectedItem as LibraryCategory)?.Name ?? "All";
        var words = LibraryFilter.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var shown = _library
            .Where(card => category == "All" || card.Category == category)
            .Where(card => words.All(w => card.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        LibraryGallery.ItemsSource = shown;
        LibraryEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------- building

    private List<CommandCard> BuildLibrary()
    {
        var cards = new List<CommandCard>();
        cards.AddRange(FeatureCards());
        cards.AddRange(RegistryCards());
        cards.AddRange(CustomCards());
        cards.AddRange(SiteCards());

        var order = Categories.Select(c => c.Name).ToList();
        return cards
            .OrderBy(c => order.IndexOf(c.Category) is var i and >= 0 ? i : order.Count)
            .ToList();
    }

    /// <summary>The things Beacon does that are not a single named command.</summary>
    private IEnumerable<CommandCard> FeatureCards()
    {
        var launcher = HotkeyManager.Describe(LauncherHotkey.Modifiers, LauncherHotkey.VirtualKey);

        yield return Card("Beacon", "", "Open Beacon",
            purpose: "Summon the search bar on top of whatever you're doing.",
            how: "Press the shortcut from anywhere, or click the Beacon icon in the system tray.",
            detail: "Commands act on the window you were in, not on Beacon itself.",
            hotkey: launcher == "None" ? "" : launcher,
            actionLabel: "Change", action: () => ShowPage(PageGeneral));

        yield return Card("Beacon", "", "Launch apps",
            purpose: "Start any installed app - desktop or Store.",
            how: "Type part of its name - “code”, “edge”, “calc” - then Enter. Apps you open often rise to the top.",
            noHotkey: "Type in the bar");

        yield return Card("Beacon", "", "Find files and folders",
            purpose: "Find any file or folder on your drives.",
            how: "Type part of the name, e.g. “wifi_scan”. Include \\ to match the folder path, e.g. “radar\\scan”.",
            detail: "Install Everything for live, instant results across the whole disk.",
            noHotkey: "Type in the bar",
            actionLabel: "Index", action: () => ShowPage(PageSearch));

        yield return Card("Beacon", "", "Websites and bookmarks",
            purpose: "Open sites by name, your browser bookmarks, or any address.",
            how: "Type “github”, a bookmark's title, or an address like “localhost:3000”.",
            noHotkey: "Type in the bar");

        yield return Card("Beacon", "", "Search inside a site",
            purpose: "Turn the bar into a search box for one site, with its logo.",
            how: "Type a site's name and press Tab - or a prefix and a space: “yt ”, “g ”, “gh ”. Backspace on an empty bar leaves.",
            noHotkey: "Tab in the bar");

        yield return Card("Beacon", "", "Calculator",
            purpose: "Do arithmetic without opening an app.",
            how: "Type a sum like “(12+8)*3” or “2^10”. Enter copies the answer.",
            noHotkey: "Type in the bar");

        var keyReady = !string.IsNullOrWhiteSpace(_settings.ApiKey);
        yield return Card("Claude", "", "Ask Claude",
            purpose: "Say what you want in plain words; Claude runs the commands for you.",
            how: "Type “?” then an instruction: “? snap this left and mute the volume”.",
            detail: keyReady
                ? $"Ready - using {AiModels.LabelFor(_settings.AiModel)}. Destructive steps still ask first."
                : "Needs an Anthropic API key - add one on the Claude page.",
            noHotkey: "Type ? in the bar",
            actionLabel: keyReady ? "Settings" : "Set up", action: () => ShowPage(PageAi));

        yield return Card("Keyboard", "", "Keys in the bar",
            purpose: "Everything you can do without the mouse.",
            how: "↑ ↓   move between results\n" +
                 "Enter   run it (dangerous ones: press twice)\n" +
                 "Tab   search inside a site, or complete the name\n" +
                 "→   accept the grey suggestion\n" +
                 "Ctrl+M   voice search\n" +
                 "Backspace   leave a site search\n" +
                 "Esc   step back, then close",
            noHotkey: "Built in");
    }

    private IEnumerable<CommandCard> RegistryCards()
    {
        foreach (var command in _registry.Commands)
        {
            var needsText = command.Parameters.Any(p => p.Required && p.Choices is null);
            var bindable = !needsText;

            _draftBuiltInHotkeys.TryGetValue(command.Id, out var binding);
            var hotkey = binding is { IsSet: true }
                ? HotkeyManager.Describe(binding.Modifiers, binding.VirtualKey)
                : string.Empty;

            var purpose = string.IsNullOrWhiteSpace(command.Subtitle) ? command.Title : command.Subtitle;
            var detail = Humanize(command.Description);
            if (string.Equals(detail.TrimEnd('.'), purpose.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                detail = string.Empty;

            var title = command.Title;

            yield return Card(
                command.Category.Length > 0 ? command.Category : "Beacon",
                command.Glyph,
                title,
                purpose: purpose,
                how: HowToCall(command, needsText),
                detail: detail,
                hotkey: hotkey,
                noHotkey: bindable ? "None" : "Can't bind - needs details",
                destructive: command.IsDestructive,
                actionLabel: bindable ? (hotkey.Length > 0 ? "Change" : "Set shortcut") : string.Empty,
                action: bindable ? () => OpenShortcutFor(title) : null,
                extra: string.Join(' ', command.Keywords) + " " + command.Id);
        }
    }

    private static string HowToCall(BeaconCommand command, bool needsText)
    {
        var lower = command.Title.ToLowerInvariant();

        if (command.Id == "voice_search")
            return "Click the \U0001F3A4 in the bar or press Ctrl+M, then speak and pause. Or type “voice search”.";

        if (!command.ShowInSearch || needsText)
        {
            var example = ClaudeExamples.TryGetValue(command.Id, out var e) ? e : $"? {lower}";
            return needsText && command.ShowInSearch
                ? $"Needs details, so ask Claude: “{example}”."
                : $"Used by Claude when you ask. Try: “{example}”.";
        }

        if (command.Parameters is [{ Choices: { Length: > 0 } choices }])
        {
            var rest = string.Join(", ", choices.Skip(1).Take(6));
            return $"Type “{lower} {choices[0]}” - or {rest}.";
        }

        var keywords = command.Keywords.Take(4).ToList();
        return keywords.Count > 0
            ? $"Type “{lower}” - also found by {string.Join(", ", keywords.Select(k => $"“{k}”"))}."
            : $"Type “{lower}”.";
    }

    private IEnumerable<CommandCard> CustomCards()
    {
        foreach (var command in _draftCommands.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
        {
            var name = command.Name.Trim();
            var lower = name.ToLowerInvariant();

            var purpose = !string.IsNullOrWhiteSpace(command.Description)
                ? command.Description
                : command.Kind switch
                {
                    CustomCommandKind.WebSearch when command.TakesQuery => $"Search {HostOf(command.Target)}.",
                    CustomCommandKind.WebSearch => $"Open {HostOf(command.Target)}.",
                    CustomCommandKind.Launch => $"Run {System.IO.Path.GetFileName(command.Target)}.",
                    CustomCommandKind.PowerShell => "Run a PowerShell snippet and show its output.",
                    _ => $"Open {command.Target}.",
                };

            var how = command.TakesQuery
                ? $"Type “{lower} ” followed by what you want, e.g. “{lower} hello”."
                : $"Type “{lower}”.";

            if (!string.IsNullOrWhiteSpace(command.Keywords))
                how += $" Also found by: {command.Keywords.Trim()}.";

            var id = command.Id;

            yield return Card("Custom", command.Glyph, name,
                purpose: purpose,
                how: how,
                detail: command.ExposeToAi ? "Claude can use this too." : string.Empty,
                hotkey: command.HasHotkey
                    ? HotkeyManager.Describe(command.HotkeyModifiers, command.HotkeyVirtualKey)
                    : string.Empty,
                noHotkey: command.TakesQuery ? "Can't bind - needs a query" : "None",
                destructive: command.RequiresConfirmation,
                actionLabel: "Edit",
                action: () =>
                {
                    ShowPage(PageCustom);
                    CustomList.SelectedItem = _customRows.FirstOrDefault(r => r.Command.Id == id);
                });
        }
    }

    private IEnumerable<CommandCard> SiteCards()
    {
        foreach (var site in SiteCatalog.All)
        {
            var lower = site.Name.ToLowerInvariant();

            var how = site.CanSearch
                ? $"Type “{lower}” - Enter opens it, Tab searches it."
                : $"Type “{lower}” and press Enter.";

            if (site.Prefix is not null && site.CanSearch)
                how += $" Or “{site.Prefix} ” then your search.";

            var aliases = site.Aliases is { Length: > 0 }
                ? "Also found by: " + string.Join(", ", site.Aliases.Take(4))
                : string.Empty;

            yield return Card("Web", site.Glyph, site.Name,
                purpose: site.CanSearch ? $"Open {site.Domain}, or search it." : $"Open {site.Domain}.",
                how: how,
                detail: aliases,
                noHotkey: "Type in the bar",
                extra: site.Domain);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static CommandCard Card(
        string category, string glyph, string title,
        string purpose, string how,
        string detail = "", string hotkey = "", string noHotkey = "None",
        bool destructive = false, string actionLabel = "", Action? action = null, string extra = "")
    {
        var color = Categories.FirstOrDefault(c => c.Name == category).Color;
        if (color == default) color = Color.FromRgb(0x9A, 0xA5, 0xB1);

        var accent = new SolidColorBrush(color);
        accent.Freeze();
        var tile = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
        tile.Freeze();

        return new CommandCard
        {
            Category = category,
            Glyph = glyph,
            Title = title,
            Purpose = purpose,
            Detail = detail,
            HowToUse = how,
            Hotkey = hotkey,
            NoHotkeyText = noHotkey,
            IsDestructive = destructive,
            ActionLabel = actionLabel,
            Action = action,
            Extra = extra,
            AccentBrush = accent,
            TileBrush = tile,
        };
    }

    /// <summary>
    /// Command descriptions are written for the model ("the user's previously focused
    /// window"). Reword them for a person reading their own settings.
    /// </summary>
    private static string Humanize(string text) => text
        .Replace("the user's previously focused window", "the window you were in", StringComparison.OrdinalIgnoreCase)
        .Replace("The user's", "Your", StringComparison.Ordinal)
        .Replace("the user's", "your", StringComparison.Ordinal)
        .Replace("The user", "You", StringComparison.Ordinal)
        .Replace("the user", "you", StringComparison.Ordinal)
        .Replace(" Use this first when you refers", " Useful when you refer", StringComparison.Ordinal);

    private static string HostOf(string urlTemplate)
    {
        var cleaned = urlTemplate.Replace(CustomCommand.QueryToken, "", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) ? uri.Host : urlTemplate;
    }

    private void ShowPage(UIElement panel) =>
        Nav.SelectedItem = _pages.FirstOrDefault(p => ReferenceEquals(p.Panel, panel));

    private void OpenShortcutFor(string title)
    {
        ShowPage(PageShortcuts);
        ShortcutFilter.Text = title;
        ShortcutFilter.Focus();
    }
}
