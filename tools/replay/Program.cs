// Offline replay: feeds a folder of PNG frames (name = frame index, fps arg) through the real SubtitlePipeline + WindowsOcrEngine.
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
// usage: replay <framesDir> <fps> <outPrefix>   -> <outPrefix>.reads.tsv, .shown.txt, .truth.txt (raw OCR of every frame, no gating)
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.Core;

var dir = args[0]; double fps = double.Parse(args[1]); var outp = args[2];
var mode = args.Length > 3 ? args[3] : "replay";
var files = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToArray();
var ocr = WindowsOcrEngine.TryCreate() ?? throw new Exception("no OCR");

Frame Load(string f, long ts)
{
    var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(f); bmp.EndInit();
    var c = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
    int stride = c.PixelWidth * 4; var px = new byte[stride * c.PixelHeight]; c.CopyPixels(px, stride, 0);
    return new Frame(c.PixelWidth, c.PixelHeight, stride, px, ts);
}

if (mode == "truth")
{
    // OCR of every frame without any gating; lines = cleaned text per frame
    using var w = new StreamWriter(outp + ".truth.tsv");
    for (int i = 0; i < files.Length; i++)
    {
        var fr = Load(files[i], (long)(i * 1000 / fps));
        var r = await ocr.ReadAsync(fr, CancellationToken.None);
        var t = TextCleaner.Clean(r, out _, out _, Array.Empty<string>());
        w.WriteLine($"{fr.TimestampMs}\t{(t is null ? "" : (t.Speaker is null ? "" : t.Speaker + ": ") + t.Text)}");
    }
    return;
}

long now = 0;
var src = new FakeSource();
var sink = new Sink(() => now);
var cache = new MemCache();
var chain = new TranslationChain(cache, new FakeTr());
using var w2 = new StreamWriter(outp + ".reads.tsv");
var timed = new TimedOcr(ocr, _ => { });
var pipe = new SubtitlePipeline(src, timed, chain, sink, "replay", new LatencyStats(), () => now, 10, () => new RegionF(0, 0, 1, 1));
pipe.Log = e => w2.WriteLine($"{e.TimeMs}\t{(e.Changed ? 1 : 0)}\t{e.Raw}\t{e.Cleaned}\t{e.Event}");
pipe.Start();
long busyUntil = 0;
for (int i = 0; i < files.Length; i++)
{
    long ts = (long)(i * 1000 / fps);
    if (ts < busyUntil) continue; // real pipeline would drop it while OCR is busy
    now = ts;
    var fr = Load(files[i], ts);
    var sw = Stopwatch.StartNew();
    src.Emit(fr);
    while (pipe.IsBusy) await Task.Delay(1);
    long cost = sw.ElapsedMilliseconds - 0;
    if (timed.Calls > 0) { busyUntil = ts + timed.LastMs; timed.Calls = 0; }
    await Task.Delay(3);
}
pipe.Stop();
File.WriteAllLines(outp + ".shown.txt", sink.Shown.Select(s => $"{s.t}\t{s.text}\t{(s.box is null ? "" : string.Join(",", new[]{s.box.X, s.box.Y, s.box.W, s.box.H}.Select(v => v.ToString("F5", System.Globalization.CultureInfo.InvariantCulture))))}\t{s.lines}\t{(s.top ? 1 : 0)}"));
Console.WriteLine($"frames={files.Length} shown={sink.Shown.Count} avgOcrMs={timed.AvgMs:F0}");

sealed class FakeSource : IFrameSource
{
    public event Action<Frame>? FrameArrived; public event Action<string>? Stopped { add { } remove { } }
    public void Start() { } public void Stop() { } public void Dispose() { }
    public void Emit(Frame f) => FrameArrived?.Invoke(f);
}
sealed class TimedOcr(IOcrEngine inner, Action<long> unused) : IOcrEngine
{
    public int Calls; public Action<long> U => unused; public long LastMs; long total; int n;
    public double AvgMs => n == 0 ? 0 : (double)total / n;
    public async Task<OcrResult> ReadAsync(Frame f, CancellationToken ct)
    { var sw = Stopwatch.StartNew(); var r = await inner.ReadAsync(f, ct); LastMs = sw.ElapsedMilliseconds; total += LastMs; n++; Calls++; return r; }
}
sealed class Sink(Func<long> clock) : ISubtitleSink
{
    public List<(long t, string text, RegionF? box, int lines, bool top)> Shown = new();
    public void Show(IReadOnlyList<ShownUtterance> u, RegionF? box = null, int lineCount = 1, bool anchorTop = false) { lock (Shown) Shown.Add((clock(), string.Join(" § ", u.Select(x => (x.Speaker is null ? "" : x.Speaker + ": ") + x.Text)), box, lineCount, anchorTop)); }
    public void Clear() { }
}
sealed class MemCache : ITranslationCache
{
    readonly Dictionary<string, string> d = new();
    public bool TryGet(string g, string s, out string t) => d.TryGetValue(s, out t!);
    public void Put(string g, string s, string t, string p) => d[s] = t;
}
sealed class FakeTr : ITranslator
{
    public string Name => "fake";
    public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult(text);
}
