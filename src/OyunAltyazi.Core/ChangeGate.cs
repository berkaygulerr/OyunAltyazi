namespace OyunAltyazi.Core;

public sealed class ChangeGate(double threshold = 3.0)
{
    private const int GW = 64, GH = 16;
    private double[]? _prev;
    private int _w, _h;

    public bool HasChanged(Frame f)
    {
        var sig = Signature(f);
        bool changed = true;
        if (_prev is not null && _w == f.Width && _h == f.Height)
        {
            double sum = 0;
            for (int i = 0; i < sig.Length; i++) sum += Math.Abs(sig[i] - _prev[i]);
            changed = sum / sig.Length > threshold;
        }
        if (changed) { _prev = sig; _w = f.Width; _h = f.Height; }
        return changed;
    }

    private static double[] Signature(Frame f)
    {
        var sums = new double[GW * GH];
        var counts = new int[GW * GH];
        for (int y = 0; y < f.Height; y++)
        {
            int gy = Math.Min(GH - 1, y * GH / f.Height);
            int row = y * f.Stride;
            for (int x = 0; x < f.Width; x++)
            {
                int gx = Math.Min(GW - 1, x * GW / f.Width);
                int p = row + x * 4;
                int idx = gy * GW + gx;
                sums[idx] += (f.Bgra[p] + f.Bgra[p + 1] + f.Bgra[p + 2]) / 3.0;
                counts[idx]++;
            }
        }
        for (int i = 0; i < sums.Length; i++) if (counts[i] > 0) sums[i] /= counts[i];
        return sums;
    }
}
