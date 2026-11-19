using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OyunAltyazi.Core;

namespace OyunAltyazi.App.UI;

public sealed partial class RegionSelector : Window
{
    private Point _start;
    private bool _drag;
    private RegionF _region;
    public RegionF Result => _region;

    public RegionSelector(Core.Frame snapshot, RegionF current)
    {
        InitializeComponent();
        double scale = Math.Min(1.0, 1000.0 / snapshot.Width);
        Surface.Width = snapshot.Width * scale;
        Surface.Height = snapshot.Height * scale;
        Img.Width = Surface.Width; Img.Height = Surface.Height;
        var bmp = BitmapSource.Create(snapshot.Width, snapshot.Height, 96, 96, PixelFormats.Bgr32, null, snapshot.Bgra, snapshot.Stride);
        bmp.Freeze();
        Img.Source = bmp;
        _region = current;
        Apply(current);
    }

    private void Apply(RegionF r)
    {
        Canvas.SetLeft(Sel, r.X * Surface.Width);
        Canvas.SetTop(Sel, r.Y * Surface.Height);
        Sel.Width = r.W * Surface.Width;
        Sel.Height = r.H * Surface.Height;
    }

    private void Down(object s, MouseButtonEventArgs e) { _start = e.GetPosition(Surface); _drag = true; Surface.CaptureMouse(); }

    private void Move(object s, MouseEventArgs e)
    {
        if (_drag) Update(e.GetPosition(Surface));
    }

    private void Up(object s, MouseButtonEventArgs e)
    {
        if (!_drag) return;
        Update(e.GetPosition(Surface));
        _drag = false;
        Surface.ReleaseMouseCapture();
    }

    private void Update(Point p)
    {
        double x1 = Math.Clamp(Math.Min(_start.X, p.X), 0, Surface.Width), x2 = Math.Clamp(Math.Max(_start.X, p.X), 0, Surface.Width);
        double y1 = Math.Clamp(Math.Min(_start.Y, p.Y), 0, Surface.Height), y2 = Math.Clamp(Math.Max(_start.Y, p.Y), 0, Surface.Height);
        if (x2 - x1 < 4 || y2 - y1 < 4) return;
        _region = new RegionF(x1 / Surface.Width, y1 / Surface.Height, (x2 - x1) / Surface.Width, (y2 - y1) / Surface.Height);
        Apply(_region);
    }

    private void Save_Click(object s, RoutedEventArgs e) { DialogResult = true; }
}
