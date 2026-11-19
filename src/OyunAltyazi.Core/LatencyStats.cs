namespace OyunAltyazi.Core;

public sealed record LineTiming(long CapturedMs, long OcrDoneMs, long StableMs, long ShownMs, TranslationSource Source);

public sealed class LatencyStats(int capacity = 50)
{
    private readonly Queue<LineTiming> _items = new();
    private readonly object _lock = new();

    public void Add(LineTiming t)
    {
        lock (_lock)
        {
            _items.Enqueue(t);
            while (_items.Count > capacity) _items.Dequeue();
        }
    }

    public (double AvgMs, double P95Ms, int Count) StableToShown() => Compute(t => t.ShownMs - t.StableMs);
    public (double AvgMs, double P95Ms, int Count) CaptureToShown() => Compute(t => t.ShownMs - t.CapturedMs);

    private (double, double, int) Compute(Func<LineTiming, long> sel)
    {
        double[] v;
        lock (_lock) v = _items.Select(sel).Select(x => (double)x).OrderBy(x => x).ToArray();
        if (v.Length == 0) return (0, 0, 0);
        int rank = (int)Math.Ceiling(0.95 * v.Length);
        return (v.Average(), v[rank - 1], v.Length);
    }
}
