using System.Diagnostics;
using System.IO;
using Beacon.Core;

namespace Beacon.Providers;

/// <summary>Shared entry point so the AI layer can search files without knowing the backend.</summary>
public interface IFileSearch
{
    Task<IReadOnlyList<FileEntry>> SearchAsync(string term, int max, CancellationToken ct);

    /// <summary>Null when everything is fine; otherwise a hint to show the user.</summary>
    string? DegradedHint { get; }
}

/// <summary>
/// File results, preferring Everything and degrading to the local index.
///
/// The backend choice is re-evaluated per query rather than pinned at startup, so starting
/// Everything mid-session upgrades results without restarting Beacon.
/// </summary>
public sealed class FileProvider : IResultProvider, IFileSearch
{
    private readonly EverythingClient _everything;
    private readonly FileIndex _fallback;
    private readonly UsageStats _stats;

    private bool _everythingSeen;

    public FileProvider(EverythingClient everything, FileIndex fallback, UsageStats stats)
    {
        _everything = everything;
        _fallback = fallback;
        _stats = stats;
    }

    public string Name => "Files";

    /// <summary>Below apps and commands — file hits are the long tail.</summary>
    public int Weight => -10;

    public string? DegradedHint
    {
        get
        {
            if (_everythingSeen) return null;

            if (_fallback.IsBuilding)
                return "Building the file index… file results will be incomplete until it finishes.";

            if (!_fallback.IsReady) return "File index not built yet.";

            return $"{_fallback.Count:N0} files indexed · " +
                   $"install Everything for live, instant results";
        }
    }

    public async Task<IReadOnlyList<FileEntry>> SearchAsync(
        string term, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return [];

        var hits = await _everything.SearchAsync(term, max, ct).ConfigureAwait(false);

        if (_everything.LastProbeSucceeded)
        {
            _everythingSeen = true;
            return hits.Select(h => new FileEntry(h.Name, h.FullPath, h.IsFolder)).ToList();
        }

        _everythingSeen = false;

        // The index does its own parallel scoring; at ~427k entries a serial scan here
        // would cost over 100ms per keystroke.
        return _fallback.Search(term, max, ct);
    }

    public async Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty || query.Text.Length < 2)
            return [];

        var entries = await SearchAsync(query.Text, 12, ct).ConfigureAwait(false);
        var results = new List<SearchResult>(entries.Count);

        foreach (var entry in entries)
        {
            var captured = entry;

            // Keep the index's score: it includes the path penalty that ranks a hit inside
            // node_modules or AppData below the same name in a real project folder.
            // Re-scoring by name alone here used to discard that.
            var score = entry.Score != 0 ? entry.Score : FuzzyMatcher.Score(entry.Name, query.Text);
            if (score == FuzzyMatcher.NoMatch) score = 0;

            results.Add(new SearchResult
            {
                Title = entry.Name,
                Subtitle = Shorten(entry.IsFolder ? entry.FullPath : Path.GetDirectoryName(entry.FullPath) ?? ""),
                CopyText = entry.FullPath,
                Source = entry.IsFolder ? "Folder" : "File",
                Glyph = entry.IsFolder ? "" : "",
                IconPath = entry.IsFolder ? null : entry.FullPath,
                Score = score + Weight + _stats.BoostFor("file:" + entry.FullPath),
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    _stats.Record("file:" + captured.FullPath);
                    OpenInShell(captured.FullPath, ctx.Notify);
                    return Task.CompletedTask;
                },
            });
        }

        return results;
    }

    public static void OpenInShell(string path, Action<string> notify)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            notify($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    public static void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    /// <summary>Middle-ellipsis so both the drive and the leaf folder stay visible.</summary>
    private static string Shorten(string path, int max = 64)
    {
        if (path.Length <= max) return path;
        var keep = (max - 3) / 2;
        return path[..keep] + "..." + path[^keep..];
    }
}
