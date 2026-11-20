using System.Text.Json;

namespace Beacon.Teach;

/// <summary>The kinds of picture Kai can draw on the screen.</summary>
public enum VisualKind { MindMap, ConceptMap, Timeline, Flow, Cycle, Hierarchy, Compare, Venn, Cutaway, Iso3D }

/// <summary>
/// One thing in a picture. Which fields matter depends on the kind: Parent for mind maps and
/// trees, When for timelines, Group for comparison columns and Venn regions, Shape/At/Size for
/// the parts of a 3D illustration.
/// </summary>
public sealed record VNode(string Id, string Label, string Note, string Parent, string Group, string When, string Shape, double[]? At, double[]? Size);

public sealed record VLink(string From, string To, string Label);

/// <summary>One spoken sentence of the explanation and the nodes it's about.</summary>
public sealed record VStep(string Say, string[] Focus);

/// <summary>
/// A picture to draw and explain, as the AI described it: plain data, checked here, never code.
/// The AI says what to draw; Beacon decides where and how (see <see cref="Sketch"/>).
/// </summary>
public sealed record Visual(VisualKind Kind, string Title, List<VNode> Nodes, List<VLink> Links, List<VStep> Steps)
{
    public const int MaxNodes = 16, MaxSteps = 10;

    /// <summary>
    /// Reads the AI's reply: tolerant of code fences, prose around the JSON, trailing commas,
    /// other field names (text/name for label) and missing pieces. Null when there's nothing usable.
    /// </summary>
    public static Visual? Parse(string reply, VisualKind? force = null)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(reply[start..(end + 1)], new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var kind = force ?? KindFrom(Str(root, "kind", "type")) ?? VisualKind.MindMap;
            var title = Clip(Str(root, "title", "topic", "name"), 60);

            var nodes = new List<VNode>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (var n in ns.EnumerateArray())
                {
                    if (n.ValueKind != JsonValueKind.Object || nodes.Count >= MaxNodes) continue;
                    var label = Clip(Str(n, "label", "text", "name", "title"), 48);
                    if (label.Length == 0) continue;
                    var id = Str(n, "id");
                    if (id.Length == 0 || ids.Contains(id)) id = "n" + (nodes.Count + 1);
                    ids.Add(id);
                    nodes.Add(new VNode(id, label, Clip(Str(n, "note", "description", "detail"), 140), Str(n, "parent"),
                        Clip(Str(n, "group", "set", "column"), 32), Clip(Str(n, "when", "date", "time", "year"), 24),
                        Str(n, "shape").ToLowerInvariant(), Vec(n, "at", "position"), Vec(n, "size")));
                }
            if (nodes.Count == 0) return null;

            // Parents that don't exist are dropped (the node hangs off the root instead).
            nodes = [.. nodes.Select(n => n.Parent.Length > 0 && !ids.Contains(n.Parent) || n.Parent == n.Id ? n with { Parent = "" } : n)];

            var links = new List<VLink>();
            if (root.TryGetProperty("links", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object) continue;
                    var from = Str(l, "from", "source");
                    var to = Str(l, "to", "target");
                    if (ids.Contains(from) && ids.Contains(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                        links.Add(new VLink(from, to, Clip(Str(l, "label", "text"), 28)));
                }

            var steps = new List<VStep>();
            if (root.TryGetProperty("steps", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var s in ss.EnumerateArray())
                {
                    if (steps.Count >= MaxSteps) break;
                    var say = s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : Str(s, "say", "text", "narration");
                    if (say.Trim().Length == 0) continue;
                    string[] focus = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("focus", out var f) && f.ValueKind == JsonValueKind.Array
                        ? [.. f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(ids.Contains)]
                        : [];
                    steps.Add(new VStep(Clip(say.Trim(), 400), focus));
                }
            // No narration given: say each node's note, in order.
            if (steps.Count == 0)
            {
                steps.Add(new VStep(title.Length > 0 ? $"Here's {title}." : "Here's the picture.", []));
                foreach (var n in nodes.Take(MaxSteps - 1))
                    steps.Add(new VStep(n.Note.Length > 0 ? $"{n.Label}: {n.Note}" : n.Label + ".", [n.Id]));
            }
            if (title.Length == 0) title = nodes[0].Label;
            return new Visual(kind, title, nodes, links, steps);
        }
    }

    /// <summary>The kind the question asks for, if it names one ("mind map of...", "timeline of...").</summary>
    public static VisualKind? Guess(string question)
    {
        var q = " " + question.ToLowerInvariant() + " ";
        bool Has(params string[] words) => words.Any(w => q.Contains(w, StringComparison.Ordinal));
        if (Has("mind map", "mindmap", "mind-map", "brainstorm")) return VisualKind.MindMap;
        if (Has("concept map", "concept-map", "relationship", "how are", "related", "connected", "ecosystem")) return VisualKind.ConceptMap;
        if (Has("timeline", "time line", "history of", "evolution of", "chronolog", "when did", "over the years")) return VisualKind.Timeline;
        if (Has("cutaway", "cut-away", "cut away", "cross section", "cross-section", "inside of", "what's inside", "whats inside", "layers of", "anatomy of")) return VisualKind.Cutaway;
        if (Has(" 3d ", "3-d", "three-d", "three d", "isometric", "3d illustration", "in 3d", "illustration of")) return VisualKind.Iso3D;
        if (Has("venn", "in common", "overlap", "similarities and differences")) return VisualKind.Venn;
        if (Has("compare", "comparison", " vs ", " vs. ", "versus", "difference between", "differences between", "pros and cons")) return VisualKind.Compare;
        if (Has("cycle", "life cycle", "lifecycle", "loop", "circular")) return VisualKind.Cycle;
        if (Has("hierarchy", "org chart", "organisation chart", "organization chart", "classification", "types of", "family tree", "tree of")) return VisualKind.Hierarchy;
        if (Has("process", "steps", "step by step", "how does", "how do", "how to", "workflow", "pipeline", "flow")) return VisualKind.Flow;
        return null;
    }

    public static string Name(VisualKind kind) => kind switch
    {
        VisualKind.MindMap => "mindmap",
        VisualKind.ConceptMap => "conceptmap",
        VisualKind.Timeline => "timeline",
        VisualKind.Flow => "flow",
        VisualKind.Cycle => "cycle",
        VisualKind.Hierarchy => "hierarchy",
        VisualKind.Compare => "compare",
        VisualKind.Venn => "venn",
        VisualKind.Cutaway => "cutaway",
        _ => "3d",
    };

    /// <summary>A kind from the AI's word for it, including near misses.</summary>
    public static VisualKind? KindFrom(string word)
    {
        var w = new string(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return w switch
        {
            "mindmap" or "mind" or "radial" => VisualKind.MindMap,
            "conceptmap" or "concept" or "relationship" or "relationships" or "network" or "graph" => VisualKind.ConceptMap,
            "timeline" or "chronology" or "history" => VisualKind.Timeline,
            "flow" or "flowchart" or "process" or "steps" or "sequence" or "pipeline" => VisualKind.Flow,
            "cycle" or "lifecycle" or "loop" => VisualKind.Cycle,
            "hierarchy" or "tree" or "orgchart" or "classification" => VisualKind.Hierarchy,
            "compare" or "comparison" or "table" or "columns" or "versus" => VisualKind.Compare,
            "venn" or "overlap" => VisualKind.Venn,
            "cutaway" or "crosssection" or "layers" or "section" => VisualKind.Cutaway,
            "3d" or "iso3d" or "isometric" or "illustration" or "3dillustration" => VisualKind.Iso3D,
            _ => null,
        };
    }

    static string Str(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => (v.GetString() ?? "").Trim(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => "",
                };
        return "";
    }

    static double[]? Vec(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                var nums = v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray();
                if (nums.Length == 3 && nums.All(double.IsFinite)) return [.. nums.Select(x => Math.Clamp(x, -50, 50))];
            }
        return null;
    }

    static string Clip(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}
