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
}
