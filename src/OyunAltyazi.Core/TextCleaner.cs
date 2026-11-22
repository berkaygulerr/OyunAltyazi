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

    private static readonly Regex LeadingJunk = new(@"^[^\p{L}\d]+(?=\p{Lu}[\p{L}'’. -]{1,24}\s?[:;])", RegexOptions.Compiled);
    private sealed record Row(string Text, double Left, double Top, double Right, double Bottom);

    /// <summary>
    /// Filters the OCR lines (subtitle block, HUD prompts, OCR noise, symbol junk) and merges fragments sharing a screen row
    /// (name beside dialogue). Rows are in reading order. Everything here happens before translation.
    /// </summary>
    private static List<Row> Rows(OcrResult ocr)
    {
        var rows = new List<Row>();
        foreach (var group in ReadingOrder(SubtitleBlock.Select(ocr.Lines)))
        {
            var parts = new List<string>();
            var used = new List<OcrLine>();
            foreach (var line in group)
            {
                var t = line.Text.Trim();
                if (t.Length == 0) continue;
                if (ButtonPrompts.IsPrompt(t)) continue;
                t = OcrNoise.Clean(FixConfusions(t)).Trim(); // digit/letter lookalikes first ("Ho1d" -> "Hold"), then junk removal
                int letters = t.Count(char.IsLetter);
                int nonSpace = t.Count(c => !char.IsWhiteSpace(c));
                if (letters < 3) continue;
                if (ButtonHint.IsMatch(t)) continue;
                if ((double)letters / nonSpace < 0.5) continue;
                parts.Add(t); used.Add(line);
            }
            if (parts.Count == 0) continue;
            // Junk in front of a speaker label ("-Hadir:", "'Alex:", "• Farah:") is not part of the name.
            var joinedText = LeadingJunk.Replace(string.Join(" ", parts), "");
            var text = RepairWords(LoneI.Replace(FixConfusions(StripMarks(joinedText)), "I"));
            rows.Add(new Row(text, used.Min(k => k.Left), used.Min(k => k.Top), used.Max(k => k.Left + k.Width), used.Max(k => k.Top + k.Height)));
        }
        return rows;
    }

    private static RegionF BoxOf(List<Row> rows)
    {
        double l = rows.Min(r => r.Left), t = rows.Min(r => r.Top), r2 = rows.Max(r => r.Right), b = rows.Max(r => r.Bottom);
        return new RegionF(l, t, r2 - l, b - t);
    }

    private static bool TooShort(string joined) =>
        joined.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2 && joined.Count(char.IsLetter) < 4;

    /// <param name="lineCount">Number of OCR lines that formed the text (1 when nothing was kept).</param>
    public static SubtitleText? Clean(OcrResult ocr, out RegionF? box, out int lineCount, IReadOnlyCollection<string>? knownSpeakers = null)
    {
        box = null; lineCount = 1;
        var rows = Rows(ocr);
        if (rows.Count == 0) return null;
        var joined = string.Join(" ", rows.Select(r => r.Text));
        if (TooShort(joined)) return null;
        lineCount = rows.Count;
        box = BoxOf(rows);
        return Corrected(SplitSpeaker(joined, knownSpeakers));
    }

    // "Price:", "ATREUS:", "Mimir Head:" at the start of a row: <= 3 words, each capitalised or ALL CAPS (GameTranslator SubtitleSegmenter.label).
    private static readonly Regex SpeakerLabel = new(@"^(?<n>\p{Lu}[\p{Lu}\d'’.\-]+(?: \p{Lu}[\p{Lu}\d'’.\-]*){0,2}|\p{Lu}\p{Ll}[\p{L}'’.\-]*(?: \p{Lu}[\p{L}'’.\-]*){0,2})\s?[:;]\s*(?=\S)", RegexOptions.Compiled);
    // Capitalised headings that end in a colon but are not speakers (they are translated as text).
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "warning", "tip", "hint", "objective", "objectives", "quest", "goal", "new", "reward", "rewards", "location", "chapter",
        "mission", "task", "tutorial", "update", "info", "status", "level", "controls", "press", "hold", "tap", "remember", "example",
        "question", "answer", "result", "results", "time", "listen", "look", "wait", "okay", "ok", "well", "so", "yes", "no", "now", "then", "and", "but",
    };

    /// <summary>The row starts a new utterance: it begins with a speaker label (or a name already known as a speaker).</summary>
    public static bool StartsWithSpeakerLabel(string row, IReadOnlyCollection<string>? known = null)
    {
        var m = SpeakerLabel.Match(row.TrimStart());
        if (m.Success)
        {
            var name = m.Groups["n"].Value;
            if (name.Contains(' ') || !NotNames.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Splits the OCR result into utterances in screen order (GameTranslator SubtitleSegmenter): every row that starts with a speaker
    /// label begins a new utterance; rows without a label continue the previous one. <paramref name="box"/> covers the whole English block.
    /// </summary>
    public static IReadOnlyList<SubtitleText> CleanScene(OcrResult ocr, out RegionF? box, out int lineCount, IReadOnlyCollection<string>? knownSpeakers = null)
    {
        box = null; lineCount = 1;
        var rows = Rows(ocr);
        if (rows.Count == 0) return [];
        if (TooShort(string.Join(" ", rows.Select(r => r.Text)))) return [];
        lineCount = rows.Count;
        box = BoxOf(rows);
        var texts = new List<string>();
        foreach (var r in rows)
        {
            if (texts.Count == 0 || StartsWithSpeakerLabel(r.Text, knownSpeakers)) texts.Add(r.Text);
            else texts[^1] += " " + r.Text;
        }
        return texts.Select(t => Corrected(SplitSpeaker(t, knownSpeakers))).Where(u => u.Text.Trim().Length > 0).ToList();
    }

    // OCR word repair touches the dialogue only; the speaker label was already settled by SplitSpeaker.
    private static SubtitleText Corrected(SubtitleText u) => u with { Text = OcrWordCorrector.Correct(u.Text) };

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
                        || (name.Length >= 4 && Lev(k.ToLowerInvariant(), name.ToLowerInvariant()) <= (k.Length >= 9 ? 2 : 1))) return k;
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

    /// <summary>Top-to-bottom rows; OCR fragments on the same visual row (name vs. dialogue) are grouped and ordered left-to-right.</summary>
    private static IEnumerable<List<OcrLine>> ReadingOrder(IReadOnlyList<OcrLine> lines)
    {
        var sorted = lines.OrderBy(l => l.Top).ToList();
        var row = new List<OcrLine>();
        foreach (var l in sorted)
        {
            if (row.Count > 0 && Math.Abs(l.Top - row[0].Top) > 0.5 * Math.Max(row[0].Height, l.Height))
            {
                yield return row.OrderBy(x => x.Left).ToList();
                row = new List<OcrLine>();
            }
            row.Add(l);
        }
        if (row.Count > 0) yield return row.OrderBy(x => x.Left).ToList();
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
