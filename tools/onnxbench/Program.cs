// SPDX-License-Identifier: AGPL-3.0-only
// Benchmark: native ONNX CPU vs the existing CTranslate2 sidecar, same model family, same cases.
//   onnx --model DIR [--beams 4] [--threads 2] [--reps 5]
//   ct2  [--device cpu|auto] [--beam 2] [--reps 5]       (uses the installed OyunAltyaziData CT2 runtime/model, read only)
// One mode per process so working-set numbers are clean. Prints one JSON summary line + the translations.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;

var mode = args.Length > 0 ? args[0] : "";
string Arg(string n, string d) { var i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
int reps = int.Parse(Arg("reps", "5"));
var casesPath = Arg("cases", Path.Combine(AppContext.BaseDirectory, "cases.txt"));
var lines = File.ReadAllLines(casesPath).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
var singles = lines.Where(l => !l.Contains('|')).ToList();
var pairs = lines.Where(l => l.Contains('|')).Select(l => l.Split('|')).ToList();
var ctx = new TranslationContext("bench", [], null);
Process? sidecarProc = null;
IDisposable? disposable = null;
Func<string, Task<string>> translate;
Func<string, string, Task<string>> continuation;
var sw = Stopwatch.StartNew();
long loadMs = 0;
string desc;
var self = Process.GetCurrentProcess();

if (mode == "onnx")
{
    int beams = int.Parse(Arg("beams", "4")), threads = int.Parse(Arg("threads", "2"));
    var model = Arg("model", "");
    OnnxMarianEngine? created = null;
    var t = new OnnxTranslator(() => { var s = Stopwatch.StartNew(); created = new OnnxMarianEngine(model, 128, beams, threads); loadMs = s.ElapsedMilliseconds; return created; }, TimeSpan.FromSeconds(60));
    disposable = t;
    translate = s => t.TranslateAsync(s, ctx, default);
    continuation = (f, p) => t.TranslateContinuationAsync(f, p, ctx, default);
    desc = $"onnx-cpu beams={beams} threads={threads}";
}
else if (mode == "ct2")
{
    var device = Arg("device", "cpu"); var beam = Arg("beam", "2");
    var python = Ct2Paths.FindPython() ?? throw new Exception("no CT2 python runtime");
    var script = Ct2Paths.FindServerScript() ?? throw new Exception("no server.py");
    SidecarLauncher launcher = () =>
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-u", script, Ct2Paths.ModelDir, "--device", device, "--beam", beam }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine(); p.StandardInput.NewLine = "\n";
        sidecarProc = p;
        return new Sidecar(p.StandardInput, p.StandardOutput, () => { if (!p.HasExited) p.Kill(true); });
    };
    var t = new Ct2Translator(launcher, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300));
    disposable = t;
    translate = s => t.TranslateAsync(s, ctx, default);
    continuation = (f, p) => t.TranslateContinuationAsync(f, p, ctx, default);
    desc = $"ct2 device={device} beam={beam}";
}
else { Console.Error.WriteLine("mode: onnx|ct2"); return 2; }

// initial load = construct + first translation
sw.Restart();
await translate("Hello there.");
var firstMs = sw.ElapsedMilliseconds;
if (mode == "ct2") loadMs = firstMs;   // sidecar start is the dominant part; the request itself is ms
long Ws() { if (mode == "ct2") { sidecarProc!.Refresh(); return sidecarProc.WorkingSet64; } self.Refresh(); return self.WorkingSet64; }
long Peak() { if (mode == "ct2") { sidecarProc!.Refresh(); return sidecarProc.PeakWorkingSet64; } self.Refresh(); return self.PeakWorkingSet64; }
long wsAfterLoad = Ws();
// warm up
for (int i = 0; i < 3; i++) await translate(singles[i % singles.Count]);

var singleMs = new List<double>(); var outputs = new Dictionary<string, string>();
for (int r = 0; r < reps; r++)
    foreach (var s in singles)
    {
        var s0 = Stopwatch.GetTimestamp();
        var o = await translate(s);
        singleMs.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds);
        outputs[s] = o;
    }

var contMs = new List<double>(); var contRows = new List<object>();
for (int r = 0; r < reps; r++)
    foreach (var p in pairs)
    {
        var f1 = p[0].Trim(); var f2 = p[1].Trim();
        var tr1 = await translate(f1);
        var s0 = Stopwatch.GetTimestamp();
        var cont = await continuation(f1 + " " + f2, tr1);
        contMs.Add(Stopwatch.GetElapsedTime(s0).TotalMilliseconds);
        if (r == 0) contRows.Add(new { f1, f2, tr1, cont, joined = tr1.TrimEnd('.', '!', '?', ' ') + " " + cont });
    }

static double Pct(List<double> v, double q) { var s = v.OrderBy(x => x).ToList(); return Math.Round(s[Math.Min(s.Count - 1, (int)Math.Ceiling(q * s.Count) - 1)], 1); }
var summary = new
{
    mode = desc, loadMs, firstTranslateAfterStartMs = firstMs, n = singleMs.Count,
    p50 = Pct(singleMs, .5), p95 = Pct(singleMs, .95), mean = Math.Round(singleMs.Average(), 1),
    contN = contMs.Count, contP50 = contMs.Count > 0 ? Pct(contMs, .5) : 0, contP95 = contMs.Count > 0 ? Pct(contMs, .95) : 0,
    wsAfterLoadMB = wsAfterLoad / 1048576, wsAfterRunMB = Ws() / 1048576, peakWsMB = Peak() / 1048576,
};
Console.WriteLine(JsonSerializer.Serialize(summary));
var outPath = Arg("out", "");
if (outPath.Length > 0)
    File.WriteAllText(outPath, JsonSerializer.Serialize(new { summary, translations = outputs, continuations = contRows }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
disposable?.Dispose();
return 0;
