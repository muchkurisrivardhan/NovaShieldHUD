using System.Diagnostics;
using System.Text.RegularExpressions;
using Beacon.Core;

namespace Beacon.Providers;

/// <summary>
/// Everything web: direct URL navigation, known sites by name, per-site search, and a
/// Google fallback.
///
/// The ordering rule that matters: typing a site's name should offer <em>that site</em>,
/// not a web search for its name. "youtube" means YouTube; "youtube cats" means search
/// YouTube for cats. Falling through to Google for either is the classic launcher failure.
/// </summary>
public sealed partial class WebProvider : IResultProvider
{
    // On the shared 0-1000 match scale (see FuzzyMatcher). A typed address or an explicit
    // "yt ..." prefix is unambiguous, so it sits above any name match. Site rows add a
    // small offset to their name-match quality so that typing "youtube" ranks YouTube just
    // above a file that happens to be called youtube-something.
    private const int UrlScore = 1100;
    private const int PrefixScore = 1050;
    private const int SiteSearchScore = 30;
    private const int SiteOpenScore = 25;

    /// <summary>Alias matches cap below an app's word match (880 + app weight).</summary>
    private const int AliasCeiling = 780;

    /// <summary>
    /// Suffixes that mean "file" in a launcher, whatever ICANN thinks. Kept to extensions
    /// people actually type into a search bar.
    /// </summary>
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "htm", "css", "js", "mjs", "cjs", "jsx", "ts", "tsx", "json", "xml", "yaml",
        "yml", "toml", "ini", "cfg", "conf", "env", "md", "txt", "rtf", "log", "csv", "tsv",
        "exe", "dll", "msi", "bat", "cmd", "ps1", "psm1", "sh", "bash", "lnk", "url",
        "py", "pyc", "rb", "php", "java", "jar", "cs", "csproj", "sln", "c", "cpp", "cc", "h",
        "hpp", "rs", "kt", "swift", "go", "lua", "sql", "db", "sqlite", "ipynb",
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "epub",
        // ".ai" is deliberately absent: claude.ai is a site people type.
        "png", "jpg", "jpeg", "gif", "bmp", "svg", "ico", "webp", "psd",
        "mp3", "mp4", "wav", "flac", "mov", "avi", "mkv", "webm",
        "zip", "rar", "7z", "tar", "gz", "iso", "bin", "dat", "tmp", "bak", "cache",
    };

    /// <summary>Words that introduce a site rather than naming one.</summary>
    private static readonly HashSet<string> LeadVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "launch", "start", "go", "goto", "visit", "browse", "show", "run",
        "search", "find", "play", "watch", "listen",
    };

    /// <summary>How much of a site's name must be typed before it is offered.</summary>
    private const int MinSiteMatch = 2;

    private readonly UsageStats _stats;

    public WebProvider(UsageStats stats) => _stats = stats;

    public string Name => "Web";
    public int Weight => 10;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var text = query.Text;
        var results = new List<SearchResult>();

        // 1. Something that is already an address wins outright - the user typed a
        //    destination, not a question.
        if (TryParseUrl(text, out var url, out var display))
        {
            results.Add(OpenRow($"Open {display}", "Go to this address", "", url, UrlScore));
            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }

        // 2. An explicit prefix with a term is unambiguous intent: "yt cats".
        var prefixSite = SiteCatalog.FindByPrefix(query.Prefix);
        if (prefixSite is not null && query.Rest.Length > 0 && prefixSite.CanSearch)
        {
            results.Add(SearchRow(prefixSite, query.Rest, PrefixScore));
            results.Add(OpenRow($"Open {prefixSite.Name}", prefixSite.Domain, prefixSite.Glyph,
                prefixSite.OpenUrl, PrefixScore - 80, ScopeFor(prefixSite)));
            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }

        // 3. A site named by name, optionally followed by something to search for.
        if (TryResolveSite(text, out var site, out var term, out var nameQuality))
        {
            var hasTerm = term.Length > 0;

            if (hasTerm && site.CanSearch)
            {
                results.Add(SearchRow(site, term, SiteSearchScore + nameQuality));
                results.Add(OpenRow($"Open {site.Name}", site.Domain, site.Glyph,
                    site.OpenUrl, SiteOpenScore - 120 + nameQuality, ScopeFor(site)));
            }
            else
            {
                // The subtitle advertises Tab, because nothing else would tell you it exists.
                results.Add(OpenRow(site.Name,
                    site.CanSearch ? $"{site.Domain}  ·  Tab to search" : site.Domain,
                    site.Glyph, site.OpenUrl, SiteOpenScore + nameQuality, ScopeFor(site)));

                // With no term there is nothing to search for yet, but showing the option
                // teaches the shortcut: type a space and keep going.
                if (site.CanSearch)
                {
                    results.Add(new SearchResult
                    {
                        Title = $"Search {site.Name}…",
                        Subtitle = site.Prefix is not null
                            ? $"Enter or Tab, then type · or start with “{site.Prefix} ”"
                            : "Enter or Tab, then type what to find",
                        FaviconUrl = site.OpenUrl,
                        Scope = ScopeFor(site),
                        EntersScopeOnInvoke = true,
                        Source = "Web",
                        Glyph = "",
                        Score = SiteOpenScore - 60 + nameQuality,
                        InvokeAsync = ctx =>
                        {
                            // Nothing to run yet - this row is a hint, so leave the bar open.
                            ctx.Notify($"Type what you want to find on {site.Name}.");
                            return Task.CompletedTask;
                        },
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }

        // 4. Nothing recognised: offer a plain web search, ranked low so it never displaces
        //    a real app or command match.
        results.Add(SearchRow(SiteCatalog.Google, text, Weight - 40));

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    // ------------------------------------------------------------- site lookup

    /// <summary>
    /// Splits the query into "site name" + "search term". Tries the longest leading phrase
    /// first so "google drive report" resolves to Drive rather than Google.
    /// </summary>
    private static bool TryResolveSite(string text, out Site site, out string term, out int quality)
    {
        site = null!;
        term = string.Empty;
        quality = 0;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        // "open youtube ..." names the site in the second word. Without this, the leading
        // verb resolves on its own - "open" is a prefix of open.spotify.com, so every
        // sentence starting with it offered a Spotify search.
        if (words.Length > 1 && LeadVerbs.Contains(words[0])) words = words[1..];

        for (var take = Math.Min(3, words.Length); take >= 1; take--)
        {
            var head = string.Join(' ', words.Take(take));
            if (head.Length < MinSiteMatch) continue;

            var best = BestSiteFor(head, out var score);
            if (best is null) continue;

            site = best;
            term = string.Join(' ', words.Skip(take));
            quality = score;
            return true;
        }

        return false;
    }

    private static Site? BestSiteFor(string head, out int quality)
    {
        quality = 0;

        var byPrefix = SiteCatalog.FindByPrefix(head);
        if (byPrefix is not null)
        {
            // Short prefixes ("g", "w", "yt") are also the first letters of countless app
            // names, so on their own they rank like a prefix match, not an exact one - typing
            // "w" should still offer Word before Wikipedia.
            quality = head.Length <= 2 ? 850 : FuzzyMatcher.ExactTier;
            return byPrefix;
        }

        Site? best = null;
        var bestScore = 0;

        foreach (var site in SiteCatalog.All)
        {
            var score = ScoreSite(site, head);
            if (score > bestScore)
            {
                bestScore = score;
                best = site;
            }
        }

        quality = bestScore;
        return best;
    }

    /// <summary>
    /// Deliberately prefix-based rather than fuzzy. A fuzzy match over 45 site names turns
    /// any three letters into a website suggestion, which buries the app the user meant.
    /// </summary>
    private static int ScoreSite(Site site, string head)
    {
        var best = 0;

        var aliases = new HashSet<string>(site.Aliases ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var field in Fields(site))
        {
            if (!field.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;

            // Prefix-only (above), then scored on the shared scale: exact 1000, otherwise
            // 800-900 favouring the field with least left untyped.
            var score = FuzzyMatcher.Score(field, head);

            // Aliases are generic words ("code", "mail", "video") that help a site be found;
            // they must never outrank an app. Typing "code" should offer an app with Code in
            // its name before GitHub, which merely lists "code" as an alias.
            if (aliases.Contains(field)) score = Math.Min(score, AliasCeiling);

            best = Math.Max(best, score);
        }

        return best;
    }

    private static IEnumerable<string> Fields(Site site)
    {
        yield return site.Name;

        // The bare label, so "youtube" matches the domain youtube.com.
        var label = site.Domain.Split('.')[0];
        if (!label.Equals(site.Name, StringComparison.OrdinalIgnoreCase)) yield return label;

        yield return site.Domain;

        foreach (var alias in site.Aliases ?? []) yield return alias;
    }

    // ---------------------------------------------------------- URL detection

    /// <summary>
    /// Recognises bare hosts, schemes, ports, IPv4 and localhost, with optional path or
    /// query. Anything containing a space is prose, not an address.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?<scheme>https?)://)?(?<host>localhost|(?:\d{1,3}\.){3}\d{1,3}|(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+[a-z]{2,24})(?<port>:\d{1,5})?(?<rest>[/?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlLike();

    public static bool TryParseUrl(string text, out string url, out string display)
    {
        url = string.Empty;
        display = string.Empty;

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(' ')) return false;

        var match = UrlLike().Match(trimmed);
        if (!match.Success) return false;

        var host = match.Groups["host"].Value;
        var scheme = match.Groups["scheme"].Success ? match.Groups["scheme"].Value : null;

        // "HUD.html", "notes.md", "setup.exe" are filenames, not addresses. Without this the
        // launcher offered to browse to them, and that row - a URL scores 1100 - sat above
        // the actual file. A few of these really are TLDs (.sh, .zip, .mov), but in a
        // launcher the file reading is overwhelmingly the right one. An explicit scheme
        // still wins: "http://foo.zip" is clearly meant as an address.
        if (scheme is null &&
            match.Groups["rest"].Value.Length == 0 &&
            !match.Groups["port"].Success &&
            host.LastIndexOf('.') is var dot && dot > 0 &&
            FileExtensions.Contains(host[(dot + 1)..]))
        {
            return false;
        }

        // A bare local host is almost always a dev server, which is http, not https.
        var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                      || host.StartsWith("127.")
                      || host.StartsWith("192.168.")
                      || host.StartsWith("10.");

        scheme ??= isLocal ? "http" : "https";

        var tail = match.Groups["port"].Value + match.Groups["rest"].Value;

        url = $"{scheme}://{host}{tail}";
        display = host + tail;
        return true;
    }

    // ------------------------------------------------------------------ rows

    private SearchResult SearchRow(Site site, string term, int score) => new()
    {
        Title = $"Search {site.Name} for “{term}”",
        Subtitle = site.Domain,
        Source = "Web",
        Glyph = site.Glyph,
        FaviconUrl = site.OpenUrl,
        Scope = ScopeFor(site),
        ScopeSeed = term,
        Score = score + _stats.BoostFor("site:" + site.Domain),
        InvokeAsync = ctx =>
        {
            ctx.Dismiss();
            _stats.Record("site:" + site.Domain);
            OpenUrl(site.SearchUrl(term), ctx.Notify);
            return Task.CompletedTask;
        },
    };

    private SearchResult OpenRow(
        string title, string subtitle, string glyph, string url, int score, SearchScope? scope = null) => new()
    {
        Title = title,
        Subtitle = subtitle,
        Source = "Web",
        Glyph = glyph,
        FaviconUrl = url,
        Scope = scope,
        Score = score + _stats.BoostFor("url:" + url),
        InvokeAsync = ctx =>
        {
            ctx.Dismiss();
            _stats.Record("url:" + url);
            OpenUrl(url, ctx.Notify);
            return Task.CompletedTask;
        },
    };

    /// <summary>The Tab-to-search scope for a catalog site, or null if it has no search.</summary>
    public static SearchScope? ScopeFor(Site site) =>
        site.CanSearch
            ? new SearchScope(site.Name, site.Glyph, site.OpenUrl, site.OpenUrl, site.SearchUrl)
            : null;

    public static void OpenUrl(string url, Action<string> notify)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            notify($"Could not open browser: {ex.Message}");
        }
    }
}
