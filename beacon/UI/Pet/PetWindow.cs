using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beacon.Assistant;
using Beacon.Core;
using Beacon.LocalAi;
using Beacon.Nl;
using Beacon.Voice;

namespace Beacon.UI.Pet;

/// <summary>
/// Nova as a pet on the screen. Ctrl+M brings it up beside the cursor, listening; Ctrl+Alt+N
/// sends it away. It floats next to the pointer (following when the pointer moves away, staying
/// put when it comes close, so it can be petted), turns to look at it, answers out loud with a
/// small speech bubble, and listens again when it asked something. Click it to talk, right-click
/// it to send it away.
///
/// The window is see-through and never takes the keyboard: whatever you were typing in keeps
/// typing. Only the pet and its bubble catch the mouse.
/// </summary>
public sealed class PetWindow : Window
{
    readonly Settings _settings;
    readonly NlParser _parser;
    readonly AssistantTools _tools;
    readonly AssistantBrain _brain;
    readonly Func<string> _vocabulary;
    readonly MicRecorder _mic = new();
    readonly Transcriber _transcriber = new();
    readonly SpeechSynthesizer _voice = new();
    readonly IntPtr[] _target = [IntPtr.Zero];

    readonly NovaPet3D _pet = new();
    readonly Border _bubble = new();
    readonly TextBlock _bubbleText = new();
    readonly StackPanel _bubbleButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly double _petSize;

    CancellationTokenSource? _work;
    TaskCompletionSource<bool>? _approval;
    bool _busy, _lastWasQuestion, _discardListen, _leaving;
    DateTime _hideBubbleAt = DateTime.MaxValue;
    Point _pos;

    public PetWindow(Settings settings, NlParser parser, NlRunner runner, Func<string> vocabulary)
    {
        _settings = settings;
        _parser = parser;
        _vocabulary = vocabulary;
        _petSize = settings.AvatarSize switch { "small" => 150d, "large" => 260d, _ => 200d };

        var inbox = new EmailInbox(settings.EmailAccount);
        _tools = new AssistantTools(parser, runner, inbox, Dispatcher, () => _target[0], ApproveAsync, Say);
        _brain = new AssistantBrain(_tools);
        _brain.Said += line => Dispatcher.BeginInvoke(() => Say(line));
        _brain.Working += _ => Dispatcher.BeginInvoke(() => _pet.State = NovaPet3D.Activity.Thinking);

        _voice.SetOutputToDefaultAudioDevice();
        if (settings.AssistantVoice is { Length: > 0 } name)
            try { _voice.SelectVoice(name); } catch (ArgumentException) { }
        _voice.SpeakCompleted += (_, _) => Dispatcher.BeginInvoke(OnSpeechDone);
        _mic.Level += level => Dispatcher.BeginInvoke(() => _pet.Level = level);

        _pet.FollowSettings(settings);
        _pet.Tapped += () =>
        {
            if (_pet.State == NovaPet3D.Activity.Listening) _work?.Cancel();
            else _ = ListenAsync();
        };
        _pet.Petted += () =>
        {
            if (Random.Shared.Next(3) == 0 && !_busy && _pet.State == NovaPet3D.Activity.Idle)
                ShowBubble(new[] { "Hehe.", "That tickles!", "♥", "Thanks!", "Mmm." }[Random.Shared.Next(5)], 2);
        };
        _pet.MouseRightButtonUp += (_, e) => { e.Handled = true; Dismiss(); };

        Title = "Nova";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;   // never steal the keyboard from the app you're in
        ResizeMode = ResizeMode.NoResize;
        Width = Math.Max(300, _petSize + 100);
        Height = _petSize + 150;
        Content = BuildLayout();

        CompositionTarget.Rendering += Follow;
    }

    // ------------------------------------------------------------------ summon, dismiss

