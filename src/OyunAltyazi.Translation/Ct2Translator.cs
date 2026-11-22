using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

/// <summary>Çalışan yan süreç (stdin/stdout JSON satırları).</summary>
public sealed class Sidecar(TextWriter input, TextReader output, Action kill) : IDisposable
{
    public TextWriter Input { get; } = input;
    public TextReader Output { get; } = output;
    public void Dispose() { try { kill(); } catch { } try { Input.Dispose(); } catch { } }
}

public delegate Sidecar SidecarLauncher();

public static class Ct2Paths
{
    // Velopack kurulum klasörü %LOCALAPPDATA%\OyunAltyazi: kurucu orayı temizler. Bu yüzden veriler ayrı klasörde.
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OyunAltyaziData");
    public static string VenvPython => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OyunAltyazi", "ct2", "venv", "Scripts", "python.exe"); // eski geliştirici venv'i
    public static string RuntimeDir => Path.Combine(Root, "runtime", "python");
    public static string RuntimePython => Path.Combine(RuntimeDir, "python.exe");
    public static string PipMarker => Path.Combine(RuntimeDir, ".pip-ok");
    public static string PackagesMarker => Path.Combine(RuntimeDir, ".pkgs-ok");
    public static string ModelDir => Path.Combine(Root, "models", "opus-mt-tc-big-en-tr-ct2");

    public static bool RuntimeReady => File.Exists(RuntimePython) && File.Exists(PackagesMarker);

    /// <summary>Uygulama paketindeki python (runtime) varsa o; yoksa geliştirici venv'i.</summary>
    public static string? FindPython() => RuntimeReady ? RuntimePython : File.Exists(VenvPython) ? VenvPython : null;

    public static string? FindServerScript()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "server.py");
        if (File.Exists(packaged)) return packaged;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tools", "ct2server", "server.py");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool ModelReady => File.Exists(Path.Combine(ModelDir, "model.bin")) && File.Exists(Path.Combine(ModelDir, "target.spm"));

    public static bool IsInstalled(string? script = null) =>
        FindPython() is not null && ModelReady && (script ?? FindServerScript()) is not null;

    public static SidecarLauncher ProcessLauncher(string python, string script, string modelDir) => () =>
    {
        var psi = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-u"); psi.ArgumentList.Add(script); psi.ArgumentList.Add(modelDir);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var proc = Process.Start(psi) ?? throw new TranslatorException("Yerel çeviri süreci başlatılamadı.");
        proc.ErrorDataReceived += (_, _) => { };
        proc.BeginErrorReadLine();
        proc.StandardInput.NewLine = "\n";
        return new Sidecar(proc.StandardInput, proc.StandardOutput, () => { if (!proc.HasExited) proc.Kill(true); });
    };
}

public sealed class Ct2Translator(SidecarLauncher launcher, TimeSpan? requestTimeout = null, TimeSpan? startTimeout = null) : IContinuationTranslator, IDisposable
{
    private readonly TimeSpan _reqTimeout = requestTimeout ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _startTimeout = startTimeout ?? TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Sidecar? _sc;
    private int _id;
    private int _failures;
    private bool _disposed;

    public string Name => "ct2";
    public string? Device { get; private set; }

    public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => RunAsync(text, null, ct);

    /// <summary>Önceki parçanın gösterilen Türkçesini hedef öneki olarak zorlar; yalnızca devamı döndürür (boş olabilir).</summary>
    public Task<string> TranslateContinuationAsync(string fullEnglish, string shownPrefixTurkish, TranslationContext ctx, CancellationToken ct)
        => RunAsync(fullEnglish, TrimFinalPunctuation(shownPrefixTurkish), ct);

    /// <summary>Cümle sonu noktalamasını atar ki model cümleyi doğal sürdürsün.</summary>
    public static string TrimFinalPunctuation(string s) => s.TrimEnd().TrimEnd('.', '!', '?', '…', ' ');

    private async Task<string> RunAsync(string text, string? prefix, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new TranslatorException("Yerel çevirmen kapatıldı.");
            // bir çökme sonrası bir kez yeniden başlat
            for (int attempt = 0; ; attempt++)
            {
                try { return await RequestAsync(text, prefix, ct).ConfigureAwait(false); }
                catch (TranslatorException) when (attempt == 0 && _failures < 2 && !ct.IsCancellationRequested) { }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string> RequestAsync(string text, string? prefix, CancellationToken ct)
    {
        if (_failures >= 3) throw new TranslatorException("Yerel çevirmen kullanılamıyor.");
        try
        {
            _sc ??= await StartAsync(ct).ConfigureAwait(false);
            int id = ++_id;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_reqTimeout);
            await _sc.Input.WriteLineAsync((prefix is null ? JsonSerializer.Serialize(new { id, texts = new[] { text } }) : JsonSerializer.Serialize(new { id, texts = new[] { text }, prefix = new[] { prefix } }))).ConfigureAwait(false);
            await _sc.Input.FlushAsync(cts.Token).ConfigureAwait(false);
            while (true)
            {
                var line = await _sc.Output.ReadLineAsync(cts.Token).ConfigureAwait(false)
                    ?? throw new TranslatorException("Yerel çeviri süreci kapandı.");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id) continue; // eski yanıt
                if (root.TryGetProperty("error", out var err)) throw new TranslatorException("Yerel çeviri hatası: " + err.GetString());
                var res = root.GetProperty("translations")[0].GetString();
                if (string.IsNullOrWhiteSpace(res) && prefix is null) throw new TranslatorException("Yerel çeviri boş döndü.");
                _failures = 0;
                return res ?? "";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail(); throw new TranslatorException("Yerel çeviri zaman aşımına uğradı.");
        }
        catch (TranslatorException) { Fail(); throw; }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            Fail(); throw new TranslatorException("Yerel çeviri süreci hata verdi: " + e.Message);
        }
    }

    private async Task<Sidecar> StartAsync(CancellationToken ct)
    {
        var sc = launcher();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_startTimeout);
            while (true)
            {
                var line = await sc.Output.ReadLineAsync(cts.Token).ConfigureAwait(false)
                    ?? throw new TranslatorException("Yerel çeviri süreci başlarken kapandı.");
                if (!line.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("ready", out _))
                {
                    Device = doc.RootElement.TryGetProperty("device", out var d) ? d.GetString() : null;
                    return sc;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sc.Dispose(); throw new TranslatorException("Yerel çeviri süreci zamanında hazır olmadı.");
        }
        catch { sc.Dispose(); throw; }
    }

    /// <summary>Modeli önceden yükler (uygulama açılışında arka planda).</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { _sc ??= await StartAsync(ct).ConfigureAwait(false); }
        catch (TranslatorException) { Fail(); }
        finally { _gate.Release(); }
    }

    private void Fail() { _failures++; _sc?.Dispose(); _sc = null; }

    public void Dispose()
    {
        _disposed = true;
        _sc?.Dispose(); _sc = null;
    }
}
