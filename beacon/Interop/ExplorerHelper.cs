using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Beacon.Interop;

public enum FolderOpenMode
{
    /// <summary>Navigate an already-open Explorer window instead of spawning another.</summary>
    ReuseWindow,

    NewWindow,

    /// <summary>Hand the path to a file manager the user configured.</summary>
    CustomManager,
}

/// <summary>
/// Opens folders without littering the desktop with Explorer windows.
///
/// Windows 11's Explorer tabs are not scriptable - there is no public API to open a path as
/// a tab in an existing window, and no shell verb for it. The closest supported behaviour is
/// to find an Explorer window that is already open and navigate it, via the Shell.Application
/// COM automation object. That is what this does; it is a genuine limitation of the platform,
/// not a shortcut taken here.
/// </summary>
public static class ExplorerHelper
{
    public static void OpenFolder(
        string path,
        FolderOpenMode mode,
        string? customManager,
        Action<string> notify)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            switch (mode)
            {
                case FolderOpenMode.CustomManager when !string.IsNullOrWhiteSpace(customManager):
                    LaunchCustom(customManager, path);
                    return;

                case FolderOpenMode.ReuseWindow when TryNavigateExistingWindow(path):
                    return;
            }

            // Falls through to a new window when reuse finds nothing to reuse, which is the
            // correct outcome rather than doing nothing.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            notify($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void LaunchCustom(string manager, string path)
    {
        // Allow either "app.exe" or "app.exe --flag {path}".
        var template = manager.Contains("{path}", StringComparison.OrdinalIgnoreCase)
            ? manager
            : manager + " \"{path}\"";

        var expanded = template.Replace("{path}", path, StringComparison.OrdinalIgnoreCase);

        var (exe, args) = SplitCommandLine(expanded);

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true,
        });
    }

    private static (string Exe, string Args) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            var close = commandLine.IndexOf('"', 1);
            if (close > 0)
                return (commandLine[1..close], commandLine[(close + 1)..].Trim());
        }

        var space = commandLine.IndexOf(' ');
        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..].Trim());
    }

    /// <summary>
    /// Navigates the most recently used Explorer window to <paramref name="path"/>.
    /// Returns false when no Explorer window is open, or COM automation is unavailable.
    /// </summary>
    private static bool TryNavigateExistingWindow(string path)
    {
        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return false;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return false;

            windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, null);
            if (windows is null) return false;

            var count = (int)(windows.GetType().InvokeMember("Count",
                System.Reflection.BindingFlags.GetProperty, null, windows, null) ?? 0);

            for (var i = count - 1; i >= 0; i--)
            {
                object? window = null;
                try
                {
                    window = windows.GetType().InvokeMember("Item",
                        System.Reflection.BindingFlags.InvokeMethod, null, windows, [i]);
                    if (window is null) continue;

                    var fullName = window.GetType().InvokeMember("FullName",
                        System.Reflection.BindingFlags.GetProperty, null, window, null) as string;

                    // Shell.Windows() also returns Internet Explorer windows on older builds.
                    if (fullName is null ||
                        !fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    window.GetType().InvokeMember("Navigate2",
                        System.Reflection.BindingFlags.InvokeMethod, null, window, [path]);

                    var hwnd = window.GetType().InvokeMember("HWND",
                        System.Reflection.BindingFlags.GetProperty, null, window, null);

                    if (hwnd is not null)
                        ForegroundTracker.FocusWindow(new IntPtr(Convert.ToInt64(hwnd)));

                    return true;
                }
                catch
                {
                    // One uncooperative window should not stop us trying the next.
                }
                finally
                {
                    if (window is not null && Marshal.IsComObject(window))
                        Marshal.ReleaseComObject(window);
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (windows is not null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }
}
