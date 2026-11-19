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
    private (IReadOnlyList<ShownUtterance> Items, RegionF? Box, int Lines, bool Top)? _current;

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
        _current = null;
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
        Render();
    }

    private bool GameInForeground()
    {
        var fg = GetForegroundWindow();
        return fg == _game || fg == new WindowInteropHelper(this).Handle;
    }

    public void Show(string? speaker, string text, TranslationSource source, RegionF? box = null, int lineCount = 1) =>
        Show([new ShownUtterance(speaker, text, source)], box, lineCount, false);

    /// <summary>All current utterances, stacked in on-screen order inside one bubble group (each with its own coloured speaker name).</summary>
    public void Show(IReadOnlyList<ShownUtterance> utterances, RegionF? box = null, int lineCount = 1, bool anchorTop = false)
    {
        Dispatcher.InvokeAsync(() => { _current = (utterances, box, lineCount, anchorTop); Render(); });
    }

    public static void Fill(TextBlock tb, IReadOnlyList<ShownUtterance> items)
    {
        tb.Inlines.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            var u = items[i];
            if (i > 0) tb.Inlines.Add(new System.Windows.Documents.LineBreak());
            if (!string.IsNullOrEmpty(u.Speaker))
                tb.Inlines.Add(new System.Windows.Documents.Run(u.Speaker + ": ")
                    { Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5)), FontWeight = FontWeights.Bold });
            tb.Inlines.Add(new System.Windows.Documents.Run(u.Text)
                { FontStyle = u.Source == TranslationSource.Fallback ? FontStyles.Italic : FontStyles.Normal });
        }
    }

    /// <summary>Applies a changed style (e.g. the cover toggle) and redraws the current line.</summary>
    public void SetStyle(OverlayStyle style)
    {
        _style = style;
        Render();
    }

    public (DipRect Actual, System.Windows.Size Desired, DipRect Screen) BoxMetrics()
    {
        var o = Box.TranslatePoint(new Point(0, 0), this);
        return (new DipRect(o.X, o.Y, Box.ActualWidth, Box.ActualHeight), new System.Windows.Size(Box.DesiredSize.Width - Box.Margin.Left - Box.Margin.Right, Box.DesiredSize.Height - Box.Margin.Top - Box.Margin.Bottom), new DipRect(0, 0, ActualWidth, ActualHeight));
    }

    private void Render()
    {
        if (_current is not { } c) return;
        bool cover = _style.CoverOriginal && c.Box is { W: > 0, H: > 0 } && _haveFrame;
        var dpi = VisualTreeHelper.GetDpi(this);
        Text.FontSize = _style.FontSize;
        Fill(Text, c.Items);
        double op = cover ? _style.CoverOpacity : _style.BackgroundOpacity;
        Box.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(op, 0, 1) * 255), 0, 0, 0));
        Box.Visibility = Visibility.Visible;
        if (!cover)
        {
            Box.MinWidth = 0; Box.MinHeight = 0;
            Text.VerticalAlignment = VerticalAlignment.Top;
            Box.HorizontalAlignment = HorizontalAlignment.Center; Box.VerticalAlignment = VerticalAlignment.Bottom;
            Box.Margin = new Thickness(0, 0, 0, ActualHeight * _style.BottomMargin);
            return;
        }
        var eng = OverlayMapping.FrameBoxToOverlayDips(c.Box!, _frameW, _frameH, _frameX, _frameY, _ovX, _ovY, dpi.DpiScaleX, dpi.DpiScaleY);
        // Same element/style as the bar; it only grows to hide the English (min size), never limited or clipped.
        Box.MinWidth = Math.Min(eng.Width + 2 * CoverLayout.Pad, ActualWidth);
        Box.MinHeight = Math.Min(eng.Height + 2 * CoverLayout.Pad, ActualHeight);
        Text.VerticalAlignment = VerticalAlignment.Center;
        Box.HorizontalAlignment = HorizontalAlignment.Left; Box.VerticalAlignment = VerticalAlignment.Top;
        Box.Margin = new Thickness(0);
        Box.Measure(new System.Windows.Size(ActualWidth, ActualHeight));
        var d = Box.DesiredSize;
        var (x, y) = CoverLayout.Place(d.Width, d.Height, eng, ActualWidth, ActualHeight, c.Top);
        Box.Margin = new Thickness(x, y, 0, 0);
        Box.UpdateLayout();
    }

    public void Clear()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _current = null;
            Box.Visibility = Visibility.Collapsed;
            Text.Inlines.Clear();
        });
    }
}
