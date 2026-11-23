using System.Text.RegularExpressions;

namespace Beacon.LocalAi;

/// <summary>
/// A quick, offline guess at whether a selection is source code (open the Code Ring) or prose
/// (open the prompt ring). No model is used: it scores a handful of signals — code keywords,
/// symbol density, lines ending in braces or semicolons, indentation, assignment and call
/// shapes — and calls it code once enough of them fire. Tuned so a dictated request
/// ("build me an app that tracks habits") scores near zero while a function or snippet scores
/// well over the threshold. When it is wrong, the forced Code Ring / prompt-ring commands are
/// always there as a fallback.
/// </summary>
public static partial class CodeHeuristics
{
    public static bool LooksLikeCode(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        if (t.Contains("```", StringComparison.Ordinal)) return true;   // a fenced block: certainly code

        var lines = t.Split('\n');
        var score = 0;

        // Symbol density: code is full of punctuation prose rarely uses.
        var symbols = t.Count(c => "{};=<>()[]|&#$\\/*+".Contains(c));
        var density = symbols / (double)t.Length;
        if (density > 0.06) score += 2;
        if (density > 0.12) score += 2;

        // Telltale tokens from common languages.
        string[] keywords =
        [
            "function ", "def ", "class ", "public ", "private ", "protected ", "const ", "let ",
            "var ", "import ", "#include", "using ", "return ", "=>", "->", "::", "===", "!==",
            "void ", "static ", "async ", "await ", "foreach", "println", "console.", "System.",
            "printf", "std::", "self.", "export ", "interface ", "struct ", "enum ",
        ];
        foreach (var k in keywords)
            if (t.Contains(k, StringComparison.Ordinal)) { score++; break; }   // one is enough; avoids over-counting
        var keywordHits = keywords.Count(k => t.Contains(k, StringComparison.Ordinal));
        if (keywordHits >= 2) score++;
        if (keywordHits >= 4) score++;

        // Lines that end like code statements/blocks.
        var blocky = lines.Count(l =>
        {
            var s = l.TrimEnd();
            return s.EndsWith(';') || s.EndsWith('{') || s.EndsWith('}') ||
                   (s.EndsWith(':') && BlockOpener().IsMatch(s));
        });
        if (blocky >= 1) score++;
        if (blocky >= 3) score += 2;

        // Indentation across several lines.
        var indented = lines.Count(l => l.Length > 0 && (l[0] == ' ' || l[0] == '\t'));
        if (lines.Length >= 3 && indented >= 2) score++;

        // Assignment (x = …) and call (name(…)) shapes.
        if (Assignment().IsMatch(t)) score++;
        if (Call().IsMatch(t)) score++;

        return score >= 3;
    }

    [GeneratedRegex(@"\b(def|class|if|elif|else|for|while|try|except|finally|with|switch|case)\b")]
    private static partial Regex BlockOpener();

    [GeneratedRegex(@"(^|\n)\s*[A-Za-z_$][\w$.\[\]]*\s*(?:[-+*/|&]|\?\?)?=(?!=)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"[A-Za-z_$][\w$]*\s*\([^)]*\)")]
    private static partial Regex Call();
}
