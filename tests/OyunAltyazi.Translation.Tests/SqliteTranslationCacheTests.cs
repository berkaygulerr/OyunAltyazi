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
    [Fact] public void Count_and_clear()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google"); c.Put("mw", "Go go go", "Gidin!", "google");
        Assert.Equal(2, c.Count());
        c.Clear();
        Assert.Equal(0, c.Count());
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
    [Fact] public void Reviewed_pack_excludes_other_games_and_unapproved_drafts()
    {
        using (var cache = new SqliteTranslationCache(Db))
        {
            cache.Put("game.exe", "Wait at the gate.", "Taslak", "ct2");
            cache.Put("game.exe\u0001gemini", "Wait at the gate.", "Kapıda bekle.", "gemini");
            cache.Put("game.exe", "Previous\u0001continuation", "Parça", "ct2");
            cache.Put("other.exe", "Wrong game.", "Başka oyun.", "ct2");
            Assert.Single(cache.ReviewRows("GAME.EXE"));
            Assert.Null(cache.ExportReviewedPack("game.exe"));
            cache.SaveReviewed("game.exe", "Wait at the gate.", "Geçitte bekle.");
            cache.Put("game.exe", "Wait at the gate.", "Yeni taslak", "ct2");
        }
        using var reopened = new SqliteTranslationCache(Db);
        var row = Assert.Single(reopened.ReviewRows("game.exe"));
        Assert.Equal("Geçitte bekle.", row.Approved);
        var result = OyunAltyazi.Core.GamePack.Parse(reopened.ExportReviewedPack("game.exe")!, "game.exe");
        Assert.True(result.Pack!.TryGet("Wait at the gate.", out var tr));
        Assert.Equal("Geçitte bekle.", tr);
        Assert.Null(reopened.ExportReviewedPack("other.exe"));
        reopened.Clear();
        Assert.Single(reopened.ReviewRows("game.exe"));
        reopened.RemoveReviewed("game.exe", "Wait at the gate.");
        Assert.Null(reopened.ExportReviewedPack("game.exe"));
    }

    [Fact] public void Put_overwrites()
    {
        using var c = new SqliteTranslationCache(Db);
        c.Put("mw", "Frag out!", "Bomba!", "google");
        c.Put("mw", "Frag out!", "Bomba geliyor!", "google");
        Assert.True(c.TryGet("mw", "Frag out!", out var t)); Assert.Equal("Bomba geliyor!", t);
    }
}
