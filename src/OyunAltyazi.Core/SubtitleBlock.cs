// Ported from GameTranslator (sefflex), AGPL-3.0: Reading.SubtitleBlock / IsMenu / Belongs (see NOTICE.md), adapted to OcrLine boxes.
namespace OyunAltyazi.Core;

/// <summary>
/// The region often holds more than the subtitle: a menu ("MAP / HELP / SETTINGS" stacked), a corner counter, a prompt. Lines are grouped
/// into blocks by position; menu-like blocks are discarded and the lowest block centred on the region is the subtitle. When nothing is
/// centred, the lowest block with >= 3 words wins. Lines with unknown geometry are kept as they are.
/// </summary>
public static class SubtitleBlock
{
    /// <summary>Block centre may be this far (fraction of region width) from the region centre to count as "centred".</summary>
    public const double CenterTolerance = 0.12;

    public static IReadOnlyList<OcrLine> Select(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count <= 1) return lines;
        if (lines.Any(l => l.Width <= 0 || l.Height <= 0)) return lines;
        var blocks = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Top))
        {
            var i = blocks.FindLastIndex(b => Belongs(line, b));
            if (i >= 0) blocks[i].Add(line); else blocks.Add([line]);
        }
        if (blocks.Count == 1) return lines;
        var candidates = blocks.Where(b => !IsMenu(b)).ToList();
        var centered = candidates.Where(b => Math.Abs(Center(b) - 0.5) <= CenterTolerance).ToList();
        if (centered.Count > 0) return centered.MaxBy(Bottom)!;
        return candidates.Where(b => Words(b) >= 3).MaxBy(Bottom) ?? (IReadOnlyList<OcrLine>)lines;
    }

    private static double Bottom(List<OcrLine> b) => b.Max(l => l.Top + l.Height);
    private static double Center(List<OcrLine> b) => (b.Min(l => l.Left) + b.Max(l => l.Left + l.Width)) / 2;
    private static int Words(List<OcrLine> b) => b.Sum(l => l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>A line belongs to the block when it sits right under the block's last line and overlaps it horizontally, or shares its row (speaker name beside dialogue) without a wide gap.</summary>
    private static bool Belongs(OcrLine line, List<OcrLine> block)
    {
        var last = block[^1];
        var height = Math.Max(line.Height, last.Height);
        // Horizontal extent is the whole block's (a speaker name sits left of the dialogue, not above it).
        double bl = block.Min(l => l.Left), br = block.Max(l => l.Left + l.Width), bb = block.Max(l => l.Top + l.Height);
        var overlap = Math.Min(line.Left + line.Width, br) - Math.Max(line.Left, bl);
        if (Math.Abs(line.Top - last.Top) <= height * 0.5)
        {
            // Same row: separate texts only when the gap is wide (a menu on the left, a counter on the right).
            var gap = -overlap;
            return !(gap > height * 4 && gap > 0.12);
        }
        if (line.Top - bb >= height * 0.8 || line.Top < last.Top) return false;
        return overlap > 0 && overlap >= Math.Min(line.Width, br - bl) * 0.5;
    }

    /// <summary>Three or more stacked short lines without sentence punctuation are a menu, not speech.</summary>
    private static bool IsMenu(List<OcrLine> b) =>
        b.Count >= 3 && b.All(l => l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 3 && !ButtonPrompts.EndsSentence(l.Text));
}
