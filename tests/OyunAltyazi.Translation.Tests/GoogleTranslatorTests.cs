using System.Net;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;
namespace OyunAltyazi.Translation.Tests;

public class GoogleTranslatorTests
{
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { LastUri = r.RequestUri; return f(r, ct); }
    }
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static readonly TranslationContext Ctx = new("g", [], null);

    [Fact] public async Task Builds_expected_url()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("[[[\"Selam\",\"Hi\",null]],null,\"en\"]")));
        await new GoogleTranslator(new HttpClient(h)).TranslateAsync("Let's go", Ctx, default);
        Assert.Equal("https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=tr&dt=t&q=Let%27s%20go", h.LastUri!.AbsoluteUri);
    }
    [Fact] public async Task Joins_segments()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("[[[\"Merhaba. \",\"Hello. \",null],[\"Nasılsın?\",\"How are you?\",null]],null,\"en\"]")));
        Assert.Equal("Merhaba. Nasılsın?", await new GoogleTranslator(new HttpClient(h)).TranslateAsync("x", Ctx, default));
    }
    [Fact] public async Task Throws_on_429_with_status()
    {
        var h = new Fake((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        var e = await Assert.ThrowsAsync<TranslatorException>(() => new GoogleTranslator(new HttpClient(h)).TranslateAsync("x", Ctx, default));
        Assert.Equal(429, e.StatusCode);
    }
    [Fact] public async Task Throws_on_garbage_json()
    {
        var h = new Fake((_, _) => Task.FromResult(Ok("<html>nope")));
        await Assert.ThrowsAsync<TranslatorException>(() => new GoogleTranslator(new HttpClient(h)).TranslateAsync("x", Ctx, default));
    }
    [Fact] public async Task Honors_cancellation()
    {
        var started = new TaskCompletionSource();
        var h = new Fake(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Ok(""); });
        using var cts = new CancellationTokenSource();
        var call = new GoogleTranslator(new HttpClient(h)).TranslateAsync("x", Ctx, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }
}
