using Beacon.Core;

namespace Beacon.Providers;

public sealed class AppProvider : IResultProvider
{
    private readonly AppIndex _index;
    private readonly UsageStats _stats;

    public AppProvider(AppIndex index, UsageStats stats)
    {
        _index = index;
        _stats = stats;
    }

    public string Name => "Apps";

    /// <summary>Apps beat files: typing "code" should offer the editor, not code.txt.</summary>
    public int Weight => 30;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var needle = query.Text;
        var results = new List<SearchResult>();

        foreach (var app in _index.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var score = FuzzyMatcher.Score(app.Name, needle);
            if (score == FuzzyMatcher.NoMatch) continue;

            var entry = app;
            results.Add(new SearchResult
            {
                Title = app.Name,

                // Where it is, not what it is: "Application" told the user nothing they
                // could act on, and the row already carries an App tag.
                Subtitle = app.Location,
                CopyText = app.Location,
                Source = "App",
                Glyph = app.IsPackaged ? "" : "",
                IconPath = app.IconSource,
                Score = score + Weight + _stats.BoostFor("app:" + app.Name),
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    _stats.Record("app:" + entry.Name);
                    try
                    {
                        AppIndex.Launch(entry);
                    }
                    catch (Exception ex)
                    {
                        ctx.Notify($"Could not launch {entry.Name}: {ex.Message}");
                    }
                    return Task.CompletedTask;
                },
            });
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }
}
