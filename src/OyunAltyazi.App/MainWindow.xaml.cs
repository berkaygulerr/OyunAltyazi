using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.App.UI;
using OyunAltyazi.Core;

namespace OyunAltyazi.App;

public partial class MainWindow : Window
{
    private sealed record Item(WindowInfo Info)
    {
        public override string ToString() => $"{Info.Title}  ({Info.ExeName})";
    }

    /// <summary>Forwards subtitles to the overlay and mirrors the last line in the main window.</summary>
    private sealed class TeeSink(ISubtitleSink inner, Action<string?, string, TranslationSource> onShow) : ISubtitleSink
    {
        public void Show(string? speaker, string text, TranslationSource source) { inner.Show(speaker, text, source); onShow(speaker, text, source); }
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

    private bool Running => _pipeline is not null;

    public MainWindow()
    {
        InitializeComponent();
        TranslatorText.Text = "Çevirmen: " + _app.TranslatorName;
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
        _diag.Tick += (_, _) => UpdateDiagnostics();
        _diag.Start();
        SourceInitialized += (_, _) =>
        {
            _hotkeys = new Hotkeys(new WindowInteropHelper(this).Handle);
            var failed = new List<string>();
            if (!_hotkeys.Register(1, 'T', ToggleRun)) failed.Add("Ctrl+Shift+T");
            if (!_hotkeys.Register(2, 'H', () => _overlay.UserHidden = !_overlay.UserHidden)) failed.Add("Ctrl+Shift+H");
            if (!_hotkeys.Register(3, 'R', SelectRegion)) failed.Add("Ctrl+Shift+R");
            if (failed.Count > 0) StatusText.Text = "Kısayol başka bir uygulamada kullanılıyor: " + string.Join(", ", failed);
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
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        StartBtn.IsEnabled = _app.Ocr is not null && (Running || _selected is not null);
        StartBtn.Content = Running ? "■ Durdur" : "▶ Başlat";
        SelectRegionBtn.IsEnabled = _selected is not null;
        WindowCombo.IsEnabled = !Running;
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        StopRun();
        ApplySelection((WindowCombo.SelectedItem as Item)?.Info);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) { if (!Running) RefreshList(); }
    private void SelectRegion_Click(object sender, RoutedEventArgs e) => SelectRegion();
    private void Start_Click(object sender, RoutedEventArgs e) => ToggleRun();

    private void SelectRegion()
    {
        if (_selected is null || _profile is null) return;
        var snap = WgcFrameSource.Snapshot(_selected.Hwnd);
        if (snap is null) { StatusText.Text = "Pencere görüntüsü alınamadı (küçültülmüş ya da kapanmış olabilir)."; return; }
        var dlg = new RegionSelector(snap, _profile.Region) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _profile = _profile with { Region = dlg.Result };
        var s = _app.Settings.Load();
        var profiles = new Dictionary<string, GameProfile>(s.Profiles ?? new(), StringComparer.OrdinalIgnoreCase)
        {
            [_profile.ExeName] = _profile
        };
        _app.Settings.Save(s with { Profiles = profiles, LastExe = _profile.ExeName });
        StatusText.Text = "Bölge kaydedildi.";
    }

    private void ToggleRun()
    {
        if (Running) { StopRun(); StatusText.Text = "Durduruldu."; return; }
        if (_app.Ocr is null || _selected is null || _profile is null) return;
        var profile = _profile;
        _overlay.Attach(_selected.Hwnd, profile.Overlay);
        var source = new WgcFrameSource(_selected.Hwnd, () => _profile!.Region);
        var sink = new TeeSink(_overlay, ShowLast);
        _source = source;
        source.Stopped += OnSourceStopped;
        _pipeline = new SubtitlePipeline(source, _app.Ocr, _app.Chain, sink, profile.ExeName, _app.Stats, () => Environment.TickCount64);
        _pipeline.Start();
        StatusText.Text = "Çalışıyor…";
        UpdateButtons();
    }

    private void StopRun()
    {
        var p = _pipeline;
        _pipeline = null;
        p?.Dispose();
        _source?.Dispose();
        _source = null;
        _overlay.Detach();
        UpdateButtons();
    }

    private void OnSourceStopped(string reason) => Dispatcher.InvokeAsync(() =>
    {
        StopRun();
        StatusText.Text = reason + " — durduruldu.";
    });

    private void ShowLast(string? speaker, string text, TranslationSource source) => Dispatcher.InvokeAsync(() =>
    {
        var label = source switch
        {
            TranslationSource.Cache => "önbellek",
            TranslationSource.Translator => "çevrimiçi",
            _ => "yedek: çeviri başarısız, İngilizce gösteriliyor",
        };
        LastLineText.Text = $"Son satır:  {(speaker is null ? "" : speaker + ": ")}{text}   (kaynak: {label})";
    });

    private void UpdateDiagnostics()
    {
        var a = _app.Stats.StableToShown();
        var b = _app.Stats.CaptureToShown();
        LatencyText.Text = a.Count == 0
            ? "Gecikme (son 50): henüz veri yok"
            : $"Gecikme (son {a.Count}): sabit→ekran ort {a.AvgMs:0} ms / p95 {a.P95Ms:0} ms · yakalama→ekran ort {b.AvgMs:0} ms / p95 {b.P95Ms:0} ms";
    }
}
