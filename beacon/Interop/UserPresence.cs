using System.Runtime.InteropServices;

namespace Beacon.Interop;

/// <summary>
/// What Windows knows about whether the user can be interrupted: a full-screen app, a game, a
/// presentation or a locked PC (SHQueryUserNotificationState, the same signal Windows uses
/// for its own notifications), and how long since the last key press or mouse move.
///
/// Windows' Do Not Disturb isn't exposed by any public API, so it isn't read here.
/// </summary>
public static class UserPresence
{
    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Why the user is busy right now, in words, or null when they can be interrupted.</summary>
    public static string? BusyReason()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return null;
            return state switch
            {
                1 => "your PC is locked",
                2 => "a full-screen app is open",
                3 => "a full-screen game is running",
                4 => "presentation mode is on",
                _ => null,
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Time since the last keyboard or mouse input.</summary>
    public static TimeSpan Idle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }
}
