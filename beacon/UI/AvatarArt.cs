using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beacon.UI;

/// <summary>
/// Nova's character art: 24 poses, recoloured to the Beacon style.
///
/// The art is a folder of pictures, looked for first in %APPDATA%\Beacon\avatar (your own,
/// overriding) and then in the Avatar folder next to Beacon.exe (the one that ships). Either
/// one sheet.png laid out like the design sheet (6 across, 4 down, in the order of
/// <see cref="Poses"/>, each with its name label underneath, which is cut off), or one
/// picture per pose named after it: idle.png, listening.png, no-signal.png...
///
/// The glow in the art (blue and cyan) is turned to the style's accent colour, so the
/// character always matches Tactical amber, HUD cyan, Cyberpunk pink, CRT green... Status
/// colours that mean something (red alert, green confirmed, pink heart) are kept.
/// </summary>
public static class AvatarArt
{
    public static readonly string[] Poses =
    [
        "Idle", "Listening", "Thinking", "Working", "Alert", "Happy",
        "Searching", "Loading", "Confirmed", "Error", "Tired", "Shy",
        "Idea", "No Signal", "Calculating", "Sad", "Muted", "Charging",
        "Celebrating", "Heart", "Doubtful", "Sleepy", "Greeting", "Encrypting",
    ];

    static readonly Dictionary<string, BitmapSource?> Raw = [];
    static readonly Dictionary<(string, Color), BitmapSource> Tinted = [];
    static string? _folder;
    static BitmapSource? _sheet;
    static bool _looked;

    /// <summary>Where the art was found, or null when there is none.</summary>
    public static string? Folder
    {
        get
        {
            if (_looked) return _folder;
            _looked = true;
            foreach (var dir in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon", "avatar"),
                         Path.Combine(AppContext.BaseDirectory, "Avatar"),
                     })
            {
                if (File.Exists(Path.Combine(dir, "sheet.png")) || File.Exists(Path.Combine(dir, "idle.png")))
                    return _folder = dir;
            }
            return null;
        }
    }

    public static bool Available => Folder is not null;

    public static string UserFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon", "avatar");

    /// <summary>Forget what was loaded, after new art was put in place.</summary>
    public static void Reload()
    {
        _looked = false; _folder = null; _sheet = null;
        Raw.Clear(); Tinted.Clear();
    }

    /// <summary>A pose in the accent colour, or null when there's no art for it.</summary>
    public static BitmapSource? Get(string pose, Color accent)
    {
        if (Tinted.TryGetValue((pose, accent), out var done)) return done;
        if (Load(pose) is not { } raw) return null;
        var tinted = Recolor(raw, accent);
        if (Tinted.Count > 200) Tinted.Clear();
        return Tinted[(pose, accent)] = tinted;
    }

    static BitmapSource? Load(string pose)
    {
        if (Raw.TryGetValue(pose, out var cached)) return cached;
        BitmapSource? result = null;
        if (Folder is { } dir)
        {
            var single = Path.Combine(dir, pose.ToLowerInvariant().Replace(' ', '-') + ".png");
            if (File.Exists(single)) result = Open(single);
            else if (File.Exists(Path.Combine(dir, "sheet.png")))
            {
                _sheet ??= Open(Path.Combine(dir, "sheet.png"));
                var index = Array.IndexOf(Poses, pose);
                if (_sheet is not null && index >= 0)
                {
                    // 6 x 4 cells; the bottom ~14% of each holds the name label.
                    var w = _sheet.PixelWidth / 6.0;
                    var h = _sheet.PixelHeight / 4.0;
                    var x = (int)(index % 6 * w);
                    var y = (int)(index / 6 * h);
                    result = new CroppedBitmap(_sheet, new Int32Rect(x, y, (int)w, (int)(h * 0.86)));
                    result.Freeze();
                }
            }
        }
        return Raw[pose] = result;
    }

    static BitmapSource? Open(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return null;
        }
    }

    static BitmapSource Recolor(BitmapSource source, Color accent)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        AvatarRecolor.ToAccent(pixels, accent.R, accent.G, accent.B);
        var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
