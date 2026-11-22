using System.Security.Cryptography;
using System.Text;
using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

internal static class AutoPackFixtures
{
    public static byte[] Pack(string id, string en = "Cover me.", string tr = "Beni koru.") =>
        Encoding.UTF8.GetBytes("{\"format\":2,\"id\":\"" + id + "\",\"title\":\"T\",\"source\":\"x\",\"entries\":{\"" + TextKey.PackKey(en) + "\":\"" + tr + "\"},\"segments\":{},\"fuzzy\":{}}");

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static PackCatalogEntry Entry(byte[] data, string id = "g1", params string[] exes) =>
        new(id, "Game " + id, $"https://gitlab.com/sefflex/GameTranslator/-/raw/{new string('a', 40)}/yamalar/{id}.json", Sha(data), data.Length, exes.Length == 0 ? ["g1.exe"] : exes);

    public static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "oa-autopack-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
{
    public int Calls; public List<HttpRequestMessage> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    { Interlocked.Increment(ref Calls); lock (Requests) Requests.Add(r); return await f(r, ct); }
    public static FakeHandler Ok(byte[] body) => new((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
}

public class PackCatalogTests
{
    [Theory]
    [InlineData("eldenring.exe", "elden-ring")]
    [InlineData("EldenRing.exe", "elden-ring")]
    [InlineData("  rdr2.exe ", "red-dead-redemption-2")]
    [InlineData("GTA5.exe", "gta-v")]
    [InlineData("ModernWarfare.exe", "call-of-duty-modern-warfare-2019")]
    [InlineData("acshadows.exe", "assassins-creed-shadows")]
    [InlineData("MafiaTheOldCountry.exe", "mafia-the-old-country")]
    public void Confirmed_exe_aliases_resolve_to_their_pack(string exe, string id) => Assert.Equal(id, PackCatalog.Default.Find(exe)?.Id);

    [Theory]
    [InlineData("cod.exe")]                 // CoD MW2 (2022) / MW3 / BO6 share cod.exe: never guessed
    [InlineData("ModernWarfare2.exe")]
    [InlineData("iw3sp.exe")]
    [InlineData("eldenring")]               // no extension
    [InlineData("eldenring.exe.bak")]
    [InlineData("xeldenring.exe")]
    [InlineData("chrome.exe")]
    [InlineData("msedge.exe")]
    [InlineData("gta5_enhanced.exe")]       // different edition, not confirmed
    [InlineData("")]
    [InlineData("  ")]
    public void Unknown_or_ambiguous_executables_never_match(string exe) => Assert.Null(PackCatalog.Default.Find(exe));

    [Fact] public void A_path_or_window_title_is_not_an_alias()
    {
        Assert.Null(PackCatalog.Default.Find(@"C:\Games\Elden Ring\game\eldenring.exe.txt"));
        Assert.Null(PackCatalog.Default.Find("ELDEN RING™"));
        Assert.Null(PackCatalog.Default.Find("Call of Duty: Modern Warfare"));
    }

    [Fact] public void Default_catalog_is_pinned_checksummed_and_unambiguous()
    {
        var all = PackCatalog.Default.Entries;
        Assert.Equal(9, all.Count);
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
        var exes = all.SelectMany(e => e.Exes).ToList();
        Assert.Equal(exes.Count, exes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in all)
        {
            Assert.Matches("^https://gitlab\\.com/sefflex/GameTranslator/-/raw/[0-9a-f]{40}/yamalar/" + e.Id + "\\.json$", e.Url);
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.InRange(e.Size, 1, GamePack.MaxFileBytes);
            Assert.All(e.Exes, x => { Assert.Equal(x, x.ToLowerInvariant()); Assert.EndsWith(".exe", x); Assert.DoesNotContain('/', x); Assert.DoesNotContain('\\', x); });
        }
        Assert.DoesNotContain("cod.exe", exes);
    }

    [Fact] public void A_catalog_rejects_duplicate_aliases_and_foreign_hosts()
    {
        var d = AutoPackFixtures.Pack("a");
        Assert.Throws<ArgumentException>(() => new PackCatalog([AutoPackFixtures.Entry(d, "a", "x.exe"), AutoPackFixtures.Entry(d, "b", "X.EXE")]));
        Assert.Throws<ArgumentException>(() => new PackCatalog([AutoPackFixtures.Entry(d, "a") with { Url = "https://evil.example/a.json" }]));
        Assert.Throws<ArgumentException>(() => new PackCatalog([AutoPackFixtures.Entry(d, "a") with { Url = "http://gitlab.com/a.json" }]));
        Assert.Throws<ArgumentException>(() => new PackCatalog([AutoPackFixtures.Entry(d, "a") with { Sha256 = "abc" }]));
    }
}

public class AutoPackServiceTests
{
    private static (AutoPackService s, FakeHandler h, string dir, PackCatalogEntry e) Make(byte[] served, byte[]? published = null, TimeSpan? timeout = null, FakeHandler? handler = null)
    {
        published ??= served;
        var e = AutoPackFixtures.Entry(published, "g1", "g1.exe");
        var h = handler ?? FakeHandler.Ok(served);
        var dir = AutoPackFixtures.TempDir();
        return (new AutoPackService(new PackCatalog([e]), dir, new HttpClient(h), timeout), h, dir, e);
    }

    [Fact] public async Task Downloads_validates_and_installs_a_pack_for_the_exact_exe()
    {
        var (s, h, dir, e) = Make(AutoPackFixtures.Pack("g1"));
        var r = await s.GetAsync("G1.exe", default);
        Assert.Equal(AutoPackState.Ready, r.State); Assert.False(r.FromCache);
        Assert.True(r.Pack!.TryGet("Cover me.", out var t)); Assert.Equal("Beni koru.", t);
        Assert.Equal(1, h.Calls); Assert.Equal(e.Url, h.Requests[0].RequestUri!.ToString()); Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.Null(h.Requests[0].Content);                                    // nothing about the user or the screen is sent
        Assert.Single(Directory.GetFiles(dir)); Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact] public async Task Second_request_uses_the_cache_even_when_offline()
    {
        var data = AutoPackFixtures.Pack("g1");
        var (s, h, dir, e) = Make(data);
        await s.GetAsync("g1.exe", default);
        var offline = new FakeHandler((_, _) => throw new HttpRequestException("offline"));
        var s2 = new AutoPackService(new PackCatalog([e]), dir, new HttpClient(offline));
        var r = await s2.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Ready, r.State); Assert.True(r.FromCache); Assert.Equal(0, offline.Calls);
    }

    [Fact] public async Task Offline_without_cache_fails_softly()
    {
        var (s, h, dir, _) = Make(AutoPackFixtures.Pack("g1"), handler: new FakeHandler((_, _) => throw new HttpRequestException("offline")));
        var r = await s.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Failed, r.State); Assert.Null(r.Pack); Assert.NotNull(r.Error); Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task Wrong_checksum_is_rejected_and_nothing_is_cached()
    {
        var (s, h, dir, _) = Make(served: AutoPackFixtures.Pack("g1", tr: "Zehirli."), published: AutoPackFixtures.Pack("g1"));
        var r = await s.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Failed, r.State); Assert.Null(r.Pack); Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task Pack_id_must_match_the_catalog_entry()
    {
        var other = AutoPackFixtures.Pack("someone-else");
        var (s, _, dir, _) = Make(other);                       // checksum matches, id does not
        var r = await s.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Failed, r.State); Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task Oversized_declared_or_streamed_body_is_rejected()
    {
        var data = AutoPackFixtures.Pack("g1");
        var big = data.Concat(new byte[10_000]).ToArray();
        var (s, _, dir, _) = Make(served: big, published: data);
        Assert.Equal(AutoPackState.Failed, (await s.GetAsync("g1.exe", default)).State);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task Http_error_and_redirect_are_failures()
    {
        foreach (var code in new[] { System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.Found, System.Net.HttpStatusCode.TooManyRequests })
        {
            var (s, _, dir, _) = Make(AutoPackFixtures.Pack("g1"), handler: new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
            Assert.Equal(AutoPackState.Failed, (await s.GetAsync("g1.exe", default)).State);
            Assert.Empty(Directory.GetFiles(dir));
        }
    }

    [Fact] public async Task A_hanging_download_times_out()
    {
        var hang = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); });
        var (s, _, _, _) = Make(AutoPackFixtures.Pack("g1"), timeout: TimeSpan.FromMilliseconds(150), handler: hang);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await s.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Failed, r.State); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact] public async Task A_corrupted_cache_file_is_discarded_and_redownloaded()
    {
        var (s, h, dir, _) = Make(AutoPackFixtures.Pack("g1"));
        await s.GetAsync("g1.exe", default);
        var f = Directory.GetFiles(dir).Single(); File.WriteAllText(f, "{tampered");
        var s2 = new AutoPackService(new PackCatalog([AutoPackFixtures.Entry(AutoPackFixtures.Pack("g1"))]), dir, new HttpClient(h));
        var r = await s2.GetAsync("g1.exe", default);
        Assert.Equal(AutoPackState.Ready, r.State); Assert.False(r.FromCache); Assert.Equal(2, h.Calls);
    }

