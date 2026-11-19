using OyunAltyazi.Core;

namespace OyunAltyazi.Core.Tests;

public class CoverLayoutTests
{
    // fake measurer: each char is 0.5*fs wide, line height 1.25*fs, greedy char wrap
    private static TextMetrics Measure(string? sp, string text, double fs, double maxW)
    {
        int perLine = Math.Max(1, (int)(maxW / (fs * 0.5)));
        int len = (sp is null ? 0 : sp.Length + 2) + text.Length;
        int lines = (len + perLine - 1) / perLine;
        double w = Math.Min(len, perLine) * fs * 0.5;
        return new TextMetrics(w, lines * fs * 1.25, lines);
    }

    private static readonly DipRect Eng = new(300, 400, 400, 30); // one line, 30 DIP high -> start font 26 (25.5 rounded)

    private static void AssertCovers(DipRect outer, DipRect inner)
    {
        Assert.True(outer.X <= inner.X + 1e-9 && outer.Y <= inner.Y + 1e-9 && outer.Right >= inner.Right - 1e-9 && outer.Bottom >= inner.Bottom - 1e-9);
        Assert.Equal(inner.CenterX, outer.CenterX, 6);
        Assert.Equal(inner.CenterY, outer.CenterY, 6);
    }

    [Fact]
    public void Short_text_keeps_start_size_and_english_width()
    {
        var f = CoverLayout.Fit(Eng, 1, "Hemen gidin", null, 1000, Measure);
        Assert.Equal(26, f.FontSize);
        Assert.Equal(1, f.Lines);
        Assert.Equal(Eng.Width + 12, f.Box.Width);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Long_text_widens_symmetrically_before_wrapping()
    {
        var text = new string('a', 50); // 50*13 = 650 DIP at 26
        var f = CoverLayout.Fit(Eng, 1, text, null, 1000, Measure);
        Assert.Equal(26, f.FontSize);
        Assert.Equal(1, f.Lines);
        Assert.True(f.Box.Width > Eng.Width + 12 && f.Box.Width <= 1000);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Wraps_to_at_most_lineCount_plus_one_then_shrinks()
    {
        var text = new string('a', 120);
        var f = CoverLayout.Fit(Eng, 1, text, null, 700, Measure);
        Assert.True(f.Lines <= 2);
        Assert.True(f.FontSize < 26);          // 3 lines at 26 -> shrink needed
        Assert.True(f.Box.Width <= 700);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Two_line_english_allows_three_lines_at_start_size()
    {
        var eng = new DipRect(100, 100, 600, 80); // 2 lines -> start 0.85*40 = 34
        var f = CoverLayout.Fit(eng, 2, new string('a', 100), null, 700, Measure);
        Assert.Equal(34, f.FontSize);
        Assert.True(f.Lines <= 3);
    }

    [Fact]
    public void Never_below_min_font_and_still_covers()
    {
        var f = CoverLayout.Fit(Eng, 1, new string('a', 5000), "Bob", 500, Measure);
        Assert.Equal(CoverLayout.MinFont, f.FontSize);
        Assert.True(f.Box.Width <= 500);
        AssertCovers(f.Box, Eng);
        var tiny = CoverLayout.Fit(new DipRect(0, 0, 50, 10), 1, "x", null, 400, Measure);
        Assert.Equal(CoverLayout.MinFont, tiny.FontSize);
        AssertCovers(tiny.Box, new DipRect(0, 0, 50, 10));
    }

    [Fact]
    public void Box_never_narrower_than_english_even_if_max_width_is_small()
    {
        var f = CoverLayout.Fit(Eng, 1, "ab", null, 100, Measure);
        Assert.True(f.Box.Width >= Eng.Width);
        Assert.True(f.Box.Height >= Eng.Height);
    }
}

public class OverlayMappingTests
{
    // Window frame at screen (100,50) px, 1600x900 px; overlay (client area) at (108,81) px.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Maps_frame_fraction_to_overlay_dips(double dpi)
    {
        var box = new RegionF(0.25, 0.8, 0.5, 0.1);
        var d = OverlayMapping.FrameBoxToOverlayDips(box, 1600, 900, 100, 50, 108, 81, dpi, dpi);
        // physical: x = 100+400 = 500, y = 50+720 = 770, w=800, h=90
        Assert.Equal((500 - 108) / dpi, d.X, 6);
        Assert.Equal((770 - 81) / dpi, d.Y, 6);
        Assert.Equal(800 / dpi, d.Width, 6);
        Assert.Equal(90 / dpi, d.Height, 6);
    }

    [Fact]
    public void At_200_percent_a_1600px_box_is_800_dips()
    {
        var d = OverlayMapping.FrameBoxToOverlayDips(new RegionF(0, 0, 1, 1), 1600, 900, 0, 0, 0, 0, 2, 2);
        Assert.Equal(800, d.Width);
        Assert.Equal(450, d.Height);
    }
}
