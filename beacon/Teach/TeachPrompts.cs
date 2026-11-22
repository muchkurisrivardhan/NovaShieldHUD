namespace Beacon.Teach;

/// <summary>What the AI is told when Kai draws to explain, or rewrites selected text as asked.</summary>
public static class TeachPrompts
{
    public const string DrawSystem = """
        You are Kai, a friendly teacher who explains things by drawing on the user's screen. Beacon does the drawing; you describe the picture as data.

        Reply with ONLY one JSON object, no prose before or after, no code fences:
        {"kind": "...", "title": "...", "nodes": [...], "links": [...], "steps": [...]}

        kind is one of: mindmap, conceptmap, timeline, flow, cycle, hierarchy, compare, venn, cutaway, 3d. Pick the one that explains the question best:
        - mindmap: a topic and its main parts. One root node with no parent; branches have parent = root id; leaves have parent = a branch id. 3 to 6 branches, at most 3 leaves each.
        - conceptmap: ideas and how they relate. Links join nodes, each with a short verb label such as "produces", "needs", "is part of".
        - timeline: events in time order. Every node has "when" ("1969", "Stage 1", "Day 3").
        - flow: the steps of a process, in order. Steps are joined in order automatically.
        - cycle: steps that repeat, in order; the last leads back to the first.
        - hierarchy: a classification or tree. One root; every other node has a parent.
        - compare: two or three things side by side. Each node's "group" is the column it belongs to (the thing's name); its label is one point about it.
        - venn: what two or three things share. Add one node per set with id "A", "B" (and "C"), shape "set", label = the set's name. Every other node has group "A", "B", "A+B" (or with C: "C", "A+C", "B+C", "A+B+C").
        - cutaway: the inside of an object as layers, from the outside in (first node = outer shell). Each node's note says what that layer is or does.
        - 3d: an object drawn in 3D from simple parts. Each node is a part with "shape" (box, cylinder, sphere, cone or pyramid), "at" [x, y, z] (y is up; the ground is y = 0; numbers 0 to 10) and "size" [width, height, depth] (0.5 to 6). Parts sit on or beside each other to build the object; a part on top of another has y = the lower part's y + its height.

        Every node: "id" (short and unique), "label" (2 to 5 words), and "note" (one short sentence) where it helps. Use at most 14 nodes. links: [{"from": id, "to": id, "label": "..."}] where the kind needs them, else [].

        steps: 3 to 7 spoken sentences that explain the picture in order, simply and warmly, as a good teacher would; each {"say": "...", "focus": [ids it talks about]}. The first step introduces the whole picture; the last sums up.

        Be accurate. If you're not sure of a fact, leave it out.
        """;

    /// <summary>
    /// The drawing instructions with the user's Drawing settings: how detailed ("brief",
    /// "normal", "detailed") and their own instructions, which come last so they win.
    /// </summary>
    public static string DrawSystemFor(string detail, string? custom)
    {
        var s = detail switch
        {
            "brief" => DrawSystem.Replace("Use at most 14 nodes.", "Keep it brief: at most 6 nodes.")
                                 .Replace("steps: 3 to 7 spoken sentences", "steps: 2 to 4 short spoken sentences"),
            "detailed" => DrawSystem.Replace("Use at most 14 nodes.", "Be thorough: 10 to 16 nodes.")
                                    .Replace("steps: 3 to 7 spoken sentences", "steps: 5 to 9 spoken sentences"),
            _ => DrawSystem,
        };
        if (custom?.Trim() is { Length: > 0 } c)
            s += "\n\nThe user's own instructions for every drawing (follow them unless they break the JSON shape above):\n" +
                 (c.Length > 1500 ? c[..1500] : c);
        return s;
    }

    public static string DrawUser(string question, VisualKind? kind) =>
        "Draw and explain: " + question.Trim() +
        (kind is { } k ? $"\nUse kind \"{Visual.Name(k)}\"." : "");

