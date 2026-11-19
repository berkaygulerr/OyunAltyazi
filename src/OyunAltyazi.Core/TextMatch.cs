// Ported from GameTranslator (sefflex), AGPL-3.0: TextMatch.cs (see NOTICE.md).
using System.Globalization;
using System.Text;

namespace OyunAltyazi.Core;

/// <summary>Noise-tolerant comparison of OCR reads: same subtitle re-read, typewriter growth, worse re-read of a shown line.</summary>
public static class TextMatch
{
    /// <summary>Lowercase, letters/digits only, single spaces.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant()) sb.Append(IsAlphanumeric(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Levenshtein ratio >= threshold. Empty text is similar to nothing.</summary>
    public static bool IsSimilar(string a, string b, double threshold = 0.85)
    {
        if (a == b) return a.Length > 0;
        if (a.Length == 0 || b.Length == 0) return false;
        return 1 - (double)Distance(a, b) / Math.Max(a.Length, b.Length) >= threshold;
    }

    /// <summary>
    /// Is <paramref name="reread"/> a (noisy) re-read of the already shown <paramref name="sent"/>? OCR turns one or two words
    /// into different junk on every frame or misses a row of a two-row subtitle, so character similarity drops while the words
    /// stay. Text GROWN at the start/end (typewriter effect, a new line starting with the same words) is new; text cut short is the
    /// same only when exactly whole rows are missing.
    /// </summary>
    public static bool IsSameSubtitle(string reread, string sent)
    {
        var rows = sent.Split('\n');
        var flat = string.Join(' ', rows);
        var x = CleanWords(reread);
        var y = CleanWords(flat);
        if (y.Count > 0 && x.Count > y.Count && (StartsWith(x, y) || EndsWith(x, y))) return false;
        if (IsSimilar(reread, flat)) return true;
        if (x.Count > 0 && x.Count < y.Count && (StartsWith(y, x) || EndsWith(y, x)))
            return IsWholeRows(x.Count, rows, StartsWith(y, x));
        var remaining = new List<string>(y);
        var sentCount = remaining.Count;
        var shared = 0;
        foreach (var word in x)
            if (remaining.Remove(word)) shared++;
        if (x.Count >= 4 && x.Count <= sentCount && shared >= x.Count * 0.85) return true;
        int longer = Math.Max(x.Count, sentCount), shorter = Math.Min(x.Count, sentCount);
        if (shorter < 6 || (double)shorter / longer < 0.75) return false;
        return (double)shared / longer >= 0.75;
    }

    /// <summary>
    /// A re-read of the shown sentence that is no better than it: same words in order (at most ~3 differing) or very similar
    /// characters, and not a clearly higher <see cref="OcrNoise.Score"/>. Growth / cut-short text is left to <see cref="IsSameSubtitle"/>.
    /// </summary>
    public static bool IsWorseReread(string reread, string sent)
    {
        var a = Normalize(reread);
        var b = Normalize(sent);
        var x = CleanWords(a);
        var y = CleanWords(b);
        if (x.Count < 5 || y.Count == 0 || IsGrowth(reread, sent) || IsGrowth(sent, reread)) return false;
        var reads = WordSimilarity(x, y) >= 0.65 || IsSimilar(a, b, 0.75);
        return reads && OcrNoise.Score(reread) < OcrNoise.Score(sent) + 4;
    }

    /// <summary><paramref name="newText"/> is <paramref name="oldText"/> with words appended (typewriter) or, when the start was missed, prepended.</summary>
    public static bool IsGrowth(string newText, string oldText)
    {
        var x = Normalize(newText).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var y = Normalize(oldText).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (y.Count == 0 || x.Count <= y.Count) return false;
        return StartsWith(x, y) || (y.Count >= 2 && EndsWith(x, y));
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
                row[j] = Math.Min(Math.Min(previous[j] + 1, row[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, row) = (row, previous);
        }
        return previous[b.Length];
    }

    private static double WordSimilarity(List<string> x, List<string> y)
    {
        if (x.Count == 0 || y.Count == 0) return x.Count == 0 && y.Count == 0 ? 1 : 0;
        var previous = new int[y.Count + 1];
        var row = new int[y.Count + 1];
        for (var j = 0; j <= y.Count; j++) previous[j] = j;
        for (var i = 1; i <= x.Count; i++)
        {
            row[0] = i;
            for (var j = 1; j <= y.Count; j++)
                row[j] = Math.Min(Math.Min(previous[j] + 1, row[j - 1] + 1), previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            (previous, row) = (row, previous);
        }
        return 1 - (double)previous[y.Count] / Math.Max(x.Count, y.Count);
    }

    private static bool StartsWith(List<string> words, List<string> part) => words.Take(part.Count).SequenceEqual(part);
    private static bool EndsWith(List<string> words, List<string> part) => words.Skip(words.Count - part.Count).SequenceEqual(part);

    private static bool IsWholeRows(int count, string[] rows, bool fromStart)
    {
        var total = 0;
        foreach (var row in fromStart ? rows : Enumerable.Reverse(rows))
        {
            total += CleanWords(row).Count;
            if (total == count) return true;
            if (total > count) return false;
        }
        return false;
    }

    // Single letters and words with digits add nothing to a comparison (OCR invents them).
    private static List<string> CleanWords(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !w.Any(char.IsNumber)).ToList();

    private static bool IsAlphanumeric(char c) => char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };
}
