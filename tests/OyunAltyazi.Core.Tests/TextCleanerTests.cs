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
