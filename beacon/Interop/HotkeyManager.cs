using System.Windows.Interop;

namespace Beacon.Interop;

/// <summary>Why a hotkey registration failed.</summary>
public enum HotkeyResult
{
    Registered,

    /// <summary>Another process already owns this chord. Windows gives no way to learn which.</summary>
    AlreadyTaken,

    Invalid,
}

/// <summary>
/// Owns every global hotkey and pumps WM_HOTKEY out as an action name.
///
/// Supports many chords, not just the launcher's: a command bound to Ctrl+Alt+M must fire
/// directly without the bar ever appearing, which means one RegisterHotKey per binding and
/// a map from Windows' integer ids back to something meaningful.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    /// <summary>Reserved action name for the chord that opens the launcher itself.</summary>
    public const string LauncherAction = "__launcher__";

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    private readonly Dictionary<int, string> _actionById = [];
    private readonly Dictionary<string, int> _idByAction = new(StringComparer.Ordinal);

    // Windows only requires ids to be unique per window; start clear of anything else.
    private int _nextId = 0xB000;
    private bool _disposed;

    /// <summary>Raised on the UI thread with the action name that fired.</summary>
    public event Action<string>? Pressed;

    private HotkeyManager(HwndSource source)
    {
        _source = source;
        _hwnd = source.Handle;
        _source.AddHook(WndProc);
    }

    public static HotkeyManager Attach(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();

        var source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("Window has no HwndSource yet.");

        return new HotkeyManager(source);
    }

    /// <summary>
    /// Registers or replaces the chord for an action. A failure is reported rather than
    /// thrown: a dead hotkey is indistinguishable from a crashed app from the user's side,
    /// so the caller needs to be able to say so.
    /// </summary>
    public HotkeyResult Register(string action, uint modifiers, uint virtualKey)
    {
        Unregister(action);

        // A modifierless chord would swallow that key across the whole desktop.
        if (modifiers == 0 || virtualKey == 0) return HotkeyResult.Invalid;

        var id = _nextId++;

        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
            return HotkeyResult.AlreadyTaken;

        _actionById[id] = action;
        _idByAction[action] = id;
        return HotkeyResult.Registered;
    }

    public void Unregister(string action)
    {
        if (!_idByAction.TryGetValue(action, out var id)) return;

        NativeMethods.UnregisterHotKey(_hwnd, id);
        _idByAction.Remove(action);
        _actionById.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _actionById.Keys.ToList())
            NativeMethods.UnregisterHotKey(_hwnd, id);

        _actionById.Clear();
        _idByAction.Clear();
    }

    /// <summary>
    /// Re-registers everything from scratch. Returns the actions that could not be bound,
    /// so settings can show exactly which chords are taken instead of failing silently.
    /// </summary>
    public IReadOnlyList<string> ReplaceAll(IEnumerable<(string Action, uint Modifiers, uint Key)> bindings)
    {
        UnregisterAll();

        var failed = new List<string>();
        var claimed = new HashSet<(uint, uint)>();

        foreach (var (action, modifiers, key) in bindings)
        {
            // Catch duplicates within our own set before asking Windows, which would
            // otherwise just report the second one as taken by "another app" - us.
            if (!claimed.Add((modifiers, key)))
            {
                failed.Add(action);
                continue;
            }

            if (Register(action, modifiers, key) != HotkeyResult.Registered)
                failed.Add(action);
        }

        return failed;
    }

    public bool IsRegistered(string action) => _idByAction.ContainsKey(action);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        if (_actionById.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }

        return IntPtr.Zero;
    }

    /// <summary>Renders a chord the way Windows would write it, e.g. "Ctrl+Alt+M".</summary>
    public static string Describe(uint modifiers, uint virtualKey)
    {
        if (modifiers == 0 || virtualKey == 0) return "None";

        var parts = new List<string>(4);
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        parts.Add(System.Windows.Input.KeyInterop
            .KeyFromVirtualKey((int)virtualKey)
            .ToString());

        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
