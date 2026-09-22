using Beacon.Core;

namespace Beacon.Providers;

public sealed class CalcProvider : IResultProvider
{
    public string Name => "Calculator";

    /// <summary>A valid expression is unambiguous intent, so it pins to the top.</summary>
    public int Weight => 1100;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty || !Calculator.TryEvaluate(query.Text, out var value))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var formatted = Calculator.Format(value);

        var result = new SearchResult
        {
            Title = formatted,
            Subtitle = $"{query.Text.Trim()} — press Enter to copy",
            Source = "Calc",
            Glyph = "",
            Score = Weight,
            InvokeAsync = ctx =>
            {
                ctx.Dismiss();
                try
                {
                    System.Windows.Clipboard.SetText(formatted);
                    ctx.Notify($"Copied {formatted}");
                }
                catch
                {
                    // Clipboard is occasionally locked by another process; not worth surfacing.
                }
                return Task.CompletedTask;
            },
        };

        return Task.FromResult<IReadOnlyList<SearchResult>>([result]);
    }
}
