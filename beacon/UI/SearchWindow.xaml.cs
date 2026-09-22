using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Beacon.Ai;
using Beacon.Core;
using Beacon.Interop;
using Beacon.Nl;
using Beacon.Providers;
using Beacon.Voice;

namespace Beacon.UI;

/// <summary>A single rendered row. Resolves its icon once, at construction.</summary>
public sealed class ResultRow
{
    public SearchResult Result { get; }

    public ResultRow(SearchResult result, FaviconCache? favicons = null)
    {
        Result = result;

        // Shell icon first (files and apps), then the site icon. A favicon miss returns
        // null immediately and fetches in the background, so this never blocks rendering.
        Icon = IconCache.Get(result.IconPath);

        if (Icon is null && result.FaviconUrl is not null)
        {
            Icon = favicons?.Get(result.FaviconUrl);
            IsFavicon = Icon is not null;
        }
    }

    /// <summary>Site icons get a light backing tile; see the row template.</summary>
    public bool IsFavicon { get; }

    public Visibility FaviconVisibility => IsFavicon ? Visibility.Visible : Visibility.Collapsed;

    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;
    public string Glyph => Result.Glyph;
    public string Source => Result.Source;
    public ImageSource? Icon { get; }

    public Visibility IconVisibility => Icon is null || IsFavicon ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SubtitleVisibility =>
        string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

    // Set by SearchWindow.ApplyStyle; rows read them when they are created.
    public static bool HudStyle { get; set; }
    public static Brush GlyphBrush { get; set; } = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DangerBrush = Frozen(Color.FromRgb(0xFF, 0x8A, 0x7A));

    /// <summary>"App" in Classic, "[ APP ]" readout-style in HUD.</summary>
    public string SourceTag =>
        HudStyle && Source.Length > 0 ? $"[ {Source.ToUpperInvariant()} ]" : Source;

    /// <summary>Destructive rows get a warning tint on their glyph.</summary>
    public Brush AccentBrush => Result.IsDestructive ? DangerBrush : GlyphBrush;

    internal static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

public partial class SearchWindow : Window
{
    /// <summary>
    /// Long enough to coalesce fast typing, short enough to feel instant. Providers are
    /// also cancelled on each keystroke, so a slow one never blocks a newer query.
    /// </summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(55);

    private readonly IReadOnlyList<IResultProvider> _providers;
    private readonly ForegroundTracker _foreground;
    private readonly Settings _settings;
    private readonly AiPlanner _planner;
    private readonly AiProvider _ai;
    private readonly FaviconCache _favicons;
    private readonly Action<string> _notify;
    private readonly NlParser _language;
    private readonly NlRunner _languageRunner;

    /// <summary>The last rendered set, kept so a late-arriving favicon can redraw it.</summary>
    private Dictionary<string, IReadOnlyList<SearchResult>>? _lastRendered;

    private readonly ObservableCollection<ResultRow> _rows = [];

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _aiCts;

    /// <summary>Set when a destructive row is selected and awaiting its second Enter.</summary>
    private SearchResult? _armed;

    /// <summary>Set while an AI run is paused on a confirmation.</summary>
    private TaskCompletionSource<bool>? _pendingConfirm;

    private bool _aiRunning;
    private bool _reallyClosing;

    /// <summary>Full text the inline ghost completion would commit to, or null when none.</summary>
    private string? _prediction;

    /// <summary>The site being searched inside, or null for normal search.</summary>
    private SearchScope? _scope;

    /// <summary>How a described search was read ("searching for files named ..."), or null.</summary>
    private string? _interpretation;

    /// <summary>True once the user moved the highlight themselves, until the query changes.</summary>
    private bool _selectionIsUsers;

    private string _defaultPlaceholder = string.Empty;

    /// <summary>Suppresses the hide-on-deactivate behaviour during the startup warm-up.</summary>
    private bool _prewarming;
    private double _savedOpacity = 1;
    private bool _savedShowActivated = true;

    /// <summary>Work area of the display the overlay is currently on, in WPF units.</summary>
    private Rect _workArea;

    // ---- voice ----
    private enum VoiceState { Idle, Downloading, Listening, Transcribing }

    private readonly MicRecorder _recorder = new();
    private readonly Transcriber _transcriber = new();
    private readonly Func<string>? _vocabulary;
    private VoiceState _voiceState = VoiceState.Idle;

    /// <summary>Cancels the whole voice operation (Esc, dismiss).</summary>
    private CancellationTokenSource? _voiceCts;

    /// <summary>Ends recording early and transcribes what was heard (mic click, Ctrl+M).</summary>
    private CancellationTokenSource? _voiceStop;

    public SearchWindow(
        IReadOnlyList<IResultProvider> providers,
        ForegroundTracker foreground,
        Settings settings,
        AiPlanner planner,
        AiProvider ai,
        FaviconCache favicons,
        Action<string> notify,
        NlParser language,
        NlRunner languageRunner,
        Func<string>? vocabulary = null)
    {
        _providers = providers;
        _foreground = foreground;
        _settings = settings;
        _planner = planner;
        _ai = ai;
        _favicons = favicons;
        _notify = notify;
        _language = language;
        _languageRunner = languageRunner;
        _vocabulary = vocabulary;

        InitializeComponent();

        _defaultPlaceholder = Placeholder.Text;

        // The HUD frame's cut corners are built to the window's real size.
        Chrome.SizeChanged += (_, _) => UpdateHudGeometry();

        _hudClock = new DispatcherTimer(
            TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => UpdateReadouts(), Dispatcher);
        _hudClock.Stop();

        MicButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _ = ToggleVoiceAsync();
        };
        _recorder.Level += OnMicLevel;

        // Icons resolve after the row was already drawn, so redraw when one lands.
        _favicons.IconArrived += OnIconArrived;

        ResultsList.ItemsSource = _rows;
        QueryBox.TextChanged += OnQueryChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        ResultsList.MouseLeftButtonUp += OnRowClicked;
        SizeChanged += (_, _) => ClampToWorkArea();
        Deactivated += (_, _) => { if (!_aiRunning && !_prewarming) Dismiss(); };

        _ai.Requested += RunAiAsync;
    }

    // -------------------------------------------------------------- lifecycle

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        _foreground.BindSelf(hwnd);

        // WS_EX_TOOLWINDOW keeps Beacon out of Alt+Tab and the taskbar; without it the
        // overlay shows up as a switchable window, which is jarring for something that
        // is meant to appear and vanish.
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        ex &= ~(long)NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>
    /// Realises the HWND without showing the window, so window creation is done up front.
    /// </summary>
    public void SourceInitializedOnce() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// Applies appearance settings to the live window, so saving in Settings takes effect
    /// immediately rather than at next launch.
    /// </summary>
    public void ApplyAppearance(Settings settings)
    {
        Width = Math.Clamp(settings.WindowWidth, 420, 1400);

        ApplyStyle(settings);

        var scale = Math.Clamp(settings.FontScale, 0.8, 1.6);
        QueryBox.FontSize = 21 * scale;
        Placeholder.FontSize = 21 * scale;
        Ghost.FontSize = 21 * scale;

        // Re-running the layout pass keeps the results panel's height cap in step with the
        // new width and font size.
        if (IsVisible) PositionOnActiveScreen();
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        try
        {
            var converted = ColorConverter.ConvertFromString(value);
            return converted is Color color ? color : fallback;
        }
        catch
        {
            // A hand-edited settings file can contain anything.
            return fallback;
        }
    }

