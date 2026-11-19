using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

/// <summary>Fake game: changing subtitle lines at the bottom, a static HUD label at the top-left.</summary>
public sealed class FakeGameWindow : IDisposable
{
    private readonly Process _p;
    public IntPtr Hwnd { get; }
    public readonly string FreezeFlag = Path.Combine(Path.GetTempPath(), "oa_freeze_" + Guid.NewGuid().ToString("N"));
    public static readonly string[] Lines =
    [
        "Get to the chopper right now", "We are running out of time here", "Cover me while I reload this",
        "They are coming from the east", "Hold the line until dawn comes", "Nobody leaves this room alive",
    ];

    public FakeGameWindow()
    {
        var ps = Path.Combine(Path.GetTempPath(), "oa_fake_" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(ps, $@"
Add-Type -AssemblyName System.Windows.Forms
$lines = @({string.Join(",", Lines.Select(l => "'" + l + "'"))})
$f = New-Object Windows.Forms.Form; $f.Text='OyunAltyaziFake'; $f.BackColor=[Drawing.Color]::FromArgb(30,30,30)
$f.ClientSize = New-Object Drawing.Size(900,560); $f.StartPosition='Manual'; $f.Left=40; $f.Top=40
$h = New-Object Windows.Forms.Label; $h.Text='OBJECTIVE: Find the secret key'; $h.Font=New-Object Drawing.Font('Segoe UI',16)
$h.ForeColor='White'; $h.AutoSize=$true; $h.Left=10; $h.Top=10; $f.Controls.Add($h)
$s = New-Object Windows.Forms.Label; $s.Font=New-Object Drawing.Font('Segoe UI',24); $s.ForeColor='White'; $s.AutoSize=$false
$s.TextAlign='MiddleCenter'; $s.Left=0; $s.Width=900; $s.Height=60; $s.Top=470; $f.Controls.Add($s)
$i = 0; $t = New-Object Windows.Forms.Timer; $t.Interval = 2500
$s.Text = $lines[0]
$t.Add_Tick({{ if (-not (Test-Path '{FreezeFlag}')) {{ $script:i = ($script:i + 1) % $lines.Count; $s.Text = $lines[$script:i] }} }})
$t.Start(); [void]$f.ShowDialog()");
        _p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps}\""))!;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var w = WindowEnumerator.List().FirstOrDefault(x => x.ProcessId == _p.Id && x.Title == "OyunAltyaziFake");
            if (w is not null) { Hwnd = w.Hwnd; Thread.Sleep(500); return; }
            Thread.Sleep(200);
        }
        Dispose();
        throw new InvalidOperationException("Sahte oyun penceresi açılamadı");
    }

    public void Freeze() => File.WriteAllText(FreezeFlag, "1");
    public void Dispose() { try { _p.Kill(true); } catch { } }
}

