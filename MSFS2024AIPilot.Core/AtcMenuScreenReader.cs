using System.Runtime.InteropServices;

namespace MSFS2024AIPilot.Core;

/// <summary>One menu line found on the captured ATC window.</summary>
public sealed class AtcMenuLineImage
{
    /// <summary>Display number of the line: 1-9 for the first nine, 0 for the tenth.</summary>
    public int Number { get; init; }

    /// <summary>Left/top of the line within the captured bitmap.</summary>
    public int Left { get; init; }
    public int Top { get; init; }

    /// <summary>Size of the cropped line.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Luminance of the cropped line, row-major.</summary>
    public float[] Gray { get; init; } = Array.Empty<float>();

    /// <summary>The key event that selects this line.</summary>
    public int MenuEventIndex => AtcRequestEngine.MenuEventIndex(Number);
}

/// <summary>
/// What the reader saw on the ATC window: one entry per segmented line, with the phrase
/// it was matched to and how confident the match was.
/// </summary>
public sealed record AtcMenuEntry(int Line, string? Text, float Score);

/// <summary>
/// How confident a match must be before the reader is trusted to pick a menu line. Below
/// this the line is left unmatched and the request engine keeps probing, so a bad
/// recognition costs a little time and never a wrong clearance.
/// </summary>
public sealed record AtcMenuReaderOptions
{
    public float MinimumScore { get; init; } = 0.55f;

    /// <summary>Region to capture; null uses the simulator window, then the desktop.</summary>
    public AtcCaptureRegion? CaptureRegion { get; init; }

    /// <summary>Extra phrases offered for a request kind, on top of the known ones.</summary>
    public IReadOnlyList<string>? ExtraPhrases { get; init; }

    /// <summary>
    /// Replaces the phrases known for the request kind, for reading a menu with nothing
    /// in particular being asked of it.
    /// </summary>
    public IReadOnlyList<string>? PhrasesOverride { get; init; }

    /// <summary>Smallest image considered a text line, in pixels.</summary>
    public int MinimumLineHeight { get; init; } = 5;

    /// <summary>Fraction of a row that must contain ink for the row to be text.</summary>
    public double MinimumInkFraction { get; init; } = 0.004;
}

/// <summary>
/// Reads the ATC menu off the screen. The simulator draws the menu itself, so there is no
/// text to query: the pipeline is capture, segment the lines, then recognise each line by
/// rendering the candidate phrases and matching the pixels. Everything here is GDI and
/// arithmetic, so no OCR package is needed.
///
/// The reader is a *backup* to the probing engine: when recognition is confident it names
/// the line to press, and when it is not it says nothing and the engine probes as before.
/// </summary>
public sealed class AtcMenuReader
{
    public AtcMenuReaderOptions Options { get; }

    private readonly IGlyphRenderer _renderer;
    private readonly AtcRequestKind _kind;
    private readonly List<string> _candidates;
    private readonly Dictionary<string, float[]> _templates = new(StringComparer.Ordinal);

    /// <summary>The menu from the most recent read.</summary>
    public IReadOnlyList<AtcMenuEntry> LastMenuRead { get; private set; } = Array.Empty<AtcMenuEntry>();

