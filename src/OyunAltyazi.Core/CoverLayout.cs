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
    public const double MinFont = 24, DefaultFont = 32, PadH = 12, PadV = 6, MaxLines = 3, LineHeightFactor = 1.15;

    /// <summary>
    /// Places the Turkish text over the English box with OUR font size (fontSize, not derived from the English).
    /// The font only shrinks (1 DIP steps, floor 24) when the text needs more than 3 lines within maxWidth.
    /// Box = max(English+pad, text+pad), centred horizontally on the English box, bottom aligned to the English bottom (+pad)
    /// so a taller box grows upward; clamped inside bounds when given (never at the cost of covering the English box).
    /// </summary>
    public static CoverFit Fit(DipRect english, string text, string? speaker, double maxWidth, double fontSize, TextMeasure measure, DipRect? bounds = null)
    {
        double minW = english.Width + 2 * PadH;
        double boxMaxW = Math.Max(maxWidth, minW);
        double textMaxW = boxMaxW - 2 * PadH;
        double fs = fontSize;
        double floor = Math.Min(MinFont, fs);
        TextMetrics m;
        while (true)
        {
            m = measure(speaker, text, fs, textMaxW);
            if (m.Lines <= MaxLines || fs <= floor) break;
            fs -= 1;
        }
        double w = Math.Min(boxMaxW, Math.Max(minW, Math.Ceiling(m.Width) + 2 * PadH));
        double h = Math.Max(english.Height + 2 * PadV, Math.Ceiling(m.Height) + 2 * PadV);
        double x = english.CenterX - w / 2;
        double y = english.Bottom + PadV - h;
        if (bounds is { } r)
        {
            x = Math.Max(r.X, Math.Min(x, r.Right - w));
            y = Math.Max(r.Y, Math.Min(y, r.Bottom - h));
            x = Math.Min(x, english.X); x = Math.Max(x, english.Right - w);
            y = Math.Min(y, english.Y); y = Math.Max(y, english.Bottom - h);
        }
        return new CoverFit(new DipRect(x, y, w, h), fs, m.Lines);
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
