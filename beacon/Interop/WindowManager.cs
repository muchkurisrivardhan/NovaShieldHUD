using static Beacon.Interop.NativeMethods;

namespace Beacon.Interop;

public enum SnapZone
{
    Left,
    Right,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    FullWorkArea,
}

/// <summary>
/// Positioning and state changes applied to a *target* window — always the one
/// <see cref="ForegroundTracker"/> captured, never Beacon itself.
/// </summary>
public static class WindowManager
{
    public static bool Maximize(IntPtr hWnd) =>
        Valid(hWnd) && ShowWindow(hWnd, SW_MAXIMIZE);

    public static bool Minimize(IntPtr hWnd) =>
        Valid(hWnd) && ShowWindow(hWnd, SW_MINIMIZE);

    public static bool Restore(IntPtr hWnd) =>
        Valid(hWnd) && ShowWindow(hWnd, SW_RESTORE);

    /// <summary>
    /// Polite close — the same WM_CLOSE the title-bar X sends, so the app still gets
    /// to prompt about unsaved work. Deliberately not TerminateProcess.
    /// </summary>
    public static bool Close(IntPtr hWnd) =>
        Valid(hWnd) && PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static bool Snap(IntPtr hWnd, SnapZone zone)
    {
        if (!Valid(hWnd)) return false;
        if (!TryGetWorkArea(hWnd, out var work)) return false;

        // A maximized window ignores SetWindowPos geometry, so drop it to normal first.
        if (IsZoomed(hWnd)) ShowWindow(hWnd, SW_RESTORE);

        int halfW = work.Width / 2;
        int halfH = work.Height / 2;

        var (x, y, w, h) = zone switch
        {
            SnapZone.Left => (work.Left, work.Top, halfW, work.Height),
            SnapZone.Right => (work.Left + halfW, work.Top, work.Width - halfW, work.Height),
            SnapZone.TopLeft => (work.Left, work.Top, halfW, halfH),
            SnapZone.TopRight => (work.Left + halfW, work.Top, work.Width - halfW, halfH),
            SnapZone.BottomLeft => (work.Left, work.Top + halfH, halfW, work.Height - halfH),
            SnapZone.BottomRight => (work.Left + halfW, work.Top + halfH, work.Width - halfW, work.Height - halfH),
            SnapZone.Center => CenteredBox(work),
            SnapZone.FullWorkArea => (work.Left, work.Top, work.Width, work.Height),
            _ => (work.Left, work.Top, work.Width, work.Height),
        };

        return SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Centers at 60% of the work area — a readable default for a focused window.</summary>
    private static (int, int, int, int) CenteredBox(RECT work)
    {
        int w = (int)(work.Width * 0.6);
        int h = (int)(work.Height * 0.75);
        return (work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);
    }

    /// <summary>
    /// Moves the window to the next monitor, preserving its relative position and
    /// size proportionally so it doesn't land half off-screen on a differently sized display.
    /// </summary>
    public static bool MoveToNextMonitor(IntPtr hWnd)
    {
        if (!Valid(hWnd)) return false;

        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length < 2) return false;

        if (!GetWindowRect(hWnd, out var winRect)) return false;

        var current = System.Windows.Forms.Screen.FromHandle(hWnd);
        var index = Array.FindIndex(screens, s => s.DeviceName == current.DeviceName);
        var next = screens[(index + 1) % screens.Length];

        var from = current.WorkingArea;
        var to = next.WorkingArea;

        bool wasMaximized = IsZoomed(hWnd);
        if (wasMaximized) ShowWindow(hWnd, SW_RESTORE);

        // Map position and size as fractions of the source work area.
        double fx = from.Width == 0 ? 0 : (winRect.Left - from.Left) / (double)from.Width;
        double fy = from.Height == 0 ? 0 : (winRect.Top - from.Top) / (double)from.Height;
        double fw = from.Width == 0 ? 0.5 : winRect.Width / (double)from.Width;
        double fh = from.Height == 0 ? 0.5 : winRect.Height / (double)from.Height;

        int nx = to.Left + (int)(fx * to.Width);
        int ny = to.Top + (int)(fy * to.Height);
        int nw = Math.Max(200, (int)(fw * to.Width));
        int nh = Math.Max(150, (int)(fh * to.Height));

        var moved = SetWindowPos(hWnd, IntPtr.Zero, nx, ny, nw, nh,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        if (moved && wasMaximized) ShowWindow(hWnd, SW_MAXIMIZE);
        return moved;
    }

    private static bool TryGetWorkArea(IntPtr hWnd, out RECT work)
    {
        work = default;

        var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        work = info.rcWork;
        return true;
    }

    private static bool Valid(IntPtr hWnd) => hWnd != IntPtr.Zero && IsWindow(hWnd);
}
