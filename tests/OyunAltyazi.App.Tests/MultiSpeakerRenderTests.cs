using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Tests;

/// <summary>Renders the overlay's utterance stack offscreen: two speaker lines, each with its own teal name, stacked in order.</summary>
public class MultiSpeakerRenderTests
{
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!; Exception? err = null;
        var th = new Thread(() => { try { result = f(); } catch (Exception e) { err = e; } });
        th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
        if (err is not null) throw err;
        return result;
    }

    private static readonly ShownUtterance[] Two =
    [
        new("Price", "Yukarı çık, hemen.", TranslationSource.Translator),
        new("Gaz", "Anlaşıldı, ilerliyorum.", TranslationSource.Translator),
    ];

    [Fact]
    public void Two_speakers_render_as_two_stacked_lines_each_with_a_teal_name()
    {
        var (desc, tealTop, tealBottom, height) = OnSta(() =>
        {
            var tb = new TextBlock { FontSize = 32, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 900 };
            OverlayWindow.Fill(tb, Two);
            tb.Measure(new Size(900, double.PositiveInfinity)); tb.Arrange(new Rect(tb.DesiredSize));
            var bmp = new RenderTargetBitmap((int)tb.DesiredSize.Width + 4, (int)tb.DesiredSize.Height + 4, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight)); }
            bmp.Render(dv); bmp.Render(tb);
            int w = bmp.PixelWidth, h = bmp.PixelHeight; var px = new byte[w * h * 4]; bmp.CopyPixels(px, w * 4, 0);
            // rows containing teal (0x4F,0xD1,0xC5) pixels
            var rows = new List<int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4; byte b = px[i], g = px[i + 1], r = px[i + 2];
                    if (Math.Abs(r - 0x4F) < 40 && Math.Abs(g - 0xD1) < 40 && Math.Abs(b - 0xC5) < 40) { rows.Add(y); break; }
                }
            var desc = tb.Inlines.Select(i => i is Run r ? (r.Text, ((SolidColorBrush)r.Foreground).Color) : ("<br>", Colors.Transparent)).ToList();
            return (desc, rows.Count == 0 ? -1 : rows.Min(), rows.Count == 0 ? -1 : rows.Max(), h);
        });

        Assert.Equal(["Price: ", "Yukarı çık, hemen.", "<br>", "Gaz: ", "Anlaşıldı, ilerliyorum."], desc.Select(d => d.Item1).ToArray());
        var teal = Color.FromRgb(0x4F, 0xD1, 0xC5);
        Assert.Equal(teal, desc[0].Item2);
        Assert.Equal(teal, desc[3].Item2);
        Assert.NotEqual(teal, desc[1].Item2);
        // rendered: teal ink exists in the upper half (Price) and in the lower half (Gaz) - two separate lines
        Assert.True(tealTop >= 0, "teal name not rendered");
        Assert.True(tealTop < height / 2 && tealBottom > height / 2, $"names not on two stacked lines: {tealTop}..{tealBottom} of {height}");
    }
}
