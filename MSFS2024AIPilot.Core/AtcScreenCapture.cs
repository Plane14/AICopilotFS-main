using System.Runtime.InteropServices;

namespace MSFS2024AIPilot.Core;

/// <summary>
/// A rectangle of the desktop, in pixels, captured for reading. The default ATC window of
/// the simulator is drawn by the game itself - there are no window controls to query -
/// so the only way to read it from outside is to look at the pixels it produced.
/// </summary>
public readonly record struct AtcCaptureRegion(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Overlap of two regions; empty when they do not intersect.</summary>
    public static AtcCaptureRegion Intersect(AtcCaptureRegion a, AtcCaptureRegion b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top
            ? new AtcCaptureRegion(left, top, right - left, bottom - top)
            : default;
    }
}

/// <summary>
/// Captured pixels, stored as BGRA (the layout a Windows DIB section hands back), plus
/// the grey view the menu reader works with.
/// </summary>
public sealed class ScreenBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public ScreenBitmap(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length < width * height * 4) throw new ArgumentException("buffer too small", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>Rec.601 luminance, 0-255.</summary>
    public byte GrayAt(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (byte)((Pixels[i] * 299 + Pixels[i + 1] * 587 + Pixels[i + 2] * 114) / 1000);
    }

    /// <summary>Luminance of the whole image as 0-255 floats, row-major.</summary>
    public float[] ToGrayscale()
    {
        var gray = new float[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
                gray[(y * Width) + x] = GrayAt(x, y);
        }

        return gray;
    }

    /// <summary>The overlapping part of this bitmap, in these coordinates.</summary>
    public ScreenBitmap Crop(AtcCaptureRegion region)
    {
        var cut = AtcCaptureRegion.Intersect(region, new AtcCaptureRegion(0, 0, Width, Height));
        if (cut.IsEmpty) throw new ArgumentOutOfRangeException(nameof(region), "region is outside the bitmap");

        var pixels = new byte[cut.Width * cut.Height * 4];
        for (var y = 0; y < cut.Height; y++)
            Array.Copy(Pixels, ((cut.Top + y) * Width + cut.Left) * 4, pixels, y * cut.Width * 4, cut.Width * 4);

        return new ScreenBitmap(cut.Width, cut.Height, pixels);
    }
}

/// <summary>
/// Captures the desktop through GDI. No imaging library is used: the ATC window is read
/// from a straight Blt, which is the same pixels the compositor shows.
/// </summary>
public static class AtcScreenCapture
{
    private const int DIB_RGB_COLORS = 0;
    private const uint SRC_COPY = 0x00CC0020;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    /// <summary>Region covering the whole desktop (every monitor in a multi-monitor setup).</summary>
    public static AtcCaptureRegion Desktop()
    {
        var left = User32.GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = User32.GetSystemMetrics(SM_YVIRTUALSCREEN);
        return new AtcCaptureRegion(left, top,
            User32.GetSystemMetrics(SM_CXVIRTUALSCREEN), User32.GetSystemMetrics(SM_CYVIRTUALSCREEN));
    }

    /// <summary>
    /// Title fragments that identify the simulator window, newest first. The title of a
    /// loaded flight becomes "Microsoft Flight Simulator 2024" plus the flight details,
    /// so an exact match would break.
    /// </summary>
    public static readonly string[] SimulatorTitleFragments =
    {
        "Microsoft Flight Simulator",
        "MSFS",
        "Flight Simulator 2024",
        "Flight Simulator 2020"
    };

    /// <summary>
    /// Finds the simulator window. Returns the empty region when the simulator is not
    /// running, so the caller can fall back to a configured region rather than crash.
    /// </summary>
    public static AtcCaptureRegion FindSimulatorWindow()
    {
        var found = default(AtcCaptureRegion);
        User32.EnumWindows((handle, parameter) =>
        {
            if (!User32.IsWindowVisible(handle) || User32.GetWindowTextLength(handle) <= 0) return true;

            var title = User32.GetWindowTitle(handle);
            foreach (var fragment in SimulatorTitleFragments)
            {
                if (title.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (User32.IsIconic(handle)) return true;

                User32.GetWindowRect(handle, out var rect);
                found = new AtcCaptureRegion(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>Captures a region of the desktop.</summary>
    public static ScreenBitmap Capture(AtcCaptureRegion region)
    {
        if (region.IsEmpty) throw new ArgumentOutOfRangeException(nameof(region), "nothing to capture");

        // A null window is the whole desktop, which is what a capture without an explicit
        // region reads.
        var screen = User32.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("no desktop window station to capture");

        try
        {
            var memory = Gdi32.CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("cannot create a memory device context");

            try
            {
                return Blt(screen, memory, region);
            }
            finally
            {
                Gdi32.DeleteDC(memory);
            }
        }
        finally
        {
            User32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static ScreenBitmap Blt(IntPtr screen, IntPtr memory, AtcCaptureRegion region)
    {
        var header = new Gdi32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
            Width = region.Width,
            // A negative height requests a top-down bitmap, which matches how the menu
            // reader walks rows.
            Height = -region.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = 0
        };

        var dib = Gdi32.CreateDIBSection(screen, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            throw new InvalidOperationException("cannot create a capture bitmap");

        var previous = Gdi32.SelectObject(memory, dib);
        var pixels = new byte[region.Width * region.Height * 4];
        try
        {
            if (Gdi32.BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, SRC_COPY))
                Marshal.Copy(bits, pixels, 0, pixels.Length);
            else
                throw new InvalidOperationException("the screen could not be captured");
        }
        finally
        {
            Gdi32.SelectObject(memory, previous);
            Gdi32.DeleteObject(dib);
        }

        return new ScreenBitmap(region.Width, region.Height, pixels);
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class User32
    {
        public delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);

        // GetDC/ReleaseDC, not "GetDC0": the Win32 exports are GetDC(HWND) and
        // ReleaseDC(HWND, HDC), and a managed declaration of a name that does not exist
        // throws EntryPointNotFoundException on the first capture. Since these were the
        // only entry points in the ATC menu reader that did not exist, the whole off-screen
        // path failed into its "nothing recognised" fallback and never read a menu.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int maxLength);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

        public static string GetWindowTitle(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0) return string.Empty;

            var text = new System.Text.StringBuilder(length + 1);
            // The unsafe handle comes from EnumWindows in this same process.
            GetWindowText(handle, text, text.Capacity);
            return text.ToString();
        }
    }

    private static class Gdi32
    {
        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, int usage,
            out IntPtr bits, IntPtr section, long offset);

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
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
    }
}
