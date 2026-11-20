using System.IO;
using System.Text;
using System.Text.Json;

namespace Beacon.UI.Pet.Kai;

/// <summary>
/// A small reader for binary glTF (.glb): the node tree, triangle meshes, materials and
/// embedded images, which is everything Kai's model uses. No skins, morphs or animations are
/// read (the model has none; Kai is animated in code through its named pivots).
/// Plain .NET, no WPF, so it can be checked anywhere.
/// </summary>
public sealed class GlbDocument
{
    /// <summary>A node; Matrix is column-major (glTF's order), local to the parent.</summary>
    public sealed record Node(string? Name, double[] Matrix, int[] Children, int? Mesh);

    public sealed record Primitive(float[] Positions, float[]? Normals, float[]? Uvs, int[] Indices, int? Material);

    /// <summary>Colours are glTF's linear values. Texture is an index into Images.</summary>
    public sealed record Material(
        float[] BaseColor, float[] Emissive, float EmissiveStrength, float Metallic,
        bool Unlit, bool Blend, bool DoubleSided, int? Texture, bool Repeat);

    public List<Node> Nodes { get; } = [];
    public List<Primitive[]> Meshes { get; } = [];
    public List<Material> Materials { get; } = [];
    public List<byte[]> Images { get; } = [];
    public int[] Roots { get; private set; } = [];

    public static GlbDocument Parse(byte[] glb)
    {
        if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67)
            throw new InvalidDataException("Not a .glb file.");
        var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        if (BitConverter.ToUInt32(glb, 16) != 0x4E4F534A) throw new InvalidDataException("The .glb has no JSON chunk.");
        var json = Encoding.UTF8.GetString(glb, 20, jsonLength);
        var binStart = 20 + jsonLength;
        ReadOnlyMemory<byte> bin = ReadOnlyMemory<byte>.Empty;
        if (binStart + 8 <= glb.Length)
        {
            var binLength = (int)BitConverter.ToUInt32(glb, binStart);
            bin = new ReadOnlyMemory<byte>(glb, binStart + 8, binLength);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new GlbDocument();
        var views = Array(root, "bufferViews");
        var accessors = Array(root, "accessors");

        ReadOnlySpan<byte> View(int index, out int stride)
        {
            var v = views[index];
            var offset = Int(v, "byteOffset", 0);
            stride = Int(v, "byteStride", 0);
            return bin.Span.Slice(offset, v.GetProperty("byteLength").GetInt32());
        }

        float[] Floats(int accessor, int width)
        {
            var a = accessors[accessor];
            var count = a.GetProperty("count").GetInt32();
            var data = View(a.GetProperty("bufferView").GetInt32(), out var stride);
            var start = Int(a, "byteOffset", 0);
            if (a.GetProperty("componentType").GetInt32() != 5126) throw new InvalidDataException("Only float vertex data is read.");
            stride = stride == 0 ? width * 4 : stride;
            var result = new float[count * width];
            for (var i = 0; i < count; i++)
                for (var k = 0; k < width; k++)
                    result[i * width + k] = BitConverter.ToSingle(data.Slice(start + i * stride + k * 4, 4));
            return result;
        }

        int[] Ints(int accessor)
        {
            var a = accessors[accessor];
            var count = a.GetProperty("count").GetInt32();
            var data = View(a.GetProperty("bufferView").GetInt32(), out _);
            var start = Int(a, "byteOffset", 0);
            var result = new int[count];
            switch (a.GetProperty("componentType").GetInt32())
            {
                case 5125: for (var i = 0; i < count; i++) result[i] = (int)BitConverter.ToUInt32(data.Slice(start + i * 4, 4)); break;
                case 5123: for (var i = 0; i < count; i++) result[i] = BitConverter.ToUInt16(data.Slice(start + i * 2, 2)); break;
                case 5121: for (var i = 0; i < count; i++) result[i] = data[start + i]; break;
                default: throw new InvalidDataException("Unknown index type.");
            }
            return result;
        }

        // Images and which texture points at which image (and whether it repeats).
        foreach (var image in Array(root, "images"))
        {
            var data = View(image.GetProperty("bufferView").GetInt32(), out _);
            result.Images.Add(data.ToArray());
        }
        var samplers = Array(root, "samplers");
        var textures = Array(root, "textures").Select(t =>
        {
            var source = Int(t, "source", -1);
            var repeat = t.TryGetProperty("sampler", out var s) && Int(samplers[s.GetInt32()], "wrapS", 10497) == 10497;
            return (source, repeat);
        }).ToArray();

        foreach (var m in Array(root, "materials"))
        {
            var pbr = m.TryGetProperty("pbrMetallicRoughness", out var p) ? p : default;
            float[] baseColor = pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("baseColorFactor", out var bc)
                ? bc.EnumerateArray().Select(x => x.GetSingle()).ToArray() : [1, 1, 1, 1];
            float[] emissive = m.TryGetProperty("emissiveFactor", out var ef) ? ef.EnumerateArray().Select(x => x.GetSingle()).ToArray() : [0, 0, 0];
            var extensions = m.TryGetProperty("extensions", out var ext) ? ext : default;
            var strength = extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("KHR_materials_emissive_strength", out var es)
                ? es.GetProperty("emissiveStrength").GetSingle() : 1f;
            var unlit = extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("KHR_materials_unlit", out _);
            int? texture = null;
            var repeat = false;
            if (pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("baseColorTexture", out var bt))
            {
                var (source, rep) = textures[bt.GetProperty("index").GetInt32()];
                if (source >= 0) { texture = source; repeat = rep; }
            }
            var metallic = pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("metallicFactor", out var mf) ? mf.GetSingle() : 1f;
            result.Materials.Add(new Material(
                baseColor, emissive, strength, metallic, unlit,
                m.TryGetProperty("alphaMode", out var am) && am.GetString() == "BLEND",
                m.TryGetProperty("doubleSided", out var ds) && ds.GetBoolean(),
                texture, repeat));
        }

        foreach (var mesh in Array(root, "meshes"))
        {
            var prims = new List<Primitive>();
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (Int(prim, "mode", 4) != 4) continue;   // triangles only
                var attrs = prim.GetProperty("attributes");
                var positions = Floats(attrs.GetProperty("POSITION").GetInt32(), 3);
                var normals = attrs.TryGetProperty("NORMAL", out var n) ? Floats(n.GetInt32(), 3) : null;
                var uvs = attrs.TryGetProperty("TEXCOORD_0", out var uv) ? Floats(uv.GetInt32(), 2) : null;
                var indices = prim.TryGetProperty("indices", out var ix)
                    ? Ints(ix.GetInt32())
                    : Enumerable.Range(0, positions.Length / 3).ToArray();
                prims.Add(new Primitive(positions, normals, uvs, indices, prim.TryGetProperty("material", out var mi) ? mi.GetInt32() : null));
            }
            result.Meshes.Add([.. prims]);
        }

