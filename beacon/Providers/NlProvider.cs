using Beacon.Core;
using Beacon.Nl;

namespace Beacon.Providers;

/// <summary>
/// Offers the spoken-sentence reading of the query as a row: "Set volume to 75%, then dim the
/// screen to 20%".
///
/// It sits above every other provider because a sentence that parses cleanly is not ambiguous
/// - nobody types "close all apps and shut down" hoping to find a file. Sentences that do not
/// parse produce nothing at all here, which is what keeps ordinary one-word searches untouched.
/// </summary>
public sealed class NlProvider : IResultProvider
{
    private readonly NlParser _parser;
    private readonly NlRunner _runner;
    private readonly Settings _settings;
    private readonly Action<string> _notify;

    public NlProvider(NlParser parser, NlRunner runner, Settings settings, Action<string> notify)
    {
        _parser = parser;
        _runner = runner;
        _settings = settings;
        _notify = notify;
    }

    public string Name => "Language";

    /// <summary>Unused: understood sentences carry a fixed score.</summary>
    public int Weight => 0;

    /// <summary>Above a URL (1100) but below an explicit "?" ask to Claude (1200).</summary>
    private const int PlanScore = 1150;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (!_settings.NaturalLanguage || query.IsEmpty)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // "?" is the user explicitly asking for Claude; don't compete with that.
        if (query.Text.StartsWith(AiProvider.Prefix, StringComparison.Ordinal))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var understanding = _parser.Parse(query.Text);
        if (understanding is null || understanding.Kind != NlKind.Actions)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        return Task.FromResult<IReadOnlyList<SearchResult>>([Row(understanding)]);
    }

    public SearchResult Row(NlUnderstanding understanding) => new()
    {
        Title = Sentence(understanding.Summary),
        Subtitle = Describe(understanding),
        Source = "Command",
        Glyph = "",
        Score = PlanScore,
        IsDestructive = understanding.IsDestructive,
        InvokeAsync = async ctx =>
        {
            var messages = await _runner.RunAsync(understanding, ctx).ConfigureAwait(false);

            // One line per step, so a chain reports what each part actually did - including
            // the parts that could not: "Brightness set to 20%. / Could not reach the audio device."
            var summary = string.Join("  ", messages.Where(m => m.Length > 0));
            if (summary.Length > 0) _notify(summary);
        },
    };

    private static string Describe(NlUnderstanding understanding)
    {
        var steps = understanding.Steps.Count;
        var prefix = steps > 1 ? $"{steps} steps" : "Understood on this PC";

        return understanding.IsDestructive
            ? $"{prefix} · Enter twice to confirm"
            : $"{prefix} · Enter to run";
    }

    private static string Sentence(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
