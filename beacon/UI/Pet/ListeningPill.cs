using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Beacon.Core;
using Beacon.Interop;

namespace Beacon.UI.Pet;

/// <summary>
/// The "Listening" pill: a small tag that rides beside the cursor while Nova listens, with
/// voice bars that move with the mic. When you stop talking it shrinks to three pulsing dots
/// until the reply starts, then goes. It wears the Beacon style: the accent, the surface and
/// text colours, the font, and the corners (square-cut for Tactical, HUD, Cyberpunk and CRT,
/// round for Mono and Classic).
///
/// Clicks go straight through it, and it never takes the keyboard.
/// </summary>
public sealed class ListeningPill : Window
{
    const int Bars = 7;
    const double BarMax = 16, BarMin = 3;

    readonly Settings _settings;
    readonly Border _pill = new();
    readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = BarMax };
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly TextBlock _label = new() { Text = "Listening", FontSize = 12.5, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
    readonly double[] _heights = new double[Bars];
    readonly DateTime _start = DateTime.Now;
    bool _thinking, _closing;

    /// <summary>The mic level, 0 to 1.</summary>
    public double Level { get; set; }

    public ListeningPill(Settings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        for (var i = 0; i < Bars; i++)
            _bars.Children.Add(new Rectangle { Width = 3, Height = BarMin, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center });
        for (var i = 0; i < 3; i++)
            _dots.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0) });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot);
        row.Children.Add(_bars);
        row.Children.Add(_dots);
        row.Children.Add(_label);
        _pill.Child = row;
        _pill.Padding = new Thickness(12, 7, 14, 7);
        _pill.Margin = new Thickness(14);   // room for the glow
        Content = _pill;

        // Clicks pass through to whatever is under it.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | 0x20 /* WS_EX_TRANSPARENT */;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>Shown beside the cursor, listening. Hold mode says to let go to send.</summary>
    public void Listen(bool hold)
    {
        ApplyLook();
        _thinking = false;
        _closing = false;
        _bars.Visibility = Visibility.Visible;
        _dots.Visibility = Visibility.Collapsed;
        _label.Text = hold ? "Listening · let go to send" : "Listening";
        _label.Visibility = Visibility.Visible;
        Array.Clear(_heights);
        Opacity = 1;
        Place();
        if (!IsVisible) Show();
    }

    /// <summary>Done listening: three dots until the reply starts.</summary>
    public void Think()
    {
        if (!IsVisible || _closing) return;
        _thinking = true;
        _bars.Visibility = Visibility.Collapsed;
        _label.Visibility = Visibility.Collapsed;
        _dots.Visibility = Visibility.Visible;
    }

    /// <summary>Fades away (the reply started, nothing was heard, or Nova left).</summary>
    public void FadeOut(bool now = false)
    {
        if (!IsVisible) return;
        if (now) { Hide(); return; }
        _closing = true;
    }

    void ApplyLook()
    {
        var p = Suite.PaletteFor(_settings);
        Color C(string key, string fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback); }
            catch (FormatException) { return Colors.Black; }
        }
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(p.Accent); }
        catch (FormatException) { accent = Color.FromRgb(0xF0, 0xA0, 0x30); }

        var surface = C("surface", "#26262A");
        _pill.Background = new SolidColorBrush(Color.FromArgb(0xF0, surface.R, surface.G, surface.B));
        _pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0xC8, accent.R, accent.G, accent.B));
        _pill.BorderThickness = new Thickness(1);
        // Round styles get a true pill; the sharp ones a cut tag, as the rest of Beacon.
        _pill.CornerRadius = new CornerRadius(p.Radius >= 6 ? 16 : p.Radius * 1.5);
        _pill.Effect = new DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
        _label.Foreground = new SolidColorBrush(C("text", "#F2F2F2"));
        _label.FontFamily = new FontFamily(p.Font + ", Segoe UI");
        var glow = new SolidColorBrush(accent);
        _dot.Fill = glow;
        foreach (Shape s in _bars.Children) s.Fill = glow;
        foreach (Shape s in _dots.Children) s.Fill = glow;
    }

    /// <summary>Just above and to the right of the pointer (Nova sits below it), kept on screen.</summary>
    void Place()
    {
        var px = System.Windows.Forms.Cursor.Position;
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
                ?? (Application.Current.MainWindow is { } w && PresentationSource.FromVisual(w) is { } s ? s.CompositionTarget.TransformFromDevice : Matrix.Identity);
        var cursor = m.Transform(new Point(px.X, px.Y));
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : 200;
        var height = ActualHeight > 0 ? ActualHeight : 60;
        var x = cursor.X + 4;
        var y = cursor.Y - height + 4;
        if (x + width > area.Right) x = cursor.X - width - 4;
        if (y < area.Top) y = cursor.Y + 20;
        Left = Math.Max(area.Left, x);
        Top = Math.Min(area.Bottom - height, y);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        Place();
        var t = (DateTime.Now - _start).TotalSeconds;

        if (_closing)
        {
            Opacity -= 0.12;
            if (Opacity <= 0.05) { Hide(); _closing = false; }
            return;
        }

        // The mic dot breathes.
        _dot.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(t * 3));

        if (_thinking)
        {
            for (var i = 0; i < 3; i++)
                ((UIElement)_dots.Children[i]).Opacity = 0.3 + 0.7 * Math.Max(0, Math.Sin(t * 6 - i * 0.9));
            return;
        }

        // Voice bars: taller with the voice, each with its own wobble; a faint ripple in silence.
        var level = Math.Min(1, Level * 1.6);
        for (var i = 0; i < Bars; i++)
        {
            var shape = 1 - Math.Abs(i - (Bars - 1) / 2.0) / Bars;   // taller in the middle
            var wobble = 0.55 + 0.45 * Math.Sin(t * 11 + i * 1.7);
            var target = BarMin + (BarMax - BarMin) * Math.Clamp(level * shape * wobble * 1.6 + 0.06 * (1 + Math.Sin(t * 4 + i)), 0, 1);
            _heights[i] += (target - _heights[i]) * (target > _heights[i] ? 0.6 : 0.25);
            ((FrameworkElement)_bars.Children[i]).Height = Math.Max(BarMin, _heights[i]);
        }
    }
}
