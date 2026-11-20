using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Beacon.Voice;

namespace Beacon.Interop;

/// <summary>
/// Watches the keyboard for the push-to-talk key (see <see cref="TalkChord"/>). Windows hotkeys
/// only report a press, never a release, so holding to talk needs a low-level keyboard hook.
/// The hook only looks: every key still goes to the app you're typing in, and nothing typed is
/// kept or sent anywhere. It runs on the UI thread and hands each key straight to the chord.
///
/// Windows doesn't let a normal app see keys typed into an app running as administrator, so
/// push-to-talk doesn't start while such a window has focus.
/// </summary>
internal sealed class PushToTalkHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    readonly TalkChord _chord = new();
    readonly HookProc _proc;   // kept alive: the hook calls it from native code
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    IntPtr _hook;

    /// <summary>Held long enough: start listening.</summary>
    public event Action? HoldStart;

    /// <summary>Let go after a hold: send what was said.</summary>
    public event Action? HoldEnd;

    /// <summary>A quick tap: listen hands-free until a pause.</summary>
    public event Action? Tap;

    /// <summary>Another key was pressed mid-hold (a shortcut): throw away what was heard.</summary>
    public event Action? Cancel;

    public PushToTalkHook()
    {
        _proc = OnKey;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        _timer.Tick += (_, _) =>
        {
            Raise(_chord.Tick(_clock.ElapsedMilliseconds));
            if (!_chord.Waiting) _timer.Stop();
        };
    }

    /// <summary>Which key, from the setting ("ctrl+alt", "rctrl" or "off"). Off removes the hook.</summary>
    public void Use(string? setting)
    {
        _chord.Keys = TalkChord.Parse(setting);
        _chord.Reset();
        if (_chord.Keys == TalkChord.Mode.Off) Unhook();
        else if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                Debug.WriteLine($"Push-to-talk: the keyboard hook was refused ({Marshal.GetLastWin32Error()}).");
        }
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var msg = (int)wParam;
                // Keys typed by software (ours included: the prompt ring pastes with Ctrl+V) don't count.
                if ((info.Flags & LLKHF_INJECTED) == 0 && msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
                {
                    var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                    if (down) _chord.Forget(vk => vk == (int)info.VkCode || (GetAsyncKeyState(vk) & 0x8000) != 0);
                    Raise(_chord.Key((int)info.VkCode, down, _clock.ElapsedMilliseconds));
                    if (_chord.Waiting && !_timer.IsEnabled) _timer.Start();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Never let a fault here hold up the keyboard.
                Debug.WriteLine("Push-to-talk: " + e.Message);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Raise(TalkChord.Signal signal)
    {
        // Handled after the key goes through: the hook must return quickly or Windows drops it.
        Action? handler = signal switch
        {
            TalkChord.Signal.HoldStart => HoldStart,
            TalkChord.Signal.HoldEnd => HoldEnd,
            TalkChord.Signal.Tap => Tap,
            TalkChord.Signal.Cancel => Cancel,
            _ => null,
        };
        if (handler is not null) _dispatcher.BeginInvoke(handler);
    }

    void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _timer.Stop();
    }

    public void Dispose() => Unhook();
}
