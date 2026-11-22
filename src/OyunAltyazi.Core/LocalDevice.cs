namespace OyunAltyazi.Core;

/// <summary>Yerel çeviri cihazı tercihi: "gpu" (varsayılan; NVIDIA CUDA, yoksa CPU'ya düşer) veya "cpu" (CUDA hiç yüklenmez).</summary>
public static class LocalDevice
{
    public const string Gpu = "gpu", Cpu = "cpu";

    public static string Normalize(string? v) => string.Equals(v, Cpu, StringComparison.OrdinalIgnoreCase) ? Cpu : Gpu;

    /// <summary>Yan sürecin --device değeri: CPU açıkça "cpu"; GPU "auto" (CUDA varsa GPU, değilse/başarısızsa CPU).</summary>
    public static string SidecarArg(string? v) => Normalize(v) == Cpu ? "cpu" : "auto";

    /// <summary>Durum metni; actual = yan sürecin bildirdiği gerçek cihaz ("cuda"/"cpu") ya da henüz hazır değilse null.</summary>
    public static string Label(string? preference, string? actual)
    {
        if (Normalize(preference) == Cpu) return actual is null ? "CPU hazırlanıyor…" : "CPU";
        return actual switch { null => "GPU hazırlanıyor…", "cuda" => "GPU (NVIDIA CUDA)", _ => "CPU (GPU kullanılamadı)" };
    }
}
