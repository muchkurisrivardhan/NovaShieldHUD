using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>The Filee page: startup, the format ring, and a live preview of the ring in Beacon's style.</summary>
public partial class SettingsWindow
{
    private void InitFileePage()
    {
        FileeRing.ValueChanged += (_, _) =>
        {
            FileeRingValue.Text = $"{(int)FileeRing.Value} px";
            DrawFileePreview();
        };
        FileeOpacity.ValueChanged += (_, _) =>
        {
            FileeOpacityValue.Text = $"{FileeOpacity.Value * 100:N0}%";
            DrawFileePreview();
        };
        FileeCaptions.Click += (_, _) => DrawFileePreview();
        FileeFollowsLook.Click += (_, _) => DrawFileePreview();
        ThemePicker.SelectionChanged += (_, _) => DrawFileePreview();
        AccentColor.TextChanged += (_, _) => DrawFileePreview();

        FileeOpen.Click += (_, _) => FileeStatus.Text = Suite.OpenFilee(show: true);
        FileeClose.Click += (_, _) => FileeStatus.Text = Suite.CloseFilee();
    }

    private void LoadFileePage()
    {
        LaunchFilee.IsChecked = _settings.LaunchFilee;
        FileeFollowsLook.IsChecked = _settings.FileeFollowsLook;
        var f = Suite.ReadFilee();
        FileeRing.Value = f.RingSize;
        FileeOpacity.Value = f.Opacity;
        FileeCaptions.IsChecked = f.Captions;
        FileePaused.IsChecked = f.Paused;
        FileeUpdates.IsChecked = f.CheckUpdates;
        FileeStatus.Text = Suite.FindFilee() is null ? "Filee isn't installed next to Beacon."
            : Suite.FileeRunning ? "Filee is running." : "Filee isn't running.";
        DrawFileePreview();
    }

    private Suite.FileeOptions ReadFileeForm() => new(
        Math.Round(FileeRing.Value),
        Math.Round(FileeOpacity.Value, 2),
        FileeCaptions.IsChecked == true,
        FileePaused.IsChecked == true,
        FileeUpdates.IsChecked == true);

    // ------------------------------------------------------------------ preview

    private static readonly string[] PreviewFormats = ["PNG", "PDF", "WEBP", "JPG", "MP4", "DOCX"];

