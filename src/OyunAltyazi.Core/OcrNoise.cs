// Ported from GameTranslator (sefflex), AGPL-3.0: OcrNoise.cs (see NOTICE.md). Speaker-icon repair is not ported (TextCleaner owns speakers).
using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

/// <summary>
/// OCR invents junk "words" from stylised fonts, fades and HUD edges ("ou$3e", "waters2aljd", "giriş>", "Enter >").
/// Cleaning happens per OCR line BEFORE translation: stray symbols/bullets/arrows are dropped, junk glued to a real
/// word is cut off, junk words are dropped. Usual forms ("PS5", "R2", "2nd", "100m", "$500") are kept.
/// </summary>
public static partial class OcrNoise
{
    private const string Symbols = "$@#%^*_=|\\~<>{}¢£¥§©®°±µ¶¤";

    public static string Clean(string line)
    {
        var kept = new List<string>();
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var (lead, core, trail) = Split(token);
            // A lone "l" / "|" is usually a misread "I"; before a non-lowercase word it is the subtitle box edge.
            if (MisreadI(core) is { } pronoun)
            {
                var next = index + 1 < tokens.Length ? tokens[index + 1].FirstOrDefault(char.IsLetter) : '\0';
                if (pronoun != "I" || char.IsLower(next)) kept.Add(lead + pronoun + trail);
                continue;
            }
            // Punctuation-only fragment ("?! Where", "car. ?!"): OCR invention; glued to the previous word when it has no end mark yet.
            if (core.Length == 0 && IsStrayPunctuation(token))
            {
                if (kept.Count > 0 && !".?!…,;:".Contains(kept[^1][^1])) kept[^1] += token;
                continue;
            }
            // A token made only of symbols / arrows / bullets (">", "»", "•", "<<") is never subtitle text.
            if (IsSymbolOnly(token)) continue;
            if (CleanCore(core) is { } fixedCore)
                kept.Add(lead + fixedCore + trail);
            else if (trail.Length > 0 && kept.Count > 0)
                kept[^1] += trail; // punctuation after dropped junk belongs to the sentence
        }
        // The HUD counter next to a subtitle glues a number onto the line start ("9? We'll lose", "23m CESARE: Here!").
        if (kept.Count >= 3 && LeadingNumber().IsMatch(kept[0]) && char.IsUpper(kept[1][0])) kept.RemoveAt(0);
        return FixLineEnd(string.Join(' ', kept));
    }

    /// <summary>Junk glued to a sentence end: OCR invents a letter or two after the final dot ("Focus.ie", "now.l.") and reads an ellipsis as low quotes.</summary>
    private static string FixLineEnd(string line)
    {
        var text = TrailingJunk().Replace(LowQuoteDots().Replace(line, "..."), "");
        return StrayTail().Replace(text, "");
    }

    [GeneratedRegex(@"(?<=[.!?…])(?:\s+(?:(?=\S{0,2}[^\p{L}\d\s.!?…])\S{1,3}|\p{L}))+$")]
    private static partial Regex StrayTail();

    [GeneratedRegex(@"(?<=\p{L}{3}[.!?…])\p{Ll}{1,2}[.?]?$")]
    private static partial Regex TrailingJunk();

    [GeneratedRegex(@"\.„\.?|„\.")]
    private static partial Regex LowQuoteDots();

    [GeneratedRegex(@"^\d+(?:m|km|ft|yd)?[.?!:;]?$")]
    private static partial Regex LeadingNumber();

    /// <summary>How word-like a read is: letters of clean words minus twice the letters of junk / mixed-case pieces. For picking the better of two reads.</summary>
    public static int Score(string text)
    {
        var score = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var core = Split(token).Core;
            var letters = core.Count(char.IsLetter);
            if (letters == 0) continue;
            var clean = CleanCore(core) == core && NormalCase(core) && core.IndexOfAny(['[', ']', '(', ')']) < 0;
            score += clean ? letters : -2 * letters;
        }
        return score;
    }

    /// <summary>More than a sixth of the letters sit in broken words (mixed case, symbols, vowel-less "Gtt"): a fade/transition garbage read.</summary>
    public static bool LooksGarbled(string text)
    {
        int good = 0, bad = 0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var core = Split(token).Core;
            var letters = new string(core.Where(char.IsLetter).ToArray());
            if (letters.Length == 0) continue;
            var clean = CleanCore(core) == core && NormalCase(core) && core.IndexOfAny(['[', ']', '(', ')']) < 0 && !Vowelless(letters);
            if (clean) good += letters.Length; else bad += letters.Length;
        }
        return bad * 6 > good + bad;
    }

    private static bool Vowelless(string letters) =>
        letters.Length >= 3 && !letters.All(char.IsUpper) && letters.ToLowerInvariant().IndexOfAny("aeiouyäöüıàáâèéêìíîòóôùúû".ToCharArray()) < 0;

    private static bool NormalCase(string core)
    {
        var letters = core.Where(char.IsLetter).ToArray();
        if (letters.All(char.IsUpper) || letters.Skip(1).All(char.IsLower)) return true;
        return letters.Skip(1).Count(char.IsUpper) == 1 && letters.Count(char.IsLower) >= 2; // McDonald, iPhone
    }

    private static string? MisreadI(string core)
    {
        if (core.Length == 0 || core[0] is not ('l' or '|')) return null;
        if (core.Length == 1) return "I";
        if (core.Length < 3 || core[1] is not ('\'' or '’') || core[2..] is not ("m" or "ll" or "ve" or "d")) return null;
        return "I" + core[1..];
    }

    private static bool IsStrayPunctuation(string token) =>
        token.Length > 0 && token.All(c => "?!.,;:".Contains(c)) && !(token.Length >= 2 && token.All(c => c == '.'));

    /// <summary>No letter or digit and at least one symbol / bullet / arrow / bracket-like char. Dashes, quotes, ellipses and plain punctuation are not.</summary>
    internal static bool IsSymbolOnly(string token) =>
        token.Length > 0 && !token.Any(char.IsLetterOrDigit) &&
        token.Any(c => Symbols.Contains(c) || "»«•·●○■□▪▶◀►◄→←↑↓✕×✖[]".Contains(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.OtherSymbol or System.Globalization.UnicodeCategory.MathSymbol);

    private static string? CleanCore(string core)
    {
        var letters = core.Count(char.IsLetter);
        var digits = core.Count(char.IsNumber);
        var hasSymbol = core.Any(c => Symbols.Contains(c));
        if (letters == 0) return core;                       // "24", "$500", "…"
        if (!hasSymbol && digits == 0) return core;
        if (!hasSymbol && IsUsualMix(core)) return core;
        // Junk glued to a real word: keep the leading >=3 letters ("waters2aljd" -> "waters", "Enter>" -> "Enter").
        var prefix = new string(core.TakeWhile(c => char.IsLetter(c) || c is '\'' or '’' or '-').ToArray());
        return prefix.Count(char.IsLetter) >= 3 ? prefix : null;
    }

    private static bool IsUsualMix(string core) =>
        core.Split('-').All(part => !part.Any(char.IsNumber) || Usual().IsMatch(part));

    [GeneratedRegex(@"^(?:[A-Za-z]{1,4}\d{1,4}|\d+(?:st|nd|rd|th|s|k|K|m|km|kg|cm|mm|p|x|fps|hp|HP|XP|xp|v|V|D|d)|[xX]\d{1,3}|\d+[.,]\d+[A-Za-z]{0,3}|\d{1,2}[:.]\d{2}(?:am|pm|AM|PM)?)$")]
    private static partial Regex Usual();

    private static bool IsEdge(char c) =>
        !Symbols.Contains(c) && (char.IsPunctuation(c) || char.IsSymbol(c) || "\"'“”‘’«»()[]".Contains(c));

    private static (string Lead, string Core, string Trail) Split(string token)
    {
        int start = 0, end = token.Length;
        while (start < end && IsEdge(token[start])) start++;
        while (end > start && IsEdge(token[end - 1])) end--;
        return (token[..start], token[start..end], token[end..]);
    }
}
