using Beacon.Ai;
using Beacon.Core;

namespace Beacon.Providers;

/// <summary>
/// Offers "ask Claude to do this" as a row.
///
/// Two ways in: an explicit <c>?</c> prefix, which pins the row to the top, or a query
/// that simply looks like a sentence, which offers it quietly at the bottom. The implicit
/// case is deliberately low-ranked - a launcher that guesses you meant AI when you were
/// halfway through typing an app name is infuriating.
/// </summary>
public sealed class AiProvider : IResultProvider
{
    public const string Prefix = "?";

    private readonly AiPlanner _planner;
    private readonly Settings _settings;

    /// <summary>
    /// Raised when the user picks an AI row. The window owns running it, because the run
    /// needs to render progress and can stop to ask for confirmation.
    /// </summary>
    public event Func<string, Task>? Requested;

    public AiProvider(AiPlanner planner, Settings settings)
    {
        _planner = planner;
        _settings = settings;
    }

    public string Name => "AI";
    public int Weight => -60;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (!_settings.AiEnabled || query.IsEmpty)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var explicitAsk = query.Text.StartsWith(Prefix, StringComparison.Ordinal);
        var prompt = explicitAsk ? query.Text[Prefix.Length..].Trim() : query.Text;

        if (prompt.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(explicitAsk
                ?
                [
                    new SearchResult
                    {
                        Title = "Ask Claude to do something",
                        Subtitle = "Type an instruction, e.g. \"snap this left and mute the volume\"",
                        Source = "AI",
                        Glyph = "",
                        Score = 1200,
                        InvokeAsync = _ => Task.CompletedTask,
                    },
                ]
                : []);
        }

        // Implicit offers only for things that read like instructions, not partial words.
        if (!explicitAsk && !LooksLikeInstruction(prompt))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var configured = _planner.IsConfigured;

        var result = new SearchResult
        {
            Title = configured ? $"Ask Claude: “{prompt}”" : "Ask Claude — no API key set",
            Subtitle = configured
                ? $"{AiModels.LabelFor(_settings.AiModel)} will act on your PC"
                : "Open Beacon settings from the tray icon to add one",
            Source = "AI",
            Glyph = "",
            // An instruction the local parser could not place is exactly what Claude is for,
            // so offer it near the top rather than buried under file matches. Anything the
            // parser did understand outranks it at 1150.
            Score = explicitAsk ? 1200 : Nl.NlParser.LooksLikeCommandSentence(prompt) ? 1000 : Weight,
            InvokeAsync = async ctx =>
            {
                if (!configured)
                {
                    ctx.Notify("No API key set. Right-click the Beacon tray icon to open settings.");
                    return;
                }

                var handler = Requested;
                if (handler is not null) await handler(prompt).ConfigureAwait(false);
            },
        };

        return Task.FromResult<IReadOnlyList<SearchResult>>([result]);
    }

    /// <summary>
    /// Three or more words, or a trailing question mark. Crude, but it is only deciding
    /// whether to show one extra low-ranked row.
    /// </summary>
    private static bool LooksLikeInstruction(string text)
    {
        if (text.EndsWith('?')) return true;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 3;
    }
}
