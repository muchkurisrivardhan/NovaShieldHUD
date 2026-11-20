namespace Beacon.UI.Pet.Kai;

/// <summary>
/// Kai's model, arranged for animating: the meshes are grouped under the joints we move
/// (body, torso, head, hair strand, arms, elbows, legs, and the holo base's rings and the listening
/// waves), with each joint's meshes merged per material so there are few pieces to draw.
///
/// A joint's Rest is where it sits in its parent joint (column-major). For the body joints
/// only the position is kept: the model was exported mid-pose (an arm raised to the ear), and
/// the pose is ours to set in code. Plain .NET, no WPF.
/// </summary>
public sealed class KaiRig
{
    public sealed record Joint(string Name, int Parent, double[] Rest);

    public sealed record Part(int Joint, int Material, float[] Positions, float[] Normals, float[] Uvs, int[] Indices);

    public const string Body = "BodyPivot", Torso = "Torso", Head = "HeadGroup", Strand = "AhogePivot",
        LeftArm = "LeftArmPivot", RightArm = "RightArmPivot", LeftLeg = "LeftLegPivot", RightLeg = "RightLegPivot",
        Base = "HoloBase", Waves = "Waves", Ring = "Ring", Arcs = "Arcs",
        LeftElbow = "LeftElbow", RightElbow = "RightElbow";

    static readonly HashSet<string> Posed = [Body, Torso, Head, Strand, LeftArm, RightArm, LeftLeg, RightLeg];

    public GlbDocument Doc { get; }
    public List<Joint> Joints { get; } = [new("Root", -1, GlbDocument.Identity)];
    public List<Part> Parts { get; } = [];

    public int this[string name] => Joints.FindIndex(j => j.Name == name);

    KaiRig(GlbDocument doc) => Doc = doc;

    public static KaiRig Build(GlbDocument doc)
    {
        var rig = new KaiRig(doc);
        var buckets = new Dictionary<(int Joint, int Material), Bucket>();
        foreach (var r in doc.Roots) rig.Visit(r, 0, GlbDocument.Identity, null, buckets);
        foreach (var ((joint, material), b) in buckets.OrderBy(k => k.Key.Joint).ThenBy(k => k.Key.Material))
            rig.Parts.Add(new Part(joint, material, [.. b.Positions], [.. b.Normals], [.. b.Uvs], [.. b.Indices]));
        return rig;
    }

    void Visit(int index, int joint, double[] parentToJoint, string? parentName, Dictionary<(int, int), Bucket> buckets)
    {
        var node = Doc.Nodes[index];
        var m = GlbDocument.Then(node.Matrix, parentToJoint);

        // Does this node get its own joint?
        string? jointName = null;
        var keepRotation = true;
        if (node.Name is { } name && Posed.Contains(name)) { jointName = name; keepRotation = false; }
        else if (node.Name is null && node.Mesh is null && parentName is LeftArm or RightArm)
        {
            jointName = parentName == LeftArm ? LeftElbow : RightElbow;   // the forearm and hand
            keepRotation = false;
        }
        else if (node.Name == Base) jointName = Base;
        else if (node.Name is null && node.Mesh is null && node.Children.Length > 0 && parentName == Head) jointName = Waves;
        else if (node.Name is null && node.Mesh is null && parentName == Base) jointName = Arcs;
        else if (node.Name is null && node.Mesh is not null && parentName == Base && node.Matrix[13] > 0.2) jointName = Ring;   // the tilted ring round the waist

        if (jointName is not null)
        {
            var rest = keepRotation ? m : [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, m[12], m[13], m[14], 1];
            Joints.Add(new Joint(jointName, joint, rest));
            joint = Joints.Count - 1;
            m = GlbDocument.Identity;
        }

        if (node.Mesh is { } mesh)
            foreach (var prim in Doc.Meshes[mesh])
            {
                var key = (joint, prim.Material ?? -1);
                if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new Bucket();
                b.Add(prim, m);
            }

        foreach (var child in node.Children)
            Visit(child, joint, m, node.Name, buckets);
    }

    /// <summary>Where each joint is, model-wide, given each joint's own extra transform (for checks).</summary>
    public double[][] Pose(Func<int, double[]> local)
    {
        var world = new double[Joints.Count][];
        for (var i = 0; i < Joints.Count; i++)
        {
            var j = Joints[i];
            var own = GlbDocument.Then(local(i), j.Rest);
            world[i] = j.Parent < 0 ? own : GlbDocument.Then(own, world[j.Parent]);
        }
        return world;
    }

    sealed class Bucket
    {
        public readonly List<float> Positions = [], Normals = [], Uvs = [];
        public readonly List<int> Indices = [];

        public void Add(GlbDocument.Primitive p, double[] m)
        {
            var start = Positions.Count / 3;
            var count = p.Positions.Length / 3;
            for (var i = 0; i < count; i++)
            {
                double x = p.Positions[i * 3], y = p.Positions[i * 3 + 1], z = p.Positions[i * 3 + 2];
                Positions.Add((float)(m[0] * x + m[4] * y + m[8] * z + m[12]));
                Positions.Add((float)(m[1] * x + m[5] * y + m[9] * z + m[13]));
                Positions.Add((float)(m[2] * x + m[6] * y + m[10] * z + m[14]));
                if (p.Normals is { } n)
                {
                    double nx = n[i * 3], ny = n[i * 3 + 1], nz = n[i * 3 + 2];
                    double tx = m[0] * nx + m[4] * ny + m[8] * nz, ty = m[1] * nx + m[5] * ny + m[9] * nz, tz = m[2] * nx + m[6] * ny + m[10] * nz;
                    var len = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    if (len < 1e-9) len = 1;
                    Normals.Add((float)(tx / len)); Normals.Add((float)(ty / len)); Normals.Add((float)(tz / len));
                }
                else { Normals.Add(0); Normals.Add(1); Normals.Add(0); }
                if (p.Uvs is { } uv) { Uvs.Add(uv[i * 2]); Uvs.Add(uv[i * 2 + 1]); }
                else { Uvs.Add(0); Uvs.Add(0); }
            }
            // A mirrored transform flips the winding; keep triangles facing out.
            var det = m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
            for (var i = 0; i + 2 < p.Indices.Length; i += 3)
            {
                Indices.Add(start + p.Indices[i]);
                if (det < 0) { Indices.Add(start + p.Indices[i + 2]); Indices.Add(start + p.Indices[i + 1]); }
                else { Indices.Add(start + p.Indices[i + 1]); Indices.Add(start + p.Indices[i + 2]); }
            }
        }
    }
}
