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

    /// <summary>
    /// Translates only the new fragment of a split sentence, using the Turkish already shown for the earlier fragments as context.
    /// Never repeats the prefix; falls back to translating the fragment alone.
    /// </summary>
    public async Task<TranslationResult> TranslateContinuationAsync(SubtitleText fragment, SubtitleText joined, string shownPrefix, TranslationContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = joined.Text + "\u0001" + shownPrefix;
        if (cache.TryGet(ctx.Game, key, out var cached) && IsUsable(cached, shownPrefix))
            return new TranslationResult(cached, TranslationSource.Cache, sw.Elapsed);
        try
        {
            string? cont = null;
            if (translator is IContinuationTranslator ct2)
                cont = await ct2.TranslateContinuationAsync(joined.Text, shownPrefix, ctx, ct).ConfigureAwait(false);
            else
            {
                var full = await translator.TranslateAsync(joined.Text, ctx, ct).ConfigureAwait(false);
                cont = RemainderAfterPrefix(full, shownPrefix);
            }
            cont = cont?.Trim();
            if (cont is not null && IsUsable(cont, shownPrefix))
            {
                cache.Put(ctx.Game, key, cont, translator.Name);
                return new TranslationResult(cont, TranslationSource.Translator, sw.Elapsed);
            }
            // no usable continuation: the fragment alone (never the prefix again)
            return await TranslateAsync(fragment, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is TranslatorException or HttpRequestException or OperationCanceledException)
        {
            return new TranslationResult(fragment.Text, TranslationSource.Fallback, sw.Elapsed, e.Message);
        }
    }

    private static bool IsUsable(string cont, string prefix)
    {
        var c = cont.Trim();
        return c.Length >= 2 && !string.Equals(Norm(c), Norm(prefix), StringComparison.Ordinal);
    }

    private static string Norm(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>If <paramref name="full"/> starts with <paramref name="prefix"/> (ignoring case, spacing and punctuation) returns the rest, else null.</summary>
    public static string? RemainderAfterPrefix(string full, string prefix)
    {
        var np = Norm(prefix);
        if (np.Length == 0) return null;
        int matched = 0, i = 0;
        for (; i < full.Length && matched < np.Length; i++)
        {
            if (!char.IsLetterOrDigit(full[i])) continue;
            if (char.ToLowerInvariant(full[i]) != np[matched]) return null;
            matched++;
        }
        if (matched < np.Length) return null;
        return full[i..].TrimStart(' ', ',', ';', ':', '.', '!', '?', '…', '-', '—', '–');
    }
}
