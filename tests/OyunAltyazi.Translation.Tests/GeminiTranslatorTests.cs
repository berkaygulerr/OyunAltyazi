using System.Net;
using System.Text;
using System.Text.Json;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;
namespace OyunAltyazi.Translation.Tests;

public class GeminiTranslatorTests
{
    private const string Key = "AIzaSy-TEST-KEY-1234567890";
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public int Calls; public Uri? Uri; public HttpRequestMessage? Req; public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Uri = r.RequestUri; Req = r;
            Body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            return await f(r, ct);
        }
    }
    private static HttpResponseMessage Ok(string text, string finish = "STOP") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } }, finishReason = finish } } }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Raw(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static GeminiTranslator T(Fake h, string? key = Key, string model = "gemini-3.8-flash", int timeoutMs = 2000, Func<DateTime>? now = null)
        => new(new HttpClient(h), () => key, model, TimeSpan.FromMilliseconds(timeoutMs), now);
    private static TranslationContext Ctx(params string[] prev) => new("secret-game.exe", prev, "Price");

    [Fact] public async Task Request_is_pinned_endpoint_key_only_in_header_never_in_url_or_body()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("Yukarı çık.")));
        var r = await T(h).TranslateAsync("Move up.", Ctx("Hold the line."), default);
        Assert.Equal("Yukarı çık.", r);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", h.Uri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, h.Req!.Method);
        Assert.Equal(Key, h.Req.Headers.GetValues("x-goog-api-key").Single());
        Assert.Empty(h.Uri.Query); Assert.DoesNotContain(Key, h.Uri.ToString()); Assert.DoesNotContain(Key, h.Body);
        Assert.Null(h.Req.Headers.Authorization);
    }

    [Fact] public async Task Sends_only_subtitle_text_bounded_context_never_the_game_name()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("tamam")));
        var prev = Enumerable.Range(0, 10).Select(i => $"line number {i} " + new string('x', 600)).ToArray();
        await T(h).TranslateAsync("Move up.", Ctx(prev), default);
        Assert.DoesNotContain("secret-game", h.Body);
        Assert.Contains("Move up.", h.Body);
        Assert.Contains("line number 9", h.Body); Assert.Contains("line number 7", h.Body);
        Assert.DoesNotContain("line number 6", h.Body);                 // at most the last 3 lines
        Assert.DoesNotContain(new string('x', 400), h.Body);             // each line truncated
        Assert.True(h.Body.Length < 4000);
    }

    [Fact] public async Task Long_current_line_is_truncated()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("tamam")));
        await T(h).TranslateAsync(new string('a', 5000), Ctx(), default);
        Assert.True(h.Body.Length < 3500);
    }

    [Fact] public async Task Thinking_level_low_for_gemini3_but_not_for_older_models()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("tamam")));
        await T(h).TranslateAsync("Hi.", Ctx(), default);
        Assert.Contains("thinkingLevel", h.Body);
        var h2 = new Fake((_, _) => Task.FromResult(Ok("tamam")));
        await T(h2, model: "gemini-2.5-flash").TranslateAsync("Hi.", Ctx(), default);
        Assert.DoesNotContain("thinkingLevel", h2.Body);
    }

    [Fact] public async Task No_key_means_no_network()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("x")));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h, key: null).TranslateAsync("Hi.", Ctx(), default));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h, key: "  ").TranslateAsync("Hi.", Ctx(), default));
        Assert.Equal(0, h.Calls);
    }

    [Theory] [InlineData("gemini-3.8-flash:evil")] [InlineData("../x")] [InlineData("")] [InlineData("gpt")]
    public async Task Invalid_model_names_never_reach_the_network(string model)
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("x")));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h, model: model).TranslateAsync("Hi.", Ctx(), default));
        Assert.Equal(0, h.Calls);
    }

    [Fact] public async Task Http_429_is_quiet_and_starts_a_cooldown_without_further_requests()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var h = new Fake((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var t = T(h, now: () => now);
        var e = await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi.", Ctx(), default));
        Assert.Equal(429, e.StatusCode); Assert.DoesNotContain(Key, e.Message);
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Again.", Ctx(), default));
        Assert.Equal(1, h.Calls);                                  // cooled down: no request
        now = now.AddMinutes(5);                                    // cooldown over
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Again.", Ctx(), default));
        Assert.Equal(2, h.Calls);
    }

    [Fact] public async Task Retry_after_header_is_honoured_but_capped()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var h = new Fake((_, _) => { var r = new HttpResponseMessage((HttpStatusCode)429); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(5)); return Task.FromResult(r); });
        var t = T(h, now: () => now);
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi.", Ctx(), default));
        now = now.AddMinutes(3);                                    // cap is 2 minutes
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Again.", Ctx(), default));
        Assert.Equal(2, h.Calls);
    }

    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(403)]
    public async Task Bad_key_or_request_stops_further_requests_until_key_changes(int status)
    {
        string? key = Key;
        var h = new Fake((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var t = new GeminiTranslator(new HttpClient(h), () => key, "gemini-3.8-flash", TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi.", Ctx(), default));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi.", Ctx(), default));
        Assert.Equal(1, h.Calls);
        key = "AIzaSy-NEW-KEY";                                     // user entered another key
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi.", Ctx(), default));
        Assert.Equal(2, h.Calls);
    }

    [Fact] public async Task Timeout_is_a_quiet_translator_exception_not_cancellation()
    {
        var h = new Fake(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok("x"); });
        var e = await Assert.ThrowsAsync<TranslatorException>(() => T(h, timeoutMs: 80).TranslateAsync("Hi.", Ctx(), default));
        Assert.DoesNotContain(Key, e.Message);
    }

    [Fact] public async Task Caller_cancellation_surfaces_as_cancellation_and_frees_the_slot()
    {
        var started = new TaskCompletionSource();
        var h = new Fake(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok("x"); });
        var t = T(h, timeoutMs: 5000);
        using var cts = new CancellationTokenSource();
        var call = t.TranslateAsync("Hi.", Ctx(), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, t.InFlight);
    }

    [Fact] public async Task Concurrency_is_limited_without_queueing_extra_requests_fail_fast()
    {
        var release = new TaskCompletionSource();
        var h = new Fake(async (_, ct) => { await release.Task.WaitAsync(ct); return Ok("tamam"); });
        var t = T(h, timeoutMs: 5000);
        var running = Enumerable.Range(0, GeminiTranslator.MaxConcurrent).Select(i => t.TranslateAsync("a" + i, Ctx(), default)).ToArray();
        await Task.Delay(100);
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("extra", Ctx(), default));
        Assert.Equal(GeminiTranslator.MaxConcurrent, h.Calls);
        release.SetResult(); await Task.WhenAll(running);
        Assert.Equal(0, t.InFlight);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"SAFETY\"}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"   \"}]},\"finishReason\":\"STOP\"}]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"kısmi\"}]},\"finishReason\":\"MAX_TOKENS\"}]}")]
    [InlineData("<html>nope")]
    public async Task Invalid_content_is_rejected_quietly(string json)
    {
        var h = new Fake((_, _) => Task.FromResult(Raw(json)));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h).TranslateAsync("Hi.", Ctx(), default));
    }

    [Fact] public async Task Runaway_or_multiline_output_is_rejected_and_wrapping_is_stripped()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok(new string('a', 3000))));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h).TranslateAsync("Hi.", Ctx(), default));
        var h2 = new Fake((_, _) => Task.FromResult(Ok("\"Selam.\"\n")));
        Assert.Equal("Selam.", await T(h2).TranslateAsync("Hi.", Ctx(), default));
        var h3 = new Fake((_, _) => Task.FromResult(Ok("Selam.\nBu bir açıklama.")));
        await Assert.ThrowsAsync<TranslatorException>(() => T(h3).TranslateAsync("Hi.", Ctx(), default));
    }

    [Fact] public async Task Thought_parts_are_ignored()
    {
        var h = new Fake((_, _) => Task.FromResult(Raw("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"düşünce\",\"thought\":true},{\"text\":\"Selam.\"}]},\"finishReason\":\"STOP\"}]}")));
        Assert.Equal("Selam.", await T(h).TranslateAsync("Hi.", Ctx(), default));
    }

    [Fact] public async Task Continuation_prompt_has_prefix_and_returns_only_the_remainder()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("Yukarı çık ve beni koru")));
        var r = await T(h).TranslateContinuationAsync("Move up and cover me", "Yukarı çık", Ctx(), default);
        Assert.Equal("ve beni koru", r);                            // echoed prefix stripped
        Assert.Contains("Yukarı çık", h.Body);
        var h2 = new Fake((_, _) => Task.FromResult(Ok("ve beni koru")));
        Assert.Equal("ve beni koru", await T(h2).TranslateContinuationAsync("Move up and cover me", "Yukarı çık", Ctx(), default));
    }

    [Fact] public void Cache_scope_names_provider_model_and_context_but_never_the_key()
    {
        var t = T(new Fake((_, _) => Task.FromResult(Ok("x"))));
        var a = t.CacheScope(Ctx("one")); var b = t.CacheScope(Ctx("two")); var c = t.CacheScope(Ctx("one"));
        Assert.Contains("gemini", a); Assert.Contains("gemini-3.8-flash", a); Assert.DoesNotContain(Key, a);
        Assert.NotEqual(a, b); Assert.Equal(a, c);
        Assert.NotEqual(a, T(new Fake((_, _) => Task.FromResult(Ok("x"))), model: "gemini-3.6-flash").CacheScope(Ctx("one")));
    }

    [Fact] public void Production_http_client_does_not_follow_redirects()
    {
        using var c = GeminiTranslator.CreateHttpClient();
        Assert.True(c.Timeout >= TimeSpan.FromSeconds(1));
        Assert.False(GeminiTranslator.CreateHandler().AllowAutoRedirect);
    }
}

