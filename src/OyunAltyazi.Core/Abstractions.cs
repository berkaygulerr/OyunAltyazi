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

public interface IFrameSource : IDisposable
{
    event Action<Frame>? FrameArrived;
    event Action<string>? Stopped;
    void Start();
    void Stop();
}

public interface IOcrEngine
{
    Task<OcrResult> ReadAsync(Frame frame, CancellationToken ct);
}

/// <summary>One translated utterance of the on-screen subtitle (a speaker line or an unlabelled one).</summary>
public sealed record ShownUtterance(string? Speaker, string Text, TranslationSource Source);

public interface ISubtitleSink
{
    /// <summary>The whole current subtitle as stacked utterances in on-screen order (replaces what was shown before).</summary>
    /// <param name="box">Stabilised cover box (frame fractions) covering the whole English block, when known.</param>
    /// <param name="anchorTop">The bubble is pinned by its top edge (the game subtitle grows downward) instead of its bottom edge.</param>
    void Show(IReadOnlyList<ShownUtterance> utterances, RegionF? box = null, int lineCount = 1, bool anchorTop = false);
    void Clear();
}
