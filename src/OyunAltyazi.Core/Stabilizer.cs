namespace OyunAltyazi.Core;

public abstract record StabilizerEvent;
public sealed record ShowLine(SubtitleText Line) : StabilizerEvent;
public sealed record ClearLine : StabilizerEvent;

public sealed class Stabilizer(int confirmReads = 2, int clearAfterEmptyReads = 4)
{
    private string? _shownCanon;
    private string? _pendingCanon;
    private SubtitleText? _pendingText;
    private int _pendingCount;
    private int _emptyCount;

    public StabilizerEvent? Feed(SubtitleText? read)
    {
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

        if (_shownCanon is not null && Similarity(c, _shownCanon) >= 0.85)
        {
            _pendingCanon = null; _pendingText = null; _pendingCount = 0;
            return null;
        }

        if (_pendingCanon is not null && c == _pendingCanon)
            _pendingCount++;
        else
        {
            // typewriter growth or a different candidate: restart the count
            _pendingCanon = c; _pendingCount = 1;
        }
        _pendingText = read;

        if (_pendingCount >= confirmReads)
        {
            _shownCanon = c; _pendingCanon = null; _pendingCount = 0;
            return new ShowLine(read);
        }
        return null;
    }

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
