using OyunAltyazi.Core;

namespace OyunAltyazi.Core.Tests;

public class RegionDetectorTests
{
    private static readonly RegionF Band = new(0, 0.55, 1, 0.45);
    private static OcrLine L(string t, double x, double y, double w, double h) =>
        new(t, (y - Band.Y) / Band.H, h / Band.H, (x - Band.X) / Band.W, w / Band.W); // window-rel -> band-rel

    private static readonly string[] Subs =
        ["Get to the chopper now", "We are running out of time", "Cover me while I reload", "They are coming from the east"];

    [Fact] public void Static_hud_text_is_rejected_and_changing_lines_give_region()
    {
        var d = new RegionDetector();
        RegionF? r = null;
        long t = 0;
        foreach (var s in Subs.Take(3))
            for (int k = 0; k < 4; k++, t += 250)
            {
                var hud = L("Objective find the key", 0.03, 0.62, 0.25, 0.03);
                var sub = L(s, 0.25, 0.85, 0.5, 0.04);
                r = d.Feed(new OcrResult([hud, sub]), Band, t) ?? r;
            }
        r = d.Feed(new OcrResult([]), Band, t) ?? r;
        Assert.NotNull(r);
        Assert.True(r!.Y <= 0.85 && r.Y + r.H >= 0.89 && r.X <= 0.25 && r.X + r.W >= 0.75);
        Assert.True(r.Y > 0.7, "HUD must be excluded");
    }

    [Fact] public void Text_unchanged_for_over_five_seconds_never_counts()
    {
        var d = new RegionDetector();
        RegionF? r = null;
        for (long t = 0; t < 12000; t += 250)
            r = d.Feed(new OcrResult([L("Press F to pay respects", 0.3, 0.85, 0.4, 0.04)]), Band, t) ?? r;
        d.Feed(new OcrResult([]), Band, 12500);
        Assert.Null(r);
        Assert.Equal(0, d.Progress);
    }

    [Fact] public void Outlier_far_from_the_cluster_is_ignored()
    {
        var d = new RegionDetector();
        RegionF? r = null;
        long t = 0;
        var ys = new[] { 0.85, 0.60, 0.85, 0.86, 0.85 };
        for (int i = 0; i < 5; i++)
            for (int k = 0; k < 3; k++, t += 250)
                r = d.Feed(new OcrResult([L(Subs[i % 4] + (i == 4 ? " again" : ""), 0.25, ys[i], 0.5, 0.04)]), Band, t) ?? r;
        r = d.Feed(new OcrResult([]), Band, t) ?? r;
        Assert.NotNull(r);
        Assert.True(r!.Y > 0.75, "outlier at y=0.60 must not stretch the region");
    }

    [Fact] public void Not_found_before_three_distinct_subtitles()
    {
        var d = new RegionDetector();
        RegionF? r = null; long t = 0;
        foreach (var s in Subs.Take(2))
            for (int k = 0; k < 4; k++, t += 250) r = d.Feed(new OcrResult([L(s, 0.25, 0.85, 0.5, 0.04)]), Band, t) ?? r;
        r = d.Feed(new OcrResult([]), Band, t) ?? r;
        Assert.Null(r);
        Assert.Equal(2, d.Progress);
    }
}
