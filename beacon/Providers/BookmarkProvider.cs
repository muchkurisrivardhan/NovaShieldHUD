using Beacon.Core;

namespace Beacon.Providers;

public sealed class BookmarkProvider : IResultProvider
{
    private readonly BookmarkIndex _index;
    private readonly UsageStats _stats;

    public BookmarkProvider(BookmarkIndex index, UsageStats stats)
    {
        _index = index;
        _stats = stats;
    }

    public string Name => "Bookmarks";

    /// <summary>
    /// Below apps and commands, above generic web search. A site the user deliberately
    /// saved is a better guess than a Google query, but not better than the app they
    /// have installed under the same name.
    /// </summary>
    public int Weight => 20;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty || query.Text.Length < 2)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var needle = query.Text;
        var results = new List<SearchResult>();

        foreach (var bookmark in _index.Bookmarks)
        {
            ct.ThrowIfCancellationRequested();

            // Match the title or the host, so both "github" and "my project board" land.
            var score = FuzzyMatcher.ScoreAny(needle, bookmark.Title, bookmark.Host);
            if (score == FuzzyMatcher.NoMatch) continue;

            var captured = bookmark;

            results.Add(new SearchResult
            {
                Title = bookmark.Title,
                Subtitle = string.IsNullOrWhiteSpace(bookmark.Folder)
                    ? bookmark.Host
                    : $"{bookmark.Host}  ·  {bookmark.Folder}",
                CopyText = bookmark.Url,
                Source = "Bookmark",
                Glyph = "",
                FaviconUrl = bookmark.Url,
                Score = score + Weight + _stats.BoostFor("bm:" + bookmark.Url),
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    _stats.Record("bm:" + captured.Url);
                    WebProvider.OpenUrl(captured.Url, ctx.Notify);
                    return Task.CompletedTask;
                },
            });
        }

        // Cap here rather than in the window: one over-bookmarked host should not be able
        // to fill the entire result list.
        return Task.FromResult<IReadOnlyList<SearchResult>>(
            results.OrderByDescending(r => r.Score).Take(5).ToList());
    }
}
