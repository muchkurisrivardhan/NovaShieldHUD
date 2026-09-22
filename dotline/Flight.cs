using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Dotline;

/// <summary>
/// Follows the flight Beacon asked for (%APPDATA%\Vantage\flight.json) using the free,
/// keyless ADS-B aggregators adsb.lol (adsb.fi as fallback). Polls every 15 s only while a
/// flight is being tracked; nothing goes online otherwise. The request and the flown trail
/// both survive restarts.
/// </summary>
static class FlightTracker
{
    public sealed record Fix(
        double Lat, double Lon, int? AltFt, bool OnGround, double? Knots, double? Track,
        string Callsign, string Type, string Reg, DateTime Utc, string Source);

    sealed record Request(string Flight, string Callsign);

    static readonly string FlightFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vantage", "flight.json");

    // The flown path, so a restart mid-flight keeps the dotted line behind the plane.
    static readonly string TrailFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vantage", "flight-trail.json");

    sealed record SavedTrail(string Flight, List<double[]> Points);

    static readonly string[] Hosts = ["https://api.adsb.lol", "https://opendata.adsb.fi/api"];
    static readonly TimeSpan Every = TimeSpan.FromSeconds(15);
    static readonly HttpClient Http = MakeClient();

    static DateTime stamp, nextPoll;
    static bool busy;
    static Request? req;

    public static string? Flight => req?.Flight;
    public static Fix? Last { get; private set; }

    public sealed record Airport(string Code, string Name, string City, double Lat, double Lon);
    public sealed record Leg(Airport From, Airport To, string Airline);

    /// <summary>Departure and destination from adsbdb.com (looked up once per flight), or null if unknown.</summary>
    public static Leg? Route { get; private set; }
    static bool routeTried;
    public static List<(double Lat, double Lon)> Trail { get; } = [];

    /// <summary>"searching", "live", "not found", "signal lost", "offline".</summary>
    public static string Status { get; private set; } = "";

