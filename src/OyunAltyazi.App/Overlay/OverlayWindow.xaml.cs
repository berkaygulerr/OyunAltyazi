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
    private Rect _capDip = Rect.Empty;
    private (string? Speaker, string Text, TranslationSource Source, RegionF? Box)? _current;


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
        // OCR boxes are fractions of the whole captured window (DWM frame); the overlay sits on the client area.
        _capDip = new Rect(0, 0, w / dpi.DpiScaleX, h / dpi.DpiScaleY);
        if (DwmGetWindowAttribute(_game, 9, out var fr, Marshal.SizeOf<RECT>()) == 0 && fr.Right > fr.Left && fr.Bottom > fr.Top)
            _capDip = new Rect((fr.Left - pt.X) / dpi.DpiScaleX, (fr.Top - pt.Y) / dpi.DpiScaleY,
                               (fr.Right - fr.Left) / dpi.DpiScaleX, (fr.Bottom - fr.Top) / dpi.DpiScaleY);
        Box.MaxWidth = w / dpi.DpiScaleX * 0.8;
        Box.Margin = new Thickness(0, 0, 0, h / dpi.DpiScaleY * _style.BottomMargin);
    }

    private bool GameInForeground()
    {
        var fg = GetForegroundWindow();
        return fg == _game || fg == new WindowInteropHelper(this).Handle;
    }

    public void Show(string? speaker, string text, TranslationSource source, RegionF? box = null)
    {
        Dispatcher.InvokeAsync(() => { _current = (speaker, text, source, box); Render(); });
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

    private void Render()
    {
        if (_current is not { } c) return;
        bool cover = _style.CoverOriginal && c.Box is { W: > 0, H: > 0 } && !_capDip.IsEmpty && _capDip.Width > 0;
        if (!cover)
        {
            CoverBox.Visibility = Visibility.Collapsed;
            Fill(Text, c.Speaker, c.Text, c.Source);
            Box.Visibility = Visibility.Visible;
            return;
        }
        Box.Visibility = Visibility.Collapsed;
        var b = c.Box!;
        double ox = _capDip.X + b.X * _capDip.Width, oy = _capDip.Y + b.Y * _capDip.Height;
        double ow = b.W * _capDip.Width, oh = b.H * _capDip.Height;
        const double pad = 6;
        double maxW = Math.Max(ow + 2 * pad, ActualWidth * 0.9);
        Fill(CoverText, c.Speaker, c.Text, c.Source);
        CoverBox.Visibility = Visibility.Visible; // collapsed elements measure to 0
        CoverBox.MaxWidth = maxW;
        CoverBox.MinWidth = ow + 2 * pad;
        // auto-fit: biggest font (min 14) whose wrapped height stays within the original block's height
        double fs = Math.Clamp(oh * 0.85, 14, 60);
        for (; ; fs -= 1)
        {
            CoverText.FontSize = fs;
            CoverBox.Measure(new Size(maxW, double.PositiveInfinity));
            if (fs <= 14 || CoverBox.DesiredSize.Height <= oh + 2 * pad + 4) break;
        }
        var sz = CoverBox.DesiredSize;
        double h = Math.Max(sz.Height, oh + 2 * pad);
        CoverBox.MinHeight = h;
        double left = Math.Clamp(ox + ow / 2 - sz.Width / 2, 0, Math.Max(0, ActualWidth - sz.Width));
        double top = Math.Clamp(oy + oh / 2 - h / 2, 0, Math.Max(0, ActualHeight - h));
        Canvas.SetLeft(CoverBox, left); Canvas.SetTop(CoverBox, top);
        CoverBox.VerticalAlignment = VerticalAlignment.Top; CoverBox.HorizontalAlignment = HorizontalAlignment.Left;
        CoverText.VerticalAlignment = VerticalAlignment.Center;
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
