using MSFS2024AIPilot.Core;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The menu reader backs up the probing engine by reading the ATC window off the screen.
/// These tests cover the parts that are pure arithmetic - thresholding, line splitting and
/// the comparison of a captured line with a rendered phrase - so they need no display.
/// </summary>
public class AtcMenuReaderTests
{
    private const int MenuWidth = 240;
    private const int MenuHeight = 300;

    /// <summary>Builds a bitmap from a luminance function; the panel is dark, ink bright.</summary>
    private static ScreenBitmap Bitmap(Func<int, int, float> luminance)
    {
        var pixels = new byte[MenuWidth * MenuHeight * 4];
        for (var y = 0; y < MenuHeight; y++)
        {
            for (var x = 0; x < MenuWidth; x++)
            {
                var value = (byte)Math.Clamp(luminance(x, y), 0, 255);
                var i = (y * MenuWidth + x) * 4;
                // BGRA, so the luminance goes into all three colour bytes.
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        return new ScreenBitmap(MenuWidth, MenuHeight, pixels);
    }

    /// <summary>An ATC menu: a dark panel with <param name="rows"/> evenly spaced lines.</summary>
    private static ScreenBitmap AtcMenu(int rows)
    {
        return Bitmap((x, y) =>
        {
            var rowInBand = y % 30;
            var bandIndex = y / 30;
            if (bandIndex >= rows) return 30;
            if (rowInBand is < 4 or > 16) return 30;

            // A blobby word, so the line has ink along its whole height.
            var columnInBand = x % 160;
            return columnInBand is >= 30 and < 60 ? 240 : 30;
        });
    }

    /// <summary>The same menu with the colours swapped, as a light theme would show it.</summary>
    private static ScreenBitmap LightTheme(ScreenBitmap menu)
    {
        var pixels = menu.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luminance = pixels[i];
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(255 - luminance);
        }

        return new ScreenBitmap(menu.Width, menu.Height, pixels);
    }

    [Fact]
    public void OtsuSeparatesThePanelFromTheText()
    {
        // A panel at 20-ish with text at 240.
        var image = AtcMenu(3).ToGrayscale();
        var threshold = AtcMenuLayout.Otsu(image);

        // The panel is the bulk of the window; the text is the smaller class.
        var textPixels = image.Count(v => v > threshold);
        Assert.True(threshold < 200, $"threshold {threshold} puts the panel on the text side");
        Assert.True(textPixels > 0, "no text was found on the text side of the split");
        Assert.True(textPixels < image.Length / 10, "the split called the panel text");
    }

    [Fact]
    public void MenuLinesAreSplitFromTheInkProfile()
    {
        var lines = AtcMenuLayout.Segment(AtcMenu(3), Options());

        Assert.Equal(3, lines.Count);
        // Lines are numbered from 1 at the top.
        Assert.Equal(new[] { 1, 2, 3 }, lines.Select(l => l.Number));

        foreach (var line in lines)
        {
            Assert.Equal(13, line.Height);
            Assert.True(line.Width > 100, $"line {line.Number} is only {line.Width} px wide");
            Assert.Equal(line.Width * line.Height, line.Gray.Length);
        }
        // The lines were found in order, top to bottom.
        Assert.True(lines[0].Top < lines[1].Top);
        Assert.True(lines[1].Top < lines[2].Top);
    }

    [Fact]
    public void TheTenthLineIsSelectedByTheZeroEvent()
    {
        var lines = AtcMenuLayout.Segment(AtcMenu(10), Options());

        // Ten lines means the simulator shows ten choices: 1-9 and 0.
        Assert.Equal(10, lines.Count);
        Assert.Equal(0, lines[^1].Number);
        Assert.Equal(1, lines[0].Number);
        Assert.Equal(0, AtcRequestEngine.MenuEventIndex(10));
        Assert.Equal(9, AtcRequestEngine.MenuEventIndex(9));
    }

    [Fact]
    public void BlankSpaceBetweenBandsIsNotAMenuLine()
    {
        var bitmap = Bitmap((x, y) =>
        {
            // Two bands in the top half; the bottom half is empty panel.
            if (y > 60) return 30;
            var rowInBand = y % 30;
            return rowInBand is >= 4 and <= 16 ? 240 : 30;
        });

        var lines = AtcMenuLayout.Segment(bitmap, Options());

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.Height <= 13));
    }

    private static AtcMenuReaderOptions Options() => new();

    [Fact]
    public void NormalisationIgnoresHowBrightThePanelIs()
    {
        var lines = AtcMenuLayout.Segment(AtcMenu(2), Options());
        var dark = AtcImageNormalizer.Normalize(lines[0].Gray, lines[0].Width, lines[0].Height);

        // The same menu on a light theme: the same shapes, different colours.
        var lightLines = AtcMenuLayout.Segment(LightTheme(AtcMenu(2)), Options());
        Assert.Equal(lines.Count, lightLines.Count);
        var bright = AtcImageNormalizer.Normalize(lightLines[0].Gray, lightLines[0].Width, lightLines[0].Height);

        Assert.Equal(AtcImageNormalizer.TargetWidth * AtcImageNormalizer.TargetHeight, dark.Length);
        Assert.True(AtcImageNormalizer.Similarity(dark, bright) > 0.95f,
            "inverting the theme changed the normalised image");
    }

    [Fact]
    public void SimilarityIsHighForTheSameShapesAndLowForDifferentOnes()
    {
        var band = new float[20 * 10];
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                band[y * 20 + x] = 240;

        var other = (float[])band.Clone();
        for (var i = 0; i < other.Length; i += 2) other[i] = 30;

        var first = AtcImageNormalizer.Normalize(band, 20, 10);
        var second = AtcImageNormalizer.Normalize(band, 20, 10);
        var third = AtcImageNormalizer.Normalize(other, 20, 10);

        Assert.Equal(1f, AtcImageNormalizer.Similarity(first, second), 6);
        Assert.True(AtcImageNormalizer.Similarity(first, third) < 0.9f,
            "two different line images compared as if they were the same");
    }

    [Fact]
    public void NormalisationFillsTheGrid()
    {
        // A small image is stretched to the fixed grid so any resolution compares.
        var tiny = new float[4 * 4];
        for (var i = 0; i < tiny.Length; i++) tiny[i] = i % 2 == 0 ? 200 : 60;

        var normalized = AtcImageNormalizer.Normalize(tiny, 4, 4);

        Assert.Equal(AtcImageNormalizer.TargetWidth * AtcImageNormalizer.TargetHeight, normalized.Length);

        var mean = normalized.Average();
        var deviation = Math.Sqrt(normalized.Sum(v => (v - mean) * (v - mean)) / normalized.Length);
        Assert.Equal(0, mean, 1e-3);
        Assert.Equal(1, deviation, 1e-3);
    }

    [Fact]
    public void EveryRequestKindHasPhrasesToRecognise()
    {
        foreach (var kind in new[]
                 {
                     AtcRequestKind.IFRClearance, AtcRequestKind.Taxi,
                     AtcRequestKind.Takeoff, AtcRequestKind.Landing
                 })
        {
            Assert.NotEmpty(AtcMenuPhrases.For(kind));
        }
    }
}

