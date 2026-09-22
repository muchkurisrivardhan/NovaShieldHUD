using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beacon.Interop;

/// <summary>
/// Icons via the Shell's image factory - the same source Explorer and the Start menu use.
///
/// Two reasons it replaces <c>Icon.ExtractAssociatedIcon</c>:
/// <list type="bullet">
/// <item>Store (packaged) apps have no file to extract from. They are only reachable as
///   shell items ("shell:AppsFolder\&lt;AppUserModelID&gt;"), and only the Shell can render
///   their icon - so without this every Store app showed a generic glyph.</item>
/// <item>ExtractAssociatedIcon is fixed at 32px, which is visibly soft on a 200% display.
///   The factory renders at whatever size is asked for.</item>
/// </list>
/// Must be called on an STA thread (COM); the UI thread qualifies.
/// </summary>
public static class ShellIcons
{
    public static BitmapSource? Load(string parsingName, int pixelSize)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return null;

        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 ||
            factory is null)
        {
            return null;
        }

        try
        {
            // ICONONLY: never generate a thumbnail. For an image or video file a thumbnail
            // means decoding the content, which is far too slow for a results list.
            var hr = factory.GetImage(
                new NativeSize { Width = pixelSize, Height = pixelSize },
                ImageFlags.IconOnly,
                out var hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                return ToBitmapSource(hBitmap);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the HBITMAP's pixels out with GetDIBits rather than using
    /// Imaging.CreateBitmapSourceFromHBitmap, which drops the alpha channel of 32-bit DIB
    /// sections and paints transparent icon corners black.
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<NativeBitmap>(), out var bm) == 0) return null;

        var width = bm.Width;
        var height = Math.Abs(bm.Height);
        if (width <= 0 || height <= 0) return null;

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,      // negative = top-down, matching WPF's row order
                Planes = 1,
                BitCount = 32,
                Compression = 0,       // BI_RGB
            },
        };

        var stride = width * 4;
        var pixels = new byte[stride * height];

        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, hBitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        // Legacy icons can come back with an all-zero alpha channel; treating those as
        // premultiplied would make them fully transparent, so render them opaque instead.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }

        var source = BitmapSource.Create(
            width, height, 96, 96,
            hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32,
            null, pixels, stride);

        source.Freeze();
        return source;
    }

    // ---------------------------------------------------------------- interop

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ImageFlags flags, out IntPtr phbm);
    }

    [Flags]
    private enum ImageFlags
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hBitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
}
