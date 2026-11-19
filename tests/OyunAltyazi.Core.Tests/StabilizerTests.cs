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

    [Fact] public void Clears_after_four_empty_reads()
    {
        var s = new Stabilizer();
        s.Feed(T("Hold the line")); s.Feed(T("Hold the line"));
        Assert.Null(s.Feed(null)); Assert.Null(s.Feed(null)); Assert.Null(s.Feed(null));
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
}
