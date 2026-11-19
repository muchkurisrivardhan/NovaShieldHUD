using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Beacon.Assistant;
using Beacon.Core;
using Beacon.LocalAi;
using Beacon.Nl;
using Beacon.Voice;

namespace Beacon.UI;

/// <summary>
/// Nova: the talking assistant. A panel in the corner, in the current Beacon style, with an
/// orb that shows what it's doing (listening, thinking, speaking), the conversation, a text
/// box and a mic. Opened with its shortcut (Ctrl+Alt+N), it starts listening at once when a
/// speech model is downloaded. Replies are spoken with a Windows voice; when Nova ends on a
/// question, it listens for the answer by itself.
///
/// Everything runs on this PC: Whisper hears, the local model thinks, Windows speaks.
/// </summary>
public sealed class AssistantWindow : Window
{
    enum Mode { Idle, Listening, Thinking, Speaking }

    readonly Settings _settings;
    readonly NlParser _parser;
    readonly AssistantTools _tools;
    readonly AssistantBrain _brain;
    readonly Func<string> _vocabulary;
    readonly MicRecorder _mic = new();
    readonly Transcriber _transcriber = new();
    readonly SpeechSynthesizer _voice = new();
    readonly IntPtr[] _target = [IntPtr.Zero];

    readonly StackPanel _transcript = new() { Margin = new Thickness(0, 4, 0, 4) };
    readonly ScrollViewer _scroll;
    readonly TextBox _input = new();
    readonly TextBlock _state = new();
    readonly Ellipse _orb = new(), _halo = new();
    readonly Path _thinkArc = new();
    readonly ScaleTransform _orbScale = new(1, 1);

    readonly Color _accent, _page, _surface, _border, _text, _secondary;
    readonly FontFamily _font;
    readonly bool _glow;

    CancellationTokenSource? _work;
    Mode _mode = Mode.Idle;
    bool _lastWasQuestion;
    bool _busy;          // a request is being worked on (between lines Nova says)
    bool _discardListen; // Esc while listening: drop the recording instead of sending it

    public AssistantWindow(Settings settings, NlParser parser, NlRunner runner, Func<string> vocabulary)
    {
        _settings = settings;
        _parser = parser;
        _vocabulary = vocabulary;

        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");
        _font = new FontFamily(p.Font + ", Segoe UI");
        _glow = settings.OverlayStyle is "hud" or "cyberpunk" or "crt";

        var inbox = new EmailInbox(settings.EmailAccount);
        _tools = new AssistantTools(parser, runner, inbox, Dispatcher, () => _target[0], ApproveAsync, s => AddNote(s));
        _brain = new AssistantBrain(LocalLlm.Shared, _tools);
        _brain.Said += line => Dispatcher.BeginInvoke(() => SayLine(line));
        _brain.Working += tool => Dispatcher.BeginInvoke(() => AddNote(ToolNote(tool)));

        _voice.SetOutputToDefaultAudioDevice();
        if (settings.AssistantVoice is { Length: > 0 } name)
            try { _voice.SelectVoice(name); } catch (ArgumentException) { }
        _voice.SpeakCompleted += (_, _) => Dispatcher.BeginInvoke(OnSpeechDone);
        _mic.Level += level => Dispatcher.BeginInvoke(() =>
        {
            if (_mode == Mode.Listening) _orbScale.ScaleX = _orbScale.ScaleY = 1 + Math.Min(level * 3, 0.35);
        });

        Title = "Nova";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 420;
        Height = 560;
        FontFamily = _font;
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _transcript };
        Content = BuildLayout();

