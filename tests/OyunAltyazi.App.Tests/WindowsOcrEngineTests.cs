using System.Drawing;
using System.Drawing.Imaging;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

public class WindowsOcrEngineTests
{
    [Fact]
    public async Task Reads_rendered_text()
    {
        var ocr = WindowsOcrEngine.TryCreate();
        if (ocr is null) return; // İngilizce OCR paketi yok: atlandı (xunit 2.9 dinamik Skip desteklemez)
        using var bmp = new Bitmap(800, 80, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Segoe UI", 32, GraphicsUnit.Pixel);
            g.DrawString("Get to the chopper now", f, Brushes.White, 10, 20);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, 800, 80), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * 80];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        var r = await ocr.ReadAsync(new Frame(800, 80, data.Stride, bytes, 0), CancellationToken.None);
        Assert.Equal("get to the chopper now", TextKey.Canon(string.Join(" ", r.Lines.Select(l => l.Text))));
    }
}
