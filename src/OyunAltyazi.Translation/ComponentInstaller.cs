using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace OyunAltyazi.Translation;

/// <param name="Step">"python" | "packages" | "model"</param>
public sealed record SetupProgress(string Step, double Fraction, string Text);

/// <summary>İlk açılışta yerel çeviri bileşenlerini kurar: gömülü Python + pip paketleri + model. Her adım yeniden başlatılabilir.</summary>
public sealed class ComponentInstaller(HttpClient http)
{
    public const string PythonUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip";
    public const string PythonSha256 = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3";
    public const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    public const string ModelUrl = "https://github.com/berkaygulerr/OyunAltyazi-assets/releases/download/model-v1/opus-mt-tc-big-en-tr-ct2.zip";
    public const string ModelSha256 = "04a4b7a9c9a1e55b23a99a3aace45b115a9f8920dd29f09591d029e2c3d54d8d";

    public static bool HasNvidiaGpu() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));

    private static string DownloadsDir => Path.Combine(Ct2Paths.Root, "downloads");

    /// <summary>Eksik adımları sırayla tamamlar; ilk hatada <see cref="SetupException"/> fırlatır.</summary>
    public async Task RunAsync(IProgress<SetupProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(DownloadsDir);
        if (!File.Exists(Ct2Paths.PipMarker) || !File.Exists(Ct2Paths.RuntimePython)) await InstallPythonAsync(progress, ct);
        else progress.Report(new("python", 1, "Python hazır"));

        bool gpu = HasNvidiaGpu();
        if (!PackagesUpToDate(gpu)) await InstallPackagesAsync(gpu, progress, ct);
        else progress.Report(new("packages", 1, "Paketler hazır"));

        if (!Ct2Paths.ModelReady) await InstallModelAsync(progress, ct);
        else progress.Report(new("model", 1, "Model hazır"));
    }

    private static bool PackagesUpToDate(bool gpu)
    {
        if (!File.Exists(Ct2Paths.PackagesMarker)) return false;
        return !gpu || File.ReadAllText(Ct2Paths.PackagesMarker).Contains("cuda");
    }

    // ---------- 1) Python ----------
    private async Task InstallPythonAsync(IProgress<SetupProgress> p, CancellationToken ct)
    {
        try
        {
            var zip = Path.Combine(DownloadsDir, "python-embed.zip");
            await DownloadAsync(PythonUrl, zip, PythonSha256, f => p.Report(new("python", f * 0.6, "Python indiriliyor…")), ct);
            p.Report(new("python", 0.65, "Python çıkarılıyor…"));
            if (Directory.Exists(Ct2Paths.RuntimeDir)) Directory.Delete(Ct2Paths.RuntimeDir, true);
            Directory.CreateDirectory(Ct2Paths.RuntimeDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, Ct2Paths.RuntimeDir, true), ct);
            var pth = Directory.GetFiles(Ct2Paths.RuntimeDir, "python*._pth").FirstOrDefault()
                ?? throw new SetupException("python._pth bulunamadı.");
            var lines = File.ReadAllLines(pth).Select(l => l.TrimStart('#', ' ') == "import site" ? "import site" : l).ToList();
            if (!lines.Contains("Lib\\site-packages"))
            {
                int i = lines.IndexOf("import site");
                lines.Insert(i >= 0 ? i : lines.Count, "Lib\\site-packages");
            }
            File.WriteAllLines(pth, lines);
            Directory.CreateDirectory(Path.Combine(Ct2Paths.RuntimeDir, "Lib", "site-packages"));
            var getPip = Path.Combine(DownloadsDir, "get-pip.py");
            await DownloadAsync(GetPipUrl, getPip, null, f => p.Report(new("python", 0.7 + f * 0.1, "pip indiriliyor…")), ct);
            p.Report(new("python", 0.82, "pip kuruluyor…"));
            await RunPythonAsync([getPip, "--no-warn-script-location", "--disable-pip-version-check"], null, ct);
            File.WriteAllText(Ct2Paths.PipMarker, "ok");
            p.Report(new("python", 1, "Python hazır"));
        }
        catch (Exception e) when (e is not OperationCanceledException and not SetupException)
        {
            throw new SetupException("Python kurulamadı: " + e.Message, e);
        }
    }

    // ---------- 2) pip paketleri ----------
    private async Task InstallPackagesAsync(bool gpu, IProgress<SetupProgress> p, CancellationToken ct)
    {
        var pkgs = new List<string> { "ctranslate2==4.9.0", "sentencepiece==0.2.3" };
        if (gpu) pkgs.Add("nvidia-cublas-cu12");
        int seen = 0;
        double expected = gpu ? 14 : 8; // yaklaşık satır sayısı; çubuk yalnızca ilerleme göstergesi
        void OnLine(string l)
        {
            if (l.StartsWith("Collecting") || l.StartsWith("Downloading") || l.StartsWith("Installing") || l.StartsWith("Successfully")) seen++;
            var text = l.Length > 70 ? l[..70] + "…" : l;
            p.Report(new("packages", Math.Min(0.95, seen / expected), "Paketler: " + text));
        }
        try
        {
            p.Report(new("packages", 0.02, "Paketler indiriliyor…"));
            string[] args = ["-m", "pip", "install", "--no-warn-script-location", "--disable-pip-version-check", .. pkgs];
            await RunPythonAsync(args, OnLine, ct);
            File.WriteAllText(Ct2Paths.PackagesMarker, gpu ? "cuda" : "cpu");
            p.Report(new("packages", 1, "Paketler hazır" + (gpu ? " (GPU)" : " (CPU)")));
        }
        catch (Exception e) when (e is not OperationCanceledException and not SetupException)
        {
            throw new SetupException("Paketler kurulamadı: " + e.Message, e);
        }
    }

    // ---------- 3) Model ----------
    private async Task InstallModelAsync(IProgress<SetupProgress> p, CancellationToken ct)
    {
        try
        {
            var zip = Path.Combine(DownloadsDir, "model.zip");
            await DownloadAsync(ModelUrl, zip, ModelSha256, f => p.Report(new("model", f * 0.9, "Model indiriliyor…")), ct);
            p.Report(new("model", 0.92, "Model çıkarılıyor…"));
            var models = Path.GetDirectoryName(Ct2Paths.ModelDir)!;
            var tmp = Path.Combine(models, ".extract");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmp, true), ct);
            var inner = Path.Combine(tmp, Path.GetFileName(Ct2Paths.ModelDir));
            if (Directory.Exists(Ct2Paths.ModelDir)) Directory.Delete(Ct2Paths.ModelDir, true);
            Directory.Move(Directory.Exists(inner) ? inner : tmp, Ct2Paths.ModelDir);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            try { File.Delete(zip); } catch { }
            p.Report(new("model", 1, "Model hazır"));
        }
        catch (Exception e) when (e is not OperationCanceledException and not SetupException)
        {
            throw new SetupException("Model kurulamadı: " + e.Message, e);
        }
    }

    // ---------- yardımcılar ----------
    private static async Task RunPythonAsync(string[] args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Ct2Paths.RuntimePython)
        {
            WorkingDirectory = Ct2Paths.RuntimeDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new SetupException("Python başlatılamadı.");
        var tail = new Queue<string>();
        void Got(string? l)
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            lock (tail) { tail.Enqueue(l); if (tail.Count > 6) tail.Dequeue(); }
            onLine?.Invoke(l);
        }
        proc.OutputDataReceived += (_, e) => Got(e.Data);
        proc.ErrorDataReceived += (_, e) => Got(e.Data);
        proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
        await proc.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string t; lock (tail) t = string.Join(" | ", tail);
            throw new SetupException($"python çıkış kodu {proc.ExitCode}: {t}");
        }
    }

    /// <summary>Kaldığı yerden devam eden (Range) indirme; sha256 verilmişse doğrular.</summary>
    public async Task DownloadAsync(string url, string dest, string? sha256, Action<double> onProgress, CancellationToken ct)
    {
        if (sha256 is not null && File.Exists(dest) && await HashAsync(dest, ct) == sha256) { onProgress(1); return; }
        var part = dest + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 416) { File.Delete(part); continue; }
                resp.EnsureSuccessStatusCode();
                bool resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resumed) have = 0;
                long total = have + (resp.Content.Headers.ContentLength ?? 0);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[81920]; int n; long done = have; var last = Environment.TickCount64;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct); done += n;
                        if (total > 0 && Environment.TickCount64 - last > 150) { onProgress((double)done / total); last = Environment.TickCount64; }
                    }
                }
                if (sha256 is not null && await HashAsync(part, ct) != sha256)
                {
                    File.Delete(part);
                    throw new SetupException("İndirilen dosyanın SHA-256 doğrulaması başarısız.");
                }
                File.Move(part, dest, true);
                onProgress(1);
                return;
            }
            catch (Exception e) when (attempt < 3 && (e is HttpRequestException or IOException) && !ct.IsCancellationRequested)
            {
                await Task.Delay(1500 * (attempt + 1), ct); // ağ kopması: .part dosyasından devam et
            }
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }
}

public sealed class SetupException(string message, Exception? inner = null) : Exception(message, inner);
