using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class SubtitlePipelineTests
{
    private sealed class Src : IFrameSource
    {
        public event Action<Frame>? FrameArrived;
        public event Action<string>? Stopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void Send(Frame f) => FrameArrived?.Invoke(f);
        public void Close() => Stopped?.Invoke("Pencere kapandı");
    }
    private sealed class Ocr : IOcrEngine
    {
        public Dictionary<byte, string> Texts = new();
        public int Calls;
        public TaskCompletionSource<bool>? Gate;
        public async Task<OcrResult> ReadAsync(Frame f, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate != null) await Gate.Task;
            var t = Texts.GetValueOrDefault(f.Bgra[0], "");
            return new OcrResult(t.Length == 0 ? [] : [new OcrLine(t, 0.25, 0.5, 0.2, 0.6)]);
        }
    }
    private sealed class Sink : ISubtitleSink
    {
        public List<string> Shown = new();
        public int Clears;
        public RegionF? LastBox;
        public void Show(string? s, string t, TranslationSource src, RegionF? box = null, int lineCount = 1) { LastBox = box; lock (Shown) Shown.Add(t); }
        public void Clear() => Interlocked.Increment(ref Clears);
        public string[] Snapshot() { lock (Shown) return Shown.ToArray(); }
    }
    private sealed class Mem : ITranslationCache
    {
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) { t = null; return false; }
        public void Put(string g, string s, string t, string p) { }
    }
    private sealed class Tr : ITranslator
    {
        public string Name => "t";
        public Dictionary<string, TaskCompletionSource<bool>> Gates = new();
        public async Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct)
        {
            // deliberately ignores ct: worst-case slow translator
            if (Gates.TryGetValue(text, out var g)) await g.Task;
            return "TR:" + text;
        }
    }

    private const string A = "Hold the line", B = "Get to the chopper";
    private static Frame F(byte v, long t) => new(64, 16, 256, Enumerable.Repeat(v, 256 * 16).ToArray(), t);

    private sealed class Rig
    {
        public Src Src = new(); public Ocr Ocr = new(); public Sink Sink = new(); public Tr Tr = new();
        public LatencyStats Stats = new(); public long Clock;
        public SubtitlePipeline P;
        public Rig()
        {
            Ocr.Texts[10] = A; Ocr.Texts[200] = B;
            P = new SubtitlePipeline(Src, Ocr, new TranslationChain(new Mem(), Tr), Sink, "g", Stats, () => Clock, 10, () => new RegionF(0, 0.6, 1, 0.4));
            P.Start();
        }
        public void Send(byte v) { Src.Send(F(v, Clock)); Clock += 200; }
    }
    private static async Task WaitFor(Func<bool> c) { for (int i = 0; i < 200 && !c(); i++) await Task.Delay(10); }

    [Fact] public async Task Box_is_converted_to_window_coordinates_and_passed_to_sink()
    {
        var r = new Rig();
        r.Send(10); r.Send(10);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        var b = r.Sink.LastBox!;
        Assert.Equal(0.2, b.X, 3); Assert.Equal(0.7, b.Y, 3); Assert.Equal(0.6, b.W, 3); Assert.Equal(0.2, b.H, 3);
    }

    [Fact] public async Task Shows_translation_after_stable_read()
    {
        var r = new Rig();
        r.Send(10); r.Send(10);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        Assert.Equal(["TR:" + A], r.Sink.Snapshot());
        Assert.Equal(1, r.Stats.StableToShown().Count);
    }

    [Fact] public async Task Stale_translation_is_discarded_when_new_line_arrives()
    {
        var r = new Rig();
        var gate = r.Tr.Gates[A] = new TaskCompletionSource<bool>();
        r.Send(10); r.Send(10);      // a stable, its translation stays pending
        r.Send(200); r.Send(200);    // b stable
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        gate.SetResult(true);
        await Task.Delay(100);
        Assert.Equal(["TR:" + B], r.Sink.Snapshot());
    }

    [Fact] public void Frames_dropped_while_ocr_busy()
    {
        var r = new Rig();
        r.Ocr.Gate = new TaskCompletionSource<bool>();
        for (int i = 0; i < 6; i++) r.Send((byte)(20 + i * 30));
        Assert.Equal(1, r.Ocr.Calls);
        r.Ocr.Gate.SetResult(true);
    }

    [Fact] public void Unchanged_frames_skip_ocr()
    {
        var r = new Rig();
        for (int i = 0; i < 3; i++) r.Send(10);
        Assert.Equal(1, r.Ocr.Calls);
    }

    [Fact] public async Task Clear_after_empty_reads_calls_sink_clear()
    {
        var r = new Rig();
        r.Send(10); r.Send(10);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        for (int i = 0; i < 4; i++) r.Send((byte)(100 + i * 20));   // changed frames, OCR returns nothing
        Assert.Equal(1, r.Sink.Clears);
    }

    [Fact] public void Source_stopped_clears_sink()
    {
        var r = new Rig();
        r.Src.Close();
        Assert.Equal(1, r.Sink.Clears);
    }
}
