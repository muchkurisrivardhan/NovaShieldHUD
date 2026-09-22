namespace Beacon.Core;

/// <summary>One row in the results list.</summary>
public sealed class SearchResult
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "";

    /// <summary>Where this came from, shown as a faint tag on the right ("App", "File", "AI").</summary>
    public string Source { get; init; } = "";

    public int Score { get; set; }

    /// <summary>Destructive rows require a second Enter; the UI renders them differently.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Optional path/icon target for shell-extracted icons.</summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// What Ctrl+C on this row puts on the clipboard - the app's executable, the file's full
    /// path, the site's URL. Null for rows with nothing meaningful to copy.
    /// </summary>
    public string? CopyText { get; init; }

    /// <summary>
    /// Optional URL whose site icon represents this row. Resolved asynchronously, so a
    /// row renders with its glyph first and upgrades if and when the icon arrives.
    /// </summary>
    public string? FaviconUrl { get; init; }

    /// <summary>When set, Tab on this row enters that site's search scope.</summary>
    public SearchScope? Scope { get; init; }

    /// <summary>Enter on this row enters <see cref="Scope"/> rather than running anything.</summary>
    public bool EntersScopeOnInvoke { get; init; }

    /// <summary>
    /// Text to carry into the scope. Tab on "Search YouTube for 'cats'" keeps "cats"
    /// rather than throwing away what was already typed.
    /// </summary>
    public string ScopeSeed { get; init; } = string.Empty;

    public required Func<ActionContext, Task> InvokeAsync { get; init; }
}

/// <summary>Parsed form of what the user typed.</summary>
public sealed class SearchQuery
{
    public required string Raw { get; init; }

    /// <summary>Raw, trimmed. What most providers match against.</summary>
    public string Text => Raw.Trim();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);

    /// <summary>First token, lowercased — used for prefix routing ("g", "yt", "?").</summary>
    public string Prefix
    {
        get
        {
            var t = Text;
            var i = t.IndexOf(' ');
            return (i < 0 ? t : t[..i]).ToLowerInvariant();
        }
    }

    /// <summary>Everything after the first token.</summary>
    public string Rest
    {
        get
        {
            var t = Text;
            var i = t.IndexOf(' ');
            return i < 0 ? string.Empty : t[(i + 1)..].Trim();
        }
    }

    public static SearchQuery From(string raw) => new() { Raw = raw ?? string.Empty };
}

/// <summary>
/// A source of results. Providers are queried in parallel on every keystroke and must
/// honour the cancellation token — a slow provider must never hold up the list.
/// </summary>
public interface IResultProvider
{
    string Name { get; }

    /// <summary>
    /// Nudges this provider's results up or down relative to others. Apps outrank files
    /// so that typing "code" offers the editor before a file called code.txt.
    /// </summary>
    int Weight { get; }

    Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct);
}
