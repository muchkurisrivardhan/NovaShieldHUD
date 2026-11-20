using System.Text.RegularExpressions;

namespace Beacon.Teach;

/// <summary>
/// Spotting, in what was said to Nova, the two requests that don't go through the normal
/// conversation: "rewrite the selected text" and "explain/draw something". Plain .NET.
/// </summary>
public static partial class Intents
{
    /// <summary>
    /// A rewrite of the selected text. StyleId: a prompt-ring style to apply ("clean" for fix-it).
    /// Instruction: a freeform change ("make it shorter"). AskStyle: show the ring to choose.
    /// </summary>
    public sealed record Rewrite(string? StyleId, string? Instruction, bool AskStyle);

    public sealed record Explain(string Question, VisualKind? Kind);

    // "it", "this", "the selected text", "my paragraph", "what I selected"...
    const string Target = @"(?:it|this|that|these|the\s+(?:selected\s+)?(?:text|selection|sentence|sentences|paragraph|paragraphs|line|lines|email|message|prompt|note|words)|my\s+(?:text|selection|sentence|paragraph|email|message|prompt|writing|words)|(?:the\s+)?selection|what\s+i\s+(?:selected|wrote|highlighted))";

    [GeneratedRegex(@"^(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?(?:kai\s*,?\s*|nova\s*,?\s*)?(?:fix|correct|clean\s+up|proofread|tidy(?:\s+up)?)\s+" + Target + @"(?:\s+up)?(?:\s+for\s+me)?(?:\s+please)?[.!?]*$|^(?:please\s+)?(?:fix|correct)\s+(?:the\s+)?(?:grammar|spelling|typos|punctuation)(?:\s+(?:in|of)\s+" + Target + @")?[.!?]*$|^clean\s+" + Target + @"\s+up[.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex FixRx();

    [GeneratedRegex(@"^(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?(?:rewrite|re-write|rephrase|reword|improve|polish|redo)\s+" + Target + @"(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RewriteRx();

    [GeneratedRegex(@"^(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?(?:make|turn|change|convert|translate|summari[sz]e|shorten|expand|simplify)\s+" + Target + @"\b(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeRx();

    [GeneratedRegex(@"\b(?:into|as)\s+(?:a|an)?\s*(?<kind>[a-z ]+?)\s*prompt\b", RegexOptions.IgnoreCase)]
    private static partial Regex AsPromptRx();

    /// <summary>A rewrite request, or null.</summary>
    public static Rewrite? FindRewrite(string said)
    {
        var s = said.Trim().TrimEnd('.', '!', '?').Trim();
        if (s.Length == 0) return null;
        if (FixRx().IsMatch(s)) return new Rewrite("clean", null, false);

        var m = RewriteRx().Match(s);
        if (m.Success)
        {
            var rest = m.Groups["rest"].Value.Trim();
            if (StyleFor(rest) is { } style) return new Rewrite(style, null, false);
            // "rewrite it" alone: choose a style on the ring. "rewrite it shorter / for my boss": do as told.
            return rest.Length < 3 || rest is "for me" or "please" ? new Rewrite(null, null, true) : new Rewrite(null, said.Trim(), false);
        }

        m = ChangeRx().Match(s);
        if (m.Success)
        {
            // "Make it louder", "make it full screen": the PC, not the text.
            if (Regex.IsMatch(s, @"\b(louder|quieter|softer|brighter|dimmer|darker|lighter|bigger|smaller|full\s*screen|faster|slower|mute[d]?|unmute[d]?|stop|play|pause)\b", RegexOptions.IgnoreCase))
                return null;
            if (StyleFor(m.Groups["rest"].Value) is { } style) return new Rewrite(style, null, false);
            if (Regex.IsMatch(s, @"\bprompt\b", RegexOptions.IgnoreCase)) return new Rewrite(null, null, true);
            return m.Groups["rest"].Value.Trim().Length > 0 || !s.StartsWith("make", StringComparison.OrdinalIgnoreCase)
                ? new Rewrite(null, said.Trim(), false)
                : null;
        }
        return null;
    }

    /// <summary>A prompt-ring style named in the request ("as a research prompt", "into a bug report").</summary>
    static string? StyleFor(string rest)
    {
        var r = " " + rest.ToLowerInvariant() + " ";
        if (AsPromptRx().Match(r) is { Success: true } p) r = " " + p.Groups["kind"].Value + " prompt ";
        else if (!Regex.IsMatch(r, @"\b(prompt|bug report|bug|as a|as an|into a|into an)\b")) return null;
        if (Regex.IsMatch(r, @"\b(bug|error|fix|broken|issue)\b")) return "fix";
        if (Regex.IsMatch(r, @"\b(build|app|feature|code|coding|website|site)\b")) return "build";
        if (Regex.IsMatch(r, @"\b(workflow|automation|automate)\b")) return "workflow";
        if (Regex.IsMatch(r, @"\b(research|search|compare)\b")) return "research";
        if (Regex.IsMatch(r, @"\b(write|writing|email|post|letter|article)\b")) return "write";
        if (Regex.IsMatch(r, @"\b(explain|learn|teach)\b")) return "explain";
        if (Regex.IsMatch(r, @"\b(plan|planning|decide|decision)\b")) return "plan";
        if (Regex.IsMatch(r, @"\b(clean|grammar|tidy)\b")) return "clean";
        return null;
    }

    [GeneratedRegex(@"^(?:(?:can|could|would|will)\s+you\s+)?(?:please\s+)?(?:explain|teach\s+me|walk\s+me\s+through|draw|sketch|illustrate|visuali[sz]e|diagram|map\s+out|show\s+me\s+how|show\s+me\s+(?:a|an|the)\s+(?:mind\s*map|concept\s+map|timeline|diagram|cutaway|3d|picture|drawing))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExplainStartRx();

    [GeneratedRegex(@"\b(?:mind\s*-?\s*map|concept\s+map|timeline|cutaway|cut-away|cross[\s-]section|3d\s+(?:illustration|model|drawing|picture|view)|isometric|venn\s+diagram|flow\s*chart|diagram\s+of|draw\s+(?:me\s+)?(?:a|an|the)|on\s+my\s+screen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VisualWordRx();

    [GeneratedRegex(@"^how\s+(?:does|do|did)\s+.+\s+work\b", RegexOptions.IgnoreCase)]
    private static partial Regex HowWorksRx();

    /// <summary>A request to be taught with a drawing, or null (a normal chat or command).</summary>
    public static Explain? FindExplain(string said)
    {
        var s = said.Trim();
        if (s.Length < 6) return null;
        // Email and inbox questions are the conversation's (it has the tools to read them).
        if (Regex.IsMatch(s, @"\b(e-?mails?|inbox|mail)\b", RegexOptions.IgnoreCase)) return null;
        if (ExplainStartRx().IsMatch(s) || VisualWordRx().IsMatch(s) || HowWorksRx().IsMatch(s))
            return new Explain(s, Visual.Guess(s));
        return null;
    }

    [GeneratedRegex(@"\b(?:on\s+(?:my|the|this)\s+screen|my\s+screen|this\s+screen|screen\s+(?:shot|is\s+showing)|what\s+am\s+i\s+looking\s+at|look\s+at\s+(?:this|that|my|the)|point\s+(?:at|to|out)|show\s+me\s+where|highlight\s+(?:the|where)|where\s+(?:do|can|should|would)\s+i\s+(?:click|find|go|press|tap|type|change|turn|set|put)|where(?:'s|\s+is)\s+the\s+\w+\s+(?:button|icon|setting|option|menu|tab|link|field)|(?:what(?:'s|\s+is|\s+does)|explain|read)\s+(?:this|that|these)\s+(?:button|icon|chart|graph|page|window|error|message|code|diagram|setting|menu|table|form|app|site|website|tab|dialog|pop-?up|screen|warning|option|toolbar|panel|report|slide|thing)|what\s+(?:is|does)\s+this\s+(?:mean|do|say|show)|help\s+me\s+with\s+this|what(?:'s|\s+is)\s+this\s*\??$|explain\s+(?:this|what's\s+on|what\s+is\s+on)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenRx();

    /// <summary>A question about what's on the screen: Kai looks, then points things out.</summary>
    public static Explain? FindScreen(string said)
    {
        var s = said.Trim();
        if (s.Length < 5 || Regex.IsMatch(s, @"\b(e-?mails?|inbox)\b", RegexOptions.IgnoreCase)) return null;
        // "Fix this", "rewrite this": the selected text, not the screen.
        if (FindRewrite(s) is not null) return null;
        return ScreenRx().IsMatch(s) ? new Explain(s, VisualKind.Screen) : null;
    }

    /// <summary>"Close it", "thanks, got it": the drawing can go.</summary>
    public static bool IsCloseBoard(string said) =>
        Regex.IsMatch(said.Trim(), @"^(?:ok(?:ay)?[, ]+)?(?:close(?:\s+(?:it|that|the\s+(?:drawing|board|picture)))?|clear(?:\s+(?:it|the\s+screen|my\s+screen))?|got\s+it|thanks?(?:\s+you)?(?:[, ]+got\s+it)?|done|that'?s\s+(?:enough|all|it))[.!]*$", RegexOptions.IgnoreCase);
}
