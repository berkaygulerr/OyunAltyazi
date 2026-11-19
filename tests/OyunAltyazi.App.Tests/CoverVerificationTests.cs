using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

/// <summary>
/// Real-screen check (needs an interactive desktop + Windows English OCR): a DPI-aware WinForms "game" shows a subtitle,
/// the real pipeline + OverlayWindow (capture exclusion OFF) run with a fake translator, then the screen is captured.
/// </summary>
public class CoverVerificationTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private const string Turkish = "Hemen helikoptere gidin yoksa hepimiz burada kalacağız";
    private static readonly string[] EnglishWords = ["chopper", "running", "time", "right", "decided", "nobody", "answer", "behind", "wouldn't"];

    private sealed class FakeGame : IDisposable
    {
        private readonly Process _p;
        public IntPtr Hwnd { get; }
        public FakeGame(int fontPx, string text)
        {
            var ps = Path.Combine(Path.GetTempPath(), "oa_cv_" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(ps, $@"
Add-Type -Namespace W -Name D -MemberDefinition '[System.Runtime.InteropServices.DllImport(""user32.dll"")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr v);'
[void][W.D]::SetProcessDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object Windows.Forms.Form; $f.Text='OyunAltyaziCover'; $f.BackColor=[Drawing.Color]::FromArgb(20,40,90)
$f.ClientSize = New-Object Drawing.Size(1600,900); $f.StartPosition='Manual'; $f.Left=60; $f.Top=60
$s = New-Object Windows.Forms.Label; $s.Font=New-Object Drawing.Font('Segoe UI',[single]{fontPx},[Drawing.GraphicsUnit]::Pixel); $s.ForeColor='White'
$s.AutoSize=$false; $s.TextAlign='MiddleCenter'; $s.Left=0; $s.Width=1600; $s.Height=100; $s.Top=770; $s.Text='{text}'; $f.Controls.Add($s)
[void]$f.ShowDialog()");
            _p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps}\""))!;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(25))
            {
                var w = WindowEnumerator.List().FirstOrDefault(x => x.ProcessId == _p.Id && x.Title == "OyunAltyaziCover");
                if (w is not null) { Hwnd = w.Hwnd; Thread.Sleep(700); return; }
                Thread.Sleep(200);
            }
            Dispose();
            throw new InvalidOperationException("Sahte oyun penceresi açılamadı");
        }
        public void Dispose() { try { _p.Kill(true); } catch { } }
    }

    private sealed class MemCache : ITranslationCache
    {
        public bool TryGet(string g, string t, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? tr) { tr = null; return false; }
        public void Put(string g, string t, string tr, string p) { }
    }
    private sealed class FakeTr : ITranslator
    {
        public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult(Turkish);
    }
    private sealed class Sink(OverlayWindow o) : ISubtitleSink
    {
        public volatile bool Shown; public volatile RegionF? Box; public volatile int Lines;
        public void Show(string? s, string t, TranslationSource src, RegionF? box = null, int lineCount = 1) { Box = box; Lines = lineCount; o.Show(s, t, src, box, lineCount); Shown = true; }
        public void Clear() => o.Clear();
    }

    private static async Task<bool> WaitUntil(Func<bool> c, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms && !c()) await Task.Delay(100); return c(); }

    private static Bitmap Shot(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
        return bmp;
    }

    /// <summary>Bounding box (bitmap px) of pixels matching pred inside strip.</summary>
    private static Rectangle? Bounds(Bitmap b, Rectangle strip, Func<Color, bool> pred)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = strip.Top; y < strip.Bottom; y++)
            for (int x = strip.Left; x < strip.Right; x++)
                if (pred(b.GetPixel(x, y))) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        return x1 < 0 ? null : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
    }

    private static async Task<OcrResult> Ocr(WindowsOcrEngine ocr, Bitmap bmp, Rectangle crop)
    {
        using var c = bmp.Clone(crop, PixelFormat.Format32bppArgb);
        var d = c.LockBits(new Rectangle(0, 0, c.Width, c.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[d.Stride * c.Height];
        Marshal.Copy(d.Scan0, bytes, 0, bytes.Length);
        c.UnlockBits(d);
        return await ocr.ReadAsync(new Frame(c.Width, c.Height, d.Stride, bytes, 0), CancellationToken.None);
    }

    [Theory]
    [InlineData(22, "Get to the chopper right now", "cover-small-22px")]
    [InlineData(44, "Get to the chopper right now", "cover-large-44px")]
    [InlineData(30, "Nobody decided to answer the question that was left behind us", "cover-long-30px")]
    public async Task Turkish_box_covers_english_and_has_matching_text_size(int fontPx, string english, string shotName)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return;
        using var game = new FakeGame(fontPx, english);
        DwmGetWindowAttribute(game.Hwnd, 9, out var fr, Marshal.SizeOf<RECT>());
        var frame = new Rectangle(fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
        var region = new RegionF(0.05, 0.80, 0.90, 0.19); // bottom band; frame includes title bar, so fractions are of the whole frame
        // keep the band over the label: label centre is at client y=820 -> frame y = (820 + titlebar)/frameH
        int titleBar = frame.Height - 900 - 8; // client 900 + bottom border 0 (DWM bounds) -> approx title bar height
        double labelTop = (770 + titleBar) / (double)frame.Height, labelH = 100.0 / frame.Height;
        region = new RegionF(0.05, labelTop - 0.02, 0.90, labelH + 0.04);

        OverlayWindow? overlay = null;
        var ready = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            overlay = new OverlayWindow();
            overlay.Attach(game.Hwnd, new OverlayStyle());
            overlay.ExcludeFromCapture = false;
            overlay.RegionProvider = () => region;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA); th.Start();
        Assert.True(ready.Wait(10000));
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(game.Hwnd);
        await Task.Delay(800);

        // ground truth English box from pixels (white text on blue), before the pipeline runs
        var strip = new Rectangle(0, (int)((labelTop - 0.01) * frame.Height), frame.Width, (int)((labelH + 0.02) * frame.Height));
        Rectangle truth; double engLineH;
        using (var before = Shot(frame))
        {
            truth = Bounds(before, strip, c => c.R > 200 && c.G > 200 && c.B > 200) ?? throw new Exception("İngilizce metin bulunamadı");
            var o = await Ocr(ocr, before, strip);
            engLineH = o.Lines.Max(l => l.Height) * strip.Height;
            Console.WriteLine($"{shotName}: truth English box {truth} (frame {frame}); OCR line height {engLineH:0.0}px");
        }

        var sink = new Sink(overlay!);
        using var src = new WgcFrameSource(game.Hwnd, () => region);
        using var pipe = new SubtitlePipeline(src, ocr, new TranslationChain(new MemCache(), new FakeTr()), sink, "fake", new LatencyStats(),
                                              () => Environment.TickCount64, regionProvider: () => region);
        pipe.Start();
        Assert.True(await WaitUntil(() => sink.Shown, 20000), "çeviri gösterilmedi");
        // The overlay only shows while the game is foreground: re-assert focus and poll until the cover is visible on screen.
        Bitmap after = null!; Rectangle? coverFound = null;
        for (int i = 0; i < 25 && coverFound is null; i++)
        {
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(game.Hwnd);
            await Task.Delay(400);
            after?.Dispose();
            after = Shot(frame);
            coverFound = Bounds(after, strip, c => Math.Abs(c.R - 18) < 4 && Math.Abs(c.G - 18) < 4 && Math.Abs(c.B - 18) < 4);
        }
        var cover = coverFound ?? throw new Exception("Örtü kutusu bulunamadı");
        Console.WriteLine($"{shotName}: cover box {cover}, pipeline lines={sink.Lines}");
        const int tol = 2;
        Assert.True(cover.Left <= truth.Left + tol && cover.Top <= truth.Top + tol && cover.Right >= truth.Right - tol && cover.Bottom >= truth.Bottom - tol,
                    $"örtü İngilizce kutuyu kapsamıyor: cover={cover} truth={truth}");
        Assert.True(cover.Height < truth.Height * 3 + 20, "örtü gereksiz büyük");

        var inner = Rectangle.Inflate(cover, -3, -2);
        var res = await Ocr(ocr, after, inner);
        var read = string.Join(" ", res.Lines.Select(l => l.Text)).ToLowerInvariant();
        Console.WriteLine($"{shotName}: OCR inside cover: {read}");
        Assert.Contains("helikoptere", read);
        Assert.DoesNotContain(EnglishWords, w => read.Contains(w));
        double trLineH = res.Lines.Max(l => l.Height) * inner.Height;
        Console.WriteLine($"{shotName}: Turkish OCR line height {trLineH:0.0}px vs English {engLineH:0.0}px ({trLineH / engLineH:0.00})");
        Assert.InRange(trLineH / engLineH, 0.7, 1.3);

        var dir = Environment.GetEnvironmentVariable("OA_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            using var crop = after.Clone(new Rectangle(0, strip.Top - 60, frame.Width, Math.Min(strip.Height + 120, frame.Height - strip.Top + 60)), PixelFormat.Format32bppArgb);
            crop.Save(Path.Combine(dir, shotName + ".png"), ImageFormat.Png);
        }
        overlay!.Dispatcher.InvokeShutdown();
    }
}
