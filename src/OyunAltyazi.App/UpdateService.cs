using System.IO;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace OyunAltyazi.App;

/// <summary>GitHub Releases'ten sessiz güncelleme: arka planda denetle + indir, çıkışta uygula; yeniden başlatmayı kullanıcı seçer.</summary>
public sealed class UpdateService : IDisposable
{
    public const string RepoUrl = "https://github.com/berkaygulerr/OyunAltyazi-releases";

    private readonly UpdateManager? _mgr;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VelopackAsset? _ready;
    private string? _scheduledFor;
    private readonly string _logPath;

    public string Status { get; private set; } = "Güncellemeler arka planda otomatik denetlenir ve indirilir.";
    public bool IsInstalled => _mgr?.IsInstalled == true;
    public string CurrentVersion => _mgr?.CurrentVersion?.ToString() ?? "geliştirme";
    public string? ReadyVersion => _ready?.Version?.ToString();
    /// <summary>UI iş parçacığında tetiklenir.</summary>
    public event Action? Changed;

    public UpdateService()
    {
        _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi", "logs", "update.log");
        try { _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
        catch (Exception e) { Log("UpdateManager: " + e.Message); }
    }

    public void Start()
    {
        if (!IsInstalled) { Status = "Geliştirme sürümü: otomatik güncelleme kapalı."; return; }
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); await CheckAsync(); });
    }

    public async Task CheckAsync()
    {
        if (_mgr is null || !IsInstalled || !await _gate.WaitAsync(0)) return;
        try
        {
            if (_ready is not null) return;
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null) { Set("Güncel (" + CurrentVersion + ")."); return; }
            Set($"v{info.TargetFullRelease.Version} indiriliyor…");
            await _mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _ready = info.TargetFullRelease;
            if (_scheduledFor != _ready.Version.ToString())
            {
                _scheduledFor = _ready.Version.ToString();
                _mgr.WaitExitThenApplyUpdates(_ready, silent: true, restart: false); // uygulama kapanınca uygula
            }
            Set($"v{_ready.Version} hazır; yeniden başlatınca uygulanır.");
        }
        catch (Exception e) { Log("Güncelleme denetimi: " + e.Message); Status = "Güncelleme denetlenemedi (çevrimdışı olabilir)."; Raise(); }
        finally { _gate.Release(); }
    }

    public void ApplyAndRestart()
    {
        if (_mgr is null || _ready is null) return;
        _mgr.ApplyUpdatesAndRestart(_ready);
    }

    private void Set(string s) { Status = s; Log(s); Raise(); }
    private void Raise() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    private void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTime.Now:s} {s}{Environment.NewLine}");
        }
        catch (Exception) { }
    }

    public void Dispose() => _timer.Stop();
}
