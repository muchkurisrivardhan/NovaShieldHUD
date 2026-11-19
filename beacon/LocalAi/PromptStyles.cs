using System.Text;
using System.Text.RegularExpressions;

namespace Beacon.LocalAi;

/// <summary>One slice of the prompt ring: what the prompt is for, and the shape it gets.</summary>
/// <param name="Sections">Headings of the rewritten prompt, in order. Empty for Clean up.</param>
public sealed record PromptStyle(string Id, string Label, string Glyph, string Hint, string[] Sections, string Focus);

/// <summary>
/// The prompt ring's styles and the instructions the local model gets for each.
///
/// The shapes follow Anthropic's published advice on prompting Claude: say plainly what you
/// want and why (context and motivation), be specific about the result you expect and how
/// you'll know it's done, structure the prompt in labelled sections, and state constraints as
/// what to do rather than what not to. A good prompt for building something looks different
/// from one for research or for a fix, which is why there is a ring of them rather than one
/// "improve" button.
/// </summary>
public static partial class PromptStyles
{
    public static readonly PromptStyle[] All =
    [
        new("build", "Build", "", "An app, a feature, a page, a script",
            ["Goal", "Context", "Requirements", "Constraints", "Done when"],
            "Goal: what to build and why, in one or two sentences. Context: who it is for, where it runs, what exists already " +
            "(stack, files, tools). Requirements: one bullet per thing it must do. Constraints: limits to respect " +
            "(budget, platform, style, things to keep). Done when: how the person will check the result."),
        new("fix", "Fix", "", "A bug, an error, something broken",
            ["Problem", "Expected", "Where and when", "Already tried", "What I need back"],
            "Problem: what goes wrong, with any error message quoted exactly. Expected: what should happen instead. " +
            "Where and when: the file, screen, steps or situation where it happens. Already tried: what was attempted. " +
            "What I need back: the fix, an explanation, or both."),
        new("workflow", "Workflow", "", "Automate steps, connect tools",
            ["Goal", "Trigger", "Steps", "Inputs and outputs", "Tools", "Edge cases"],
            "Goal: what the workflow achieves. Trigger: what starts it (a time, an event, a file). Steps: numbered, in order. " +
            "Inputs and outputs: what goes in and what comes out, and where. Tools: apps or services involved. " +
            "Edge cases: what to do when something fails or is missing."),
        new("research", "Research", "", "Find out, compare, look into",
            ["Question", "Why I need it", "Scope", "Sources", "Answer format"],
            "Question: the exact question. Why I need it: the decision or task it feeds. Scope: time period, region, depth. " +
            "Sources: which to prefer or avoid. Answer format: summary, table, list of options with pros and cons, citations."),
        new("write", "Write", "", "An email, a post, a document",
            ["Purpose", "Audience", "Key points", "Tone", "Length and format"],
            "Purpose: what the text must achieve. Audience: who reads it and what they know. Key points: one bullet each, " +
            "all of them. Tone: formal, friendly, persuasive... Length and format: words, paragraphs, subject line, bullets."),
        new("explain", "Explain", "", "Learn or understand something",
            ["Topic", "What I already know", "What confuses me", "How to explain"],
            "Topic: what to explain. What I already know: the person's level. What confuses me: the specific gap. " +
            "How to explain: depth, examples, analogies, step by step, length."),
        new("plan", "Plan", "", "Decide, plan, compare options",
            ["Goal", "Situation", "Options", "Constraints", "Deliverable"],
            "Goal: what the plan or decision is for. Situation: the facts as they stand. Options: the choices being weighed, " +
            "if any. Constraints: time, money, people, must-haves. Deliverable: a plan with steps, a recommendation, a timeline."),
        new("clean", "Clean up", "", "Just fix the dictation",
            [],
            ""),
    ];

    public static PromptStyle Find(string id) =>
        All.FirstOrDefault(s => s.Id == id) ?? All[^1];

    /// <summary>The local model's instructions for one style.</summary>
    public static string SystemPrompt(PromptStyle style)
    {
        if (style.Sections.Length == 0)
            return "You clean up dictated text. Fix grammar, punctuation and capitalisation, remove filler words " +
                   "(um, uh, like, you know, so yeah, I mean), false starts and repeated words, and split run-on " +
                   "sentences. Keep the person's own words, meaning, order and voice; add nothing and leave nothing out. " +
                   "Reply with the cleaned text only.";

        var headings = string.Join("\n", style.Sections.Select(s => "## " + s));
        return
            "You rewrite a person's dictated, rambling request into a clear, well-structured prompt that they will send " +
            "to an AI assistant. You do not answer the request; you only rewrite it.\n\n" +
            "Rules:\n" +
            "- Keep every fact, name, number, example and requirement the person gave. Do not invent any.\n" +
            "- Write as the person, in the first person (\"I want...\", \"My app...\").\n" +
            "- Drop filler words, repetition and false starts.\n" +
            "- Use exactly these Markdown headings, in this order:\n" + headings + "\n" +
            "- What goes under each: " + style.Focus + "\n" +
            "- Use short bullet points under a heading when there are several items.\n" +
            "- When the text gives nothing for a heading, write one short question in square brackets under it, " +
            "so the person knows what to add, for example: [Which platform should it run on?]\n" +
            "- Reply with the rewritten prompt only: no introduction, no explanation, no closing remark.";
    }

