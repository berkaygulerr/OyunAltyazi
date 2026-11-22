using OyunAltyazi.Translation;
namespace OyunAltyazi.Translation.Tests;

public class Ct2DeviceTests
{
    [Fact] public void Process_args_pass_device_explicitly()
    {
        Assert.Equal(["-u", "s.py", "m", "--device", "cpu"], Ct2Paths.BuildArgs("s.py", "m", "cpu"));
        Assert.Equal(["-u", "s.py", "m", "--device", "auto"], Ct2Paths.BuildArgs("s.py", "m", "gpu"));
    }

    [Fact] public async Task Host_reuses_same_device_and_replaces_and_disposes_on_change()
    {
        var made = new List<(string dev, Ct2Translator t)>();
        using var host = new LocalTranslatorHost(dev => { var t = new Ct2Translator(() => throw new InvalidOperationException()); made.Add((dev, t)); return t; });
        var a = host.Get("gpu"); var a2 = host.Get("gpu");
        Assert.Same(a, a2); Assert.Single(made);
        var b = host.Get("cpu");
        Assert.NotSame(a, b); Assert.Equal(2, made.Count);
        // old translator is disposed: using it fails cleanly, never reaches a sidecar
        await Assert.ThrowsAsync<OyunAltyazi.Core.TranslatorException>(() => a.TranslateAsync("x", new("g", [], null), default));
        Assert.Same(b, host.Get("CPU"));
    }
}

/// <summary>Gerçek yan süreç, kurulu çalışma zamanına karşı (salt okunur). Kurulu değilse atlanır.</summary>
public class Ct2RealDeviceTests
{
    private static readonly OyunAltyazi.Core.TranslationContext Ctx = new("g", [], null);

    [Theory]
    [InlineData("cpu", "cpu")]
    [InlineData("gpu", null)]   // gpu: CUDA varsa "cuda", yoksa "cpu" — ikisi de geçerli; asıl gerçek cihaz bildirilir
    public async Task Real_sidecar_reports_actual_device(string pref, string? expected)
    {
        var script = Ct2Paths.FindServerScript();
        if (!Ct2Paths.IsInstalled(script)) return;
        using var t = new Ct2Translator(Ct2Paths.ProcessLauncher(Ct2Paths.FindPython()!, script!, Ct2Paths.ModelDir, pref), startTimeout: TimeSpan.FromSeconds(120));
        var tr = await t.TranslateAsync("Get to the helicopter, now!", Ctx, default);
        Assert.False(string.IsNullOrWhiteSpace(tr));
        if (expected is not null) Assert.Equal(expected, t.Device); else Assert.Contains(t.Device, new[] { "cuda", "cpu" });
        Console.WriteLine($"{pref} -> {t.Device}: {tr}");
    }
}