    /// <summary>Ctrl+M: appear beside the cursor and listen. Pressed again while listening: done talking.</summary>
    public void Summon(IntPtr target)
    {
        if (IsVisible && !_leaving)
        {
            if (_pet.State == NovaPet3D.Activity.Listening) _work?.Cancel();
            else _ = ListenAsync();
            return;
        }
        _leaving = false;
        _target[0] = target;
        _pet.FollowSettings(_settings);
        var cursor = CursorDip();
        _pos = Beside(cursor);
        Left = _pos.X; Top = _pos.Y;
        Opacity = 1;
        Show();
        _pet.Appear();
        if (WhisperModels.IsDownloaded(WhisperModels.Find(_settings.VoiceModel))) _ = ListenAsync();
        else Say("I need a speech model to hear you. Get one in Beacon Settings, on the Voice page.");
    }

    /// <summary>Ctrl+Alt+N or right-click: gone from the screen, everything stopped.</summary>
    public void Dismiss()
    {
        _discardListen = true;
        _lastWasQuestion = false;
        _work?.Cancel();
        _approval?.TrySetResult(false);
        _voice.SpeakAsyncCancelAll();
        _pet.State = NovaPet3D.Activity.Idle;
        HideBubble();
        Hide();
    }

    // ------------------------------------------------------------------ following the cursor

    Point CursorDip()
    {
        var px = System.Windows.Forms.Cursor.Position;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
                ?? (Application.Current.MainWindow is { } w && PresentationSource.FromVisual(w) is { } s ? s.CompositionTarget.TransformFromDevice : Matrix.Identity);
        return m.Transform(new Point(px.X, px.Y));
    }