        Deactivated += (_, _) => { if (_mode is Mode.Idle) Hide(); };
        PreviewKeyDown += OnKey;
        Closing += (_, e) => { e.Cancel = true; Dismiss(); };
    }

    /// <summary>The shortcut: show (and listen, when a speech model is there); pressed again while listening, stop and send.</summary>
    public void Toggle(IntPtr target)
    {
        if (IsVisible && _mode == Mode.Listening) { _work?.Cancel(); return; }
        if (IsVisible && IsActive) { Dismiss(); return; }

        _target[0] = target;
        PlaceInCorner();
        Show();
        Activate();
        if (_transcript.Children.Count == 0) Greet();
        if (WhisperModels.IsDownloaded(WhisperModels.Find(_settings.VoiceModel))) _ = ListenAsync();
        else _input.Focus();
    }

    // ------------------------------------------------------------------ conversation

    async Task HandleAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        AddBubble(text, mine: true);
        StopSpeaking();
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        SetMode(Mode.Thinking);
        _busy = true;
        try
        {
            if (text is "new chat" or "start over" or "reset")
            {
                _brain.Reset();
                SayLine("Okay, fresh start.");
                return;
            }

            // A plain command Beacon fully understands runs at once, without the model.
            var plan = _parser.Parse(text);
            if (plan is { Kind: NlKind.Actions, Confidence: >= 1, IsDestructive: false } && plan.Steps.Count > 0)
            {
                await _tools.RunInstructionAsync(text);
                var reply = Capitalise(plan.Summary) + ".";
                _brain.Remember(text, reply);
                SayLine(reply);
                return;
            }

            if (LocalModels.ForAssistant(_settings.LocalModel) is not { } model)
            {
                SayLine("I can run commands like \"open Notion\" now. To talk with me properly, download the AI model in Beacon Settings, on the Prompt Ring page. Qwen 3 4B works best.");
                return;
            }

            await Task.Run(() => _brain.RespondAsync(LocalModels.PathFor(model), text, work.Token), work.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SayLine("Something went wrong on my side: " + e.Message);
        }
        finally
        {
            _busy = false;
            if (_mode == Mode.Thinking) SetMode(Mode.Idle);
            if (_mode == Mode.Idle && _voice.State != SynthesizerState.Speaking) OnSpeechDone();
        }
    }

    async Task ListenAsync()
    {
        var model = WhisperModels.Find(_settings.VoiceModel);
        if (!WhisperModels.IsDownloaded(model))
        {
            AddNote("Download a speech model in Settings > Voice to talk to me. You can type meanwhile.");
            _input.Focus();
            return;
        }
        StopSpeaking();
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        _discardListen = false;
        SetMode(Mode.Listening);
        try
        {
            var recording = await _mic.RecordAsync(_settings.VoiceDevice, autoStop: true, work.Token);
            if (_discardListen || recording.End == RecordingEnd.NoSpeech || recording.Samples.Length == 0) { SetMode(Mode.Idle); return; }
            SetMode(Mode.Thinking);
            var heard = await _transcriber.TranscribeAsync(model, recording.Samples, _vocabulary(), CancellationToken.None);
            SetMode(Mode.Idle);
            if (!string.IsNullOrWhiteSpace(heard)) await HandleAsync(heard);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetMode(Mode.Idle);
            AddNote("Couldn't hear you: " + e.Message);
        }
    }

    void SayLine(string line)
    {
        AddBubble(line, mine: false);
        _lastWasQuestion = line.TrimEnd().EndsWith('?');
        if (_settings.AssistantSpeaks && IsVisible)
        {
            SetMode(Mode.Speaking);
            _voice.SpeakAsync(line);
        }
        else if (_mode != Mode.Thinking) SetMode(Mode.Idle);
    }

    void OnSpeechDone()
    {
        if (_voice.State == SynthesizerState.Speaking) return;   // more lines queued
        if (_mode == Mode.Speaking) SetMode(_busy ? Mode.Thinking : Mode.Idle);
        if (_busy) return;   // Nova is still working: listen once it has finished
        if (_lastWasQuestion && _settings.AssistantListensForReply && IsVisible && _mode == Mode.Idle)
        {
            _lastWasQuestion = false;
            _ = ListenAsync();
        }
    }

    void StopSpeaking()
    {
        _voice.SpeakAsyncCancelAll();
        if (_mode == Mode.Speaking) SetMode(Mode.Idle);
    }

    /// <summary>An approval card in the conversation; Nova waits for Yes or No.</summary>
    Task<bool> ApproveAsync(string action)
    {
        var answer = new TaskCompletionSource<bool>();
        var card = new Border
        {
            Background = new SolidColorBrush(WithAlpha(_accent, 0x22)),
            BorderBrush = new SolidColorBrush(_accent), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius()), Padding = new Thickness(12), Margin = new Thickness(0, 6, 30, 6),
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Approve?", FontWeight = FontWeights.SemiBold, FontSize = 11,
            Foreground = new SolidColorBrush(_accent),
        });
        stack.Children.Add(new TextBlock
        {
            Text = Capitalise(action), TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Foreground = new SolidColorBrush(_text), Margin = new Thickness(0, 4, 0, 10),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Decide(bool yes)
        {
            row.IsEnabled = false;
            card.Opacity = 0.6;
            answer.TrySetResult(yes);
        }
        row.Children.Add(Pill("Yes, do it", true, () => Decide(true)));
        row.Children.Add(Pill("No", false, () => Decide(false)));
        stack.Children.Add(row);
        card.Child = stack;
        _transcript.Children.Add(card);
        _scroll.ScrollToEnd();
        SayLine($"Should I {action}?");
        _lastWasQuestion = false;   // answered with the card, not by voice
        return answer.Task;
    }

    // ------------------------------------------------------------------ look

    UIElement BuildLayout()
    {
        var panel = new Border
        {
            Background = new LinearGradientBrush(WithAlpha(Mix(_surface, _page, 0.3), 0xF4), WithAlpha(_page, 0xF8), 90),
            BorderBrush = new SolidColorBrush(WithAlpha(_glow ? _accent : _border, 0xC0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius() * 1.5),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect
            {
                Color = _glow ? _accent : Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = _glow ? 0.5 : 0.6,
            },
        };

        var grid = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: the orb, the name, what Nova is doing, and a close button.
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var orb = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 12, 0) };
        _halo.Stroke = new SolidColorBrush(WithAlpha(_accent, 0x55));
        _halo.StrokeThickness = 1.5;
        _orb.Width = _orb.Height = 30;
        _orb.Fill = new RadialGradientBrush(Mix(_accent, Colors.White, 0.45), _accent) { GradientOrigin = new Point(0.35, 0.3) };
        _orb.RenderTransformOrigin = new Point(0.5, 0.5);
        _orb.RenderTransform = _orbScale;
        _orb.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.8 };
        _thinkArc.Data = Geometry.Parse("M 23,2 A 21,21 0 0 1 44,23");
        _thinkArc.Stroke = new SolidColorBrush(_accent);
        _thinkArc.StrokeThickness = 2.5;
        _thinkArc.StrokeEndLineCap = _thinkArc.StrokeStartLineCap = PenLineCap.Round;
        _thinkArc.RenderTransform = new RotateTransform(0, 23, 23);
        _thinkArc.Visibility = Visibility.Collapsed;
        orb.Children.Add(_halo);
        orb.Children.Add(_orb);
        orb.Children.Add(_thinkArc);
        DockPanel.SetDock(orb, Dock.Left);
        header.Children.Add(orb);

        var close = Pill("", false, Dismiss, glyph: true);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = "NOVA", FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(_text), Effect = _glow ? new DropShadowEffect { Color = _accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 } : null,
        });
        names.Children.Add(_state);
        _state.FontSize = 11.5;
        _state.Foreground = new SolidColorBrush(_secondary);
        header.Children.Add(names);
        grid.Children.Add(header);

        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);

        // Input: type and press Enter, or the mic.
        var inputRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var mic = Pill("", true, () =>
        {
            if (_mode == Mode.Listening) _work?.Cancel();
            else _ = ListenAsync();
        }, glyph: true);
        DockPanel.SetDock(mic, Dock.Right);
        inputRow.Children.Add(mic);
        _input.Background = new SolidColorBrush(WithAlpha(_surface, 0xE0));
        _input.Foreground = new SolidColorBrush(_text);
        _input.CaretBrush = new SolidColorBrush(_accent);
        _input.BorderBrush = new SolidColorBrush(WithAlpha(_border, 0xC0));
        _input.BorderThickness = new Thickness(1);
        _input.Padding = new Thickness(10, 8, 10, 8);
        _input.FontSize = 13.5;
        _input.Margin = new Thickness(0, 0, 8, 0);
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var text = _input.Text;
            _input.Clear();
            await HandleAsync(text);
        };
        inputRow.Children.Add(_input);
        Grid.SetRow(inputRow, 2);
        grid.Children.Add(inputRow);

        panel.Child = grid;
        SetMode(Mode.Idle);
        return panel;
    }

    void SetMode(Mode mode)
    {
        _mode = mode;
        _state.Text = mode switch
        {
            Mode.Listening => "Listening…  (Esc to stop)",
            Mode.Thinking => "Thinking…",
            Mode.Speaking => "Speaking…",
            _ => "Ready. Type, or press the mic.",
        };
        _thinkArc.Visibility = mode == Mode.Thinking ? Visibility.Visible : Visibility.Collapsed;
        _thinkArc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, mode == Mode.Thinking
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);

        var breathe = new DoubleAnimation(1, mode == Mode.Speaking ? 1.18 : 1.06, TimeSpan.FromSeconds(mode == Mode.Speaking ? 0.35 : 1.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        _orbScale.BeginAnimation(ScaleTransform.ScaleXProperty, mode == Mode.Listening ? null : breathe);
        _orbScale.BeginAnimation(ScaleTransform.ScaleYProperty, mode == Mode.Listening ? null : breathe);
        _halo.Opacity = mode == Mode.Listening ? 1 : 0.4;
        _halo.Stroke = new SolidColorBrush(WithAlpha(_accent, mode == Mode.Listening ? (byte)0xE0 : (byte)0x55));
    }

    void AddBubble(string text, bool mine)
    {
        var bubble = new Border
        {
            Background = new SolidColorBrush(mine ? WithAlpha(_accent, 0x30) : WithAlpha(_surface, 0xE8)),
            BorderBrush = new SolidColorBrush(mine ? WithAlpha(_accent, 0x80) : WithAlpha(_border, 0x90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius()),
            Padding = new Thickness(11, 8, 11, 9),
            Margin = mine ? new Thickness(48, 4, 0, 4) : new Thickness(0, 4, 36, 4),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 19,
                Foreground = new SolidColorBrush(_text),
            },
        };
        _transcript.Children.Add(bubble);
        _scroll.ScrollToEnd();
    }

    void AddNote(string text)
    {
        _transcript.Children.Add(new TextBlock
        {
            Text = "›  " + text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(_secondary), Margin = new Thickness(4, 2, 0, 2),
        });
        _scroll.ScrollToEnd();
    }

    void Greet()
    {
        var hour = DateTime.Now.Hour;
        var hello = hour < 12 ? "Good morning." : hour < 17 ? "Good afternoon." : "Good evening.";
        AddBubble($"{hello} I'm Nova. Ask me to open something, check your email, or just talk.", mine: false);
    }

    static string ToolNote(string tool) => tool switch
    {
        "check_inbox" => "checking your inbox",
        "read_email" => "reading the email",
        "open_email" => "opening the email",
        "reply_email" => "drafting a reply",
        _ => "working on it",
    };

    Border Pill(string label, bool primary, Action click, bool glyph = false)
    {
        var pill = new Border
        {
            Background = new SolidColorBrush(primary ? WithAlpha(_accent, 0xE0) : WithAlpha(_surface, 0xE0)),
            BorderBrush = new SolidColorBrush(primary ? _accent : WithAlpha(_border, 0xC0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius()),
            Padding = glyph ? new Thickness(10, 7, 10, 7) : new Thickness(14, 6, 14, 7),
            Margin = new Thickness(0, 0, glyph ? 0 : 8, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label, FontSize = glyph ? 14 : 13,
                FontFamily = glyph ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : _font,
                Foreground = new SolidColorBrush(primary ? Contrast(_accent) : _text),
            },
        };
        pill.MouseEnter += (_, _) => pill.Opacity = 0.85;
        pill.MouseLeave += (_, _) => pill.Opacity = 1;
        pill.MouseLeftButtonUp += (_, _) => click();
        return pill;
    }

    double Radius() => Math.Max(2, Suite.PaletteFor(_settings).Radius);

    void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Bottom - Height - 8;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_mode == Mode.Listening) { _discardListen = true; _work?.Cancel(); SetMode(Mode.Idle); }
        else if (_mode == Mode.Thinking) { _work?.Cancel(); SetMode(Mode.Idle); }
        else if (_mode == Mode.Speaking) StopSpeaking();
        else Dismiss();
    }

    void Dismiss()
    {
        _discardListen = true;
        _lastWasQuestion = false;
        _work?.Cancel();
        StopSpeaking();
        SetMode(Mode.Idle);
        Hide();
    }

    static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Contrast(Color c) =>
        0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White;
}
