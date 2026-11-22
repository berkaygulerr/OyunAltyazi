using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OyunAltyazi.Core;

/// <summary>One published ready-made pack: where it lives (commit-pinned raw URL), what it must hash to, and the exact executables it applies to.</summary>
public sealed record PackCatalogEntry(string Id, string Title, string Url, string Sha256, long Size, string[] Exes);

/// <summary>
/// Small reviewed catalog of genuinely published GameTranslator packs (https://gitlab.com/sefflex/GameTranslator, folder yamalar/). Each entry pins a commit
/// and a SHA-256. Games are recognised ONLY by an exact, lower-case executable file name that was confirmed against Discord's public detectable-application
/// list (never by window title, never fuzzy). Shared or ambiguous executables (cod.exe: MW2 2022 / MW3 / BO6 ...) and editions without a confirmed exe are left out.
/// </summary>
public sealed class PackCatalog
{
    private readonly Dictionary<string, PackCatalogEntry> _byExe = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PackCatalogEntry> Entries { get; }

    public PackCatalog(IEnumerable<PackCatalogEntry> entries)
    {
        Entries = entries.ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || !ids.Add(e.Id)) throw new ArgumentException("Duplicate or empty pack id: " + e.Id);
            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) || u.Query.Length > 0)
                throw new ArgumentException("Pack URL must be a plain https://gitlab.com URL: " + e.Url);
            if (e.Sha256.Length != 64 || !e.Sha256.All(Uri.IsHexDigit)) throw new ArgumentException("Bad SHA-256 for " + e.Id);
            if (e.Size <= 0 || e.Size > GamePack.MaxFileBytes) throw new ArgumentException("Bad size for " + e.Id);
            foreach (var x in e.Exes)
                if (!_byExe.TryAdd(x.Trim(), e)) throw new ArgumentException("Executable alias used twice: " + x);
        }
    }

    /// <summary>Exact executable file name (case-insensitive, trimmed). No paths, no titles, no partial matches.</summary>
    public PackCatalogEntry? Find(string? exe) => string.IsNullOrWhiteSpace(exe) ? null : _byExe.GetValueOrDefault(exe.Trim());

    private const string Root = "https://gitlab.com/sefflex/GameTranslator/-/raw/70e12ef2e3a0f5122b099ce9d8a682cc32c37d82/yamalar/";
    private static PackCatalogEntry E(string id, string title, long size, string sha, params string[] exes) => new(id, title, Root + id + ".json", sha, size, exes);

    public static PackCatalog Default { get; } = new(
    [
        E("elden-ring", "Elden Ring + Shadow of the Erdtree", 4228848, "d95fa27cbca27d03bab15ccf580f3a85b9d8480aab0e1c41b5e541a2e313efce", "eldenring.exe"),
        E("red-dead-redemption-2", "Red Dead Redemption 2", 17463288, "b76a8968c3e21f0c891ae495db6a769a8c11fba1c3154802472cc4fd1ad80b7c", "rdr2.exe"),
        E("gta-v", "Grand Theft Auto V", 18081009, "801463bb1426c281602e39d0b2203ff354a08ea120ecd76b6c0ee90c34048b42", "gta5.exe"),
        E("watch-dogs", "Watch Dogs", 3222317, "68fdc4cffdd42faab284f190276692d79d81a1ff1d50d5e483dc4a6b17dda79c", "watch_dogs.exe"),
        E("call-of-duty-modern-warfare-2019", "Call of Duty: Modern Warfare (2019)", 5066467, "a78060c9a844c7eb256cdf81b029b3e1570eacc5fe4b49df6ba99490a03c7fcc", "modernwarfare.exe"),
        E("assassins-creed-shadows", "Assassin's Creed Shadows", 7033306, "ecb4eb44a35dd4561e3308bd922831583b1e30531f6cc0b6143436b84c1d712a", "acshadows.exe"),
        E("final-fantasy-vii-remake", "Final Fantasy VII Remake", 851882, "5dcf7c6dcfb81dbeb0504e36bc0b567095c1509872feb8b60a5882fc9e3e1a37", "ff7remake_.exe"),
        E("mafia-the-old-country", "Mafia: The Old Country", 2564151, "2e3a6e2558780e2d1627c5fca28b83925e68f045444da525ba783f0d0861954c", "mafiatheoldcountry.exe"),
        E("death-stranding-directors-cut", "Death Stranding: Director's Cut", 790652, "b907c25dc04b07b115d0bc145828b2a1f4c3678d48e8c22773ddc950fe845452", "ds.exe"),
    ]);
}

public enum AutoPackState { Off, NoMatch, Loading, Ready, Manual, Failed }

public sealed record AutoPackResult(AutoPackState State, GamePack? Pack = null, string? Title = null, string? Error = null, bool FromCache = false);

