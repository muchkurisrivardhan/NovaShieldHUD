using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace Beacon.Interop;

/// <summary>
/// A screenshot of one monitor, for Kai to look at when asked about the screen. Taken only on
/// that request, kept in memory, and never written anywhere by Beacon. It's scaled down to at
/// most 1568 pixels on the long side (what vision models read best) and saved as a JPEG.
/// </summary>
internal static class ScreenCapture
{
    /// <param name="Bounds">The monitor, in physical pixels.</param>
    public sealed record Shot(byte[] Jpeg, int Width, int Height, Rectangle Bounds);

    public static Shot Capture(Point at, int maxSide = 1568)
    {
        var bounds = Forms.Screen.FromPoint(at).Bounds;
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        var k = Math.Min(1.0, maxSide / (double)Math.Max(bounds.Width, bounds.Height));
        var w = Math.Max(1, (int)Math.Round(bounds.Width * k));
        var h = Math.Max(1, (int)Math.Round(bounds.Height * k));
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, 0, 0, w, h);
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        using var stream = new MemoryStream();
        small.Save(stream, codec, quality);
        return new Shot(stream.ToArray(), w, h, bounds);
    }
}
