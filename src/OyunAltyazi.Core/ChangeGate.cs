namespace OyunAltyazi.Core;

/// <summary>
/// Cheap "did the subtitle area change?" test. Per-cell grayscale signature on a fine grid; the frame counts as
/// changed as soon as a few cells move by more than a per-cell threshold (max-style, not a frame-wide mean, so a
/// line swap of the same length still triggers). A forced re-read happens after <paramref name="maxReuse"/> unchanged frames.
/// </summary>
public sealed class ChangeGate(int cellThreshold = 14, int minCells = 3, int maxReuse = 12)
{
    private const int GW = 96, GH = 24;
    private byte[]? _prev;
    private int _w, _h, _reused;

    public bool HasChanged(Frame f)
    {
        var sig = Signature(f);
        bool changed = true;
        if (_prev is not null && _w == f.Width && _h == f.Height)
        {
            int cells = 0;
            for (int i = 0; i < sig.Length; i++)
                if (Math.Abs(sig[i] - _prev[i]) > cellThreshold) cells++;
            changed = cells >= minCells || _reused >= maxReuse;
        }
        if (changed) { _prev = sig; _w = f.Width; _h = f.Height; _reused = 0; } else _reused++;
        return changed;
    }

    private static byte[] Signature(Frame f)
    {
        var sums = new long[GW * GH];
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
                sums[idx] += f.Bgra[p] + f.Bgra[p + 1] * 2 + f.Bgra[p + 2];
                counts[idx]++;
            }
        }
        var r = new byte[sums.Length];
        for (int i = 0; i < r.Length; i++) r[i] = counts[i] > 0 ? (byte)(sums[i] / counts[i] / 4) : (byte)0;
        return r;
    }
}