    /// <summary>
    /// Forces WPF's one-time rendering work - template expansion, glyph-cache population,
    /// text layout - to happen at startup instead of on the user's first hotkey press.
    ///
    /// Creating the HWND is not enough on its own: measured cold, the first Show() took
    /// long enough that results were still blank half a second after typing, while every
    /// subsequent summon was immediate. The window is rendered far off-screen at zero
    /// opacity and without activation, so nothing flashes and focus is never taken.
    /// </summary>
    /// <remarks>
    /// Must be called once the dispatcher loop is running, not from OnStartup. Blocking on
    /// a render from inside startup pushes a nested dispatcher frame before there is a
    /// message loop to drain it, which deadlocks composition partway through - observed as
    /// the hotkey silently never being registered.
    /// </remarks>
    public void PrewarmRender()
    {
        if (_prewarming) return;
        _prewarming = true;

        _savedOpacity = Opacity;
        _savedShowActivated = ShowActivated;

        Opacity = 0;
        ShowActivated = false;
        Left = -32000;
        Top = -32000;

        // A throwaway row makes WPF expand and lay out the item template too, which is the
        // expensive half - an empty list would leave that cost for the first real query.
        _rows.Add(new ResultRow(new SearchResult
        {
            Title = "warmup",
            Subtitle = "warmup",
            Glyph = "",
            InvokeAsync = _ => Task.CompletedTask,
        }));
        ResultsPanel.Visibility = Visibility.Visible;

        Show();
        UpdateLayout();

        // Tear down on a later, lower-priority callback rather than blocking here: by the
        // time a Background item runs, the higher-priority Render pass has already gone
        // through, which is the whole point of the exercise.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FinishPrewarm));
    }

    private void FinishPrewarm()
    {
        Hide();

        _rows.Clear();
        ResultsPanel.Visibility = Visibility.Collapsed;

        Opacity = _savedOpacity;
        ShowActivated = _savedShowActivated;
        _prewarming = false;
    }

