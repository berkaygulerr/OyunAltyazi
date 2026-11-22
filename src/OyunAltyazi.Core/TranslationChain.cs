using System.Diagnostics;

namespace OyunAltyazi.Core;

public enum TranslationSource { Cache, Translator, Fallback, Pack, Glossary, Gemini, AutoGlossary }

public sealed record TranslationResult(string Text, TranslationSource Source, TimeSpan Elapsed, string? Error = null);

/// <param name="premium">Optional online translator (Gemini). Null = never any request to it. When set it is tried after the exact local
/// glossary/pack and its own scoped cache; any failure (429, timeout, invalid content...) silently falls back to the local path.</param>
public sealed class TranslationChain(ITranslationCache cache, ITranslator translator, ILocalTranslations? local = null, ITranslator? premium = null)
{
    private static string ScopedGame(ITranslator t, TranslationContext ctx) =>
        t is ICacheScoped s ? ctx.Game + "" + s.CacheScope(ctx) : ctx.Game;

    private static bool IsQuietFailure(Exception e) => e is TranslatorException or HttpRequestException or OperationCanceledException;

    public Task<TranslationResult> TranslateAsync(SubtitleText line, TranslationContext ctx, CancellationToken ct) => TranslateCoreAsync(line, ctx, ct, allowPremium: true);

    private async Task<TranslationResult> TranslateCoreAsync(SubtitleText line, TranslationContext ctx, CancellationToken ct, bool allowPremium)
    {
        var sw = Stopwatch.StartNew();
        // exact local glossary / game pack first (never fuzzy, never another game's), then cache, then the model
        if (local is not null && local.TryGet(ctx.Game, line.Text, out var localText, out var localSource))
            return new TranslationResult(localText, localSource, sw.Elapsed);
        if (allowPremium && premium is not null)
        {
            var pg = ScopedGame(premium, ctx);
            if (cache.TryGet(pg, line.Text, out var pc)) return new TranslationResult(pc, TranslationSource.Cache, sw.Elapsed);
            try
            {
                var pr = await premium.TranslateAsync(line.Text, ctx, ct).ConfigureAwait(false);
                cache.Put(pg, line.Text, pr, premium.Name);
                return new TranslationResult(pr, TranslationSource.Gemini, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (IsQuietFailure(e)) { /* quiet: use the local path below */ }
        }
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
        catch (Exception e) when (IsQuietFailure(e))
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
        if (local is not null && local.TryGet(ctx.Game, joined.Text, out var whole, out var wholeSource)
            && RemainderAfterPrefix(whole, shownPrefix) is { } rest && IsUsable(rest, shownPrefix))
            return new TranslationResult(rest, wholeSource, sw.Elapsed);   // only the new part, never the shown prefix
        var key = joined.Text + "" + shownPrefix;
        if (premium is IContinuationTranslator pc2)
        {
            var pg = ScopedGame(premium, ctx);
            if (cache.TryGet(pg, key, out var pcached) && IsUsable(pcached, shownPrefix))
                return new TranslationResult(pcached, TranslationSource.Cache, sw.Elapsed);
            try
            {
                var c = await pc2.TranslateContinuationAsync(joined.Text, shownPrefix, ctx, ct).ConfigureAwait(false) ?? "";
                var raw = (RemainderAfterPrefix(c, shownPrefix) ?? c).Trim();   // never the shown prefix again
                if (raw.Length > 0 && IsUsable(raw, shownPrefix)) { cache.Put(pg, key, raw, premium.Name); return new TranslationResult(raw, TranslationSource.Gemini, sw.Elapsed); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (IsQuietFailure(e)) { }
        }
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
            // no usable continuation: the fragment alone (never the prefix again; no second online request)
            return await TranslateCoreAsync(fragment, ctx, ct, allowPremium: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (IsQuietFailure(e))
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
