using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OyunAltyazi.Translation;

namespace OyunAltyazi.App.Tests;

public sealed class ReviewWindowTests
{
    [Fact]
    public void Workshop_opens_edits_approves_filters_and_enables_package_export()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                using var cache = new SqliteTranslationCache(":memory:");
                cache.Put("test-game.exe", "Wait by the brldge.", "Köprü bekleyin.", "ct2");
                cache.Put("test-game.exe", "Stay where you are.", "Orada kal.", "ct2");
                var window = new ReviewWindow(cache, "test-game.exe");
                var rows = (ListBox)window.FindName("Rows");
                var export = (Button)window.FindName("ExportButton");
                Assert.Equal(2, rows.Items.Count);
                Assert.False(export.IsEnabled);
                rows.SelectedItem = rows.Items.Cast<ReviewRow>().Single(r => r.Original.Contains("brldge"));
                ((TextBox)window.FindName("SourceBox")).Text = "Wait by the bridge.";
                ((TextBox)window.FindName("TurkishBox")).Text = "Köprünün yanında bekle.";
                var approve = ((Grid)window.FindName("Editor")).Children.OfType<DockPanel>().Single().Children.OfType<Button>().First();
                approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(export.IsEnabled);
                ((CheckBox)window.FindName("ApprovedOnly")).IsChecked = true;
                Assert.Single(rows.Items);
                Assert.Contains("Wait by the bridge.", cache.ExportReviewedPack("test-game.exe")!);
                if (Environment.GetEnvironmentVariable("OA_REVIEW_SHOT") is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                    ((CheckBox)window.FindName("ApprovedOnly")).IsChecked = false;
                    foreach (var (width, height) in new[] { (900, 600), (700, 515) })
                    {
                        var root = (FrameworkElement)window.Content;
                        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        var background = new DrawingVisual();
                        using (var context = background.RenderOpen()) context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                        bitmap.Render(background); bitmap.Render(root);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(folder, $"paket-atolyesi-{width}.png"));
                        png.Save(file);
                    }
                }
                window.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(12)), "Atölye testi zaman aşımına uğradı.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
