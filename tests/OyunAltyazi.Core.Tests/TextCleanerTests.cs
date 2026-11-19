using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class TextCleanerTests
{
    private static SubtitleText? Clean(params string[] lines) =>
        TextCleaner.Clean(new OcrResult(lines.Select((t, i) => new OcrLine(t, i * 0.2, 0.15)).ToList()));

    [Fact] public void Splits_speaker() => Assert.Equal(new SubtitleText("Price", "Move up, now."), Clean("Price: Move up, now."));
    [Fact] public void Joins_two_lines() => Assert.Equal(new SubtitleText(null, "We need to go now before they come back."), Clean("We need to go now", "before they come back."));
    [Theory][InlineData("[F] Interact")][InlineData("LB")][InlineData("x")][InlineData("|| -- ..")]
    public void Drops_hud_noise(string line) => Assert.Null(Clean(line));
    [Fact] public void Fixes_confusions() => Assert.Equal("Hold the line", Clean("Ho1d the 1ine")!.Text);
    [Fact] public void Drops_noise_line_but_keeps_dialogue() => Assert.Equal("Get to the chopper.", Clean("[E] Interact", "Get to the chopper.")!.Text);
}

public class SpeakerNameTests
{
    private static OcrResult R(params OcrLine[] l) => new(l);

    [Theory]
    [InlineData("Valeria: We move now.", "Valeria")]
    [InlineData("Valeria:We move now.", "Valeria")]
    [InlineData("Kyle Garrick: We move now.", "Kyle Garrick")]
    [InlineData("Ghost: We move now.", "Ghost")]
    public void Whole_name_is_separated(string raw, string name)
    {
        var c = TextCleaner.Clean(R(new OcrLine(raw, 0.2, 0.1)))!;
        Assert.Equal(name, c.Speaker);
        Assert.Equal("We move now.", c.Text);
    }

    [Fact]
    public void Name_and_dialogue_as_separate_ocr_lines_keep_left_to_right_order()
    {
        // dialogue box reported slightly higher than the bold name box
        var c = TextCleaner.Clean(R(new OcrLine("We need to move now.", 0.200, 0.06, 0.25, 0.5), new OcrLine("Valeria:", 0.210, 0.06, 0.05, 0.18)))!;
        Assert.Equal("Valeria", c.Speaker);
        Assert.Equal("We need to move now.", c.Text);
    }

    [Fact]
    public void Truncated_name_is_never_shown_partially()
    {
        var c = TextCleaner.Clean(R(new OcrLine("ria: We need to move now.", 0.2, 0.1)))!;
        Assert.Null(c.Speaker);
        Assert.Equal("We need to move now.", c.Text);
    }

    [Fact]
    public void Truncated_name_is_repaired_from_known_speakers()
    {
        var c = TextCleaner.Clean(R(new OcrLine("ria: We need to move now.", 0.2, 0.1)), out _, out _, ["Valeria", "Price"])!;
        Assert.Equal("Valeria", c.Speaker);
    }
}
