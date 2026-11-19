using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class LatencyStatsTests
{
    private static LineTiming L(long ms) => new(0, 0, 0, ms, TranslationSource.Cache);

    [Fact] public void P95_of_known_values()
    {
        var s = new LatencyStats(100);
        for (int i = 1; i <= 100; i++) s.Add(L(i));
        var (avg, p95, n) = s.StableToShown();
        Assert.Equal(50.5, avg); Assert.Equal(95, p95); Assert.Equal(100, n);
    }
    [Fact] public void Keeps_last_50()
    {
        var s = new LatencyStats();
        for (int i = 1; i <= 80; i++) s.Add(L(i));
        var (_, _, n) = s.CaptureToShown();
        Assert.Equal(50, n);
    }
}
