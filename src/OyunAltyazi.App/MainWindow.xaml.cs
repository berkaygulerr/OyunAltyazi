using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.App.UI;
using OyunAltyazi.Core;

namespace OyunAltyazi.App;

public sealed partial class MainWindow : Window
{
    private sealed record Item(WindowInfo Info)
    {
        public override string ToString() => $"{Info.Title}  ({Info.ExeName})";
    }

    /// <summary>Forwards subtitles to the overlay and mirrors the last line in the main window.</summary>
    private sealed class TeeSink(ISubtitleSink inner, Action<string?, string, TranslationSource> onShow) : ISubtitleSink
    {
        public void Show(IReadOnlyList<ShownUtterance> utterances, RegionF? box = null, int lineCount = 1, bool anchorTop = false) { inner.Show(utterances, box, lineCount, anchorTop); foreach (var u in utterances) onShow(u.Speaker, u.Text, u.Source); }
        public void Clear() => inner.Clear();
    }

    private readonly App _app = (App)Application.Current;
    private readonly OverlayWindow _overlay = new();
    private readonly DispatcherTimer _diag = new() { Interval = TimeSpan.FromSeconds(1) };
    private Hotkeys? _hotkeys;
    private GameProfile? _profile;
    private WindowInfo? _selected;
    private IFrameSource? _source;
    private SubtitlePipeline? _pipeline;
    private bool _suppressSelection;
    private bool _ready;

