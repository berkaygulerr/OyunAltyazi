using OyunAltyazi.Core;

namespace OyunAltyazi.Core.Tests;

public class CoverLayoutTests
{
    private static readonly DipRect Eng = new(300, 400, 400, 30);

    [Fact]
    public void Centered_and_bottom_aligned_growing_upward()
    {
        var (x, y) = CoverLayout.Place(500, 120, Eng, 1600, 900);
        Assert.Equal(Eng.CenterX, x + 250, 6);
        Assert.Equal(Eng.Bottom + CoverLayout.Pad, y + 120, 6);
    }

    [Fact]
    public void Clamped_left_right_top_bottom()
    {
        Assert.Equal(0, CoverLayout.Place(500, 50, new DipRect(0, 400, 100, 30), 1600, 900).X);
        Assert.Equal(1600 - 500, CoverLayout.Place(500, 50, new DipRect(1500, 400, 100, 30), 1600, 900).X);
        Assert.Equal(0, CoverLayout.Place(500, 300, new DipRect(300, 100, 400, 30), 1600, 900).Y);
        Assert.Equal(900 - 50, CoverLayout.Place(500, 50, new DipRect(300, 890, 400, 30), 1600, 900).Y);
    }
}
