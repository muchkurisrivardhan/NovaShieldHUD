namespace Beacon.LocalAi;

/// <summary>
/// The Code Ring's actions and the instructions the local model gets for each. It reuses the
/// <see cref="PromptStyle"/> record (only Id, Label, Glyph and Hint are read by the ring; the
/// Sections/Focus fields are unused here). Unlike the prompt ring there is no rules-only
/// fallback: code needs a model, so a missing model is reported instead of guessed.
///
/// Every action returns text that is pasted straight back over the selection, so each system
/// prompt is told to reply with code only — no Markdown fences, nothing outside comments — and
/// the additive actions (Explain, Comment, Tests, Review) keep the original code intact so
/// nothing is lost. Ctrl+Z in the app undoes the paste.
/// </summary>
public static class CodeStyles
{
    public static readonly PromptStyle[] All =
    [
        new("explain", "Explain", "", "What this code does, in plain words", [], ""),
        new("fix", "Fix", "", "Find and fix bugs or errors", [], ""),
        new("comment", "Comment", "", "Add comments and a docstring", [], ""),
        new("simplify", "Simplify", "", "Refactor for readability, same behaviour", [], ""),
        new("types", "Types", "", "Add type annotations", [], ""),
        new("tests", "Tests", "", "Write unit tests for it", [], ""),
        new("regex", "Regex", "", "Describe a match → get a regex", [], ""),
        new("review", "Review", "", "List bugs, risks and smells", [], ""),
    ];

    public static PromptStyle Find(string id) =>
        All.FirstOrDefault(s => s.Id == id) ?? All[0];

    /// <summary>The shared rules every action ends with: output must drop straight into an editor.</summary>
    const string PasteRule =
        " Reply with the result only: no Markdown code fences, no backticks, and no prose outside " +
        "the language's own comments. Match the indentation and style of the input.";

    /// <summary>The local model's instructions for one action.</summary>
    public static string SystemPrompt(PromptStyle style) => style.Id switch
    {
        "explain" =>
            "You explain code. Detect the language. Return the original code UNCHANGED, with a short " +
            "plain-language explanation added as comments at the very top, plus a brief inline comment on " +
            "any genuinely tricky line. Do not change the code or its formatting otherwise." + PasteRule,

        "fix" =>
            "You fix broken code. Detect the language. Find bugs, errors and likely mistakes, and return a " +
            "corrected version that keeps the original intent, structure and style. Put a short comment on " +
            "each line you change saying what was wrong. If nothing is wrong, return the code unchanged with " +
            "one comment at the top saying it looks correct." + PasteRule,

        "comment" =>
            "You document code. Detect the language. Return the code with clear documentation added: a short " +
            "docstring or summary comment for each function, class or block, and brief inline comments where " +
            "the intent is not obvious. Keep the code and its behaviour exactly as given — add nothing but " +
            "comments, using the language's own comment and docstring syntax." + PasteRule,

        "simplify" =>
            "You refactor code for readability. Detect the language. Return a cleaner version that behaves " +
            "identically and keeps the same public interface: clearer names, less repetition, early returns, " +
            "standard idioms. Do not change what the code does." + PasteRule,

        "types" =>
            "You add type annotations. Detect the language. Return the code with precise type hints or " +
            "annotations added to parameters, return values and variables where they help, using the " +
            "language's own type syntax. Change nothing else about the behaviour. If the language has no " +
            "static types, add the types in comments instead." + PasteRule,

        "tests" =>
            "You write unit tests. Detect the language. Return the ORIGINAL code unchanged, then below it a " +
            "set of unit tests for it using the language's usual test framework, covering the main cases and " +
            "the obvious edge cases. Keep the tests focused and runnable." + PasteRule,

        "regex" =>
            "The input is a plain-language description of text to match. Return exactly one regular expression " +
            "that matches it, on its own first line, in a widely supported flavour (PCRE / ECMAScript). On the " +
            "following lines add a short comment breaking down each part of the pattern." + PasteRule,

        "review" =>
            "You review code. Detect the language. Return the ORIGINAL code unchanged, with a short review " +
            "added as a comment block at the very top: a bulleted list of bugs, risks, performance issues and " +
            "style problems, most important first, one line each. If it looks fine, say so in one line." + PasteRule,

        _ => "You help with code. Return the code only." + PasteRule,
    };

    /// <summary>What the model is given: the selection, fenced off as the material to work on.</summary>
    public static string UserMessage(string selection) =>
        "<input>\n" + selection.Trim() + "\n</input>";
}