/// <summary>
/// Fetches (once) and caches a catalog pack. Plain GET of the pinned URL only: no query, headers, cookies or body, so nothing about the user or the screen leaves the PC.
/// Bounded by size (catalog Size), a total timeout and no redirects; verified by SHA-256 and by the pack's own "id"; installed with an atomic rename.
/// Concurrent requests for the same pack share one download that the caller's cancellation cannot abort.
/// </summary>
public sealed class AutoPackService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private readonly PackCatalog _catalog; private readonly string _dir; private readonly HttpClient _http; private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, Task<AutoPackResult>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public AutoPackService(PackCatalog catalog, string cacheDir, HttpClient http, TimeSpan? timeout = null)
    { _catalog = catalog; _dir = cacheDir; _http = http; _timeout = timeout ?? DefaultTimeout; }

    public PackCatalog Catalog => _catalog;

    public async Task<AutoPackResult> GetAsync(string exe, CancellationToken ct)
    {
        var entry = _catalog.Find(exe);
        if (entry is null) return new(AutoPackState.NoMatch);
        var game = exe.Trim();
        var task = _inflight.GetOrAdd(entry.Id + "|" + game, k => Task.Run(async () =>
        {
            try { return await LoadAsync(entry, game); }
            finally { _inflight.TryRemove(k, out var _done); }
        }));
        return await task.WaitAsync(ct);
    }

    private string CachePath(PackCatalogEntry e) => Path.Combine(_dir, $"{e.Id}-{e.Sha256[..12]}.json");

    private static AutoPackResult? Validate(PackCatalogEntry e, byte[] data, string game, bool fromCache, out string error)
    {
        error = "";
        if (data.Length == 0 || data.Length > e.Size) { error = "Beklenmeyen boyut."; return null; }
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(e.Sha256, StringComparison.OrdinalIgnoreCase)) { error = "SHA-256 uyuşmuyor."; return null; }
        var r = GamePack.ParseBytes(data, game);
        if (r.Pack is null) { error = r.Error ?? "Paket okunamadı."; return null; }
        if (!string.Equals(r.Pack.Id, e.Id, StringComparison.Ordinal)) { error = "Paket kimliği uyuşmuyor."; return null; }
        return new(AutoPackState.Ready, r.Pack, e.Title, null, fromCache);
    }

    private async Task<AutoPackResult> LoadAsync(PackCatalogEntry e, string game)
    {
        var path = CachePath(e);
        try
        {
            if (File.Exists(path))
            {
                var ok = new FileInfo(path).Length <= e.Size ? Validate(e, await File.ReadAllBytesAsync(path), game, true, out _) : null;
                if (ok is not null) return ok;
                try { File.Delete(path); } catch (IOException) { }     // damaged or tampered: fall through to a fresh download
            }

            using var cts = new CancellationTokenSource(_timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, e.Url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return Fail($"Sunucu {(int)resp.StatusCode} döndürdü.");
            if (resp.Content.Headers.ContentLength is long len && len > e.Size) return Fail("Paket beklenenden büyük.");
            using var ms = new MemoryStream();
            await using (var s = await resp.Content.ReadAsStreamAsync(cts.Token))
            {
                var buf = new byte[81920]; int n;
                while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
                {
                    if (ms.Length + n > e.Size) return Fail("Paket beklenenden büyük.");
                    ms.Write(buf, 0, n);
                }
            }
            var data = ms.ToArray();
            var res = await Task.Run(() => Validate(e, data, game, false, out _));
            if (res is null) { Validate(e, data, game, false, out var err); return Fail(err); }

            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(tmp, data); File.Move(tmp, path, overwrite: true); }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
                foreach (var old in Directory.GetFiles(_dir, e.Id + "-*.json")) if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch (IOException) { } }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* works in memory this session; just not cached */ }
            return res;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return Fail(ex is OperationCanceledException ? "Zaman aşımı." : ex.Message);
        }
        static AutoPackResult Fail(string m) => new(AutoPackState.Failed, Error: m);
    }
}

/// <summary>Keeps the auto pack of the CURRENTLY selected game in <see cref="LocalTranslations"/>; manual packs always win and are never replaced or removed here.</summary>
public sealed class AutoPackCoordinator(LocalTranslations local, AutoPackService service, Func<bool> enabled)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GamePack> _auto = new(StringComparer.OrdinalIgnoreCase);
    private int _gen;

    public AutoPackState State { get; private set; } = AutoPackState.NoMatch;
    public string? Exe { get; private set; }
    public string? Title { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    /// <summary>Call whenever the selected game, the opt-out switch or the manual files change. Returns once the (possible) download has finished; never blocks the caller before that.</summary>
    public Task SelectAsync(string? exe)
    {
        exe = exe?.Trim();
        int gen; Task? work = null;
        lock (_gate)
        {
            gen = ++_gen; Exe = exe;
            foreach (var (g, p) in _auto.ToList())
                if (!string.Equals(g, exe, StringComparison.OrdinalIgnoreCase) || !enabled()) { local.ClearPackIf(g, p); _auto.Remove(g); }
            Title = null; Error = null;
            if (string.IsNullOrEmpty(exe) || service.Catalog.Find(exe) is not { } entry) State = AutoPackState.NoMatch;
            else if (!enabled()) State = AutoPackState.Off;
            else if (local.PackFor(exe) is { } cur) { var mine = _auto.GetValueOrDefault(exe) == cur; State = mine ? AutoPackState.Ready : AutoPackState.Manual; Title = entry.Title; }
            else { State = AutoPackState.Loading; Title = entry.Title; work = RunAsync(gen, exe); }
        }
        Changed?.Invoke();
        return work ?? Task.CompletedTask;
    }

    private async Task RunAsync(int gen, string exe)
    {
        AutoPackResult r;
        try { r = await service.GetAsync(exe, default); }
        catch (Exception ex) { r = new(AutoPackState.Failed, Error: ex.Message); }
        lock (_gate)
        {
            if (gen != _gen) return;                                  // a newer selection / opt-out superseded this one
            if (!enabled()) State = AutoPackState.Off;
            else if (r.State == AutoPackState.Ready && r.Pack is not null)
            {
                if (local.TrySetPackIfAbsent(exe, r.Pack)) { _auto[exe] = r.Pack; State = AutoPackState.Ready; }
                else State = AutoPackState.Manual;                    // the user's own file arrived first
            }
            else { State = AutoPackState.Failed; Error = r.Error; }
        }
        Changed?.Invoke();
    }
}
