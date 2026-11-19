using System.Runtime.InteropServices.WindowsRuntime;
using OyunAltyazi.Core;
using OcrResult = OyunAltyazi.Core.OcrResult;
using OcrLine = OyunAltyazi.Core.OcrLine;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

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

    public async Task<OcrResult> ReadAsync(Frame frame, CancellationToken ct)
    {
        int scale = frame.Height < 40 ? 2 : 1;
        int w = frame.Width * scale, h = frame.Height * scale;
        if (w > OcrEngine.MaxImageDimension || h > OcrEngine.MaxImageDimension) { scale = 1; w = frame.Width; h = frame.Height; }
        var buf = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = y / scale;
            for (int x = 0; x < w; x++)
            {
                int so = sy * frame.Stride + (x / scale) * 4;
                int d = (y * w + x) * 4;
                buf[d] = frame.Bgra[so]; buf[d + 1] = frame.Bgra[so + 1]; buf[d + 2] = frame.Bgra[so + 2]; buf[d + 3] = 255;
            }
        }
        using var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        bmp.CopyFromBuffer(buf.AsBuffer());
        var res = await _engine.RecognizeAsync(bmp).AsTask(ct).ConfigureAwait(false);
        var lines = new List<OcrLine>();
        foreach (var line in res.Lines)
        {
            if (line.Words.Count == 0) continue;
            double top = line.Words.Min(x => x.BoundingRect.Top);
            double bottom = line.Words.Max(x => x.BoundingRect.Bottom);
            lines.Add(new OcrLine(line.Text, top / h, (bottom - top) / h));
        }
        return new OcrResult(lines);
    }
}
