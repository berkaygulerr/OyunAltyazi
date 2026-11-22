using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.App.UI;
using OyunAltyazi.Translation;

namespace OyunAltyazi.App;

public partial class SetupWindow : Window
{
    private readonly App _app = (App)Application.Current;
    private CancellationTokenSource? _cts;
    private bool _running;

    public SetupWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loaded += async (_, _) => { RefreshOcr(); await RunAsync(); };
        Closing += (_, _) => { if (_running) { _cts?.Cancel(); } };
    }

    private void RefreshOcr()
    {
        if (_app.Ocr is null) _app.RefreshOcr();
        bool ok = _app.Ocr is not null;
        OcrText.Text = ok ? "Hazır" : "İngilizce OCR dil paketi bu bilgisayarda yok; altyazı okunamaz.";
        OcrBtn.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        RetryBtn.Visibility = Visibility.Collapsed; ErrorText.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        var progress = new Progress<SetupProgress>(OnProgress);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OyunAltyazi");
            await new ComponentInstaller(http).RunAsync(progress, _cts.Token);
            IntroText.Text = "Yerel çeviri hazır.";
            CloseBtn.Content = "Kapat";
            _app.NotifyComponentsChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            ErrorText.Text = e.Message + "\nUygulama Google çeviri ile çalışmaya devam eder. Bağlantınızı kontrol edip yeniden deneyin (kaldığı yerden devam eder).";
            ErrorText.Visibility = Visibility.Visible; RetryBtn.Visibility = Visibility.Visible;
            _app.LogSetup(e.ToString());
            _app.NotifyComponentsChanged();
        }
        finally { _running = false; }
    }

    private void OnProgress(SetupProgress p)
    {
        var (bar, txt) = p.Step switch { "python" => (PyBar, PyText), "packages" => (PkgBar, PkgText), _ => (ModelBar, ModelText) };
        bar.Value = p.Fraction; txt.Text = p.Text;
    }

    private async void OcrBtn_Click(object sender, RoutedEventArgs e)
    {
        OcrBtn.IsEnabled = false; OcrText.Text = "Kuruluyor… (birkaç dakika sürebilir)";
        await OcrSetup.InstallEnglishOcrAsync();
        _app.RefreshOcr();
        OcrBtn.IsEnabled = true;
        RefreshOcr();
        if (_app.Ocr is null) OcrText.Text = "Kurulamadı veya izin verilmedi. Ayarlar › Zaman ve dil › Dil bölümünden İngilizce (ABD) ekleyin.";
        _app.NotifyComponentsChanged();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
