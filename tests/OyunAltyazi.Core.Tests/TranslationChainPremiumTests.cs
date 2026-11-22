using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TranslationChainPremiumTests
{
    private sealed class MemCache : ITranslationCache
    {
        public Dictionary<string, string> D = new(); public List<string> Puts = new();
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) => D.TryGetValue(g + "|" + TextKey.Canon(s), out t);
        public void Put(string g, string s, string t, string p) { D[g + "|" + TextKey.Canon(s)] = t; Puts.Add(g + "|" + p); }
    }
    private sealed class Base : ITranslator
    {
        public int Calls; public string Name => "base";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) { Calls++; return Task.FromResult("local:" + text); }
    }
    private sealed class Premium : IContinuationTranslator, ICacheScoped
    {
        public int Calls, ContCalls; public Exception? Throw; public string Name => "gemini"; public string Reply = "gem";
        public string CacheScope(TranslationContext ctx) => "gemini:m1:" + string.Join("/", ctx.PreviousLines);
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct)
        { Calls++; if (Throw != null) throw Throw; ct.ThrowIfCancellationRequested(); return Task.FromResult(Reply + ":" + text); }
        public Task<string> TranslateContinuationAsync(string full, string prefix, TranslationContext ctx, CancellationToken ct)
        { ContCalls++; if (Throw != null) throw Throw; return Task.FromResult("devam"); }
    }
    private static GamePack P(string en, string tr) => GamePack.Parse("{\"version\":2,\"game\":\"g.exe\",\"entries\":[{\"en\":\"" + en + "\",\"tr\":\"" + tr + "\"}]}", "g.exe").Pack!;
    private static readonly SubtitleText Line = new("Price", "Move up, now.");
    private static TranslationContext Ctx(params string[] prev) => new("g.exe", prev, "Price");

    [Fact] public async Task Off_no_premium_never_touches_anything_but_base()
    {
        var b = new Base();
        var r = await new TranslationChain(new MemCache(), b).TranslateAsync(Line, Ctx(), default);
        Assert.Equal(TranslationSource.Translator, r.Source); Assert.Equal(1, b.Calls);
    }

    [Fact] public async Task Premium_result_is_used_and_marked_gemini_and_base_not_called()
    {
        var b = new Base(); var p = new Premium();
        var r = await new TranslationChain(new MemCache(), b, null, p).TranslateAsync(Line, Ctx(), default);
        Assert.Equal(("gem:Move up, now.", TranslationSource.Gemini), (r.Text, r.Source));
        Assert.Equal(0, b.Calls);
    }

    [Fact] public async Task Exact_glossary_and_pack_beat_premium()
    {
        var local = new LocalTranslations(); local.SetPack("g.exe", P("Move up now", "paket")); var p = new Premium();
        var r = await new TranslationChain(new MemCache(), new Base(), local, p).TranslateAsync(Line, Ctx(), default);
        Assert.Equal(("paket", TranslationSource.Pack), (r.Text, r.Source)); Assert.Equal(0, p.Calls);
    }

    [Fact] public async Task Premium_cache_is_scoped_by_provider_model_and_context()
    {
        var c = new MemCache(); var p = new Premium(); var chain = new TranslationChain(c, new Base(), null, p);
        await chain.TranslateAsync(Line, Ctx("a"), default);
        var again = await chain.TranslateAsync(Line, Ctx("a"), default);
        Assert.Equal(TranslationSource.Cache, again.Source); Assert.Equal(1, p.Calls);
        await chain.TranslateAsync(Line, Ctx("different context"), default);   // other context => other cache entry
        Assert.Equal(2, p.Calls);
        Assert.DoesNotContain("g.exe|base", c.Puts);
        Assert.All(c.D.Keys, k => Assert.StartsWith("g.exe", k));
    }

    [Fact] public async Task Local_cache_entries_never_answer_in_premium_mode_and_premium_entries_never_leak_to_local()
    {
        var c = new MemCache(); var b = new Base();
        await new TranslationChain(c, b).TranslateAsync(Line, Ctx(), default);                       // local result cached unscoped
        var p = new Premium();
        var r = await new TranslationChain(c, b, null, p).TranslateAsync(Line, Ctx(), default);
        Assert.Equal(TranslationSource.Gemini, r.Source);                                            // premium asked, not the local cache
        var local = await new TranslationChain(c, new Base(), null).TranslateAsync(Line, Ctx(), default);
        Assert.Equal("local:Move up, now.", local.Text);                                             // premium text never served by local mode
    }

    [Theory]
    [InlineData("t")]
    [InlineData("h")]
    [InlineData("o")]   // timeout (token not cancelled)
    public async Task Premium_failure_falls_back_to_local_quietly_without_caching_as_premium(string t)
    {
        var c = new MemCache(); var b = new Base(); var p = new Premium { Throw = t == "t" ? new TranslatorException("x", 429) : t == "h" ? new HttpRequestException("x") : new OperationCanceledException("x") };
        var r = await new TranslationChain(c, b, null, p).TranslateAsync(Line, Ctx(), default);
        Assert.Equal(("local:Move up, now.", TranslationSource.Translator), (r.Text, r.Source)); Assert.Null(r.Error);
        Assert.Equal(["g.exe|base"], c.Puts);
    }

    [Fact] public async Task Caller_cancellation_propagates_and_does_not_fall_back()
    {
        var b = new Base(); var p = new Premium(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TranslationChain(new MemCache(), b, null, p).TranslateAsync(Line, Ctx(), cts.Token));
        Assert.Equal(0, b.Calls);
    }

    [Fact] public async Task Continuation_uses_premium_returns_only_the_continuation()
    {
        var p = new Premium();
        var r = await new TranslationChain(new MemCache(), new Base(), null, p)
            .TranslateContinuationAsync(new SubtitleText(null, "and cover me"), new SubtitleText(null, "Move up and cover me"), "Yukarı çık", Ctx(), default);
        Assert.Equal(("devam", TranslationSource.Gemini), (r.Text, r.Source)); Assert.Equal(1, p.ContCalls);
    }

    [Fact] public async Task Continuation_premium_echoing_the_prefix_falls_to_local_not_to_the_prefix()
    {
        var p = new Premium(); var b = new Base();
        var echo = new EchoPremium();
        var r = await new TranslationChain(new MemCache(), b, null, echo)
            .TranslateContinuationAsync(new SubtitleText(null, "and cover me"), new SubtitleText(null, "Move up and cover me"), "Yukarı çık", Ctx(), default);
        Assert.NotEqual("Yukarı çık", r.Text); Assert.NotEqual(TranslationSource.Gemini, r.Source);
    }
    private sealed class EchoPremium : IContinuationTranslator
    {
        public string Name => "gemini";
        public Task<string> TranslateAsync(string t, TranslationContext c, CancellationToken ct) => Task.FromResult("x");
        public Task<string> TranslateContinuationAsync(string f, string p, TranslationContext c, CancellationToken ct) => Task.FromResult(p);
    }

    [Fact] public async Task Continuation_premium_failure_falls_back_to_local_continuation()
    {
        var p = new Premium { Throw = new TranslatorException("429", 429) }; var b = new Base();
        var r = await new TranslationChain(new MemCache(), b, null, p)
            .TranslateContinuationAsync(new SubtitleText(null, "and cover me"), new SubtitleText(null, "Move up and cover me"), "Yukarı çık", Ctx(), default);
        Assert.Equal(TranslationSource.Translator, r.Source); Assert.Equal(0, p.Calls);   // no second online request for the fragment
    }
}

