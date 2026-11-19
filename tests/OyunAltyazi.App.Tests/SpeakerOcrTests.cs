using System.Drawing;
using System.Drawing.Imaging;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.Core;
using Xunit.Abstractions;

namespace OyunAltyazi.App.Tests;

public class SpeakerOcrTests(ITestOutputHelper output)
{
    private static Frame Render(string name, string text, int fontPx, bool noisy, bool boldName = true)
    {
        int w = 1100, h = fontPx * 2 + 30;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(24, 26, 30));
            if (noisy)
            {
                var rnd = new Random(7);
                for (int i = 0; i < 400; i++)
                    using (var br = new SolidBrush(Color.FromArgb(rnd.Next(30, 110), rnd.Next(30, 110), rnd.Next(30, 110))))
                        g.FillRectangle(br, rnd.Next(w), rnd.Next(h), rnd.Next(5, 60), rnd.Next(3, 20));
            }
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fb = new Font("Segoe UI", fontPx, boldName ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var fr = new Font("Segoe UI", fontPx, GraphicsUnit.Pixel);
            using var teal = new SolidBrush(Color.FromArgb(0x3F, 0xC8, 0xC8));
            var label = name + ":";
            g.DrawString(label, fb, teal, 10, 10);
            var sz = g.MeasureString(label + " ", fb, 2000, StringFormat.GenericTypographic); sz = new SizeF(sz.Width + fontPx * 0.3f, sz.Height);
            g.DrawString(text, fr, Brushes.White, 10 + sz.Width, 10);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return new Frame(w, h, data.Stride, bytes, 0);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var n in new[] { "Valeria", "Laswell", "Price", "Ghost", "Farah", "Kyle Garrick" })
            foreach (var px in new[] { 22, 32, 44 })
                foreach (var noisy in new[] { false })
                    yield return new object[] { n, px, noisy };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Speaker_name_comes_out_whole(string name, int px, bool noisy)
    {
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return;
        var r = await ocr.ReadAsync(Render(name, "We need to move before they find us.", px, noisy), CancellationToken.None);
        var cleaned = TextCleaner.Clean(r);
        output.WriteLine($"{name}/{px}/{noisy}: raw=[{string.Join(" | ", r.Lines.Select(l => $"{l.Text}@{l.Left:F3}"))}] -> spk={cleaned?.Speaker} txt={cleaned?.Text}");
        Assert.Equal(name, cleaned?.Speaker);
        Assert.StartsWith("We need to move", cleaned?.Text);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Noisy_background_never_yields_partial_name(string name, int px, bool _)
    {
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return;
        var r = await ocr.ReadAsync(Render(name, "We need to move before they find us.", px, true), CancellationToken.None);
        var cleaned = TextCleaner.Clean(r);
        output.WriteLine($"noisy {name}/{px}: spk={cleaned?.Speaker} txt={cleaned?.Text}");
        // whole name or no name; a proper suffix such as 'ria' / 'aleria' must never be shown
        if (cleaned?.Speaker is { } sp)
            Assert.False(name.EndsWith(sp, StringComparison.Ordinal) && sp != name, $"partial speaker '{sp}'");
    }
}
