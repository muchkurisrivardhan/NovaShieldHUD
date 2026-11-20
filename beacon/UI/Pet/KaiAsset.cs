using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Beacon.UI.Pet.Kai;

namespace Beacon.UI.Pet;

/// <summary>
/// Kai's model, read once from the app's own resources and shared by every Kai on screen
/// (the pet and the Settings previews): the rig, its meshes ready for WPF, and the decoded
/// pictures (face, hair, holo floor, and the thinking "?").
/// </summary>
internal sealed class KaiAsset
{
    public sealed record Picture(byte[] Bgra, int Width, int Height);

    public KaiRig Rig { get; }
    public MeshGeometry3D[] Meshes { get; }
    public Picture?[] Images { get; }
    public Picture? Think { get; }

    /// <summary>The image index of the face texture.</summary>
    public int? FaceImage { get; }

    static readonly Lazy<KaiAsset?> Instance = new(Load);

    /// <summary>The shared model, or null if it couldn't be read (the pet then shows a simple orb).</summary>
    public static KaiAsset? Shared => Instance.Value;

    public static string? LoadError { get; private set; }

    KaiAsset(KaiRig rig)
    {
        Rig = rig;
        Meshes = [.. rig.Parts.Select(ToMesh)];
        // The face is the textured material that isn't repeated (the hair repeats round the head).
        FaceImage = rig.Doc.Materials.FirstOrDefault(m => m.Texture is not null && !m.Repeat && !m.Blend)?.Texture;
        Images = [.. rig.Doc.Images.Select((png, i) => Decode(png, i == FaceImage ? 512 : 0))];
        Think = Resource("think.png") is { } think ? Decode(think, 0) : null;
    }

    static KaiAsset? Load()
    {
        try
        {
            var glb = Resource("kai.glb") ?? throw new FileNotFoundException("kai.glb is missing from the app.");
            return new KaiAsset(KaiRig.Build(GlbDocument.Parse(glb)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
                                       or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            LoadError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"Kai's model could not be loaded: {ex.Message}");
            return null;
        }
    }

    static byte[]? Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Beacon.Kai." + name);
        if (s is null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static MeshGeometry3D ToMesh(KaiRig.Part p)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(p.Positions.Length / 3);
        var normals = new Vector3DCollection(p.Positions.Length / 3);
        var uvs = new PointCollection(p.Positions.Length / 3);
        for (var i = 0; i < p.Positions.Length / 3; i++)
        {
            positions.Add(new Point3D(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]));
            normals.Add(new Vector3D(p.Normals[i * 3], p.Normals[i * 3 + 1], p.Normals[i * 3 + 2]));
            uvs.Add(new Point(p.Uvs[i * 2], p.Uvs[i * 2 + 1]));
        }
        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TextureCoordinates = uvs;
        mesh.TriangleIndices = new Int32Collection(p.Indices);
        mesh.Freeze();
        return mesh;
    }

    /// <summary>PNG bytes to BGRA pixels, scaled down to `size` wide if given.</summary>
    static Picture? Decode(byte[] png, int size)
    {
        try
        {
            using var s = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (size > 0 && frame.PixelWidth > size)
            {
                var k = (double)size / frame.PixelWidth;
                frame = new TransformedBitmap(frame, new ScaleTransform(k, k));
            }
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var px = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
            return new Picture(px, bgra.PixelWidth, bgra.PixelHeight);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"A picture in Kai's model could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pixels to a frozen bitmap.</summary>
    public static BitmapSource ToBitmap(byte[] bgra, int width, int height)
    {
        var b = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        b.Freeze();
        return b;
    }

    /// <summary>A brush that lays a picture over a mesh by its texture coordinates.</summary>
    public static ImageBrush TextureBrush(ImageSource source, bool repeat) => new(source)
    {
        ViewportUnits = BrushMappingMode.Absolute,
        Viewport = new Rect(0, 0, 1, 1),
        TileMode = repeat ? TileMode.Tile : TileMode.None,
        Stretch = Stretch.Fill,
    };
}
