using System.Runtime.InteropServices.WindowsRuntime;
using OyunAltyazi.Core;
using OcrResult = OyunAltyazi.Core.OcrResult;
using OcrLine = OyunAltyazi.Core.OcrLine;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using OcrWord = Windows.Media.Ocr.OcrWord;

namespace OyunAltyazi.App.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine _engine;
    private WindowsOcrEngine(OcrEngine engine) => _engine = engine;

    public static WindowsOcrEngine? TryCreate()
    {
        try
        {
            var e = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            return e is null ? null : new WindowsOcrEngine(e);
        }
        catch (Exception) { return null; }
    }

    // Tunables (env overrides only for experiments).
    private static readonly int TargetTextPx = int.TryParse(Environment.GetEnvironmentVariable("OCR_TARGET"), out var tp) ? tp : 96;
    private static readonly string GrayMode = Environment.GetEnvironmentVariable("OCR_GRAY") ?? "0";
    private static readonly bool Gray = GrayMode != "0";
    private static readonly bool Invert = Environment.GetEnvironmentVariable("OCR_INV") == "1";
    private static readonly bool Stretch = Environment.GetEnvironmentVariable("OCR_STRETCH") != "0";

    private static readonly int[] Targets = (Environment.GetEnvironmentVariable("OCR_TARGETS") ?? "80,110").Split(',').Select(int.Parse).ToArray();

    public async Task<OcrResult> ReadAsync(Frame frame, CancellationToken ct)
    {
        var cands = new List<OcrResult>();
        foreach (var t in Targets)
        {
            cands.Add(await ReadOnce(frame, t, ct).ConfigureAwait(false));
            if (cands.Count == 2 && Norm(cands[0]).SequenceEqual(Norm(cands[1]))) break;
        }
        return Pick(cands);
    }

    private static List<string> Norm(OcrResult r) => string.Join(" ", r.Lines.OrderBy(l => l.Top).ThenBy(l => l.Left).Select(l => l.Text))
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new string(w.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToList();

    // Word-level consensus between scales: words several passes agree on are right; odd tokens are noise.
    private static OcrResult Pick(List<OcrResult> c)
    {
        if (c.Count == 1) return c[0];
        var norms = c.Select(Norm).ToList();
        OcrResult best = c[0]; double bs = double.MinValue;
        for (int i = 0; i < c.Count; i++)
        {
            double s = 0;
            foreach (var w in norms[i])
            {
                int votes = 0;
                for (int j = 0; j < c.Count; j++) if (j != i && norms[j].Contains(w)) votes++;
                bool plausible = w.All(char.IsLetter) && (w.Length == 1 ? w is "i" or "a" : w.Any(ch => "aeiouy".Contains(ch)));
                s += votes * 1.0 + (plausible ? 0.6 : -1.5);
            }
            if (s > bs) { bs = s; best = c[i]; }
        }
        return best;
    }

    private async Task<OcrResult> ReadOnce(Frame frame, int TargetTextPx, CancellationToken ct)
    {
        // Subtitle text is typically ~25-35 px tall in a region ~60-100 px high: scale the region to ~TargetTextPx total height.
        double sc = Math.Clamp((double)TargetTextPx / Math.Max(1, frame.Height), 1.0, 3.0);
        int maxDim = (int)OcrEngine.MaxImageDimension;
        int w = (int)Math.Round(frame.Width * sc), h = (int)Math.Round(frame.Height * sc);
        if (w + 48 > maxDim || h + 48 > maxDim)
        {
            sc = Math.Min((double)maxDim / frame.Width, (double)maxDim / frame.Height);
            w = (int)(frame.Width * sc); h = (int)(frame.Height * sc);
        }
        sc = Math.Max(sc, 0.0001);
        int pad = 24, W = w + 2 * pad, H = h + 2 * pad;
        var buf = Resample(frame, W, H, sc, pad, out var lo, out var hi);
        using var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, W, H, BitmapAlphaMode.Premultiplied);
        bmp.CopyFromBuffer(buf.AsBuffer());
        var res = await _engine.RecognizeAsync(bmp).AsTask(ct).ConfigureAwait(false);
        var lines = new List<OcrLine>();
        foreach (var line in res.Lines)
        {
            if (line.Words.Count == 0) continue;
            var words = line.Words.OrderBy(x => x.BoundingRect.Left).ToList();
            double top = words.Min(x => x.BoundingRect.Top);
            double bottom = words.Max(x => x.BoundingRect.Bottom);
            double left = words.Min(x => x.BoundingRect.Left);
            double right = words.Max(x => x.BoundingRect.Right);
            var text = RebuildText(frame, words, sc, pad);
            lines.Add(new OcrLine(text, (top - pad) / h, (bottom - top) / h, (left - pad) / w, (right - left) / w));
        }
        return new OcrResult(lines);
    }

    // Bilinear upscale; grayscale = max(R,G,B) so teal and white are both bright; contrast stretched to the 2..98 percentile.
    private static byte[] Resample(Frame f, int w, int h, double sc, int pad, out int lo, out int hi)
    {
        var buf = new byte[w * h * 4];
        var hist = new int[256];
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            double fy = Math.Clamp((y - pad + 0.5) / sc - 0.5, 0, f.Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, f.Height - 1); double wy = fy - y0;
            for (int x = 0; x < w; x++)
            {
                double fx = Math.Clamp((x - pad + 0.5) / sc - 0.5, 0, f.Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, f.Width - 1); double wx = fx - x0;
                int o00 = y0 * f.Stride + x0 * 4, o01 = y0 * f.Stride + x1 * 4, o10 = y1 * f.Stride + x0 * 4, o11 = y1 * f.Stride + x1 * 4;
                int d = (y * w + x) * 4;
                double m = 0; int[] c = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    double v = (f.Bgra[o00 + k] * (1 - wx) + f.Bgra[o01 + k] * wx) * (1 - wy) + (f.Bgra[o10 + k] * (1 - wx) + f.Bgra[o11 + k] * wx) * wy;
                    c[k] = (int)v; if (v > m) m = v;
                }
                if (Gray) { if (GrayMode == "2") m = 0.299 * c[2] + 0.587 * c[1] + 0.114 * c[0]; else if (GrayMode == "3") m = (c[1] + c[0]) / 2.0; byte g = (byte)m; gray[y * w + x] = g; hist[g]++; }
                else { buf[d] = (byte)c[0]; buf[d + 1] = (byte)c[1]; buf[d + 2] = (byte)c[2]; buf[d + 3] = 255; }
            }
        }
        lo = 0; hi = 255;
        if (Gray)
        {
            int total = w * h, acc = 0;
            if (Stretch)
            {
                for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= total * 0.02) { lo = i; break; } }
                acc = 0;
                for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= total * 0.005) { hi = i; break; } }
                if (hi - lo < 32) { lo = Math.Max(0, hi - 32); }
            }
            double span = Math.Max(1, hi - lo);
            for (int i = 0; i < gray.Length; i++)
            {
                int v = (int)Math.Clamp((gray[i] - lo) * 255 / span, 0, 255);
                if (Invert) v = 255 - v;
                int d = i * 4; buf[d] = buf[d + 1] = buf[d + 2] = (byte)v; buf[d + 3] = 255;
            }
        }
        return buf;
    }

    private static readonly char[] TrailJunk = new[] { (char)58, (char)59, (char)46, (char)44, (char)0x2022, (char)42, (char)39, (char)34, (char)45, (char)95 };

    // Joins words left-to-right with a space between every pair; a leading run of coloured (teal) words followed by white ones is a speaker label and gets a ':'.
    private static string RebuildText(Frame f, List<OcrWord> words, double sc, int pad)
    {
        var parts = words.Select(x => x.Text).ToList();
        var tinted = words.Select(x => IsTinted(f, x.BoundingRect, sc, pad)).ToList();
        int lead = 0; while (lead < words.Count && tinted[lead]) lead++;
        if (lead > 0 && lead < words.Count && lead <= 3 && tinted.Skip(lead).All(t => !t) && char.IsLetter(parts[0][0]))
        {
            var last = parts[lead - 1].TrimEnd(TrailJunk);
            if (last.Length == 0) { parts.RemoveAt(lead - 1); lead--; if (lead == 0) return string.Join(" ", parts); last = parts[lead - 1]; }
            parts[lead - 1] = last + ":";
        }
        return string.Join(" ", parts);
    }

    // Bright pixels of the word box are clearly more green/blue than red (white text is neutral).
    private static bool IsTinted(Frame f, Windows.Foundation.Rect r, double sc, int pad)
    {
        int x0 = Math.Max(0, (int)((r.Left - pad) / sc)), x1 = Math.Min(f.Width, (int)Math.Ceiling((r.Right - pad) / sc));
        int y0 = Math.Max(0, (int)((r.Top - pad) / sc)), y1 = Math.Min(f.Height, (int)Math.Ceiling((r.Bottom - pad) / sc));
        if (x1 <= x0 || y1 <= y0) return false;
        int mx = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int o = y * f.Stride + x * 4; mx = Math.Max(mx, Math.Max(f.Bgra[o], Math.Max(f.Bgra[o + 1], f.Bgra[o + 2]))); }
        if (mx < 90) return false;
        long sr = 0, sg = 0; int n = 0;
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
        {
            int o = y * f.Stride + x * 4; int m = Math.Max(f.Bgra[o], Math.Max(f.Bgra[o + 1], f.Bgra[o + 2]));
            if (m >= mx * 0.8) { sr += f.Bgra[o + 2]; sg += Math.Max(f.Bgra[o + 1], f.Bgra[o]); n++; }
        }
        return n >= 4 && (double)sr / sg < 0.7;
    }
}