    public AtcMenuReader(IGlyphRenderer renderer, AtcRequestKind kind, AtcMenuReaderOptions? options = null)
    {
        _renderer = renderer;
        _kind = kind;
        Options = options ?? new AtcMenuReaderOptions();

        var phrases = Options.PhrasesOverride ?? AtcMenuPhrases.For(kind);
        _candidates = new List<string>(phrases);
        if (Options.ExtraPhrases != null)
            _candidates.AddRange(Options.ExtraPhrases.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>What the window shows right now, in top-to-bottom order.</summary>
    public IReadOnlyList<AtcMenuEntry> ReadMenu()
    {
        var bitmap = AtcScreenCapture.Capture(ResolveRegion());
        LastMenuRead = ReadMenu(bitmap);
        return LastMenuRead;
    }

    /// <summary>Recognises the lines of an already captured bitmap.</summary>
    public IReadOnlyList<AtcMenuEntry> ReadMenu(ScreenBitmap bitmap)
    {
        var lines = AtcMenuLayout.Segment(bitmap, Options);
        var entries = new List<AtcMenuEntry>(lines.Count);

        foreach (var line in lines)
        {
            var best = BestMatch(line);
            entries.Add(new AtcMenuEntry(line.Number, best.Text, best.Score));
        }

        return entries;
    }

    /// <summary>
    /// Menu lines that name the request, most confident first. Empty when the reader is
    /// not confident, which tells the caller to probe instead. The menu is captured fresh
    /// on every call.
    /// <para>
    /// The numbers returned are the request engine's 1-10 numbering, not the display
    /// numbering the lines are captured with: the tenth band is displayed as "0" (the
    /// <c>ATC_MENU_0</c> label) but the engine addresses it as line 10, so a 0 is mapped
    /// to 10 here or the tenth entry - the one the reader is most useful for - is dropped.
    /// </para>
    /// <para>
    /// Reusing the previous read was wrong: the reader object is cached per request kind by
    /// the hub, so a second request for the same kind was handed the first one's entries.
    /// The menu had moved, and the line it was told to press was a line number that no
    /// longer referred to the same question.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> LinesForRequest()
    {
        var entries = ReadMenu();
        return entries
            .Where(e => e.Text != null && e.Score >= Options.MinimumScore)
            .OrderByDescending(e => e.Score)
            .Select(e => e.Line == 0 ? 10 : e.Line)
            .ToList();
    }

    private (string? Text, float Score) BestMatch(AtcMenuLineImage line)
    {
        var linePixels = AtcImageNormalizer.Normalize(line.Gray, line.Width, line.Height);
        string? bestText = null;
        var bestScore = 0f;

        foreach (var phrase in _candidates)
        {
            var text = $"{line.Number}. {phrase}";
            if (!_templates.TryGetValue(text, out var template))
            {
                var rendered = _renderer.Render(text);
                _templates[text] = template =
                    AtcImageNormalizer.Normalize(rendered.ToGrayscale(), rendered.Width, rendered.Height);
            }

            var score = AtcImageNormalizer.Similarity(linePixels, template);
            if (score <= bestScore) continue;

            bestScore = score;
            bestText = phrase;
        }

        return bestScore >= Options.MinimumScore ? (bestText, bestScore) : (null, 0f);
    }

    private AtcCaptureRegion ResolveRegion()
    {
        if (Options.CaptureRegion is { IsEmpty: false } region) return region;

        var window = AtcScreenCapture.FindSimulatorWindow();
        return window.IsEmpty ? AtcScreenCapture.Desktop() : window;
    }
}

/// <summary>
/// The phrases the simulator's ATC menu uses, by request. The labels change between
/// versions and are localised, so several spellings of the same question are offered and
/// the recogniser takes the best match.
/// </summary>
public static class AtcMenuPhrases
{
    public static IReadOnlyList<string> For(AtcRequestKind kind) => kind switch
    {
        AtcRequestKind.IFRClearance => new[]
        {
            "Request IFR clearance",
            "Request IFR clearance for instrument flight plan",
            "Request clearance",
            "File IFR flight plan"
        },
        AtcRequestKind.Taxi => new[]
        {
            "Request taxi",
            "Request taxi clearance",
            "Request taxi to runway",
            "Taxi to runway"
        },
        AtcRequestKind.Takeoff => new[]
        {
            "Request takeoff clearance",
            "Line up and wait",
            "Takeoff",
            "Request lineup"
        },
        AtcRequestKind.Landing => new[]
        {
            "Request landing clearance",
            "Request full stop landing",
            "Request approach",
            "Land"
        },
        // A pushback is a two-step menu: the request itself, and then the direction the
        // tug is to take. Both are offered, because the line that grants the request is
        // the direction one once the sub-menu has opened, and a reader that only knew
        // the first line would fall back to probing exactly where it is most needed.
        AtcRequestKind.Pushback => new[]
        {
            "Request pushback",
            "Pushback",
            "Request pushback approval",
            "Straight",
            "Left 45",
            "Left 90",
            "Right 45",
            "Right 90"
        },
        _ => Array.Empty<string>()
    };
}

/// <summary>
/// Pure image maths shared by the menu reader: crop to the ink, rescale to a fixed grid
/// and normalise contrast, then compare. Kept free of GDI so it can be tested without a
/// desktop.
/// </summary>
internal static class AtcImageNormalizer
{
    public const int TargetWidth = 128;
    public const int TargetHeight = 16;

    /// <summary>
    /// Crops an image to the pixels that differ from the background, rescales it to the
    /// standard grid with a box filter and normalises it to zero mean and unit variance,
    /// so the result is independent of position and of the menu's colour scheme.
    /// </summary>
    public static float[] Normalize(float[] gray, int width, int height)
    {
        var bounds = InkBounds(gray, width, height);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new float[TargetWidth * TargetHeight];

        var output = new float[TargetWidth * TargetHeight];
        for (var y = 0; y < TargetHeight; y++)
        {
            for (var x = 0; x < TargetWidth; x++)
            {
                // Box-filter resample of the cropped source.
                var sourceX = bounds.Left + (int)((long)x * bounds.Width / TargetWidth);
                var sourceY = bounds.Top + (int)((long)y * bounds.Height / TargetHeight);
                output[(y * TargetWidth) + x] = gray[(sourceY * width) + sourceX];
            }
        }

        NormalizeContrast(output);
        return output;
    }

    /// <summary>
    /// Pearson correlation of two normalised images, mapped to 0-1. Both inputs are
    /// already zero-mean unit-variance, so this is the average of their product.
    /// </summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;

        double product = 0;
        for (var i = 0; i < a.Length; i++) product += a[i] * b[i];

        var correlation = product / a.Length;
        return (float)Math.Max(0, Math.Min(1, (correlation + 1) / 2));
    }

    private static void NormalizeContrast(float[] output)
    {
        double mean = 0;
        foreach (var value in output) mean += value;
        mean /= output.Length;

        double variance = 0;
        foreach (var value in output)
        {
            var delta = value - mean;
            variance += delta * delta;
        }

        var deviation = Math.Sqrt(variance / output.Length);
        if (deviation < 1e-6) return;

        for (var i = 0; i < output.Length; i++) output[i] = (float)((output[i] - mean) / deviation);
    }

    private static (int Left, int Top, int Width, int Height) InkBounds(float[] gray, int width, int height)
    {
        var background = Background(gray);
        var left = width;
        var top = height;
        var right = 0;
        var bottom = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (Math.Abs(gray[(y * width) + x] - background) < 24) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        return right >= left && bottom >= top
            ? (left, top, right - left + 1, bottom - top + 1)
            : (0, 0, 0, 0);
    }

    /// <summary>The most common luminance, which for a menu window is the panel.</summary>
    private static float Background(float[] gray)
    {
        var histogram = new int[256];
        foreach (var value in gray)
        {
            var bucket = (int)Math.Round(value);
            if (bucket >= 0 && bucket < histogram.Length) histogram[bucket]++;
        }

        var best = 0;
        for (var i = 1; i < histogram.Length; i++)
            if (histogram[i] > histogram[best]) best = i;

        return best;
    }
}

/// <summary>
/// Turns a captured bitmap into the rows of text it shows. The ATC menu is a stack of
/// evenly spaced lines, so the ink profile of the rows is enough to split it: the
/// background of the window is a single colour and the text is whatever departs from it,
/// which works for the light-on-dark and dark-on-light themes alike.
/// </summary>
internal static class AtcMenuLayout
{
    public static IReadOnlyList<AtcMenuLineImage> Segment(ScreenBitmap bitmap, AtcMenuReaderOptions options)
    {
        var gray = bitmap.ToGrayscale();
        var threshold = Otsu(gray);
        var brightInk = InkIsBrighterThanBackground(gray, threshold);

        var bands = new List<(int Top, int Bottom)>();
        var start = -1;
        var gap = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            if (RowIsText(gray, bitmap.Width, y, threshold, brightInk, options.MinimumInkFraction))
            {
                if (start < 0) start = y;
                gap = 0;
                continue;
            }

            if (start < 0) continue;

            // A blank row inside a band only ends it once it has been blank for a while:
            // one or two blank rows are what a hyphen, an apostrophe or a descender gap
            // looks like, and closing on the first one split a single menu line in two and
            // shifted the line number of everything after it.
            if (++gap > LineGap)
            {
                bands.Add((start, y - gap));
                start = -1;
                gap = 0;
            }
        }

