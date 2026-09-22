using Beacon.Core;
using Beacon.Interop;

namespace Beacon.Providers;

/// <summary>
/// Surfaces the user's own commands.
///
/// Ranked above built-ins: someone who took the trouble to define "jira" meant that, not a
/// fuzzy match against a system command that happens to share letters.
/// </summary>
public sealed class CustomCommandProvider : IResultProvider
{
    private readonly Settings _settings;
    private readonly UsageStats _stats;

    public CustomCommandProvider(Settings settings, UsageStats stats)
    {
        _settings = settings;
        _stats = stats;
    }

    public string Name => "Custom";
    public int Weight => 60;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var results = new List<SearchResult>();

        foreach (var command in _settings.CustomCommands)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(command.Name)) continue;

            // "jira some ticket" - the name matches, the rest is the query it takes.
            var (score, argument) = Match(command, query);
            if (score == FuzzyMatcher.NoMatch) continue;

            var captured = command;
            var capturedArgument = argument;

            results.Add(new SearchResult
            {
                Title = BuildTitle(command, argument),
                Subtitle = BuildSubtitle(command),
                Source = "Custom",
                Glyph = command.Glyph,
                IsDestructive = command.RequiresConfirmation,
                Scope = ScopeFor(command),
                FaviconUrl = command.Kind == CustomCommandKind.WebSearch ? HomeOf(command.Target) : null,
                Score = score + Weight + _stats.BoostFor("custom:" + command.Id),
                InvokeAsync = async ctx =>
                {
                    _stats.Record("custom:" + captured.Id);

                    // PowerShell reports back into the bar, so it stays open; everything
                    // else has a visible effect elsewhere and should get out of the way.
                    if (captured.Kind != CustomCommandKind.PowerShell) ctx.Dismiss();

                    var message = await CustomCommandRunner.RunAsync(
                        captured,
                        capturedArgument,
                        ctx.TargetWindow,
                        _settings.FolderOpenModeValue,
                        _settings.CustomFileManager,
                        ctx.Notify).ConfigureAwait(true);

                    if (captured.Kind == CustomCommandKind.PowerShell ||
                        message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("exited with", StringComparison.Ordinal) ||
                        message.Contains("timed out", StringComparison.Ordinal))
                    {
                        ctx.Notify(message);
                    }
                },
            });
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    /// <summary>
    /// Matches the command name against the query, splitting off a trailing argument when
    /// the command takes one. Tries the longest leading phrase first so a two-word command
    /// name still wins over its own first word.
    /// </summary>
    private static (int Score, string Argument) Match(CustomCommand command, SearchQuery query)
    {
        var haystackKeywords = command.Keywords;
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0) return (FuzzyMatcher.NoMatch, string.Empty);

        if (command.TakesQuery)
        {
            var nameWordCount = command.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            for (var take = Math.Min(Math.Max(nameWordCount, 1), words.Length); take >= 1; take--)
            {
                var head = string.Join(' ', words.Take(take));
                var score = FuzzyMatcher.ScoreAny(head, command.Name, haystackKeywords);
                if (score == FuzzyMatcher.NoMatch) continue;

                return (score, string.Join(' ', words.Skip(take)));
            }

            return (FuzzyMatcher.NoMatch, string.Empty);
        }

        var whole = FuzzyMatcher.ScoreAny(query.Text, command.Name, haystackKeywords);
        return (whole, string.Empty);
    }

    /// <summary>
    /// A custom web search is its own Tab-to-search scope. A plain link with no {query}
    /// - "youtube" pointing at https://www.youtube.com/ - borrows the search of the catalog
    /// site on the same host, so Tab still does the obvious thing.
    /// </summary>
    private static SearchScope? ScopeFor(CustomCommand command)
    {
        if (command.Kind != CustomCommandKind.WebSearch) return null;

        var home = HomeOf(command.Target);

        if (command.TakesQuery)
        {
            return new SearchScope(
                command.Name, command.Glyph, home, home,
                term => CustomCommandRunner.Substitute(command.Target, term));
        }

        if (home is null) return null;

        var host = new Uri(home).Host;
        var site = SiteCatalog.All.FirstOrDefault(s =>
            host.Equals(s.Domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + s.Domain, StringComparison.OrdinalIgnoreCase));

        return site is null ? null : WebProvider.ScopeFor(site);
    }

    /// <summary>scheme://host of a URL template, ignoring the {query} placeholder.</summary>
    private static string? HomeOf(string target)
    {
        var cleaned = target.Replace(CustomCommand.QueryToken, "", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.Host}/"
            : null;
    }

    private static string BuildTitle(CustomCommand command, string argument)
    {
        if (!command.TakesQuery) return command.Name;

        return argument.Length > 0
            ? $"{command.Name}: {argument}"
            : $"{command.Name}…";
    }

    private string BuildSubtitle(CustomCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.Description)) return Decorate(command, command.Description);

        var what = command.Kind switch
        {
            CustomCommandKind.WebSearch => "Web search",
            CustomCommandKind.Launch => LaunchSummary(command.Target),
            CustomCommandKind.PowerShell => "PowerShell",
            _ => command.Target,
        };

        return Decorate(command, what);
    }

    /// <summary>"Run notepad" for one line, "Workspace · 3 items" for several.</summary>
    private static string LaunchSummary(string target)
    {
        var lines = target.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(l => !l.StartsWith('#'));

        return lines > 1 ? $"Workspace · opens {lines} things" : "Run " + target.Trim();
    }

    /// <summary>Appends the bound chord so the list doubles as a hotkey reference.</summary>
    private static string Decorate(CustomCommand command, string text) =>
        command.HasHotkey
            ? $"{text}  ·  {HotkeyManager.Describe(command.HotkeyModifiers, command.HotkeyVirtualKey)}"
            : text;
}
