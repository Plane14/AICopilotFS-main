using MSFS2024AIPilot.Core;
using Xunit;

namespace MSFS2024AIPilot.Tests;

/// <summary>
/// The band splitter decides how many menu lines the ATC window has, and the line number
/// is the ATC_MENU_n event that selects it. A line split in two shifts the number of every
/// line below it, so the aircraft would press the wrong menu entry, and a band past the
/// tenth would be numbered 0 again and pressed twice as the tenth.
/// </summary>
public class MenuLineSegmentationTests
{
    private const int Width = 240;

    private static ScreenBitmap Bitmap(Func<int, int, float> luminance, int height = 300)
    {
        var pixels = new byte[Width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var value = (byte)Math.Clamp(luminance(x, y), 0, 255);
                var i = (y * Width + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        return new ScreenBitmap(Width, height, pixels);
    }

    /// <summary>
    /// A line of text with a gap in the middle of it, the way an apostrophe, a hyphen or
    /// the crossbar of a word shows in the ink profile.
    /// </summary>
    private static ScreenBitmap LineWithInternalGap(int gapRows)
    {
        const int InkStart = 40;
        const int InkEnd = 64;
        const int GapFrom = 52;

        var gapTo = GapFrom + gapRows - 1;

        return Bitmap((x, y) =>
        {
            if (y >= 100) return 30;
            if (y is < InkStart or > InkEnd) return 30;
            if (y >= GapFrom && y <= gapTo) return 30;

            var column = x % 160;
            return column is >= 20 and < 90 ? 240 : 30;
        }, height: 100);
    }

    [Fact]
    public void AGapInsideALineIsStillOneLine()
    {
        // The splitter used to end the band on the first blank row, so the crossbar of a
        // letter or the gap above an apostrophe turned one line into two and shifted the
        // line number of every entry below it.
        var lines = AtcMenuLayout.Segment(LineWithInternalGap(1), new AtcMenuReaderOptions());

        Assert.Single(lines);
    }

    [Fact]
    public void AGapOfTwoRowsIsStillOneLine()
    {
        var lines = AtcMenuLayout.Segment(LineWithInternalGap(2), new AtcMenuReaderOptions());

        Assert.Single(lines);
    }

    [Fact]
    public void TwoSeparateLinesAreStillTwoLines()
    {
        // A gap wider than the tolerance is the gap between lines, which still splits.
        var lines = AtcMenuLayout.Segment(LineWithInternalGap(8), new AtcMenuReaderOptions());

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ALineWithAnInternalGapKeepsItsFullHeight()
    {
        var lines = AtcMenuLayout.Segment(LineWithInternalGap(2), new AtcMenuReaderOptions());
        var single = AtcMenuLayout.Segment(LineWithInternalGap(0), new AtcMenuReaderOptions());

        // The height of a band is measured from its first to its last row of ink, so a gap
        // inside it does not shorten the line by the gap's height.
        Assert.Equal(single[0].Height, lines[0].Height);
    }

    [Fact]
    public void LineNumbersAreOneToNineThenZero()
    {
        for (var position = 0; position < 9; position++)
        {
            Assert.Equal(position + 1, AtcMenuLayout.MenuEntryIndex(position));
        }

        // The tenth band has no entry of its own: it is selected by ATC_MENU_0, the tenth.
        Assert.Equal(0, AtcMenuLayout.MenuEntryIndex(9));
    }

    [Fact]
    public void ALinePastTheTenthSelectsNoEntry()
    {
        // Bands beyond the tenth used to be numbered 0 again, which meant ATC_MENU_0 was
        // pressed for the tenth line and then again for the eleventh. There is no eleventh
        // entry to select, so the answer is "none".
        Assert.Equal(0, AtcMenuLayout.MenuEntryIndex(9));
        Assert.Equal(-1, AtcMenuLayout.MenuEntryIndex(10));
        Assert.Equal(-1, AtcMenuLayout.MenuEntryIndex(11));
    }

    [Fact]
    public void OnlyTenBandsAreNumberedAsMenuLines()
    {
        // Twelve bands of text: the simulator's menu holds ten entries, so the reader must
        // stop at ten rather than reporting lines that select nothing.
        var bitmap = Bitmap((x, y) =>
        {
            var rowInBand = y % 20;
            var band = y / 20;
            if (rowInBand is < 4 or > 12) return 30;
            if (band >= 12) return 30;

            var column = x % 160;
            return column is >= 30 and < 70 ? 240 : 30;
        }, height: 260);

        var lines = AtcMenuLayout.Segment(bitmap, new AtcMenuReaderOptions { MinimumLineHeight = 5 });

        Assert.Equal(AtcMenuLayout.MaxMenuLines, lines.Count);
        Assert.Equal(0, lines[^1].Number);
        Assert.Equal(1, lines[0].Number);
    }
}