    [Fact] public async Task Concurrent_requests_share_one_download()
    {
        var gate = new TaskCompletionSource();
        var data = AutoPackFixtures.Pack("g1");
        var h = new FakeHandler(async (_, _) => { await gate.Task; return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; });
        var (s, _, _, _) = Make(data, handler: h);
        var all = Enumerable.Range(0, 5).Select(_ => s.GetAsync("g1.exe", default)).ToArray();
        gate.SetResult();
        var rs = await Task.WhenAll(all);
        Assert.All(rs, r => Assert.Equal(AutoPackState.Ready, r.State)); Assert.Equal(1, h.Calls);
    }

    [Fact] public async Task Unknown_exe_does_no_io_at_all()
    {
        var (s, h, dir, _) = Make(AutoPackFixtures.Pack("g1"));
        var r = await s.GetAsync("cod.exe", default);
        Assert.Equal(AutoPackState.NoMatch, r.State); Assert.Equal(0, h.Calls); Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact] public async Task Caller_cancellation_does_not_poison_the_shared_download()
    {
        var gate = new TaskCompletionSource(); var data = AutoPackFixtures.Pack("g1");
        var h = new FakeHandler(async (_, _) => { await gate.Task; return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }; });
        var (s, _, _, _) = Make(data, handler: h);
        using var cts = new CancellationTokenSource();
        var a = s.GetAsync("g1.exe", cts.Token); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        var b = s.GetAsync("g1.exe", default); gate.SetResult();
        Assert.Equal(AutoPackState.Ready, (await b).State); Assert.Equal(1, h.Calls);
    }
}