/// <summary>
/// How the reader feeds the probing engine: when it names a line, that line is pressed
/// first and the blind walk is kept only as a fallback.
/// </summary>
public class AtcMenuReaderIntegrationTests
{
    private sealed class FakeMenu : IAtcMenu
    {
        public List<string> Actions { get; } = new();
        public int LastIndex { get; private set; } = -1;

        public void Open() => Actions.Add("open");
        public void Close() => Actions.Add("close");
        public void ToggleWindow() => Actions.Add("toggle");

        public void Select(int item)
        {
            LastIndex = item;
            Actions.Add($"select:{item}");
        }

        public void TuneCom1(double frequencyMHz) => Actions.Add($"tune:{frequencyMHz:0.000}");
        public void SetTransponder(string code) => Actions.Add($"xpndr:{code}");
    }

    [Fact]
    public void ARecognisedLineIsPressedBeforeAnythingElse()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var options = new AtcRequestOptions
        {
            MenuSettle = TimeSpan.FromMilliseconds(400),
            EntryTimeout = TimeSpan.FromMilliseconds(1500),
            TotalTimeout = TimeSpan.FromSeconds(20),
            MaxEntry = 10,
            MaxEntriesProbed = 5,
            GracePeriod = TimeSpan.FromSeconds(2)
        };

        var engine = new AtcRequestEngine(menu, () => now, null, options);

        // The reader names line 7 as the one asking for a takeoff clearance.
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => menu.LastIndex == 7, "takeoff clearance",
            "takeoff|TaxiOut|TWR", null, null, () => new[] { 7 }));

        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();

        Assert.Equal(7, engine.ProbedEntry);
        Assert.Equal(new[] { "open", "select:7" }, menu.Actions);
        Assert.Equal(1, engine.PressedEntryCount);
    }

    [Fact]
    public void NoRecognitionFallsBackToProbingTheFirstLine()
    {
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var options = new AtcRequestOptions
        {
            MenuSettle = TimeSpan.FromMilliseconds(400),
            EntryTimeout = TimeSpan.FromMilliseconds(1500),
            TotalTimeout = TimeSpan.FromSeconds(20),
            MaxEntry = 10,
            MaxEntriesProbed = 5,
            GracePeriod = TimeSpan.FromSeconds(2)
        };

        var engine = new AtcRequestEngine(menu, () => now, null, options);
        Assert.True(engine.Begin(AtcRequestKind.Taxi, () => false, "taxi clearance",
            "taxi|TaxiOut|GND", null, null, () => Array.Empty<int>()));

        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();

        Assert.Equal(1, engine.ProbedEntry);
        Assert.Equal(new[] { "open", "select:1" }, menu.Actions);
    }

    [Fact]
    public void TheTenthLineIsPressedAsTheZeroEvent()
    {
        // The reader's tenth menu line is displayed "0" (the ATC_MENU_0 label) but the
        // request engine addresses it as line 10. LinesForRequest maps the display 0 to
        // 10, and the engine fires ATC_MENU_0 for it; before the mapping the tenth entry
        // - the one the reader is most useful for - was dropped.
        var menu = new FakeMenu();
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var options = new AtcRequestOptions
        {
            MenuSettle = TimeSpan.FromMilliseconds(400),
            EntryTimeout = TimeSpan.FromMilliseconds(1500),
            TotalTimeout = TimeSpan.FromSeconds(20),
            MaxEntry = 10,
            MaxEntriesProbed = 5,
            GracePeriod = TimeSpan.FromSeconds(2)
        };

        var engine = new AtcRequestEngine(menu, () => now, null, options);
        Assert.True(engine.Begin(AtcRequestKind.Takeoff, () => menu.LastIndex == 0, "takeoff clearance",
            "takeoff|TaxiOut|TWR", null, null, () => new[] { 10 }));

        now = now.AddMilliseconds(500);
        engine.Tick();
        now = now.AddMilliseconds(2100);
        engine.Tick();

        Assert.Equal(10, engine.ProbedEntry);
        Assert.Contains("select:0", menu.Actions);
    }
}
