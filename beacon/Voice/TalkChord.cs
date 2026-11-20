namespace Beacon.Voice;

/// <summary>
/// The push-to-talk key, as a small state machine fed with raw key presses (from the keyboard
/// hook) and the clock. Plain .NET so it can be tested anywhere.
///
/// Hold the chord (Ctrl + Alt by default) for a moment: talking starts; let go: what you said is
/// sent. A quick tap instead starts hands-free listening, which ends when you pause. Pressing
/// any other key while holding it means you were reaching for a shortcut (Ctrl+Alt+N, Ctrl+Alt+P,
/// Ctrl+Alt+Del...): nothing starts, or what started is thrown away. AltGr, which European
/// keyboards use to type @ and €, sends Ctrl + Right Alt, so only the left Alt counts.
/// </summary>
public sealed class TalkChord
{
    public enum Mode { Off, CtrlAlt, RightCtrl }

    public enum Signal { None, HoldStart, HoldEnd, Tap, Cancel }

    /// <summary>How long the chord must be held before talking starts.</summary>
    public const long HoldMs = 250;

    // Virtual-key codes.
    const int LShift = 0xA0, RShift = 0xA1, LCtrl = 0xA2, RCtrl = 0xA3, LAlt = 0xA4, RAlt = 0xA5;
    const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12;

    enum Phase { Idle, Pending, Holding, Spoiled }

    readonly HashSet<int> _down = [];
    Phase _phase = Phase.Idle;
    long _since;

    public Mode Keys { get; set; } = Mode.CtrlAlt;

    /// <summary>True while it's worth calling Tick (the chord is down, waiting to become a hold).</summary>
    public bool Waiting => _phase == Phase.Pending;

    /// <summary>A key went down or up. Returns what that means.</summary>
    public Signal Key(int vk, bool down, long nowMs)
    {
        vk = vk switch { Shift => LShift, Ctrl => LCtrl, Alt => LAlt, _ => vk };
        if (Keys == Mode.Off) { _down.Clear(); _phase = Phase.Idle; return Signal.None; }

        if (down)
        {
            if (!_down.Add(vk)) return Signal.None;   // auto-repeat
            if (IsChordKey(vk))
            {
                if (_phase == Phase.Idle && ChordComplete() && OnlyChordDown())
                {
                    _phase = Phase.Pending;
                    _since = nowMs;
                }
                return Signal.None;
            }
            // Another key: this is a shortcut, not talking.
            var was = _phase;
            if (was is Phase.Pending or Phase.Holding) _phase = Phase.Spoiled;
            return was == Phase.Holding ? Signal.Cancel : Signal.None;
        }

        _down.Remove(vk);
        if (!IsChordKey(vk)) return Signal.None;
        var phase = _phase;
        switch (phase)
        {
            case Phase.Pending:
                _phase = Phase.Idle;
                return Signal.Tap;   // let go before it became a hold (or the clock missed it): hands-free
            case Phase.Holding:
                _phase = Phase.Idle;
                return Signal.HoldEnd;
            case Phase.Spoiled when !_down.Any(IsChordKey):
                _phase = Phase.Idle;
                return Signal.None;
        }
        return Signal.None;
    }

    /// <summary>Time passing while the chord is down: becomes a hold after HoldMs.</summary>
    public Signal Tick(long nowMs)
    {
        if (_phase != Phase.Pending || nowMs - _since < HoldMs) return Signal.None;
        _phase = Phase.Holding;
        return Signal.HoldStart;
    }

    /// <summary>
    /// Drops keys that aren't really down any more: a release Windows never reported (the PC
    /// locked mid-press, a key let go in a secure desktop) would otherwise block the chord.
    /// </summary>
    public void Forget(Func<int, bool> stillDown) => _down.RemoveWhere(vk => !stillDown(vk));

    /// <summary>Forget everything (the hook was reinstalled, or keys went missing while locked).</summary>
    public void Reset()
    {
        _down.Clear();
        _phase = Phase.Idle;
    }

    bool IsChordKey(int vk) => Keys switch
    {
        Mode.CtrlAlt => vk is LCtrl or RCtrl or LAlt,
        Mode.RightCtrl => vk == RCtrl,
        _ => false,
    };

    bool ChordComplete() => Keys switch
    {
        Mode.CtrlAlt => (_down.Contains(LCtrl) || _down.Contains(RCtrl)) && _down.Contains(LAlt) && !_down.Contains(RAlt),
        Mode.RightCtrl => _down.Contains(RCtrl),
        _ => false,
    };

    bool OnlyChordDown() => _down.All(IsChordKey);

    public static Mode Parse(string? setting) => setting switch
    {
        "off" => Mode.Off,
        "rctrl" => Mode.RightCtrl,
        _ => Mode.CtrlAlt,
    };
}