    /// <summary>
    /// Draws Filee's format ring as it will look in the style picked on the Appearance page
    /// (not yet saved): the same surfaces, accent, font and corners Beacon hands to Filee.
    /// </summary>
    private void DrawFileePreview()
    {
        if (FileePreview is null || ThemePicker is null) return;

        var draft = new Settings
        {
            OverlayStyle = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.OverlayStyle,
            AccentColor = AccentColor.Text.Trim(),
            Theme = _settings.Theme,
        };
        var follows = FileeFollowsLook.IsChecked == true;
        var p = Suite.PaletteFor(follows ? draft : new Settings { OverlayStyle = "classic", AccentColor = "#7F77DD" });
        var colors = follows ? p.Colors : null;

        Color C(string key, string fallback) => Parse(colors is not null && colors.TryGetValue(key, out var v) ? v : fallback);
        var accent = Parse(p.Accent);
        var page = C("page", "#1C1C1F");
        var surface = C("surface", "#26262A");
        var border = C("border", "#3A3A40");
        var text = C("text", "#F2F2F2");
        var secondary = C("textSecondary", "#A0A0A8");
        var font = new FontFamily(follows ? p.Font + ", Segoe UI" : "Segoe UI Variable Text, Segoe UI");
        var glowing = follows && draft.OverlayStyle is "hud" or "cyberpunk" or "crt";

        FileeLookName.Text = follows
            ? $"Now: {((ThemePicker.SelectedItem as ComboBoxItem)?.Content as string ?? draft.OverlayStyle)}"
            : "Now: Filee's own look (lavender, rounded)";

        FileePreviewBack.Background = new RadialGradientBrush(Mix(page, accent, 0.10), page);
        FileePreview.Children.Clear();

        // Backdrop grid lines in the framed looks, like the HUD's scanlines.
        if (glowing)
            for (var y = 4.0; y < 220; y += 4)
                FileePreview.Children.Add(new Line
                {
                    X1 = 0, X2 = 220, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(WithAlpha(accent, 0x0E)), StrokeThickness = 1,
                });

        const double cx = 110, cy = 110;
        var outer = 70 + (FileeRing.Value - 90) / (260 - 90) * 32;   // 70..102 for ring sizes 90..260
        var inner = outer * 0.42;
        var opacity = FileeOpacity.Value;
        var gap = p.Radius == 0 ? 1.2 : 2.4;   // degrees between slices
        var step = 360.0 / PreviewFormats.Length;

        for (var i = 0; i < PreviewFormats.Length; i++)
        {
            var a0 = -90 + i * step + gap / 2;
            var a1 = a0 + step - gap;
            var hot = i == 0;
            var slice = new Path
            {
                Data = Slice(cx, cy, inner, outer, a0, a1),
                Fill = new SolidColorBrush(hot ? WithAlpha(accent, (byte)(0xFF * Math.Max(opacity, 0.85)))
                                               : WithAlpha(surface, (byte)(0xFF * opacity))),
                Stroke = new SolidColorBrush(hot ? accent : WithAlpha(border, 0xC0)),
                StrokeThickness = 1,
            };
            if (hot && glowing)
                slice.Effect = new DropShadowEffect { Color = accent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 };
            FileePreview.Children.Add(slice);

            var mid = (a0 + a1) / 2 * Math.PI / 180;
            var r = (inner + outer) / 2;
            var label = new TextBlock
            {
                Text = PreviewFormats[i],
                FontFamily = font,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(hot ? Contrast(accent) : text),
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var dy = FileeCaptions.IsChecked == true ? -6 : 0;
            Canvas.SetLeft(label, cx + r * Math.Cos(mid) - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, cy + r * Math.Sin(mid) - label.DesiredSize.Height / 2 + dy);
            FileePreview.Children.Add(label);

            if (FileeCaptions.IsChecked == true)
            {
                var caption = new TextBlock
                {
                    Text = hot ? "convert" : "image",
                    FontFamily = font,
                    FontSize = 8.5,
                    Foreground = new SolidColorBrush(hot ? WithAlpha(Contrast(accent), 0xB0) : secondary),
                };
                caption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(caption, cx + r * Math.Cos(mid) - caption.DesiredSize.Width / 2);
                Canvas.SetTop(caption, cy + r * Math.Sin(mid) + 4);
                FileePreview.Children.Add(caption);
            }
        }

        // The hole: the dragged file, with a progress arc in the accent.
        var hole = new Ellipse
        {
            Width = inner * 2 - 8, Height = inner * 2 - 8,
            Fill = new SolidColorBrush(page),
            Stroke = new SolidColorBrush(WithAlpha(accent, 0x90)), StrokeThickness = 1,
        };
        Canvas.SetLeft(hole, cx - hole.Width / 2);
        Canvas.SetTop(hole, cy - hole.Height / 2);
        FileePreview.Children.Add(hole);
        FileePreview.Children.Add(new Path
        {
            Data = Arc(cx, cy, inner - 4, -90, 150),
            Stroke = new SolidColorBrush(accent), StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var file = new TextBlock
        {
            Text = draft.OverlayStyle == "crt" && follows ? "IMG_01_" : "trip.jpg",
            FontFamily = font, FontSize = 10, Foreground = new SolidColorBrush(text),
        };
        file.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(file, cx - file.DesiredSize.Width / 2);
        Canvas.SetTop(file, cy - file.DesiredSize.Height / 2);
        FileePreview.Children.Add(file);

        // Corner brackets for the HUD-family looks.
        if (follows && draft.OverlayStyle is not ("classic" or "mono"))
        {
            var bracket = new SolidColorBrush(WithAlpha(accent, 0xB0));
            foreach (var (x, y, sx, sy) in new[] { (8.0, 8.0, 1, 1), (212.0, 8.0, -1, 1), (8.0, 212.0, 1, -1), (212.0, 212.0, -1, -1) })
                FileePreview.Children.Add(new Polyline
                {
                    Points = [new Point(x, y + 14 * sy), new Point(x, y), new Point(x + 14 * sx, y)],
                    Stroke = bracket, StrokeThickness = 1.5,
                });
        }
    }

    private static Geometry Slice(double cx, double cy, double r0, double r1, double a0, double a1)
    {
        Point P(double r, double a) => new(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var large = a1 - a0 > 180;
        var figure = new PathFigure { StartPoint = P(r0, a0), IsClosed = true };
        figure.Segments.Add(new LineSegment(P(r1, a0), true));
        figure.Segments.Add(new ArcSegment(P(r1, a1), new Size(r1, r1), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(P(r0, a1), true));
        figure.Segments.Add(new ArcSegment(P(r0, a0), new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true));
        return new PathGeometry([figure]);
    }

    private static Geometry Arc(double cx, double cy, double r, double a0, double a1)
    {
        Point P(double a) => new(cx + r * Math.Cos(a * Math.PI / 180), cy + r * Math.Sin(a * Math.PI / 180));
        var figure = new PathFigure { StartPoint = P(a0) };
        figure.Segments.Add(new ArcSegment(P(a1), new Size(r, r), 0, a1 - a0 > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    private static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Black or white, whichever reads better on <paramref name="c"/>.</summary>
    private static Color Contrast(Color c) =>
        0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 150 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White;
}
