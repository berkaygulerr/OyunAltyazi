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
        public IReadOnlyList<ShownUtterance> LastItems = [];
        public void Show(IReadOnlyList<ShownUtterance> u, RegionF? box = null, int lineCount = 1, bool anchorTop = false) { LastBox = box; LastItems = u; lock (Shown) Shown.Add(string.Join(" ", u.Select(x => x.Text))); }
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

    private const string A = "Hold the line.", B = "Get to the chopper.";
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
        public void SetTranslator(ITranslator t)
        {
            P.Stop();
            P = new SubtitlePipeline(Src, Ocr, new TranslationChain(new Mem(), t), Sink, "g", Stats, () => Clock, 10, () => new RegionF(0, 0.6, 1, 0.4));
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
        for (int i = 0; i < 6; i++) r.Send((byte)(30 + i * 20));   // changed frames, OCR returns nothing
        Assert.Equal(1, r.Sink.Clears);
    }

    [Fact] public void Source_stopped_clears_sink()
    {
        var r = new Rig();
        r.Src.Close();
        Assert.Equal(1, r.Sink.Clears);
    }

    [Fact] public async Task Open_fragment_shows_alone_then_joined_translation_replaces_it()
    {
        var r = new Rig();
        r.Ocr.Texts[50] = "We have to get to the extraction point";
        r.Ocr.Texts[120] = "before the enemy arrives.";
        r.Send(50); r.Send(50);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        r.Send(120); r.Send(120);
        await WaitFor(() => r.Sink.Snapshot().Length == 3);
        var shown = r.Sink.Snapshot();
        Assert.Equal("TR:before the enemy arrives.", shown[1]);
        Assert.Equal("before the enemy arrives.", shown[2]); // remainder only: fragment 1 is never repeated
    }

    [Fact] public async Task Stale_joined_translation_is_discarded()
    {
        var r = new Rig();
        r.Ocr.Texts[50] = "We have to get to the extraction point";
        r.Ocr.Texts[120] = "before the enemy arrives.";
        r.Ocr.Texts[200] = "Something else entirely.";
        var gate = new TaskCompletionSource<bool>();
        r.Tr.Gates["We have to get to the extraction point before the enemy arrives."] = gate;
        r.Send(50); r.Send(50);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        r.Send(120); r.Send(120);
        await WaitFor(() => r.Sink.Snapshot().Length == 2);
        r.Send(200); r.Send(200);
        await WaitFor(() => r.Sink.Snapshot().Length == 3);
        gate.SetResult(true);
        await Task.Delay(300);
        Assert.DoesNotContain(r.Sink.Snapshot(), x => x.Contains("extraction point before"));
    }

    // ---- continuation (target prefix) ----
    private sealed class ContTr : IContinuationTranslator
    {
        public string Name => "c";
        public List<(string Full, string Prefix)> Calls = new();
        public Func<string, string, string> Cont = (full, prefix) => "DEVAM(" + prefix + ")";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult("TR:" + text);
        public Task<string> TranslateContinuationAsync(string full, string prefix, TranslationContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Calls) Calls.Add((full, prefix));
            return Task.FromResult(Cont(full, prefix));
        }
    }
    private static readonly TranslationContext Cx = new("g", [], null);
    private static readonly SubtitleText Frag = new(null, "before the enemy arrives.");
    private static readonly SubtitleText Joined = new(null, "We have to go before the enemy arrives.");

    [Fact] public async Task Chain_uses_continuation_translator_with_prefix()
    {
        var t = new ContTr { Cont = (_, _) => "düşman gelmeden önce." };
        var r = await new TranslationChain(new Mem(), t).TranslateContinuationAsync(Frag, Joined, "Gitmeliyiz", Cx, default);
        Assert.Equal("düşman gelmeden önce.", r.Text);
        Assert.Equal(("We have to go before the enemy arrives.", "Gitmeliyiz"), t.Calls.Single());
    }

    [Theory] [InlineData("")] [InlineData("a")] [InlineData("Gitmeliyiz")]
    public async Task Chain_falls_back_to_fragment_alone_when_continuation_is_empty_or_garbage(string cont)
    {
        var t = new ContTr { Cont = (_, _) => cont };
        var r = await new TranslationChain(new Mem(), t).TranslateContinuationAsync(Frag, Joined, "Gitmeliyiz", Cx, default);
        Assert.Equal("TR:before the enemy arrives.", r.Text);
    }

    [Fact] public async Task Chain_continuation_cancellation_propagates()
    {
        var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TranslationChain(new Mem(), new ContTr()).TranslateContinuationAsync(Frag, Joined, "Gitmeliyiz", Cx, cts.Token));
    }

    private sealed class Dict(Func<string, string> f) : ITranslator
    {
        public string Name => "g";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult(f(text));
    }

    [Fact] public async Task Google_style_shows_remainder_when_joined_starts_with_previous_translation()
    {
        var t = new Dict(x => x.StartsWith("We have") ? "Gitmeliyiz, düşman gelmeden önce." : "ALONE");
        var r = await new TranslationChain(new Mem(), t).TranslateContinuationAsync(Frag, Joined, "Gitmeliyiz.", Cx, default);
        Assert.Equal("düşman gelmeden önce.", r.Text);
    }

    [Fact] public async Task Google_style_shows_fragment_alone_when_joined_does_not_start_with_prefix()
    {
        var t = new Dict(x => x.StartsWith("We have") ? "Düşman gelmeden gitmeliyiz." : "Düşman gelmeden önce.");
        var r = await new TranslationChain(new Mem(), t).TranslateContinuationAsync(Frag, Joined, "Gitmeliyiz", Cx, default);
        Assert.Equal("Düşman gelmeden önce.", r.Text);
    }

    [Fact] public async Task Pipeline_second_fragment_shows_only_continuation_never_fragment_one()
    {
        var r = new Rig(); var ct = new ContTr(); r.SetTranslator(ct);
        r.Ocr.Texts[50] = "We have to get to the extraction point";
        r.Ocr.Texts[120] = "before the enemy arrives.";
        r.Send(50); r.Send(50);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        r.Send(120); r.Send(120);
        await WaitFor(() => r.Sink.Snapshot().Length == 3);
        var shown = r.Sink.Snapshot();
        Assert.Equal("TR:We have to get to the extraction point", shown[0]);
        Assert.Equal("DEVAM(TR:We have to get to the extraction point)", shown[2]);
        Assert.DoesNotContain("TR:We have to get to the extraction point", shown[1]);
        Assert.Equal("TR:We have to get to the extraction point", ct.Calls.Single().Prefix);
    }

    [Fact] public async Task Pipeline_unrelated_sentences_never_use_prefix()
    {
        var r = new Rig(); var ct = new ContTr(); r.SetTranslator(ct);
        r.Send(10); r.Send(10);
        await WaitFor(() => r.Sink.Snapshot().Length == 1);
        r.Send(200); r.Send(200);
        await WaitFor(() => r.Sink.Snapshot().Length == 2);
        await Task.Delay(100);
        Assert.Empty(ct.Calls);
    }

    [Fact] public async Task Pipeline_different_speakers_never_use_prefix()
    {
        var r = new Rig(); var ct = new ContTr(); r.SetTranslator(ct);
        r.Ocr.Texts[50] = "Ana: We have to get to the extraction point";
        r.Ocr.Texts[120] = "Bob: before the enemy arrives.";
        r.Send(50); r.Send(50);
        await WaitFor(() => r.Sink.Snapshot().Length >= 1);
        r.Send(120); r.Send(120);
        await Task.Delay(500);
        Assert.Empty(ct.Calls);
    }
}
