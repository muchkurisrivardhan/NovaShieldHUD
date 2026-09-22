using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Beacon.UI;

/// <summary>
/// Makes mouse-wheel and touchpad scrolling match the rest of Windows.
///
/// WPF's ScrollViewer ignores how far the wheel actually moved: every wheel event scrolls
/// a fixed three lines, whether its delta is a full notch (120) or a sliver. Precision
/// touchpads - which this Snapdragon laptop has - report a swipe as many small events,
/// so each tiny movement jumped a whole notch and scrolling felt several times too fast.
///
/// This scrolls in proportion to the delta and honours the Windows "lines to scroll at a
/// time" setting (including "one screen at a time"). Installed once for every
/// ScrollViewer in the app.
/// </summary>
public static class WheelScrolling
{
    /// <summary>A full notch on a classic mouse wheel.</summary>
    private const double Notch = 120;

    /// <summary>Partial movement not yet large enough for a whole line, per scroll viewer.</summary>
    private static readonly ConditionalWeakTable<ScrollViewer, StrongBox<double>> Pending = new();

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer outermost) return;

        // Preview events tunnel from the outside in, so the outermost scroll viewer sees
        // the wheel first. Scroll the innermost one under the pointer that can still move
        // in that direction; if it is at its end, the next one out takes over.
        var target = FindTarget(e.OriginalSource as DependencyObject, outermost, e.Delta);
        if (target is null) return;

        e.Handled = true;
        Scroll(target, e.Delta);
    }

    private static ScrollViewer? FindTarget(DependencyObject? from, ScrollViewer outermost, int delta)
    {
        for (var node = from; node is not null; node = ParentOf(node))
        {
            if (node is ScrollViewer viewer && CanScroll(viewer, delta)) return viewer;
            if (ReferenceEquals(node, outermost)) break;
        }

        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight > 0 &&
        (delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight);

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);   // text runs are not in the visual tree

    private static void Scroll(ScrollViewer viewer, int delta)
    {
        var pending = Pending.GetValue(viewer, _ => new StrongBox<double>());

        // Reversing direction discards leftovers from the other way.
        if (pending.Value != 0 && Math.Sign(pending.Value) != Math.Sign(delta)) pending.Value = 0;
        pending.Value += delta;

        var lines = SystemParameters.WheelScrollLines;

        // Windows "one screen at a time".
        if (lines < 0)
        {
            while (pending.Value >= Notch) { viewer.PageUp(); pending.Value -= Notch; }
            while (pending.Value <= -Notch) { viewer.PageDown(); pending.Value += Notch; }
            return;
        }

        // LineUp/LineDown use the viewer's own unit - pixels for pages, one item for lists -
        // and are queued, so rapid touchpad events are never lost to stale offsets.
        var perLine = Notch / Math.Max(lines, 1);
        while (pending.Value >= perLine) { viewer.LineUp(); pending.Value -= perLine; }
        while (pending.Value <= -perLine) { viewer.LineDown(); pending.Value += perLine; }
    }
}