    private DiagnosticLog? _diagLog;
    private bool Running => _pipeline is not null;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        var settings = _app.Settings.Load();
        (settings.Translator == "google_only" ? TrGoogle : TrLocal).IsChecked = true;
        InitTranslatorCard();
        var showShots = _app.Settings.Load().ShowInScreenshots;
        ScreenshotCheck.IsChecked = showShots;
        DiagCheck.IsChecked = _app.Settings.Load().DiagnosticLog;
        _overlay.ExcludeFromCapture = !showShots;
        if (_app.Ocr is null)
        {
            WarningText.Text = "Windows İngilizce OCR paketi gerekli: Ayarlar › Zaman ve dil › Dil › İngilizce (ABD) ekleyin.";
            WarningText.Visibility = Visibility.Visible;
        }
        else if (!WgcFrameSource.IsSupported)
        {
            WarningText.Text = "Bu Windows sürümü ekran yakalamayı (Windows 10 1903+) desteklemiyor.";
            WarningText.Visibility = Visibility.Visible;
        }
        RefreshList(selectLast: true);
        UpdateButtons();
        _ready = true;
        InitSetupUi();
        // Ekran görüntüsü doğrulaması için: OYUNALTYAZI_TAB=0..3 başlangıç sekmesini, OYUNALTYAZI_COMBO=1 açılır listeyi açar.
        if (int.TryParse(Environment.GetEnvironmentVariable("OYUNALTYAZI_TAB"), out var tab) && tab is >= 0 and < 4) Tabs.SelectedIndex = tab;
        if (Environment.GetEnvironmentVariable("OYUNALTYAZI_COMBO") == "1") Loaded += (_, _) => WindowCombo.IsDropDownOpen = true;
        RefreshPreview();
        UpdateSubtitlePreview();
        _diag.Tick += (_, _) => UpdateDiagnostics();
        _diag.Start();
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new Hotkeys(new WindowInteropHelper(this).Handle);
            var failed = new List<string>();
            if (!_hotkeys.Register(1, 'T', ToggleRun)) failed.Add("Ctrl+Shift+T");
            if (!_hotkeys.Register(2, 'H', () => _overlay.UserHidden = !_overlay.UserHidden)) failed.Add("Ctrl+Shift+H");
            if (!_hotkeys.Register(3, 'R', SelectRegion)) failed.Add("Ctrl+Shift+R");
            if (failed.Count > 0) SetStatus("Kısayol başka bir uygulamada kullanılıyor: " + string.Join(", ", failed));
        };
        Closed += (_, _) =>
        {
            StopRun();
            _hotkeys?.Dispose();
            _overlay.Close();
            Application.Current.Shutdown();
        };
    }

    private void RefreshList(bool selectLast = false)
    {
        var prev = _selected;
        var list = WindowEnumerator.List().Select(w => new Item(w)).ToList();
        _suppressSelection = true;
        WindowCombo.ItemsSource = list;
        string? want = prev?.ExeName ?? (selectLast ? _app.Settings.Load().LastExe : null);
        var match = want is null ? null : list.FirstOrDefault(i => prev is not null ? i.Info.Hwnd == prev.Hwnd : i.Info.ExeName == want)
                                          ?? list.FirstOrDefault(i => i.Info.ExeName == want);
        WindowCombo.SelectedItem = match;
        _suppressSelection = false;
        ApplySelection(match?.Info);
    }

    private void ApplySelection(WindowInfo? info)
    {
        _selected = info;
        _profile = info is null ? null : _app.Settings.ProfileFor(info.ExeName);
        if (info is not null)
        {
            var s = _app.Settings.Load();
            if (s.LastExe != info.ExeName) _app.Settings.Save(s with { LastExe = info.ExeName });
        }
        LoadProfileControls();
        UpdateButtons();
        if (_ready) RefreshPreview();
    }

    private void UpdateButtons()
    {
        StartBtn.IsEnabled = _app.Ocr is not null && (Running || _selected is not null);
        StartBtn.Content = Running ? "■  Durdur" : "▶  Çeviriyi Başlat";
        StartBtn.Style = (Style)FindResource(Running ? "DangerButton" : "SuccessButton");
        SelectRegionBtn.IsEnabled = _selected is not null;
        PreviewRefreshBtn.IsEnabled = _selected is not null;
        WindowCombo.IsEnabled = !Running;
        RefreshBtn.IsEnabled = !Running;
        TrLocal.IsEnabled = TrGoogle.IsEnabled = !Running;
        TrLockedNote.Visibility = Running ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusDots();
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        StopRun();
        ApplySelection((WindowCombo.SelectedItem as Item)?.Info);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { if (!Running) RefreshList(); }
    private void Start_Click(object sender, RoutedEventArgs e) => ToggleRun();

    private void ToggleRun()
    {
        if (Running) { StopRun(); SetStatus("Durduruldu."); return; }
        if (_app.Ocr is null || _selected is null || _profile is null) return;
        var profile = _profile;
        _overlay.Attach(_selected.Hwnd, profile.Overlay);
        var source = new WgcFrameSource(_selected.Hwnd, () => _profile!.Region);
        var sink = new TeeSink(_overlay, ShowLast);
        _source = source;
        source.Stopped += OnSourceStopped;
        _overlay.RegionProvider = () => _profile!.Region;
        _pipeline = new SubtitlePipeline(source, _app.Ocr, _app.Chain, sink, profile.ExeName, _app.Stats, () => Environment.TickCount64,
                                         regionProvider: () => _profile!.Region);
        if (_app.Settings.Load().DiagnosticLog)
        {
            try { _diagLog = new DiagnosticLog(LogDirectory, profile.ExeName); _pipeline.Log = _diagLog.Write; }
            catch (Exception) { _diagLog = null; }
        }
        _pipeline.Start();
        SetStatus("Çalışıyor…");
        UpdateButtons();
    }

    private void StopRun()
    {
        var p = _pipeline;
        _pipeline = null;
        p?.Dispose();
        _diagLog?.Dispose(); _diagLog = null;
        _source?.Dispose();
        _source = null;
        _overlay.Detach();
        UpdateButtons();
    }

    private void OnSourceStopped(string reason) => Dispatcher.InvokeAsync(() =>
    {
        StopRun();
        SetStatus(reason + " — durduruldu.");
    });

    private string _lastText = "";

    private void ShowLast(string? speaker, string text, TranslationSource source) => Dispatcher.InvokeAsync(() =>
    {
        var label = source switch
        {
            TranslationSource.Cache => "kaynak: önbellek",
            TranslationSource.Translator => $"kaynak: {(_app.TranslatorName == "Google" ? "Google (çevrimiçi)" : "yerel model")}",
            _ => "yedek: çeviri başarısız, İngilizce gösteriliyor",
        };
        _lastText = text;
        LastSpeakerText.Text = speaker ?? "";
        LastSpeakerText.Visibility = string.IsNullOrEmpty(speaker) ? Visibility.Collapsed : Visibility.Visible;
        LastLineText.Text = text;
        LastSourceText.Text = label;
        CopyBtn.IsEnabled = true;
    });

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_lastText.Length == 0) return;
        try { Clipboard.SetText(_lastText); } catch (System.Runtime.InteropServices.COMException) { }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDiagnostics()
    {
        var a = _app.Stats.StableToShown();
        var b = _app.Stats.CaptureToShown();
        if (a.Count == 0)
        {
            LatAvg.Text = LatP95.Text = "—";
            LatencyText.Text = "Henüz veri yok";
        }
        else
        {
            LatAvg.Text = $"{a.AvgMs:0} ms";
            LatP95.Text = $"{a.P95Ms:0} ms";
            LatencyText.Text = $"Son {a.Count} satır · yakalama→ekran ort {b.AvgMs:0} ms / p95 {b.P95Ms:0} ms";
        }
        if (Tabs.SelectedIndex == 2) UpdateCacheCount();
    }

    private void UpdateStatusDots()
    {
        static void Set(System.Windows.Shapes.Ellipse dot, TextBlock label, string key, string text)
        {
            dot.Fill = (System.Windows.Media.Brush)Application.Current.FindResource(key);
            label.Text = text;
        }
        if (!WgcFrameSource.IsSupported) Set(DotCapture, CaptureStatus, "ErrorBrush", "Desteklenmiyor");
        else if (Running) Set(DotCapture, CaptureStatus, "SuccessBrush", "Yakalanıyor");
        else if (_selected is not null) Set(DotCapture, CaptureStatus, "AccentBrush", "Hazır (beklemede)");
        else Set(DotCapture, CaptureStatus, "MutedBrush", "Oyun seçilmedi");

        if (_app.Ocr is not null) Set(DotOcr, OcrStatus, "SuccessBrush", "Windows OCR hazır");
        else Set(DotOcr, OcrStatus, "ErrorBrush", "İngilizce OCR yok");

        if (TrGoogle.IsChecked == true) Set(DotTr, TrStatus, "SuccessBrush", "Google (çevrimiçi)");
        else if (_app.LocalInstalled) Set(DotTr, TrStatus, "SuccessBrush", "Yerel model hazır (GPU)");
        else Set(DotTr, TrStatus, "WarnBrush", "Model yok → Google");
    }
}