public class AutoPackCoordinatorTests
{
    private static (AutoPackCoordinator c, LocalTranslations l, FakeHandler h, TaskCompletionSource gate) Make(bool enabled = true, Func<bool>? en = null, string[]? exes = null)
    {
        var gate = new TaskCompletionSource();
        var cat = new List<PackCatalogEntry>(); var bodies = new Dictionary<string, byte[]>();
        foreach (var (id, exe) in new[] { ("g1", "g1.exe"), ("g2", "g2.exe") })
        { var d = AutoPackFixtures.Pack(id, tr: "TR-" + id); bodies[id] = d; cat.Add(AutoPackFixtures.Entry(d, id, exe)); }
        var h = new FakeHandler(async (r, ct) => { await gate.Task; var id = Path.GetFileNameWithoutExtension(r.RequestUri!.AbsolutePath); return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bodies[id]) }; });
        var l = new LocalTranslations();
        var svc = new AutoPackService(new PackCatalog(cat), AutoPackFixtures.TempDir(), new HttpClient(h));
        return (new AutoPackCoordinator(l, svc, en ?? (() => enabled)), l, h, gate);
    }
    private static string? Tr(LocalTranslations l, string g) => l.TryGet(g, "Cover me.", out var t, out _) ? t : null;

    [Fact] public async Task Selecting_a_known_game_installs_the_pack_asynchronously()
    {
        var (c, l, _, gate) = Make();
        var task = c.SelectAsync("g1.exe");
        Assert.Equal(AutoPackState.Loading, c.State); Assert.Null(Tr(l, "g1.exe"));      // select returns before the download completes
        gate.SetResult(); await task;
        Assert.Equal(AutoPackState.Ready, c.State); Assert.Equal("TR-g1", Tr(l, "g1.exe"));
        Assert.Equal(TranslationSource.Pack, Src(l, "g1.exe"));
    }
    private static TranslationSource Src(LocalTranslations l, string g) { l.TryGet(g, "Cover me.", out _, out var s); return s; }

    [Fact] public async Task Unknown_game_and_wrong_cod_do_nothing()
    {
        var (c, l, h, gate) = Make(); gate.SetResult();
        foreach (var exe in new[] { "cod.exe", "chrome.exe", "", null })
        { await c.SelectAsync(exe); Assert.Equal(AutoPackState.NoMatch, c.State); }
        Assert.Equal(0, h.Calls); Assert.Null(l.PackFor("cod.exe"));
    }

    [Fact] public async Task A_manual_pack_always_wins_and_nothing_is_downloaded()
    {
        var (c, l, h, gate) = Make(); gate.SetResult();
        var manual = GamePack.Parse("""{"version":2,"game":"g1.exe","entries":[{"en":"Cover me.","tr":"MANUEL"}]}""", "g1.exe").Pack!;
        l.SetPack("g1.exe", manual);
        await c.SelectAsync("g1.exe");
        Assert.Equal(AutoPackState.Manual, c.State); Assert.Equal(0, h.Calls); Assert.Equal("MANUEL", Tr(l, "g1.exe"));
    }

    [Fact] public async Task A_manual_pack_imported_while_downloading_is_not_overwritten()
    {
        var (c, l, _, gate) = Make();
        var task = c.SelectAsync("g1.exe");
        l.SetPack("g1.exe", GamePack.Parse("""{"version":2,"game":"g1.exe","entries":[{"en":"Cover me.","tr":"MANUEL"}]}""", "g1.exe").Pack!);
        gate.SetResult(); await task;
        Assert.Equal("MANUEL", Tr(l, "g1.exe")); Assert.Equal(AutoPackState.Manual, c.State);
    }

    [Fact] public async Task A_manual_glossary_still_outranks_the_auto_pack()
    {
        var (c, l, _, gate) = Make(); gate.SetResult();
        l.SetGlossary("g1.exe", GamePack.Parse("""{"version":2,"game":"g1.exe","entries":[{"en":"Cover me.","tr":"SÖZLÜK"}]}""", "g1.exe").Pack!);
        await c.SelectAsync("g1.exe");
        Assert.Equal(AutoPackState.Ready, c.State); Assert.Equal("SÖZLÜK", Tr(l, "g1.exe")); Assert.Equal(TranslationSource.Glossary, Src(l, "g1.exe"));
    }

    [Fact] public async Task Switching_games_mid_download_drops_the_stale_result()
    {
        var (c, l, _, gate) = Make();
        var a = c.SelectAsync("g1.exe"); var b = c.SelectAsync("g2.exe");
        gate.SetResult(); await Task.WhenAll(a, b);
        Assert.Null(Tr(l, "g1.exe")); Assert.Equal("TR-g2", Tr(l, "g2.exe")); Assert.Equal("g2.exe", c.Exe);
    }

    [Fact] public async Task Switching_to_an_unknown_window_mid_download_drops_it_and_releases_other_games_packs()
    {
        var (c, l, _, gate) = Make(); gate.SetResult();
        await c.SelectAsync("g1.exe"); Assert.Equal("TR-g1", Tr(l, "g1.exe"));
        await c.SelectAsync("notepad.exe");
        Assert.Null(l.PackFor("g1.exe")); Assert.Equal(AutoPackState.NoMatch, c.State);
    }

    [Fact] public async Task Opting_out_clears_the_auto_pack_and_in_flight_results()
    {
        bool on = true; var (c, l, _, gate) = Make(en: () => on);
        var inflight = c.SelectAsync("g1.exe"); on = false;
        gate.SetResult(); await inflight;
        Assert.Null(Tr(l, "g1.exe")); Assert.Equal(AutoPackState.Off, c.State);

        on = true; await c.SelectAsync("g1.exe"); Assert.Equal("TR-g1", Tr(l, "g1.exe"));
        on = false; await c.SelectAsync("g1.exe");
        Assert.Null(Tr(l, "g1.exe")); Assert.Equal(AutoPackState.Off, c.State);
    }

    [Fact] public async Task Opting_out_never_touches_a_manual_pack()
    {
        bool on = false; var (c, l, _, gate) = Make(en: () => on); gate.SetResult();
        l.SetPack("g1.exe", GamePack.Parse("""{"version":2,"game":"g1.exe","entries":[{"en":"Cover me.","tr":"MANUEL"}]}""", "g1.exe").Pack!);
        await c.SelectAsync("g1.exe");
        Assert.Equal("MANUEL", Tr(l, "g1.exe"));
    }

    [Fact] public async Task Failure_leaves_normal_translation_path_untouched()
    {
        var gate = new TaskCompletionSource(); gate.SetResult();
        var d = AutoPackFixtures.Pack("g1"); var l = new LocalTranslations();
        var svc = new AutoPackService(new PackCatalog([AutoPackFixtures.Entry(d)]), AutoPackFixtures.TempDir(), new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("offline"))));
        var c = new AutoPackCoordinator(l, svc, () => true);
        await c.SelectAsync("g1.exe");
        Assert.Equal(AutoPackState.Failed, c.State); Assert.False(l.TryGet("g1.exe", "Cover me.", out _, out _)); Assert.Null(l.PackFor("g1.exe"));
    }

    [Fact] public async Task Selecting_the_same_game_again_does_not_download_twice()
    {
        var (c, l, h, gate) = Make(); gate.SetResult();
        await c.SelectAsync("g1.exe"); await c.SelectAsync("g1.exe");
        Assert.Equal(1, h.Calls); Assert.Equal("TR-g1", Tr(l, "g1.exe"));
    }
}

