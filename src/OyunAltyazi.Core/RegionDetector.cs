namespace OyunAltyazi.Core;

/// <summary>
/// Finds the subtitle area by watching OCR output over time: dialogue lines change, HUD text does not.
/// Pure logic; feed it every OCR result together with the region (window-relative) it was read from.
/// </summary>
public sealed class RegionDetector(int needed = 3, long staticMs = 5000, long forgetGapMs = 1500)
{
    public static readonly RegionF DefaultBand = new(0, 0.55, 1, 0.45);

    private sealed class Track(string canon, RegionF box, long now)
    {
        public string Canon = canon; public RegionF Box = box;
        public long First = now, Last = now; public int Seen = 1; public bool Present = true;
    }

    private readonly List<Track> _tracks = new();
    private readonly List<(string Canon, RegionF Box)> _confirmed = new();
    private readonly List<string> _static = new();

    public int Needed => needed;
    /// <summary>Number of distinct changing subtitles that cluster together so far.</summary>
    public int Progress { get; private set; }

    public RegionF? Feed(OcrResult ocr, RegionF band, long nowMs)
    {
        var cands = new List<(string Canon, RegionF Box)>();
        var accepted = ocr.Lines.Select(l => (Line: l, Text: l.Text.Trim())).Where(x => IsDialogueLike(x.Text))
            .Select(x => (x.Text, Box: ToWindow(x.Line, band))).OrderBy(x => x.Box.Y).ToList();
        // merge vertically adjacent lines of one read into a single (possibly multi-line) subtitle
        var group = new List<(string Text, RegionF Box)>();
        void Flush()
        {
            if (group.Count == 0) return;
            var canon = TextKey.Canon(string.Join(" ", group.Select(g => g.Text)));
            if (canon.Length > 0) cands.Add((canon, Union(group.Select(g => g.Box))));
            group.Clear();
        }
        foreach (var a in accepted)
        {
            if (group.Count > 0)
            {
                var prev = group[^1].Box;
                if (a.Box.Y - (prev.Y + prev.H) > 1.2 * Math.Max(prev.H, a.Box.H)) Flush();
            }
            group.Add(a);
        }
        Flush();

        foreach (var t in _tracks) t.Present = false;
        foreach (var (canon, box) in cands)
        {
            if (_static.Any(s => Sim(s, canon) >= 0.85)) continue;
            var tr = _tracks.FirstOrDefault(t => Sim(t.Canon, canon) >= 0.85);
            if (tr is null) { _tracks.Add(new Track(canon, box, nowMs)); continue; }
            if (nowMs - tr.Last > forgetGapMs) { tr.First = nowMs; tr.Seen = 0; }
            tr.Last = nowMs; tr.Seen++; tr.Present = true; tr.Box = Union([tr.Box, box]); tr.Canon = canon;
            if (nowMs - tr.First > staticMs) { _static.Add(tr.Canon); _tracks.Remove(tr); RemoveConfirmed(tr.Canon); }
        }

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            if (t.Present) continue;
            _tracks.RemoveAt(i);
            // read at least twice, then gone or replaced -> a real, changing subtitle
            if (t.Seen >= 2 && t.Last - t.First <= staticMs && !_confirmed.Any(c => Sim(c.Canon, t.Canon) >= 0.85))
                _confirmed.Add((t.Canon, t.Box));
        }

        var cluster = BestCluster();
        Progress = Math.Min(cluster.Count, needed);
        return cluster.Count < needed ? null : Pad(cluster);
    }

    private void RemoveConfirmed(string canon) => _confirmed.RemoveAll(c => Sim(c.Canon, canon) >= 0.85);

    private static bool IsDialogueLike(string t)
    {
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) return false;
        int letters = t.Count(char.IsLetter), nonSpace = t.Count(c => !char.IsWhiteSpace(c));
        return letters >= 6 && (double)letters / nonSpace >= 0.75;
    }

    private static RegionF ToWindow(OcrLine l, RegionF b) =>
        new(b.X + l.Left * b.W, b.Y + l.Top * b.H, l.Width * b.W, l.Height * b.H);

    private static RegionF Union(IEnumerable<RegionF> boxes)
    {
        var a = boxes.ToList();
        double x0 = a.Min(r => r.X), y0 = a.Min(r => r.Y), x1 = a.Max(r => r.X + r.W), y1 = a.Max(r => r.Y + r.H);
        return new RegionF(x0, y0, x1 - x0, y1 - y0);
    }

    private List<RegionF> BestCluster()
    {
        var boxes = _confirmed.Select(c => c.Box).ToList();
        if (boxes.Count == 0) return [];
        var hs = boxes.Select(b => b.H).OrderBy(h => h).ToList();
        double tolY = Math.Max(0.05, 2 * hs[hs.Count / 2]);
        List<RegionF> best = [];
        foreach (var seed in boxes)
        {
            double sc = seed.X + seed.W / 2, sy = seed.Y + seed.H / 2;
            var members = boxes.Where(b => Math.Abs(b.Y + b.H / 2 - sy) <= tolY
                && (Math.Abs(b.X + b.W / 2 - sc) <= 0.1 || Math.Abs(b.X - seed.X) <= 0.05)).ToList();
            if (members.Count > best.Count) best = members;
        }
        return best;
    }

    private static RegionF Pad(List<RegionF> cluster)
    {
        var u = Union(cluster);
        double med = cluster.Select(b => b.H).OrderBy(h => h).ElementAt(cluster.Count / 2);
        double py = Math.Max(0.25 * u.H, 0.5 * med), px = Math.Max(0.02, 0.1 * u.W);
        double x0 = Math.Max(0, u.X - px), y0 = Math.Max(0, u.Y - py);
        double x1 = Math.Min(1, u.X + u.W + px), y1 = Math.Min(1, u.Y + u.H + py);
        return new RegionF(x0, y0, x1 - x0, y1 - y0);
    }

    private static double Sim(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / max;
    }
}