public class TranslatorChoiceGeminiTests
{
    [Fact] public void Gemini_choice_resolves_base_like_auto()
    {
        Assert.Equal(TranslatorKind.Ct2, TranslatorChoice.Resolve("gemini", true, false));
        Assert.Equal(TranslatorKind.Google, TranslatorChoice.Resolve("gemini", false, false));
    }
    [Theory]
    [InlineData("gemini", true, true, true)]
    [InlineData("gemini", true, false, false)]   // no key
    [InlineData("gemini", false, true, false)]   // no consent
    [InlineData("auto", true, true, false)]      // other modes never use Gemini even with key+consent
    [InlineData("google_only", true, true, false)]
    [InlineData(null, true, true, false)]
    public void Premium_only_when_selected_consented_and_key_present(string? choice, bool consent, bool key, bool expected)
        => Assert.Equal(expected, TranslatorChoice.UseGemini(choice, consent, key));

    [Theory]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-2.5-flash", true)]
    [InlineData("", false)] [InlineData(null, false)]
    [InlineData("gemini-3.8-flash:evil", false)] [InlineData("../x", false)] [InlineData("gemini 3", false)] [InlineData("other-model", false)]
    public void Model_names_are_validated(string? m, bool ok) => Assert.Equal(ok, GeminiSettings.IsValidModel(m));

    [Fact] public void Defaults_are_off_and_old_settings_load()
    {
        var s = new AppSettings();
        Assert.False(s.GeminiConsent); Assert.Equal(GeminiSettings.DefaultModel, s.GeminiModel); Assert.NotEqual("gemini", s.Translator);
        var dir = Path.Combine(Path.GetTempPath(), "gs-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Translator\":\"auto\"}");
            Assert.False(new SettingsStore(dir).Load().GeminiConsent);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
