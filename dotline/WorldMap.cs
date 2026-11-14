using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Dotline;

public sealed class MapState
{
    public bool On { get; set; } = true;
    public List<string> Zones { get; set; } = [];
    // Where the map sits inside the wallpaper image: Miller projection fitted to its grid lines.
    public string CalibratedFor { get; set; } = "2454655.jpg";
    public double X0 { get; set; } = 1188.5;   // image x of 0° longitude
    public double Y0 { get; set; } = 979;      // image y of the equator
    public double K { get; set; } = 404.9;     // image px per radian
}

public sealed record City(string Name, double Lat, double Lon, string Tz)
{
    public TimeZoneInfo Zone => Geo.Zone(Tz);
}

static class Geo
{
    public static readonly City[] Cities =
    [
        new("Honolulu", 21.31, -157.86, "Pacific/Honolulu"), new("Anchorage", 61.22, -149.90, "America/Anchorage"),
        new("Los Angeles", 34.05, -118.24, "America/Los_Angeles"), new("San Francisco", 37.77, -122.42, "America/Los_Angeles"),
        new("Seattle", 47.61, -122.33, "America/Los_Angeles"), new("Vancouver", 49.28, -123.12, "America/Vancouver"),
        new("Phoenix", 33.45, -112.07, "America/Phoenix"), new("Denver", 39.74, -104.99, "America/Denver"),
        new("Chicago", 41.88, -87.63, "America/Chicago"), new("Dallas", 32.78, -96.80, "America/Chicago"),
        new("Houston", 29.76, -95.37, "America/Chicago"), new("Mexico City", 19.43, -99.13, "America/Mexico_City"),
        new("New York", 40.71, -74.01, "America/New_York"), new("Toronto", 43.65, -79.38, "America/Toronto"),
        new("Miami", 25.76, -80.19, "America/New_York"), new("Bogotá", 4.71, -74.07, "America/Bogota"),
        new("Lima", -12.05, -77.04, "America/Lima"), new("Santiago", -33.45, -70.67, "America/Santiago"),
        new("Buenos Aires", -34.60, -58.38, "America/Argentina/Buenos_Aires"), new("São Paulo", -23.55, -46.63, "America/Sao_Paulo"),
        new("Reykjavík", 64.15, -21.94, "Atlantic/Reykjavik"), new("Lisbon", 38.72, -9.14, "Europe/Lisbon"),
        new("London", 51.51, -0.13, "Europe/London"), new("Dublin", 53.35, -6.26, "Europe/Dublin"),
        new("Paris", 48.86, 2.35, "Europe/Paris"), new("Madrid", 40.42, -3.70, "Europe/Madrid"),
        new("Amsterdam", 52.37, 4.90, "Europe/Amsterdam"), new("Berlin", 52.52, 13.40, "Europe/Berlin"),
        new("Zurich", 47.38, 8.54, "Europe/Zurich"), new("Rome", 41.90, 12.50, "Europe/Rome"),
        new("Stockholm", 59.33, 18.07, "Europe/Stockholm"), new("Warsaw", 52.23, 21.01, "Europe/Warsaw"),
        new("Athens", 37.98, 23.73, "Europe/Athens"), new("Istanbul", 41.01, 28.98, "Europe/Istanbul"),
        new("Kyiv", 50.45, 30.52, "Europe/Kyiv"), new("Moscow", 55.76, 37.62, "Europe/Moscow"),
        new("Cairo", 30.04, 31.24, "Africa/Cairo"), new("Lagos", 6.52, 3.38, "Africa/Lagos"),
        new("Nairobi", -1.29, 36.82, "Africa/Nairobi"), new("Johannesburg", -26.20, 28.05, "Africa/Johannesburg"),
        new("Cape Town", -33.92, 18.42, "Africa/Johannesburg"), new("Tel Aviv", 32.09, 34.78, "Asia/Jerusalem"),
        new("Riyadh", 24.71, 46.68, "Asia/Riyadh"), new("Dubai", 25.20, 55.27, "Asia/Dubai"),
        new("Tehran", 35.69, 51.39, "Asia/Tehran"), new("Karachi", 24.86, 67.01, "Asia/Karachi"),
        new("Mumbai", 19.08, 72.88, "Asia/Kolkata"), new("Delhi", 28.61, 77.21, "Asia/Kolkata"),
        new("Bengaluru", 12.97, 77.59, "Asia/Kolkata"), new("Hyderabad", 17.39, 78.49, "Asia/Kolkata"),
        new("Chennai", 13.08, 80.27, "Asia/Kolkata"), new("Kathmandu", 27.72, 85.32, "Asia/Kathmandu"),
        new("Dhaka", 23.81, 90.41, "Asia/Dhaka"), new("Bangkok", 13.76, 100.50, "Asia/Bangkok"),
        new("Ho Chi Minh City", 10.82, 106.63, "Asia/Ho_Chi_Minh"), new("Jakarta", -6.21, 106.85, "Asia/Jakarta"),
        new("Singapore", 1.35, 103.82, "Asia/Singapore"), new("Kuala Lumpur", 3.14, 101.69, "Asia/Kuala_Lumpur"),
        new("Manila", 14.60, 120.98, "Asia/Manila"), new("Hong Kong", 22.32, 114.17, "Asia/Hong_Kong"),
        new("Shanghai", 31.23, 121.47, "Asia/Shanghai"), new("Beijing", 39.90, 116.41, "Asia/Shanghai"),
        new("Taipei", 25.03, 121.57, "Asia/Taipei"), new("Seoul", 37.57, 126.98, "Asia/Seoul"),
        new("Tokyo", 35.68, 139.69, "Asia/Tokyo"), new("Perth", -31.95, 115.86, "Australia/Perth"),
        new("Adelaide", -34.93, 138.60, "Australia/Adelaide"), new("Brisbane", -27.47, 153.03, "Australia/Brisbane"),
        new("Sydney", -33.87, 151.21, "Australia/Sydney"), new("Melbourne", -37.81, 144.96, "Australia/Melbourne"),
        new("Auckland", -36.85, 174.76, "Pacific/Auckland"),
    ];

