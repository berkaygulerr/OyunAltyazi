using System.IO.Pipes;
using OyunAltyazi.Core;
using OyunAltyazi.Translation;

namespace OyunAltyazi.Translation.Tests;

public class Ct2TranslatorTests
{
    private static readonly TranslationContext Ctx = new("g", [], null);

    // Sahte yan süreç: arka plan görevi istekleri okuyup 'handler' ile yanıtlar (null = çökme, "" = sessiz).
    private static Sidecar Fake(Func<string, string?> handler)
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverIn = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle));
        var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        var serverOut = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(() =>
        {
            try
            {
                serverOut.WriteLine("{\"ready\":true,\"device\":\"cuda\"}");
                string? l;
                while ((l = serverIn.ReadLine()) is not null)
                {
                    var r = handler(l);
                    if (r is null) { serverOut.Dispose(); return; }
                    if (r.Length > 0) serverOut.WriteLine(r);
                }
            }
            catch { }
        });
        return new Sidecar(new StreamWriter(toServer) { NewLine = "\n" }, new StreamReader(fromServer), () => { try { toServer.Dispose(); } catch { } });
    }

    private static string Echo(string req)
    {
        using var d = System.Text.Json.JsonDocument.Parse(req);
        var id = d.RootElement.GetProperty("id").GetInt32();
        var t = d.RootElement.GetProperty("texts")[0].GetString();
        return "{\"id\":" + id + ",\"translations\":[\"TR:" + t + "\"],\"ms\":1}";
    }

    [Fact]
    public async Task Translates_and_reports_device()
    {
        using var t = new Ct2Translator(() => Fake(Echo));
        Assert.Equal("TR:Hello", await t.TranslateAsync("Hello", Ctx, default));
        Assert.Equal("TR:Again", await t.TranslateAsync("Again", Ctx, default));
        Assert.Equal("cuda", t.Device);
    }

    [Fact]
    public async Task Restarts_once_after_crash()
    {
        int launches = 0;
        using var t = new Ct2Translator(() => ++launches == 1 ? Fake(_ => null) : Fake(Echo));
        Assert.Equal("TR:Hi", await t.TranslateAsync("Hi", Ctx, default));
        Assert.Equal(2, launches);
    }

    [Fact]
    public async Task Throws_TranslatorException_when_always_crashing()
    {
        using var t = new Ct2Translator(() => Fake(_ => null));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi", Ctx, default));
    }

    [Fact]
    public async Task Times_out_on_silent_sidecar()
    {
        using var t = new Ct2Translator(() => Fake(_ => ""), TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi", Ctx, default));
    }

    [Fact]
    public async Task Cancellation_is_not_wrapped()
    {
        using var t = new Ct2Translator(() => Fake(_ => ""), TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.TranslateAsync("Hi", Ctx, cts.Token));
    }

    [Fact]
    public async Task Launcher_failure_becomes_TranslatorException()
    {
        using var t = new Ct2Translator(() => throw new TranslatorException("yok"));
        await Assert.ThrowsAsync<TranslatorException>(() => t.TranslateAsync("Hi", Ctx, default));
    }
}
