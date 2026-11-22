using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

/// <summary>Pipeline-level guarantees for the optional online translator: one presentation per utterance, no stale writes.</summary>
public class PipelineGeminiTests
{
    private sealed class Src : IFrameSource
    {
        public event Action<Frame>? FrameArrived; public event Action<string>? Stopped;
        public void Start() { } public void Stop() { } public void Dispose() { }
        public void Send(Frame f) => FrameArrived?.Invoke(f); public void Close() => Stopped?.Invoke("x");
    }
    private sealed class Ocr : IOcrEngine
    {
        public Dictionary<byte, string> Texts = new();
        public Task<OcrResult> ReadAsync(Frame f, CancellationToken ct)
        { var t = Texts.GetValueOrDefault(f.Bgra[0], ""); return Task.FromResult(new OcrResult(t.Length == 0 ? [] : [new OcrLine(t, 0.25, 0.5, 0.2, 0.6)])); }
    }
    private sealed class Sink : ISubtitleSink
    {
        public List<string> Shown = new();
        public void Show(IReadOnlyList<ShownUtterance> u, RegionF? box = null, int lineCount = 1, bool anchorTop = false) { lock (Shown) Shown.Add(string.Join(" ", u.Select(x => x.Source + ":" + x.Text))); }
        public void Clear() { }
        public string[] Snap() { lock (Shown) return Shown.ToArray(); }
    }
    private sealed class Mem : ITranslationCache
    {
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) { t = null; return false; }
        public void Put(string g, string s, string t, string p) { }
    }
    private sealed class Local : ITranslator
    {
        public string Name => "local"; public Task<string> TranslateAsync(string t, TranslationContext c, CancellationToken ct) => Task.FromResult("L:" + t);
    }
    private sealed class Slow : ITranslator
    {
        public string Name => "gemini"; public int Cancelled;
        public Dictionary<string, TaskCompletionSource<bool>> Gates = new(); public Exception? Fail;
        public async Task<string> TranslateAsync(string t, TranslationContext c, CancellationToken ct)
        {
            if (Fail != null) throw Fail;
            if (Gates.TryGetValue(t, out var g)) { try { await g.Task.WaitAsync(ct); } catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; } }
            return "G:" + t;
        }
    }
    private const string A = "Hold the line.", B = "Get to the chopper.";
    private static Frame F(byte v, long t) => new(64, 16, 256, Enumerable.Repeat(v, 256 * 16).ToArray(), t);
    private static async Task WaitFor(Func<bool> c) { for (int i = 0; i < 300 && !c(); i++) await Task.Delay(10); }

    private static (SubtitlePipeline p, Src src, Sink sink, Func<byte, long> send) Rig(ITranslator premium, Ocr ocr)
    {
        var src = new Src(); var sink = new Sink(); long clock = 0;
        var p = new SubtitlePipeline(src, ocr, new TranslationChain(new Mem(), new Local(), null, premium), sink, "g", new LatencyStats(), () => clock, 10, () => new RegionF(0, 0.6, 1, 0.4));
        p.Start();
        return (p, src, sink, v => { src.Send(F(v, clock)); clock += 200; return clock; });
    }

    [Fact] public async Task One_presentation_per_utterance_no_flicker()
    {
        var ocr = new Ocr(); ocr.Texts[10] = A;
        var (p, _, sink, send) = Rig(new Slow(), ocr);
        send(10); await WaitFor(() => sink.Snap().Length > 0);
        for (int i = 0; i < 4; i++) { send(10); await Task.Delay(40); }
        Assert.Equal(["Gemini:G:" + A], sink.Snap());
        p.Dispose();
    }

    [Fact] public async Task Slow_premium_result_of_a_replaced_line_is_cancelled_and_never_written()
    {
        var ocr = new Ocr(); ocr.Texts[10] = A; ocr.Texts[200] = B;
        var slow = new Slow(); slow.Gates[A] = new();
        var (p, _, sink, send) = Rig(slow, ocr);
        send(10); await Task.Delay(100);
        send(200); await WaitFor(() => sink.Snap().Length > 0);
        slow.Gates[A].TrySetResult(true); await Task.Delay(100);
        Assert.Equal(["Gemini:G:" + B], sink.Snap());
        Assert.True(slow.Cancelled >= 1);
        p.Dispose();
    }

    [Fact] public async Task Premium_429_shows_local_once_and_quietly()
    {
        var ocr = new Ocr(); ocr.Texts[10] = A;
        var (p, _, sink, send) = Rig(new Slow { Fail = new TranslatorException("HTTP 429", 429) }, ocr);
        send(10); await WaitFor(() => sink.Snap().Length > 0); await Task.Delay(100);
        Assert.Equal(["Translator:L:" + A], sink.Snap());
        p.Dispose();
    }
}