    public static City? Find(string name) => Cities.FirstOrDefault(c => c.Name == name);

    static readonly Dictionary<string, TimeZoneInfo> zones = [];

    /// <summary>IANA id → zone. Windows resolves IANA ids through ICU; if that fails, fall back to UTC.</summary>
    public static TimeZoneInfo Zone(string iana)
    {
        if (zones.TryGetValue(iana, out var z)) return z;
        try { z = TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException)
        {
            z = TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win) ? TimeZoneInfo.FindSystemTimeZoneById(win) : TimeZoneInfo.Utc;
        }
        return zones[iana] = z;
    }

    static double Rad(double d) => d * Math.PI / 180;
    static double Deg(double r) => r * 180 / Math.PI;

    public static double MillerY(double lat) => 1.25 * Math.Log(Math.Tan(Math.PI / 4 + 0.4 * Rad(lat)));
    public static double MillerLat(double y) => Deg(2.5 * Math.Atan(Math.Exp(0.8 * y)) - 0.625 * Math.PI);


    /// <summary>"+10 hours 30 minutes" / "−2 hours" / "Same time" relative to this PC's clock.</summary>
    public static string Relative(TimeSpan diff)
    {
        if (diff == TimeSpan.Zero) return "Same time";
        var sign = diff < TimeSpan.Zero ? "−" : "+";
        diff = diff.Duration();
        var parts = new List<string>();
        if (diff.Hours > 0) parts.Add(diff.Hours == 1 ? "1 hour" : $"{diff.Hours} hours");
        if (diff.Minutes > 0) parts.Add($"{diff.Minutes} minutes");
        return sign + string.Join(" ", parts);
    }

    public static void SelfTest()
    {
        static void Near(double got, double want, double tol, string what)
        {
            if (Math.Abs(got - want) > tol) throw new Exception($"{what}: got {got:0.###}, want {want} ± {tol}");
        }
        foreach (var lat in new[] { -60.0, -12.5, 0, 33.3, 71 }) Near(MillerLat(MillerY(lat)), lat, 1e-9, "Miller round trip");
        Near(MillerY(60) * 404.9, 979 - 494, 3, "Miller fits the wallpaper's 60° line");
        if (Relative(new TimeSpan(10, 30, 0)) != "+10 hours 30 minutes") throw new Exception("relative +10:30");
        if (Relative(new TimeSpan(-1, 0, 0)) != "−1 hour") throw new Exception("relative -1");
    }
}

/// <summary>
/// A layer over the wallpaper, under every widget: one small amber dot per chosen time zone.
/// Hovering a dot shows its local time, city, date and offset from this PC. Everything but the
/// dots is fully transparent, so clicks there fall through to the desktop. It only draws when
/// the current wallpaper is the one the map was calibrated against.
/// </summary>
sealed class WorldMap : Window
{
    readonly Canvas layer = new();
    Size image;
    double ox, oy, sx, sy;   // wallpaper image px → screen DIPs: screen = o + image * s
    FrameworkElement? card;

    static MapState M => Hub.S.Map;

    /// <summary>Why the map is or isn't showing, for the menu.</summary>
    public string Status { get; private set; } = "";