        if (start >= 0) bands.Add((start, bitmap.Height - 1));

        var lines = new List<AtcMenuLineImage>();
        for (var index = 0; index < bands.Count && lines.Count < MaxMenuLines; index++)
        {
            var (top, bottom) = bands[index];
            if (bottom - top + 1 < options.MinimumLineHeight) continue;

            var left = bitmap.Width;
            var right = 0;
            for (var y = top; y <= bottom; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (!IsInk(gray[(y * bitmap.Width) + x], threshold, brightInk)) continue;
                    if (left > x) left = x;
                    if (right < x) right = x;
                }
            }

            if (right < left) continue;

            var crop = new float[(right - left + 1) * (bottom - top + 1)];
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var value = gray[(y * bitmap.Width) + x];
                    // Store the line with the text always bright, so a light theme and a
                    // dark one produce the same pixels to compare.
                    crop[((y - top) * (right - left + 1)) + (x - left)] = brightInk ? value : 255 - value;
                }
            }

            lines.Add(new AtcMenuLineImage
            {
                // The first line is menu entry 1; the tenth is entry 0, which is what
                // ATC_MENU_0 selects. A band beyond the tenth has no entry at all, so the
                // menu is capped there rather than numbered 0 again and pressed twice.
                Number = MenuEntryIndex(lines.Count),
                Left = left,
                Top = top,
                Width = right - left + 1,
                Height = bottom - top + 1,
                Gray = crop
            });
        }

        return lines;
    }

    /// <summary>Highest menu entry the simulator's ATC menu has: 1-9, then 0 for the tenth.</summary>
    public const int MaxMenuLines = 10;

    /// <summary>
    /// The ATC_MENU_n index the line at a zero-based position selects, or -1 when the
    /// position has no entry at all. The first nine bands are entries 1-9 and the tenth is
    /// entry 0; anything past that is a band the simulator does not show, and it must read
    /// as "no entry" rather than as 0 again, which is the tenth entry.
    /// </summary>
    public static int MenuEntryIndex(int position)
    {
        if (position < 0) return -1;
        if (position < MaxMenuLines - 1) return position + 1;
        return position == MaxMenuLines - 1 ? 0 : -1;
    }

    private const int LineGap = 3;

    /// <summary>True when a pixel is on the text side of the threshold.</summary>
    private static bool IsInk(float luminance, int threshold, bool brightInk) =>
        brightInk ? luminance > threshold : luminance <= threshold;

    private static bool RowIsText(float[] gray, int width, int y, int threshold, bool brightInk,
        double minimumFraction)
    {
        var inkPixels = 0;
        for (var x = 0; x < width; x++)
        {
            if (IsInk(gray[(y * width) + x], threshold, brightInk)) inkPixels++;
        }

        return inkPixels >= Math.Max(1, (int)(width * minimumFraction));
    }

    /// <summary>
    /// Which side of the threshold the text sits on. The panel behind the text covers
    /// most of the window, so the minority class is the text: this is what makes the same
    /// code work for a light theme and a dark one.
    /// </summary>
    private static bool InkIsBrighterThanBackground(float[] gray, int threshold)
    {
        var darker = 0;
        var brighter = 0;
        foreach (var value in gray)
        {
            if (value <= threshold) darker++;
            else brighter++;
        }

        return brighter < darker;
    }

    /// <summary>
    /// Otsu's threshold: the split that best separates the panel from the text. The value
    /// returned is the highest level of the darker class, so text is whatever lies above
    /// it when the text is brighter than the panel, and whatever lies at or below it when
    /// it is darker.
    /// </summary>
    public static int Otsu(float[] gray)
    {
        var histogram = new int[256];
        foreach (var value in gray)
        {
            var bucket = (int)Math.Round(value);
            if (bucket >= 0 && bucket < histogram.Length) histogram[bucket]++;
        }

        var total = gray.LongLength;
        double sum = 0;
        for (var i = 0; i < histogram.Length; i++) sum += (long)i * histogram[i];

        double backgroundWeight = 0;
        double backgroundSum = 0;
        var best = 128f;
        var bestVariance = -1d;

        for (var i = 0; i < histogram.Length; i++)
        {
            backgroundWeight += histogram[i];
            if (backgroundWeight == 0) continue;

            var foregroundWeight = total - backgroundWeight;
            if (foregroundWeight == 0) break;

            backgroundSum += (long)i * histogram[i];
            var backgroundMean = backgroundSum / backgroundWeight;
            var foregroundMean = (sum - backgroundSum) / foregroundWeight;

            var delta = backgroundMean - foregroundMean;
            var variance = backgroundWeight * foregroundWeight * delta * delta;
            if (variance <= bestVariance) continue;

            bestVariance = variance;
            best = i;
        }

        return (int)best;
    }
}

