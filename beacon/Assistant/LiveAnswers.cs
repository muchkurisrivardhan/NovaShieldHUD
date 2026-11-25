using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Beacon.Assistant;

/// <summary>A video found by <see cref="LiveAnswers.LatestVideoAsync"/>.</summary>
public sealed record VideoAnswer(string Channel, string Title, DateTimeOffset Published, long? Views, string Url);

/// <summary>An answer to say out loud, and the fuller text for a model or an agent.</summary>
public sealed record LiveAnswer(string Say, string Detail, VideoAnswer? Video = null);

/// <summary>
/// Real-world questions answered inside Beacon, without opening the browser: the weather from
/// Open-Meteo and a channel's latest video from YouTube's public feed. Both are free, need no
/// account or key, and are only asked the place or channel name, nothing about the user.
///
/// Nothing here opens anything. A video's link is only opened if the user clicks Watch.
/// </summary>
public static partial class LiveAnswers
{
    static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    /// <summary>Text for a tool result (brain or MCP): the detail, or why it couldn't be found.</summary>
    public static async Task<LiveAnswer> AskAsync(string kind, string what, CancellationToken ct) => kind switch
    {
        "weather" => await WeatherAsync(what, tomorrow: false, ct).ConfigureAwait(false),
        "latest_video" => await LatestVideoAsync(what, ct).ConfigureAwait(false),
        _ => new LiveAnswer("I can't look that up.", "No such lookup."),
    };

    // ------------------------------------------------------------------ questions

    /// <summary>A question Beacon answers itself. <see cref="What"/> may be empty ("what's the weather?").</summary>
    public sealed record Question(string Kind, string What, bool Tomorrow);