    /// <summary>Where the window goes so the pet sits just below-right of the pointer, on screen.</summary>
    Point Beside(Point cursor)
    {
        var area = SystemParameters.WorkArea;
        var x = cursor.X + 28 - (Width - _petSize) / 2;
        var y = cursor.Y + 16 - (Height - _petSize);
        // Off the right or bottom edge: go to the other side of the pointer.
        if (x + Width > area.Right) x = cursor.X - 28 - Width + (Width - _petSize) / 2;
        if (y + Height > area.Bottom) y = area.Bottom - Height;
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    void Follow(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var cursor = CursorDip();
        var petCenter = new Point(Left + Width / 2, Top + Height - _petSize / 2);
        _pet.LookAt = new Point(cursor.X - (Left + (Width - _petSize) / 2), cursor.Y - (Top + Height - _petSize));

        // Follow when the pointer wanders off; stay put when it comes near, so it can be petted.
        if (_settings.PetFollowsCursor && (cursor - petCenter).Length > _petSize * 1.4)
        {
            var target = Beside(cursor);
            _pos = new Point(_pos.X + (target.X - _pos.X) * 0.12, _pos.Y + (target.Y - _pos.Y) * 0.12);
            Left = _pos.X; Top = _pos.Y;
        }
        else _pos = new Point(Left, Top);

        if (DateTime.Now > _hideBubbleAt) HideBubble();
    }

    // ------------------------------------------------------------------ conversation

    async Task ListenAsync()
    {
        var model = WhisperModels.Find(_settings.VoiceModel);
        if (!WhisperModels.IsDownloaded(model))
        {
            Say("I need a speech model to hear you. Get one in Beacon Settings, on the Voice page.");
            return;
        }
        _voice.SpeakAsyncCancelAll();
        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        _discardListen = false;
        _pet.State = NovaPet3D.Activity.Listening;
        ShowBubble("Listening…", 30);
        try
        {
            var recording = await _mic.RecordAsync(_settings.VoiceDevice, autoStop: true, work.Token);
            _pet.Level = 0;
            if (_discardListen || recording.End == RecordingEnd.NoSpeech || recording.Samples.Length == 0)
            {
                _pet.State = NovaPet3D.Activity.Idle;
                HideBubble();
                return;
            }
            _pet.State = NovaPet3D.Activity.Thinking;
            ShowBubble("…", 30);
            var heard = await _transcriber.TranscribeAsync(model, recording.Samples, _vocabulary(), CancellationToken.None);
            _pet.State = NovaPet3D.Activity.Idle;
            if (!string.IsNullOrWhiteSpace(heard)) await HandleAsync(heard);
            else HideBubble();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _pet.State = NovaPet3D.Activity.Idle;
            Say("I couldn't hear you: " + e.Message);
        }
    }

    async Task HandleAsync(string text)
    {
        text = text.Trim();
        _pet.Mood = NovaPet3D.Detect(text);

        // Waiting for a yes or no: the answer goes to the question.
        if (_approval is { Task.IsCompleted: false } pending)
        {
            var yes = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(yes|yeah|yep|sure|do it|go ahead|okay|ok)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var no = System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(no|nope|don't|cancel|stop)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (yes != no) { pending.TrySetResult(yes); return; }
        }

        _work?.Cancel();
        var work = _work = new CancellationTokenSource();
        _pet.State = NovaPet3D.Activity.Thinking;
        _busy = true;
        try
        {
            if (text.ToLowerInvariant() is "new chat" or "start over" or "reset")
            {
                _brain.Reset();
                Say("Okay, fresh start.");
                return;
            }
            if (text.ToLowerInvariant().TrimEnd('.', '!') is "go away" or "bye" or "goodbye" or "dismiss")
            {
                Say("Bye!");
                await Task.Delay(1200);
                Dismiss();
                return;
            }

            // A plain command Beacon fully understands runs at once, without the model.
            var plan = _parser.Parse(text);
            if (plan is { Kind: NlKind.Actions, Confidence: >= 1, IsDestructive: false } && plan.Steps.Count > 0)
            {
                await _tools.RunInstructionAsync(text);
                var reply = char.ToUpperInvariant(plan.Summary[0]) + plan.Summary[1..] + ".";
                _brain.Remember(text, reply);
                Say(reply);
                return;
            }

            if (Brain() is not { } brain) return;
            await Task.Run(() => _brain.RespondAsync(brain, text, work.Token), work.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Say("Something went wrong on my side: " + e.Message);
        }
        finally
        {
            _busy = false;
            if (_pet.State == NovaPet3D.Activity.Thinking) _pet.State = NovaPet3D.Activity.Idle;
            if (_voice.State != SynthesizerState.Speaking) OnSpeechDone();
        }
    }

    /// <summary>The brain chosen in Settings > Brain, or null (after saying what's missing).</summary>
    Func<IReadOnlyList<(string Role, string Content)>, CancellationToken, Task<string>>? Brain()
    {
        var effort = _settings.BrainEffort;
        switch (_settings.Brain)
        {
            case "claude" when _settings.ApiKey is { Length: > 0 } key:
                var claudeModel = _settings.BrainClaudeModel;
                return (messages, ct) => CloudBrains.ClaudeAsync(key, claudeModel, effort, messages, ct);
            case "claude":
                Say("Claude is my brain, but there's no API key yet. Add it in Settings, Brain, Accounts.");
                return null;
            case "claudecode":
                var ccModel = _settings.BrainClaudeCodeModel;
                return (messages, ct) => CliBrains.ChatAsync(CliBrains.Cli.ClaudeCode, ccModel, effort, messages, ct);
            case "codex":
                return (messages, ct) => CliBrains.ChatAsync(CliBrains.Cli.Codex, null, effort, messages, ct);
            case "openrouter" when _settings.OpenRouterKey is { Length: > 0 } orKey && _settings.BrainOpenRouterModel.Length > 0:
                var orModel = _settings.BrainOpenRouterModel;
                return (messages, ct) => CloudBrains.OpenRouterAsync(orKey, orModel, effort, messages, ct);
            case "openrouter":
                Say("OpenRouter is my brain, but it isn't connected yet. Do that in Settings, Brain, Accounts.");
                return null;
        }
        if (LocalModels.ForAssistant(_settings.LocalModel) is not { } model)
        {
            Say("I can open things for you now. To really talk, pick a brain in Beacon Settings, on the Brain page.");
            return null;
        }
        var path = LocalModels.PathFor(model);
        return (messages, ct) => LocalLlm.Shared.ChatAsync(path, messages, 400, AssistantBrain.Gbnf, ct);
    }

    void Say(string line)
    {
        _pet.Mood = NovaPet3D.Detect(line);
        _lastWasQuestion = line.TrimEnd().EndsWith('?');
        ShowBubble(line, 6 + line.Length / 18.0);
        if (_settings.AssistantSpeaks && IsVisible)
        {
            _pet.State = NovaPet3D.Activity.Speaking;
            _voice.SpeakAsync(line);
        }
    }

    void OnSpeechDone()
    {
        if (_voice.State == SynthesizerState.Speaking) return;   // more lines queued
        if (_pet.State == NovaPet3D.Activity.Speaking) _pet.State = _busy ? NovaPet3D.Activity.Thinking : NovaPet3D.Activity.Idle;
        if (_busy) return;
        if (_lastWasQuestion && _settings.AssistantListensForReply && IsVisible)
        {
            _lastWasQuestion = false;
            _ = ListenAsync();
        }
    }

    /// <summary>Something that can't be undone: asked in the bubble, answered by button or by saying yes or no.</summary>
    Task<bool> ApproveAsync(string action)
    {
        var answer = _approval = new TaskCompletionSource<bool>();
        Say($"Should I {action}?");
        _hideBubbleAt = DateTime.MaxValue;
        _bubbleButtons.Children.Clear();
        _bubbleButtons.Children.Add(Pill("Yes, do it", true, () => answer.TrySetResult(true)));
        _bubbleButtons.Children.Add(Pill("No", false, () => answer.TrySetResult(false)));
        _bubbleButtons.Visibility = Visibility.Visible;
        answer.Task.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _bubbleButtons.Visibility = Visibility.Collapsed;
            _hideBubbleAt = DateTime.Now.AddSeconds(2);
        }), TaskScheduler.Default);
        return answer.Task;
    }

