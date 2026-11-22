using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class GamePackTests
{
    private const string Good = """
        {"version":2,"game":"mw4.exe","title":"my notes","entries":[
          {"en":"Move up, now.","tr":"Yukarı çıkın."},
          {"key":"5ad7d73f3757fa41","tr":"Beni koru."}]}
        """;

    [Fact] public void Parses_valid_pack_and_matches_exactly_after_canonicalisation()
    {
        var r = GamePack.Parse(Good, "MW4.exe");
        Assert.Null(r.Error); Assert.NotNull(r.Pack); Assert.Equal(2, r.Pack!.Count);
        Assert.True(r.Pack.TryGet("move up now", out var t)); Assert.Equal("Yukarı çıkın.", t);
        Assert.True(r.Pack.TryGet("Cover me.", out t)); Assert.Equal("Beni koru.", t);   // by PackKey
        Assert.False(r.Pack.TryGet("Move up now, fast.", out _));                          // no fuzzy matching
    }

    [Fact] public void Pack_for_another_game_is_refused()
    {
        var r = GamePack.Parse(Good, "mw2019.exe");
        Assert.Null(r.Pack); Assert.Contains("mw4.exe", r.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"version":1,"game":"mw4.exe","entries":[{"en":"a b","tr":"x"}]}""")]
    [InlineData("""{"version":2,"entries":[{"en":"a b","tr":"x"}]}""")]
    [InlineData("""{"version":2,"game":"mw4.exe","entries":[{"en":"a b"}]}""")]
    [InlineData("""{"version":2,"game":"mw4.exe","entries":[{"en":"a b","tr":"  "}]}""")]
    [InlineData("""{"version":2,"game":"mw4.exe","entries":[{"key":"zz","tr":"x"}]}""")]
    [InlineData("""{"version":2,"game":"mw4.exe","entries":[{"en":"Go go","tr":"x"},{"en":"go go!","tr":"y"}]}""")]
    [InlineData("""{"version":2,"game":"mw4.exe","entries":[{"en":"a b","tr":"x"}""")]
    public void Malformed_pack_is_rejected_whole_without_throwing(string json)
    {
        var r = GamePack.Parse(json, "mw4.exe");
        Assert.Null(r.Pack); Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact] public void Same_translation_duplicate_is_fine() =>
        Assert.NotNull(GamePack.Parse("""{"version":2,"game":"mw4.exe","entries":[{"en":"Go go","tr":"x"},{"en":"go go!","tr":"x"}]}""", "mw4.exe").Pack);

    [Fact] public void Missing_or_oversized_file_is_an_error_not_an_exception()
    {
        Assert.NotNull(GamePack.LoadFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"), "mw4.exe").Error);
        var big = Path.Combine(Path.GetTempPath(), "big-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllBytes(big, new byte[GamePack.MaxFileBytes + 1]);
            var r = GamePack.LoadFile(big, "mw4.exe");
            Assert.Null(r.Pack); Assert.NotNull(r.Error);
        }
        finally { File.Delete(big); }
    }

    [Fact] public void Loads_from_file()
    {
        var p = Path.Combine(Path.GetTempPath(), "pack-" + Guid.NewGuid() + ".json");
        try { File.WriteAllText(p, Good); Assert.NotNull(GamePack.LoadFile(p, "mw4.exe").Pack); }
        finally { File.Delete(p); }
    }
}

public class LocalTranslationsTests
{
    private static GamePack P(string game, string en, string tr) =>
        GamePack.Parse("{\"version\":2,\"game\":\"" + game + "\",\"entries\":[{\"en\":\"" + en + "\",\"tr\":\"" + tr + "\"}]}", game).Pack!;

    [Fact] public void Pack_applies_only_to_its_own_game()
    {
        var l = new LocalTranslations();
        Assert.True(l.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack")));
        Assert.True(l.TryGet("mw4.exe", "Move up, now.", out var t, out var s)); Assert.Equal("pack", t); Assert.Equal(TranslationSource.Pack, s);
        Assert.False(l.TryGet("mw2019.exe", "Move up, now.", out _, out _));
        Assert.False(l.TryGet("other.exe", "Move up, now.", out _, out _));
    }

    [Fact] public void Setting_a_pack_for_a_different_game_is_refused()
    {
        var l = new LocalTranslations();
        Assert.False(l.SetPack("mw2019.exe", P("mw4.exe", "Move up now", "pack")));
        Assert.False(l.TryGet("mw2019.exe", "Move up now", out _, out _));
    }

    [Fact] public void Glossary_beats_pack()
    {
        var l = new LocalTranslations();
        l.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack"));
        l.SetGlossary("mw4.exe", P("mw4.exe", "Move up now", "glossary"));
        Assert.True(l.TryGet("MW4.EXE", "move up now", out var t, out var s)); Assert.Equal("glossary", t); Assert.Equal(TranslationSource.Glossary, s);
    }

    [Fact] public void Clear_removes_game_entries()
    {
        var l = new LocalTranslations();
        l.SetPack("mw4.exe", P("mw4.exe", "Move up now", "pack"));
        l.Clear("mw4.exe", glossary: false);
        Assert.False(l.TryGet("mw4.exe", "Move up now", out _, out _));
    }
}
