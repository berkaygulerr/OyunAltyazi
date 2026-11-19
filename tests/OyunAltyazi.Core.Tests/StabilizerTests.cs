using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class StabilizerTests
{
    private static SubtitleText T(string s) => new(null, s);
    private static string Shown(StabilizerEvent? e) => ((ShowLine)e!).Line.Text;

    [Fact] public void Shows_after_two_equal_reads()
    {
        var s = new Stabilizer();
        Assert.Null(s.Feed(T("Move up now")));
        Assert.Equal("Move up now", Shown(s.Feed(T("Move up now"))));
    }

    [Fact] public void Typewriter_waits_for_full_line()
    {
        var s = new Stabilizer();
        Assert.Null(s.Feed(T("We nee")));
        Assert.Null(s.Feed(T("We need to")));
        Assert.Null(s.Feed(T("We need to go now")));
        Assert.Equal("We need to go now", Shown(s.Feed(T("We need to go now"))));
    }

    [Fact] public void Ocr_jitter_does_not_reshow()
    {
        var s = new Stabilizer();
        s.Feed(T("Hold the line")); s.Feed(T("Hold the line"));
        Assert.Null(s.Feed(T("Hold the 1ine")));
        Assert.Null(s.Feed(T("Hold the 1ine")));
    }

    [Fact] public void Clears_after_six_empty_reads()
    {
        var s = new Stabilizer();
        s.Feed(T("Hold the line")); s.Feed(T("Hold the line"));
        for (int i = 0; i < 5; i++) Assert.Null(s.Feed(null));
        Assert.IsType<ClearLine>(s.Feed(null));
    }

    [Fact] public void Single_empty_read_does_not_clear()
    {
        var s = new Stabilizer();
        s.Feed(T("Hold the line")); s.Feed(T("Hold the line"));
        Assert.Null(s.Feed(null));
        Assert.Null(s.Feed(T("Hold the line")));
    }

    [Fact] public void New_line_replaces_old()
    {
        var s = new Stabilizer();
        s.Feed(T("Hold the line")); s.Feed(T("Hold the line"));
        Assert.Null(s.Feed(T("Get to the chopper")));
        Assert.Equal("Get to the chopper", Shown(s.Feed(T("Get to the chopper"))));
    }

    [Fact] public void Complete_sentence_shows_on_first_read()
    {
        var s = new Stabilizer();
        Assert.Equal("Alex: Then help us track it, before they...", Shown(s.Feed(new SubtitleText(null, "Alex: Then help us track it, before they..."))));
    }

    [Fact] public void Garbled_read_needs_more_agreement()
    {
        var s = new Stabilizer();
        Assert.Null(s.Feed(T("Commander was shooting at the tAnk")));
        Assert.Null(s.Feed(T("Commander was shooting at the tAnk")));
        Assert.Null(s.Feed(T("Commander was shooting at the tAnk")));
        Assert.NotNull(s.Feed(T("Commander was shooting at the tAnk")));
    }

    [Fact] public void Clean_reread_upgrades_garbled_line()
    {
        var s = new Stabilizer();
        for (int i = 0; i < 4; i++) s.Feed(T("ussian is still h%re"));
        Assert.Equal("...and the Russian army is still here.", Shown(s.Feed(T("...and the Russian army is still here."))));
    }

    [Fact] public void Scrolled_out_line_is_not_reshown()
    {
        var s = new Stabilizer();
        s.Feed(T("We would never use these poisons. Then help us track it, before they..."));
        Assert.Null(s.Feed(T("Then help us track it, before they...")));
        Assert.Null(s.Feed(T("Then help us track it, before they...")));
    }

    [Fact] public void Similar_length_lines_are_distinct()
    {
        var s = new Stabilizer();
        s.Feed(T("We live like this every day.")); 
        Assert.Equal("We move out in one hour.", Shown(s.Feed(T("We move out in one hour."))));
    }

    [Fact] public void Jittery_candidate_still_confirms()
    {
        var s = new Stabilizer();
        Assert.Null(s.Feed(T("Hold the line men")));
        Assert.Equal("Hold the Iine men", Shown(s.Feed(T("Hold the Iine men"))));
    }
}
