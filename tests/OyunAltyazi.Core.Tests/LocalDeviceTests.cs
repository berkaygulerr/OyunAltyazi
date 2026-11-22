using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class LocalDeviceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oa-dev-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] public void Default_is_gpu() => Assert.Equal("gpu", new AppSettings().LocalDevice);

    [Fact] public void Old_settings_file_without_field_loads_as_gpu()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Translator\":\"ct2\",\"LastExe\":\"a.exe\"}");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal("gpu", s.LocalDevice); Assert.Equal("ct2", s.Translator);
    }

    [Fact] public void Cpu_choice_roundtrips()
    {
        var st = new SettingsStore(_dir);
        st.Save(new AppSettings(LocalDevice: "cpu"));
        Assert.Equal("cpu", st.Load().LocalDevice);
    }

    [Theory]
    [InlineData("cpu", "cpu")] [InlineData("CPU", "cpu")] [InlineData("gpu", "gpu")]
    [InlineData(null, "gpu")] [InlineData("", "gpu")] [InlineData("tpu", "gpu")]
    public void Normalize(string? input, string expected) => Assert.Equal(expected, LocalDevice.Normalize(input));

    [Fact] public void Sidecar_args_cpu_is_explicit_cpu_gpu_is_auto_with_fallback()
    {
        Assert.Equal("cpu", LocalDevice.SidecarArg("cpu"));
        Assert.Equal("auto", LocalDevice.SidecarArg("gpu"));
        Assert.Equal("auto", LocalDevice.SidecarArg("garbage"));
    }

    [Theory]
    [InlineData("cpu", null, "CPU hazırlanıyor…")] [InlineData("gpu", null, "GPU hazırlanıyor…")]
    [InlineData("gpu", "cuda", "GPU (NVIDIA CUDA)")] [InlineData("gpu", "cpu", "CPU (GPU kullanılamadı)")]
    [InlineData("cpu", "cpu", "CPU")]
    public void Status_label_reports_actual_device(string pref, string? actual, string expected) =>
        Assert.Equal(expected, LocalDevice.Label(pref, actual));
}