/// <summary>
/// Opt-in checks against REAL published packs (nothing committed). OA_LIVE=1 fetches from GitLab; OA_LIVE_DIR=folder serves the previously downloaded raw files
/// (named id.json) through the same service/handler path, which is useful when GitLab answers 429.
/// </summary>
public class AutoPackLiveTests
{
    private static HttpClient Client()
    {
        var dir = Environment.GetEnvironmentVariable("OA_LIVE_DIR");
        if (string.IsNullOrEmpty(dir)) return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        return new HttpClient(new FakeHandler((r, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(dir, Path.GetFileName(r.RequestUri!.AbsolutePath)))) })));
    }
    private static bool Enabled => Environment.GetEnvironmentVariable("OA_LIVE") == "1" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OA_LIVE_DIR"));

    [Fact] public async Task Real_modern_warfare_pack_downloads_verifies_and_matches_a_known_line()
    {
        if (!Enabled) return;
        var dir = AutoPackFixtures.TempDir();
        var s = new AutoPackService(PackCatalog.Default, dir, Client());
        var r = await s.GetAsync("ModernWarfare.exe", default);
        Assert.True(r.State == AutoPackState.Ready, r.Error);
        Assert.False(r.FromCache); Assert.Equal(51147, r.Pack!.Count);
        Assert.True(r.Pack.TryGet("Cover me.", out var tr)); Assert.False(string.IsNullOrWhiteSpace(tr)); Console.WriteLine("Cover me. => " + tr);
        var again = await new AutoPackService(PackCatalog.Default, dir, new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException()))).GetAsync("modernwarfare.exe", default);
        Assert.True(again.FromCache); Assert.Equal(51147, again.Pack!.Count);
        Directory.Delete(dir, true);
    }

    [Fact] public async Task Every_catalog_entry_matches_its_published_file()
    {
        if (!Enabled) return;
        var dir = AutoPackFixtures.TempDir();
        var s = new AutoPackService(PackCatalog.Default, dir, Client());
        foreach (var e in PackCatalog.Default.Entries)
        {
            var r = await s.GetAsync(e.Exes[0], default);
            Assert.True(r.State == AutoPackState.Ready, e.Id + ": " + r.Error);
            Console.WriteLine($"{e.Id}: {r.Pack!.Count} lines");
        }
        Directory.Delete(dir, true);
    }
}
