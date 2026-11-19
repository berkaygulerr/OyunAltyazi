namespace OyunAltyazi.Core;

public record RegionF(double X, double Y, double W, double H);
public record OcrLine(string Text, double Top, double Height, double Left = 0, double Width = 1);
public record OcrResult(IReadOnlyList<OcrLine> Lines);
public record SubtitleText(string? Speaker, string Text);
public sealed record Frame(int Width, int Height, int Stride, byte[] Bgra, long TimestampMs);
