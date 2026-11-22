using System.Text.RegularExpressions;

namespace OyunAltyazi.Core;

public static partial class GeminiSettings
{
    /// <summary>Latest stable Flash model per ai.google.dev/gemini-api/docs/models (checked 2026-11-22). Overridable in settings.json (GeminiModel).</summary>
    public const string DefaultModel = "gemini-3.8-flash";

    [GeneratedRegex(@"^gemini-[a-z0-9][a-z0-9.\-]{0,60}$")]
    private static partial Regex ModelPattern();

    /// <summary>Only plain Gemini model ids: the name goes into the URL path, so nothing else (no ':', '/', '?') is accepted.</summary>
    public static bool IsValidModel(string? model) => model is not null && ModelPattern().IsMatch(model);
}
