using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beacon.Interop;

namespace Beacon.UI;

/// <summary>
/// Icons for apps and files, cached by path or shell name.
///
/// Lookups happen on the UI thread during rendering, so every miss is cached too - a path
/// with no icon costs one attempt, not one per keystroke. App icons are warmed on a
/// background thread at startup (<see cref="Prewarm"/>) so the first search that shows
/// them does not pay for rendering a few hundred icons.
/// </summary>
public static class IconCache
{
    /// <summary>
    /// Rendered size in physical pixels. Rows draw icons at 22 DIP, i.e. 44px at 200%;
    /// 64 leaves headroom for higher scale factors and downsamples cleanly.
    /// </summary>
    private const int PixelSize = 64;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Cache.GetOrAdd(path, Load);
    }

    private static ImageSource? Load(string path)
    {
        // Shell first: the only way to get Store app icons, and sharp at any DPI.
        var shell = ShellIcons.Load(path, PixelSize);
        if (shell is not null) return shell;

        // Plain file paths can still fall back to the classic 32px extractor.
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders icons ahead of time on a low-priority STA thread (the Shell requires COM
    /// single-threaded apartments). Frozen bitmaps are safe to hand to the UI thread later.
    /// </summary>
    public static void Prewarm(IEnumerable<string?> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct().ToList();
        if (list.Count == 0) return;

        var thread = new Thread(() =>
        {
            foreach (var path in list)
            {
                try
                {
                    Cache.GetOrAdd(path, Load);
                }
                catch
                {
                    // One broken shortcut must not stop the rest.
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Beacon icon prewarm",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