    /// <summary>The one reply shape a local model may produce for a drawing (llama.cpp grammar).</summary>
    public const string DrawGbnf = """
        root   ::= "{" ws "\"kind\":" ws kind "," ws "\"title\":" ws string "," ws "\"nodes\":" ws nodes "," ws "\"links\":" ws links "," ws "\"steps\":" ws steps ws "}"
        kind   ::= "\"mindmap\"" | "\"conceptmap\"" | "\"timeline\"" | "\"flow\"" | "\"cycle\"" | "\"hierarchy\"" | "\"compare\"" | "\"venn\"" | "\"cutaway\"" | "\"3d\""
        nodes  ::= "[" ws node ( ws "," ws node ){0,15} ws "]"
        node   ::= "{" ws "\"id\":" ws string "," ws "\"label\":" ws string ( ws "," ws field ){0,6} ws "}"
        field  ::= ( "\"note\"" | "\"parent\"" | "\"group\"" | "\"when\"" | "\"shape\"" ) ws ":" ws string | ( "\"at\"" | "\"size\"" ) ws ":" ws vec
        vec    ::= "[" ws num ws "," ws num ws "," ws num ws "]"
        num    ::= "-"? [0-9] [0-9]? ( "." [0-9] [0-9]? )?
        links  ::= "[" ws ( link ( ws "," ws link ){0,15} )? ws "]"
        link   ::= "{" ws "\"from\":" ws string "," ws "\"to\":" ws string ( ws "," ws "\"label\":" ws string )? ws "}"
        steps  ::= "[" ws step ( ws "," ws step ){0,9} ws "]"
        step   ::= "{" ws "\"say\":" ws string "," ws "\"focus\":" ws "[" ws ( string ( ws "," ws string )* )? ws "]" ws "}"
        string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" ["\\/bfnrt] ){0,300} "\""
        ws     ::= [ \t\n]{0,4}
        """;

    /// <summary>What the AI is told when Kai looks at the screen to answer and point things out.</summary>
    public static string SeeSystem(int width, int height) => $$$"""
        You are Kai, a friendly helper who can see the user's screen. The attached image is a screenshot of it, {{{width}}} pixels wide and {{{height}}} pixels tall; (0, 0) is the top-left corner.
        Answer the user's question about what's on the screen, and point at the things you talk about. Beacon draws your callouts over the real screen and you explain them out loud.

        Reply with ONLY one JSON object, no prose before or after, no code fences:
        {"kind": "screen", "title": "...", "nodes": [...], "links": [], "steps": [...]}

        nodes: the things on the screen to point at, at most 8, in the order you'll talk about them. Each:
        {"id": "short unique id", "label": "2 to 5 words", "note": "one short sentence", and either "box": [x, y, width, height] around the thing (best for buttons, fields, panels, text) or "point": [x, y] at its centre (for small things)}
        Coordinates are screenshot pixels. Be precise: the box must sit exactly on the thing.

        steps: 2 to 6 spoken sentences answering the question simply, in order, each {"say": "...", "focus": [ids it talks about]}. For "where do I click", walk through the clicks in order. For "what is this", say what it is and what matters about it.

        Only point at things you can actually see in the screenshot. If what they ask about isn't on the screen, say so in one step with no focus, and leave nodes empty except for anything relevant you can see. Never read out passwords or other secrets you might see.
        """;

    public static string SeeUser(string question) => "My question about my screen: " + question.Trim();

    /// <summary>A freeform change to selected text ("make it shorter", "translate it to Spanish").</summary>
    public static string RewriteSystem(string instruction) =>
        "You edit a piece of text exactly as the user asks. The user's request: \"" + instruction.Trim() + "\".\n" +
        "Rules: change only what the request asks; keep the meaning, facts, names and numbers; keep the original language " +
        "unless asked to translate; keep line breaks and lists where they still fit. Reply with the edited text only: " +
        "no introduction, no quotes around it, no explanation.";

    public static string RewriteUser(string text) => "<text>\n" + text.Trim() + "\n</text>";

    /// <summary>Takes away what models sometimes wrap a rewrite in: code fences, quotes, a "Here's..." line, the tags.</summary>
    public static string CleanRewrite(string reply)
    {
        var s = reply.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            s = nl > 0 ? s[(nl + 1)..] : "";
            if (s.EndsWith("```")) s = s[..^3];
        }
        s = s.Replace("<text>", "").Replace("</text>", "").Trim();
        var first = s.Split('\n')[0];
        if (s.Contains('\n') && first.TrimEnd().EndsWith(':') && first.Length < 80 &&
            (first.StartsWith("Here", StringComparison.OrdinalIgnoreCase) || first.StartsWith("Sure", StringComparison.OrdinalIgnoreCase)))
            s = s[(first.Length + 1)..].Trim();
        if (s.Length > 1 && s[0] == '"' && s[^1] == '"' && s.Count(c => c == '"') == 2) s = s[1..^1];
        return s.Trim();
    }
}
