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

    /// <summary>Where you end up going <paramref name="meters"/> along a great circle from a point on a compass bearing.</summary>
    /// <summary>Great-circle distance in km.</summary>
    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        double dp = Rad(lat2 - lat1), dl = Rad(lon2 - lon1);
        var h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * 6371 * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>Points along the great circle from a to b (the path planes actually fly), n+1 of them.</summary>
    public static List<(double Lat, double Lon)> GreatCircle(double lat1, double lon1, double lat2, double lon2, int n)
    {
        static (double, double, double) Xyz(double lat, double lon) =>
            (Math.Cos(Rad(lat)) * Math.Cos(Rad(lon)), Math.Cos(Rad(lat)) * Math.Sin(Rad(lon)), Math.Sin(Rad(lat)));
        var (x1, y1, z1) = Xyz(lat1, lon1);
        var (x2, y2, z2) = Xyz(lat2, lon2);
        var d = Math.Acos(Math.Clamp(x1 * x2 + y1 * y2 + z1 * z2, -1, 1));
        if (d < 1e-9) return [(lat1, lon1), (lat2, lon2)];
        var pts = new List<(double, double)>(n + 1);
        for (var i = 0; i <= n; i++)
        {
            double f = (double)i / n, a = Math.Sin((1 - f) * d) / Math.Sin(d), b = Math.Sin(f * d) / Math.Sin(d);
            double x = a * x1 + b * x2, y = a * y1 + b * y2, z = a * z1 + b * z2;
            pts.Add((Deg(Math.Atan2(z, Math.Sqrt(x * x + y * y))), Deg(Math.Atan2(y, x))));
        }
        return pts;
    }

    public static (double Lat, double Lon) Move(double lat, double lon, double bearing, double meters)
    {
        var d = meters / 6_371_000;
        double p1 = Rad(lat), l1 = Rad(lon), b = Rad(bearing);
        var p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(b));
        var l2 = l1 + Math.Atan2(Math.Sin(b) * Math.Sin(d) * Math.Cos(p1), Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        return (Deg(p2), (Deg(l2) + 540) % 360 - 180);
    }

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
        Near(Move(0, 0, 0, 111_195).Lat, 1, 0.001, "one degree north");
        Near(Km(40.64, -73.78, 51.47, -0.46), 5540, 30, "JFK to LHR distance");
        var gc = GreatCircle(40.64, -73.78, 51.47, -0.46, 10);
        Near(gc[0].Lat, 40.64, 1e-9, "great circle start");
        Near(gc[^1].Lon, -0.46, 1e-9, "great circle end");
        if (gc.Max(p => p.Lat) < 52) throw new Exception("JFK-LHR great circle should bulge north of both ends");
        Near(Move(0, 179.5, 90, 111_195).Lon, -179.5, 0.001, "east across the date line");
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
        if (!M.On) { Status = "off"; flightLayer?.Clear(); return; }
        if (ActualWidth <= 0 || !Place()) { flightLayer?.Clear(); return; }
        foreach (var c in M.Zones.Select(Geo.Find).OfType<City>().Take(5)) Dot(c);
        DrawFlight();
    }

    /// <summary>A small glowing amber dot; the transparent-looking halo around it is the hover target.</summary>
    void Dot(City c)
    {
        var p = GeoToScreen(c.Lat, c.Lon);
        var dot = new Grid { Width = 22, Height = 22, Background = Theme.Hit, Cursor = System.Windows.Input.Cursors.Hand };
        dot.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Fill = Theme.Accent,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xF0, 0xA0, 0x30), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
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
        box.Children.Add(new ChamferPlate { Fill = Theme.B(0xF02A2C2F), Stroke = Theme.Line, CutTopLeft = 10, CutBottomRight = 10 });
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

    // ---- flight ------------------------------------------------------------------------

    public static readonly Geometry PlaneShape = Frozen(Geometry.Parse(
        "M0,-11 L1.6,-4 L10,1.5 L10,3.5 L1.6,1.5 L1.2,7 L4.5,9.5 L4.5,11 L0,10 L-4.5,11 L-4.5,9.5 L-1.2,7 L-1.6,1.5 L-10,3.5 L-10,1.5 L-1.6,-4 Z"));

    static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

    FlightLayer? flightLayer;

    /// <summary>
    /// The tracked flight. The plane, its route and airports live in a small window of their own
    /// (FlightLayer), so their animation repaints a few hundred pixels, not this screen-sized layer.
    /// Until there's a position, a status tag here says why; the route still shows if it's known.
    /// </summary>
    void DrawFlight()
    {
        if (FlightTracker.Flight is not { } flight) { flightLayer?.Clear(); return; }
        var fix = FlightTracker.Last;
        var route = FlightTracker.Route;

        if (fix is null)
        {
            var msg = FlightTracker.Status switch
            {
                "not found" => "NOT AIRBORNE YET, OR NOT BROADCASTING",
                "offline" => "CAN'T REACH THE FLIGHT DATA SERVICE — RETRYING",
                _ => "SEARCHING…",
            };
            if (route is not null) msg = $"{route.From.Code} → {route.To.Code}  ·  {msg}";
            var tag = FlightTag($"FLIGHT {flight}", msg, warn: FlightTracker.Status is "not found" or "offline");
            tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Put(tag, (ActualWidth - tag.DesiredSize.Width) / 2, 48);
            if (route is null) { flightLayer?.Clear(); return; }
        }
        (flightLayer ??= new FlightLayer(this)).Draw(flight, fix, route);
    }

    /// <summary>A small tag: bold title, a detail line, and ✕ to stop tracking.</summary>
    public static Grid FlightTag(string title, string detail, bool warn)
    {
        var text = new TextBlock { FontFamily = Theme.Font, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 3, 6, 3) };
        text.Inlines.Add(new System.Windows.Documents.Run("  ") { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = Theme.Accent });
        text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.Bold, Foreground = Theme.Text });
        if (detail.Length > 0) text.Inlines.Add(new System.Windows.Documents.Run("   " + detail) { Foreground = warn ? Theme.Warn : Theme.Accent });

        var stop = Theme.Chip("✕", false, FlightTracker.Stop);
        stop.ToolTip = "Stop tracking this flight";
        stop.Margin = new Thickness(0, 3, 4, 3);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(stop);

        var tag = new Grid { Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black } };
        tag.Children.Add(new ChamferPlate { Fill = Theme.B(0xE01A1B1E), Stroke = Theme.Line, CutTopLeft = 6, CutBottomRight = 6 });
        tag.Children.Add(row);
        return tag;
    }

    /// <summary>Hover card: route first (from → to), then the live numbers.</summary>
    public static StackPanel FlightCard(string flight, FlightTracker.Fix? f, FlightTracker.Leg? route, (double Lat, double Lon)? now)
    {
        var col = new StackPanel { Margin = new Thickness(18, 10, 24, 12) };
        TextBlock Line(string text, double size, Brush fg) =>
            new() { Text = text, FontFamily = Theme.Font, FontSize = size, Foreground = fg };

        var head = new TextBlock { FontFamily = Theme.Font };
        head.Inlines.Add(new System.Windows.Documents.Run(flight) { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.B(0xFFEDEDED) });
        var who = f is null ? route?.Airline ?? "" : string.Join("  ·  ", new[] { route?.Airline ?? "", f.Type, f.Reg }.Where(s => s.Length > 0));
        head.Inlines.Add(new System.Windows.Documents.Run("   " + who.ToUpperInvariant()) { FontSize = 15, Foreground = Theme.Accent });
        col.Children.Add(head);

        if (route is not null)
        {
            var leg = new TextBlock { FontFamily = Theme.Font, Margin = new Thickness(0, 2, 0, 2) };
            leg.Inlines.Add(new System.Windows.Documents.Run(route.From.Code) { FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Theme.Accent });
            leg.Inlines.Add(new System.Windows.Documents.Run("    ") { FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Theme.Dim });
            leg.Inlines.Add(new System.Windows.Documents.Run(route.To.Code) { FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Theme.Accent });
            col.Children.Add(leg);
            col.Children.Add(Line($"FROM  {route.From.City.ToUpperInvariant()} · {route.From.Name.ToUpperInvariant()}", 14, Theme.Text));
            col.Children.Add(Line($"TO      {route.To.City.ToUpperInvariant()} · {route.To.Name.ToUpperInvariant()}", 14, Theme.Text));
            if (now is { } p)
            {
                var flown = Geo.Km(route.From.Lat, route.From.Lon, p.Lat, p.Lon);
                var left = Geo.Km(p.Lat, p.Lon, route.To.Lat, route.To.Lon);
                var pct = flown / Math.Max(1, flown + left) * 100;
                col.Children.Add(Line($"{flown:N0} KM FLOWN   ·   {left:N0} KM TO GO   ·   {pct:0}%", 14, Theme.Accent));
            }
        }

        if (f is null)
        {
            col.Children.Add(Line("NOT AIRBORNE YET, OR NOT BROADCASTING — CHECKING EVERY 15 S", 14, Theme.Warn));
            return col;
        }
        var knots = f.Knots is double k ? $"{k:0} KN  ·  {k * 1.852:0} KM/H" : "SPEED —";
        var alt = f.OnGround ? "ON THE GROUND" : f.AltFt is int a ? $"{a:N0} FT  ·  {a * 0.3048:N0} M" : "ALTITUDE —";
        col.Children.Add(Line($"{alt}   ·   {knots}", 15, Theme.Dim));
        col.Children.Add(Line($"HEADING {f.Track ?? 0:000}°   ·   {Math.Abs(f.Lat):0.00}°{(f.Lat >= 0 ? "N" : "S")} {Math.Abs(f.Lon):0.00}°{(f.Lon >= 0 ? "E" : "W")}   ·   {f.Callsign}", 15, Theme.Dim));
        col.Children.Add(Line($"UPDATED {Ago(f.Utc)}   ·   LIVE: {f.Source.ToUpperInvariant()}{(route is null ? "" : "   ·   ROUTE: ADSBDB.COM")}", 13, Theme.Faint));
        return col;
    }

    public static string Ago(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        return s < 60 ? $"{s:0} S AGO" : s < 3600 ? $"{s / 60:0} MIN AGO" : $"{s / 3600:0.#} H AGO";
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


/// <summary>
/// The tracked flight in a window just big enough for it. With a known route: marching dots from
/// the departure airport to the plane (bright) and on to the destination (faint), both airports
/// marked. Without one: the flown trail behind and a 3 h projection of its heading ahead. The
/// plane glides along its dead-reckoned position with a radar pulse; hovering shows the details.
/// Sized to the flight, not the screen, because every animation frame repaints the whole window.
/// </summary>
sealed class FlightLayer : Window
{
    readonly WorldMap map;
    readonly Canvas canvas = new();
    readonly System.Windows.Threading.DispatcherTimer fly = new() { Interval = TimeSpan.FromMilliseconds(250) };
    FrameworkElement? planeEl, labelEl;
    RotateTransform? planeTurn;
    System.Windows.Shapes.Path? behind, ahead;
    (double Lat, double Lon)? shown;
    string flight = "";
    FlightTracker.Fix? fix;
    FlightTracker.Leg? route;

    static bool Anim => SystemParameters.ClientAreaAnimation;

    public FlightLayer(WorldMap map)
    {
        this.map = map;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "Dotline flight";
        Content = canvas;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Native.PinToDesktop(this, noActivate: true);
        // Four frames a second is plenty for position: at world scale an airliner covers about a pixel a minute.
        fly.Tick += (_, _) => FlyTick();
    }

    public void Clear()
    {
        fly.Stop();
        shown = null;
        canvas.Children.Clear();
        planeEl = labelEl = null;
        Hide();
    }

    Point Local(Point screen) => new(screen.X - Left, screen.Y - Top);

    /// <summary>Screen polylines for a geographic path, split where it crosses the map's edge.</summary>
    List<List<Point>> Screen(IEnumerable<(double Lat, double Lon)> path)
    {
        var parts = new List<List<Point>>();
        foreach (var (lat, lon) in path)
        {
            var q = map.GeoToScreen(lat, lon);
            if (parts.Count == 0 || Math.Abs(parts[^1][^1].X - q.X) > map.ActualWidth / 3) parts.Add([]);
            parts[^1].Add(q);
        }
        return parts;
    }

    static List<(double, double)> Arc((double Lat, double Lon) a, (double Lat, double Lon) b) =>
        Geo.GreatCircle(a.Lat, a.Lon, b.Lat, b.Lon, Math.Clamp((int)(Geo.Km(a.Lat, a.Lon, b.Lat, b.Lon) / 60), 2, 200));

    /// <summary>The two dotted lines for a plane at <paramref name="at"/> (or no plane yet).</summary>
    (List<List<Point>> Behind, List<List<Point>> Ahead) Lines((double Lat, double Lon)? at)
    {
        if (route is not null)
        {
            (double, double) from = (route.From.Lat, route.From.Lon), to = (route.To.Lat, route.To.Lon);
            return at is { } p ? (Screen(Arc(from, p)), Screen(Arc(p, to))) : ([], Screen(Arc(from, to)));
        }
        if (at is not { } q || fix is null) return ([], []);
        var trail = Screen(FlightTracker.Trail.Append(q));
        var course = new List<(double, double)> { q };
        if (!fix.OnGround && fix.Knots is double k && k > 30 && fix.Track is double t)
            for (var min = 5; min <= 180; min += 5) course.Add(Geo.Move(q.Lat, q.Lon, t, k * 1852 / 60 * min));
        return (trail, Screen(course));
    }

    Geometry Geometry(List<List<Point>> parts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
            foreach (var part in parts.Where(p => p.Count > 1))
            {
                ctx.BeginFigure(Local(part[0]), false, false);
                ctx.PolyLineTo(part.Skip(1).Select(Local).ToList(), true, true);
            }
        g.Freeze();
        return g;
    }

    public void Draw(string flight, FlightTracker.Fix? fix, FlightTracker.Leg? route)
    {
        this.flight = flight;
        this.fix = fix;
        this.route = route;
        canvas.Children.Clear();
        planeEl = labelEl = null;
        if (fix is null) shown = null;
        var at = fix is null ? ((double, double)?)null : shown ?? FlightTracker.Now() ?? (fix.Lat, fix.Lon);

        // Everything in screen coordinates first, then fit the window around it.
        var (back, front) = Lines(at);
        var airports = route is null ? [] : new[] { route.From, route.To };
        FrameworkElement? label = null;
        var box = Rect.Empty;
        foreach (var q in back.Concat(front).SelectMany(p => p)) box.Union(q);
        foreach (var ap in airports) box.Union(new Rect(map.GeoToScreen(ap.Lat, ap.Lon), new Size(1, 1)));
        if (at is { } a0)
        {
            var p = map.GeoToScreen(a0.Item1, a0.Item2);
            var alt = fix!.OnGround ? "ON GROUND" : fix.AltFt is int ft ? $"{ft:N0} FT" : "";
            var detail = FlightTracker.Status == "signal lost" ? $"SIGNAL LOST · {WorldMap.Ago(fix.Utc)}"
                : route is null ? alt : $"{route.From.Code}→{route.To.Code}  ·  {alt}";
            label = WorldMap.FlightTag(flight, detail, warn: FlightTracker.Status == "signal lost");
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var lw = label.DesiredSize.Width;
            box.Union(new Rect(p.X - 60 - lw, p.Y - 40, 120 + 2 * lw, 80));
        }
        if (box.IsEmpty) { Clear(); return; }
        box.Inflate(70, 40);   // airport tags and the pulse
        box.Intersect(new Rect(0, 0, map.ActualWidth, map.ActualHeight));
        if (box.IsEmpty) { Clear(); return; }
        Left = box.X;
        Top = box.Y;
        Width = box.Width;
        Height = box.Height;

        behind = Dots(0.8);
        ahead = Dots(0.4);
        behind.Data = Geometry(back);
        ahead.Data = Geometry(front);
        canvas.Children.Add(behind);
        canvas.Children.Add(ahead);
        // At world scale a short route puts both airports on top of the plane: tag only those with room.
        var planeAt = at is { } pa ? map.GeoToScreen(pa.Item1, pa.Item2) : (Point?)null;
        foreach (var ap in airports)
        {
            var other = ap == route!.From ? route.To : route.From;
            var q = map.GeoToScreen(ap.Lat, ap.Lon);
            var roomy = (q - map.GeoToScreen(other.Lat, other.Lon)).Length > 60 && (planeAt is not { } pp || (q - pp).Length > 45);
            Airport(ap, departure: ap == route.From, tagged: roomy);
        }

        if (at is not null)
        {
            var plane = new Grid { Width = 30, Height = 30, Background = Theme.Hit, Cursor = System.Windows.Input.Cursors.Hand };
            if (Anim)
            {
                var pulse = new Ellipse
                {
                    Width = 30, Height = 30, Stroke = Theme.Accent, StrokeThickness = 1, IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
                };
                var grow = Theme.Loop(0.3, 1.6, 2, fps: 12);
                var fade = Theme.Loop(0.9, 0, 2, fps: 12);
                pulse.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                pulse.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                pulse.BeginAnimation(OpacityProperty, fade);
                plane.Children.Add(pulse);
            }
            planeTurn = new RotateTransform(fix!.Track ?? 0);
            plane.Children.Add(new System.Windows.Shapes.Path
            {
                Data = WorldMap.PlaneShape, Fill = Theme.Accent, Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = planeTurn,
                Effect = new DropShadowEffect { Color = Color.FromRgb(0xF0, 0xA0, 0x30), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 },
            });
            plane.MouseEnter += (_, _) => map.ShowCard(WorldMap.FlightCard(flight, this.fix, this.route, shown),
                new Point(Left + Canvas.GetLeft(plane) + 15, Top + Canvas.GetTop(plane) + 15));
            plane.MouseLeave += (_, _) => map.HideCard();
            canvas.Children.Add(plane);
            planeEl = plane;
            canvas.Children.Add(label!);
            labelEl = label;
        }

        if (!IsVisible) Show();
        if (planeEl is null) { fly.Stop(); return; }
        FlyTick();
        fly.Start();
    }

    /// <summary>An airport: ring, code tag (bright for departure), hover for its full name.</summary>
    void Airport(FlightTracker.Airport ap, bool departure, bool tagged)
    {
        var p = Local(map.GeoToScreen(ap.Lat, ap.Lon));
        var ring = new Grid { Width = 16, Height = 16, Background = Theme.Hit, Cursor = System.Windows.Input.Cursors.Hand };
        ring.Children.Add(new Ellipse { Stroke = Theme.Accent, StrokeThickness = 1.5 });
        ring.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = departure ? Theme.Accent : Theme.Now });
        Canvas.SetLeft(ring, p.X - 8);
        Canvas.SetTop(ring, p.Y - 8);
        canvas.Children.Add(ring);

        var tag = new TextBlock { FontFamily = Theme.Font, FontSize = 13, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
        tag.Inlines.Add(new System.Windows.Documents.Run((departure ? "FROM " : "TO ") ) { Foreground = Theme.Faint, FontWeight = FontWeights.Normal });
        tag.Inlines.Add(new System.Windows.Documents.Run(ap.Code) { Foreground = departure ? Theme.Accent : Theme.Text });
        tag.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1, Color = Colors.Black };
        Canvas.SetLeft(tag, p.X - 20);
        Canvas.SetTop(tag, p.Y + 9);
        if (tagged) canvas.Children.Add(tag);

        var card = new StackPanel { Margin = new Thickness(16, 8, 20, 10) };
        card.Children.Add(new TextBlock { Text = $"{(departure ? "DEPARTED" : "DESTINATION")}  ·  {ap.Code}", FontFamily = Theme.Font, FontSize = 14, Foreground = Theme.Accent });
        card.Children.Add(new TextBlock { Text = ap.City.ToUpperInvariant(), FontFamily = Theme.Font, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Theme.B(0xFFEDEDED) });
        card.Children.Add(new TextBlock { Text = ap.Name, FontFamily = Theme.Font, FontSize = 14, Foreground = Theme.Dim });
        ring.MouseEnter += (_, _) => map.ShowCard(card, new Point(Left + p.X, Top + p.Y));
        ring.MouseLeave += (_, _) => map.HideCard();
    }

    /// <summary>A dotted amber line whose dots march forward along it.</summary>
    static System.Windows.Shapes.Path Dots(double opacity)
    {
        var line = new System.Windows.Shapes.Path
        {
            Stroke = Theme.Accent, StrokeThickness = 2, StrokeDashArray = [0, 3], StrokeDashCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Opacity = opacity, IsHitTestVisible = false,
        };
        if (Anim) line.BeginAnimation(Shape.StrokeDashOffsetProperty, Theme.Loop(0, -6, 1.2, fps: 12));
        return line;
    }

    /// <summary>Move the plane to where it is now, easing toward each new fix instead of jumping.</summary>
    void FlyTick()
    {
        if (planeEl is null || labelEl is null || fix is null || FlightTracker.Now() is not { } now) return;
        if (FlightTracker.Last is { } latest) fix = latest;

        // Glide toward the dead-reckoned position; snap if it's far (first fix, or a big correction).
        if (shown is not { } s || Math.Abs(s.Lat - now.Lat) > 1 || Math.Abs(s.Lon - now.Lon) > 1) shown = now;
        else shown = (s.Lat + (now.Lat - s.Lat) * 0.35, s.Lon + (now.Lon - s.Lon) * 0.35);

        var p = Local(map.GeoToScreen(shown.Value.Lat, shown.Value.Lon));
        if (p.X < 20 || p.Y < 20 || p.X > Width - 20 || p.Y > Height - 20) { Draw(flight, fix, route); return; }   // left its box

        Canvas.SetLeft(planeEl, p.X - planeEl.Width / 2);
        Canvas.SetTop(planeEl, p.Y - planeEl.Height / 2);
        if (planeTurn is not null) planeTurn.Angle = fix.Track ?? 0;

        // Label behind the plane, so it never covers the line ahead; flip if that runs off screen.
        var w = labelEl.DesiredSize.Width;
        var eastbound = fix.Track is double tr && tr is > 0 and < 180;
        var left = eastbound ? Left + p.X - 20 - w >= 8 : Left + p.X + 20 + w > map.ActualWidth - 8;
        Canvas.SetLeft(labelEl, left ? p.X - 20 - w : p.X + 20);
        Canvas.SetTop(labelEl, p.Y - labelEl.DesiredSize.Height / 2);

        var (back, front) = Lines(shown);
        if (behind is not null) behind.Data = Geometry(back);
        if (ahead is not null) ahead.Data = Geometry(front);
    }
}
