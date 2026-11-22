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
        if (Tabs.SelectedIndex == 2) { UpdateCacheCount(); UpdateLocalFilesInfo(); }
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
            PreviewPlaceholder.Text = "Önizleme için yukarıdan bir oyun penceresi seçin.";
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
        UpdateTranslatorWarning(); UpdateGeminiStatus();
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
            (_app.TranslatorName.StartsWith("Gemini") ? TrGemini : _app.TranslatorName == "Google" ? TrGoogle : TrLocal).IsChecked = true;
            _loadingControls = false;
            return;
        }
        // the radio only knows Local/Google: keep an explicit "ct2"/"onnx" opt-in instead of overwriting it with "auto"
        var saved = _app.Settings.Load().Translator;
        var choice = ChooseTranslator(_app.Settings.Load());
        if (choice is null) return;
        _app.SetTranslator(choice);
        _app.Settings.Save(_app.Settings.Load() with { Translator = choice });
        UpdateTranslatorWarning();
        UpdateStatusDots();
    }

    // ---------- Yerel model cihazı (GPU/CPU) ----------
    private void SyncLocalDeviceCombo()
    {
        _loadingControls = true;
        LocalDeviceCombo.SelectedIndex = _app.LocalDevicePreference == LocalDevice.Cpu ? 1 : 0;
        _loadingControls = false;
    }

    private void LocalDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingControls) return;
        if (Running) { SyncLocalDeviceCombo(); return; }   // çalışırken değişmez; durdurulunca uygulanır
        var dev = (LocalDeviceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? LocalDevice.Gpu;
        var s = _app.Settings.Load();
        if (LocalDevice.Normalize(s.LocalDevice) == dev && s.LocalDevice == dev) return;
        _app.Settings.Save(s with { LocalDevice = dev });
        if (TrGoogle.IsChecked != true) _app.SetTranslator(s.Translator);   // eski yan süreç kapatılır, yenisi başlar
        UpdateStatusDots();
    }

    // ---------- Gemini (isteğe bağlı) ----------
    private void RevertTranslatorRadio()
    {
        var saved = _app.Settings.Load().Translator;
        _loadingControls = true;
        (saved == "google_only" ? TrGoogle : TrLocal).IsChecked = true;
        _loadingControls = false;
    }

    /// <summary>The setting value for the selected radio, or null when the choice was refused (radio is reverted). Gemini needs a stored key AND the user's explicit consent.</summary>
    private string? ChooseTranslator(AppSettings s)
    {
        if (TrGoogle.IsChecked == true) return "google_only";
        if (TrGemini.IsChecked != true) return s.Translator is "ct2" or "onnx" ? s.Translator : "auto";
        if (!_app.GeminiKeyStored)
        {
            GeminiStatus.Text = "Önce Gemini API anahtarınızı kaydedin.";
            RevertTranslatorRadio();
            return null;
        }
        if (!s.GeminiConsent)
        {
            var r = MessageBox.Show(this,
                "Gemini'yi açarsanız, çevrilen altyazı satırları (ve önceki en fazla 3 satır) Google'ın Gemini API'sine İNTERNET ÜZERİNDEN gönderilir. " +
                "Ekran görüntüsü ve oyun adı gönderilmez. Kullanım Google'ın koşullarına ve kendi API hesabınızın kotasına/ücretine tabidir.\n\n" +
                "Gemini yanıt vermezse yerel çeviri gösterilir. Bunu istediğiniz zaman kapatabilirsiniz.\n\nGemini kullanılsın mı?",
                "Gemini — çevrimiçi çeviri", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) { RevertTranslatorRadio(); return null; }
            _app.SetGeminiConsent(true);
        }
        return "gemini";
    }

    private void UpdateGeminiStatus() =>
        GeminiStatus.Text = _app.GeminiKeyStored ? "Anahtar bu Windows hesabına şifrelenmiş olarak kayıtlı (ayarlar dosyasında değil)." : "Kayıtlı anahtar yok. Anahtarı Google AI Studio'dan alabilirsiniz.";

    private void GeminiSave_Click(object sender, RoutedEventArgs e)
    {
        var key = GeminiKeyBox.Password.Trim();
        GeminiKeyBox.Clear();
        if (key.Length == 0) { GeminiStatus.Text = "Anahtar boş olamaz."; return; }
        try { _app.SaveGeminiKey(key); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException) { GeminiStatus.Text = "Anahtar kaydedilemedi."; return; }
        UpdateGeminiStatus();
    }

    private void GeminiRemove_Click(object sender, RoutedEventArgs e)
    {
        _app.RemoveGeminiKey();
        GeminiKeyBox.Clear();
        if (TrGemini.IsChecked == true && !Running)
        {
            _app.Settings.Save(_app.Settings.Load() with { Translator = "auto" });
            _loadingControls = true; TrLocal.IsChecked = true; _loadingControls = false;
            _app.SetTranslator("auto");
            UpdateStatusDots();
        }
        UpdateGeminiStatus();
    }

    // ---------- Oyun paketi / sözlük (yerel, yalnızca bu oyun için) ----------
    private void UpdateLocalFilesInfo()
    {
        var exe = _profile?.ExeName;
        bool has = exe is not null;
        PackImportBtn.IsEnabled = GlossaryImportBtn.IsEnabled = has;
        ReviewBtn.IsEnabled = has;
        PackRemoveBtn.IsEnabled = has && _app.Local.PackFor(exe!) is not null;
        GlossaryRemoveBtn.IsEnabled = has && _app.Local.GlossaryFor(exe!) is not null;
        string Describe(GamePack? p) => p is null ? "yok" : $"{p.Count:N0} satır";
        LocalFilesInfo.Text = !has ? "Önce soldan bir oyun penceresi seçin."
            : $"{exe}: oyun paketi {Describe(_app.Local.PackFor(exe!))} · sözlük {Describe(_app.Local.GlossaryFor(exe!))}";
        var ap = _app.AutoPacks;
        AutoPackStatus.Text = !has ? "" : ap.State switch
        {
            AutoPackState.Loading => $"Hazır paket indiriliyor: {ap.Title}…",
            AutoPackState.Ready => $"Hazır paket kullanılıyor: {ap.Title}",
            AutoPackState.Manual => "Kendi paket dosyanız kullanılıyor.",
            AutoPackState.Failed => $"Hazır paket alınamadı ({ap.Error}); normal çeviri sürüyor.",
            AutoPackState.Off => "Hazır paketler kapalı.",
            _ => "İngilizce repliklerden hazır paket aranıyor; normal çeviri sürer.",
        };
        var ag = _app.AutoGlossaries;
        bool manualGl = has && _app.Local.GlossaryFor(exe!) is not null;
        AutoGlossaryStatus.Text = !has ? "" : ag.State switch
        {
            AutoGlossaryState.Ready => manualGl
                ? $"Otomatik sözlük: {ag.Title} ({ag.Count:N0} ifade) hazır, ama kendi sözlüğünüz öncelikli (kaldırırsanız devreye girer)."
                : $"Otomatik sözlük kullanılıyor: {ag.Title} ({ag.Count:N0} ifade). Oyun paketi varsa o önce gelir.",
            AutoGlossaryState.Off => "Otomatik sözlük kapalı.",
            _ => "Bu oyun için otomatik sözlük yok.",
        };
        _loadingControls = true; AutoGlossaryCheck.IsChecked = _app.Settings.Load().AutoGlossary; AutoPackCheck.IsChecked = _app.Settings.Load().AutoGamePack; _loadingControls = false;
    }

    private void AutoPackCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;
        _app.Settings.Save(_app.Settings.Load() with { AutoGamePack = AutoPackCheck.IsChecked == true });
        _app.AutoPacks.Refresh();
        UpdateLocalFilesInfo();
    }

    private void AutoGlossaryCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;
        _app.Settings.Save(_app.Settings.Load() with { AutoGlossary = AutoGlossaryCheck.IsChecked == true });
        _app.AutoGlossaries.Select(_selected?.ExeName);
        UpdateLocalFilesInfo();
    }

    private void ImportLocal(bool glossary)
    {
        if (_profile is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Oyun paketi (*.json;*.gz)|*.json;*.gz", Title = glossary ? "Sözlük dosyası seç" : "Oyun paketi seç" };
        if (dlg.ShowDialog(this) != true) return;
        var err = _app.ImportLocalFile(_profile.ExeName, dlg.FileName, glossary);
        if (!glossary) _app.AutoPacks.Refresh();
        if (err is not null) MessageBox.Show(this, err, "İçe aktarılamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
        UpdateLocalFilesInfo();
    }

    private void PackImport_Click(object sender, RoutedEventArgs e) => ImportLocal(glossary: false);
    private void GlossaryImport_Click(object sender, RoutedEventArgs e) => ImportLocal(glossary: true);
    private void PackRemove_Click(object sender, RoutedEventArgs e) { if (_profile is not null) { _app.RemoveLocalFile(_profile.ExeName, glossary: false); _app.AutoPacks.Refresh(); } UpdateLocalFilesInfo(); }
    private void GlossaryRemove_Click(object sender, RoutedEventArgs e) { if (_profile is not null) { _app.RemoveLocalFile(_profile.ExeName, glossary: true); _app.AutoGlossaries.Select(_selected?.ExeName); } UpdateLocalFilesInfo(); }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_profile is not { } profile) return;
        if (Running)
        {
            MessageBox.Show(this, "Replikleri incelemek için önce çeviriyi durdur.", "Paket atölyesi");
            return;
        }
        try { new ReviewWindow(_app.Cache, profile.ExeName) { Owner = this }.ShowDialog(); }
        catch (Microsoft.Data.Sqlite.SqliteException) { MessageBox.Show(this, "Replik geçmişi okunamadı.", "Paket atölyesi"); }
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
