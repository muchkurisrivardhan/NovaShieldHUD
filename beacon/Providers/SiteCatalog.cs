namespace Beacon.Providers;

/// <summary>
/// A known website: somewhere to go, and usually something to search there.
/// </summary>
/// <param name="Name">Display name, e.g. "YouTube".</param>
/// <param name="Domain">Shown as the subtitle and used as the open target.</param>
/// <param name="SearchTemplate">Format string with {0} for the URL-escaped term. Null when the site has no useful search.</param>
/// <param name="Prefix">Short explicit prefix, e.g. "yt". Typing it routes straight to this site's search.</param>
/// <param name="Aliases">Extra words that should match this site.</param>
public sealed record Site(
    string Name,
    string Domain,
    string? SearchTemplate,
    string Glyph,
    string? Prefix = null,
    string[]? Aliases = null)
{
    public string OpenUrl => "https://" + Domain;

    public bool CanSearch => SearchTemplate is not null;

    public string SearchUrl(string term) =>
        string.Format(SearchTemplate!, Uri.EscapeDataString(term));

    public string Haystack => Name + " " + Domain + " " + string.Join(' ', Aliases ?? []);
}

/// <summary>
/// The catalog of sites Beacon knows by name.
///
/// This exists so that typing "youtube" offers YouTube itself rather than a Google search
/// for the word "youtube" - the single most common thing a launcher gets wrong. Entries are
/// deliberately curated rather than scraped: a short, high-precision list beats a long one
/// that pushes real results down.
/// </summary>
public static class SiteCatalog
{
    public static readonly Site[] All =
    [
        // ----- search & reference -----
        new("Google", "google.com", "https://www.google.com/search?q={0}", "", "g",
            ["search", "web"]),
        new("Wikipedia", "wikipedia.org", "https://en.wikipedia.org/w/index.php?search={0}", "", "w",
            ["wiki", "encyclopedia"]),
        new("Google Translate", "translate.google.com", "https://translate.google.com/?text={0}", "", "tr",
            ["translate", "translation"]),
        new("Google Maps", "maps.google.com", "https://www.google.com/maps/search/{0}", "", "maps",
            ["map", "directions", "navigate"]),
        new("Google Scholar", "scholar.google.com", "https://scholar.google.com/scholar?q={0}", "", null,
            ["papers", "academic", "research"]),

        // ----- video & music -----
        new("YouTube", "youtube.com", "https://www.youtube.com/results?search_query={0}", "", "yt",
            ["video", "videos", "watch", "stream"]),
        new("Netflix", "netflix.com", "https://www.netflix.com/search?q={0}", "", null,
            ["movies", "shows", "tv"]),
        new("Spotify", "open.spotify.com", "https://open.spotify.com/search/{0}", "", null,
            ["music", "songs", "podcast"]),
        new("Twitch", "twitch.tv", "https://www.twitch.tv/search?term={0}", "", null,
            ["stream", "streaming", "games"]),
        new("Prime Video", "primevideo.com", null, "", null, ["amazon video", "movies"]),

        // ----- mail, calendar & docs -----
        new("Gmail", "mail.google.com", "https://mail.google.com/mail/u/0/#search/{0}", "", "gm",
            ["email", "mail", "inbox", "google mail"]),
        new("Google Calendar", "calendar.google.com", "https://calendar.google.com/calendar/r/search?q={0}", "", "cal",
            ["calendar", "schedule", "meetings", "agenda"]),
        new("Google Drive", "drive.google.com", "https://drive.google.com/drive/search?q={0}", "", "gd",
            ["drive", "storage", "cloud files"]),
        new("Google Docs", "docs.google.com", "https://drive.google.com/drive/search?q={0}%20type:document", "", null,
            ["docs", "document", "word"]),
        new("Google Sheets", "sheets.google.com", "https://drive.google.com/drive/search?q={0}%20type:spreadsheet", "", null,
            ["sheets", "spreadsheet", "excel"]),
        new("Outlook", "outlook.office.com", "https://outlook.office.com/mail/search/{0}", "", null,
            ["email", "mail", "microsoft mail", "office"]),
        new("Notion", "notion.so", "https://www.notion.so/search?query={0}", "", null,
            ["notes", "wiki", "docs"]),
        new("Google Photos", "photos.google.com", "https://photos.google.com/search/{0}", "", null,
            ["photos", "pictures", "images"]),

        // ----- developer -----
        new("GitHub", "github.com", "https://github.com/search?q={0}", "", "gh",
            ["git", "repo", "repository", "code"]),
        new("Stack Overflow", "stackoverflow.com", "https://stackoverflow.com/search?q={0}", "", "so",
            ["stackoverflow", "questions", "programming help"]),
        new("MDN Web Docs", "developer.mozilla.org", "https://developer.mozilla.org/en-US/search?q={0}", "", "mdn",
            ["javascript docs", "web docs", "css docs", "html docs"]),
        new("npm", "npmjs.com", "https://www.npmjs.com/search?q={0}", "", "npm",
            ["node package", "javascript package"]),
        new("NuGet", "nuget.org", "https://www.nuget.org/packages?q={0}", "", "nuget",
            ["dotnet package", "c# package"]),
        new("PyPI", "pypi.org", "https://pypi.org/search/?q={0}", "", "pypi",
            ["python package", "pip"]),
        new("Docker Hub", "hub.docker.com", "https://hub.docker.com/search?q={0}", "", null,
            ["docker", "container", "image"]),
        new("Crates.io", "crates.io", "https://crates.io/search?q={0}", "", null,
            ["rust package", "cargo"]),
        new("Microsoft Learn", "learn.microsoft.com", "https://learn.microsoft.com/en-us/search/?terms={0}", "", null,
            ["dotnet docs", "azure docs", "msdn"]),
        new("Hacker News", "news.ycombinator.com", "https://hn.algolia.com/?q={0}", "", "hn",
            ["hn", "ycombinator", "tech news"]),
        new("Claude", "claude.ai", null, "", null, ["anthropic", "ai", "chat"]),
        new("ChatGPT", "chatgpt.com", null, "", null, ["openai", "ai", "chat"]),

        // ----- social -----
        new("Reddit", "reddit.com", "https://www.reddit.com/search/?q={0}", "", "r",
            ["subreddit", "forum"]),
        new("X", "x.com", "https://x.com/search?q={0}", "", null,
            ["twitter", "tweet", "tweets"]),
        new("LinkedIn", "linkedin.com", "https://www.linkedin.com/search/results/all/?keywords={0}", "", "li",
            ["jobs", "professional", "network"]),
        new("Instagram", "instagram.com", "https://www.instagram.com/explore/tags/{0}/", "", null,
            ["insta", "ig", "photos"]),
        new("Facebook", "facebook.com", "https://www.facebook.com/search/top?q={0}", "", null,
            ["fb", "social"]),
        new("WhatsApp", "web.whatsapp.com", null, "", null,
            ["chat", "messaging", "whatsapp web"]),
        new("Discord", "discord.com", null, "", null, ["chat", "voice", "servers"]),
        new("Telegram", "web.telegram.org", null, "", null, ["chat", "messaging"]),

        // ----- shopping & everyday -----
        new("Amazon", "amazon.com", "https://www.amazon.com/s?k={0}", "", "az",
            ["shop", "shopping", "buy", "order"]),
        new("eBay", "ebay.com", "https://www.ebay.com/sch/i.html?_nkw={0}", "", null,
            ["shop", "auction", "buy"]),
        new("Flipkart", "flipkart.com", "https://www.flipkart.com/search?q={0}", "", null,
            ["shop", "shopping", "buy"]),
        new("Google News", "news.google.com", "https://news.google.com/search?q={0}", "", null,
            ["news", "headlines"]),
        new("Weather", "weather.com", "https://weather.com/search/enhancedlocalsearch?where={0}", "", null,
            ["forecast", "temperature", "rain"]),

        // ----- design & productivity -----
        new("Figma", "figma.com", null, "", null, ["design", "ui", "mockup"]),
        new("Canva", "canva.com", "https://www.canva.com/search?q={0}", "", null,
            ["design", "graphics", "poster"]),
        new("Dropbox", "dropbox.com", null, "", null, ["cloud", "storage", "files"]),
        new("Trello", "trello.com", null, "", null, ["board", "kanban", "tasks"]),
    ];

