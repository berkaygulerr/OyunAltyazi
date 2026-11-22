using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

/// <summary>
/// Conservative repair of OCR-damaged lower-case words against the embedded English word list. A word is only replaced when it is
/// not a word / contraction / known slang, is long enough, and exactly ONE list word is a single edit (or one OCR look-alike
/// substitution such as rn-&gt;m) away. Names (any capital), acronyms, tokens with digits, apostrophes or hyphens, ambiguous words
/// and lines that are mostly unreadable are returned untouched. Offline, no allocation of a full SymSpell index: each list word is
/// stored under its first and last half (pigeonhole: one edit leaves one half intact), so a lookup verifies a few candidates only.
/// </summary>
public static class OcrWordCorrector
{
    private const int MinLength = 5, MaxLength = 18;
    private const int TokensPerUnknown = 4;   // a line may hold one unreadable token per 4 tokens (at least 1); more means the read is junk

    private static readonly Regex Token = new(@"\p{L}[\p{L}\p{N}'’\-]*", RegexOptions.Compiled);
    // Short words that are normal on their own; any other 1-3 letter token beside an unknown word looks like a piece of it.
    private static readonly HashSet<string> CommonShort = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "are", "not", "but", "can", "all", "any", "was", "his", "her", "him", "she", "who", "how", "why", "yes", "now",
        "get", "got", "let", "may", "our", "out", "one", "two", "new", "old", "see", "say", "way", "too", "off", "its", "had", "has", "did", "end",
        "man", "run", "go", "do", "no", "so", "to", "of", "in", "on", "at", "by", "up", "us", "we", "me", "my", "he", "it", "is", "if", "or", "as",
        "an", "be", "am", "oh", "ok", "hi", "a", "ago", "yet", "few", "try", "ask", "use", "own", "big", "bad", "far", "low", "hit", "hot", "key", "car",
        "war", "gun", "men", "day", "job", "law", "lot", "red", "sir", "top", "won", "dog", "eye", "fly", "pay", "put", "set", "sit", "sun", "ten", "win", "ran", "saw", "fix", "ever",
    };
    private static readonly HashSet<string> Slang = new(StringComparer.Ordinal)
    {
        "gonna", "wanna", "gotta", "kinda", "sorta", "lemme", "gimme", "dunno", "outta", "hafta", "coulda", "woulda", "shoulda", "mighta",
        "musta", "oughta", "lotta", "gotcha", "whatcha", "cuz", "cmon", "yeah", "yeap", "yep", "nope", "okay", "huh", "ugh", "hmm", "hmmm",
    };
    // Symbols that real subtitles do not contain: their presence means the whole line is damaged and nothing in it is trusted.
    private static readonly Regex Junk = new(@"[^\p{L}\p{N}\s.,!?'’"":;\-–—…()/%&]", RegexOptions.Compiled);
    // Multi-char look-alikes: sequence read -> sequence intended.
    private static readonly (string From, string To)[] Rules = [("rn", "m"), ("vv", "w"), ("cl", "d")];

    private sealed class Index
    {
        public readonly Dictionary<string, List<string>> Prefix = new(StringComparer.Ordinal), Suffix = new(StringComparer.Ordinal);
        public HashSet<string> Words = new(StringComparer.Ordinal);
    }
    private static readonly Lazy<Index> Idx = new(Build);
    private static readonly ConcurrentDictionary<string, string> Memo = new(StringComparer.Ordinal);

    private static Index Build()
    {
        var ix = new Index();
        foreach (var w in EnglishWords.All)
        {
            if (w.Length < MinLength - 1 || w.Length > MaxLength + 1 || !IsLowerAscii(w)) continue;
            ix.Words.Add(w);
            int h = w.Length / 2;
            Add(ix.Prefix, w[..h], w); Add(ix.Suffix, w[^h..], w);
        }
        return ix;
    }

    private static void Add(Dictionary<string, List<string>> d, string key, string w)
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<string>(2);
        l.Add(w);
    }

    private static bool IsLowerAscii(string s) { foreach (var c in s) if (c < 'a' || c > 'z') return false; return true; }

    public static string Correct(string text)
    {
        if (string.IsNullOrEmpty(text) || !EnglishWords.Available) return text;
        var matches = Token.Matches(text);
        if (matches.Count == 0) return text;

        if (Junk.IsMatch(text)) return text;
        int tokens = matches.Count, unknown = 0;
        var kind = new Kind[tokens];
        for (int i = 0; i < tokens; i++)
        {
            var w = matches[i].Value;
            if (IsSuspect(w)) { kind[i] = Kind.Broken; unknown++; }
            else if (!IsCandidateShape(w)) kind[i] = Kind.Fine;
            else if (EnglishWords.IsWord(w) || TextQuality.IsContraction(w)) kind[i] = w.Length <= 3 && !CommonShort.Contains(w) ? Kind.Fragment : Kind.Fine;
            else { kind[i] = Kind.Unknown; unknown++; }
        }
        List<(Match m, string fix)>? fixes = null;
        for (int i = 0; i < tokens; i++)
        {
            var w = matches[i].Value;
            if (kind[i] != Kind.Unknown || w.Length < MinLength) continue;
            // A word next to another unreadable token or a stray fragment ("li utenant", "distinc ion") is a split / truncated word: no repair.
            if (Damaged(kind, matches, text, i - 1, i) || Damaged(kind, matches, text, i + 1, i)) continue;
            var fix = Memo.TryGetValue(w, out var memo) ? memo : Memoise(w, Resolve(w));
            if (fix.Length > 0) (fixes ??= new()).Add((matches[i], fix));
        }
        if (fixes is null || unknown > Math.Max(1, tokens / TokensPerUnknown)) return text;

        var sb = new StringBuilder(text.Length);
        int last = 0;
        foreach (var (m, fix) in fixes) { sb.Append(text, last, m.Index - last).Append(fix); last = m.Index + m.Length; }
        return sb.Append(text, last, text.Length - last).ToString();
    }

    private enum Kind : byte { Fine, Unknown, Broken, Fragment }

    private static bool Damaged(Kind[] kind, MatchCollection matches, string text, int neighbour, int self)
    {
        if (neighbour < 0 || neighbour >= kind.Length || kind[neighbour] == Kind.Fine) return false;
        int from = Math.Min(neighbour, self), to = Math.Max(neighbour, self);
        int gapStart = matches[from].Index + matches[from].Length;
        for (int i = gapStart; i < matches[to].Index; i++) if (text[i] is not (' ' or ',' or ';' or '-' or '.')) return false;   // quote/bracket/other symbol in the gap: not neighbours
        return true;
    }

    private static string Memoise(string w, string fix)
    {
        if (Memo.Count > 8192) Memo.Clear();
        Memo[w] = fix;
        return fix;
    }

    // Only plain lower-case ASCII words that are not slang and not elongated ("heyyy") are ever judged.
    private static bool IsCandidateShape(string w)
    {
        if (w.Length > MaxLength || !IsLowerAscii(w) || Slang.Contains(w)) return false;
        for (int i = 2; i < w.Length; i++) if (w[i] == w[i - 1] && w[i] == w[i - 2]) return false;
        return true;
    }

    // Fragments that cannot be judged but prove the read is damaged: stray single letters, odd inner capitals ("IiVe", "thlS").
    private static bool IsSuspect(string w)
    {
        int letters = w.Count(char.IsLetter);
        if (letters == 1) return !(w is "a" or "A" or "I" or "O");
        for (int i = 1; i < w.Length; i++)
            if (char.IsUpper(w[i]) && char.IsLower(w[i - 1])) return true;
        return false;
    }

    /// <summary>The single repair for an unknown word, or "" when there is none or it is ambiguous.</summary>
    private static string Resolve(string q)
    {
        var ix = Idx.Value;
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (int n = q.Length; n <= q.Length + 1; n++)   // same length or one letter dropped by OCR; never delete letters
        {
            int h = n / 2;
            if (h > q.Length) continue;
            if (ix.Prefix.TryGetValue(q[..h], out var a)) foreach (var w in a) if (w.Length == n && Within1(q, w)) found.Add(w);
            if (ix.Suffix.TryGetValue(q[^h..], out var b)) foreach (var w in b) if (w.Length == n && Within1(q, w)) found.Add(w);
        }
        foreach (var (from, to) in Rules)
            for (int i = q.IndexOf(from, StringComparison.Ordinal); i >= 0; i = q.IndexOf(from, i + 1, StringComparison.Ordinal))
            {
                var v = q[..i] + to + q[(i + from.Length)..];
                if (ix.Words.Contains(v)) found.Add(v);
            }
        return found.Count == 1 ? found.First() : "";   // several candidates = ambiguous: leave the word as read
    }

    /// <summary>Levenshtein distance exactly 1 (substitution, insertion or deletion); equal strings are not "within".</summary>
    private static bool Within1(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1 || a == b) return false;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (a.Length == b.Length) return string.CompareOrdinal(a, i + 1, b, i + 1, a.Length) == 0;
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        return string.CompareOrdinal(s, i, l, i + 1, s.Length) == 0;
    }
}
