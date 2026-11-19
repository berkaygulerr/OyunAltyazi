using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class SentenceJoinerTests
{
    private static SubtitleText L(string t, string? s = null) => new(s, t);

    [Fact]
    public void Joins_open_fragment_with_next()
    {
        var j = new SentenceJoiner();
        Assert.Null(j.Feed(L("We have to get to the extraction point"), 0).Joined);
        var r = j.Feed(L("before the enemy reinforcements arrive."), 2000);
        Assert.Equal("We have to get to the extraction point before the enemy reinforcements arrive.", r.Joined!.Text);
        Assert.Equal(2, r.Fragments);
    }

    [Fact]
    public void Does_not_join_after_sentence_end()
    {
        var j = new SentenceJoiner();
        j.Feed(L("Move out."), 0);
        Assert.Null(j.Feed(L("before they come"), 1000).Joined);
        j = new SentenceJoiner();
        j.Feed(L("Did you hear that?\""), 0);
        Assert.Null(j.Feed(L("Contact front"), 1000).Joined);
    }

    [Fact]
    public void Does_not_join_different_speakers_but_joins_missing_speaker()
    {
        var j = new SentenceJoiner();
        j.Feed(L("We need to go", "Price"), 0);
        Assert.Null(j.Feed(L("now before it is", "Ghost"), 500).Joined);
        j = new SentenceJoiner();
        j.Feed(L("We need to go", "Price"), 0);
        var r = j.Feed(L("now, soldier."), 500);
        Assert.Equal("Price", r.Joined!.Speaker);
    }

    [Fact]
    public void Window_expires()
    {
        var j = new SentenceJoiner(windowMs: 4000);
        j.Feed(L("We need to go"), 0);
        Assert.Null(j.Feed(L("now before it is too late."), 4500).Joined);
    }

    [Fact]
    public void Max_three_fragments_and_chars()
    {
        var j = new SentenceJoiner();
        j.Feed(L("one two"), 0);
        Assert.Equal(2, j.Feed(L("three four"), 100).Fragments);
        Assert.Equal(3, j.Feed(L("five six"), 200).Fragments);
        var r = j.Feed(L("seven eight"), 300);
        Assert.Null(r.Joined);
        Assert.Equal(1, r.Fragments);

        var k = new SentenceJoiner(maxChars: 20);
        k.Feed(L("aaaaaaaaaaaaaaaa"), 0);
        Assert.Null(k.Feed(L("bbbbbbbbbbbb"), 10).Joined);
    }

    [Fact]
    public void Comma_dash_ellipsis_signals()
    {
        var j = new SentenceJoiner();
        j.Feed(L("Listen, I know that,"), 0);
        Assert.NotNull(j.Feed(L("Whatever you say."), 100).Joined); // comma: join even before an uppercase word
        j = new SentenceJoiner();
        j.Feed(L("Well..."), 0);
        Assert.Null(j.Feed(L("Fine. Go."), 100).Joined);
        j = new SentenceJoiner();
        j.Feed(L("Well..."), 0);
        Assert.NotNull(j.Feed(L("fine, go."), 100).Joined);
    }
}
