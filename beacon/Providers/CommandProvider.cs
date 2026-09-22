using Beacon.Core;

namespace Beacon.Providers;

/// <summary>
/// Surfaces <see cref="CommandRegistry"/> entries as typed search rows.
///
/// Commands whose only parameter has a fixed set of choices get expanded into one row per
/// choice ("Snap Window Left", "Snap Window Right"...). That keeps the typed experience
/// direct - you never have to type a command, press Enter, then type an argument - while
/// the underlying command stays a single declaration that the AI sees as one tool.
/// </summary>
public sealed class CommandProvider : IResultProvider
{
    private readonly CommandRegistry _registry;
    private readonly UsageStats _stats;
    private readonly List<Row> _rows;

    private sealed record Row(
        BeaconCommand Command,
        string Title,
        string Subtitle,
        IReadOnlyDictionary<string, string> Arguments,
        string HaystackKeywords,
        string StatsKey);

    public CommandProvider(CommandRegistry registry, UsageStats stats)
    {
        _registry = registry;
        _stats = stats;
        _rows = BuildRows().ToList();
    }

    public string Name => "Commands";

    /// <summary>Above apps: "lock" should offer Lock PC before any app called Lock.</summary>
    public int Weight => 45;

    private IEnumerable<Row> BuildRows()
    {
        foreach (var cmd in _registry.Commands)
        {
            if (!cmd.ShowInSearch) continue;

            var keywords = string.Join(' ', cmd.Keywords);

            // Single choice-parameter commands fan out into one row per choice.
            if (cmd.Parameters is [{ Choices: { Length: > 0 } choices } param])
            {
                foreach (var choice in choices)
                {
                    var title = cmd.ChoiceTitles is not null && cmd.ChoiceTitles.TryGetValue(choice, out var named)
                        ? named
                        : $"{cmd.Title} {Humanize(choice)}";

                    yield return new Row(
                        cmd,
                        title,
                        cmd.Subtitle,
                        new Dictionary<string, string> { [param.Name] = choice },
                        keywords + " " + choice,
                        $"cmd:{cmd.Id}:{choice}");
                }
                continue;
            }

            // Commands needing free-text input can't be a one-shot row; they're reachable
            // via the AI layer, which can supply the argument.
            if (cmd.Parameters.Any(p => p.Required && p.Choices is null)) continue;

            yield return new Row(
                cmd,
                cmd.Title,
                cmd.Subtitle,
                new Dictionary<string, string>(),
                keywords,
                $"cmd:{cmd.Id}");
        }
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var needle = query.Text;
        var results = new List<SearchResult>();

        foreach (var row in _rows)
        {
            ct.ThrowIfCancellationRequested();

            var score = FuzzyMatcher.ScoreAny(needle, row.Title, row.HaystackKeywords);
            if (score == FuzzyMatcher.NoMatch) continue;

            var captured = row;
            results.Add(new SearchResult
            {
                Title = row.Title,
                Subtitle = row.Subtitle,
                Source = "Command",
                Glyph = row.Command.Glyph,
                IsDestructive = row.Command.IsDestructive,
                Score = score + Weight + _stats.BoostFor(row.StatsKey),
                InvokeAsync = async ctx =>
                {
                    _stats.Record(captured.StatsKey);

                    var scoped = new ActionContext
                    {
                        TargetWindow = ctx.TargetWindow,
                        Arguments = captured.Arguments,
                        RawArgument = ctx.RawArgument,
                        Dismiss = ctx.Dismiss,
                        Notify = ctx.Notify,
                    };

                    var message = await captured.Command.RunAsync(scoped).ConfigureAwait(false);

                    // Only surface a toast when something went wrong or is worth confirming;
                    // a successful launch is self-evident from the window that just appeared.
                    if (message.StartsWith("Could not", StringComparison.Ordinal) ||
                        message.StartsWith("No ", StringComparison.Ordinal))
                    {
                        ctx.Notify(message);
                    }
                },
            });
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    private static string Humanize(string choice) =>
        choice switch
        {
            "full" => "Fullscreen",
            "top-left" => "Top Left",
            "top-right" => "Top Right",
            "bottom-left" => "Bottom Left",
            "bottom-right" => "Bottom Right",
            _ => char.ToUpperInvariant(choice[0]) + choice[1..],
        };
}
