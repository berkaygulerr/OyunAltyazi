using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class ChangeGateTests
{
    private static Frame Make(int w, int h, byte v = 0)
    {
        var b = new byte[w * h * 4];
        if (v != 0) Array.Fill(b, v);
        return new Frame(w, h, w * 4, b, 0);
    }

    [Fact] public void First_frame_changed() => Assert.True(new ChangeGate().HasChanged(Make(400, 80)));

    [Fact] public void Identical_frame_not_changed()
    {
        var g = new ChangeGate(); g.HasChanged(Make(400, 80, 50));
        Assert.False(g.HasChanged(Make(400, 80, 50)));
    }

    [Fact] public void Small_noise_not_changed()
    {
        var g = new ChangeGate(); var a = Make(400, 80, 50); g.HasChanged(a);
        var b = Make(400, 80, 50);
        for (int i = 0; i < b.Bgra.Length; i++) b.Bgra[i] = (byte)(b.Bgra[i] + (i % 2 == 0 ? 1 : -1));
        Assert.False(g.HasChanged(b));
    }

    [Fact] public void Text_appearing_changed()
    {
        var g = new ChangeGate(); g.HasChanged(Make(800, 100));
        var f = Make(800, 100);
        for (int y = 40; y < 60; y++)
            for (int x = 300; x < 500; x++)
            {
                int p = y * f.Stride + x * 4;
                f.Bgra[p] = f.Bgra[p + 1] = f.Bgra[p + 2] = f.Bgra[p + 3] = 255;
            }
        Assert.True(g.HasChanged(f));
    }

    [Fact] public void Different_size_frame_changed()
    {
        var g = new ChangeGate(); g.HasChanged(Make(400, 80));
        Assert.True(g.HasChanged(Make(401, 80)));
    }

    private static void Bar(Frame f, int x0, int x1, int y0, int y1, byte v)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int p = y * f.Stride + x * 4;
                f.Bgra[p] = f.Bgra[p + 1] = f.Bgra[p + 2] = f.Bgra[p + 3] = v;
            }
    }

    [Fact] public void Same_length_line_swap_is_detected()
    {
        // two lines of equal length cover the same area; only the glyph pattern differs
        var g = new ChangeGate();
        var a = Make(1344, 120);
        for (int k = 0; k < 20; k++) Bar(a, 300 + k * 30, 300 + k * 30 + 12, 50, 70, 255);
        g.HasChanged(a);
        var b = Make(1344, 120);
        for (int k = 0; k < 20; k++) Bar(b, 306 + k * 30, 306 + k * 30 + 12, 50, 70, 255);
        Assert.True(g.HasChanged(b));
    }

    [Fact] public void Single_word_change_is_detected()
    {
        var g = new ChangeGate();
        var a = Make(1344, 120, 20); Bar(a, 400, 700, 50, 70, 230); g.HasChanged(a);
        var b = Make(1344, 120, 20); Bar(b, 400, 700, 50, 70, 230); Bar(b, 560, 640, 50, 70, 20);
        Assert.True(g.HasChanged(b));
    }

    [Fact] public void Forced_reread_after_many_unchanged_frames()
    {
        var g = new ChangeGate(maxReuse: 3);
        g.HasChanged(Make(400, 80, 50));
        Assert.False(g.HasChanged(Make(400, 80, 50)));
        Assert.False(g.HasChanged(Make(400, 80, 50)));
        Assert.False(g.HasChanged(Make(400, 80, 50)));
        Assert.True(g.HasChanged(Make(400, 80, 50)));
    }
}