    /// <summary>
    /// Hide instead of close, so the window (and its warmed-up render tree) survives for
    /// the next summon. Actually closing it would put a ~200ms rebuild on every hotkey.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Dismiss();
            return;
        }

        base.OnClosing(e);
    }

    public void ShutdownForReal()
    {
        CancelVoice();
        _transcriber.Dispose();
        _reallyClosing = true;
        Close();
    }

    /// <summary>
    /// Brings the overlay up. The foreground window must already have been captured by
    /// the caller <em>before</em> this runs - once we take focus, it is too late.
    /// </summary>
    public void Summon()
    {
        CancelPendingConfirmation();
        CancelVoice();
        _aiCts?.Cancel();
        _aiRunning = false;
        MicButton.Visibility = _settings.VoiceEnabled ? Visibility.Visible : Visibility.Collapsed;

        QueryBox.Text = string.Empty;
        _rows.Clear();
        _armed = null;
        _selectionIsUsers = false;
        _prediction = null;
        Ghost.Visibility = Visibility.Collapsed;
        ClearScope();

        HideNotice();
        ResultsPanel.Visibility = Visibility.Collapsed;
        SetBusy(false);
        UpdateStatus();

        PositionOnActiveScreen();

        Show();
        Activate();

        if (_hud) StartHudAnimations();
        PlayIntro();

        QueryBox.Focus();
        Keyboard.Focus(QueryBox);

        OfferClipboard();
    }

    /// <summary>
    /// If what you last copied is a link or a path, the bar opens with it as the first row:
    /// copy a URL in chat or a path from a terminal, Alt+Space, Enter.
    ///
    /// Deliberately only links and paths that exist. Arbitrary copied text is never shown -
    /// it is too often a password or a private message to put on screen unasked.
    /// </summary>
    private void OfferClipboard()
    {
        string text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            text = System.Windows.Clipboard.GetText().Trim();
        }
        catch
        {
            return;   // another process holds the clipboard open
        }

        if (text.Length is 0 or > 2048 || text.Contains('\n')) return;

        var path = Environment.ExpandEnvironmentVariables(text.Trim('"'));
        SearchResult? row = null;

        if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));

            row = new SearchResult
            {
                Title = "Open " + (name.Length > 0 ? name : path),
                Subtitle = $"From your clipboard  ·  {path}",
                Source = "Clipboard",
                Glyph = "",
                IconPath = System.IO.File.Exists(path) ? path : null,
                CopyText = path,
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    ShellRun.Start(path);
                    return Task.CompletedTask;
                },
            };
        }
        else if (WebProvider.TryParseUrl(text, out var url, out var display))
        {
            row = new SearchResult
            {
                Title = $"Open {display}",
                Subtitle = "Link from your clipboard",
                Source = "Clipboard",
                Glyph = "",
                FaviconUrl = url,
                CopyText = url,
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    WebProvider.OpenUrl(url, ctx.Notify);
                    return Task.CompletedTask;
                },
            };
        }

        if (row is null) return;

        _rows.Clear();
        _rows.Add(new ResultRow(row, _favicons));
        ResultsList.SelectedIndex = 0;
        ResultsPanel.Visibility = Visibility.Visible;
        UpdateStatus();
    }

    private void Dismiss()
    {
        if (!IsVisible) return;

        CancelPendingConfirmation();
        CancelVoice();
        _searchCts?.Cancel();
        _aiCts?.Cancel();
        _aiRunning = false;
        SetBusy(false);

        Hide();
        StopHudAnimations();
        _foreground.Restore();
    }

    /// <summary>
    /// Places the overlay on whichever display holds the mouse - the best available proxy
    /// for "the screen the user is looking at". Work-area coordinates are physical pixels,
    /// so they must be converted to WPF's device-independent units before use.
    /// </summary>
    private void PositionOnActiveScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var work = screen.WorkingArea;

        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var topLeft = transform.Transform(new Point(work.Left, work.Top));
        var size = transform.Transform(new Point(work.Width, work.Height));

        _workArea = new Rect(topLeft.X, topLeft.Y, size.X, size.Y);

        // The list must not assume a tall display. ChromeAllowance covers the query row,
        // status bar, notice panel and the shadow margin.
        const double ChromeAllowance = 210;
        ResultsList.MaxHeight = Math.Max(140, _workArea.Height * 0.72 - ChromeAllowance);

        Left = _workArea.X + (_workArea.Width - Width) / 2;
        Top = _workArea.Y + _workArea.Height * (Math.Clamp(_settings.TopOffsetPercent, 0, 70) / 100.0);
    }

    /// <summary>
    /// The window grows downward as results arrive, so its final height isn't known when
    /// it is positioned. On a short display that pushes the status bar off-screen; nudge it
    /// back up once the real height is known.
    /// </summary>
    private void ClampToWorkArea()
    {
        if (_workArea.Height <= 0 || ActualHeight <= 0) return;

        var lowestTop = _workArea.Bottom - ActualHeight;
        if (Top > lowestTop) Top = Math.Max(_workArea.Y, lowestTop);
    }

    // ----------------------------------------------------------------- search

    private async void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        var text = QueryBox.Text;

        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModeGlyph.Text = text.StartsWith(AiProvider.Prefix, StringComparison.Ordinal)
            ? ""
            : "";

        _armed = null;
        _selectionIsUsers = false;   // a new query re-decides what the best row is
        HideNotice();

        // "yt " - a site prefix followed by a space - drops straight into that site's
        // search, the keyboard equivalent of Tab on its row. Prefixes only, not full
        // names: "google drive" must still be able to reach Google Drive.
        if (_scope is null && text.EndsWith(' '))
        {
            var head = text.Trim();
            if (head.Length > 0 && !head.Contains(' ') &&
                SiteCatalog.FindByPrefix(head) is { } prefixSite &&
                WebProvider.ScopeFor(prefixSite) is { } prefixScope)
            {
                EnterScope(prefixScope);
                return;
            }
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            await Task.Delay(SearchDebounce, cts.Token).ConfigureAwait(true);
            await RunSearchAsync(text, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private async Task RunSearchAsync(string text, CancellationToken ct)
    {
        if (_scope is not null)
        {
            RenderScopeRows(_scope, text);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _rows.Clear();
            ResultsPanel.Visibility = Visibility.Collapsed;
            UpdateGhost();
            UpdateStatus();

            // An empty box is the clipboard's slot - including after Summon() clears the
            // previous query, whose change event lands here a moment after the offer.
            OfferClipboard();
            return;
        }

        // "find the file wifi_scan" describes a search rather than an action, so the
        // providers run against what was described - the results appear directly, with no
        // extra keystroke, and the status line says how the sentence was read.
        var query = SearchQuery.From(text);
        _interpretation = null;

        if (_settings.NaturalLanguage && _language.Parse(text) is { Kind: NlKind.Search } described)
        {
            query = SearchQuery.From(described.SearchTerm);
            _interpretation = described.Summary;
        }

        var byProvider = new Dictionary<string, IReadOnlyList<SearchResult>>();
        var pending = _providers.Select(p => QueryProviderAsync(p, query, ct)).ToList();

        // Render as each provider lands, rather than waiting for all of them. Commands and
        // apps answer in microseconds; file search may have to time out against an absent
        // Everything server. Gating the fast results behind the slow one makes the whole
        // launcher feel broken even though every individual part is working.
        await foreach (var finished in Task.WhenEach(pending).WithCancellation(ct).ConfigureAwait(true))
        {
            if (ct.IsCancellationRequested) return;

            var (name, results) = await finished.ConfigureAwait(true);
            byProvider[name] = results;

            RenderRows(byProvider);
        }
    }

    /// <summary>
    /// Wraps one provider so a single failure or timeout degrades to "no results from this
    /// source" instead of taking the whole query down.
    /// </summary>
    private static async Task<(string Provider, IReadOnlyList<SearchResult> Results)>
        QueryProviderAsync(IResultProvider provider, SearchQuery query, CancellationToken ct)
    {
        try
        {
            return (provider.Name, await provider.QueryAsync(query, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return (provider.Name, []);
        }
        catch
        {
            return (provider.Name, []);
        }
    }

    /// <summary>
    /// A favicon finished downloading. Re-render the current set so the row picks it up,
    /// but only while the bar is actually on screen - otherwise a late icon would resurrect
    /// stale results the next time it opens.
    /// </summary>
    private void OnIconArrived()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(OnIconArrived));
            return;
        }

        if (!IsVisible) return;

        // The chip's logo may be the icon that just arrived.
        if (_scope is not null) UpdateScopeIcon();

        if (_lastRendered is null || _rows.Count == 0) return;
        RenderRows(_lastRendered);
    }

    private void RenderRows(Dictionary<string, IReadOnlyList<SearchResult>> byProvider)
    {
        _lastRendered = byProvider;

        // Results arrive incrementally, so the list is rebuilt repeatedly. Hold on to what
        // the user had highlighted: silently yanking the selection out from under someone
        // mid-keystroke is how a launcher runs the wrong thing.
        //
        // Only what they chose with the arrow keys, though. Keeping an auto-selected row
        // meant the highlight stuck to whichever provider answered first - type "brave" and
        // the bar could still be pointing at a command that answered a keystroke ago, while
        // the top row is the app you wanted.
        var previous = _selectionIsUsers ? (ResultsList.SelectedItem as ResultRow)?.Result : null;

        var ordered = byProvider.Values
            .SelectMany(x => x)
            .OrderByDescending(r => r.Score)
            .Take(_settings.MaxResults)
            .Select(r => new ResultRow(r, _favicons))
            .ToList();

        _rows.Clear();
        foreach (var row in ordered) _rows.Add(row);

        if (_rows.Count > 0)
        {
            var restored = previous is null
                ? -1
                : ordered.FindIndex(r => ReferenceEquals(r.Result, previous));

            ResultsList.SelectedIndex = restored >= 0 ? restored : 0;
        }

        ResultsPanel.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGhost();
        UpdateStatus();
    }

    // ------------------------------------------------------------ site scope

    /// <summary>
    /// Turns the bar into a search box for one site: the site's logo and name move into a
    /// chip at the start of the bar and the text box clears for the query. Backspace on an
    /// empty box, or Esc, leaves again.
    /// </summary>
    private void EnterScope(SearchScope scope, string seed = "")
    {
        _scope = scope;
        _armed = null;

        ScopeName.Text = scope.Name;
        UpdateScopeIcon();

        ScopeChip.Visibility = Visibility.Visible;
        Reactor.Visibility = Visibility.Collapsed;
        UpdateHudState();
        Placeholder.Text = $"Search {scope.Name}…";
        Placeholder.Margin = new Thickness(12, 0, 0, 0);

        // Replacing the text re-runs the query, which now renders the scope rows. When the
        // text is already what we want, TextChanged will not fire, so run it by hand.
        if (QueryBox.Text == seed) OnQueryChanged(QueryBox, null!);
        else QueryBox.Text = seed;

        QueryBox.CaretIndex = QueryBox.Text.Length;
        QueryBox.Focus();
    }

    private void ExitScope()
    {
        ClearScope();
        OnQueryChanged(QueryBox, null!);
    }

    /// <summary>Resets scope visuals without re-querying; used when the bar is re-summoned.</summary>
    private void ClearScope()
    {
        _scope = null;
        ScopeChip.Visibility = Visibility.Collapsed;
        Reactor.Visibility = Visibility.Visible;
        Placeholder.Text = _defaultPlaceholder;
        Placeholder.Margin = new Thickness(12, 0, 0, 0);
        UpdateHudState();
    }

    private void UpdateScopeIcon()
    {
        var icon = _scope?.IconUrl is { } url ? _favicons.Get(url) : null;

        ScopeIcon.Source = icon;
        ScopeIconPlate.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        ScopeGlyph.Text = _scope?.Glyph ?? string.Empty;
        ScopeGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Inside a scope there is exactly one thing to do: search that site.</summary>
    private void RenderScopeRows(SearchScope scope, string text)
    {
        var term = text.Trim();
        if (scope.Row is { } build)
        {
            RenderRows(new Dictionary<string, IReadOnlyList<SearchResult>> { ["scope"] = [build(term)] });
            return;
        }
        var target = term.Length > 0 ? scope.UrlFor(term) : scope.HomeUrl;

        var row = new SearchResult
        {
            Title = term.Length > 0
                ? $"Search {scope.Name} for “{term}”"
                : $"Open {scope.Name}",
            Subtitle = term.Length > 0
                ? "Enter to search  ·  Backspace to leave"
                : "Type what to search for  ·  Esc to leave",
            Source = "Web",
            Glyph = scope.Glyph,
            FaviconUrl = scope.IconUrl,
            Score = 1,
            InvokeAsync = ctx =>
            {
                if (target is null) return Task.CompletedTask;

                ctx.Dismiss();
                WebProvider.OpenUrl(target, ctx.Notify);
                return Task.CompletedTask;
            },
        };

        RenderRows(new Dictionary<string, IReadOnlyList<SearchResult>> { ["scope"] = [row] });
    }

    // ------------------------------------------------------- inline prediction

    /// <summary>
    /// Shows the rest of the highlighted result as dimmed text after the caret, the way a
    /// browser address bar does. Only offered when the result genuinely continues what has
    /// been typed - a prediction that rewrites earlier characters would be a trap, since
    /// accepting it is a single keystroke.
    /// </summary>
    private void UpdateGhost()
    {
        var typed = QueryBox.Text;
        _prediction = null;

        // Never predict mid-string: the completion would appear detached from the caret.
        // Nor inside a site scope, where rows read "Search YouTube for ..." - typing "S"
        // would otherwise predict "earch YouTube for".
        if (_scope is not null || typed.Length == 0 || QueryBox.CaretIndex != typed.Length)
        {
            Ghost.Visibility = Visibility.Collapsed;
            return;
        }

        if (ResultsList.SelectedItem is not ResultRow row)
        {
            Ghost.Visibility = Visibility.Collapsed;
            return;
        }

        var candidate = row.Title;
        if (candidate.Length <= typed.Length ||
            !candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            Ghost.Visibility = Visibility.Collapsed;
            return;
        }

        var remainder = candidate[typed.Length..];

        // The transparent run carries the user's own characters, not the candidate's, so
        // its advance width matches what the TextBox is actually drawing on top.
        GhostTyped.Text = typed;
        GhostRest.Text = remainder;
        Ghost.Visibility = Visibility.Visible;

        _prediction = typed + remainder;
    }

    private bool TryAcceptPrediction()
    {
        if (_prediction is null) return false;

        QueryBox.Text = _prediction;
        QueryBox.CaretIndex = QueryBox.Text.Length;
        return true;
    }

    // --------------------------------------------------------------- keyboard

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A pending confirmation swallows everything else - it is a modal decision.
        if (_pendingConfirm is not null)
        {
            if (e.Key is Key.Enter or Key.Y)
            {
                e.Handled = true;
                ResolveConfirmation(true);
            }
            else if (e.Key is Key.Escape or Key.N)
            {
                e.Handled = true;
                ResolveConfirmation(false);
            }
            return;
        }

        // Ctrl+M toggles the microphone, mirroring the button.
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = ToggleVoiceAsync();
            return;
        }

        // Ctrl+C copies the selected row's path - unless text is selected in the box, where
        // the user plainly meant to copy that instead.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && QueryBox.SelectionLength == 0)
        {
            e.Handled = true;
            CopySelectedPath();
            return;
        }

        // Esc while listening abandons the recording rather than closing the bar.
        if (e.Key == Key.Escape && _voiceState != VoiceState.Idle)
        {
            e.Handled = true;
            CancelVoice();
            ShowNotice("Voice search cancelled.", "");
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_aiRunning)
                {
                    _aiCts?.Cancel();
                    _aiRunning = false;
                    SetBusy(false);
                    ShowNotice("Cancelled.", "");
                }
                else if (_scope is not null)
                {
                    // Step out of the site search first; a second Esc closes the bar.
                    ExitScope();
                }
                else
                {
                    Dismiss();
                }
                return;

            case Key.Back:
                // Backspace on an empty box removes the chip, like deleting a token.
                if (_scope is not null && QueryBox.Text.Length == 0)
                {
                    e.Handled = true;
                    ExitScope();
                }
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(+1);
                return;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.Enter:
                e.Handled = true;
                await InvokeSelectedAsync().ConfigureAwait(true);
                return;

            case Key.Right:
                // Accept the inline prediction only when the caret is already at the end;
                // otherwise Right must keep meaning "move the caret".
                if (_prediction is not null && QueryBox.CaretIndex == QueryBox.Text.Length)
                {
                    e.Handled = true;
                    TryAcceptPrediction();
                }
                return;

            case Key.Tab:
                e.Handled = true;

                // A site row takes priority: Tab means "search inside this site".
                if (_scope is null && ResultsList.SelectedItem is ResultRow { Result: { Scope: { } scope } tabbed })
                {
                    EnterScope(scope, tabbed.ScopeSeed);
                    return;
                }

                // Otherwise complete to the selected row's title - a launcher convention
                // that makes refining a fuzzy match much less fiddly.
                if (!TryAcceptPrediction() && ResultsList.SelectedItem is ResultRow row)
                {
                    QueryBox.Text = row.Title;
                    QueryBox.CaretIndex = QueryBox.Text.Length;
                }
                return;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;

        _armed = null;
        HideNotice();

        var next = ResultsList.SelectedIndex + delta;
        if (next < 0) next = _rows.Count - 1;
        if (next >= _rows.Count) next = 0;

        ResultsList.SelectedIndex = next;
        _selectionIsUsers = true;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        UpdateGhost();
        UpdateStatus();
    }

    /// <summary>
    /// Puts the highlighted row's path on the clipboard: an app's executable, a file's full
    /// path, a bookmark's URL.
    /// </summary>
    private void CopySelectedPath()
    {
        if (ResultsList.SelectedItem is not ResultRow { Result.CopyText: { Length: > 0 } text })
        {
            ShowNotice("Nothing to copy.", "Highlight an app, file or bookmark first.");
            return;
        }

        try
        {
            // The clipboard is shared with every other process, so it can be locked by one.
            // Clipboard.SetText already retries; this catch is for when it gives up.
            System.Windows.Clipboard.SetText(text);
            ShowNotice("Copied.", text);
        }
        catch (Exception ex)
        {
            ShowNotice("Could not copy that.", ex.Message);
        }
    }

    private async void OnRowClicked(object sender, MouseButtonEventArgs e) =>
        await InvokeSelectedAsync().ConfigureAwait(true);

    private async Task InvokeSelectedAsync()
    {
        if (ResultsList.SelectedItem is not ResultRow row) return;

        var result = row.Result;

        // "Search YouTube..." rows are a doorway, not an action.
        if (result.EntersScopeOnInvoke && result.Scope is not null && _scope is null)
        {
            EnterScope(result.Scope);
            return;
        }

        // Destructive rows need a second, deliberate Enter. The first press arms them and
        // says exactly what will happen; only the second actually runs it.
        if (result.IsDestructive && !ReferenceEquals(_armed, result))
        {
            _armed = result;
            ShowNotice(
                $"{result.Title} — press Enter again to confirm",
                "This cannot be undone. Esc or keep typing to cancel.");
            return;
        }

        _armed = null;

        var ctx = new ActionContext
        {
            TargetWindow = _foreground.Captured,
            Dismiss = Dismiss,
            Notify = _notify,
        };

        try
        {
            await result.InvokeAsync(ctx).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _notify($"That failed: {ex.Message}");
        }
    }

    // --------------------------------------------------------------------- AI

    private async Task RunAiAsync(string prompt)
    {
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;

        _aiRunning = true;
        SetBusy(true);
        _rows.Clear();
        ResultsPanel.Visibility = Visibility.Collapsed;
        ShowNotice($"Working on “{prompt}”…", AiModels.LabelFor(_settings.AiModel));

        try
        {
            var result = await _planner.RunAsync(
                prompt,
                _foreground.Captured,
                // No-op dismiss: the window must stay up to show progress and to ask for
                // confirmations. The user closes it themselves once they've read the result.
                dismiss: () => { },
                notify: _notify,
                confirm: ConfirmDestructiveAsync,
                ct).ConfigureAwait(true);

            RenderAiResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Cancelled.", "");
        }
        catch (Exception ex)
        {
            ShowNotice("That didn't work.", ex.Message);
        }
        finally
        {
            _aiRunning = false;
            SetBusy(false);
        }
    }

    private void RenderAiResult(AiRunResult result)
    {
        var done = result.Steps
            .Where(s => !s.Outcome.StartsWith("Declined", StringComparison.Ordinal))
            .Select(s => s.Outcome)
            .ToList();

        if (result.Error is not null)
        {
            ShowNotice(
                result.Error,
                done.Count > 0 ? "Completed first: " + string.Join(" ", done) : "");
            return;
        }

        var headline = !string.IsNullOrWhiteSpace(result.Message)
            ? result.Message!
            : done.Count > 0 ? string.Join(" ", done) : "Nothing to do.";

        ShowNotice(headline, done.Count > 1 ? $"{done.Count} actions — Esc to close" : "Esc to close");
    }

    /// <summary>
    /// Pauses the AI run and hands the decision to the user. Returns when they answer.
    /// </summary>
    private Task<bool> ConfirmDestructiveAsync(BeaconCommand command, string call)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirm = tcs;

        SetBusy(false);
        ShowNotice(
            $"Claude wants to: {call}",
            "This is destructive and cannot be undone. Enter to allow, Esc to refuse.");

        return tcs.Task;
    }

    private void ResolveConfirmation(bool approved)
    {
        var tcs = _pendingConfirm;
        _pendingConfirm = null;
        if (tcs is null) return;

        // These messages describe an AI run; other confirmations (the voice model
        // download) show their own.
        if (_aiRunning)
        {
            if (approved)
            {
                SetBusy(true);
                ShowNotice("Running…", "");
            }
            else
            {
                ShowNotice("Refused.", "Telling Claude to stop.");
            }
        }

        tcs.TrySetResult(approved);
    }

    private void CancelPendingConfirmation()
    {
        var tcs = _pendingConfirm;
        _pendingConfirm = null;
        tcs?.TrySetResult(false);
    }

    // ------------------------------------------------------------------ chrome

    // ------------------------------------------------------------------ voice

    /// <summary>
    /// The Voice Search command and its global chord (Alt+M by default).
    ///
    /// It toggles rather than only starting: pressing the same chord again is the obvious way
    /// to say "I've finished talking", and it ends the recording and transcribes immediately
    /// instead of waiting out the silence timeout.
    /// </summary>
    public void StartVoice() => _ = ToggleVoiceAsync();

    /// <summary>True while the microphone is open, so a second chord press can end it.</summary>
    public bool IsListening => _voiceState == VoiceState.Listening;

    /// <summary>
    /// Mic button / Ctrl+M. First press listens; a second press while listening stops early
    /// and transcribes what was said so far.
    /// </summary>
    private async Task ToggleVoiceAsync()
    {
        switch (_voiceState)
        {
            case VoiceState.Listening:
                _voiceStop?.Cancel();
                return;
            case VoiceState.Transcribing or VoiceState.Downloading:
                return;
        }

        if (!_settings.VoiceEnabled || _pendingConfirm is not null) return;

        var model = WhisperModels.Find(_settings.VoiceModel);
        if (!WhisperModels.IsDownloaded(model) && !await OfferModelDownloadAsync(model).ConfigureAwait(true))
            return;

        await ListenAndTranscribeAsync(model).ConfigureAwait(true);
    }

    /// <summary>
    /// The model is not shipped with Beacon, so the first use asks before fetching it.
    /// It is downloaded once, verified, and kept; audio itself never leaves the PC.
    /// </summary>
    private async Task<bool> OfferModelDownloadAsync(WhisperModel model)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirm = tcs;

        ShowNotice(
            $"Voice search needs a speech model — {model.Label}, {model.SizeLabel}",
            "Downloaded once from the official whisper.cpp repository on Hugging Face and kept on " +
            "this PC. Transcription runs locally; your voice is never uploaded. " +
            "Enter to download, Esc to cancel.");

        if (!await tcs.Task.ConfigureAwait(true))
        {
            HideNotice();
            return false;
        }

        _voiceCts = new CancellationTokenSource();
        SetVoiceState(VoiceState.Downloading);

        var progress = new Progress<double>(p =>
            ShowNotice($"Downloading speech model… {p:P0}", $"{model.Label}, {model.SizeLabel}. Esc to cancel."));

        try
        {
            await WhisperModels.DownloadAsync(model, progress, _voiceCts.Token).ConfigureAwait(true);
            HideNotice();
            return true;
        }
        catch (OperationCanceledException)
        {
            ShowNotice("Download cancelled.", "");
            return false;
        }
        catch (Exception ex)
        {
            ShowNotice("Couldn't download the speech model.", ex.Message);
            return false;
        }
        finally
        {
            SetVoiceState(VoiceState.Idle);
        }
    }

    private async Task ListenAndTranscribeAsync(WhisperModel model)
    {
        _voiceCts = new CancellationTokenSource();
        _voiceStop = CancellationTokenSource.CreateLinkedTokenSource(_voiceCts.Token);
        var cancel = _voiceCts.Token;

        // The transcript replaces what was typed - but a site scope survives, so speaking
        // inside [YouTube] searches YouTube.
        QueryBox.Text = string.Empty;
        HideNotice();
        SetVoiceState(VoiceState.Listening);

        // Load the model while the user is still talking; see Transcriber.PreloadAsync.
        _ = _transcriber.PreloadAsync(model, cancel).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        Recording recording;
        try
        {
            recording = await _recorder
                .RecordAsync(_settings.VoiceDevice, _settings.VoiceAutoStop, _voiceStop.Token)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetVoiceState(VoiceState.Idle);
            ShowNotice("Couldn't use the microphone.",
                $"{ex.Message} Check that a microphone is connected, and that Settings › Privacy › " +
                "Microphone allows desktop apps.");
            return;
        }

        if (cancel.IsCancellationRequested)
        {
            SetVoiceState(VoiceState.Idle);
            return;
        }

        // A quarter-second or less is a click or a breath, not a query.
        if (recording.End == RecordingEnd.NoSpeech || recording.Samples.Length < MicRecorder.SampleRate / 4)
        {
            SetVoiceState(VoiceState.Idle);
            ShowNotice("Didn't hear anything.",
                "Try again closer to the mic. If it keeps happening, pick the right microphone in " +
                "Beacon Settings › Voice, and check Settings › Privacy › Microphone.");
            return;
        }

        SetVoiceState(VoiceState.Transcribing);

        string text;
        try
        {
            text = await _transcriber
                .TranscribeAsync(model, recording.Samples, _vocabulary?.Invoke(), cancel)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetVoiceState(VoiceState.Idle);
            return;
        }
        catch (Exception ex)
        {
            SetVoiceState(VoiceState.Idle);
            ShowNotice("Couldn't transcribe that.", ex.Message);
            return;
        }

        SetVoiceState(VoiceState.Idle);

        if (text.Length == 0)
        {
            ShowNotice("Didn't catch that.", "Try again, speaking a little more slowly.");
            return;
        }

        QueryBox.Text = text;
        QueryBox.CaretIndex = text.Length;
        QueryBox.Focus();

        await RunDictatedAsync(text).ConfigureAwait(true);
    }

    /// <summary>
    /// Acts on what was just said, when there is nothing to be unsure about.
    ///
    /// Three conditions, all required: the sentence parsed into actions locally, the parse was
    /// unambiguous (a fuzzy command-name match is not), and nothing in it is destructive.
    /// Anything else stays in the box with its row selected, one Enter away - misheard speech
    /// must never be the reason a PC shut down.
    /// </summary>
    private async Task RunDictatedAsync(string text)
    {
        if (!_settings.VoiceAutoRun || !_settings.NaturalLanguage) return;

        var understanding = _language.Parse(text);
        if (understanding is null ||
            understanding.Kind != NlKind.Actions ||
            understanding.IsDestructive ||
            understanding.Confidence < 0.99)
        {
            return;
        }

        ShowNotice(Capitalize(understanding.Summary), "Heard and running…");

        var ctx = new ActionContext
        {
            TargetWindow = _foreground.Captured,
            Dismiss = Dismiss,
            Notify = _notify,
        };

        try
        {
            var messages = await _languageRunner.RunAsync(understanding, ctx).ConfigureAwait(true);
            var summary = string.Join("  ", messages.Where(m => m.Length > 0));
            if (summary.Length > 0) _notify(summary);
        }
        catch (Exception ex)
        {
            _notify($"That failed: {ex.Message}");
        }
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void CancelVoice()
    {
        _voiceCts?.Cancel();
        _voiceStop?.Cancel();
        if (_voiceState != VoiceState.Idle) SetVoiceState(VoiceState.Idle);
    }

    private void SetVoiceState(VoiceState state)
    {
        _voiceState = state;

        switch (state)
        {
            case VoiceState.Listening:
                // Resource references, not fixed brushes, so a style change mid-session
                // recolours them - red in Classic, the HUD colour in HUD.
                MicButton.SetResourceReference(Border.BackgroundProperty, "ListenBack");
                MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "ListenGlyph");
                MicLevel.Opacity = 0.35;
                Placeholder.Text = "Listening… speak now";
                Placeholder.Visibility = Visibility.Visible;
                SetBusy(false);
                break;

            case VoiceState.Transcribing:
                MicLevel.Opacity = 0;
                MicLevelScale.ScaleX = MicLevelScale.ScaleY = 1;
                MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                Placeholder.Text = "Transcribing…";
                SetBusy(true);
                break;

            case VoiceState.Downloading:
                SetBusy(true);
                break;

            default:
                MicButton.ClearValue(Border.BackgroundProperty);
                MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                MicLevel.Opacity = 0;
                MicLevelScale.ScaleX = MicLevelScale.ScaleY = 1;
                Placeholder.Text = _scope is null ? _defaultPlaceholder : $"Search {_scope.Name}…";
                if (!_aiRunning) SetBusy(false);
                break;
        }

        UpdateStatus();
    }

    /// <summary>Arrives on the audio thread roughly every 50 ms.</summary>
    private void OnMicLevel(double level)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (_voiceState != VoiceState.Listening) return;

            var scale = 1 + level * 0.6;
            MicLevelScale.ScaleX = scale;
            MicLevelScale.ScaleY = scale;
            MicLevel.Opacity = 0.35 + level * 0.65;
        });
    }

    // ------------------------------------------------------------------ style

    private static readonly Color DefaultAccent = Color.FromRgb(0x5B, 0x8D, 0xEF);
    private static readonly Color HudCyan = Color.FromRgb(0x35, 0xE6, 0xFF);

    /// <summary>Corner cut of the HUD frame, matching the bracket paths in the XAML.</summary>
    private const double HudCut = 16;

    private bool _hud;
    private bool _busy;
    private readonly DispatcherTimer _hudClock;

    private enum OverlayLook { Classic, Hud, Tactical }

    private OverlayLook _look = OverlayLook.Tactical;

    /// <summary>
    /// Switches between the looks by replacing the window's resource palette - every
    /// colour and font in the XAML is a DynamicResource, so the window restyles in place -
    /// and toggling each look's own layers.
    ///
    /// HUD and Tactical share the machinery (cut-corner frame, brackets, reactor ring, live
    /// readouts); <see cref="_hud"/> means "one of those two".
    /// </summary>
    private void ApplyStyle(Settings settings)
    {
        _look = (settings.OverlayStyle ?? string.Empty).ToLowerInvariant() switch
        {
            "classic" => OverlayLook.Classic,
            "hud" => OverlayLook.Hud,
            _ => OverlayLook.Tactical,
        };

        _hud = _look != OverlayLook.Classic;
        ResultRow.HudStyle = _hud;

        var accent = ParseColor(settings.AccentColor, DefaultAccent);

        // Resources only the Tactical look uses; neutral unless it overrides them below.
        foreach (var key in new[]
                 {
                     "FrameInner", "RowStripe", "RowStripeActive", "TileFill", "TileFillActive",
                     "TileHover", "TileLip", "TileLipActive", "TileStroke", "QueryFill", "PanelFill",
                 })
        {
            SetBrush(key, Colors.Transparent);
        }

        Resources["RowMargin"] = new Thickness(6, 1, 6, 1);
        Resources["RowPadding"] = new Thickness(10, 8, 10, 8);
        Resources["RowTitleSize"] = 14d;
        Resources["RowSubSize"] = 11.5d;
        Resources["QuerySize"] = 21d;

        switch (_look)
        {
            case OverlayLook.Tactical:
                ApplyTactical(accent == DefaultAccent ? TacticalAmber : accent);
                break;
            case OverlayLook.Hud:
                ApplyHud(accent == DefaultAccent ? HudCyan : accent);
                break;
            default:
                ApplyClassic(accent);
                break;
        }

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        var isHud = _look == OverlayLook.Hud;
        var isTactical = _look == OverlayLook.Tactical;

        // Row hover: a flat wash in the framed looks; Tactical lightens the tile instead.
        Resources["RowHover"] = isTactical ? ResultRow.Frozen(Colors.Transparent) : Resources["HoverFill"];

        ClassicFrame.Visibility = Show(_look == OverlayLook.Classic);
        HudFrame.Visibility = Show(isHud);
        HudCorners.Visibility = Show(isHud);
        ReactorOuter.Visibility = Show(_hud);
        ReactorInner.Visibility = Show(_hud);

        HudScanlines.Visibility = Show(isHud);
        HudSweep.Visibility = Show(isHud);
        HudHeader.Visibility = Show(isHud);
        HudClock.Visibility = Show(isHud);

        // Tactical has no enclosing panel at all: header tags, command bar, result tiles
        // and status strip each float on their own over the desktop.
        HudFrameInner.Visibility = Visibility.Collapsed;
        TacHeader.Visibility = Show(isTactical);
        QueryPlate.Visibility = Show(isTactical);
        QueryRow.Height = isTactical ? 54 : 62;
        ResultsPanel.Margin = isTactical ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        StatusBar.Margin = isTactical ? new Thickness(0, 4, 0, 0) : new Thickness(0);
        StatusBar.Padding = isTactical ? new Thickness(18, 5, 18, 5) : new Thickness(20, 8, 20, 9);
        ResultsList.Padding = isTactical ? new Thickness(0) : new Thickness(4, 6, 4, 6);

        _defaultPlaceholder = _look switch
        {
            OverlayLook.Tactical => "Enter command — apps, files, sites, or ? for Claude",
            OverlayLook.Hud => "Awaiting command — apps, files, sites, or ? for Claude",
            _ => "Search apps, files and commands—or ? to ask Claude",
        };

        if (_scope is null && _voiceState == VoiceState.Idle) Placeholder.Text = _defaultPlaceholder;

        UpdateHudGeometry();
        UpdateReadouts();

        if (IsVisible && !_prewarming)
        {
            StopHudAnimations();
            if (_hud) StartHudAnimations();
        }
    }

    private static readonly Color TacticalAmber = Color.FromRgb(0xF0, 0xA0, 0x30);

    /// <summary>
    /// Gunmetal and amber: machined grey plates with a double bevelled frame, amber
    /// stripes and readouts, and condensed Agency FB labels.
    /// </summary>
    private void ApplyTactical(Color amber)
    {
        var surface = new LinearGradientBrush(
            Color.FromRgb(0x2C, 0x2E, 0x31), Color.FromRgb(0x1A, 0x1B, 0x1E), 90);
        surface.Freeze();
        Resources["Surface"] = surface;

        SetBrush("SurfaceBorder", Colors.Transparent);
        SetBrush("HudLine", Color.FromRgb(0x68, 0x6C, 0x72));
        SetBrush("FrameBright", Color.FromRgb(0x8E, 0x92, 0x98));
        SetBrush("Divider", Colors.Transparent);   // the pieces float apart; no rules between
        SetBrush("TextPrimary", Color.FromRgb(0xE6, 0xE6, 0xE6));
        SetBrush("TextSecondary", Color.FromRgb(0xA4, 0xA7, 0xAC));
        SetBrush("TextTertiary", Color.FromRgb(0x80, 0x83, 0x88));
        SetBrush("Accent", amber);
        SetBrush("GlyphTint", amber);
        SetBrush("HudBright", amber);
        SetBrush("HudDim", Mix(amber, Color.FromRgb(0x2A, 0x2C, 0x2F), 0.35));
        SetBrush("SelectionBar", amber);
        SetBrush("HoverFill", Color.FromRgb(0x3E, 0x41, 0x45));
        SetBrush("ChipFill", WithAlpha(amber, 0x2A));
        SetBrush("ChipBorder", amber);
        SetBrush("ListenFill", WithAlpha(amber, 0x55));
        SetBrush("ListenBack", WithAlpha(amber, 0x26));
        SetBrush("ListenGlyph", amber);

        // Translucent gunmetal: every piece lets the desktop show through, but stays dark
        // enough behind text that a busy wallpaper never competes with it.
        Resources["TileFill"] = Gradient(0xDE, (0x3C, 0x3F, 0x44), (0x24, 0x26, 0x2A));
        Resources["TileHover"] = Gradient(0xE6, (0x4A, 0x4D, 0x52), (0x30, 0x32, 0x36));
        Resources["TileFillActive"] = Gradient(0xF0, (0x5A, 0x5E, 0x64), (0x3C, 0x3F, 0x44));
        Resources["QueryFill"] = Gradient(0xE8, (0x28, 0x2A, 0x2E), (0x14, 0x15, 0x18));
        Resources["PanelFill"] = Gradient(0xD8, (0x28, 0x2A, 0x2E), (0x18, 0x19, 0x1C));
        SetBrush("TileLip", Color.FromArgb(0xC0, 0x7A, 0x7E, 0x85));
        SetBrush("TileLipActive", Color.FromArgb(0xF0, 0xA8, 0xAC, 0xB2));
        SetBrush("TileStroke", Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        SetBrush("SelectionFill", Colors.Transparent);

        SetBrush("RowStripe", Mix(amber, Color.FromRgb(0x2A, 0x2C, 0x2F), 0.4));
        SetBrush("RowStripeActive", amber);

        Resources["RowCorner"] = new CornerRadius(0);
        Resources["RowMargin"] = new Thickness(0, 2, 0, 2);
        Resources["RowPadding"] = new Thickness(14, 6, 26, 10);
        Resources["RowTitleSize"] = 16d;
        Resources["RowSubSize"] = 11d;
        Resources["QuerySize"] = 18d;
        Resources["UiFont"] = new FontFamily("Bahnschrift, Segoe UI");
        Resources["TitleFont"] = new FontFamily("Agency FB, Bahnschrift, Segoe UI");
        Resources["TagFont"] = new FontFamily("Agency FB, Bahnschrift, Segoe UI");

        // A plain drop shadow rather than a glow: this look is machined metal, not light.
        HudGlow.Color = Colors.Black;
        HudGlow.Opacity = 0.7;
        HudGlow.BlurRadius = 26;
        if (HudCorners.Effect is System.Windows.Media.Effects.DropShadowEffect bracketGlow) bracketGlow.Opacity = 0;

        ResultRow.GlyphBrush = ResultRow.Frozen(amber);
    }

    private void ApplyHud(Color primary)
    {
        var soft = Mix(primary, Colors.White, 0.55);

        // Nearly opaque: translucency reads as "holographic", but text behind the bar
        // competing with what you type is worse.
        SetBrush("Surface", Color.FromArgb(0xF6, 0x04, 0x0B, 0x15));
        SetBrush("SurfaceBorder", Colors.Transparent);
        SetBrush("Divider", WithAlpha(primary, 0x30));
        SetBrush("TextPrimary", Color.FromRgb(0xE8, 0xFB, 0xFF));
        SetBrush("TextSecondary", WithAlpha(soft, 0xB8));
        SetBrush("TextTertiary", WithAlpha(primary, 0x9C));
        SetBrush("Accent", primary);
        SetBrush("GlyphTint", primary);
        SetBrush("SelectionBar", primary);
        SetBrush("HoverFill", WithAlpha(primary, 0x14));
        SetBrush("ChipFill", WithAlpha(primary, 0x22));
        SetBrush("ChipBorder", WithAlpha(primary, 0x99));
        SetBrush("ListenFill", WithAlpha(primary, 0x55));
        SetBrush("ListenBack", WithAlpha(primary, 0x26));
        SetBrush("ListenGlyph", primary);
        SetBrush("HudLine", WithAlpha(primary, 0x75));
        SetBrush("HudBright", primary);
        SetBrush("FrameBright", primary);
        SetBrush("HudDim", WithAlpha(primary, 0xA0));

        // Selected row: a light that fades out across the row, plus the edge marker.
        var selection = new LinearGradientBrush(WithAlpha(primary, 0x3A), WithAlpha(primary, 0x05), 0);
        selection.Freeze();
        Resources["SelectionFill"] = selection;

        Resources["RowCorner"] = new CornerRadius(2);
        Resources["UiFont"] = new FontFamily("Bahnschrift, Segoe UI Variable Text, Segoe UI");
        Resources["TitleFont"] = new FontFamily("Bahnschrift, Segoe UI Variable Text, Segoe UI");
        Resources["TagFont"] = new FontFamily("Cascadia Mono, Consolas");

        HudGlow.Color = primary;
        HudGlow.Opacity = 0.5;
        HudGlow.BlurRadius = 30;
        if (HudCorners.Effect is System.Windows.Media.Effects.DropShadowEffect bracketGlow)
        {
            bracketGlow.Color = primary;
            bracketGlow.Opacity = 0.9;
        }

        ResultRow.GlyphBrush = ResultRow.Frozen(WithAlpha(primary, 0xE6));
    }

    private void ApplyClassic(Color accent)
    {
        SetBrush("Surface", Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1F));
        SetBrush("SurfaceBorder", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        SetBrush("Divider", Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        SetBrush("TextPrimary", Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        SetBrush("TextSecondary", Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        SetBrush("TextTertiary", Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        SetBrush("Accent", accent);
        SetBrush("GlyphTint", Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        SetBrush("SelectionFill", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        SetBrush("SelectionBar", Colors.Transparent);
        SetBrush("HoverFill", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        SetBrush("ChipFill", WithAlpha(accent, 0x26));
        SetBrush("ChipBorder", WithAlpha(accent, 0x66));
        SetBrush("ListenFill", Color.FromArgb(0x40, 0xFF, 0x5B, 0x5B));
        SetBrush("ListenBack", Color.FromArgb(0x30, 0xFF, 0x5B, 0x5B));
        SetBrush("ListenGlyph", Color.FromRgb(0xFF, 0x7A, 0x7A));

        Resources["RowCorner"] = new CornerRadius(8);
        Resources["UiFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Resources["TitleFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Resources["TagFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");

        ResultRow.GlyphBrush = ResultRow.Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>Clock, date, greeting and the amber progress bar; ticked once a second while visible.</summary>
    private void UpdateReadouts()
    {
        var now = DateTime.Now;
        HudClock.Text = now.ToString("HH:mm:ss");

        if (_look != OverlayLook.Tactical) return;

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        TacTime.Text = now.ToString("hh:mm tt", invariant);
        TacDay.Text = now.ToString("dd", invariant);
        TacMonth.Text = now.ToString("MMM", invariant).ToUpperInvariant();
        TacWeekday.Text = now.ToString("ddd", invariant).ToUpperInvariant();
        TacGreeting.Text = now.Hour switch
        {
            >= 5 and < 12 => "GOOD MORNING",
            >= 12 and < 17 => "GOOD AFTERNOON",
            >= 17 and < 21 => "GOOD EVENING",
            _ => "GOOD NIGHT",
        };

        // Focus session from Dotline when one is running; otherwise how much of today is gone.
        double progress;
        if (Core.FocusLink.Current() is { } focus)
        {
            var left = Core.FocusLink.SecondsLeft(focus);
            progress = 1 - left / focus.Seconds;
            var clock = TimeSpan.FromSeconds(Math.Ceiling(left)).ToString(left >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
            TacProgressLabel.Text = focus.PausedLeftSeconds is null ? $"FOCUS  ·  {clock} LEFT" : $"FOCUS PAUSED  ·  {clock} LEFT";
        }
        else
        {
            progress = now.TimeOfDay.TotalHours / 24;
            TacProgressLabel.Text = $"DAY {progress:P0} GONE";
        }
        TacProgressScale.ScaleX = Math.Clamp(progress, 0, 1);
    }

    private void SetBrush(string key, Color color) => Resources[key] = ResultRow.Frozen(color);

    /// <summary>Top-to-bottom gradient with one shared alpha.</summary>
    private static Brush Gradient(byte alpha, (byte R, byte G, byte B) top, (byte R, byte G, byte B) bottom)
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(alpha, top.R, top.G, top.B),
            Color.FromArgb(alpha, bottom.R, bottom.G, bottom.B),
            90);
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    /// <summary>
    /// Rebuilds the cut-corner outline at the window's actual size. A fixed Path stretched
    /// to fit would skew the corners into slivers; building it per size keeps a clean
    /// 45-degree cut that lines up with the corner brackets.
    /// </summary>
    private void UpdateHudGeometry()
    {
        if (!_hud) return;

        var width = Chrome.ActualWidth;
        var height = Chrome.ActualHeight;
        if (width <= 0 || height <= 0) return;

        HudFrame.Data = Chamfered(width, height, HudCut, 0.5);
        HudFrameInner.Data = Chamfered(width, height, HudCut - 2, 5.5);
        HudScanlines.Clip = Chamfered(width, height, HudCut, 1);
        HudSweep.Clip = null;
    }

    private static Geometry Chamfered(double width, double height, double cut, double inset)
    {
        double left = inset, top = inset, right = width - inset, bottom = height - inset;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left + cut, top), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(right - cut, top), true, false);
            ctx.LineTo(new Point(right, top + cut), true, false);
            ctx.LineTo(new Point(right, bottom - cut), true, false);
            ctx.LineTo(new Point(right - cut, bottom), true, false);
            ctx.LineTo(new Point(left + cut, bottom), true, false);
            ctx.LineTo(new Point(left, bottom - cut), true, false);
            ctx.LineTo(new Point(left, top + cut), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Reactor ring rotation, status-dot pulse and the clock. Only while visible: WPF keeps
    /// ticking animations on a hidden window, which would cost CPU for nothing.
    /// </summary>
    private void StartHudAnimations()
    {
        // Continue from the current angle so a change of speed (idle vs busy) never jumps.
        Spin(ReactorSpin, clockwise: true, seconds: _busy ? 1.1 : 9);
        Spin(ReactorCounterSpin, clockwise: false, seconds: _busy ? 0.8 : 6);
        Spin(TacEmblemSpin, clockwise: true, seconds: _busy ? 2 : 16);

        HudDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromSeconds(0.9))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        UpdateReadouts();
        _hudClock.Start();
    }

    private static void Spin(RotateTransform rotation, bool clockwise, double seconds)
    {
        var from = rotation.Angle % 360;
        rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(from, clockwise ? from + 360 : from - 360, TimeSpan.FromSeconds(seconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    private void StopHudAnimations()
    {
        ReactorSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        ReactorCounterSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        TacEmblemSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        HudDot.BeginAnimation(OpacityProperty, null);
        _hudClock.Stop();
    }

    /// <summary>
    /// Opening animation: a quick scale-and-fade in both styles; in HUD, a scan line also
    /// sweeps down the panel as it "powers up".
    /// </summary>
    private void PlayIntro()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(_hud ? 230 : 140);

        var grow = new DoubleAnimation(_hud ? 0.955 : 0.975, 1, duration) { EasingFunction = ease };
        ChromeScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ChromeScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));

        if (_look != OverlayLook.Hud) return;

        var travel = Math.Max(Chrome.ActualHeight, 120);
        HudSweepMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-80, travel, TimeSpan.FromMilliseconds(620))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640))));
        HudSweep.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The header readout on the right: what the system is doing right now.</summary>
    private void UpdateHudState()
    {
        if (!_hud) return;

        HudState.Text = _voiceState switch
        {
            VoiceState.Listening => "AUDIO INPUT ACTIVE",
            VoiceState.Transcribing => "PROCESSING AUDIO",
            VoiceState.Downloading => "DOWNLOADING MODEL",
            _ when _aiRunning => "CLAUDE UPLINK ACTIVE",
            _ when _scope is not null => $"TARGET LOCK // {_scope.Name.ToUpperInvariant()}",
            _ when _busy => "PROCESSING",
            _ => "SYSTEMS ONLINE",
        };
    }

    private void ShowNotice(string title, string body)
    {
        NoticeTitle.Text = title;
        NoticeBody.Text = body;
        NoticeBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        NoticePanel.Visibility = Visibility.Visible;
    }

    private void HideNotice() => NoticePanel.Visibility = Visibility.Collapsed;

    private void SetBusy(bool busy)
    {
        _busy = busy;

        // In HUD the reactor ring is the activity indicator: it spins up instead of a
        // separate spinner appearing.
        if (_hud)
        {
            BusyGlyph.Visibility = Visibility.Collapsed;
            BusyRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            if (IsVisible && !_prewarming) StartHudAnimations();
            UpdateHudState();
            return;
        }

        BusyGlyph.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (busy)
        {
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            BusyRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            BusyRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void UpdateStatus()
    {
        UpdateStatusText();
        if (!_hud) return;

        // HUD readout style: uppercase, with a target indicator instead of an arrow.
        if (StatusLeft.Text.Length > 0) StatusLeft.Text = "▸ " + StatusLeft.Text.ToUpperInvariant();

        var target = StatusRight.Text.TrimStart('←', ' ');
        StatusRight.Text = target.Length > 0 ? "TGT » " + target.ToUpperInvariant() : string.Empty;

        UpdateHudState();
    }

    private void UpdateStatusText()
    {
        StatusRight.Text = _foreground.HasLiveTarget
            ? "← " + _foreground.DescribeTarget()
            : string.Empty;

        // The degraded-search hint only earns its place once the user is actually
        // searching for something, not on an empty bar.
        var fileProvider = _providers.OfType<IFileSearch>().FirstOrDefault();
        var hint = fileProvider?.DegradedHint;

        if (_voiceState == VoiceState.Listening)
        {
            StatusLeft.Text = _settings.VoiceAutoStop
                ? "Listening · stops when you pause · Esc to cancel"
                : "Listening · click the mic or Ctrl+M to finish · Esc to cancel";
            return;
        }

        if (_voiceState == VoiceState.Transcribing)
        {
            StatusLeft.Text = "Transcribing on this PC…";
            return;
        }

        if (_scope is not null)
        {
            StatusLeft.Text = $"Searching {_scope.Name}  ·  Enter to search  ·  Backspace or Esc to leave";
            return;
        }

        // Tab-to-search is invisible unless something says so.
        if (ResultsList.SelectedItem is ResultRow { Result.Scope: { } selectedScope })
        {
            StatusLeft.Text = $"Tab to search {selectedScope.Name}  ·  Enter to open";
            return;
        }

        StatusLeft.Text = _rows.Count switch
        {
            > 0 when _interpretation is not null => _interpretation,
            0 when QueryBox.Text.Length > 0 => "No results",
            0 => "Type to search  ·  ? to ask Claude  ·  Esc to close",
            _ when QueryBox.Text.Length == 0 => "From your clipboard  ·  Enter to open  ·  or just start typing",
            _ when hint is not null => hint,
            _ when ResultsList.SelectedItem is ResultRow { Result.CopyText: { Length: > 0 } } =>
                "↑↓ to move  ·  Enter to run  ·  Ctrl+C to copy path",
            _ => "↑↓ to move  ·  Enter to run  ·  Tab to complete",
        };
    }
}
