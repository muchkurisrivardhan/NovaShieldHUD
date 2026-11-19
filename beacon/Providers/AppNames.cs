using Beacon.Core;

namespace Beacon.Providers;

/// <summary>
/// Finds an installed app from what someone typed or said, without any training or list of
/// names to keep: the app list itself is the vocabulary. Three passes, strictest first:
///
///   1. The launcher's own fuzzy score, at word-start tier or better ("code" finds Visual
///      Studio Code; a stray "pin" inside "Snipping Tool" doesn't).
///   2. Letters only, spaces and punctuation ignored: dictation splits and joins words
///      freely, so "free l l m", "freellm" and "Free-LLM" are all "freellm", "tail scale" is
///      "Tailscale" and "note pad plus plus" reaches "Notepad++" via "notepadplusplus".
///   3. One or two letters off, for a name the speech model misheard ("tailscail",
///      "notio"): only for names long enough that a near miss can't be another app.
/// </summary>
public static class AppNames
{
    public static string? Best(IReadOnlyList<AppEntry> apps, string spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken) || apps.Count == 0) return null;

        var best = apps
            .Select(a => (a.Name, Score: FuzzyMatcher.Score(a.Name, spoken)))
            .Where(x => x.Score != FuzzyMatcher.NoMatch)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (best.Name is not null && best.Score >= FuzzyMatcher.WordPrefixTier) return best.Name;

        var want = Compact(spoken);
        if (want.Length < 3) return null;

        // Exact, then the app's name starting with what was said ("freellm" -> "Free LLM API"),
        // shortest name first so "notion" picks "Notion" over "Notion Calendar".
        var byLetters = apps
            .Select(a => (a.Name, Key: Compact(a.Name)))
            .Where(x => x.Key.Length > 0)
            .ToList();
        var exact = byLetters.FirstOrDefault(x => x.Key == want);
        if (exact.Name is not null) return exact.Name;
        var prefix = byLetters.Where(x => x.Key.StartsWith(want, StringComparison.Ordinal))
            .OrderBy(x => x.Key.Length).FirstOrDefault();
        if (prefix.Name is not null) return prefix.Name;

        // Misheard: compare with the whole name and with its start, the length of what was said.
        var allowed = want.Length >= 9 ? 2 : want.Length >= 5 ? 1 : 0;
        if (allowed == 0) return null;
        var close = byLetters
            .Select(x => (x.Name, x.Key, Distance: Math.Min(
                Distance(want, x.Key),
                x.Key.Length > want.Length ? Distance(want, x.Key[..want.Length]) + 1 : int.MaxValue)))
            .Where(x => x.Distance <= allowed)
            .OrderBy(x => x.Distance).ThenBy(x => x.Key.Length)
            .ToList();
        // Two apps equally close: guessing would open the wrong one.
        if (close.Count == 0 || (close.Count > 1 && close[1].Distance == close[0].Distance && close[1].Key != close[0].Key))
            return null;
        return close[0].Name;
    }

    /// <summary>Lower-case letters and digits only; "+" reads as "plus" (Notepad++, C++).</summary>
    public static string Compact(string text)
    {
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) chars.Append(c);
            else if (c == '+') chars.Append("plus");
        }
        return chars.ToString();
    }

    /// <summary>Optimal string alignment distance (a swap of two letters counts as one).</summary>
    public static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return int.MaxValue / 2;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
}
