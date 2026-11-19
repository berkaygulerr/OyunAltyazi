using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

public static class TextCleaner
{
    private static readonly Regex ButtonHint = new(@"^\s*[\[(][^\]\)]{1,6}[\])](\s+\w+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex Speaker = new(@"^([A-Z][A-Za-z0-9 .'\-]{0,24}?)\s?[:;]\s*(?=\S)(.+)$", RegexOptions.Compiled);
    // A name whose first letters were lost by OCR ('ria:' from 'Valeria:'): lowercase start, short, then a colon.
    private static readonly Regex GarbledSpeaker = new(@"^[a-z][A-Za-z'\-]{0,15}\s?[:;]\s*(?=\S)(.+)$", RegexOptions.Compiled);
    // A lone "|" or "l" before a lowercase word is the pronoun I.
    private static readonly Regex LoneI = new(@"(?<=^|\s)[|l](?=\s+[a-z])", RegexOptions.Compiled);
    private static readonly Regex Confusable = new(@"[A-Za-z01|]+", RegexOptions.Compiled);

    public static SubtitleText? Clean(OcrResult ocr) => Clean(ocr, out _);

    /// <summary>Like Clean, also returning the union box (region-relative 0..1) of the OCR lines that made it into the text.</summary>
    public static SubtitleText? Clean(OcrResult ocr, out RegionF? box) => Clean(ocr, out box, out _);

    /// <param name="lineCount">Number of OCR lines that formed the text (1 when nothing was kept).</param>
    public static SubtitleText? Clean(OcrResult ocr, out RegionF? box, out int lineCount, IReadOnlyCollection<string>? knownSpeakers = null)
    {
        box = null; lineCount = 1;
        var keptLines = new List<OcrLine>();
        var kept = new List<string>();
        foreach (var line in ReadingOrder(ocr.Lines))
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            int letters = t.Count(char.IsLetter);
            int nonSpace = t.Count(c => !char.IsWhiteSpace(c));
            if (letters < 3) continue;
            if (ButtonHint.IsMatch(t)) continue;
            if ((double)letters / nonSpace < 0.5) continue;
            kept.Add(t);
            keptLines.Add(line);
        }
        if (kept.Count == 0) return null;

        var joined = RepairWords(LoneI.Replace(FixConfusions(StripMarks(string.Join(" ", kept))), "I"));
        int totalLetters = joined.Count(char.IsLetter);
        int words = joined.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 2 && totalLetters < 4) return null;

        lineCount = keptLines.Count;
        double l = keptLines.Min(k => k.Left), t0 = keptLines.Min(k => k.Top);
        double r = keptLines.Max(k => k.Left + k.Width), b = keptLines.Max(k => k.Top + k.Height);
        box = new RegionF(l, t0, r - l, b - t0);
        return SplitSpeaker(joined, knownSpeakers);
    }

    /// <summary>Separates 'Name: text'. A garbled/partial name is repaired from knownSpeakers or dropped - never shown partially.</summary>
    public static SubtitleText SplitSpeaker(string joined, IReadOnlyCollection<string>? known = null)
    {
        var m = Speaker.Match(joined);
        if (m.Success)
        {
            var name = m.Groups[1].Value.Trim();
            var fixedName = Repair(name, known);
            if (fixedName is null) return new SubtitleText(null, joined); // unsure about the name: keep the raw text, never lose words
            return new SubtitleText(fixedName, m.Groups[2].Value);
        }
        var g = GarbledSpeaker.Match(joined);
        if (g.Success)
        {
            var prefix = joined[..(joined.Length - g.Groups[1].Value.Length)].TrimEnd(' ', ':', ';');
            var rep = Repair(prefix, known);
            return new SubtitleText(rep, g.Groups[1].Value);
        }
        return new SubtitleText(null, joined);
    }

    // Returns the name to show: as-is when whole/known, the known full name when the OCR name is a fragment of it, null when unusable.
    private static string? Repair(string name, IReadOnlyCollection<string>? known)
    {
        if (known is not null)
        {
            foreach (var k in known)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return k;
            if (name.Length >= 2)
                foreach (var k in known)
                    if (k.EndsWith(name, StringComparison.OrdinalIgnoreCase) || k.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                        || (name.Length >= 4 && Lev(k.ToLowerInvariant(), name.ToLowerInvariant()) <= 1)) return k;
        }
        // Without a known name we cannot tell a fragment from a whole short name; only lowercase starts are certainly cut.
        return name.Length > 0 && char.IsUpper(name[0]) && name.Any(char.IsLetter) && name.Count(char.IsLetter) >= 3 ? name : null;
    }

    private static int Lev(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Top-to-bottom, but OCR fragments on the same visual row (name vs. dialogue) are ordered left-to-right.</summary>
    private static IEnumerable<OcrLine> ReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Top).ToList();
        var row = new List<OcrLine>();
        foreach (var l in sorted)
        {
            if (row.Count > 0 && Math.Abs(l.Top - row[0].Top) > 0.5 * Math.Max(row[0].Height, l.Height))
            {
                foreach (var r in row.OrderBy(x => x.Left)) yield return r;
                row.Clear();
            }
            row.Add(l);
        }
        foreach (var r in row.OrderBy(x => x.Left)) yield return r;
    }

    private static readonly Regex Glued = new(@"\b(of|to|in|on|at|for|and|is|not)(you|your|the|our|my|we|they|them|him|her)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DoubleI = new(@"\bII(?=\s+[a-z])", RegexOptions.Compiled);

    // English OCR sometimes adds accents (Årcher) or glues function words / doubles the pronoun I; fix those without dropping anything.
    private static string RepairWords(string s) => Glued.Replace(DoubleI.Replace(s, "I"), "$1 $2");

    private static string StripMarks(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var c in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static string FixConfusions(string s) => Confusable.Replace(s, m =>
    {
        var w = m.Value;
        if (!w.Any(char.IsLetter) || !w.Any(c => c is '0' or '1' or '|')) return w;
        var chars = w.Select(c => c switch { '0' => 'o', '1' or '|' => 'l', _ => c });
        return new string(chars.ToArray());
    });
}
