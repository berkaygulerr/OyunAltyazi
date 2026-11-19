namespace OyunAltyazi.Core;

public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public static class CoverLayout
{
    public const double DefaultFont = 32, Pad = 6;

    /// <summary>
    /// Position only: element centred horizontally on the English box, bottom at English bottom + Pad (grows upward),
    /// clamped so it stays fully inside the overlay (when it fits).
    /// </summary>
    public static (double X, double Y) Place(double width, double height, DipRect english, double overlayW, double overlayH, bool anchorTop = false)
    {
        double x = english.CenterX - width / 2;
        // Bottom-anchored games grow upward (bubble bottom pinned); top-anchored ones grow downward (bubble top pinned).
        double y = anchorTop ? english.Y - Pad : english.Bottom + Pad - height;
        x = Math.Max(0, Math.Min(x, overlayW - width));
        y = Math.Max(0, Math.Min(y, overlayH - height));
        return (x, y);
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
