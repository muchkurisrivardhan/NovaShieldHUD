namespace Beacon.Teach;

public readonly record struct P(double X, double Y)
{
    public static P operator +(P a, P b) => new(a.X + b.X, a.Y + b.Y);
    public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y);
    public static P operator *(P a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

public enum MarkKind { Line, Polygon, Ellipse, Box, Text }

public enum Align { Center, Left }

/// <summary>
/// One thing to draw. Line: a polyline through Pts (an arrowhead at the end with Arrow).
/// Polygon: closed. Ellipse: Pts[0] the centre, Pts[1] the radii. Box: Pts[0] the top-left,
/// Pts[1] the size. Text: Pts[0] the centre (or the top-left with Align.Left).
///
/// Ink: an index into the picture's colours (0-5, from the accent), or Accent, Text or Muted.
/// Order: the step of the explanation in which it appears. Node: what it belongs to, for
/// highlighting while it's being talked about.
/// </summary>
public sealed record Mark(MarkKind Kind, P[] Pts, int Ink, int Order)
{
    public string? Node { get; init; }
    public string? Text { get; init; }
    public double Size { get; init; } = 15;
    public double MaxWidth { get; init; }
    public bool Bold { get; init; }
    public bool Arrow { get; init; }
    public bool Dashed { get; init; }
    /// <summary>
    /// 0: outline only. Otherwise how strongly the ink fills it. Boxes, and fills of 0.5 or more,
    /// are solid (the ink mixed into the board's surface, hiding what's under them); lighter
    /// fills are see-through, so overlaps show (Venn circles, highlights).
    /// </summary>
    public double Fill { get; init; }
    public double Stroke { get; init; } = 2;
    public double Radius { get; init; } = 10;
    /// <summary>For 3D faces: lighter (positive) or darker (negative).</summary>
    public double Shade { get; init; }
    /// <summary>Text drawn on a small plate, so it reads over lines.</summary>
    public bool Plate { get; init; }
    public Align Align { get; init; } = Align.Center;
}

public sealed record Scene(VisualKind Kind, string Title, double Width, double Height, List<Mark> Marks, int Steps);

/// <summary>
/// Lays a <see cref="Visual"/> out on a screen-sized board, as marks to draw: where every card,
/// branch, arrow, layer and label goes, for each kind of picture. Text sizes are estimated, so
/// the board measures real text when drawing. Plain .NET, no WPF.
/// </summary>
public static class Sketch
{
    public const int Accent = -1, Ink = -2, Muted = -3, OnAccent = -4;
    public const int Colours = 6;

    /// <param name="imageWidth">For Screen pictures: the screenshot's width in pixels, to place callouts on the board.</param>
    public static Scene Layout(Visual v, double width, double height, double imageWidth = 0, double imageHeight = 0)
    {
        var b = new Builder(v, width, height, v.Kind == VisualKind.Screen);
        switch (v.Kind)
        {
            case VisualKind.MindMap: b.MindMap(); break;
            case VisualKind.ConceptMap: b.ConceptMap(); break;
            case VisualKind.Timeline: b.Timeline(); break;
            case VisualKind.Flow: b.Flow(); break;
            case VisualKind.Cycle: b.Cycle(); break;
            case VisualKind.Hierarchy: b.Hierarchy(); break;
            case VisualKind.Compare: b.Compare(); break;
            case VisualKind.Venn: b.Venn(); break;
            case VisualKind.Cutaway: b.Cutaway(); break;
            case VisualKind.Screen: b.Callouts(imageWidth > 0 ? width / imageWidth : 1, imageHeight > 0 ? height / imageHeight : 1); break;
            default: b.Iso3D(); break;
        }
        return new Scene(v.Kind, v.Title, width, height, b.Marks, v.Steps.Count);
    }

    public static double TextWidth(string s, double size) => s.Length * size * 0.53;

    public static int Lines(string s, double size, double maxWidth) =>
        s.Length == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(TextWidth(s, size) / Math.Max(20, maxWidth)));

    sealed class Builder
    {
        readonly Visual _v;
        readonly double _l, _t, _r, _b;
        readonly Dictionary<string, int> _order = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (P C, double W, double H)> _boxes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Mark> Marks = [];

        public Builder(Visual v, double width, double height, bool overScreen = false)
        {
            _v = v;
            // Room for the title above and the caption below; over the screen, almost all of it.
            (_l, _t, _r, _b) = overScreen ? (16, 64, width - 16, height - 96) : (64, 118, width - 64, height - 96);
            for (var i = 0; i < v.Steps.Count; i++)
                foreach (var id in v.Steps[i].Focus)
                    _order.TryAdd(id, i);
        }

        double W => _r - _l;
        double H => _b - _t;
        P Mid => new((_l + _r) / 2, (_t + _b) / 2);

        int Order(string id) => _order.GetValueOrDefault(id, 0);
        int Order(string a, string b) => Math.Max(Order(a), Order(b));

        // ---------------------------------------------------------------- cards

        double CardHeight(VNode n, double w, string? top = null, bool note = true)
        {
            var inner = w - 24;
            var h = 18 + Lines(n.Label, 17, inner) * 22;
            if (note && n.Note.Length > 0) h += 4 + Lines(n.Note, 13, inner) * 17;
            if (top is { Length: > 0 }) h += 20;
            return h;
        }

        /// <summary>A card for a node, centred on c: a plate with its label, and its note under it.</summary>
        double Card(VNode n, P c, double w, int ink, double fill = 0.16, string? top = null, bool note = true, double stroke = 2)
        {
            var h = CardHeight(n, w, top, note);
            var order = Order(n.Id);
            Marks.Add(new Mark(MarkKind.Box, [new(c.X - w / 2, c.Y - h / 2), new(w, h)], ink, order) { Node = n.Id, Fill = fill, Stroke = stroke });
            var y = c.Y - h / 2 + 9;
            if (top is { Length: > 0 })
            {
                Marks.Add(new Mark(MarkKind.Text, [new(c.X, y + 9)], Accent, order) { Node = n.Id, Text = top, Size = 13, Bold = true, MaxWidth = w - 24 });
                y += 20;
            }
            var lh = Lines(n.Label, 17, w - 24) * 22;
            Marks.Add(new Mark(MarkKind.Text, [new(c.X, y + lh / 2)], Ink, order) { Node = n.Id, Text = n.Label, Size = 17, Bold = true, MaxWidth = w - 24 });
            y += lh + 4;
            if (note && n.Note.Length > 0)
            {
                var nh = Lines(n.Note, 13, w - 24) * 17;
                Marks.Add(new Mark(MarkKind.Text, [new(c.X, y + nh / 2)], Muted, order) { Node = n.Id, Text = n.Note, Size = 13, MaxWidth = w - 24 });
            }
            _boxes[n.Id] = (c, w, h);
            return h;
        }

        /// <summary>Where the line from a box's centre toward a point leaves the box (plus a gap).</summary>
        P Edge(string id, P toward, double gap = 6)
        {
            var (c, w, h) = _boxes[id];
            var d = toward - c;
            if (d.Length < 1e-6) return c;
            var t = Math.Min(Math.Abs(d.X) > 1e-6 ? (w / 2 + gap) / Math.Abs(d.X) : double.MaxValue,
                             Math.Abs(d.Y) > 1e-6 ? (h / 2 + gap) / Math.Abs(d.Y) : double.MaxValue);
            return c + d * Math.Min(t, 1);
        }

        /// <summary>An arrow between two cards; with bulge, curved out of the way (below, when positive).</summary>
        void Arrow(string from, string to, int ink, string label = "", bool dashed = false, double bulge = 0)
        {
            var a = _boxes[from].C;
            var b = _boxes[to].C;
            var order = Order(from, to);
            if (bulge != 0)
            {
                // Leave from the bottom (or top) of each card and swing round, clear of the cards between.
                var (_, _, ha) = _boxes[from];
                var (_, _, hb) = _boxes[to];
                var s0 = new P(a.X, a.Y + Math.Sign(bulge) * (ha / 2 + 4));
                var s1 = new P(b.X, b.Y + Math.Sign(bulge) * (hb / 2 + 10));
                var curve = Bezier(s0, s0 + new P(0, bulge), s1 + new P(0, bulge), s1);
                Marks.Add(new Mark(MarkKind.Line, [.. curve], ink, order) { Arrow = true, Stroke = 2.2, Dashed = dashed, Node = to });
                if (label.Length > 0)
                    Marks.Add(new Mark(MarkKind.Text, [curve[curve.Count / 2]], Accent, order) { Text = label, Size = 13, Plate = true, MaxWidth = 150, Node = to });
                return;
            }
            var p0 = Edge(from, b);
            var p1 = Edge(to, a, 10);
            Marks.Add(new Mark(MarkKind.Line, [p0, p1], ink, order) { Arrow = true, Stroke = 2.2, Dashed = dashed, Node = to });
            if (label.Length > 0)
                Marks.Add(new Mark(MarkKind.Text, [(p0 + p1) * 0.5], Accent, order) { Text = label, Size = 13, Plate = true, MaxWidth = 150, Node = to });
        }

        static List<P> Bezier(P p0, P p1, P p2, P p3, int n = 24)
        {
            var pts = new List<P>(n + 1);
            for (var i = 0; i <= n; i++)
            {
                var t = i / (double)n;
                var u = 1 - t;
                pts.Add(p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t));
            }
            return pts;
        }

        static P Polar(P c, double rx, double ry, double deg) =>
            new(c.X + rx * Math.Cos(deg * Math.PI / 180), c.Y + ry * Math.Sin(deg * Math.PI / 180));

        P ClampCard(P c, double w, double h) =>
            new(Math.Clamp(c.X, _l + w / 2, _r - w / 2), Math.Clamp(c.Y, _t + h / 2, _b - h / 2));

        VNode? Root() => _v.Nodes.FirstOrDefault(n => n.Parent.Length == 0);

        // ---------------------------------------------------------------- mind map

        public void MindMap()
        {
            var root = Root() ?? _v.Nodes[0];
            var branches = _v.Nodes.Where(n => n != root && (n.Parent == root.Id || n.Parent.Length == 0)).ToList();
            var leaves = _v.Nodes.Where(n => n != root && !branches.Contains(n)).GroupBy(n => n.Parent).ToDictionary(g => g.Key, g => g.ToList());
            var c = Mid;
            var nb = Math.Max(1, branches.Count);
            double brx = W * 0.27, bry = H * 0.3, lrx = W * 0.43, lry = H * 0.44;
            var lines = new List<Mark>();
            var cards = new List<Action>();

            for (var i = 0; i < branches.Count; i++)
            {
                var br = branches[i];
                var ink = i % Colours;
                var a = -90 + 360.0 * i / nb + (nb % 2 == 0 ? 360.0 / nb / 2 : 0);
                var bw = 190.0;
                var bp = ClampCard(Polar(c, brx, bry, a), bw, CardHeight(br, bw, note: false));
                var mid = (c + bp) * 0.5;
                lines.Add(new Mark(MarkKind.Line, [.. Bezier(c, new P(mid.X, c.Y), new P(mid.X, bp.Y), bp)], ink, Order(br.Id)) { Stroke = 5, Node = br.Id });
                cards.Add(() => Card(br, bp, bw, ink, 0.3, note: false, stroke: 2.5));

                var kids = leaves.GetValueOrDefault(br.Id) ?? [];
                var spread = Math.Min(360.0 / nb * 0.8, 75);
                for (var j = 0; j < kids.Count; j++)
                {
                    var leaf = kids[j];
                    var la = kids.Count == 1 ? a : a + spread * ((double)j / (kids.Count - 1) - 0.5);
                    var lw = 176.0;
                    var lp = ClampCard(Polar(c, lrx, lry, la), lw, CardHeight(leaf, lw));
                    var m2 = (bp + lp) * 0.5;
                    lines.Add(new Mark(MarkKind.Line, [.. Bezier(bp, new P(m2.X, bp.Y), new P(m2.X, lp.Y), lp)], ink, Order(leaf.Id)) { Stroke = 2.5, Node = leaf.Id });
                    cards.Add(() => Card(leaf, lp, lw, ink, 0.08));
                }
            }
            Marks.AddRange(lines);
            foreach (var card in cards) card();
            Card(root, c, 230, Accent, 0.28, stroke: 3);
        }

        // ---------------------------------------------------------------- concept map

        public void ConceptMap()
        {
            var nodes = _v.Nodes;
            var degree = nodes.ToDictionary(n => n.Id, n => _v.Links.Count(l => l.From == n.Id || l.To == n.Id), StringComparer.OrdinalIgnoreCase);
            var hub = nodes.Count >= 5 ? nodes.OrderByDescending(n => degree[n.Id]).First() : null;
            if (hub is not null && degree[hub.Id] < 3) hub = null;
            var ring = nodes.Where(n => n != hub).ToList();
            var w = 190.0;
            var pos = new Dictionary<string, P>();
            if (hub is not null) pos[hub.Id] = Mid;
            for (var i = 0; i < ring.Count; i++)
                pos[ring[i].Id] = ClampCard(Polar(Mid, W * 0.37, H * 0.36, -90 + 360.0 * i / ring.Count), w, CardHeight(ring[i], w));

            // Cards first (to know their sizes), then links under them would hide arrowheads: so links after, trimmed to edges.
            var cardMarks = Marks.Count;
            for (var i = 0; i < nodes.Count; i++)
                Card(nodes[i], pos[nodes[i].Id], nodes[i] == hub ? 220 : w, nodes[i] == hub ? Accent : i % Colours, nodes[i] == hub ? 0.28 : 0.16);
            var cards = Marks.GetRange(cardMarks, Marks.Count - cardMarks);
            Marks.RemoveRange(cardMarks, Marks.Count - cardMarks);
            var links = _v.Links.Count > 0 ? _v.Links : nodes.Skip(1).Select(n => new VLink((hub ?? nodes[0]).Id, n.Id, "")).ToList();
            foreach (var l in links) Arrow(l.From, l.To, Muted, l.Label);
            Marks.AddRange(cards);
            // Link labels go on top of everything.
            var labels = Marks.Where(m => m.Plate).ToList();
            Marks.RemoveAll(m => m.Plate);
            Marks.AddRange(labels);
        }

        // ---------------------------------------------------------------- timeline

        public void Timeline()
        {
            var n = _v.Nodes.Count;
            var axisY = Mid.Y;
            var cw = Math.Clamp(W / n * 1.75, 150, 250);
            Marks.Add(new Mark(MarkKind.Line, [new(_l, axisY), new(_r, axisY)], Accent, 0) { Stroke = 3.5, Arrow = true });
            for (var i = 0; i < n; i++)
            {
                var node = _v.Nodes[i];
                var x = n == 1 ? Mid.X : _l + cw / 2 + (W - cw) * i / (n - 1);
                var ink = i % Colours;
                var order = Order(node.Id);
                var up = i % 2 == 0;
                var h = CardHeight(node, cw, node.When);
                var gap = 56 + (i % 4 >= 2 ? 26 : 0);   // stagger, so neighbours on the same side don't touch
                var cy = up ? axisY - gap - h / 2 : axisY + gap + h / 2;
                cy = Math.Clamp(cy, _t + h / 2, _b - h / 2);
                Marks.Add(new Mark(MarkKind.Line, [new(x, axisY), new(x, up ? cy + h / 2 : cy - h / 2)], ink, order) { Stroke = 2, Node = node.Id });
                Marks.Add(new Mark(MarkKind.Ellipse, [new(x, axisY), new(9, 9)], ink, order) { Fill = 1, Node = node.Id });
                Card(node, new P(x, cy), cw, ink, 0.14, node.When);
            }
        }

        // ---------------------------------------------------------------- flow

        public void Flow()
        {
            var n = _v.Nodes.Count;
            var rows = n <= 5 ? 1 : (int)Math.Ceiling(n / 4.0);
            var perRow = (int)Math.Ceiling(n / (double)rows);
            var gapX = 70.0;
            var cw = Math.Min(250, (W - (perRow - 1) * gapX) / perRow);
            var rowH = H / rows;
            var span = perRow * cw + (perRow - 1) * gapX;
            var badges = new List<Mark>();
            for (var i = 0; i < n; i++)
            {
                var row = i / perRow;
                var col = i % perRow;
                if (row % 2 == 1) col = perRow - 1 - col;   // snake, so the next row starts under this one's end
                var x = Mid.X - span / 2 + cw / 2 + col * (cw + gapX);
                var y = _t + rowH * (row + 0.5);
                var node = _v.Nodes[i];
                var h = Card(node, new P(x, y), cw, i % Colours, 0.15);
                // The step number, on the card's corner.
                var corner = new P(x - cw / 2 + 4, y - h / 2 + 4);
                badges.Add(new Mark(MarkKind.Ellipse, [corner, new(15, 15)], Accent, Order(node.Id)) { Fill = 1, Node = node.Id });
                badges.Add(new Mark(MarkKind.Text, [corner], OnAccent, Order(node.Id)) { Text = (i + 1).ToString(), Size = 14, Bold = true, Node = node.Id });
            }
            for (var i = 0; i + 1 < n; i++) Arrow(_v.Nodes[i].Id, _v.Nodes[i + 1].Id, Accent);
            // Other links (going back, skipping ahead) curve below the row, or above the first row of several.
            foreach (var l in _v.Links.Where(l => !IsConsecutive(l)))
            {
                var row = _v.Nodes.FindIndex(n => n.Id.Equals(l.From, StringComparison.OrdinalIgnoreCase)) / perRow;
                Arrow(l.From, l.To, Muted, l.Label, dashed: true, bulge: rows > 1 && row == 0 ? -rowH * 0.3 : rowH * 0.3);
            }
            Marks.AddRange(badges);   // numbers on top of the arrows
        }

        bool IsConsecutive(VLink l)
        {
            var a = _v.Nodes.FindIndex(n => n.Id.Equals(l.From, StringComparison.OrdinalIgnoreCase));
            var b = _v.Nodes.FindIndex(n => n.Id.Equals(l.To, StringComparison.OrdinalIgnoreCase));
            return b == a + 1;
        }

        // ---------------------------------------------------------------- cycle

        public void Cycle()
        {
            var n = _v.Nodes.Count;
            var w = Math.Clamp(W / Math.Max(3, n) * 1.3, 170, 220);
            double rx = Math.Min(W * 0.36, H * 0.6), ry = H * 0.38;
            var c = Mid;
            var angles = Enumerable.Range(0, n).Select(i => -90 + 360.0 * i / n).ToArray();
            var cardMarks = Marks.Count;
            for (var i = 0; i < n; i++)
                Card(_v.Nodes[i], ClampCard(Polar(c, rx, ry, angles[i]), w, CardHeight(_v.Nodes[i], w)), w, i % Colours, 0.16);
            var cards = Marks.GetRange(cardMarks, Marks.Count - cardMarks);
            Marks.RemoveRange(cardMarks, Marks.Count - cardMarks);

            // Curved arrows round the ring, from each card to the next, the last back to the first.
            var gap = Math.Clamp(Math.Asin(Math.Min(1, (w / 2 + 18) / rx)) * 180 / Math.PI, 8, 360.0 / n / 2 - 4);
            for (var i = 0; i < n; i++)
            {
                var a0 = angles[i] + gap;
                var a1 = angles[i] + 360.0 / n - gap;
                var pts = new List<P>();
                for (var k = 0; k <= 20; k++) pts.Add(Polar(c, rx, ry, a0 + (a1 - a0) * k / 20));
                var next = _v.Nodes[(i + 1) % n];
                Marks.Add(new Mark(MarkKind.Line, [.. pts], Accent, Order(_v.Nodes[i].Id, next.Id)) { Arrow = true, Stroke = 3, Node = next.Id });
            }
            Marks.AddRange(cards);
            Marks.Add(new Mark(MarkKind.Text, [c], Muted, 0) { Text = "↻", Size = 64 });
            Marks.Add(new Mark(MarkKind.Text, [c + new P(0, 52)], Muted, 0) { Text = _v.Title, Size = 16, MaxWidth = rx });
        }

        // ---------------------------------------------------------------- hierarchy

        public void Hierarchy()
        {
            var root = Root() ?? _v.Nodes[0];
            var kids = _v.Nodes.Where(n => n != root).GroupBy(n => n.Parent.Length == 0 ? root.Id : n.Parent)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var leavesUnder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int Count(VNode n, int d)
            {
                if (!seen.Add(n.Id)) return 0;   // a loop in the parents: stop
                depth[n.Id] = d;
                var ks = kids.GetValueOrDefault(n.Id) ?? [];
                var leaves = ks.Count == 0 ? 1 : ks.Sum(k => Count(k, d + 1));
                return leavesUnder[n.Id] = Math.Max(1, leaves);
            }
            var total = Count(root, 0);
            var levels = depth.Values.DefaultIfEmpty(0).Max() + 1;
            var cw = Math.Clamp(W / total - 18, 120, 230);
            var levelGap = H / levels;
            var x = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var next = _l;
            var slot = W / total;
            void Place(VNode n)
            {
                var ks = (kids.GetValueOrDefault(n.Id) ?? []).Where(k => depth.ContainsKey(k.Id) && depth[k.Id] == depth[n.Id] + 1).ToList();
                if (ks.Count == 0) { x[n.Id] = next + slot / 2; next += slot; return; }
                foreach (var k in ks) Place(k);
                x[n.Id] = ks.Average(k => x[k.Id]);
            }
            Place(root);

            var branchInk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var top = kids.GetValueOrDefault(root.Id) ?? [];
            for (var i = 0; i < top.Count; i++) branchInk[top[i].Id] = i % Colours;
            int InkOf(VNode n)
            {
                for (var cur = n; cur != root; cur = _v.Nodes.FirstOrDefault(p => p.Id == (cur.Parent.Length == 0 ? root.Id : cur.Parent)) ?? root)
                    if (branchInk.TryGetValue(cur.Id, out var ink)) return ink;
                return Accent;
            }

            var cardMarks = Marks.Count;
            foreach (var n in _v.Nodes.Where(n => x.ContainsKey(n.Id)))
            {
                var y = _t + levelGap * (depth[n.Id] + 0.5);
                Card(n, new P(x[n.Id], y), n == root ? Math.Max(cw, 200) : cw, n == root ? Accent : InkOf(n), n == root ? 0.28 : 0.15, note: levels <= 3);
            }
            var cards = Marks.GetRange(cardMarks, Marks.Count - cardMarks);
            Marks.RemoveRange(cardMarks, Marks.Count - cardMarks);
            foreach (var n in _v.Nodes.Where(n => n != root && x.ContainsKey(n.Id)))
            {
                var parent = n.Parent.Length == 0 ? root.Id : n.Parent;
                if (!_boxes.ContainsKey(parent)) continue;
                var (pc, _, ph) = _boxes[parent];
                var (cc, _, ch) = _boxes[n.Id];
                var from = new P(pc.X, pc.Y + ph / 2);
                var to = new P(cc.X, cc.Y - ch / 2);
                var midY = (from.Y + to.Y) / 2;
                Marks.Add(new Mark(MarkKind.Line, [from, new(from.X, midY), new(to.X, midY), to], InkOf(n), Order(n.Id)) { Stroke = 2.2, Node = n.Id });
            }
            Marks.AddRange(cards);
        }

        // ---------------------------------------------------------------- compare

        public void Compare()
        {
            var groups = _v.Nodes.Select(n => n.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            var headerNodes = new Dictionary<string, VNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in _v.Nodes.Where(n => n.Group.Length == 0))
                if (groups.FirstOrDefault(g => g.Equals(n.Label, StringComparison.OrdinalIgnoreCase)) is { } g && !headerNodes.ContainsKey(g))
                    headerNodes[g] = n;
            var items = _v.Nodes.Where(n => !headerNodes.ContainsValue(n)).ToList();
            if (groups.Count == 0)
            {
                // No columns given: split the points into two halves.
                var half = (items.Count + 1) / 2;
                items = [.. items.Select((n, i) => n with { Group = i < half ? "First" : "Second" })];
                groups = ["First", "Second"];
            }

            var cols = groups.Count;
            var gap = 30.0;
            var colW = (W - (cols - 1) * gap) / cols;
            for (var c = 0; c < cols; c++)
            {
                var g = groups[c];
                var x = _l + c * (colW + gap) + colW / 2;
                var head = headerNodes.GetValueOrDefault(g);
                var order = head is null ? 0 : Order(head.Id);
                Marks.Add(new Mark(MarkKind.Box, [new(x - colW / 2, _t), new(colW, 56)], c % Colours, order) { Fill = 0.38, Stroke = 2.5, Node = head?.Id });
                Marks.Add(new Mark(MarkKind.Text, [new(x, _t + 28)], Ink, order) { Text = head?.Label ?? g, Size = 21, Bold = true, MaxWidth = colW - 20, Node = head?.Id });
                if (head is not null) _boxes[head.Id] = (new P(x, _t + 28), colW, 56);
                var y = _t + 56 + 18;
                foreach (var n in items.Where(n => n.Group.Equals(g, StringComparison.OrdinalIgnoreCase)))
                {
                    var h = CardHeight(n, colW);
                    if (y + h > _b) break;
                    Card(n, new P(x, y + h / 2), colW, c % Colours, 0.1);
                    y += h + 12;
                }
            }
        }

        // ---------------------------------------------------------------- venn

        public void Venn()
        {
            // The sets: nodes marked "set", or named A/B/C with no group.
            var sets = _v.Nodes.Where(n => n.Shape == "set" || (n.Group.Length == 0 && n.Id.Length == 1 && "ABCabc".Contains(n.Id))).Take(3).ToList();
            if (sets.Count < 2)
            {
                var names = _v.Nodes.SelectMany(n => SplitGroup(n.Group)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
                sets = [.. names.Select((name, i) => new VNode(name, name, "", "", "", "", "set", null, null))];
            }
            if (sets.Count < 2) sets = [new("A", "A", "", "", "", "", "set", null, null), new("B", "B", "", "", "", "", "set", null, null)];
            var k = sets.Count;
            var c = Mid;
            double r;
            P[] centres;
            if (k == 2)
            {
                r = Math.Min(W * 0.26, H * 0.44);
                centres = [new(c.X - r * 0.6, c.Y + 10), new(c.X + r * 0.6, c.Y + 10)];
            }
            else
            {
                r = Math.Min(W * 0.2, H * 0.32);
                centres = [new(c.X - r * 0.58, c.Y - r * 0.28), new(c.X + r * 0.58, c.Y - r * 0.28), new(c.X, c.Y + r * 0.62)];
            }
            for (var i = 0; i < k; i++)
            {
                var order = sets[i].Id.Length > 0 ? Order(sets[i].Id) : 0;
                Marks.Add(new Mark(MarkKind.Ellipse, [centres[i], new(r, r)], i % Colours, order) { Fill = 0.16, Stroke = 3, Node = sets[i].Id });
                var away = centres[i] - c;
                var dir = away.Length < 1 ? new P(0, -1) : away * (1 / away.Length);
                var labelAt = k == 2 ? new P(centres[i].X + (i == 0 ? -r * 0.35 : r * 0.35), centres[i].Y - r - 22) : centres[i] + dir * (r + 26);
                Marks.Add(new Mark(MarkKind.Text, [labelAt], i % Colours, order) { Text = sets[i].Label, Size = 21, Bold = true, MaxWidth = r * 1.4, Node = sets[i].Id });
            }

            int Index(string token)
            {
                for (var i = 0; i < k; i++)
                    if (sets[i].Id.Equals(token, StringComparison.OrdinalIgnoreCase) || sets[i].Label.Equals(token, StringComparison.OrdinalIgnoreCase)) return i;
                return -1;
            }
            P Anchor(int[] region)
            {
                if (region.Length == 1)
                {
                    var i = region[0];
                    var away = centres[i] - c;
                    return k == 2 ? centres[i] + new P(i == 0 ? -r * 0.45 : r * 0.45, 0) : centres[i] + away * 0.85;
                }
                if (region.Length == k) return k == 2 ? c + new P(0, 10) : c + new P(0, r * 0.02);
                var m = (centres[region[0]] + centres[region[1]]) * 0.5;
                var other = centres[Enumerable.Range(0, k).First(i => !region.Contains(i))];
                return m + (m - other) * 0.32;
            }

            var regions = _v.Nodes.Where(n => !sets.Contains(n) && n.Group.Length > 0)
                .GroupBy(n => string.Join("+", SplitGroup(n.Group).Select(Index).Where(i => i >= 0).Distinct().Order()))
                .Where(g => g.Key.Length > 0);
            foreach (var g in regions)
            {
                var region = g.Key.Split('+').Select(int.Parse).ToArray();
                var at = Anchor(region);
                var list = g.Take(5).ToList();
                var lineH = 24.0;
                var maxW = region.Length == 1 ? r * 0.8 : r * 0.62;
                for (var j = 0; j < list.Count; j++)
                {
                    var y = at.Y + (j - (list.Count - 1) / 2.0) * lineH;
                    Marks.Add(new Mark(MarkKind.Text, [new(at.X, y)], Ink, Order(list[j].Id)) { Text = list[j].Label, Size = 14.5, MaxWidth = maxW, Node = list[j].Id });
                }
            }
        }

        static IEnumerable<string> SplitGroup(string group) =>
            group.Split(['+', '&', ',', '∩', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // ---------------------------------------------------------------- cutaway

        public void Cutaway()
        {
            var layers = _v.Nodes.Take(8).ToList();
            var n = layers.Count;
            var R0 = Math.Min(H * 0.46, W * 0.27);
            var o = new P(_l + W * 0.3, Mid.Y);
            double Radius(int i) => i >= n ? 0 : R0 * (1 - 0.84 * i / n);
            List<P> Arc(double r, double from, double to, int steps = 36)
            {
                var pts = new List<P>();
                for (var s = 0; s <= steps; s++) pts.Add(Polar(o, r, r, from + (to - from) * s / steps));
                return pts;
            }

            // The object, with a quarter cut away (top right) to show what's inside.
            var outer = new List<P> { o };
            outer.AddRange(Arc(R0, 0, 270, 54));
            Marks.Add(new Mark(MarkKind.Polygon, [.. outer], 0, Order(layers[0].Id)) { Fill = 0.55, Stroke = 3, Shade = -0.05, Node = layers[0].Id });
            // A soft highlight on the outside, so it reads as round.
            Marks.Add(new Mark(MarkKind.Ellipse, [o + new P(-R0 * 0.4, -R0 * 0.4), new(R0 * 0.3, R0 * 0.2)], Ink, Order(layers[0].Id)) { Fill = 0.12, Stroke = 0, Node = layers[0].Id });
            for (var i = 0; i < n; i++)
            {
                var poly = Arc(Radius(i), 270, 360);
                var inner = Radius(i + 1);
                if (inner > 0) poly.AddRange(Enumerable.Reverse(Arc(inner, 270, 360)));
                else poly.Add(o);
                Marks.Add(new Mark(MarkKind.Polygon, [.. poly], i % Colours, Order(layers[i].Id)) { Fill = 0.62, Stroke = 1.6, Shade = 0.12, Node = layers[i].Id });
            }
            Marks.Add(new Mark(MarkKind.Line, [Polar(o, R0, R0, 270), o, Polar(o, R0, R0, 0)], Accent, 0) { Stroke = 2.5 });

            // Labels down the right, each with a leader to its layer.
            var colX = o.X + R0 + 90;
            var cw = Math.Min(340, _r - colX);
            var heights = layers.Select(l => CardHeight(l, cw)).ToList();
            var total = heights.Sum() + (n - 1) * 14;
            var y = Math.Max(_t, Mid.Y - total / 2);
            for (var i = 0; i < n; i++)
            {
                var h = heights[i];
                var cy = y + h / 2;
                var mid = (Radius(i) + Radius(i + 1)) / 2;
                var a = 270 + 90 * (i + 0.5) / n;   // spread the leader starts across the cut
                var from = Polar(o, mid, mid, a);
                var to = new P(colX - 4, cy);
                var order = Order(layers[i].Id);
                Marks.Add(new Mark(MarkKind.Line, [from, new(from.X + (to.X - from.X) * 0.55, to.Y), to], i % Colours, order) { Stroke = 2, Node = layers[i].Id });
                Marks.Add(new Mark(MarkKind.Ellipse, [from, new(5, 5)], Ink, order) { Fill = 1, Node = layers[i].Id });
                Card(layers[i], new P(colX + cw / 2, cy), cw, i % Colours, 0.12);
                y += h + 14;
            }
        }

        // ---------------------------------------------------------------- callouts on the screen

        /// <summary>
        /// Pointing at things on the real screen: a ring or a box exactly on each thing, a numbered
        /// card with its label and note beside it (on whichever side has room, clear of the
        /// other cards), and a leader joining them.
        /// </summary>
        public void Callouts(double sx, double sy)
        {
            var placed = new List<Area>();
            var targets = _v.Nodes.Select(n =>
            {
                if (n.Box is { } b)
                {
                    var r = new Area(b[0] * sx, b[1] * sy, Math.Max(8, b[2] * sx), Math.Max(8, b[3] * sy));
                    return (Node: n, Centre: new P(r.X + r.W / 2, r.Y + r.H / 2), Rect: (Area?)r);
                }
                var p = n.Point!;
                return (Node: n, Centre: new P(p[0] * sx, p[1] * sy), Rect: (Area?)null);
            }).ToList();
            // Nothing should cover what's being pointed at.
            foreach (var t in targets)
                placed.Add(t.Rect is { } r ? new Area(r.X - 6, r.Y - 6, r.W + 12, r.H + 12) : new Area(t.Centre.X - 30, t.Centre.Y - 30, 60, 60));

            bool Free(Area c) =>
                c.X >= _l && c.Y >= _t && c.X + c.W <= _r && c.Y + c.H <= _b &&
                !placed.Any(o => c.X < o.X + o.W && c.X + c.W > o.X && c.Y < o.Y + o.H && c.Y + c.H > o.Y);

            for (var i = 0; i < targets.Count; i++)
            {
                var (n, c, rect) = targets[i];
                var ink = i % Colours;
                var order = Order(n.Id);

                // The mark on the thing itself.
                if (rect is { } r)
                {
                    var pad = 5.0;
                    P[] box = [new(r.X - pad, r.Y - pad), new(r.X + r.W + pad, r.Y - pad), new(r.X + r.W + pad, r.Y + r.H + pad), new(r.X - pad, r.Y + r.H + pad)];
                    Marks.Add(new Mark(MarkKind.Polygon, box, ink, order) { Fill = 0.1, Stroke = 3, Node = n.Id });
                }
                else
                    Marks.Add(new Mark(MarkKind.Ellipse, [c, new(26, 26)], ink, order) { Fill = 0.12, Stroke = 3, Node = n.Id });
                var halfW = rect is { } rr ? rr.W / 2 + 5 : 26;
                var halfH = rect is { } rh ? rh.H / 2 + 5 : 26;

                // The card: right of it, else left, else below, else above; nudged down past other cards.
                var cw = Math.Min(300, Math.Max(200, Sketch.TextWidth(n.Label, 17) + 40));
                var ch = CardHeight(n, cw);
                Area? spot = null;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) })
                {
                    var cx = dx > 0 ? c.X + halfW + 60 : dx < 0 ? c.X - halfW - 60 - cw : c.X - cw / 2;
                    var cy = dy > 0 ? c.Y + halfH + 50 : dy < 0 ? c.Y - halfH - 50 - ch : c.Y - ch / 2;
                    for (var tries = 0; tries < 8 && spot is null; tries++)
                    {
                        var cand = new Area(Math.Clamp(cx, _l, _r - cw), Math.Clamp(cy + (ch + 12) * (tries % 2 == 0 ? 1 : -1) * ((tries + 1) / 2), _t, _b - ch), cw, ch);
                        if (Free(cand)) spot = cand;
                    }
                    if (spot is not null) break;
                }
                var s = spot ?? new Area(Math.Clamp(c.X + halfW + 40, _l, _r - cw), Math.Clamp(c.Y - ch / 2, _t, _b - ch), cw, ch);
                placed.Add(s);
                var cardC = new P(s.X + cw / 2, s.Y + ch / 2);

                // Leader: from the mark's edge to the card's nearest edge.
                var to = new P(Math.Clamp(c.X, s.X, s.X + cw), Math.Clamp(c.Y, s.Y, s.Y + ch));
                if (to.X > s.X && to.X < s.X + cw && to.Y > s.Y && to.Y < s.Y + ch) to = new P(s.X, cardC.Y);
                var d = to - c;
                var len = d.Length;
                if (len > 1)
                {
                    var u = d * (1 / len);
                    var from = rect is not null
                        ? c + u * Math.Min(Math.Abs(u.X) > 1e-6 ? halfW / Math.Abs(u.X) : double.MaxValue, Math.Abs(u.Y) > 1e-6 ? halfH / Math.Abs(u.Y) : double.MaxValue)
                        : c + u * 26;
                    Marks.Add(new Mark(MarkKind.Line, [from, to], ink, order) { Stroke = 2.5, Node = n.Id });
                }
                Card(n, cardC, cw, ink, 0.22);
                // The number, so "first click here, then there" reads at a glance.
                var badge = new P(s.X + 2, s.Y + 2);
                Marks.Add(new Mark(MarkKind.Ellipse, [badge, new(14, 14)], ink, order) { Fill = 1, Stroke = 0, Node = n.Id });
                Marks.Add(new Mark(MarkKind.Text, [badge], OnAccent, order) { Text = (i + 1).ToString(), Size = 13, Bold = true, Node = n.Id });
            }
        }

        // ---------------------------------------------------------------- 3D illustration

        public void Iso3D()
        {
            var parts = _v.Nodes.Take(12).Select((n, i) =>
            {
                var size = n.Size ?? [3, 1.5, 3];
                size = [.. size.Select(s => Math.Clamp(Math.Abs(s), 0.2, 12))];
                var at = n.At ?? [0, i * 1.6, 0];   // no position: stacked
                var shape = n.Shape is "box" or "cube" or "cylinder" or "sphere" or "ball" or "cone" or "pyramid" ? n.Shape : "box";
                return (Node: n, Index: i, At: at, Size: size, Shape: shape);
            }).ToList();

            const double cos30 = 0.8660254, sin30 = 0.5;
            P Iso(double x, double y, double z) => new((x - z) * cos30, -y + (x + z) * sin30);

            // Fit the drawing into the left part of the board.
            var gx0 = parts.Min(p => p.At[0]) - 0.6;
            var gx1 = parts.Max(p => p.At[0] + p.Size[0]) + 0.6;
            var gz0 = parts.Min(p => p.At[2]) - 0.6;
            var gz1 = parts.Max(p => p.At[2] + p.Size[2]) + 0.6;
            var gy = parts.Min(p => p.At[1]);
            var all = parts.SelectMany(p => Corners(p.At, p.Size)).Select(c => Iso(c.x, c.y, c.z))
                .Concat([Iso(gx0, gy, gz0), Iso(gx1, gy, gz0), Iso(gx1, gy, gz1), Iso(gx0, gy, gz1)]).ToList();
            double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
            var areaW = W * 0.6;
            var s = Math.Min(Math.Min(areaW / Math.Max(0.1, maxX - minX), H / Math.Max(0.1, maxY - minY)), 110);
            var ox = _l + areaW / 2 - (minX + maxX) / 2 * s;
            var oy = Mid.Y - (minY + maxY) / 2 * s;
            P S(double x, double y, double z) { var p = Iso(x, y, z); return new P(ox + p.X * s, oy + p.Y * s); }

            // A ground shadow under everything.
            Marks.Add(new Mark(MarkKind.Polygon, [S(gx0, gy, gz0), S(gx1, gy, gz0), S(gx1, gy, gz1), S(gx0, gy, gz1)], Accent, 0) { Fill = 0.08, Stroke = 1, Dashed = true });

            var anchors = new Dictionary<string, P>();
            // Back to front: lower parts first (what sits on them is drawn over them), then the far ones.
            foreach (var p in parts.OrderBy(p => Math.Round(p.At[1], 1)).ThenBy(p => p.At[0] + p.Size[0] / 2 + p.At[2] + p.Size[2] / 2))
            {
                double x = p.At[0], y = p.At[1], z = p.At[2], w = p.Size[0], h = p.Size[1], d = p.Size[2];
                var ink = p.Index % Colours;
                var order = Order(p.Node.Id);
                Mark Face(P[] pts, double shade) => new(MarkKind.Polygon, pts, ink, order) { Fill = 0.92, Stroke = 1.6, Shade = shade, Node = p.Node.Id };
                switch (p.Shape)
                {
                    case "cylinder":
                    {
                        double cx = x + w / 2, cz = z + d / 2, r = Math.Min(w, d) / 2;
                        P Ring(double yy, double deg) => S(cx + r * Math.Cos(deg * Math.PI / 180), yy, cz + r * Math.Sin(deg * Math.PI / 180));
                        var side = new List<P>();
                        for (var a = -45; a <= 135; a += 10) side.Add(Ring(y, a));
                        for (var a = 135; a >= -45; a -= 10) side.Add(Ring(y + h, a));
                        Marks.Add(Face([.. side], -0.05));
                        Marks.Add(Face([.. Enumerable.Range(0, 36).Select(k => Ring(y + h, k * 10))], 0.35));
                        anchors[p.Node.Id] = S(cx, y + h, cz);
                        break;
                    }
                    case "sphere" or "ball":
                    {
                        var r = Math.Min(w, Math.Min(h, d)) / 2;
                        var c = S(x + w / 2, y + h / 2, z + d / 2);
                        Marks.Add(new Mark(MarkKind.Ellipse, [c, new(r * s, r * s)], ink, order) { Fill = 0.92, Stroke = 1.6, Node = p.Node.Id });
                        Marks.Add(new Mark(MarkKind.Ellipse, [c + new P(-r * s * 0.32, -r * s * 0.34), new(r * s * 0.42, r * s * 0.32)], ink, order) { Fill = 0.9, Stroke = 0, Shade = 0.5, Node = p.Node.Id });
                        anchors[p.Node.Id] = c + new P(0, -r * s * 0.7);
                        break;
                    }
                    case "cone":
                    {
                        double cx = x + w / 2, cz = z + d / 2, r = Math.Min(w, d) / 2;
                        P Base(double deg) => S(cx + r * Math.Cos(deg * Math.PI / 180), y, cz + r * Math.Sin(deg * Math.PI / 180));
                        Marks.Add(Face([.. Enumerable.Range(0, 36).Select(k => Base(k * 10))], -0.25));
                        var side = new List<P>();
                        for (var a = -45; a <= 135; a += 10) side.Add(Base(a));
                        side.Add(S(cx, y + h, cz));
                        Marks.Add(Face([.. side], 0.05));
                        anchors[p.Node.Id] = S(cx, y + h * 0.6, cz);
                        break;
                    }
                    case "pyramid":
                    {
                        var apex = S(x + w / 2, y + h, z + d / 2);
                        Marks.Add(Face([S(x + w, y, z), S(x + w, y, z + d), apex], -0.25));
                        Marks.Add(Face([S(x, y, z + d), S(x + w, y, z + d), apex], 0.05));
                        anchors[p.Node.Id] = S(x + w / 2, y + h * 0.6, z + d / 2);
                        break;
                    }
                    default:
                        Marks.Add(Face([S(x + w, y, z), S(x + w, y + h, z), S(x + w, y + h, z + d), S(x + w, y, z + d)], -0.28));
                        Marks.Add(Face([S(x, y, z + d), S(x + w, y, z + d), S(x + w, y + h, z + d), S(x, y + h, z + d)], 0.0));
                        Marks.Add(Face([S(x, y + h, z), S(x + w, y + h, z), S(x + w, y + h, z + d), S(x, y + h, z + d)], 0.32));
                        anchors[p.Node.Id] = S(x + w / 2, y + h, z + d / 2);
                        break;
                }
            }

            // Labels down the right, each with a leader to its part.
            var colX = _l + areaW + 60;
            var cw = Math.Min(330, _r - colX);
            var sorted = parts.OrderBy(p => anchors[p.Node.Id].Y).ToList();
            var heights = sorted.Select(p => CardHeight(p.Node, cw)).ToList();
            var total = heights.Sum() + (sorted.Count - 1) * 12;
            var yy = Math.Max(_t, Mid.Y - total / 2);
            for (var i = 0; i < sorted.Count; i++)
            {
                var p = sorted[i];
                var h = heights[i];
                var cy = yy + h / 2;
                var from = anchors[p.Node.Id];
                var order = Order(p.Node.Id);
                Marks.Add(new Mark(MarkKind.Line, [from, new(colX - 30, cy), new(colX - 4, cy)], p.Index % Colours, order) { Stroke = 1.8, Node = p.Node.Id });
                Marks.Add(new Mark(MarkKind.Ellipse, [from, new(4.5, 4.5)], Ink, order) { Fill = 1, Node = p.Node.Id });
                Card(p.Node, new P(colX + cw / 2, cy), cw, p.Index % Colours, 0.12);
                yy += h + 12;
            }
        }

        readonly record struct Area(double X, double Y, double W, double H);

        static IEnumerable<(double x, double y, double z)> Corners(double[] at, double[] size)
        {
            foreach (var dx in new[] { 0.0, 1 })
                foreach (var dy in new[] { 0.0, 1 })
                    foreach (var dz in new[] { 0.0, 1 })
                        yield return (at[0] + size[0] * dx, at[1] + size[1] * dy, at[2] + size[2] * dz);
        }
    }
}