    static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Dotline/1.0 (desktop flight widget)");
        return h;
    }

    /// <summary>Called every second by the Hub.</summary>
    public static void Tick()
    {
        var s = File.Exists(FlightFile) ? File.GetLastWriteTimeUtc(FlightFile) : default;
        if (s != stamp)
        {
            stamp = s;
            req = Read();
            Last = null;
            Route = null;
            routeTried = false;
            LoadTrail();
            Status = req is null ? "" : "searching";
            nextPoll = DateTime.UtcNow;
            Hub.Map.Redraw();
        }
        if (req is not null && !busy && DateTime.UtcNow >= nextPoll)
        {
            busy = true;
            nextPoll = DateTime.UtcNow + Every;
            _ = Poll(req);
        }
    }

    static bool Valid(string s) => System.Text.RegularExpressions.Regex.IsMatch(s ?? "", "^[A-Z0-9]{2,10}$");

    static Request? Read()
    {
        try
        {
            var r = JsonSerializer.Deserialize<Request>(File.ReadAllText(FlightFile));
            // Any app running as this user can write this file: accept only a plain flight number/callsign.
            return r is not null && Valid(r.Flight) && (r.Callsign.Length == 0 || Valid(r.Callsign)) ? r : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static void LoadTrail()
    {
        Trail.Clear();
        try
        {
            if (req is null || !File.Exists(TrailFile)) return;
            var saved = JsonSerializer.Deserialize<SavedTrail>(File.ReadAllText(TrailFile));
            if (saved?.Flight == req.Flight) Trail.AddRange(saved.Points.Where(p => p.Length == 2).Select(p => (p[0], p[1])));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }

    static void SaveTrail()
    {
        if (req is null) return;
        try
        {
            var tmp = TrailFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new SavedTrail(req.Flight, Trail.Select(p => new[] { p.Lat, p.Lon }).ToList())));
            File.Move(tmp, TrailFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the trail just restarts next time
    }

    /// <summary>
    /// Where the plane is now, dead-reckoned from its last fix along its heading at its ground
    /// speed. Capped at 90 s past the fix, so a stale report doesn't fly off on its own.
    /// </summary>
    public static (double Lat, double Lon)? Now()
    {
        if (Last is not { } f) return null;
        if (f.OnGround || f.Knots is not double k || f.Track is not double t) return (f.Lat, f.Lon);
        var seconds = Math.Clamp((DateTime.UtcNow - f.Utc).TotalSeconds, 0, 90);
        return Geo.Move(f.Lat, f.Lon, t, k * 1852 / 3600 * seconds);
    }

    public static void Stop()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlightFile)!);
            File.WriteAllText(FlightFile, JsonSerializer.Serialize(new Request("", "")));
        }
        catch (IOException) { }
        Tick();   // picks up the change and clears the map
    }

    /// <summary>Callsign first (AAL100), then the flight number as typed; each host in turn if one is down.</summary>
    static async Task Poll(Request r)
    {
        var online = false;
        Fix? fix = null;
        try
        {
            if (!routeTried)
            {
                routeTried = true;
                try
                {
                    using var resp = await Http.GetAsync($"https://api.adsbdb.com/v0/callsign/{Uri.EscapeDataString(r.Callsign)}");
                    // 404 = no known route for this callsign (final); other failures retry next poll.
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                        Route = ParseRoute(await resp.EnsureSuccessStatusCode().Content.ReadAsStringAsync());
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { routeTried = false; }
            }
            foreach (var cs in new[] { r.Callsign, r.Flight }.Where(c => c.Length > 0).Distinct())
            {
                foreach (var host in Hosts)
                {
                    string json;
                    try { json = await Http.GetStringAsync($"{host}/v2/callsign/{Uri.EscapeDataString(cs)}"); }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { continue; }
                    online = true;
                    fix = Parse(json, new Uri(host).Host);
                    break;   // this host answered; a miss here means the flight isn't up, not that the host is down
                }
                if (fix is not null) break;
            }
        }
        finally { busy = false; }

        if (req != r) return;   // the request changed while we were waiting
        if (fix is not null)
        {
            Last = fix;
            if (Trail.Count == 0 || Trail[^1] != (fix.Lat, fix.Lon)) Trail.Add((fix.Lat, fix.Lon));
            if (Trail.Count > 2000) Trail.RemoveAt(0);   // ~8 h at one point per 15 s
            SaveTrail();
            Status = "live";
        }
        else Status = !online ? "offline" : Last is null ? "not found" : "signal lost";
        Hub.Map.Redraw();
    }

    /// <summary>The freshest aircraft in an adsb.lol / adsb.fi v2 response, or null.</summary>
    public static Fix? Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("ac", out var list)) return null;
        JsonElement? best = null;
        foreach (var a in list.EnumerateArray())
        {
            if (!a.TryGetProperty("lat", out _) || !a.TryGetProperty("lon", out _)) continue;
            if (best is null || Num(a, "seen_pos") < Num(best.Value, "seen_pos")) best = a;
        }
        if (best is not JsonElement b) return null;

        // alt_baro is a number of feet, or the string "ground".
        int? alt = null;
        var ground = false;
        if (b.TryGetProperty("alt_baro", out var ab))
        {
            if (ab.ValueKind == JsonValueKind.Number) alt = ab.GetInt32();
            else ground = ab.GetString() == "ground";
        }
        var seen = Num(b, "seen_pos") ?? 0;
        return new Fix(
            b.GetProperty("lat").GetDouble(), b.GetProperty("lon").GetDouble(), alt, ground,
            Num(b, "gs"), Num(b, "track"), Str(b, "flight"), Str(b, "t"), Str(b, "r"),
            DateTime.UtcNow.AddSeconds(-seen), source);
    }

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>adsbdb.com /v0/callsign response → route, or null when it has none.</summary>
    public static Leg? ParseRoute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var resp) || resp.ValueKind != JsonValueKind.Object
            || !resp.TryGetProperty("flightroute", out var fr)) return null;
        static Airport Ap(JsonElement a) => new(
            Str(a, "iata_code") is { Length: > 0 } iata ? iata : Str(a, "icao_code"), Str(a, "name"), Str(a, "municipality"),
            a.GetProperty("latitude").GetDouble(), a.GetProperty("longitude").GetDouble());
        var airline = fr.TryGetProperty("airline", out var al) && al.ValueKind == JsonValueKind.Object ? Str(al, "name") : "";
        return new Leg(Ap(fr.GetProperty("origin")), Ap(fr.GetProperty("destination")), airline);
    }

    public static void SelfTest()
    {
        var leg = ParseRoute("""{"response":{"flightroute":{"airline":{"name":"American Airlines"},"origin":{"iata_code":"JFK","name":"John F Kennedy International Airport","municipality":"New York","latitude":40.6398,"longitude":-73.7789},"destination":{"iata_code":"LHR","name":"London Heathrow Airport","municipality":"London","latitude":51.4706,"longitude":-0.4619}}}}""")
            ?? throw new Exception("route parse: none");
        if (leg.From.Code != "JFK" || leg.To.City != "London" || leg.Airline != "American Airlines") throw new Exception("route parse: fields");
        if (ParseRoute("""{"response":"unknown callsign"}""") is not null) throw new Exception("route parse: unknown");
        var f = Parse("""{"ac":[{"flight":"UAL2678 ","t":"B737","r":"N27734","alt_baro":6800,"gs":235.4,"track":266.6,"lat":41.86,"lon":-88.48,"seen_pos":0.2},{"flight":"UAL2678 ","alt_baro":"ground","lat":1,"lon":2,"seen_pos":30}]}""", "test")
            ?? throw new Exception("flight parse: no fix");
        if (f.Callsign != "UAL2678" || f.AltFt != 6800 || f.OnGround || f.Lat != 41.86) throw new Exception("flight parse: wrong fields");
        if (Parse("""{"ac":[{"alt_baro":"ground","lat":1,"lon":2}]}""", "t") is not { OnGround: true, AltFt: null }) throw new Exception("flight parse: ground");
        if (Parse("""{"ac":[],"total":0}""", "t") is not null) throw new Exception("flight parse: empty");
    }
}