public class DpapiKeyStoreTests
{
    private static string Dir() => Path.Combine(Path.GetTempPath(), "gk-" + Guid.NewGuid());

    [Fact] public void Round_trip_encrypted_file_has_no_plaintext_and_is_separate_from_settings()
    {
        var d = Dir();
        try
        {
            var s = new DpapiKeyStore(d);
            Assert.Null(s.Load()); Assert.False(s.Exists);
            s.Save("AIzaSy-SECRET-VALUE");
            Assert.True(s.Exists); Assert.Equal("AIzaSy-SECRET-VALUE", new DpapiKeyStore(d).Load());
            foreach (var f in Directory.GetFiles(d)) Assert.DoesNotContain("SECRET-VALUE", Encoding.Latin1.GetString(File.ReadAllBytes(f)));
            Assert.DoesNotContain("settings.json", Directory.GetFiles(d).Select(Path.GetFileName));
            s.Delete(); Assert.False(s.Exists); Assert.Null(s.Load());
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact] public void Corrupt_or_foreign_file_loads_as_null_not_an_exception()
    {
        var d = Dir(); Directory.CreateDirectory(d);
        try
        {
            File.WriteAllBytes(Path.Combine(d, DpapiKeyStore.FileName), [1, 2, 3, 4]);
            Assert.Null(new DpapiKeyStore(d).Load());
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact] public void Blank_keys_are_rejected_and_whitespace_trimmed()
    {
        var d = Dir();
        try
        {
            var s = new DpapiKeyStore(d);
            Assert.Throws<ArgumentException>(() => s.Save("  "));
            s.Save("  AIza-x \n"); Assert.Equal("AIza-x", s.Load());
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }
}
