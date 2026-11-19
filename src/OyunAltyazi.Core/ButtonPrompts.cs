// Ported from GameTranslator (sefflex), AGPL-3.0: ButtonPrompts (Reading.cs) + PC key names / bracketed key hints (see NOTICE.md).
using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

/// <summary>Recognises on-screen control hints ("R2 Swing", "[F] Interact", "Press E to open", "Enter >") - HUD, never subtitles.</summary>
public static class ButtonPrompts
{
    private static readonly HashSet<string> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        "R1", "R2", "R3", "L1", "L2", "L3", "LB", "RB", "LT", "RT", "OPTIONS", "TOUCHPAD",
        "✕", "×", "⨯", "✖", "○", "◯", "●", "□", "▢", "◻", "■", "△", "▲", "⨂",
    };

    /// <summary>PC key names; a line made only of these (optionally plus one more word) is a hint.</summary>
    internal static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "enter", "esc", "escape", "space", "spacebar", "tab", "shift", "ctrl", "alt", "caps", "lmb", "rmb", "mmb",
        "backspace", "pgup", "pgdn", "mouse1", "mouse2", "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    };

    private static readonly HashSet<string> Lookalikes = new(StringComparer.Ordinal) { "X", "O" };
    private static readonly HashSet<string> MisreadButtons = new(StringComparer.Ordinal) { "RI", "LI", "Rl", "Ll", "RZ", "LZ" };
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase) { "press", "hold", "tap", "push", "use", "mash", "release" };
    private static readonly char[] Separators = [' ', '\t', '+', '/'];
    private static readonly char[] Brackets = ['(', ')', '[', ']', '{', '}', '<', '>', '「', '」'];
    private static readonly Regex BracketKey = new(@"^\s*[\[<(]\s*[^\]>)\s]{1,8}\s*[\]>)]", RegexOptions.Compiled);
    private static readonly Regex Labels = new(@"\((?:[A-Z]{1,3}|[A-Z]{2,4})\)", RegexOptions.Compiled);
    private static readonly Regex Decimal = new(@"(?<![\w.])\d+\.\d(?![\w.])", RegexOptions.Compiled);

    internal static bool EndsSentence(string line)
    {
        var t = line.Trim().TrimEnd('"', '\'', '”', '’', '»', ')', ']');
        return t.Length > 0 && ".!?…".Contains(t[^1]);
    }

    private static bool IsInterfaceBar(string line)
    {
        if (EndsSentence(line)) return false;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var labels = Labels.Matches(line).Count;
        if (labels >= 2 || labels + tokens.Count(Lookalikes.Contains) >= 3) return true;
        var words = tokens.Count(token => token.Count(char.IsLetter) >= 2);
        return words <= 6 && Decimal.IsMatch(line);
    }

    public static bool IsPrompt(string line)
    {
        var tokens = line.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim(Brackets))
            .Where(token => token.Length > 0)
            .ToList();
        if (tokens.Count == 0) return false;
        if (IsInterfaceBar(line)) return true;
        // "[F] Interact", "<Price> text", "(E) Open": a short bracketed key/label at the line start, not reading as a sentence.
        if (BracketKey.IsMatch(line) && tokens.Count <= 6 && !EndsSentence(line)) return true;
        // Key names ("Enter", "Space >", "Hold Shift"): key names plus at most one other word.
        if (!EndsSentence(line) && tokens.Count <= 3 && tokens.All(Keys.Contains)) return true;
        if (!EndsSentence(line) && tokens.Count == 2 && Keys.Contains(tokens[0]) && char.IsUpper(tokens[1][0])) return true;
        var trimmed = line.TrimStart();
        if (trimmed.Length > 1 && trimmed[0] is ':' or ';' or '•' or '©' or '®' && char.IsUpper(trimmed.TrimStart(':', ';', '•', '©', '®', ' ').FirstOrDefault()) &&
            tokens.Count <= 5 && !EndsSentence(line))
            return true;
        var first = tokens[0];
        var words = tokens.Count(token => !Buttons.Contains(token) && token.Any(char.IsLetter));
        if (Buttons.Contains(first)) return words <= 5;
        if (MisreadButtons.Contains(first) && tokens.Count >= 2 && words <= 6 && !EndsSentence(line)) return true;
        if (Buttons.Contains(tokens[^1]) && words <= 4 && !EndsSentence(line)) return true;
        if (Lookalikes.Contains(first) && tokens.Count is >= 4 and <= 16 && !EndsSentence(line) &&
            tokens.Skip(1).Any(token => Lookalikes.Contains(token) || Buttons.Contains(token)) &&
            tokens.Count(token => char.IsUpper(token[0])) * 3 >= tokens.Count * 2)
            return true;
        if (Lookalikes.Contains(first) && tokens.Count is >= 2 and <= 4 && char.IsUpper(tokens[1][0]) && !EndsSentence(line)) return true;
        if (Verbs.Contains(first) && tokens.Count is >= 2 and <= 10)
            return Buttons.Contains(tokens[1]) || Keys.Contains(tokens[1]) || Lookalikes.Contains(tokens[1]) || MisreadButtons.Contains(tokens[1]) ||
                   (tokens[1].Length <= 2 && !tokens[1].Any(char.IsLetterOrDigit)) ||
                   (tokens.Count >= 4 && tokens[1].Length <= 2 && string.Equals(tokens[2], "to", StringComparison.OrdinalIgnoreCase));
        return false;
    }
}
