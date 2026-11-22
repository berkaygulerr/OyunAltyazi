using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class OcrWordCorrectorTests
{
    [Theory]
    [InlineData("The helicopler is waiting.", "The helicopter is waiting.")]
    [InlineData("Good mornlng, soldier.", "Good morning, soldier.")]
    [InlineData("The rnission is clear.", "The mission is clear.")]
    [InlineData("Check your weapcn, \"soldier\"!", "Check your weapon, \"soldier\"!")]
    [InlineData("We need more ammunitlon.", "We need more ammunition.")]
    public void Corrects_unknown_word_with_one_clear_candidate(string input, string expected) =>
        Assert.Equal(expected, OcrWordCorrector.Correct(input));

    [Theory]
    [InlineData("We need to mave now.")]      // 4 letters: too short to guess
    [InlineData("We need the clane now.")]    // clone / crane / plane / clang ... : no single winner
    [InlineData("Watch the plare ahead.")]    // place / plane / plate / flare / glare ...
    public void Ambiguous_word_is_left_alone(string input)
    {
        Assert.False(EnglishWords.IsWord(input.Split(' ')[^2]), "test word must be unknown to the list");
        Assert.Equal(input, OcrWordCorrector.Correct(input));
    }

    [Theory]
    [InlineData("Karim and Zakhaev are waiting at the airfield.")]
    [InlineData("Tell Soap to meet Valeria at the NATO base.")]
    [InlineData("Check the AK47 and the M4A1 at Kowalski's place.")]
    [InlineData("Ghost, I'm gonna need the thing y'all found.")]
    [InlineData("Hold the line, soldier. We can't let them through.")]
    [InlineData("Yeah, I dunno. Gotta go, kinda busy right now.")]
    public void Valid_words_names_acronyms_and_slang_are_unchanged(string input) =>
        Assert.Equal(input, OcrWordCorrector.Correct(input));

    [Theory]
    [InlineData("We need the eould poison now.")]                     // e->w / e->c: two real candidates, no look-alike guessing
    [InlineData("I make a q distinctlon here.")]     // stray single letter proves a damaged read
    [InlineData("The helicopler is wa$ting for us.")]                  // junk symbol anywhere: trust nothing in the line
    [InlineData("The helicopler is mornlng in the hangar.")]           // two unreadable words in a short line
    [InlineData("IiVe like thlS every day, helicopler.")]              // odd inner capitals = damaged read
    public void Damaged_or_doubtful_lines_are_left_alone(string input) =>
        Assert.Equal(input, OcrWordCorrector.Correct(input));

    [Fact] public void Capitalised_unknown_word_is_treated_as_a_name() =>
        Assert.Equal("Helicopler Zulu is ready.", OcrWordCorrector.Correct("Helicopler Zulu is ready."));

    [Fact] public void Short_unknown_words_are_not_guessed() =>
        Assert.Equal("Wc go, hx!", OcrWordCorrector.Correct("Wc go, hx!"));

    [Fact] public void Mostly_unreadable_line_is_not_rewritten() =>
        Assert.Equal("xqzv wlorp frabnic helicopler", OcrWordCorrector.Correct("xqzv wlorp frabnic helicopler"));

    [Fact] public void Correction_is_idempotent_and_keeps_empty_input()
    {
        var once = OcrWordCorrector.Correct("The helicopler is waiting, mornlng.");
        Assert.Equal(once, OcrWordCorrector.Correct(once));
        Assert.Equal("", OcrWordCorrector.Correct(""));
    }

    [Fact] public void Correction_never_lowers_quality()
    {
        foreach (var s in new[] { "The helicopler is waiting.", "Good mornlng, soldier.", "We need to mave now." })
            Assert.True(TextQuality.Score(OcrWordCorrector.Correct(s)) >= TextQuality.Score(s));
    }

    [Fact] public void Cleaner_corrects_text_but_not_speaker_label_or_names()
    {
        var c = TextCleaner.Clean(new OcrResult([new OcrLine("Valeria: The helicopler is waiting, Karim.", 0.2, 0.1)]))!;
        Assert.Equal("Valeria", c.Speaker);
        Assert.Equal("The helicopter is waiting, Karim.", c.Text);
    }

    [Fact] public void Cleaner_scene_keeps_multiple_speakers_and_continuations()
    {
        var scene = TextCleaner.CleanScene(new OcrResult([
            new OcrLine("Valeria: The helicopler is waiting", 0.2, 0.1),
            new OcrLine("near the airfield.", 0.3, 0.1),
            new OcrLine("Karim: Understood.", 0.4, 0.1)]), out _, out _);
        Assert.Equal(2, scene.Count);
        Assert.Equal(new SubtitleText("Valeria", "The helicopter is waiting near the airfield."), scene[0]);
        Assert.Equal("Karim", scene[1].Speaker);
    }
}
