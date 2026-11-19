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

    public SettingsStore Settings { get; private set; } = null!;
    public SqliteTranslationCache Cache { get; private set; } = null!;
    public TranslationChain Chain { get; private set; } = null!;
    public LatencyStats Stats { get; } = new();
    public WindowsOcrEngine? Ocr { get; private set; }
    public string TranslatorName { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--overlay-demo")) { RunOverlayDemo(e.Args); return; }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OyunAltyazi");
        Directory.CreateDirectory(dir);
        Settings = new SettingsStore(dir);
        Cache = new SqliteTranslationCache(Path.Combine(dir, "cache.db"));
        var translator = new GoogleTranslator(new HttpClient { Timeout = TimeSpan.FromSeconds(8) });
        TranslatorName = translator.Name;
        Chain = new TranslationChain(Cache, translator);
        Ocr = WindowsOcrEngine.TryCreate();
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
