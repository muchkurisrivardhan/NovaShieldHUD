using System.IO;
using System.Text.Json;

namespace Beacon.Providers;

public sealed record Bookmark(string Title, string Url, string Folder, string Browser)
{
    public string Host
    {
        get
        {
            try
            {
                return new Uri(Url).Host;
            }
            catch
            {
                return Url;
            }
        }
    }
}

/// <summary>
/// Reads bookmarks from the Chromium-family browsers.
///
/// A curated site catalog only ever knows the sites <em>I</em> thought of; bookmarks are
/// the sites the user actually cares about, which is what makes "type a name, open the
/// site" genuinely useful. Chromium stores them as plain JSON that is not locked while the
/// browser runs, so this needs no dependency and no database access.
///
/// Firefox is deliberately not supported: its bookmarks live in places.sqlite, which would
/// mean taking on a SQLite dependency and contending with a locked file.
/// </summary>
public sealed class BookmarkIndex
{
    private volatile List<Bookmark> _bookmarks = [];

    public IReadOnlyList<Bookmark> Bookmarks => _bookmarks;
    public bool IsReady { get; private set; }

    private static IEnumerable<(string Root, string Browser)> BrowserRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        yield return (Path.Combine(local, @"Google\Chrome\User Data"), "Chrome");
        yield return (Path.Combine(local, @"Microsoft\Edge\User Data"), "Edge");
        yield return (Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "Brave");
        yield return (Path.Combine(local, @"Vivaldi\User Data"), "Vivaldi");
        yield return (Path.Combine(local, @"Chromium\User Data"), "Chromium");
        yield return (Path.Combine(roaming, @"Opera Software\Opera Stable"), "Opera");
    }

    public Task BuildAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        // Keyed by URL so the same site bookmarked in two browsers appears once.
        var seen = new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, browser) in BrowserRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            foreach (var file in BookmarkFilesIn(root))
            {
                try
                {
                    foreach (var bookmark in Parse(file, browser))
                        seen.TryAdd(bookmark.Url, bookmark);
                }
                catch
                {
                    // A profile mid-write or an unexpected shape: skip that file only.
                }
            }
        }

        _bookmarks = seen.Values.ToList();
        IsReady = true;
    }, ct);

    private static IEnumerable<string> BookmarkFilesIn(string root)
    {
        // Opera keeps the file at the root; Chromium browsers nest it per profile.
        var direct = Path.Combine(root, "Bookmarks");
        if (File.Exists(direct)) yield return direct;

        IEnumerable<string> profiles;
        try
        {
            profiles = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var profile in profiles)
        {
            var name = Path.GetFileName(profile);

            // Guest and System profiles hold no bookmarks a person put there.
            if (name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System Profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Path.Combine(profile, "Bookmarks");
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private static List<Bookmark> Parse(string file, string browser)
    {
        var found = new List<Bookmark>();

        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(stream);

        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return found;

        foreach (var root in roots.EnumerateObject())
        {
            // "sync_transaction_version" and friends are scalars, not folders.
            if (root.Value.ValueKind == JsonValueKind.Object)
                Walk(root.Value, browser, "", found, 0);
        }

        return found;
    }

    private static void Walk(JsonElement node, string browser, string folder, List<Bookmark> acc, int depth)
    {
        if (depth > 12 || acc.Count > 5000) return;

        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        var name = node.TryGetProperty("name", out var n) ? n.GetString() : null;

        if (type == "url")
        {
            var url = node.TryGetProperty("url", out var u) ? u.GetString() : null;

            // javascript: bookmarklets and chrome:// pages are not things to "open" here.
            if (!string.IsNullOrWhiteSpace(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                acc.Add(new Bookmark(
                    string.IsNullOrWhiteSpace(name) ? url : name,
                    url,
                    folder,
                    browser));
            }

            return;
        }

        if (!node.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var childFolder = string.IsNullOrWhiteSpace(name) ? folder : name;
        foreach (var child in children.EnumerateArray())
            Walk(child, browser, childFolder, acc, depth + 1);
    }
}
