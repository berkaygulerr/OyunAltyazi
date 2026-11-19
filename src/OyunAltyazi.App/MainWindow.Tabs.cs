using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.UI;
using OyunAltyazi.Core;

namespace OyunAltyazi.App;

// Sağ sütundaki sekmelerin (Okuma Alanı, Altyazı Görünümü, Çeviri, Sistem) mantığı.
public sealed partial class MainWindow
{
    private bool _loadingControls;

    private static string LogDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi", "logs");

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.OriginalSource != Tabs) return;
        if (Tabs.SelectedIndex == 0) RefreshPreview();
        if (Tabs.SelectedIndex == 2) UpdateCacheCount();
    }

    /// <summary>Profilin değiştirilmiş halini kaydeder (profil yoksa oluşturur).</summary>
    private void SaveProfile(GameProfile p)
    {
        _profile = p;
        var s = _app.Settings.Load();
        var profiles = new Dictionary<string, GameProfile>(s.Profiles ?? new(), StringComparer.OrdinalIgnoreCase) { [p.ExeName] = p };
        _app.Settings.Save(s with { Profiles = profiles });
    }

    private void LoadProfileControls()
    {
        _loadingControls = true;
        CoverCheck.IsChecked = _profile?.Overlay.CoverOriginal ?? true;
        FontSlider.Value = _profile?.Overlay.FontSize ?? CoverLayout.DefaultFont;
        OpacitySlider.Value = Math.Round((_profile?.Overlay.BackgroundOpacity ?? new OverlayStyle().BackgroundOpacity) * 20) * 5;
        FontValue.Text = ((int)FontSlider.Value).ToString();
        OpacityValue.Text = $"%{(int)OpacitySlider.Value}";
        var s = _app.Settings.Load();
        ScreenshotCheck.IsChecked = s.ShowInScreenshots;
        DiagCheck.IsChecked = s.DiagnosticLog;
        _loadingControls = false;
        UpdateSubtitlePreview();
        UpdateRegionInfo();
    }

    // ---------- Okuma Alanı ----------
    private Core.Frame? _snapshot;

    private void PreviewRefresh_Click(object sender, RoutedEventArgs e) => RefreshPreview(userAction: true);
    private void SelectRegion_Click(object sender, RoutedEventArgs e) => SelectRegion();

    private void RefreshPreview(bool userAction = false)
    {
        if (_selected is null)
        {
            _snapshot = null;
            PreviewBox.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Text = "Önizleme için sol taraftan bir oyun penceresi seçin.";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        Core.Frame? snap = null;
        try { snap = WgcFrameSource.Snapshot(_selected.Hwnd); } catch (Exception) { }
        if (snap is null)
        {
            if (userAction || _snapshot is null) PreviewPlaceholder.Text = "Pencere görüntüsü alınamadı (küçültülmüş ya da kapanmış olabilir).";
            if (_snapshot is null) { PreviewBox.Visibility = Visibility.Collapsed; PreviewPlaceholder.Visibility = Visibility.Visible; }
            return;
        }
        _snapshot = snap;
        var bmp = BitmapSource.Create(snap.Width, snap.Height, 96, 96, PixelFormats.Bgr32, null, snap.Bgra, snap.Stride);
        bmp.Freeze();
        PreviewImg.Source = bmp;
        PreviewHost.Width = snap.Width; PreviewHost.Height = snap.Height;
        PreviewCanvas.Width = snap.Width; PreviewCanvas.Height = snap.Height;
        PreviewBox.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        DrawRegion();
    }

    private void DrawRegion()
    {
        UpdateRegionInfo();
        if (_snapshot is null || _profile is null) return;
        var r = _profile.Region;
        Canvas.SetLeft(RegionRect, r.X * _snapshot.Width);
        Canvas.SetTop(RegionRect, r.Y * _snapshot.Height);
        RegionRect.Width = r.W * _snapshot.Width;
        RegionRect.Height = r.H * _snapshot.Height;
        RegionRect.StrokeThickness = Math.Max(2, _snapshot.Width / 280.0);
    }

    private void UpdateRegionInfo()
    {
        var r = _profile?.Region;
        RegionInfo.Text = r is null ? "" : string.Create(CultureInfo.InvariantCulture, $"x %{r.X * 100:0} · y %{r.Y * 100:0} · %{r.W * 100:0} × %{r.H * 100:0}");
    }

    private void SetRegion(RegionF r)
    {
        if (_profile is null) return;
        SaveProfile(_profile with { Region = r, AutoDetected = false });
        _app.Settings.Save(_app.Settings.Load() with { LastExe = _profile!.ExeName });
        DrawRegion();
        SetStatus("Bölge kaydedildi.");
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var p = ((string)((Button)sender).Tag).Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        SetRegion(new RegionF(p[0], p[1], p[2], p[3]));
    }

    private void SelectRegion()
    {
        if (_selected is null || _profile is null) return;
        var snap = WgcFrameSource.Snapshot(_selected.Hwnd);
        if (snap is null) { SetStatus("Pencere görüntüsü alınamadı (küçültülmüş ya da kapanmış olabilir)."); return; }
        var dlg = new RegionSelector(snap, _profile.Region) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetRegion(dlg.Result);
        RefreshPreview();
    }

    // ---------- Altyazı Görünümü ----------
    private void FontSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontValue is null) return;
        FontValue.Text = ((int)e.NewValue).ToString();
        UpdateSubtitlePreview();
        if (_loadingControls || _profile is null) return;
        SaveProfile(_profile with { Overlay = _profile.Overlay with { FontSize = e.NewValue } });
        _overlay.SetStyle(_profile.Overlay);
    }

    private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"%{(int)e.NewValue}";
        UpdateSubtitlePreview();
        if (_loadingControls || _profile is null) return;
        SaveProfile(_profile with { Overlay = _profile.Overlay with { BackgroundOpacity = e.NewValue / 100.0 } });
        _overlay.SetStyle(_profile.Overlay);
    }

    private void CoverCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is null) return;
        _profile = _profile with { Overlay = _profile.Overlay with { CoverOriginal = CoverCheck.IsChecked == true } };
        if (_app.Settings.HasProfile(_profile.ExeName)) SaveProfile(_profile);
        _overlay.SetStyle(_profile.Overlay);
    }

    /// <summary>Örnek altyazı balonu: kaplamayla aynı biçim (teal konuşmacı + Türkçe metin).</summary>
    private void UpdateSubtitlePreview()
    {
        if (BubbleText is null || FontSlider is null || OpacitySlider is null) return;
        BubbleText.FontSize = FontSlider.Value * 0.6; // önizleme ölçekli
        BubblePreview.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(OpacitySlider.Value / 100, 0, 1) * 255), 0, 0, 0));
        BubbleText.Inlines.Clear();
        BubbleText.Inlines.Add(new System.Windows.Documents.Run("Price: ") { Foreground = (Brush)FindResource("TealBrush"), FontWeight = FontWeights.Bold });
        BubbleText.Inlines.Add(new System.Windows.Documents.Run("Yukarı çıkın, hemen."));
    }

    // ---------- Çeviri ----------
    private void InitTranslatorCard()
    {
        bool ok = _app.LocalInstalled;
        LocalBadgeText.Text = ok ? "Hazır" : "Kurulu değil";
        LocalBadgeText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "WarnBrush");
        LocalBadge.Background = new SolidColorBrush(ok ? Color.FromArgb(0x26, 0x22, 0xC5, 0x5E) : Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B));
        LocalHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        UpdateTranslatorWarning();
        UpdateCacheCount();
    }

    private void UpdateTranslatorWarning()
    {
        if (TrLocal.IsChecked == true && !_app.LocalInstalled)
        {
            WarningText.Text = "Yerel model kurulu değil: tools\\ct2server\\setup.ps1 çalıştırın (şimdilik Google kullanılıyor).";
            WarningText.Visibility = Visibility.Visible;
        }
        else if (_app.Ocr is not null && WgcFrameSource.IsSupported) WarningText.Visibility = Visibility.Collapsed;
    }

    private void Translator_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loadingControls) return;
        if (Running)
        {
            _loadingControls = true;
            (_app.TranslatorName == "Google" ? TrGoogle : TrLocal).IsChecked = true;
            _loadingControls = false;
            return;
        }
        var choice = TrGoogle.IsChecked == true ? "google_only" : "auto";
        _app.SetTranslator(choice);
        _app.Settings.Save(_app.Settings.Load() with { Translator = choice });
        UpdateTranslatorWarning();
        UpdateStatusDots();
    }

    private void UpdateCacheCount()
    {
        try { CacheCount.Text = _app.Cache.Count().ToString("N0", CultureInfo.GetCultureInfo("tr-TR")); } catch (Exception) { CacheCount.Text = "?"; }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this, "Kayıtlı tüm çeviriler silinecek; satırlar yeniden çevrilecek. Devam edilsin mi?", "Önbelleği temizle",
                                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        _app.Cache.Clear();
        UpdateCacheCount();
    }

    // ---------- Sistem ----------
    private void ScreenshotCheck_Click(object sender, RoutedEventArgs e)
    {
        bool on = ScreenshotCheck.IsChecked == true;
        _overlay.ExcludeFromCapture = !on;
        _app.Settings.Save(_app.Settings.Load() with { ShowInScreenshots = on });
    }

    private void DiagCheck_Click(object sender, RoutedEventArgs e) =>
        _app.Settings.Save(_app.Settings.Load() with { DiagnosticLog = DiagCheck.IsChecked == true });

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception) { SetStatus("Günlük klasörü açılamadı."); }
    }
}