    public WorldMap()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Dotline map";
        Content = layer;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        SourceInitialized += (_, _) => Native.PinToDesktop(this, noActivate: true);
        Loaded += (_, _) => Redraw();
    }

    Point ImageToScreen(double x, double y) => new(ox + x * sx, oy + y * sy);

    /// <summary>A place on screen; the map repeats past 180°, so keep the point on the drawn part.</summary>
    public Point GeoToScreen(double lat, double lon)
    {
        var x = M.X0 + M.K * lon * Math.PI / 180;
        var span = 2 * Math.PI * M.K;
        if (x < 0) x += span;
        if (x > image.Width) x -= span;
        return ImageToScreen(x, M.Y0 - M.K * Geo.MillerY(Math.Clamp(lat, -85, 85)));
    }

    /// <summary>Wallpaper → screen for the primary monitor, following the Fill/Fit/Stretch/Center setting.</summary>
    bool Place()
    {
        string path;
        int style;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
        {
            path = k?.GetValue("WallPaper") as string ?? "";
            int.TryParse(k?.GetValue("WallpaperStyle") as string, out style);
        }
        if (!string.Equals(System.IO.Path.GetFileName(path), M.CalibratedFor, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            Status = "wallpaper not calibrated";
            return false;
        }
        if (image == default)
        {
            var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            image = new Size(frame.PixelWidth, frame.PixelHeight);
        }

        double sw = ActualWidth, sh = ActualHeight;
        sx = sw / image.Width;
        sy = sh / image.Height;
        switch (style)
        {
            case 6: sx = sy = Math.Min(sx, sy); break;                                   // Fit
            case 2: break;                                                               // Stretch
            case 0: sx = sy = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX; break;        // Center: 1 image px per physical px
            default: sx = sy = Math.Max(sx, sy); break;                                  // Fill (10), Span (22)
        }
        ox = (sw - image.Width * sx) / 2;
        oy = (sh - image.Height * sy) / 2;
        Status = M.Zones.Count == 0 ? "pick zones" : "live";
        return true;
    }

    public void Redraw()
    {
        if (card is not null) return;   // keep the hover card steady; the next tick catches up
        layer.Children.Clear();
        card = null;
        if (!M.On) { Status = "off"; return; }
        if (ActualWidth <= 0 || !Place()) return;
        foreach (var c in M.Zones.Select(Geo.Find).OfType<City>().Take(5)) Dot(c);
    }

    /// <summary>A small glowing amber dot; the transparent-looking halo around it is the hover target.</summary>
    void Dot(City c)
    {
        var p = GeoToScreen(c.Lat, c.Lon);
        var dot = new Grid { Width = 22, Height = 22, Background = Theme.Hit, Cursor = System.Windows.Input.Cursors.Hand };
        dot.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Fill = Theme.Accent,
            Effect = new DropShadowEffect { Color = Theme.AccentColor, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
        });
        var ring = new Ellipse { Width = 16, Height = 16, Stroke = Theme.Accent, StrokeThickness = 1, Opacity = 0 };
        dot.Children.Add(ring);
        dot.MouseEnter += (_, _) => { ring.Opacity = 1; ShowCard(c, p); };
        dot.MouseLeave += (_, _) => { ring.Opacity = 0; HideCard(); };
        Put(dot, p.X - 11, p.Y - 11);
    }

    /// <summary>The hover card, laid out like the Windows clock flyout: day/night icon, time, city, date and offset.</summary>
    void ShowCard(City c, Point p)
    {
        HideCard();
        var utc = DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, c.Zone);
        var diff = c.Zone.GetUtcOffset(utc) - TimeZoneInfo.Local.GetUtcOffset(utc);
        var en = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var day = local.Hour is >= 6 and < 18;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 10, 22, 10) };
        row.Children.Add(new TextBlock
        {
            Text = day ? "" : "",   // Segoe MDL2: sun (Brightness) / moon (QuietHours)
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            Foreground = day ? Theme.Accent : Theme.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 22, 0),
        });
        var time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0) };
        time.Inlines.Add(new System.Windows.Documents.Run(local.ToString("h:mm", en)) { FontFamily = Theme.Font, FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Theme.B(0xFFEDEDED) });
        time.Inlines.Add(new System.Windows.Documents.Run(" " + local.ToString("tt", en)) { FontFamily = Theme.Font, FontSize = 18, Foreground = Theme.Dim });
        row.Children.Add(time);
        row.Children.Add(new TextBlock
        {
            Text = c.Name.ToUpperInvariant(), FontFamily = Theme.Font, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 40, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{local.ToString("d", en)},  {Geo.Relative(diff)}", FontFamily = Theme.Font, FontSize = 17,
            Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center,
        });
        ShowCard(row, p);
    }

    /// <summary>Puts a card (dark plate, amber edge) above a point, nudged to stay on screen.</summary>
    public void ShowCard(UIElement row, Point p)
    {
        HideCard();
        var box = new Grid { IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black } };
        box.Children.Add(new ChamferPlate { Fill = Theme.Plate(2), Stroke = Theme.Line, CutTopLeft = 10, CutBottomRight = 10 });
        box.Children.Add(new Border { Height = 2, Width = 90, Background = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 0) });
        box.Children.Add(row);
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = box.DesiredSize;

        var x = Math.Clamp(p.X - size.Width / 2, 8, ActualWidth - size.Width - 8);
        var y = p.Y - size.Height - 16;
        if (y < 8) y = p.Y + 16;
        Put(box, x, y);
        card = box;
    }


    public void HideCard()
    {
        if (card is not null) layer.Children.Remove(card);
        card = null;
    }

    void Put(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        layer.Children.Add(e);
    }
}
