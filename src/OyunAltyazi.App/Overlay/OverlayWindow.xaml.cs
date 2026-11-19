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
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const uint SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private IntPtr _game;
    private OverlayStyle _style = new();
    private bool _userHidden;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
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
        Box.MaxWidth = w / dpi.DpiScaleX * 0.8;
        Box.Margin = new Thickness(0, 0, 0, h / dpi.DpiScaleY * _style.BottomMargin);
    }

    private bool GameInForeground()
    {
        var fg = GetForegroundWindow();
        return fg == _game || fg == new WindowInteropHelper(this).Handle;
    }

    public void Show(string? speaker, string text, TranslationSource source)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Text.Inlines.Clear();
            if (!string.IsNullOrEmpty(speaker))
                Text.Inlines.Add(new System.Windows.Documents.Run(speaker + ": ")
                    { Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5)), FontWeight = FontWeights.Bold });
            Text.Inlines.Add(new System.Windows.Documents.Run(text)
                { FontStyle = source == TranslationSource.Fallback ? FontStyles.Italic : FontStyles.Normal });
            Box.Visibility = Visibility.Visible;
        });
    }

    public void Clear()
    {
        Dispatcher.InvokeAsync(() =>
        {
            Box.Visibility = Visibility.Collapsed;
            Text.Inlines.Clear();
        });
    }
}
