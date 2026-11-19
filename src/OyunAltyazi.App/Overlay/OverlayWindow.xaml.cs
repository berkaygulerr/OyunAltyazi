using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.Overlay;

public sealed partial class OverlayWindow : Window, ISubtitleSink
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint SWP_NOACTIVATE = 0x10, WDA_EXCLUDEFROMCAPTURE = 0x11;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _game;
    private OverlayStyle _style = new();
    private bool _userHidden, _excludeFromCapture = true;

    /// <summary>Default true: the overlay is invisible to screen capture (so it can never be OCR'd). Tests turn it off to screenshot it.</summary>
    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set { _excludeFromCapture = value; ApplyAffinity(); }
    }

    private void ApplyAffinity()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, _excludeFromCapture ? WDA_EXCLUDEFROMCAPTURE : 0); // best effort (Win10 2004+)
    }
    // The captured game window (what the OCR boxes are relative to), in DIPs relative to the overlay's top-left.
    private bool _haveFrame;
    private int _frameX, _frameY, _frameW, _frameH, _ovX, _ovY; // physical px: captured frame rect and overlay origin
    private (string? Speaker, string Text, TranslationSource Source, RegionF? Box, int Lines)? _current;

    /// <summary>Region the OCR reads (window fractions); its width caps how wide the Turkish box may grow.</summary>
    public Func<RegionF>? RegionProvider { get; set; }


    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ApplyAffinity();
        };
        _timer.Tick += (_, _) => Align();
    }

    /// <summary>User toggle (Ctrl+Shift+H): hides the overlay regardless of the game state.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set { _userHidden = value; Align(); }
    }

    public void Attach(IntPtr gameHwnd, OverlayStyle style)
    {
        _game = gameHwnd;
        _style = style;
        Text.FontSize = style.FontSize;
        _current = null;
        CoverBox.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(style.CoverOpacity, 0, 1) * 255), 0x12, 0x12, 0x12));
        Box.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(style.BackgroundOpacity, 0, 1) * 255), 0, 0, 0));
        new WindowInteropHelper(this).EnsureHandle();
        _timer.Start();
        Align();
    }

    public void Detach()
    {
        _timer.Stop();
        _game = IntPtr.Zero;
        Clear();
        Hide();
    }

    private void Align()
    {
        var self = new WindowInteropHelper(this).Handle;
        if (self == IntPtr.Zero) return;
        if (_game == IntPtr.Zero || !IsWindow(_game) || IsIconic(_game) || _userHidden || !GameInForeground())
        {
            if (IsVisible) Hide();
            return;
        }
        if (!GetClientRect(_game, out var rc)) return;
        var pt = new POINT();
        ClientToScreen(_game, ref pt);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) { if (IsVisible) Hide(); return; }
        if (!IsVisible) Show();
        SetWindowPos(self, HWND_TOPMOST, pt.X, pt.Y, w, h, SWP_NOACTIVATE);
        var dpi = VisualTreeHelper.GetDpi(this);
        // OCR boxes are fractions of the whole captured window (DWM extended frame bounds); the overlay sits on the client area.
        // Both rects are kept in physical px and mapped with OverlayMapping (DPI-correct).
        (_frameX, _frameY, _frameW, _frameH, _ovX, _ovY) = (pt.X, pt.Y, w, h, pt.X, pt.Y);
        if (DwmGetWindowAttribute(_game, 9, out var fr, Marshal.SizeOf<RECT>()) == 0 && fr.Right > fr.Left && fr.Bottom > fr.Top)
            (_frameX, _frameY, _frameW, _frameH) = (fr.Left, fr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top);
        _haveFrame = true;
        Box.MaxWidth = w / dpi.DpiScaleX * 0.8;
        Box.Margin = new Thickness(0, 0, 0, h / dpi.DpiScaleY * _style.BottomMargin);
    }

    private bool GameInForeground()
    {
        var fg = GetForegroundWindow();
        return fg == _game || fg == new WindowInteropHelper(this).Handle;
    }

    public void Show(string? speaker, string text, TranslationSource source, RegionF? box = null, int lineCount = 1)
    {
        Dispatcher.InvokeAsync(() => { _current = (speaker, text, source, box, lineCount); Render(); });
    }

    private static void Fill(TextBlock tb, string? speaker, string text, TranslationSource source)
    {
        tb.Inlines.Clear();
        if (!string.IsNullOrEmpty(speaker))
            tb.Inlines.Add(new System.Windows.Documents.Run(speaker + ": ")
                { Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5)), FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new System.Windows.Documents.Run(text)
            { FontStyle = source == TranslationSource.Fallback ? FontStyles.Italic : FontStyles.Normal });
    }

    /// <summary>Applies a changed style (e.g. the cover toggle) and redraws the current line.</summary>
    public void SetStyle(OverlayStyle style)
    {
        _style = style;
        CoverBox.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(style.CoverOpacity, 0, 1) * 255), 0x12, 0x12, 0x12));
        Render();
    }

    private TextMetrics MeasureText(string? speaker, string text, double fs, double maxWidth)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(CoverText.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Make(string t) => new(t, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, fs, Brushes.White, ppd);
        var full = Make((string.IsNullOrEmpty(speaker) ? "" : speaker + ": ") + text);
        full.MaxTextWidth = Math.Max(1, maxWidth);
        full.LineHeight = fs * CoverLayout.LineHeightFactor;
        var oneT = Make("Ag"); oneT.LineHeight = fs * CoverLayout.LineHeightFactor; double one = oneT.Height;
        return new TextMetrics(full.Width + 2, full.Height, Math.Max(1, (int)Math.Round(full.Height / one)));
    }

    private void Render()
    {
        if (_current is not { } c) return;
        bool cover = _style.CoverOriginal && c.Box is { W: > 0, H: > 0 } && _haveFrame;
        if (!cover)
        {
            CoverBox.Visibility = Visibility.Collapsed;
            Fill(Text, c.Speaker, c.Text, c.Source);
            Box.Visibility = Visibility.Visible;
            return;
        }
        Box.Visibility = Visibility.Collapsed;
        var dpi = VisualTreeHelper.GetDpi(this);
        var eng = OverlayMapping.FrameBoxToOverlayDips(c.Box!, _frameW, _frameH, _frameX, _frameY, _ovX, _ovY, dpi.DpiScaleX, dpi.DpiScaleY);
        double maxW = ActualWidth * 0.95;
        if (RegionProvider?.Invoke() is { W: > 0 } rg) maxW = Math.Min(maxW, rg.W * _frameW / dpi.DpiScaleX);
        var fit = CoverLayout.Fit(eng, c.Text, c.Speaker, maxW, _style.FontSize, MeasureText, new DipRect(0, 0, ActualWidth, ActualHeight));
        Fill(CoverText, c.Speaker, c.Text, c.Source);
        CoverText.FontSize = fit.FontSize;
        CoverText.LineHeight = fit.FontSize * CoverLayout.LineHeightFactor;
        var b = fit.Box;
        CoverBox.Padding = new Thickness(CoverLayout.PadH, CoverLayout.PadV, CoverLayout.PadH, CoverLayout.PadV);
        CoverBox.Width = b.Width; CoverBox.Height = b.Height;
        CoverText.VerticalAlignment = VerticalAlignment.Center;
        CoverBox.CornerRadius = new CornerRadius(8);
        Canvas.SetLeft(CoverBox, b.X); Canvas.SetTop(CoverBox, b.Y);
        CoverBox.Visibility = Visibility.Visible;
    }

    public void Clear()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _current = null;
            Box.Visibility = Visibility.Collapsed;
            CoverBox.Visibility = Visibility.Collapsed;
            Text.Inlines.Clear();
            CoverText.Inlines.Clear();
        });
    }
}
