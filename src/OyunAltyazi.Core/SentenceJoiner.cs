namespace OyunAltyazi.Core;

/// <summary>Result of feeding a stabilized line: <see cref="Joined"/> is non-null when it continues the previous open sentence.</summary>
public sealed record JoinResult(SubtitleText? Joined, int Fragments);

/// <summary>Detects subtitle sentences that the game splits across consecutive screens, so they can be translated as a whole.</summary>
public sealed class SentenceJoiner(int windowMs = 4000, int maxFragments = 3, int maxChars = 300)
{
    private string? _open;      // accumulated English text of the open sentence
    private string? _speaker;
    private long _lastMs;
    private int _frags;

    public JoinResult Feed(SubtitleText line, long nowMs)
    {
        SubtitleText? joined = null;
        int frags = 1;
        if (_open is not null && nowMs - _lastMs <= windowMs && _frags < maxFragments
            && (line.Speaker is null || _speaker is null || string.Equals(line.Speaker, _speaker, StringComparison.OrdinalIgnoreCase))
            && _open.Length + 1 + line.Text.Length <= maxChars
            && Continues(_open, line.Text))
        {
            joined = new SubtitleText(_speaker ?? line.Speaker, _open + " " + line.Text);
            frags = _frags + 1;
        }
        var full = joined?.Text ?? line.Text;
        var speaker = joined?.Speaker ?? line.Speaker;
        if (IsOpen(full)) { _open = full; _speaker = speaker; _frags = frags; _lastMs = nowMs; }
        else _open = null;
        return new JoinResult(joined, frags);
    }

    public void Reset() => _open = null;

    private static string TrimClosers(string s) => s.TrimEnd().TrimEnd('"', '\'', ')', ']', '”', '’', '»', '*');

    private static bool EndsEllipsis(string t) => t.EndsWith("...") || t.EndsWith('…');

    public static bool IsOpen(string text)
    {
        var t = TrimClosers(text);
        if (t.Length == 0) return false;
        if (EndsEllipsis(t)) return true;
        return t[^1] is not ('.' or '!' or '?');
    }

    private static bool Continues(string prev, string next)
    {
        var t = TrimClosers(prev);
        var n = next.TrimStart();
        if (n.Length == 0) return false;
        if (t.Length > 0 && t[^1] is ',' or '—' or '–' or '-' or ';' or ':') return true;
        if (EndsEllipsis(t)) return char.IsLower(n[0]);
        return IsOpen(prev);
    }
}
