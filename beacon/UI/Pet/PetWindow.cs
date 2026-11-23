using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beacon.Assistant;
using Beacon.Core;
using Beacon.LocalAi;
using Beacon.Interop;
using Beacon.Nl;
using Beacon.Teach;
using Visual = Beacon.Teach.Visual;
using PromptStyle = Beacon.LocalAi.PromptStyle;
using Beacon.Voice;

namespace Beacon.UI.Pet;

/// <summary>
/// Nova as a pet on the screen. Ctrl+M brings it up beside the cursor, listening; Ctrl+Alt+N
/// sends it away. It floats next to the pointer (following when the pointer moves away, staying
/// put when it comes close, so it can be petted), turns to look at it, answers out loud with a
/// small speech bubble, and listens again when it asked something. Click it to talk, right-click
/// it to send it away.
///
/// Push-to-talk: hold Ctrl+Alt (Settings > Avatar) to talk and let go to send; a quick tap
/// listens hands-free until you pause. While listening, the "Listening" pill rides beside the
/// cursor with voice bars, then shows dots until the reply starts.
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
    readonly ListeningPill _pill;
    ExplainBoard? _board;
    string _boardTopic = "";
    Point? _goTo;   // where Kai walks while pointing something out on the screen (this window's units)
    TaskCompletionSource? _spoken;   // the line being said aloud while explaining
    readonly Border _bubble = new();
    readonly TextBlock _bubbleText = new();
    readonly StackPanel _bubbleButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly double _petSize;

    CancellationTokenSource? _work;
    TaskCompletionSource<bool>? _approval;
    bool _busy, _lastWasQuestion, _discardListen, _leaving, _holding;
    int _listens;   // which listen is the latest, so an older one finishing doesn't tidy away a newer one
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
        _voice.SpeakCompleted += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_spoken is { } s && _voice.State != SynthesizerState.Speaking) { _spoken = null; s.TrySetResult(); }
            OnSpeechDone();
        });
        _pill = new ListeningPill(settings);
        _mic.Level += level => Dispatcher.BeginInvoke(() => _pet.Level = _pill.Level = level);

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
        Appear(target);
        if (WhisperModels.IsDownloaded(WhisperModels.Find(_settings.VoiceModel))) _ = ListenAsync();
        else Say("I need a speech model to hear you. Get one in Beacon Settings, on the Voice page.");
    }

    void Appear(IntPtr target)
    {
        _target[0] = target;
        if (IsVisible && !_leaving) return;
        _leaving = false;
        _pet.FollowSettings(_settings);
        var cursor = CursorDip();
        _pos = Beside(cursor);
        Left = _pos.X; Top = _pos.Y;
        Opacity = 1;
        Show();
        _pet.Appear();
    }

    // ------------------------------------------------------------------ push-to-talk

    /// <summary>The talk key has been held: Nova comes (if away) and listens until it's let go.</summary>
    public void TalkHold(IntPtr target)
    {
        Appear(target);
        _ = ListenAsync(hold: true);
    }

    /// <summary>The talk key was let go: what was said is sent.</summary>
    public void TalkRelease()
    {
        if (_holding) _work?.Cancel();
    }

    /// <summary>Another key was pressed mid-hold (a shortcut): what was heard is thrown away.</summary>
    public void TalkCancel()
    {
        if (!_holding) return;
        _discardListen = true;
        _work?.Cancel();
    }

    QuickAskBox? _ask;

    /// <summary>
    /// A quick tap of the talk key: the quick-ask box opens beside Kai, to type to the AI
    /// (Settings > Avatar can make a tap listen hands-free instead, like Ctrl+M).
    /// </summary>
    public void TalkTap(IntPtr target)
    {
        if (_settings.TalkTap == "listen") { Summon(target); return; }
        if (_ask is not null) { _ask.Activate(); return; }
        Appear(target);
        if (_pet.State == NovaPet3D.Activity.Listening) _work?.Cancel();
        HideBubble();
        var box = _ask = new QuickAskBox(_settings);
        box.PlaceBeside(new Rect(Left + (Width - _petSize) / 2, Top + Height - _petSize, _petSize, _petSize));
        void Back() { if (_target[0] != IntPtr.Zero) NativeMethods.SetForegroundWindow(_target[0]); }
        box.Closed += (_, _) => { if (_ask == box) _ask = null; };
        box.Sent += text => { Back(); _ = HandleAsync(text); };
        box.Talk += () => { Back(); _ = ListenAsync(); };
        box.Cancelled += Back;
        box.Show();
        _pet.State = NovaPet3D.Activity.Listening;
        _pet.Level = 0;
        box.Closed += (_, _) => { if (_pet.State == NovaPet3D.Activity.Listening && !_busy) _pet.State = NovaPet3D.Activity.Idle; };
    }

    /// <summary>
    /// Typed to Kai from elsewhere (the launcher's "?" row when there's no API key): Kai comes
    /// and answers with the brain chosen in Settings > Brain, your subscription included.
    /// </summary>
    public void Ask(string text, IntPtr target)
    {
        Appear(target);
        _ = HandleAsync(text);
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
        _pill.FadeOut(now: true);
        _board?.Dismiss();
        _ask?.Close();
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

        // Pointing something out: walk over beside it and look at it.
        if (_goTo is { } spot)
        {
            _pet.LookAt = new Point(spot.X - (Left + (Width - _petSize) / 2), spot.Y - (Top + Height - _petSize));
            var target = Beside(spot);
            var step = (target.X - _pos.X) * 0.08;
            _pos = new Point(_pos.X + step, _pos.Y + (target.Y - _pos.Y) * 0.08);
            Left = _pos.X; Top = _pos.Y;
            _pet.Walk = Math.Clamp(step / 10, -1, 1);
        }
        // Follow when the pointer wanders off; stay put when it comes near, so it can be petted.
        else if (_settings.PetFollowsCursor && _ask is null && (cursor - petCenter).Length > _petSize * 1.4)
        {
            var target = Beside(cursor);
            var step = (target.X - _pos.X) * 0.12;
            _pos = new Point(_pos.X + step, _pos.Y + (target.Y - _pos.Y) * 0.12);
            Left = _pos.X; Top = _pos.Y;
            _pet.Walk = Math.Clamp(step / 10, -1, 1);   // Kai turns and walks the way it's going
        }
        else
        {
            _pos = new Point(Left, Top);
            _pet.Walk = 0;
        }

        if (DateTime.Now > _hideBubbleAt) HideBubble();
    }

    // ------------------------------------------------------------------ conversation

    async Task ListenAsync(bool hold = false)
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
        _holding = hold;
        var listen = ++_listens;
        _pet.State = NovaPet3D.Activity.Listening;
        HideBubble();
        _pill.Listen(hold);
        // Load the speech model while you talk, so it's ready when you stop.
        _ = _transcriber.PreloadAsync(model, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        try
        {
            // Held: until the key is let go. Otherwise: until you pause.
            var recording = await _mic.RecordAsync(_settings.VoiceDevice, autoStop: !hold, work.Token);
            if (listen == _listens) _holding = false;
            _pet.Level = _pill.Level = 0;
            if (_discardListen || recording.End == RecordingEnd.NoSpeech || recording.Samples.Length == 0)
            {
                _pet.State = NovaPet3D.Activity.Idle;
                _pill.FadeOut();
                return;
            }
            _pet.State = NovaPet3D.Activity.Thinking;
            _pill.Think();
            var heard = await _transcriber.TranscribeAsync(model, recording.Samples, _vocabulary(), CancellationToken.None);
            _pet.State = NovaPet3D.Activity.Idle;
            if (!string.IsNullOrWhiteSpace(heard)) await HandleAsync(heard);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _pet.State = NovaPet3D.Activity.Idle;
            Say("I couldn't hear you: " + e.Message);
        }
        finally
        {
            if (listen == _listens)
            {
                _holding = false;
                _pill.FadeOut();
            }
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

            // The drawing on the screen: "thanks, got it" puts it away.
            if (_board is { IsVisible: true } && Intents.IsCloseBoard(text))
            {
                _board.Dismiss();
                Say("Okay!");
                return;
            }

            // "Fix it", "rewrite this as a research prompt", "make it shorter": the selected text.
            if (Intents.FindRewrite(text) is { } rewrite && await RewriteAsync(rewrite, work.Token)) return;

            // "What's this?", "where do I click to...": Kai looks at the screen and points.
            if (Intents.FindScreen(text) is { } look && _settings.KaiSeesScreen)
            {
                await SeeAsync(look, work.Token);
                return;
            }

            // With a drawing up, "tell me more about the mantle" draws that part in more detail.
            if (_board is { IsVisible: true } && System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"^(?:(?:can|could)\s+you\s+)?(?:go\s+deeper|tell\s+me\s+more|more\s+about|zoom\s+in|expand\s+on|what\s+about|explain\s+(?:the|that|this)\b)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                await ExplainAsync(new Intents.Explain($"{text} (we were just looking at: {_boardTopic})", Visual.Guess(text)), work.Token);
                return;
            }

            // "Explain...", "draw a mind map of...", "timeline of...": Kai draws it on the screen.
            if (Intents.FindExplain(text) is { } explain)
            {
                await ExplainAsync(explain, work.Token);
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

    /// <summary>
    /// The brain chosen in Settings > Brain, or null (after saying what's missing). The grammar
    /// and length are for the model on this PC; cloud models are told the shape in the prompt.
    /// </summary>
    Func<IReadOnlyList<(string Role, string Content)>, CancellationToken, Task<string>>? Brain(
        string? gbnf = AssistantBrain.Gbnf, int maxTokens = 400, bool quiet = false)
    {
        void Say(string line) { if (!quiet) this.Say(line); }
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
            case "muse":
                var museModel = _settings.BrainMuseModel;
                return (messages, ct) => MuseBrain.ChatAsync(museModel, effort, messages, ct);
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
        return (messages, ct) => LocalLlm.Shared.ChatAsync(path, messages, maxTokens, gbnf, ct);
    }

    // ------------------------------------------------------------------ rewriting the selection

    /// <summary>
    /// Rewrites the text selected in the app you were in: fixed, in a prompt-ring style (the ring
    /// is shown when no style was named), or changed as asked. Pasted back over the selection;
    /// Ctrl+Z in that app undoes it. False when it wasn't really about text (nothing selected
    /// for "make it bigger"-like requests), so the request goes on to the conversation.
    /// </summary>
    async Task<bool> RewriteAsync(Intents.Rewrite ask, CancellationToken ct)
    {
        var target = _target[0];
        SelectionTransfer.Snapshot? saved = null;
        PromptRingWindow? ring = null;
        try
        {
            await SelectionTransfer.WaitForModifiersReleasedAsync();
            saved = SelectionTransfer.Save();
            if (target != IntPtr.Zero) NativeMethods.SetForegroundWindow(target);
            await Task.Delay(120, ct);
            var text = (await SelectionTransfer.CopySelectionAsync()).Trim();
            if (text.Length == 0)
            {
                if (ask.Instruction is not null) return false;   // "make it shorter" with nothing selected: not about text
                Say("Select the text first, then ask me again.");
                return true;
            }
            if (text.Length > 12_000)
            {
                Say("That's too much text for me to rewrite in one go. Try a smaller part.");
                return true;
            }

            PromptStyle? style = ask.StyleId is { } id ? PromptStyles.Find(id) : null;
            if (ask.AskStyle)
            {
                // The prompt ring, at the cursor: pick how to rewrite it.
                Say("How should I rewrite it?");
                ring = new PromptRingWindow(_settings, text, true);
                ring.Show();
                style = await ring.Choice;
                if (style is null) { ring.Close(); ring = null; Say("Okay, I left it as it was."); return true; }
                ring.ShowWorking(style, true);
            }

            _pet.State = NovaPet3D.Activity.Thinking;
            IReadOnlyList<(string, string)> messages = style is not null
                ? [("system", PromptStyles.SystemPrompt(style)), ("user", PromptStyles.UserMessage(text))]
                : [("system", TeachPrompts.RewriteSystem(ask.Instruction!)), ("user", TeachPrompts.RewriteUser(text))];
            string result;
            if (Brain(null, 1500, quiet: style is not null) is { } brain)
                result = TeachPrompts.CleanRewrite(await Task.Run(() => brain(messages, ct), ct));
            else if (style is not null)
                result = PromptStyles.Template(style, text);   // no AI: the prompt-ring guide, filled from the text
            else
                return true;   // Brain() said what's missing

            ring?.Close();
            ring = null;
            if (result.Length == 0)
            {
                Say("I couldn't rewrite that one. Try again?");
                return true;
            }
            await SelectionTransfer.PasteAsync(target, result);
            _pet.Mood = NovaPet3D.Feeling.Happy;
            Say(style?.Id == "clean" ? "Fixed! Ctrl+Z undoes it." : "Done! Ctrl+Z undoes it.");
            return true;
        }
        finally
        {
            ring?.Close();
            if (saved is not null) SelectionTransfer.Restore(saved);
        }
    }

    // ------------------------------------------------------------------ explaining by drawing

    /// <summary>
    /// Kai explains by drawing: asks the brain for a picture (as data), takes over the screen
    /// with the sketchboard, and talks through it step by step while each part draws in.
    /// </summary>
    async Task ExplainAsync(Intents.Explain ask, CancellationToken ct)
    {
        if (Brain(TeachPrompts.DrawGbnf, _settings.DrawDetail == "detailed" ? 2200 : 1600) is not { } brain) return;
        // The question names no kind: the one picked in Settings > Drawing, if any.
        if (ask.Kind is null && _settings.DrawKind != "auto" && Visual.KindFrom(_settings.DrawKind) is { } preferred)
            ask = ask with { Kind = preferred };
        Say(ask.Kind switch
        {
            VisualKind.MindMap => "Let me map that out.",
            VisualKind.Timeline => "Let me put that on a timeline.",
            VisualKind.Cutaway => "Let me cut it open for you.",
            VisualKind.Iso3D => "Let me build that in 3D.",
            _ => "Let me draw that for you.",
        });
        _pet.State = NovaPet3D.Activity.Thinking;
        var system = TeachPrompts.DrawSystemFor(_settings.DrawDetail, _settings.DrawInstructions);
        var reply = await Task.Run(() => brain([("system", system), ("user", TeachPrompts.DrawUser(ask.Question, ask.Kind))], ct), ct);
        if (Visual.Parse(reply, ask.Kind) is not { } visual)
        {
            Say("Sorry, I couldn't work out a drawing for that. Could you ask it a little differently?");
            return;
        }
        _brain.Remember(ask.Question, $"I drew a {Visual.Name(visual.Kind)} of {visual.Title} on the screen and explained it.");

        // The drawing panel, in the corner of the screen the pointer is on (Settings > Drawing).
        _board?.Dismiss();
        var board = _board = new ExplainBoard(_settings, visual, ScreenDip(), null, WorkDip());
        _boardTopic = visual.Title;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TaskCompletionSource? skip = null;
        board.Dismissed += () =>
        {
            closed.Cancel();
            _voice.SpeakAsyncCancelAll();
            _goTo = null;
            if (_target[0] != IntPtr.Zero) NativeMethods.SetForegroundWindow(_target[0]);   // back to your app
        };
        board.Skipped += () => { _voice.SpeakAsyncCancelAll(); skip?.TrySetResult(); };
        board.Show();
        board.Activate();   // so Esc reaches it
        Topmost = false;    // Kai stays in front of the board
        Topmost = true;
        // Kai walks over beside the panel (not over it) and presents from there.
        if (_settings.DrawPlace != "full")
            _goTo = board.Left + board.Width / 2 < ScreenDip().Left + ScreenDip().Width / 2
                ? new Point(board.Left + board.Width + 6, board.Top + board.Height - _petSize - 20)
                : new Point(board.Left - _petSize - 60, board.Top + board.Height - _petSize - 20);

        try
        {
            await Task.Delay(300, closed.Token);
            for (var i = 0; i < visual.Steps.Count; i++)
            {
                board.Step(i);
                if (_settings.DrawNarrate) await SpeakAndWaitAsync(visual.Steps[i].Say, closed.Token);
                else
                {
                    // Not read aloud: long enough to read the caption, or until a click.
                    skip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var words = visual.Steps[i].Say.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    await Task.WhenAny(skip.Task, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(words * 0.38, 2.5, 12)), closed.Token));
                    closed.Token.ThrowIfCancellationRequested();
                }
            }
            board.Finish(visual.Title);
            Say("Want me to go deeper into any part? Say “got it” when you're done.");
            if (_settings.DrawCloseAfter > 0)
                _ = Task.Delay(TimeSpan.FromSeconds(_settings.DrawCloseAfter), closed.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled) Dispatcher.BeginInvoke(() => { if (_board == board) board.Dismiss(); });
                }, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }   // the board was closed
        finally { if (!board.IsVisible) _goTo = null; }
    }

    // ------------------------------------------------------------------ looking at the screen

    /// <summary>
    /// Kai looks at the screen: a screenshot of the monitor the pointer is on (Kai and the pill
    /// step out of the picture for it) goes to the brain with the question, and the answer comes
    /// back as callouts on the real screen. Kai walks to each thing while explaining it.
    /// </summary>
    async Task SeeAsync(Intents.Explain ask, CancellationToken ct)
    {
        if (SeeingBrain() is not { } see) return;
        _board?.Dismiss();
        _bubble.Visibility = Visibility.Collapsed;

        // Out of the picture for a moment, so the screenshot shows the screen, not Kai.
        var px = System.Windows.Forms.Cursor.Position;
        Opacity = 0;
        _pill.FadeOut(now: true);
        await Task.Delay(120, ct);
        ScreenCapture.Shot shot;
        try { shot = ScreenCapture.Capture(new System.Drawing.Point(px.X, px.Y)); }
        finally { Opacity = 1; }

        Say("Let me take a look.");
        _pet.State = NovaPet3D.Activity.Thinking;
        var system = TeachPrompts.SeeSystem(shot.Width, shot.Height);
        var reply = await Task.Run(() => see(system, TeachPrompts.SeeUser(ask.Question), shot.Jpeg, ct), ct);
        if (Visual.Parse(reply, VisualKind.Screen) is not { } visual)
        {
            Say("Hmm, I couldn't make sense of the screen this time. Could you ask again?");
            return;
        }
        _brain.Remember(ask.Question, "I looked at the screen and answered: " + string.Join(" ", visual.Steps.Select(s => s.Say)));

        var screen = ScreenDip();
        var board = _board = new ExplainBoard(_settings, visual, screen, new Size(shot.Width, shot.Height));
        _boardTopic = visual.Title;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        board.Dismissed += () => { closed.Cancel(); _voice.SpeakAsyncCancelAll(); _goTo = null; };
        board.Show();
        Topmost = false;   // Kai stays in front of the callouts
        Topmost = true;

        // Where each thing is, in this window's units: the screenshot scaled onto the screen.
        Point? Spot(string id)
        {
            var n = visual.Nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (n is null) return null;
            var (x, y) = n.Box is { } b ? (b[0] + b[2] / 2, b[1] + b[3]) : (n.Point![0], n.Point[1]);
            return new Point(screen.Left + x * screen.Width / shot.Width, screen.Top + y * screen.Height / shot.Height);
        }

        try
        {
            for (var i = 0; i < visual.Steps.Count; i++)
            {
                board.Step(i);
                _goTo = visual.Steps[i].Focus.Select(Spot).FirstOrDefault(p => p is not null) ?? _goTo;
                await SpeakAndWaitAsync(visual.Steps[i].Say, closed.Token);
            }
            board.Finish(visual.Title);
            _goTo = null;
            // Over the real screen it can't be clicked away, so it goes by itself after a while.
            _ = Task.Delay(TimeSpan.FromSeconds(20), closed.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) Dispatcher.BeginInvoke(() => { if (_board == board) board.Dismiss(); });
            }, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally { if (_board != board || !board.IsVisible) _goTo = null; }
    }

    /// <summary>A brain that can look at a picture, or null (after saying what's missing).</summary>
    Func<string, string, byte[], CancellationToken, Task<string>>? SeeingBrain()
    {
        var effort = _settings.BrainEffort;
        switch (_settings.Brain)
        {
            case "claude" when _settings.ApiKey is { Length: > 0 } key:
                var claudeModel = _settings.BrainClaudeModel;
                return (system, q, jpeg, ct) => CloudBrains.ClaudeSeeAsync(key, claudeModel, effort, system, q, jpeg, ct);
            case "openrouter" when _settings.OpenRouterKey is { Length: > 0 } orKey && _settings.BrainOpenRouterModel.Length > 0:
                var orModel = _settings.BrainOpenRouterModel;
                return (system, q, jpeg, ct) => CloudBrains.OpenRouterSeeAsync(orKey, orModel, effort, system, q, jpeg, ct);
            case "claudecode":
                var ccModel = _settings.BrainClaudeCodeModel;
                return (system, q, jpeg, ct) => CliBrains.ChatAsync(CliBrains.Cli.ClaudeCode, ccModel, effort, [("system", system), ("user", q)], ct, jpeg);
            case "codex":
                return (system, q, jpeg, ct) => CliBrains.ChatAsync(CliBrains.Cli.Codex, null, effort, [("system", system), ("user", q)], ct, jpeg);
            case "muse":
                var museModel = _settings.BrainMuseModel;
                return (system, q, jpeg, ct) => MuseBrain.ChatAsync(museModel, effort, [("system", system), ("user", q)], ct, jpeg);
            case "claude" or "openrouter":
                Brain();   // says what's missing
                return null;
        }
        Say("To see your screen I need a brain that can look at pictures: Claude, OpenRouter, Claude Code, ChatGPT or Muse. Pick one in Settings, on the Brain page.");
        return null;
    }

    /// <summary>Says a line of the explanation (no bubble: the board captions it) and waits until it's said.</summary>
    async Task SpeakAndWaitAsync(string line, CancellationToken ct)
    {
        _pet.Mood = NovaPet3D.Detect(line);
        if (!_settings.AssistantSpeaks || !IsVisible)
        {
            await Task.Delay(Math.Clamp(line.Length * 65, 1800, 9000), ct);
            return;
        }
        var done = _spoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pet.State = NovaPet3D.Activity.Speaking;
        _voice.SpeakAsync(line);
        using (ct.Register(() => done.TrySetCanceled()))
            await done.Task;
        _pet.State = NovaPet3D.Activity.Idle;
        await Task.Delay(250, ct);
    }

    /// <summary>The screen the pointer is on, in this window's units.</summary>
    /// <summary>The work area (screen without the taskbar) of the monitor the pointer is on, in this window's units.</summary>
    Rect WorkDip()
    {
        var px = System.Windows.Forms.Cursor.Position;
        var b = System.Windows.Forms.Screen.FromPoint(px).WorkingArea;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        return new Rect(m.Transform(new Point(b.Left, b.Top)), m.Transform(new Point(b.Right, b.Bottom)));
    }

    Rect ScreenDip()
    {
        var px = System.Windows.Forms.Cursor.Position;
        var b = System.Windows.Forms.Screen.FromPoint(px).Bounds;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(new Point(b.Left, b.Top));
        var br = m.Transform(new Point(b.Right, b.Bottom));
        return new Rect(tl, br);
    }

    void Say(string line)
    {
        _pill.FadeOut();   // the reply has started
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
