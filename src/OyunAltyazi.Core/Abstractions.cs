using System.Diagnostics.CodeAnalysis;

namespace OyunAltyazi.Core;

public interface ITranslationCache
{
    bool TryGet(string game, string sourceText, [NotNullWhen(true)] out string? translation);
    void Put(string game, string sourceText, string translation, string provider);
}

public sealed record TranslationContext(string Game, IReadOnlyList<string> PreviousLines, string? Speaker);

public interface ITranslator
{
    string Name { get; }
    Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct);
}

public sealed class TranslatorException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
