using OyunAltyazi.Core;
using OyunAltyazi.Translation;
using Xunit.Abstractions;

namespace OyunAltyazi.Translation.Tests;

public class JoinedSentenceE2ETests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_ct2_joined_sentence_reads_as_one()
    {
        var script = Ct2Paths.FindServerScript();
        if (script is null || !Ct2Paths.IsInstalled(script)) return; // CT2 yok: atlandı
        using var t = new Ct2Translator(Ct2Paths.ProcessLauncher(Ct2Paths.VenvPython, script, Ct2Paths.ModelDir));
        var ctx = new TranslationContext("g", [], null);
        var j = new SentenceJoiner();
        var a = new SubtitleText(null, "We have to get to the extraction point");
        var b = new SubtitleText(null, "before the enemy reinforcements arrive.");
        j.Feed(a, 0);
        var join = j.Feed(b, 1500);
        Assert.NotNull(join.Joined);
        var fa = await t.TranslateAsync(a.Text, ctx, default);
        var fb = await t.TranslateAsync(b.Text, ctx, default);
        var full = await t.TranslateAsync(join.Joined!.Text, ctx, default);
        output.WriteLine($"FRAG1: {fa}\nFRAG2: {fb}\nJOINED: {full}");
        Assert.False(string.IsNullOrWhiteSpace(full));
    }

    [Theory]
    [InlineData("We have to get to the extraction point", "before the enemy reinforcements arrive.")]
    [InlineData("If we don't stop them now,", "thousands of people are going to die.")]
    public async Task Real_ct2_continuation_reads_as_one_sentence(string f1, string f2)
    {
        var script = Ct2Paths.FindServerScript();
        if (script is null || !Ct2Paths.IsInstalled(script)) return;
        using var t = new Ct2Translator(Ct2Paths.ProcessLauncher(Ct2Paths.VenvPython, script, Ct2Paths.ModelDir), TimeSpan.FromSeconds(10));
        var ctx = new TranslationContext("g", [], null);
        await t.TranslateAsync("Warm.", ctx, default);
        var tr1 = await t.TranslateAsync(f1, ctx, default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cont = await t.TranslateContinuationAsync(f1 + " " + f2, tr1, ctx, default);
        sw.Stop();
        output.WriteLine($"F1: {tr1}\nCONT: {cont}\nCONCAT: {tr1} {cont}\nLATENCY_MS: {sw.ElapsedMilliseconds}");
        Assert.True(cont.Length >= 2);
        Assert.DoesNotContain(Ct2Translator.TrimFinalPunctuation(tr1), cont);
    }
}
