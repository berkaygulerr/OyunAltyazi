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

public interface ISubtitleSink
{
    /// <param name="box">Window-relative (0..1) bounding box of the original English subtitle, when known.</param>
    void Show(string? speaker, string text, TranslationSource source, RegionF? box = null, int lineCount = 1);
    void Clear();
}
