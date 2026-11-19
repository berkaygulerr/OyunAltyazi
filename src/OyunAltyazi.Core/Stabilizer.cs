namespace OyunAltyazi.Core;

public abstract record StabilizerEvent;
public sealed record ShowLine(SubtitleText Line) : StabilizerEvent;
public sealed record ClearLine : StabilizerEvent;

public sealed class Stabilizer(int confirmReads = 2, int clearAfterEmptyReads = 6)
{
    private string? _shownCanon;
    private bool _shownGarbled;
    private bool _pendingIsUpgrade;
    private void ResetPending() { _pendingCanon = null; _pendingText = null; _pendingCount = 0; _pendingIsUpgrade = false; }
    private static int WordCount(string c) => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    private string? _pendingCanon;
    private SubtitleText? _pendingText;
    private int _pendingCount;
    private int _emptyCount;

    /// <summary>The read is (a noisy re-read of) the line this stabilizer shows or is about to show.</summary>
    public bool Matches(string text)
    {
        var c = TextKey.Canon(text);
        return c.Length > 0 && ((_shownCanon is not null && SameLine(c, _shownCanon)) || (_pendingCanon is not null && SameLine(c, _pendingCanon)));
    }

    private int _shownScore;
    private bool _upgraded;

    public StabilizerEvent? Feed(SubtitleText? read)
    {
        // Gibberish gate: a read that is not mostly English words / names is treated as "nothing readable" (never pending, never shown).
        if (read is not null && !TextQuality.Passes(read.Text)) read = null;
        if (read is null)
        {
            _pendingCanon = null; _pendingText = null; _pendingCount = 0;
            if (_shownCanon is null) return null;
            if (++_emptyCount >= clearAfterEmptyReads)
            {
                _shownCanon = null; _emptyCount = 0;
                return new ClearLine();
            }
            return null;
        }

        _emptyCount = 0;
        var c = TextKey.Canon(read.Text);
        if (c.Length == 0) return null;

        // Any misspelled word counts as "garbled": such a read must repeat longer (a clean re-read usually follows within a second).
        bool garbled = Garbled(read.Text) || OcrNoise.LooksGarbled(read.Text) || TextQuality.Measure(read.Text).Bad > 0;
        if (_shownCanon is not null)
        {
            if (ScrolledOut(c, _shownCanon)) { ResetPending(); return null; }
            // A noisy re-read of the shown sentence that is no better than it (GameTranslator TextMatch.IsWorseReread) is not a new line.
            if (!SameLine(c, _shownCanon) && (TextMatch.IsWorseReread(c, _shownCanon) ||
                (Similarity(c, _shownCanon) >= 0.6 && TextQuality.Score(read.Text) <= _shownScore))) { ResetPending(); return null; }
            if (SameLine(c, _shownCanon))
            {
                // Same line again. Only an upgrade (clean re-read of a line first shown garbled/partial) may replace it.
                // Replace a shown line only by a clearly better read of the SAME line, once (no flip-flopping Turkish):
                // higher word-quality score, or a fuller read (>= 2 more words) that is not worse.
                int q = TextQuality.Score(read.Text);
                bool upgrade = !_upgraded && !garbled &&
                    (q >= _shownScore + TextQuality.UpgradeMargin || (WordCount(c) >= WordCount(_shownCanon) + 2 && q >= _shownScore));
                if (!upgrade) { ResetPending(); return null; }
                _pendingIsUpgrade = true;
            }
        }

        if (_pendingCanon is not null && SameLine(c, _pendingCanon))
            _pendingCount++;
        else
        {
            // typewriter growth or a different candidate: restart the count
            _pendingCanon = c; _pendingCount = 1;
        }
        _pendingText = read;

        // A complete, clean sentence (ends with . ! ? or an ellipsis) is not a typewriter fragment: show it on the first read.
        // Garbled (fade / compression-damaged) reads need more agreement before they are shown.
        if (_pendingCount >= (garbled ? confirmReads + 2 : confirmReads) || (_pendingCount == 1 && IsCompleteSentence(read.Text)))
        {
            _upgraded = _pendingIsUpgrade && _shownCanon is not null && SameLine(c, _shownCanon);
            _shownCanon = c; _shownGarbled = garbled; _shownScore = TextQuality.Score(read.Text); ResetPending();
            return new ShowLine(read);
        }
        return null;
    }

    /// <summary>The new read is just the shown text minus a line that faded/scrolled away (2-line subtitle losing a line).</summary>
    /// <summary>Same on-screen line: near-identical, or a noisy re-read sharing most of its meaningful words (fuzzy, edit distance 1).</summary>
    public static bool SameLine(string a, string b)
    {
        if (Similarity(a, b) >= 0.85) return true;
        if (TextMatch.IsSameSubtitle(a, b)) return true;
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToArray();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4).ToArray();
        if (ta.Length == 0 || tb.Length == 0) return false;
        var (small, big) = ta.Length <= tb.Length ? (ta, tb) : (tb, ta);
        int matched = small.Count(w => big.Any(v => v == w || (Math.Abs(v.Length - w.Length) <= 1 && Levenshtein(v, w) <= 1)));
        return matched >= 2 && matched >= small.Length * 0.6;
    }

    /// <summary>Stray symbols / non-ASCII letters / mid-word capitals: typical of a fade or compression-damaged read.</summary>
    public static bool Garbled(string text)
    {
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            for (int i = 0; i < w.Length; i++)
            {
                char ch = w[i];
                bool ok = (ch < 128 && (char.IsLetterOrDigit(ch) || ".,!?:;'\"()-/%&".Contains(ch))) || ch is '\u2019' or '\u2026' or '\u201C' or '\u201D' or '\u2013' or '\u2014';
                if (!ok) return true;
                if (i > 0 && i < w.Length - 1 && char.IsUpper(ch) && char.IsLower(w[i - 1]) && char.IsLetter(w[i + 1])) return true;
                if (i > 0 && i < w.Length - 1 && char.IsLetter(w[i - 1]) && ch is ',' or ';' or ':') return true;
            }
        }
        return false;
    }

    private static bool ScrolledOut(string c, string shown)
    {
        var a = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 3) return false;
        var b = new HashSet<string>(shown.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return a.Count(b.Contains) >= a.Length * 0.9 && c.Length < shown.Length;
    }

    public static bool IsCompleteSentence(string text)
    {
        var t = text.TrimEnd('"', '\'', '”', '’', ')', ']', ' ');
        if (t.Length < 12 || !(t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith('…'))) return false;
        if (Garbled(t)) return false;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return false;
        // garbled reads contain stray 1-2 letter fragments ("omman er, a aa"): require mostly real words
        int junk = words.Count(w => w.Count(char.IsLetter) is >= 1 and <= 2 && !ShortWords.Contains(new string(w.Where(char.IsLetter).ToArray()).ToLowerInvariant()));
        return junk == 0;
    }

    private static readonly HashSet<string> ShortWords = new() { "a", "i", "an", "am", "as", "at", "be", "by", "do", "go", "he", "hi", "if", "in", "is", "it", "me", "my", "no", "of", "oh", "ok", "on", "or", "so", "to", "up", "us", "we" };

    private static double Similarity(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1;
        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
