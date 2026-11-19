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

    private static readonly DipRect Eng = new(300, 400, 400, 30);

    private static void AssertCovers(DipRect outer, DipRect inner)
    {
        Assert.True(outer.X <= inner.X + 1e-9 && outer.Y <= inner.Y + 1e-9 && outer.Right >= inner.Right - 1e-9 && outer.Bottom >= inner.Bottom - 1e-9);
    }

    [Fact]
    public void Short_text_keeps_configured_font_regardless_of_english_height()
    {
        var f = CoverLayout.Fit(Eng, "Hemen gidin", null, 1000, 32, Measure);
        Assert.Equal(32, f.FontSize);
        Assert.Equal(Eng.Width + 24, f.Box.Width);
        Assert.Equal(Eng.CenterX, f.Box.CenterX, 6);
        AssertCovers(f.Box, Eng);
        var tiny = CoverLayout.Fit(new DipRect(300, 400, 400, 10), "Hemen gidin", null, 1000, 40, Measure);
        Assert.Equal(40, tiny.FontSize);
    }

    [Fact]
    public void Long_text_keeps_font_and_widens_up_to_region()
    {
        var f = CoverLayout.Fit(Eng, new string('a', 50), null, 1000, 32, Measure); // 800 DIP at 32
        Assert.Equal(32, f.FontSize);
        Assert.Equal(1, f.Lines);
        Assert.True(f.Box.Width > Eng.Width + 24 && f.Box.Width <= 1000);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Taller_box_grows_upward_with_bottom_aligned_to_english()
    {
        var f = CoverLayout.Fit(Eng, new string('a', 120), null, 700, 32, Measure);
        Assert.Equal(3, f.Lines);
        Assert.Equal(32, f.FontSize);
        Assert.True(f.Box.Height > Eng.Height + 12);
        Assert.Equal(Eng.Bottom + CoverLayout.PadV, f.Box.Bottom, 6);
        Assert.True(f.Box.Y < Eng.Y);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Floor_24_only_when_more_than_three_lines()
    {
        var ok = CoverLayout.Fit(Eng, new string('a', 100), null, 700, 32, Measure); // 3 lines at 32
        Assert.Equal(32, ok.FontSize);
        var f = CoverLayout.Fit(Eng, new string('a', 5000), "Bob", 500, 32, Measure);
        Assert.Equal(CoverLayout.MinFont, f.FontSize);
        Assert.True(f.Box.Width <= 500);
        AssertCovers(f.Box, Eng);
        var g = CoverLayout.Fit(Eng, new string('a', 60), null, 700, 32, Measure); // 2 lines -> no shrink
        Assert.Equal(32, g.FontSize);
    }

    [Fact]
    public void Clamped_to_bounds_but_still_covers_english()
    {
        var bounds = new DipRect(0, 380, 1000, 100);
        var f = CoverLayout.Fit(Eng, new string('a', 120), null, 700, 32, Measure, bounds);
        AssertCovers(f.Box, Eng);
    }

    [Fact]
    public void Box_never_narrower_than_english_even_if_max_width_is_small()
    {
        var f = CoverLayout.Fit(Eng, "ab", null, 100, 32, Measure);
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
