using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TranslationChainTests
{
    private sealed class MemCache : ITranslationCache
    {
        public Dictionary<string, string> D = new();
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) { var r = D.TryGetValue(g + "|" + TextKey.Canon(s), out t); return r; }
        public void Put(string g, string s, string t, string p) => D[g + "|" + TextKey.Canon(s)] = t;
    }
    private sealed class Fake(Func<string, CancellationToken, Task<string>> f) : ITranslator
    {
        public int Calls;
        public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) { Calls++; return f(text, ct); }
    }
    private static readonly TranslationContext Ctx = new("g", [], null);
    private static readonly SubtitleText Line = new("Price", "Move up, now.");

    [Fact] public async Task Cache_hit_skips_translator()
    {
        var c = new MemCache(); c.Put("g", "Move up, now.", "Yukarı çıkın.", "x");
        var t = new Fake((_, _) => Task.FromResult("x"));
        var r = await new TranslationChain(c, t).TranslateAsync(Line, Ctx, default);
        Assert.Equal(TranslationSource.Cache, r.Source); Assert.Equal("Yukarı çıkın.", r.Text); Assert.Equal(0, t.Calls);
    }
    [Fact] public async Task Miss_calls_translator_and_stores()
    {
        var c = new MemCache(); var t = new Fake((_, _) => Task.FromResult("Yukarı çıkın."));
        var r = await new TranslationChain(c, t).TranslateAsync(Line, Ctx, default);
        Assert.Equal(TranslationSource.Translator, r.Source);
        Assert.True(c.TryGet("g", "Move up, now.", out var s)); Assert.Equal("Yukarı çıkın.", s);
    }
    [Fact] public async Task Translator_error_returns_source_as_fallback_without_caching()
    {
        var c = new MemCache(); var t = new Fake((_, _) => throw new TranslatorException("429", 429));
        var r = await new TranslationChain(c, t).TranslateAsync(Line, Ctx, default);
        Assert.Equal(TranslationSource.Fallback, r.Source); Assert.Equal("Move up, now.", r.Text); Assert.NotNull(r.Error);
        Assert.Empty(c.D);
    }
    [Fact] public async Task Outer_cancellation_propagates()
    {
        var t = new Fake(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; });
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranslationChain(new MemCache(), t).TranslateAsync(Line, Ctx, cts.Token));
    }
}
