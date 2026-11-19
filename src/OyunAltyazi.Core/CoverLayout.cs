namespace OyunAltyazi.Core;

public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>Result of measuring wrapped text: widest line, total height, line count (all in DIPs).</summary>
public readonly record struct TextMetrics(double Width, double Height, int Lines);

/// <summary>Measures speaker+text at a font size, wrapped to maxWidth DIPs.</summary>
public delegate TextMetrics TextMeasure(string? speaker, string text, double fontSize, double maxWidth);

public readonly record struct CoverFit(DipRect Box, double FontSize, int Lines);

public static class CoverLayout
{
    public const double MinFont = 14, PadH = 6, PadV = 3;

    /// <summary>
    /// Places the Turkish text over the English box. Start font = line height*0.85; width may grow symmetrically up to
    /// <paramref name="maxWidth"/>; at most lineCount+1 lines; otherwise font shrinks (1 DIP steps, min 14).
    /// The returned box always contains the English box (plus padding), centred on it.
    /// </summary>
    public static CoverFit Fit(DipRect english, int lineCount, string text, string? speaker, double maxWidth, TextMeasure measure)
    {
        lineCount = Math.Max(1, lineCount);
        double minW = english.Width + 2 * PadH;
        double boxMaxW = Math.Max(maxWidth, minW);
        double textMaxW = boxMaxW - 2 * PadH;
        double fs = Math.Max(MinFont, Math.Round(english.Height / lineCount * 0.85));
        TextMetrics m;
        while (true)
        {
            m = measure(speaker, text, fs, textMaxW);
            if (m.Lines <= lineCount + 1 || fs <= MinFont) break;
            fs -= 1;
        }
        double w = Math.Min(boxMaxW, Math.Max(minW, Math.Ceiling(m.Width) + 2 * PadH));
        double h = Math.Max(english.Height + 2 * PadV, Math.Ceiling(m.Height) + 2 * PadV);
        return new CoverFit(new DipRect(english.CenterX - w / 2, english.CenterY - h / 2, w, h), fs, m.Lines);
    }
}

public static class OverlayMapping
{
    /// <summary>
    /// Maps a box given as fractions (0..1) of the captured frame to overlay DIPs.
    /// frame px = fraction * frame size; screen px = frame origin + frame px; overlay DIPs = (screen px - overlay origin) / dpiScale.
    /// All "px" values are physical screen pixels.
    /// </summary>
    public static DipRect FrameBoxToOverlayDips(RegionF box, int framePxW, int framePxH, int frameScreenX, int frameScreenY,
                                                int overlayScreenX, int overlayScreenY, double dpiScaleX, double dpiScaleY)
    {
        double sx = frameScreenX + box.X * framePxW, sy = frameScreenY + box.Y * framePxH;
        return new DipRect((sx - overlayScreenX) / dpiScaleX, (sy - overlayScreenY) / dpiScaleY,
                           box.W * framePxW / dpiScaleX, box.H * framePxH / dpiScaleY);
    }
}
