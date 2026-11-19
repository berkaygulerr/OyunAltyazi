using OyunAltyazi.Translation;
namespace OyunAltyazi.Translation.Tests;

public class SqliteTranslationCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oa-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "cache.db");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] public void Put_then_get_same_game()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google");
        Assert.True(c.TryGet("mw", "Frag out!", out var t)); Assert.Equal("Bomba!", t);
    }
    [Fact] public void Canon_equivalent_text_hits()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google");
        Assert.True(c.TryGet("mw", "FRAG OUT", out _));
    }
    [Fact] public void Other_game_misses()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google");
        Assert.False(c.TryGet("other", "Frag out!", out _));
    }
    [Fact] public void Persists_across_instances()
    {
        using (var c = new SqliteTranslationCache(Db)) c.Put("mw", "Frag out!", "Bomba!", "google");
        using var c2 = new SqliteTranslationCache(Db);
        Assert.True(c2.TryGet("mw", "Frag out!", out var t)); Assert.Equal("Bomba!", t);
    }
    [Fact] public void Put_overwrites()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google");
        c.Put("mw", "Frag out!", "Bomba geliyor!", "google");
        Assert.True(c.TryGet("mw", "Frag out!", out var t)); Assert.Equal("Bomba geliyor!", t);
    }
}
