using System.Net;
using System.Security.Cryptography;
using System.Text;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;
using Xunit.Abstractions;

namespace OyunAltyazi.Translation.Tests;

public class OnnxModelStoreTests
{
    [Fact] public void Pinned_assets_are_complete_and_revision_locked()
    {
        var f = OnnxModelStore.EnglishTurkish;
        Assert.Equal(OnnxMarianEngine.RequiredFiles.OrderBy(x => x), f.Select(x => x.Name).OrderBy(x => x));
        Assert.Matches("^[0-9a-f]{40}$", OnnxModelStore.Revision);
        foreach (var x in f)
        {
            Assert.Matches("^[0-9a-f]{64}$", x.Sha256);
            Assert.True(x.Size > 0);
            Assert.Contains(OnnxModelStore.Revision, x.Url.ToString());   // never "main"
            Assert.Equal("https", x.Url.Scheme);
        }
    }

    private sealed class Fake(Func<Uri, byte[]> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body(r.RequestUri!)) });
    }

    private static (OnnxModelFile File, byte[] Data) Make(string name, string content)
    {
        var d = Encoding.UTF8.GetBytes(content);
        return (new OnnxModelFile(name, new Uri("https://example.invalid/" + name), d.Length, Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant()), d);
    }

    [Fact] public async Task Download_installs_only_verified_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onnxstore-" + Guid.NewGuid());
        try
        {
            var a = Make("a.bin", "alpha"); var b = Make("b.bin", "bravo!");
            var store = new OnnxModelStore(dir, [a.File, b.File], new HttpClient(new Fake(u => u.AbsolutePath.EndsWith("a.bin") ? a.Data : b.Data)));
            Assert.False(store.IsInstalled);
            await store.DownloadAsync(null, default);
            Assert.True(store.IsInstalled);
            Assert.Empty(await store.VerifyAsync());
            File.WriteAllText(store.PathOf("a.bin"), "ALPHA");               // same size, wrong content
            Assert.Equal(["a.bin"], await store.VerifyAsync());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact] public async Task Checksum_mismatch_is_rejected_and_leaves_nothing_installed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onnxstore-" + Guid.NewGuid());
        try
        {
            var a = Make("a.bin", "alpha");
            var store = new OnnxModelStore(dir, [a.File], new HttpClient(new Fake(_ => Encoding.UTF8.GetBytes("ALPHA"))));  // same length, tampered
            await Assert.ThrowsAsync<InvalidDataException>(() => store.DownloadAsync(null, default));
            Assert.False(File.Exists(store.PathOf("a.bin")));
            Assert.False(File.Exists(store.PathOf("a.bin") + ".part"));
            Assert.False(store.IsInstalled);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

/// <summary>
/// Real-model tests. Set OYUNALTYAZI_ONNX_MODEL to a folder holding the pinned files (see OnnxModelStore); without it they return early.
/// Nothing here fakes the model: real SentencePiece/vocab ids, real encoder/decoder, real beam search.
/// </summary>
public class OnnxRealModelTests(ITestOutputHelper output)
{
    private static string? Model()
    {
        var d = Environment.GetEnvironmentVariable("OYUNALTYAZI_ONNX_MODEL");
        return !string.IsNullOrEmpty(d) && OnnxTranslator.IsInstalledIn(d) ? d : null;
    }
    private static readonly TranslationContext Ctx = new("g", [], null);

    [Fact] public async Task Model_files_match_the_pinned_checksums()
    {
        if (Model() is not { } d) return;
        Assert.Empty(await new OnnxModelStore(d).VerifyAsync());
    }

    // ids produced by the reference pipeline (Python sentencepiece + the model's vocab.json, i.e. Hugging Face MarianTokenizer behaviour)
    public static TheoryData<string, int[]> SourceIds => new()
    {
        { "Hello there.", [24170, 50436, 33, 43741] },
        { "We need to move before they find us.", [54337, 35891, 50848, 35166, 7086, 50467, 19851, 52809, 33, 43741] },
        { "Don't just stand there, help me with this door.", [15637, 46, 49045, 28136, 47787, 50436, 14, 24175, 33729, 54714, 50518, 15768, 33, 43741] },
        { "Press and hold [F] to open the map.", [40820, 4138, 24657, 139, 19120, 73, 50848, 37889, 50390, 33360, 33, 43741] },
        { "Café déjà vu — 100% naïve!", [9609, 52508, 13251, 52508, 27796, 52508, 90, 54089, 52508, 361, 35631, 52508, 53450, 21, 43741] },
        { "WATCH OUT!!", [54292, 38453, 23, 43741] },
        { "Öğrenci ğüşiöç İstanbul", [90, 52508, 42650, 10977, 90, 20793, 51926, 43746, 25179, 52508, 90, 25185, 47784, 43741] },
    };

    [Theory, MemberData(nameof(SourceIds))]
    public void Source_tokenizer_matches_reference_ids(string text, int[] expected)
    {
        if (Model() is not { } d) return;
        var t = MarianTokenizer.Load(Path.Combine(d, "source.spm"), Path.Combine(d, "target.spm"), Path.Combine(d, "vocab.json"));
        Assert.Equal(expected, t.EncodeSource(text));
    }

    public static TheoryData<string, int[]> TargetIds => new()
    {
        { "Eğer onları şimdi durdurmazsak", [17014, 37791, 46186, 16178, 56758, 44024] },
        { "Yaşlı adam limanda onunla buluşmamı söyledi", [55709, 2667, 32232, 13254, 37849, 9264, 34314, 47283] },
        { "Çıkarma noktasına ulaşmamız lazım", [11069, 32862, 36338, 52138, 34758, 31608] },
    };

    [Theory, MemberData(nameof(TargetIds))]
    public void Target_prefix_tokenizer_matches_reference_ids(string text, int[] expected)
    {
        if (Model() is not { } d) return;
        var t = MarianTokenizer.Load(Path.Combine(d, "source.spm"), Path.Combine(d, "target.spm"), Path.Combine(d, "vocab.json"));
        Assert.Equal(expected, t.EncodeTargetPrefix(text));
    }

    [Theory]
    [InlineData("Get down, now!", "yat")]
    [InlineData("Reloading!", "")]
    [InlineData("We need to move before they find us.", "hareket")]
    public async Task Translates_to_turkish(string en, string mustContain)
    {
        if (Model() is not { } d) return;
        using var t = new OnnxTranslator(d, beams: 2, intraOpThreads: 4, requestTimeout: TimeSpan.FromSeconds(60));
        var tr = await t.TranslateAsync(en, Ctx, default);
        output.WriteLine($"{en} => {tr}");
        Assert.False(string.IsNullOrWhiteSpace(tr));
        Assert.DoesNotContain(en, tr);
        Assert.Contains(mustContain, tr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("If we don't stop them now,", "thousands of people are going to die.")]
    [InlineData("When the storm finally passed,", "the village was quiet and every road was covered in snow.")]
    [InlineData("The old man told me to meet him at the harbor", "and to bring the letter nobody else was supposed to read.")]
    public async Task Continuation_returns_only_the_current_part_and_reads_as_one_sentence(string f1, string f2)
    {
        if (Model() is not { } d) return;
        using var t = new OnnxTranslator(d, beams: 2, intraOpThreads: 4, requestTimeout: TimeSpan.FromSeconds(60));
        var tr1 = await t.TranslateAsync(f1, Ctx, default);
        var cont = await t.TranslateContinuationAsync(f1 + " " + f2, tr1, Ctx, default);
        output.WriteLine($"PREFIX: {tr1}\nCONT: {cont}\nJOINED: {Ct2Translator.TrimFinalPunctuation(tr1)} {cont}");
        Assert.True(cont.Length >= 4);
        Assert.DoesNotContain(Ct2Translator.TrimFinalPunctuation(tr1), cont);          // prefix not repeated
        Assert.NotEqual(tr1, cont);
        // through the chain: only the new part is shown and cached
        var chain = new TranslationChain(new MemCache(), t);
        var r = await chain.TranslateContinuationAsync(new SubtitleText(null, f2), new SubtitleText(null, f1 + " " + f2), tr1, Ctx, default);
        Assert.Equal(TranslationSource.Translator, r.Source);
        Assert.DoesNotContain(Ct2Translator.TrimFinalPunctuation(tr1), r.Text);
        Assert.True(r.Text.Length >= 4);
    }

    [Fact] public async Task Engine_error_surfaces_as_TranslatorException_and_is_not_retried()
    {
        using var t = new OnnxTranslator(() => throw new DllNotFoundException("onnxruntime"));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hello.", Ctx, default));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hello.", Ctx, default));
    }

    private sealed class MemCache : ITranslationCache
    {
        private readonly Dictionary<string, string> _d = new();
        public bool TryGet(string game, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? tr) => _d.TryGetValue(game + "\0" + s, out tr);
        public void Put(string game, string s, string tr, string provider) => _d[game + "\0" + s] = tr;
    }
}
