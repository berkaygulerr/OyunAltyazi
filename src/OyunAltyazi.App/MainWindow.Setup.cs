using System.IO;
using System.Windows;
using OyunAltyazi.Translation;

namespace OyunAltyazi.App;

// Güncelleme bandı, sürüm bilgisi ve bileşen kurulum/onarım bağlantıları.
public sealed partial class MainWindow
{
    private void InitSetupUi()
    {
        _app.Updates.Changed += RefreshUpdateUi;
        _app.ComponentsChanged += OnComponentsChanged;
        RefreshUpdateUi();
        RefreshComponentsText();
    }

    private void RefreshUpdateUi()
    {
        VersionText.Text = "Sürüm: " + _app.Updates.CurrentVersion;
        UpdateStatusText.Text = _app.Updates.Status;
        var v = _app.Updates.ReadyVersion;
        if (v is null) return;
        UpdateBannerText.Text = $"Yeni sürüm hazır (v{v}) — Yeniden başlat";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void RefreshComponentsText()
    {
        string Mark(bool ok) => ok ? "✓" : "✗";
        ComponentsText.Text = $"{Mark(_app.Ocr is not null)} İngilizce OCR   {Mark(Ct2Paths.FindPython() is not null)} Python/CTranslate2   {Mark(Ct2Paths.ModelReady)} Çeviri modeli";
    }

    private void OnComponentsChanged()
    {
        var choice = _app.Settings.Load().Translator;
        if (!Running && !_app.TranslatorName.StartsWith("Yerel") && choice != "google_only") _app.SetTranslator(choice);
        InitTranslatorCard();
        if (_app.Ocr is not null && WarningText.Text.Contains("OCR")) WarningText.Visibility = Visibility.Collapsed;
        UpdateStatusDots();
        RefreshComponentsText();
    }

    private void UpdateRestart_Click(object sender, RoutedEventArgs e)
    {
        if (Running && MessageBox.Show(this, "Çalışan çeviri oturumu kapanacak. Yeniden başlatılsın mı?", "Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _app.Updates.ApplyAndRestart();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Denetleniyor…";
        await _app.Updates.CheckAsync();
        RefreshUpdateUi();
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        try { File.Delete(Ct2Paths.PackagesMarker); } catch (Exception) { }
        new SetupWindow { Owner = this }.Show();
    }
}
