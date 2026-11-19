using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

public class EndToEndSmokeTests
{
    private sealed class MemCache : ITranslationCache
    {
        private readonly Dictionary<string, string> _d = new();
        public bool TryGet(string game, string t, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? tr) => _d.TryGetValue(game + "|" + TextKey.Canon(t), out tr);
        public void Put(string game, string t, string tr, string provider) => _d[game + "|" + TextKey.Canon(t)] = tr;
    }

    private sealed class FakeTranslator : ITranslator
    {
        public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult("Yukarı çıkın, hemen.");
    }

    private sealed class Sink : ISubtitleSink
    {
        public readonly List<(string? Speaker, string Text, TranslationSource Source)> Shown = new();
        public int Cleared;
        public void Show(string? speaker, string text, TranslationSource source) { lock (Shown) Shown.Add((speaker, text, source)); }
        public void Clear() => Interlocked.Increment(ref Cleared);
    }

    [Fact]
    public async Task Window_text_flows_to_overlay_sink_then_clears_on_close()
    {
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return;
        using var w = new TestWindow();
        var sink = new Sink();
        var chain = new TranslationChain(new MemCache(), new FakeTranslator());
        using var src = new WgcFrameSource(w.Hwnd, () => new RegionF(0, 0.08, 1, 0.5));
        using var pipe = new SubtitlePipeline(src, ocr, chain, sink, "powershell.exe", new LatencyStats(), () => Environment.TickCount64);
        pipe.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(6) && sink.Shown.Count == 0) await Task.Delay(100);
        Assert.NotEmpty(sink.Shown);
        Assert.Equal("Price", sink.Shown[0].Speaker);
        Assert.Equal("Yukarı çıkın, hemen.", sink.Shown[0].Text);
        w.Kill();
        sw.Restart();
        while (sw.Elapsed < TimeSpan.FromSeconds(3) && sink.Cleared == 0) await Task.Delay(100);
        Assert.True(sink.Cleared > 0);
    }
}
