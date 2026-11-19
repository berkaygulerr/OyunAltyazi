using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

public static class TextKey
{
    private static readonly Regex Tags = new(@"<[^<>]{0,40}>", RegexOptions.Compiled);

    public static string Canon(string text)
    {
        var s = Tags.Replace(text, " ");
        s = s.Normalize(NormalizationForm.FormKC);
        s = s.Replace("\u0130", "i\u0307");
        s = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return Regex.Replace(sb.ToString(), @" +", " ").Trim();
    }

    public static string PackKey(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canon(text)));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
