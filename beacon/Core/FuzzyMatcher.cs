namespace Beacon.Core;

/// <summary>
/// Subsequence matcher in the Sublime/VS Code mould: every character of the needle must
/// appear in order in the haystack, and the score rewards matches that land on word
/// boundaries so "vsc" beats "vsomethingc" for "Visual Studio Code".
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = int.MinValue;

    /// <summary>
    /// Match quality is tiered, not additive: exact = 1000, prefix = 800-900, subsequence
    /// at most 700. Without tiers, searching "beacon.csproj" surfaced
    /// "Beacon_x_wpftmp.csproj.nuget.g.props" first, because two long consecutive runs in a
    /// long name out-earned a short exact hit.
    ///
    /// Every provider shares this one scale, which is why the numbers are modest. Providers
    /// add small weights (tens) and the file index subtracts path penalties (hundreds). An
    /// earlier version used 100,000 / 50,000 here, which let any file whose name merely
    /// started with the query outrank fixed-score rows like "Open YouTube".
    /// </summary>
    public const int ExactTier = 1_000;
    public const int PrefixTier = 900;

    /// <summary>
    /// The query is a whole later word: "edge" in "Microsoft Edge", "code" in "Visual
    /// Studio Code". Without this tier those counted as scattered subsequence matches and
    /// lost to any folder that happened to be named exactly "Edge".
    /// </summary>
    public const int WordExactTier = 880;
    public const int WordPrefixTier = 860;

    public const int MaxSubsequence = 700;

    private const int BaseCharScore = 10;
    private const int StartOfStringBonus = 18;
    private const int AfterSeparatorBonus = 12;
    private const int CamelHumpBonus = 9;
    private const int ConsecutiveBonus = 6;

    /// <summary>Stops one long adjacent run from dominating the whole score.</summary>
    private const int MaxConsecutiveBonus = 6;

    public static int Score(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        if (string.IsNullOrEmpty(haystack)) return NoMatch;

        if (haystack.Equals(needle, StringComparison.OrdinalIgnoreCase))
            return ExactTier;

        // Among prefix matches, prefer the one with least left over: typing "read" should
        // rank "readme" above "readme-contributing-guidelines".
        if (haystack.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return PrefixTier - Math.Min((haystack.Length - needle.Length) * 2, 100);

        var wordScore = WordStartScore(haystack, needle);
        if (wordScore != NoMatch) return wordScore;

        int score = 0;
        int h = 0;
        int consecutive = 0;
        int firstMatchIndex = -1;

        foreach (var rawNeedleChar in needle)
        {
            // Spaces are separators in the query, not characters to match.
            if (rawNeedleChar == ' ')
            {
                consecutive = 0;
                continue;
            }

            char nc = char.ToLowerInvariant(rawNeedleChar);
            bool matched = false;

            while (h < haystack.Length)
            {
                char hc = haystack[h];

                if (char.ToLowerInvariant(hc) == nc)
                {
                    if (firstMatchIndex < 0) firstMatchIndex = h;

                    int bonus = 0;
                    if (h == 0)
                    {
                        bonus += StartOfStringBonus;
                    }
                    else
                    {
                        char prev = haystack[h - 1];
                        if (IsSeparator(prev)) bonus += AfterSeparatorBonus;
                        else if (char.IsLower(prev) && char.IsUpper(hc)) bonus += CamelHumpBonus;
                    }

                    if (consecutive > 0)
                        bonus += ConsecutiveBonus + Math.Min(consecutive, MaxConsecutiveBonus);

                    score += BaseCharScore + bonus;
                    consecutive++;
                    h++;
                    matched = true;
                    break;
                }

                h++;
                consecutive = 0;
            }

            if (!matched) return NoMatch;
        }

        // Prefer tight matches: penalise long tails and matches that start late.
        score -= Math.Min(haystack.Length - needle.Length, 60) / 3;
        if (firstMatchIndex > 0) score -= Math.Min(firstMatchIndex, 24);

        // A long query can accumulate a large subsequence score; it must never reach the
        // prefix tier, or a scattered match could outrank a real prefix hit.
        return Math.Clamp(score, 1, MaxSubsequence);
    }

    /// <summary>Best score across several searchable fields (title, keywords, path...).</summary>
    public static int ScoreAny(string needle, params string?[] fields)
    {
        int best = NoMatch;
        foreach (var f in fields)
        {
            var s = Score(f, needle);
            if (s > best) best = s;
        }
        return best;
    }

    /// <summary>
    /// Scores the query appearing at the start of any word after the first. Returns
    /// <see cref="NoMatch"/> when it does not.
    /// </summary>
    private static int WordStartScore(string haystack, string needle)
    {
        for (var i = 1; i + needle.Length <= haystack.Length; i++)
        {
            if (!IsSeparator(haystack[i - 1])) continue;
            if (string.Compare(haystack, i, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;

            // How much of that word is still untyped.
            var end = i + needle.Length;
            var wordEnd = end;
            while (wordEnd < haystack.Length && !IsSeparator(haystack[wordEnd])) wordEnd++;

            var leftover = wordEnd - end;
            return leftover == 0 ? WordExactTier : WordPrefixTier - Math.Min(leftover * 2, 60);
        }

        return NoMatch;
    }

    private static bool IsSeparator(char c) =>
        c is ' ' or '-' or '_' or '.' or '/' or '\\' or ':' or '(' or '[';
}