    /// <summary>What the model is given: the dictated text, fenced off as material to rewrite.</summary>
    public static string UserMessage(string dictated) =>
        "Rewrite this:\n<dictation>\n" + dictated.Trim() + "\n</dictation>";

    /// <summary>
    /// Without a downloaded model: the same structure, filled from the text as far as rules
    /// can go. The dictation, cleaned of filler, becomes the first section, and every other
    /// section is a bracketed question to answer. Not a rewrite, but a guide to a good prompt.
    /// </summary>
    public static string Template(PromptStyle style, string dictated)
    {
        var text = CleanDictation(dictated);
        if (style.Sections.Length == 0) return text;

        var sb = new StringBuilder();
        for (var i = 0; i < style.Sections.Length; i++)
        {
            sb.Append("## ").AppendLine(style.Sections[i]);
            sb.AppendLine(i == 0 ? text : "[" + Question(style.Id, style.Sections[i]) + "]");
            if (i < style.Sections.Length - 1) sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    static string Question(string style, string section) => (style, section) switch
    {
        (_, "Context") => "Who is it for, where does it run, and what exists already?",
        (_, "Requirements") => "What must it do? One line each.",
        (_, "Constraints") => "Any limits: platform, budget, style, things to keep?",
        (_, "Done when") => "How will you check that it works?",
        (_, "Expected") => "What should happen instead?",
        (_, "Where and when") => "Where does it happen, and what steps lead to it?",
        (_, "Already tried") => "What have you tried so far?",
        (_, "What I need back") => "Do you want the fix, an explanation, or both?",
        (_, "Trigger") => "What starts it: a time, an event, a new file?",
        (_, "Steps") => "What happens, step by step?",
        (_, "Inputs and outputs") => "What goes in, what comes out, and where?",
        (_, "Tools") => "Which apps or services are involved?",
        (_, "Edge cases") => "What should happen when something fails or is missing?",
        (_, "Why I need it") => "What decision or task is this for?",
        (_, "Scope") => "How far back, which region, how deep?",
        (_, "Sources") => "Which sources to prefer or avoid?",
        (_, "Answer format") => "Summary, table, list of options with pros and cons?",
        (_, "Audience") => "Who will read it, and what do they know?",
        (_, "Key points") => "Which points must it make?",
        (_, "Tone") => "Formal, friendly, persuasive?",
        (_, "Length and format") => "How long, and in what form?",
        (_, "What I already know") => "What do you know about it already?",
        (_, "What confuses me") => "Which part is unclear?",
        (_, "How to explain") => "How deep, with examples or step by step?",
        (_, "Situation") => "What are the facts as they stand?",
        (_, "Options") => "Which options are you weighing?",
        (_, "Deliverable") => "A step-by-step plan, a recommendation, a timeline?",
        _ => "Add details",
    };

    /// <summary>Filler words out, spacing and sentence capitals fixed. Words otherwise untouched.</summary>
    public static string CleanDictation(string text)
    {
        var t = Regex.Replace(" " + text.Trim() + " ", @",\s*(?:um+|uh+|erm)\s*,", " ", RegexOptions.IgnoreCase);
        t = Filler().Replace(t, " ");
        t = FillerLike().Replace(t, " ");
        t = Regex.Replace(t, @"^\s*(?:so\s+)?(?:basically|okay so|ok so|so)\b[,\s]*", " ", RegexOptions.IgnoreCase);
        t = Repeats().Replace(t, "$1");
        t = Regex.Replace(t, @"\s+([,.!?;:])", "$1");
        t = Regex.Replace(t, @",\s*,", ",");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        t = Regex.Replace(t, @"^[,.\s]+", "");
        t = Regex.Replace(t, @"(^|[.!?]\s+)([a-z])", m => m.Groups[1].Value + char.ToUpperInvariant(m.Groups[2].Value[0]));
        t = Regex.Replace(t, @"\bi\b", "I");
        if (t.Length > 0 && !".!?".Contains(t[^1])) t += ".";
        return t;
    }

    [GeneratedRegex(@"(?<=[\s,])(?:um+|uh+|erm|uh-huh|you know,?|I mean,?|like,|so yeah,?|kind of like|basically,?)(?=[\s,.])", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    /// <summary>
    /// "like" as filler ("I want like a graph"), not as a verb ("I like it", "would like to")
    /// or a comparison ("looks like", "like this").
    /// </summary>
    [GeneratedRegex(@"(?<!\b(?:i|you|we|they|would|'d|do|don't|does|doesn't|also|really|just|feel|feels|look|looks|seem|seems|sounds|something|anything|nothing)\s)\blike\b(?!\s+(?:to|it|this|that|these|those|them|him|her|me|us)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FillerLike();

    [GeneratedRegex(@"\b(\w+)(?:\s+\1\b)+", RegexOptions.IgnoreCase)]
    private static partial Regex Repeats();
}
