// Ported from GameTranslator (sefflex), AGPL-3.0: Misspelling.cs + WindowsSpelling.InEnglishList / english-words.txt.gz (see NOTICE.md).
using System.IO.Compression;

namespace OyunAltyazi.Core;

/// <summary>Common English words (SCOWL size 60, ~93k, lower case), embedded as a gz resource.</summary>
public static class EnglishWords
{
    private static readonly Lazy<HashSet<string>> Words = new(Load);

    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(EnglishWords).Assembly.GetManifestResourceStream("OyunAltyazi.Core.english-words.txt.gz");
            if (stream is null) return set;
            using var unzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(unzip);
            while (reader.ReadLine() is { } line) if (line.Length > 0) set.Add(line);
        }
        catch { }
        return set;
    }

    internal static IReadOnlyCollection<string> All => Words.Value;

    /// <summary>The list loaded (when it did not, every quality check passes: no list, no judgement).</summary>
    public static bool Available => Words.Value.Count > 0;

    /// <summary>Word is in the list, including "'s", "s'", dropped-g "-in'" forms and hyphenated parts.</summary>
    public static bool IsWord(string word)
    {
        var words = Words.Value;
        if (words.Count == 0) return true;
        var lower = word.ToLowerInvariant().Replace('’', '\'');
        if (words.Contains(lower)) return true;
        if (lower.EndsWith("'s", StringComparison.Ordinal) && words.Contains(lower[..^2])) return true;
        if (lower.EndsWith("s'", StringComparison.Ordinal) && words.Contains(lower[..^1])) return true;
        if (lower.EndsWith("in'", StringComparison.Ordinal) && words.Contains(lower[..^1] + "g")) return true;
        if (lower.EndsWith("in", StringComparison.Ordinal) && lower.Length > 4 && words.Contains(lower + "g")) return true;
        if (lower.Contains('-')) return lower.Split('-').All(part => part.Length == 0 || words.Contains(part));
        return false;
    }
}

/// <summary>
/// How much of a read is real English (GameTranslator Misspelling): OCR drops letters or glues words in moving scenes
/// ("Igot it", "gonnatry workaroun"); symbols are caught by <see cref="OcrNoise"/>, these need a dictionary.
/// Properly capitalised words ("Kraven") and known speaker names are never counted as misspelled; mixed-case ones ("IVe") are.
/// </summary>
public static class TextQuality
{
    /// <summary>A read needs at least this share of its tokens to be words / names to be shown at all.</summary>
    public const double MinWordRatio = 0.70;
    /// <summary>A new read must beat the shown one by this many points to replace it.</summary>
    public const int UpgradeMargin = 4;

    private static string Core(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && !char.IsLetter(token[start])) start++;
        while (end > start && !char.IsLetter(token[end - 1])) end--;
        return token[start..end];
    }

    internal static bool IsContraction(string word)
    {
        var lower = word.ToLowerInvariant();
        if (lower is "em" or "cmon" or "ya" or "til" or "cause") return true;
        if (word.Contains('\'') || word.Contains('’') || word.Length < 3) return false;
        foreach (var tail in new[] { 1, 2 })
            if (word.Length > tail && EnglishWords.IsWord(word[..^tail] + "'" + word[^tail..])) return true;
        return false;
    }

    private static bool IsProper(string word) => char.IsUpper(word[0]) && word.Skip(1).Where(char.IsLetter).All(char.IsLower);

    /// <summary>Letters in misspelled words and in all words (single letters other than a/I/O and all-caps abbreviations are not judged).</summary>
    public static (int Bad, int All) Measure(string text)
    {
        int bad = 0, all = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Core(token);
            var letters = word.Count(char.IsLetter);
            if (letters == 0) continue;
            all += letters;
            if (letters < 2 || word.All(c => !char.IsLetter(c) || char.IsUpper(c))) continue;
            if (IsProper(word))
            {
                // "I" glued to the next word ("Igot it") is a misread, not a name; a whole run-together line is not a name either.
                if (word[0] == 'I' && letters >= 3 && !EnglishWords.IsWord(word) && EnglishWords.IsWord(word[1..])) bad += letters;
                else if (letters > 13 && !word.Contains('-') && !EnglishWords.IsWord(word)) bad += letters;
                continue;
            }
            if (!EnglishWords.IsWord(word) && !IsContraction(word)) bad += letters;
        }
        return (bad, all);
    }

    /// <summary>Share of tokens that are words, contractions, abbreviations, proper-looking names or known speakers (0..1; 1 when nothing to judge).</summary>
    public static double WordRatio(string text, IReadOnlyCollection<string>? known = null)
    {
        int tokens = 0, good = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Core(token);
            var letters = word.Count(char.IsLetter);
            if (letters == 0) continue;                       // numbers, punctuation
            tokens++;
            if (known is not null && known.Any(k => string.Equals(k, word, StringComparison.OrdinalIgnoreCase))) { good++; continue; }
            if (letters == 1) { if (word is "a" or "A" or "I" or "O") good++; continue; }
            if (word.All(c => !char.IsLetter(c) || char.IsUpper(c))) { if (letters <= 6) good++; continue; }   // PS5, CIA, AK
            if (IsProper(word)) { if (!(word[0] == 'I' && letters >= 3 && !EnglishWords.IsWord(word) && EnglishWords.IsWord(word[1..])) && letters <= 13) good++; continue; }
            if (EnglishWords.IsWord(word) || IsContraction(word)) good++;
        }
        return tokens == 0 ? 1 : (double)good / tokens;
    }

    /// <summary>The read is English enough to show/translate: &gt;= 70 % of tokens are words/names.</summary>
    public static bool Passes(string text, IReadOnlyCollection<string>? known = null) =>
        !EnglishWords.Available || WordRatio(text, known) >= MinWordRatio;

    /// <summary>Word-likeness points: <see cref="OcrNoise.Score"/> minus 3x the letters of misspelled words. Higher is better.</summary>
    public static int Score(string text) => OcrNoise.Score(text) - 3 * Measure(text).Bad;
}