public class AutoRegionCoverTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private sealed class MemCache : ITranslationCache
    {
        public bool TryGet(string g, string t, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? tr) { tr = null; return false; }
        public void Put(string g, string t, string tr, string p) { }
    }
    private sealed class FakeTr : ITranslator
    {
        public string Name => "fake";
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => Task.FromResult("Hemen helikoptere gidin buradan");
    }
    private sealed class Sink : ISubtitleSink
    {
        public volatile RegionF? Box; public volatile bool Shown;
        public void Show(string? s, string t, TranslationSource src, RegionF? box = null) { Box = box; Shown = true; }
        public void Clear() { }
    }

    private sealed class LogOcr(IOcrEngine inner) : IOcrEngine
    {
        public async Task<OcrResult> ReadAsync(Frame f, CancellationToken ct)
        {
            var r = await inner.ReadAsync(f, ct);
            Console.WriteLine("OCR@" + Environment.TickCount64 % 100000 + ": " + string.Join(" | ", r.Lines.Select(l => $"{l.Text} [{l.Left:0.00},{l.Top:0.00},{l.Width:0.00},{l.Height:0.00}]")));
            return r;
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> c, int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms && !c()) await Task.Delay(100); return c(); }

    [Fact]
    public async Task Detects_subtitle_region_then_cover_overlay_hides_english()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return;
        using var game = new FakeGameWindow();
        var chain = new TranslationChain(new MemCache(), new FakeTr());

        // ---- Feature A: detect over the whole window (HUD sits inside the band on purpose)
        RegionF? found = null;
        var sink0 = new Sink();
        var band = new RegionF(0, 0, 1, 1);
        using (var src = new WgcFrameSource(game.Hwnd, () => band))
        using (var pipe = new SubtitlePipeline(src, new LogOcr(ocr), chain, sink0, "fake", new LatencyStats(), () => Environment.TickCount64,
                                               regionProvider: () => band, detector: new RegionDetector()))
        {
            pipe.RegionDetected += r => found = r;
            pipe.Start();
            Assert.True(await WaitUntil(() => found is not null, 45000), "bölge bulunamadı");
        }
        var reg = found!;
        Console.WriteLine($"detected region: {reg}");
        // subtitle label spans y 470..530 of 560 (0.84..0.95); HUD sits at y<0.1
        Assert.True(reg.Y <= 0.86 && reg.Y + reg.H >= 0.93, "altyazı bölgesi içinde olmalı");
        Assert.True(reg.Y > 0.5, "HUD bölgenin dışında olmalı");

        // ---- Feature B: freeze the English line, run in normal mode with the cover overlay
        game.Freeze();
        await Task.Delay(500);
        var sink = new Sink();
        OverlayWindow? overlay = null;
        var ready = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            overlay = new OverlayWindow();
            overlay.Attach(game.Hwnd, new OverlayStyle());
            overlay.ExcludeFromCapture = false; // so the screenshot below can see the overlay
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA); th.Start();
        Assert.True(ready.Wait(10000));

        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(game.Hwnd);
        await Task.Delay(800);

        using var src2 = new WgcFrameSource(game.Hwnd, () => reg);
        var tee = new TeeSink(overlay!, sink);
        using var pipe2 = new SubtitlePipeline(src2, ocr, chain, tee, "fake", new LatencyStats(), () => Environment.TickCount64, regionProvider: () => reg);

        var before = await OcrScreenCrop(ocr, game.Hwnd, reg);       // English readable before the overlay shows anything
        Console.WriteLine($"before: {before}");
        Assert.Contains("east", before + " " + string.Join(' ', FakeGameWindow.Lines), StringComparison.OrdinalIgnoreCase);
        var englishWords = FakeGameWindow.Lines.SelectMany(l => l.Split(' ')).Where(w => w.Length >= 5).Select(w => w.ToLowerInvariant()).Distinct().ToArray();
        Assert.Contains(englishWords, w => before.ToLowerInvariant().Contains(w));

        pipe2.Start();
        Assert.True(await WaitUntil(() => sink.Shown, 15000), "çeviri gösterilmedi");
        Assert.NotNull(sink.Box);
        await Task.Delay(1200);
        SetForegroundWindow(game.Hwnd);
        await Task.Delay(500);
        var after = (await OcrScreenCrop(ocr, game.Hwnd, reg)).ToLowerInvariant();
        Console.WriteLine($"after: {after}");
        Assert.DoesNotContain(englishWords, w => after.Contains(w));
        Assert.Contains("hemen", after);

        // belt-and-braces: with the default affinity the overlay must be invisible to screen capture
        overlay!.Dispatcher.Invoke(() => overlay.ExcludeFromCapture = true);
        await Task.Delay(500);
        var excluded = (await OcrScreenCrop(ocr, game.Hwnd, reg)).ToLowerInvariant();
        Console.WriteLine($"excluded: {excluded}");
        Assert.Contains(englishWords, w => excluded.Contains(w));
        overlay.Dispatcher.InvokeShutdown();
    }

    private sealed class TeeSink(OverlayWindow o, Sink s) : ISubtitleSink
    {
        public void Show(string? sp, string t, TranslationSource src, RegionF? box = null) { o.Show(sp, t, src, box); s.Show(sp, t, src, box); }
        public void Clear() => o.Clear();
    }

    /// <summary>Real screen capture (CopyFromScreen) of the detected region, then OCR.</summary>
    private static async Task<string> OcrScreenCrop(WindowsOcrEngine ocr, IntPtr hwnd, RegionF r)
    {
        DwmGetWindowAttribute(hwnd, 9, out var fr, Marshal.SizeOf<RECT>());
        int fw = fr.Right - fr.Left, fh = fr.Bottom - fr.Top;
        int x = fr.Left + (int)(r.X * fw), y = fr.Top + (int)(r.Y * fh), w = (int)(r.W * fw), h = (int)(r.H * fh);
        using var bmp = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(fr.Left, fr.Top, 0, 0, new Size(fw, fh));
        bmp.Save(Path.Combine(Path.GetTempPath(), $"oa_shot_{Environment.TickCount64}.png"));
        using var bmp0 = bmp.Clone(new Rectangle(x - fr.Left, y - fr.Top, w, h), PixelFormat.Format32bppArgb);
        var data = bmp0.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp0.UnlockBits(data);
        var res = await ocr.ReadAsync(new Frame(w, h, data.Stride, bytes, 0), CancellationToken.None);
        return string.Join(" ", res.Lines.Select(l => l.Text));
    }
}