        foreach (var node in Array(root, "nodes"))
        {
            var matrix = node.TryGetProperty("matrix", out var mx)
                ? mx.EnumerateArray().Select(x => x.GetDouble()).ToArray()
                : Trs(
                    node.TryGetProperty("translation", out var t) ? t.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [0, 0, 0],
                    node.TryGetProperty("rotation", out var r) ? r.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [0, 0, 0, 1],
                    node.TryGetProperty("scale", out var s) ? s.EnumerateArray().Select(x => x.GetDouble()).ToArray() : [1, 1, 1]);
            result.Nodes.Add(new Node(
                node.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                matrix,
                node.TryGetProperty("children", out var ch) ? ch.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [],
                node.TryGetProperty("mesh", out var me) ? me.GetInt32() : null));
        }

        var scene = Int(root, "scene", 0);
        var scenes = Array(root, "scenes");
        result.Roots = scenes.Length > scene && scenes[scene].TryGetProperty("nodes", out var sn)
            ? sn.EnumerateArray().Select(x => x.GetInt32()).ToArray()
            : [.. Enumerable.Range(0, result.Nodes.Count).Except(result.Nodes.SelectMany(n => n.Children))];
        return result;
    }

    /// <summary>Translation, rotation (quaternion x,y,z,w) and scale as a column-major matrix.</summary>
    public static double[] Trs(double[] t, double[] q, double[] s)
    {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        return
        [
            (1 - 2 * (y * y + z * z)) * s[0], 2 * (x * y + z * w) * s[0], 2 * (x * z - y * w) * s[0], 0,
            2 * (x * y - z * w) * s[1], (1 - 2 * (x * x + z * z)) * s[1], 2 * (y * z + x * w) * s[1], 0,
            2 * (x * z + y * w) * s[2], 2 * (y * z - x * w) * s[2], (1 - 2 * (x * x + y * y)) * s[2], 0,
            t[0], t[1], t[2], 1,
        ];
    }

    /// <summary>The transform that applies a, then b (the column-major product b·a).</summary>
    public static double[] Then(double[] a, double[] b)
    {
        var r = new double[16];
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += b[k * 4 + row] * a[col * 4 + k];
                r[col * 4 + row] = sum;
            }
        return r;
    }

    public static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    static JsonElement[] Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) ? [.. a.EnumerateArray()] : [];

    static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
}
