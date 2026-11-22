using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TranslationChainLocalTests
{
    private sealed class MemCache : ITranslationCache
    {
        public Dictionary<string, string> D = new(); public int Gets;
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) { Gets++; return D.TryGetValue(g + "|" + TextKey.Canon(s), out t); }
        public void Put(string g, string s, string t, string p) => D[g + "|" + TextKey.Canon(s)] = t;
    }
    private sealed class Fake : ITranslator
    {
        public int Calls; public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) { Calls++; return Task.FromResult("model:" + text); }
    }
    private static GamePack P(string game, string en, string tr) =>
        GamePack.Parse("{\"version\":2,\"game\":\"" + game + "\",\"entries\":[{\"en\":\"" + en + "\",\"tr\":\"" + tr + "\"}]}", game).Pack!;
    private static readonly SubtitleText Line = new("Price", "Move up, now.");
    private static readonly TranslationContext Ctx = new("mw4.exe", [], null);

    [Fact] public async Task Precedence_glossary_pack_cache_model()
    {
        var local = new LocalTranslations(); var cache = new MemCache(); var model = new Fake();
        var chain = new TranslationChain(cache, model, local);
        cache.Put("mw4.exe", "Move up, now.", "cache", "x");
        local.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack"));
        local.SetGlossary("mw4.exe", P("mw4.exe", "Move up now", "glossary"));
        var r = await chain.TranslateAsync(Line, Ctx, default);
        Assert.Equal(("glossary", TranslationSource.Glossary), (r.Text, r.Source));
        local.Clear("mw4.exe", glossary: true);
        r = await chain.TranslateAsync(Line, Ctx, default);
        Assert.Equal(("pack", TranslationSource.Pack), (r.Text, r.Source));
        local.Clear("mw4.exe", glossary: false);
        r = await chain.TranslateAsync(Line, Ctx, default);
        Assert.Equal(("cache", TranslationSource.Cache), (r.Text, r.Source));
        Assert.Equal(0, model.Calls);
    }

    [Fact] public async Task Local_hit_skips_cache_and_model_and_writes_nothing()
    {
        var local = new LocalTranslations(); var cache = new MemCache(); var model = new Fake();
        local.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack"));
        await new TranslationChain(cache, model, local).TranslateAsync(Line, Ctx, default);
        Assert.Equal(0, cache.Gets); Assert.Equal(0, model.Calls); Assert.Empty(cache.D);
    }

    [Fact] public async Task Pack_of_another_game_never_applies_and_model_is_used()
    {
        var local = new LocalTranslations(); var model = new Fake();
        local.SetPack("mw2019.exe", P("mw2019.exe", "Move up now", "pack"));
        var r = await new TranslationChain(new MemCache(), model, local).TranslateAsync(Line, Ctx, default);
        Assert.Equal(TranslationSource.Translator, r.Source); Assert.Equal(1, model.Calls);
    }

    [Fact] public async Task No_exact_match_falls_through_unchanged()
    {
        var local = new LocalTranslations(); var model = new Fake();
        local.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack"));
        var r = await new TranslationChain(new MemCache(), model, local).TranslateAsync(new SubtitleText(null, "Move up now please."), Ctx, default);
        Assert.Equal(TranslationSource.Translator, r.Source);
    }

    [Fact] public async Task Continuation_from_pack_returns_only_the_remainder_never_the_prefix()
    {
        var local = new LocalTranslations(); var model = new Fake();
        local.SetPack("mw4.exe", P("mw4.exe", "Move up now and cover me", "Yukarı çık ve beni koru"));
        var chain = new TranslationChain(new MemCache(), model, local);
        var r = await chain.TranslateContinuationAsync(new SubtitleText(null, "and cover me"), new SubtitleText(null, "Move up, now and cover me"), "Yukarı çık", Ctx, default);
        Assert.Equal("ve beni koru", r.Text); Assert.Equal(TranslationSource.Pack, r.Source); Assert.Equal(0, model.Calls);
    }

    [Fact] public async Task Continuation_with_unusable_pack_text_falls_back_to_model()
    {
        var local = new LocalTranslations(); var model = new Fake();
        local.SetPack("mw4.exe", P("mw4.exe", "Move up now and cover me", "Tamamen farklı cümle"));   // does not start with the shown prefix
        var chain = new TranslationChain(new MemCache(), model, local);
        var r = await chain.TranslateContinuationAsync(new SubtitleText(null, "and cover me"), new SubtitleText(null, "Move up now and cover me"), "Yukarı çık", Ctx, default);
        Assert.Equal(TranslationSource.Translator, r.Source);
        Assert.NotEqual("Yukarı çık", r.Text);
    }
}

public class SettingsLocalPackTests
{
    [Fact] public void Pack_bindings_round_trip_and_old_settings_still_load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "st-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(dir);
            store.Save(new AppSettings(GamePacks: new() { ["mw4.exe"] = @"C:\p\a.json" }, Glossaries: new() { ["mw4.exe"] = @"C:\p\g.json" }));
            var s = store.Load();
            Assert.Equal(@"C:\p\a.json", s.GamePacks!["mw4.exe"]); Assert.Equal(@"C:\p\g.json", s.Glossaries!["mw4.exe"]);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Translator\":\"google\"}");
            Assert.Null(store.Load().GamePacks);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
