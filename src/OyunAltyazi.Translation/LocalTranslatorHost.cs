using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

/// <summary>Cihaz başına tek Ct2Translator tutar; cihaz değişince eskisini kapatır (eski yan süreç sonlanır, eski nesne artık "kapandı" hatası verir).</summary>
public sealed class LocalTranslatorHost(Func<string, Ct2Translator> factory) : IDisposable
{
    private readonly object _lock = new();
    private Ct2Translator? _current;
    private string? _device;

    public string? Device { get { lock (_lock) return _device; } }
    public Ct2Translator? Current { get { lock (_lock) return _current; } }

    public Ct2Translator Get(string? device)
    {
        var d = LocalDevice.Normalize(device);
        Ct2Translator? old = null;
        Ct2Translator result;
        lock (_lock)
        {
            if (_current is not null && _device == d) return _current;
            old = _current;
            result = _current = factory(d);
            _device = d;
        }
        old?.Dispose();
        return result;
    }

    public void Dispose() { lock (_lock) { _current?.Dispose(); _current = null; } }
}