    private static readonly Dictionary<string, Site> ByPrefix =
        All.Where(s => s.Prefix is not null)
           .ToDictionary(s => s.Prefix!, StringComparer.OrdinalIgnoreCase);

    public static Site Google { get; } = All[0];

    public static Site? FindByPrefix(string prefix) =>
        ByPrefix.TryGetValue(prefix, out var site) ? site : null;

    /// <summary>
    /// Matches the way a sentence names a site: "youtube", "you tube", "youtube.com",
    /// "google maps", "mail". Whole-word only - a substring match would make "search for
    /// news on my pc" resolve to Google News.
    /// </summary>
    public static Site? FindByWords(string words)
    {
        var key = Squash(words);
        if (key.Length < 2) return null;

        foreach (var site in All)
        {
            if (Squash(site.Name) == key) return site;
            if (Squash(site.Domain) == key) return site;

            // "youtube" for youtube.com, "mail" for mail.google.com.
            foreach (var label in site.Domain.Split('.'))
            {
                if (label is "com" or "org" or "co" or "io" or "so" or "tv" or "ai" or "www") continue;
                if (Squash(label) == key) return site;
            }

            if (site.Aliases is not null && site.Aliases.Any(a => Squash(a) == key)) return site;
        }

        return null;
    }

    private static string Squash(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..n]);
    }

    /// <summary>Used by the AI's web_search command, which names engines in plain words.</summary>
    public static Site FindByEngineName(string name) =>
        All.FirstOrDefault(s => s.Name.Replace(" ", "").Equals(
                name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
        ?? Google;
}
