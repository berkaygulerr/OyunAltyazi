using System.Net.Http;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using OyunAltyazi.App.Capture;
using OyunAltyazi.App.Ocr;
using OyunAltyazi.App.Overlay;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;

namespace OyunAltyazi.App;

public partial class App : Application
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public SettingsStore Settings { get; private set; } = null!;
    public SqliteTranslationCache Cache { get; private set; } = null!;
    public TranslationChain Chain { get; private set; } = null!;
    public LatencyStats Stats { get; } = new();
    /// <summary>Opt-in local glossary / game pack lookups (exact match, per game). Local files only.</summary>
    public LocalTranslations Local { get; } = new();

    /// <summary>Ready-made catalog pack for the selected game (exact exe match, async, cached under %AppData%\OyunAltyaziutopacks). Manual files always win.</summary>
    public AutoPackCoordinator AutoPacks { get; private set; } = null!;

    /// <summary>Built-in offline glossary for confidently recognised games (exact exe match, no network, no files).</summary>
    public AutoGlossaryCoordinator AutoGlossaries { get; private set; } = null!;

    private static string PackDir(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi", "packs", Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());

    /// <summary>(Re)loads the glossary and pack the user chose for this game. Returns an error text when one of them could not be used (it is then not applied).</summary>
    public string? ApplyLocalFiles(string exe)
    {
        var s = Settings.Load();
        string? error = null;
        foreach (var glossary in new[] { true, false })
        {
            var path = (glossary ? s.Glossaries : s.GamePacks)?.GetValueOrDefault(exe.ToLowerInvariant());
            Local.Clear(exe, glossary);
            if (string.IsNullOrEmpty(path)) continue;
            var r = GamePack.LoadFile(path, exe);
            if (r.Pack is null) { error ??= $"{(glossary ? "Sözlük" : "Oyun paketi")} kullanılmadı: {r.Error}"; continue; }
            if (glossary) Local.SetGlossary(exe, r.Pack); else Local.SetPack(exe, r.Pack);
        }
        return error;
    }

    /// <summary>Validates a user-chosen file for exactly this game, copies it into the app data folder and applies it. Returns an error text or null.</summary>
    public string? ImportLocalFile(string exe, string sourcePath, bool glossary)
    {
        var r = GamePack.LoadFile(sourcePath, exe);
        if (r.Pack is null) return r.Error;
        try
        {
            var dir = PackDir(exe);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, glossary ? "glossary.json" : "pack.json");
            File.Copy(sourcePath, dest, overwrite: true);
            var s = Settings.Load();
            var key = exe.ToLowerInvariant();
            var map = new Dictionary<string, string>((glossary ? s.Glossaries : s.GamePacks) ?? new(), StringComparer.OrdinalIgnoreCase) { [key] = dest };
            Settings.Save(glossary ? s with { Glossaries = map } : s with { GamePacks = map });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Dosya kaydedilemedi: " + e.Message; }
        return ApplyLocalFiles(exe);
    }

    public void RemoveLocalFile(string exe, bool glossary)
    {
        var s = Settings.Load();
        var map = new Dictionary<string, string>((glossary ? s.Glossaries : s.GamePacks) ?? new(), StringComparer.OrdinalIgnoreCase);
        if (map.Remove(exe.ToLowerInvariant(), out var path)) { try { File.Delete(path); } catch (Exception) { } }
        Settings.Save(glossary ? s with { Glossaries = map } : s with { GamePacks = map });
        Local.Clear(exe, glossary);
    }
    public WindowsOcrEngine? Ocr { get; private set; }

    public UpdateService Updates { get; } = new();

    public event Action? ComponentsChanged;

    public void RefreshOcr() => Ocr = WindowsOcrEngine.TryCreate();

    public void NotifyComponentsChanged() => ComponentsChanged?.Invoke();

    public void LogSetup(string s)
    {
        try { var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi", "logs", "setup.log"); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.AppendAllText(f, $"{DateTime.Now:s} {s}{Environment.NewLine}"); } catch (Exception) { }

    }
    public string TranslatorName { get; private set; } = "";

    // ---------- Optional Gemini (explicit consent + own API key; DPAPI file separate from settings.json) ----------
    public DpapiKeyStore KeyStore { get; private set; } = null!;
    private string? _geminiKey;   // in memory only while Gemini mode is active; never logged
    private HttpClient? _geminiHttp;
    private const int GeminiTimeoutMs = 2000;   // bounded: a slow answer is dropped and the local translation is shown instead

    public bool GeminiKeyStored => KeyStore.Exists;
    public void SaveGeminiKey(string key) { KeyStore.Save(key); _geminiKey = KeyStore.Load(); }
    public void RemoveGeminiKey() { KeyStore.Delete(); _geminiKey = null; }
    public void SetGeminiConsent(bool consent) => Settings.Save(Settings.Load() with { GeminiConsent = consent });

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--overlay-demo")) { RunOverlayDemo(e.Args); return; }

        var dir = Environment.GetEnvironmentVariable("OYUNALTYAZI_DATA") is { Length: > 0 } testDir   // yalnızca ekran görüntüsü/test: ayrı ve boş veri klasörü
            ? testDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi");
        Directory.CreateDirectory(dir);
        Settings = new SettingsStore(dir); KeyStore = new DpapiKeyStore(dir);
        var autoHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(130) };
        autoHttp.DefaultRequestHeaders.UserAgent.ParseAdd("OyunAltyazi-pack-fetch");
        AutoPacks = new AutoPackCoordinator(Local, new AutoPackService(PackCatalog.Default, Path.Combine(dir, "autopacks"), autoHttp), () => Settings.Load().AutoGamePack);
        AutoGlossaries = new AutoGlossaryCoordinator(Local, AutoGlossaryCatalog.Default, () => Settings.Load().AutoGlossary);
        Cache = new SqliteTranslationCache(Path.Combine(dir, "cache.db"));
        SetTranslator(Settings.Load().Translator);
        Ocr = WindowsOcrEngine.TryCreate();
        var main = new MainWindow();
        main.Show();
        Updates.Start();
        if (!LocalInstalled || Ocr is null) new SetupWindow { Owner = main }.Show();
    }

    private readonly GoogleTranslator _google = new(new HttpClient { Timeout = TimeSpan.FromSeconds(8) });
    private LocalTranslatorHost? _ct2Host;
    /// <summary>Raised (any thread) when the local sidecar finished starting, so the UI can show the actual device.</summary>
    public event Action? LocalDeviceReady;
    public string LocalDevicePreference => LocalDevice.Normalize(Settings.Load().LocalDevice);
    /// <summary>Status text from the device the sidecar really reported (null device = not ready yet).</summary>
    public string LocalDeviceLabel => LocalDevice.Label(LocalDevicePreference, _ct2Host?.Current?.Device);
    private OnnxTranslator? _onnx;
    public bool LocalInstalled => Ct2Paths.IsInstalled();
    public bool OnnxInstalled => OnnxTranslator.IsInstalledIn(OnnxPaths.ModelDir);

    /// <summary>choice: "auto" | "ct2" | "onnx" (opt-in native CPU) | "google_only". Yerel model yoksa Google kullanılır. "auto" never picks ONNX.</summary>
    public void SetTranslator(string choice)
    {
        ITranslator t = _google;
        switch (TranslatorChoice.Resolve(choice, LocalInstalled, OnnxInstalled))
        {
            case TranslatorKind.Ct2:
                _ct2Host ??= new LocalTranslatorHost(dev => new Ct2Translator(Ct2Paths.ProcessLauncher(Ct2Paths.FindPython()!, Ct2Paths.FindServerScript()!, Ct2Paths.ModelDir, dev)));
                var ct2 = _ct2Host.Get(LocalDevicePreference);   // same device: reused; changed: old sidecar disposed, new one started
                _ = ct2.WarmUpAsync().ContinueWith(_ => LocalDeviceReady?.Invoke(), TaskScheduler.Default);
                t = ct2;
                break;
            case TranslatorKind.Onnx:
                _onnx ??= new OnnxTranslator(OnnxPaths.ModelDir, OnnxMarianEngine.DefaultBeams, intraOpThreads: 4);
                _ = _onnx.WarmUpAsync();
                t = _onnx;
                break;
        }
        TranslatorName = t.Name switch { "ct2" => "Yerel (CTranslate2)", "onnx" => "Yerel (ONNX, CPU)", _ => "Google" };
        // Gemini only when selected AND consented AND a key is stored; otherwise no Gemini object, no key read, no request.
        var s = Settings.Load();
        ITranslator? premium = null;
        if (TranslatorChoice.UseGemini(choice, s.GeminiConsent, KeyStore.Exists))
        {
            _geminiKey = KeyStore.Load();
            if (_geminiKey is not null)
            {
                premium = new GeminiTranslator(_geminiHttp ??= GeminiTranslator.CreateHttpClient(), () => _geminiKey,
                    GeminiSettings.IsValidModel(s.GeminiModel) ? s.GeminiModel : GeminiSettings.DefaultModel, TimeSpan.FromMilliseconds(GeminiTimeoutMs));
                TranslatorName = "Gemini (çevrimiçi) + " + TranslatorName;
            }
        }
        else _geminiKey = null;
        Chain = new TranslationChain(Cache, t, Local, premium);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Updates.Dispose();

        _ct2Host?.Dispose();
        _onnx?.Dispose();
        Cache?.Dispose();
        base.OnExit(e);
    }

    // Manual check: overlay on a target window (default notepad.exe), three sample lines, then clear.
    private void RunOverlayDemo(string[] args)
    {
        int i = Array.IndexOf(args, "--overlay-demo");
        var exe = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1].ToLowerInvariant() : "notepad.exe";
        var target = WindowEnumerator.List().FirstOrDefault(w => w.ExeName == exe);
        if (target is null)
        {
            MessageBox.Show($"{exe} penceresi bulunamadı.", "OyunAltyazı", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }
        keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt press/release lifts the foreground lock
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(target.Hwnd);
        var overlay = new OverlayWindow();
        overlay.Attach(target.Hwnd, new OverlayStyle());
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            overlay.Show("Price", "Yukarı çıkın, hemen.", TranslationSource.Translator);
            await Task.Delay(1500);
            overlay.Show(null, "Helikoptere gidiyoruz.", TranslationSource.Cache);
            await Task.Delay(1500);
            overlay.Show("Soap", "Bu bir yedek satırdır (italik).", TranslationSource.Fallback);
            await Task.Delay(1500);
            overlay.Clear();
            if (!args.Contains("--hold"))
            {
                await Task.Delay(1500);
                Dispatcher.Invoke(() => Shutdown(0));
            }
        });
    }
}
