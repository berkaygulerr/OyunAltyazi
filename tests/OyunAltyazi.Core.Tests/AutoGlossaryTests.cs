using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class AutoGlossaryTests
{
    private sealed class MemCache : ITranslationCache
    {
        public Dictionary<string, string> D = new();
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) => D.TryGetValue(g + "|" + TextKey.Canon(s), out t);
        public void Put(string g, string s, string t, string p) => D[g + "|" + TextKey.Canon(s)] = t;
    }
    private sealed class Fake : ITranslator
    {
        public int Calls; public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) { Calls++; return Task.FromResult("model:" + text); }
    }
    private static GamePack P(string game, string en, string tr) => GamePack.FromEntries(game, "t", [new(en, tr)]);
    private static TranslationContext Ctx(string exe) => new(exe, [], null);
    private const string Known = "modernwarfare.exe";
    private static KeyValuePair<string, string> First => AutoGlossaryCatalog.Default.Find(Known)!.Entries.First();
    private static string KnownPhrase => First.Key;
    private static string KnownTr => First.Value;
    private static AutoGlossaryCoordinator Coord(LocalTranslations l, Func<bool>? on = null) => new(l, AutoGlossaryCatalog.Default, on ?? (() => true));

    [Fact] public void Catalog_is_valid_small_and_short_expressions_only()
    {
        Assert.NotEmpty(AutoGlossaryCatalog.Default.Presets);
        foreach (var p in AutoGlossaryCatalog.Default.Presets)
        {
            Assert.InRange(p.Entries.Count, 5, 200);
            Assert.All(p.Entries, e => Assert.True(e.Key.Split(' ').Length <= 6, "short expressions only: " + e.Key));
            GamePack.FromEntries("x.exe", p.Title, p.Entries);
        }
    }

    [Theory] [InlineData("modernwarfare.exe")] [InlineData("ModernWarfare.EXE")] [InlineData(" eldenring.exe ")] [InlineData("rdr2.exe")]
    public void Known_exe_gets_a_preset(string exe) => Assert.NotNull(AutoGlossaryCatalog.Default.Find(exe));

    [Theory] [InlineData("cod.exe")] [InlineData("chrome.exe")] [InlineData("")] [InlineData(null)] [InlineData("eldenring")] [InlineData("Elden Ring")] [InlineData("gta5_enhanced.exe")]
    public void Unknown_or_ambiguous_does_nothing(string? exe)
    {
        Assert.Null(AutoGlossaryCatalog.Default.Find(exe));
        var local = new LocalTranslations();
        var c = Coord(local);
        c.Select(exe);
        Assert.Equal(AutoGlossaryState.NoMatch, c.State);
        Assert.False(local.TryGet(exe ?? "x.exe", KnownPhrase, out _, out _));
    }

    [Fact] public void Genre_data_is_shared_only_through_explicit_mappings()
    {
        var cat = AutoGlossaryCatalog.Default;
        Assert.Same(cat.Find("modernwarfare.exe"), cat.Find("ModernWarfare.exe"));
        var local = new LocalTranslations();
        Coord(local).Select(Known);
        Assert.False(local.TryGet("unlisted.exe", KnownPhrase, out _, out _));
    }

    [Fact] public void Selecting_installs_for_that_game_only_and_switching_clears()
    {
        var local = new LocalTranslations();
        var c = Coord(local);
        c.Select(Known);
        Assert.Equal(AutoGlossaryState.Ready, c.State);
        Assert.True(local.TryGet(Known, KnownPhrase, out var t, out var src));
        Assert.Equal((KnownTr, TranslationSource.AutoGlossary), (t, src));
        Assert.False(local.TryGet("chrome.exe", KnownPhrase, out _, out _));
        c.Select("chrome.exe");
        Assert.False(local.TryGet(Known, KnownPhrase, out _, out _));
    }

    [Fact] public async Task Works_offline_with_model_never_called()
    {
        var local = new LocalTranslations(); var model = new Fake();
        Coord(local).Select(Known);
        var r = await new TranslationChain(new MemCache(), model, local).TranslateAsync(new(null, KnownPhrase), Ctx(Known), default);
        Assert.Equal((KnownTr, TranslationSource.AutoGlossary), (r.Text, r.Source));
        Assert.Equal(0, model.Calls);
    }

    [Fact] public async Task Effective_precedence_manual_glossary_pack_auto_glossary_cache_model()
    {
        var local = new LocalTranslations(); var cache = new MemCache(); var model = new Fake();
        var chain = new TranslationChain(cache, model, local);
        var line = new SubtitleText(null, KnownPhrase);
        cache.Put(Known, KnownPhrase, "cache", "x");
        async Task<(string, TranslationSource)> Run() { var r = await chain.TranslateAsync(line, Ctx(Known), default); return (r.Text, r.Source); }
        Assert.Equal(("cache", TranslationSource.Cache), await Run());
        Coord(local).Select(Known);
        Assert.Equal((KnownTr, TranslationSource.AutoGlossary), await Run());
        local.SetPack(Known, P(Known, KnownPhrase, "pack"));
        Assert.Equal(("pack", TranslationSource.Pack), await Run());
        local.SetGlossary(Known, P(Known, KnownPhrase, "manual"));
        Assert.Equal(("manual", TranslationSource.Glossary), await Run());
    }

    [Fact] public void Removing_manual_glossary_or_pack_restores_auto()
    {
        var local = new LocalTranslations();
        Coord(local).Select(Known);
        local.SetGlossary(Known, P(Known, KnownPhrase, "manual"));
        local.SetPack(Known, P(Known, KnownPhrase, "pack"));
        local.TryGet(Known, KnownPhrase, out var a, out _); Assert.Equal("manual", a);
        local.Clear(Known, glossary: true);
        local.TryGet(Known, KnownPhrase, out var b, out _); Assert.Equal("pack", b);
        local.Clear(Known, glossary: false);
        Assert.True(local.TryGet(Known, KnownPhrase, out var c, out var s));
        Assert.Equal((KnownTr, TranslationSource.AutoGlossary), (c, s));
    }

    [Fact] public void Toggle_off_removes_on_restores_independent_of_auto_pack()
    {
        var local = new LocalTranslations(); bool on = true;
        var c = Coord(local, () => on);
        c.Select(Known); Assert.Equal(AutoGlossaryState.Ready, c.State);
        on = false; c.Select(Known);
        Assert.Equal(AutoGlossaryState.Off, c.State);
        Assert.False(local.TryGet(Known, KnownPhrase, out _, out _));
        on = true; c.Select(Known);
        Assert.Equal(AutoGlossaryState.Ready, c.State);
        Assert.True(local.TryGet(Known, KnownPhrase, out _, out _));
        Assert.True(new AppSettings().AutoGlossary);
        var off = new AppSettings() with { AutoGlossary = false };
        Assert.False(off.AutoGlossary); Assert.True(off.AutoGamePack);
    }

    [Fact] public void Setting_persists_and_old_settings_default_on()
    {
        var d = Path.Combine(Path.GetTempPath(), "oa-ag-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        var store = new SettingsStore(d);
        Assert.True(store.Load().AutoGlossary);
        store.Save(store.Load() with { AutoGlossary = false });
        Assert.False(new SettingsStore(d).Load().AutoGlossary);
        File.WriteAllText(Path.Combine(d, "settings.json"), "{\"Translator\":\"auto\"}");
        Assert.True(new SettingsStore(d).Load().AutoGlossary);
    }

    [Fact] public async Task Continuation_from_auto_glossary_never_repeats_the_shown_prefix()
    {
        var local = new LocalTranslations(); var model = new Fake();
        Coord(local).Select(Known);
        var chain = new TranslationChain(new MemCache(), model, local);
        var full = AutoGlossaryCatalog.Default.Find(Known)!.Entries.First(e => e.Value.Split(' ').Length >= 2);
        var words = full.Value.Split(' ');
        var prefix = words[0];
        var r = await chain.TranslateContinuationAsync(new(null, "x"), new(null, full.Key), prefix, Ctx(Known), default);
        Assert.Equal(TranslationSource.AutoGlossary, r.Source);
        Assert.Equal(string.Join(' ', words.Skip(1)).TrimStart(' ', ',', ';', ':', '.', '!', '?', '…', '-', '—', '–'), r.Text.TrimEnd().TrimEnd());
        Assert.DoesNotContain(prefix, r.Text.Split(' ').Take(1).Where(w => w == prefix));
    }

    [Fact] public void Coordinator_takes_no_network_or_path_dependency()
    {
        var ps = typeof(AutoGlossaryCoordinator).GetConstructors().Single().GetParameters();
        Assert.DoesNotContain(ps, p => p.ParameterType == typeof(HttpClient) || p.ParameterType == typeof(string));
    }
}