    /// <summary>
    /// "What's the weather in Paris?", "will it rain tomorrow", "latest MrBeast video",
    /// "show me the newest video from Veritasium", "what did MKBHD upload". Null for anything
    /// else, including "search youtube for…" and "open…", which the user means literally.
    /// </summary>
    public static Question? Find(string text)
    {
        var t = text.Trim().TrimEnd('?', '.', '!').Trim();
        if (t.Length == 0 || LiteralAsk().IsMatch(t)) return null;

        foreach (var rx in new[] { VideoOf(), VideoFrom(), ChannelPosted() })
            if (rx.Match(t) is { Success: true } m)
            {
                var who = ChannelNoise().Replace(m.Groups["ch"].Value, "").Trim();
                if (who.Length > 0 && who.Length <= 40) return new Question("latest_video", who, false);
            }

        if (WeatherWords().IsMatch(t))
        {
            var place = WeatherPlace().Match(t) is { Success: true } p ? CleanPlace(p.Groups["place"].Value) : "";
            return new Question("weather", place, t.Contains("tomorrow", StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>A city the user asked Nova to remember: "I live in Pune", "The user lives in Austin."</summary>
    public static string HomeFrom(IEnumerable<string> memories)
    {
        foreach (var m in memories)
            if (HomeRx().Match(m) is { Success: true } hit)
                return CleanPlace(Regex.Split(hit.Groups["place"].Value, @"\s+(?:and|but|with|since|for|now)\s+", RegexOptions.IgnoreCase)[0]);
        return "";
    }

    [GeneratedRegex(@"^(?:please\s+)?(?:search|google|look\s*up|open|go\s*to|browse)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiteralAsk();

    // "latest mrbeast video", "show me the newest MrBeast's youtube video", "what's the last video of veritasium"
    [GeneratedRegex(@"\b(?:latest|newest|new|last|most\s+recent|recent)\s+(?:video|upload|vid)s?\s+(?:of|from|by|on)\s+(?<ch>.+?)(?:\s+(?:on|in)\s+youtube)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VideoFrom();

    [GeneratedRegex(@"\b(?:latest|newest|new|last|most\s+recent|recent)\s+(?<ch>[\w@][\w .'’@-]{0,40}?)\s+(?:youtube\s+)?(?:video|upload|vid)s?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex VideoOf();

    // "what did mkbhd upload", "what has mrbeast posted lately"
    [GeneratedRegex(@"^what\s+(?:did|has|have)\s+(?<ch>.+?)\s+(?:just\s+)?(?:upload|post|put\s+out|release)(?:ed)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPosted();

    [GeneratedRegex(@"\b(?:weather|forecast|temperature|how\s+(?:hot|cold|warm)|(?:will|is|does)\s+it\s+(?:rain|snow|be\s+(?:hot|cold|sunny))|raining|snowing|umbrella)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeatherWords();

    [GeneratedRegex(@"\b(?:in|for|at|near)\s+(?<place>[\p{L}][\p{L} .,'’-]{1,40}?)(?:\s+(?:today|tonight|tomorrow|now|right\s+now|this\s+\w+))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeatherPlace();

    [GeneratedRegex(@"\b(?:live|lives|living|based|staying|stays)\s+in\s+(?<place>[\p{L}][\p{L} .'’-]{1,40})", RegexOptions.IgnoreCase)]
    private static partial Regex HomeRx();

    // ------------------------------------------------------------------ weather

    /// <summary>The weather now, today and tomorrow in <paramref name="place"/>.</summary>
    public static async Task<LiveAnswer> WeatherAsync(string place, bool tomorrow, CancellationToken ct)
    {
        place = CleanPlace(place);
        if (place.Length == 0)
            return new LiveAnswer("Which city should I check?", "Couldn't check the weather: no place was given. Ask the user which city.");

        try
        {
            var geo = await GetJsonAsync("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" +
                                         Uri.EscapeDataString(place), ct).ConfigureAwait(false);
            if (geo?["results"] is not JsonArray { Count: > 0 } results || results[0] is not JsonObject spot)
                return new LiveAnswer($"I couldn't find a place called {place}.", $"Couldn't find a place called \"{place}\".");

            var name = Str(spot["name"]);
            var where = string.Join(", ", new[] { name, Str(spot["admin1"]), Str(spot["country"]) }
                .Where(s => s.Length > 0).Distinct());
            var lat = spot["latitude"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);
            var lon = spot["longitude"]!.GetValue<double>().ToString(CultureInfo.InvariantCulture);

            // The place's own units, not the PC's: Hyderabad in °C even on a US-English Windows.
            var metric = Str(spot["country_code"]).ToUpperInvariant() is not ("US" or "LR" or "MM" or "BS" or "BZ" or "KY" or "PW" or "PR" or "GU");
            var units = metric ? "" : "&temperature_unit=fahrenheit&wind_speed_unit=mph";
            var f = await GetJsonAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&timezone=auto&forecast_days=2{units}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max", ct).ConfigureAwait(false);
            if (f?["current"] is not JsonObject now || f["daily"] is not JsonObject daily)
                return new LiveAnswer("The weather service didn't answer properly. Try again in a bit.", "Couldn't get the forecast.");

            var deg = metric ? "°C" : "°F";
            var speed = metric ? "km/h" : "mph";
            int Round(JsonNode? n) => n is null ? 0 : (int)Math.Round(n.GetValue<double>());
            int Day(string key, int i) => daily[key] is JsonArray a && a.Count > i && a[i] is not null ? Round(a[i]) : 0;

            var temp = Round(now["temperature_2m"]);
            var feels = Round(now["apparent_temperature"]);
            var sky = Sky(Round(now["weather_code"]));
            var wind = Round(now["wind_speed_10m"]);
            var humidity = Round(now["relative_humidity_2m"]);

            string DayText(int i) =>
                $"high {Day("temperature_2m_max", i)}{deg}, low {Day("temperature_2m_min", i)}{deg}, {Sky(Day("weather_code", i))}, " +
                $"{Day("precipitation_probability_max", i)}% chance of rain";

            var detail = $"Weather in {where} (from Open-Meteo): now {temp}{deg}, feels like {feels}{deg}, {sky}, wind {wind} {speed}, " +
                         $"humidity {humidity}%. Today: {DayText(0)}. Tomorrow: {DayText(1)}.";

            var say = tomorrow
                ? $"Tomorrow in {name}: {Sky(Day("weather_code", 1))}, a high of {Day("temperature_2m_max", 1)} and a low of " +
                  $"{Day("temperature_2m_min", 1)} degrees, with a {Day("precipitation_probability_max", 1)} percent chance of rain."
                : $"It's {temp} degrees and {sky} in {name} right now" + (Math.Abs(feels - temp) >= 3 ? $", feels like {feels}" : "") +
                  $". Today's high is {Day("temperature_2m_max", 0)} and the low {Day("temperature_2m_min", 0)}, " +
                  $"with a {Day("precipitation_probability_max", 0)} percent chance of rain.";
            return new LiveAnswer(say, detail);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException
                                      or System.Text.Json.JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return new LiveAnswer("I couldn't reach the weather service. Are we online?", "Couldn't reach the weather service: " + e.Message);
        }
    }

    static string CleanPlace(string place)
    {
        place = place.Trim().Trim('?', '.', '!', '"', '\'');
        place = WhenWords().Replace(place, "").Trim();
        var comma = place.IndexOf(',');   // the geocoder wants a bare name: "Austin, Texas" -> "Austin"
        return comma > 0 ? place[..comma].Trim() : place;
    }

    [GeneratedRegex(@"\b(?:today|tonight|tomorrow|right now|now|this (?:morning|afternoon|evening|week))\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhenWords();

    /// <summary>The WMO weather code in plain words.</summary>
    internal static string Sky(int code) => code switch
    {
        0 => "clear",
        1 => "mostly clear",
        2 => "partly cloudy",
        3 => "cloudy",
        45 or 48 => "foggy",
        51 or 53 or 55 => "drizzly",
        56 or 57 => "freezing drizzle",
        61 => "light rain",
        63 => "raining",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 => "light snow",
        73 => "snowing",
        75 => "heavy snow",
        77 => "snow grains",
        80 or 81 => "showers",
        82 => "heavy showers",
        85 or 86 => "snow showers",
        95 => "thunderstorms",
        96 or 99 => "thunderstorms with hail",
        _ => "mixed weather",
    };

    // ------------------------------------------------------------------ YouTube

    /// <summary>A channel's newest upload, from YouTube's public feed of the channel.</summary>
    public static async Task<LiveAnswer> LatestVideoAsync(string channel, CancellationToken ct)
    {
        var asked = channel.Trim().Trim('?', '.', '!', '"');
        if (asked.Length == 0)
            return new LiveAnswer("Whose videos should I look at?", "Couldn't look up a video: no channel was given. Ask the user which channel.");

        try
        {
            var id = await ChannelIdAsync(asked, ct).ConfigureAwait(false);
            if (id is null)
                return new LiveAnswer($"I couldn't find a YouTube channel called {asked}.",
                    $"Couldn't find a YouTube channel for \"{asked}\". Ask the user for the channel's exact name or @handle.");

            var xml = await GetTextAsync("https://www.youtube.com/feeds/videos.xml?channel_id=" + id, ct).ConfigureAwait(false);
            var video = ParseFeed(xml);
            if (video is null)
                return new LiveAnswer($"{asked} hasn't posted any videos I can see.", $"The channel \"{asked}\" has no public videos.");

            var views = video.Views is { } v ? $", {Count(v)} views" : "";
            var say = $"{video.Channel}'s latest video is “{video.Title}”, posted {Ago(video.Published)}{views}.";
            var detail = $"Latest video from {video.Channel} on YouTube: \"{video.Title}\", posted {Ago(video.Published)} " +
                         $"({video.Published.LocalDateTime:ddd d MMM yyyy, h:mm tt}){views}. Link: {video.Url}";
            return new LiveAnswer(say, detail, video);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            ct.ThrowIfCancellationRequested();
            return new LiveAnswer("I couldn't reach YouTube just now. Are we online?", "Couldn't reach YouTube: " + e.Message);
        }
    }

    /// <summary>"MrBeast", "mr beast", "@MrBeast" or a channel id → the UC… id.</summary>
    static async Task<string?> ChannelIdAsync(string asked, CancellationToken ct)
    {
        if (ChannelIdRx().Match(asked) is { Success: true } direct) return direct.Value;

        var bare = asked.TrimStart('@');
        bare = ChannelNoise().Replace(bare, "").Trim();
        var joined = new string(bare.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.').ToArray());
        if (joined.Length == 0) return null;

        foreach (var path in new[] { "@" + joined, "c/" + joined, "user/" + joined })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/" + path);
            request.Headers.Add("Cookie", "SOCS=CAI; CONSENT=YES+1");   // skip the cookie-consent page, accepting only the essentials
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            foreach (var rx in new[] { CanonicalRx(), ExternalIdRx() })
                if (rx.Match(html) is { Success: true } m) return m.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex(@"\bUC[\w-]{22}\b")]
    private static partial Regex ChannelIdRx();

    [GeneratedRegex(@"(?:'s|’s)?\s*(?:youtube|yt)?\s*(?:channel)?\s*$|^(?:the\s+)?(?:youtube\s*r?|youtuber|channel)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelNoise();

    [GeneratedRegex(@"<link rel=""canonical"" href=""https://www\.youtube\.com/channel/(UC[\w-]{22})""")]
    private static partial Regex CanonicalRx();

    [GeneratedRegex(@"""externalId"":""(UC[\w-]{22})""")]
    private static partial Regex ExternalIdRx();

    internal static VideoAnswer? ParseFeed(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace media = "http://search.yahoo.com/mrss/";
        XNamespace yt = "http://www.youtube.com/xml/schemas/2015";
        var doc = XDocument.Parse(xml);
        var feed = doc.Root;
        if (feed is null) return null;
        var entry = feed.Elements(atom + "entry")
            .OrderByDescending(e => DateTimeOffset.TryParse((string?)e.Element(atom + "published"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d : DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (entry is null) return null;

        var channel = (string?)entry.Element(atom + "author")?.Element(atom + "name") ?? (string?)feed.Element(atom + "title") ?? "The channel";
        var title = (string?)entry.Element(atom + "title") ?? "an untitled video";
        var id = (string?)entry.Element(yt + "videoId");
        var url = id is not null ? "https://www.youtube.com/watch?v=" + id : (string?)entry.Element(atom + "link")?.Attribute("href") ?? "";
        DateTimeOffset.TryParse((string?)entry.Element(atom + "published"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var published);
        long? views = long.TryParse((string?)entry.Descendants(media + "statistics").FirstOrDefault()?.Attribute("views"), out var n) ? n : null;
        return new VideoAnswer(channel.Trim(), title.Trim(), published, views, url);
    }

    internal static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Plural((int)span.TotalMinutes, "minute") + " ago";
        if (span.TotalDays < 1) return Plural((int)span.TotalHours, "hour") + " ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 14) return Plural((int)span.TotalDays, "day") + " ago";
        if (span.TotalDays < 60) return Plural((int)(span.TotalDays / 7), "week") + " ago";
        return "on " + when.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";

    internal static string Count(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#} billion",
        >= 1_000_000 => $"{n / 1e6:0.#} million",
        >= 10_000 => $"{n / 1e3:0} thousand",
        _ => n.ToString("N0", CultureInfo.CurrentCulture),
    };

    // ------------------------------------------------------------------ the web

    const int PagesRead = 3;
    const int CharsPerPage = 1400;

    /// <summary>
    /// Any question that needs the internet: searched and read in the background (DuckDuckGo,
    /// with Wikipedia when that fails), nothing opened. Returns the sources' text for the model
    /// to answer from. It's written by strangers, so callers wrap it as untrusted.
    /// </summary>
    public static async Task<string> WebAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0) return "Couldn't look anything up: no question was given.";

        List<(string Title, string Url, string Snippet)> hits;
        try { hits = await DuckDuckGoAsync(query, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { ct.ThrowIfCancellationRequested(); hits = []; }

        if (hits.Count == 0)
        {
            try { hits = await WikipediaAsync(query, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
        if (hits.Count == 0) return $"Couldn't find anything online for \"{query}\" (or we're offline).";

        // The top pages, read at the same time; a slow or odd page is just left out.
        var reads = hits.Where(h => Readable(h.Url)).Take(PagesRead)
            .Select(async h => (h.Url, Text: await PageTextAsync(h.Url, ct).ConfigureAwait(false))).ToList();
        var pages = (await Task.WhenAll(reads).ConfigureAwait(false)).Where(p => p.Text.Length > 200)
            .ToDictionary(p => p.Url, p => p.Text);

        var sb = new System.Text.StringBuilder($"Looked up online (searched \"{query}\" on {DateTime.Now:d MMMM yyyy}):\n");
        var n = 0;
        foreach (var h in hits.Take(6))
        {
            sb.Append($"\n[{++n}] {h.Title} ({Host(h.Url)})\n");
            if (h.Snippet.Length > 0) sb.Append(h.Snippet).Append('\n');
            if (pages.TryGetValue(h.Url, out var text)) sb.Append("From the page: ").Append(text).Append('\n');
        }
        sb.Append("\nAnswer the user's question from these sources in plain words. If they disagree, go with the newest. " +
                  "If they don't answer it, say so.");
        return sb.ToString();
    }

    static async Task<List<(string, string, string)>> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        // GET: a POST from here is answered with a bot check (202) instead of results.
        using var response = await Http.GetAsync("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var list = new List<(string, string, string)>();
        var snippets = DdgSnippet().Matches(html).Select(m => Plain(m.Groups[1].Value)).ToList();
        var i = 0;
        foreach (Match m in DdgResult().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (href.Contains("uddg=", StringComparison.Ordinal))
            {
                var q = href[(href.IndexOf("uddg=", StringComparison.Ordinal) + 5)..];
                var amp = q.IndexOf('&');
                href = Uri.UnescapeDataString(amp >= 0 ? q[..amp] : q);
            }
            if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
            var snippet = i < snippets.Count ? snippets[i] : "";
            i++;
            // Ads go through duckduckgo.com/y.js; they're not answers.
            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase) || href.Contains("duckduckgo.com/y.js", StringComparison.Ordinal)) continue;
            list.Add((Plain(m.Groups[2].Value), href, snippet));
            if (list.Count >= 8) break;
        }
        return list;
    }

    [GeneratedRegex(@"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgResult();

    [GeneratedRegex(@"<a[^>]+class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgSnippet();

    static async Task<List<(string, string, string)>> WikipediaAsync(string query, CancellationToken ct)
    {
        var search = await GetJsonAsync("https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=3&srsearch=" +
                                        Uri.EscapeDataString(query), ct).ConfigureAwait(false);
        var list = new List<(string, string, string)>();
        if (search?["query"]?["search"] is not JsonArray found) return list;
        foreach (var item in found)
        {
            var title = Str(item?["title"]);
            if (title.Length == 0) continue;
            list.Add((title, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')), Plain(Str(item?["snippet"]))));
        }
        return list;
    }

    static bool Readable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && IsPublic(u)
        && !u.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && !u.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        && !u.Host.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)
        && !u.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only the public internet: a search result (or a redirect) pointing at this PC or the home
    /// network, such as a router's page, is never fetched.
    /// </summary>
    public static bool IsPublic(Uri u)
    {
        if (u.IsLoopback || !u.Host.Contains('.') || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return false;
        var b = ip.GetAddressBytes();
        return b.Length != 4 || !(b[0] is 10 or 127 or 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                                  || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    static readonly HttpClient PageHttp = MakePageClient();

    static HttpClient MakePageClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false, AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    /// <summary>GET that follows up to 4 redirects itself, refusing any hop that isn't the public internet.</summary>
    static async Task<HttpResponseMessage?> GetPublicAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop < 5; hop++)
        {
            if (url.Scheme is not ("https" or "http") || !IsPublic(url)) return null;
            var response = await PageHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } next) return response;
            response.Dispose();
            url = next.IsAbsoluteUri ? next : new Uri(url, next);
        }
        return null;
    }

    /// <summary>A page's main text, roughly: scripts, menus and markup stripped, the start kept.</summary>
    static async Task<string> PageTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            // Wikipedia's pages are mostly markup; its API gives the article as plain text.
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase)
                && u.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
            {
                var title = Uri.UnescapeDataString(u.AbsolutePath[6..]);
                var api = await GetJsonAsync($"https://{u.Host}/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=plain" +
                                             $"&exchars={CharsPerPage}&redirects=1&format=json&titles=" + Uri.EscapeDataString(title), timeout.Token).ConfigureAwait(false);
                if (api?["query"]?["pages"] is JsonObject wikiPages)
                    foreach (var (_, page) in wikiPages)
                        if (Str(page?["extract"]) is { Length: > 0 } extract) return Spaces().Replace(extract, " ");
                return "";
            }
            using var response = await GetPublicAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "text/plain")) return "";
            var html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (html.Length > 2_000_000) html = html[..2_000_000];
            var body = MainPart().Match(html) is { Success: true } main ? main.Value : html;
            var text = Plain(Clutter().Replace(body, " "));
            return text.Length > CharsPerPage ? text[..CharsPerPage] + "…" : text;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return "";
        }
    }

    [GeneratedRegex(@"<(?:article|main)\b.*?</(?:article|main)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainPart();

    [GeneratedRegex(@"<(script|style|noscript|svg|nav|header|footer|aside|form|iframe)\b.*?</\1>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Clutter();

    [GeneratedRegex(@"<(?:[^>""']|""[^""]*""|'[^']*')*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    static string Plain(string html) => Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;

    // ------------------------------------------------------------------ plumbing

    static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct) =>
        JsonNode.Parse(await GetTextAsync(url, ct).ConfigureAwait(false));

    static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
}