    // ------------------------------------------------------------------ look

    UIElement BuildLayout()
    {
        var accent = _pet.Accent;
        var p = Suite.PaletteFor(_settings);
        Color C(string key, string fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback); }
            catch (FormatException) { return Colors.Black; }
        }

        _bubbleText.TextWrapping = TextWrapping.Wrap;
        _bubbleText.FontSize = 13.5;
        _bubbleText.Foreground = new SolidColorBrush(C("text", "#F2F2F2"));
        _bubbleText.FontFamily = new FontFamily(p.Font + ", Segoe UI");
        var stack = new StackPanel();
        stack.Children.Add(_bubbleText);
        stack.Children.Add(_bubbleButtons);
        _bubbleButtons.Visibility = Visibility.Collapsed;
        _bubble.Child = stack;
        _bubble.MaxWidth = Width - 16;
        _bubble.Padding = new Thickness(12, 9, 12, 10);
        _bubble.CornerRadius = new CornerRadius(Math.Max(3, p.Radius * 1.5));
        _bubble.Background = new SolidColorBrush(Color.FromArgb(0xEE, C("surface", "#26262A").R, C("surface", "#26262A").G, C("surface", "#26262A").B));
        _bubble.BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B));
        _bubble.BorderThickness = new Thickness(1);
        _bubble.Effect = new DropShadowEffect { Color = accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.45 };
        _bubble.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble.VerticalAlignment = VerticalAlignment.Bottom;
        _bubble.Margin = new Thickness(8, 0, 8, 4);
        _bubble.Visibility = Visibility.Collapsed;

        _pet.Width = _petSize;
        _pet.Height = _petSize;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_bubble);
        Grid.SetRow(_pet, 1);
        grid.Children.Add(_pet);
        return grid;
    }

    void ShowBubble(string text, double seconds)
    {
        _bubbleText.Text = text;
        _bubble.Visibility = Visibility.Visible;
        _hideBubbleAt = DateTime.Now.AddSeconds(seconds);
    }

    void HideBubble()
    {
        if (_approval is { Task.IsCompleted: false }) return;
        _bubble.Visibility = Visibility.Collapsed;
        _hideBubbleAt = DateTime.MaxValue;
    }

    Border Pill(string label, bool primary, Action click)
    {
        var a = _pet.Accent;
        var pill = new Border
        {
            Background = new SolidColorBrush(primary ? a : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label, FontSize = 12.5,
                Foreground = new SolidColorBrush(primary && 0.2126 * a.R + 0.7152 * a.G + 0.0722 * a.B > 150 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White),
            },
        };
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return pill;
    }
}
