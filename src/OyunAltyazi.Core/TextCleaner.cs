using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

public static class TextCleaner
{
    private static readonly Regex ButtonHint = new(@"^\s*[\[(][^\]\)]{1,6}[\])](\s+\w+)?\s*$", RegexOptions.Compiled);
    private static readonly Regex Speaker = new(@"^([A-Z][A-Za-z0-9 .'\-]{0,24}):\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex Confusable = new(@"[A-Za-z01|]+", RegexOptions.Compiled);

    public static SubtitleText? Clean(OcrResult ocr)
    {
        var kept = new List<string>();
        foreach (var line in ocr.Lines.OrderBy(l => l.Top))
        {
            var t = line.Text.Trim();
            if (t.Length == 0) continue;
            int letters = t.Count(char.IsLetter);
            int nonSpace = t.Count(c => !char.IsWhiteSpace(c));
            if (letters < 3) continue;
            if (ButtonHint.IsMatch(t)) continue;
            if ((double)letters / nonSpace < 0.5) continue;
            kept.Add(t);
        }
        if (kept.Count == 0) return null;

        var joined = FixConfusions(string.Join(" ", kept));
        int totalLetters = joined.Count(char.IsLetter);
        int words = joined.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 2 && totalLetters < 4) return null;

        var m = Speaker.Match(joined);
        return m.Success ? new SubtitleText(m.Groups[1].Value, m.Groups[2].Value) : new SubtitleText(null, joined);
    }

    private static string FixConfusions(string s) => Confusable.Replace(s, m =>
    {
        var w = m.Value;
        if (!w.Any(char.IsLetter) || !w.Any(c => c is '0' or '1' or '|')) return w;
        var chars = w.Select(c => c switch { '0' => 'o', '1' or '|' => 'l', _ => c });
        return new string(chars.ToArray());
    });
}
