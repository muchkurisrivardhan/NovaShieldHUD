using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Beacon.Interop;

/// <summary>
/// Reads where a Start Menu .lnk actually points.
///
/// The launcher indexes shortcuts, but a shortcut's own path
/// (…\Start Menu\Programs\Brave.lnk) is not what anyone means by "where is this app". The
/// target is, and only the Shell can say: a .lnk is a binary structure, and the target may
/// be stored as an ID list rather than a path.
///
/// COM here is apartment-threaded, so callers resolve on an STA thread (see AppIndex).
/// </summary>
internal static class ShortcutResolver
{
    /// <summary>The .exe a shortcut points at, or null if it cannot be read.</summary>
    public static string? Resolve(string shortcutPath)
    {
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, STGM_READ);

            var buffer = new StringBuilder(MAX_PATH);
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SLGP_RAWPATH);

            var target = Environment.ExpandEnvironmentVariables(buffer.ToString()).Trim();

            // Store apps and Control Panel entries are ID-list shortcuts with no file path
            // at all; an empty answer means "there is no path", not "something failed".
            return target.Length == 0 || !File.Exists(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private const int MAX_PATH = 260;
    private const int STGM_READ = 0;

    /// <summary>Ask for the path as stored, without resolving it to a short (8.3) form.</summary>
    private const uint SLGP_RAWPATH = 4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    // Only the methods actually called are declared, and GetPath is the first slot, so the
    // rest of the vtable can stay unnamed.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxChars,
            IntPtr findData,
            uint flags);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
    }
}