/// <summary>Draws a candidate phrase so it can be compared with the captured menu.</summary>
public interface IGlyphRenderer
{
    ScreenBitmap Render(string text);
}

/// <summary>
/// Renders candidate phrases with GDI in the font the simulator's UI uses, so the same
/// words in the same font can be matched pixel by pixel against the menu. Font and size
/// are approximations: the recogniser compares shapes after normalisation, which tolerates
/// small differences, and a poor match simply yields no answer.
/// </summary>
public sealed class GdiGlyphRenderer : IGlyphRenderer
{
    private const int PointSize = 22;
    private const string FontFace = "Segoe UI";

    public ScreenBitmap Render(string text)
    {
        var screen = User32.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new InvalidOperationException("no desktop device to draw on");

        try
        {
            var memory = Gdi.CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("cannot create a drawing context");

            try
            {
                return Draw(screen, memory, text);
            }
            finally
            {
                Gdi.DeleteDC(memory);
            }
        }
        finally
        {
            User32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static ScreenBitmap Draw(IntPtr screen, IntPtr memory, string text)
    {
        var canvasWidth = CanvasWidth;
        var canvasHeight = CanvasHeight;
        var header = new Gdi.BitmapInfoHeader
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Gdi.BitmapInfoHeader>(),
            Width = canvasWidth,
            Height = -canvasHeight,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };

        var dib = Gdi.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) throw new InvalidOperationException("cannot create a drawing bitmap");

        // The bitmap goes in first: selecting the font afterwards would deselect it again.
        var previousBitmap = Gdi.SelectObject(memory, dib);
        var font = Gdi.CreateFont(-PointSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, FontFace);
        if (font == IntPtr.Zero)
        {
            Gdi.SelectObject(memory, previousBitmap);
            Gdi.DeleteObject(dib);
            throw new InvalidOperationException("cannot create the text font");
        }

        var previousFont = Gdi.SelectObject(memory, font);
        var pixels = new byte[canvasWidth * canvasHeight * 4];
        try
        {
            Gdi.SetBkColor(memory, 0);
            Gdi.SetTextColor(memory, 0x00FFFFFF);
            Gdi.TextOut(memory, Padding, Padding, text, text.Length);
            if (bits != IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        }
        finally
        {
            Gdi.SelectObject(memory, previousFont);
            Gdi.DeleteObject(font);
            Gdi.SelectObject(memory, previousBitmap);
            Gdi.DeleteObject(dib);
        }

        return new ScreenBitmap(canvasWidth, canvasHeight, pixels);
    }

    private const int CanvasWidth = 1600;
    private const int CanvasHeight = 64;
    private const int Padding = 8;

    private static class User32
    {
        // GetDC/ReleaseDC, not "GetDc0": the Win32 exports are GetDC(HWND) and
        // ReleaseDC(HWND, HDC). Declaring a name that does not exist makes the runtime
        // throw EntryPointNotFoundException on the first call, which is how the
        // off-screen menu reader used to fail every time it was asked for a template.
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    }

    private static class Gdi
    {
        [System.Runtime.InteropServices.DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteDC(IntPtr dc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern bool DeleteObject(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

        [System.Runtime.InteropServices.DllImport("gdi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
            int weight, uint italic, uint underline, uint strikeOut, uint charset, uint outputPrecision,
            uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr dc, uint color);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern uint SetBkColor(IntPtr dc, uint color);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, int usage,
            out IntPtr bits, IntPtr section, long offset);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
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
