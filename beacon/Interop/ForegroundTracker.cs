using System.Diagnostics;
using System.Text;

namespace Beacon.Interop;

/// <summary>
/// Remembers which window the user was actually in when they summoned Beacon.
///
/// This is the single most important piece of the overlay. Once our search window takes
/// focus, <c>GetForegroundWindow</c> returns *us* — so any command that acts on "the current
/// window" would target the search bar instead of the user's editor. We therefore capture
/// the handle on the way in and hand that captured handle to every command.
/// </summary>
public sealed class ForegroundTracker
{
    private IntPtr _captured;
    private IntPtr _selfHandle;

    public void BindSelf(IntPtr ownWindowHandle) => _selfHandle = ownWindowHandle;

    /// <summary>The window that was in front immediately before Beacon appeared.</summary>
    public IntPtr Captured => _captured;

    /// <summary>
    /// Call this *before* showing the overlay. Ignores our own window so that
    /// re-summoning while already open doesn't overwrite the real target.
    /// </summary>
    public void Capture()
    {
        var fg = NativeMethods.GetForegroundWindow();

        if (fg == IntPtr.Zero || fg == _selfHandle) return;
        if (fg == NativeMethods.GetShellWindow()) return;

        _captured = fg;
    }

    public bool HasLiveTarget =>
        _captured != IntPtr.Zero && NativeMethods.IsWindow(_captured);

    /// <summary>
    /// Puts focus back where it was. Plain SetForegroundWindow is unreliable because
    /// Windows blocks focus steals from a process that isn't the active one, so we
    /// briefly attach our input queue to the target's thread to inherit the right.
    /// </summary>
    public void Restore()
    {
        if (!HasLiveTarget) return;
        FocusWindow(_captured);
    }

    public static void FocusWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd)) return;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);

        var foreground = NativeMethods.GetForegroundWindow();
        var targetThread = NativeMethods.GetWindowThreadProcessId(hWnd, out _);
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var thisThread = NativeMethods.GetCurrentThreadId();

        var attachedForeground = foregroundThread != 0 && foregroundThread != thisThread
            && NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != thisThread
            && NativeMethods.AttachThreadInput(thisThread, targetThread, true);

        try
        {
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(thisThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    /// <summary>Human-readable description of the captured window, for the UI hint line.</summary>
    public string DescribeTarget()
    {
        if (!HasLiveTarget) return "no window";

        var title = GetTitle(_captured);
        var process = GetProcessName(_captured);

        if (string.IsNullOrWhiteSpace(title)) return process ?? "window";
        return title.Length > 48 ? title[..45] + "..." : title;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        var len = NativeMethods.GetWindowTextW(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        var len = NativeMethods.GetClassNameW(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    public static string? GetProcessName(IntPtr hWnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            // Process died between enumeration and inspection, or is protected.
            return null;
        }
    }
}
