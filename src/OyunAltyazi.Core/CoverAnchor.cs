namespace OyunAltyazi.Core;

/// <summary>
/// Keeps the Turkish cover bubble from hopping between lines. GameTranslator places each line's backdrop from that line's own
/// OCR box (and merges it with the previous one, StableCover); OCR boxes wobble per line (descenders, one vs two rows, a missed
/// word), so the bubble jumped. Here the bubble is anchored on the English block's STABLE edge:
///  - game subtitles are bottom-anchored (grow upward) or top-anchored (grow downward); the edge whose recent positions vary least is
///    the anchor (bottom by default; switching needs the other edge to be clearly steadier). Anchor = running median of that edge.
///  - horizontally: the region centre (subtitles are centred; when the English is not, the running median of its centres).
///  - only the size follows the line: the box still covers the whole English block (grows on the free side / symmetrically).
/// The anchor moves only when the whole English block is clearly elsewhere (> <see cref="SnapFraction"/> of the region height) for
/// <see cref="SnapLines"/> consecutive lines that agree with each other. All values are fractions of the captured frame.
/// </summary>
public sealed class CoverAnchor
{
    public const double SnapFraction = 0.15;
    public const int SnapLines = 2;
    private const int History = 7;
    /// <summary>English sticking out past the anchor by more than this (fraction of region height) pulls this line's box out so it stays covered.</summary>
    private const double CoverSlack = 0.04;
    private readonly List<double> _bottoms = new(), _tops = new(), _centers = new(), _modeTops = new(), _modeBottoms = new();
    private readonly List<(double Top, double Bottom, double Center)> _far = new();
    private readonly object _lock = new();
    private bool _top;

    /// <summary>True when the bubble is pinned by its top edge (the game's subtitle grows downward); false: pinned by its bottom edge.</summary>
    public bool AnchorTop { get { lock (_lock) return _top; } }
    public double? AnchorEdge { get { lock (_lock) return _bottoms.Count == 0 ? null : Median(_top ? _tops : _bottoms); } }
    public double? AnchorBottom { get { lock (_lock) return _bottoms.Count == 0 ? null : Median(_bottoms); } }

    public void Reset() { lock (_lock) { _bottoms.Clear(); _tops.Clear(); _centers.Clear(); _far.Clear(); _modeTops.Clear(); _modeBottoms.Clear(); _top = false; } }

    /// <param name="english">Union box of the English lines (frame fractions).</param>
    /// <param name="region">The OCR region (frame fractions); its height scales the snap rule, its centre is the horizontal anchor.</param>
    /// <returns>The box the cover must occupy: centred on the anchor, the stable edge on the anchor, covering <paramref name="english"/>.</returns>
    public RegionF Stabilize(RegionF english, RegionF region)
    {
        lock (_lock)
        {
            double top = english.Y, bottom = english.Y + english.H, centre = english.X + english.W / 2;
            double snap = SnapFraction * region.H;
            double Edge((double Top, double Bottom, double Center) s) => _top ? s.Top : s.Bottom;
            if (_bottoms.Count == 0) Add(top, bottom, centre);
            // Far = BOTH edges away from their running medians (the whole block moved). One edge moving is just a 1- vs 2-row line.
            else if (Math.Abs(top - Median(_tops)) <= snap || Math.Abs(bottom - Median(_bottoms)) <= snap) { _far.Clear(); Add(top, bottom, centre); }
            else
            {
                _far.Add((top, bottom, centre));
                if (_far.Count > SnapLines) _far.RemoveAt(0);
                // Clearly elsewhere for SnapLines lines that agree with each other: the subtitle moved - re-anchor there.
                if (_far.Count >= SnapLines && _far.Max(Edge) - _far.Min(Edge) <= snap)
                {
                    _bottoms.Clear(); _tops.Clear(); _centers.Clear(); _modeTops.Clear(); _modeBottoms.Clear();
                    foreach (var f in _far) Add(f.Top, f.Bottom, f.Center);
                    _far.Clear();
                }
            }

            // Pick the steadier edge over a longer memory (20 lines); leave the current one only when the other is clearly steadier.
            _modeTops.Add(top); _modeBottoms.Add(bottom);
            if (_modeTops.Count > 20) { _modeTops.RemoveAt(0); _modeBottoms.RemoveAt(0); }
            if (_modeTops.Count >= 6)
            {
                double rt = Spread(_modeTops), rb = Spread(_modeBottoms), meaningful = 0.1 * region.H;
                if (!_top && rb > meaningful && rt < rb * 0.4) _top = true;
                else if (_top && rt > meaningful && rb < rt * 0.4) _top = false;
            }

            double regionCentre = region.X + region.W / 2;
            double medianCentre = Median(_centers);
            double anchorCentre = Math.Abs(medianCentre - regionCentre) <= SubtitleBlock.CenterTolerance * region.W ? regionCentre : medianCentre;
            double half = Math.Max(Math.Max(english.W / 2, Math.Abs(english.X - anchorCentre)), Math.Abs(english.X + english.W - anchorCentre));
            double slack = CoverSlack * region.H;
            double outTop, outBottom;
            if (_top)
            {
                double a = Median(_tops);
                outTop = top < a - slack ? top : a;                    // English above the anchor: extend up to cover it
                outBottom = Math.Max(bottom, outTop);                   // the free side follows the line (size only)
            }
            else
            {
                double a = Median(_bottoms);
                outBottom = bottom > a + slack ? bottom : a;
                outTop = Math.Min(top, outBottom);
            }
            return new RegionF(anchorCentre - half, outTop, 2 * half, Math.Max(0, outBottom - outTop));
        }
    }

    private void Add(double top, double bottom, double centre)
    {
        _tops.Add(top); _bottoms.Add(bottom); _centers.Add(centre);
        if (_bottoms.Count > History) { _tops.RemoveAt(0); _bottoms.RemoveAt(0); _centers.RemoveAt(0); }
    }

    /// <summary>Inter-quartile spread: robust against the odd garbled box.</summary>
    private static double Spread(List<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        return s[(int)Math.Round((s.Length - 1) * 0.75)] - s[(int)Math.Round((s.Length - 1) * 0.25)];
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
