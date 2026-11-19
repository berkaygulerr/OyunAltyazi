using System.Diagnostics;

namespace OyunAltyazi.Core;

public enum TranslationSource { Cache, Translator, Fallback }

public sealed record TranslationResult(string Text, TranslationSource Source, TimeSpan Elapsed, string? Error = null);

public sealed class TranslationChain(ITranslationCache cache, ITranslator translator)
{
    public async Task<TranslationResult> TranslateAsync(SubtitleText line, TranslationContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (cache.TryGet(ctx.Game, line.Text, out var cached))
            return new TranslationResult(cached, TranslationSource.Cache, sw.Elapsed);

        try
        {
            var tr = await translator.TranslateAsync(line.Text, ctx, ct).ConfigureAwait(false);
            cache.Put(ctx.Game, line.Text, tr, translator.Name);
            return new TranslationResult(tr, TranslationSource.Translator, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is TranslatorException or HttpRequestException or OperationCanceledException)
        {
            return new TranslationResult(line.Text, TranslationSource.Fallback, sw.Elapsed, e.Message);
        }
    }
}
