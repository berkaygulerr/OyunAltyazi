// SPDX-License-Identifier: AGPL-3.0-only
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

/// <summary>Opt-in native in-process English→Turkish translator (OPUS-MT tc-big, ONNX Runtime CPU, int8). No Python, no Torch.</summary>
public sealed class OnnxTranslator : IContinuationTranslator, IDisposable
{
    private readonly Func<OnnxMarianEngine> _factory;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);   // one sentence at a time: parallel runs only fight over the cores
    private OnnxMarianEngine? _engine;
    private Exception? _failure;
    private bool _disposed;

    public OnnxTranslator(string modelDirectory, int beams = OnnxMarianEngine.DefaultBeams, int intraOpThreads = 2, TimeSpan? requestTimeout = null)
        : this(() => new OnnxMarianEngine(modelDirectory, 128, beams, intraOpThreads), requestTimeout) { }

    public OnnxTranslator(Func<OnnxMarianEngine> factory, TimeSpan? requestTimeout = null)
    {
        _factory = factory;
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    }

    public string Name => "onnx";

    public static bool IsInstalledIn(string modelDirectory) =>
        OnnxMarianEngine.RequiredFiles.All(n => File.Exists(Path.Combine(modelDirectory, n)));

    public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) => RunAsync(text, null, ct);

    /// <summary>Forces the shown Turkish prefix as the start of the output and returns only the continuation (may be empty).</summary>
    public Task<string> TranslateContinuationAsync(string fullEnglish, string shownPrefixTurkish, TranslationContext ctx, CancellationToken ct)
        => RunAsync(fullEnglish, Ct2Translator.TrimFinalPunctuation(shownPrefixTurkish), ct);

    private async Task<string> RunAsync(string text, string? prefix, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new TranslatorException("Yerel çevirmen kapatıldı.");
            var engine = GetEngine();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try
            {
                var result = await Task.Run(() => engine.Translate(text, prefix, cts.Token), cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result) && prefix is null) throw new TranslatorException("Yerel çeviri boş döndü.");
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TranslatorException("Yerel çeviri zaman aşımına uğradı.");
            }
            catch (Exception e) when (e is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or InvalidOperationException or ArgumentException)
            {
                throw new TranslatorException("Yerel çeviri hatası: " + e.Message);
            }
        }
        finally { _gate.Release(); }
    }

    private OnnxMarianEngine GetEngine()
    {
        if (_failure is not null) throw new TranslatorException("Yerel çevirmen kullanılamıyor: " + _failure.Message);
        try { return _engine ??= _factory(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _failure = e;   // a missing native DLL or broken model is not fixed by retrying
            throw new TranslatorException("Yerel çevirmen yüklenemedi: " + e.Message);
        }
    }

    /// <summary>Loads the model and runs one sentence in the background so the first subtitle does not wait.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try { await RunAsync("Hello.", null, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is TranslatorException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _engine?.Dispose(); _engine = null; }
        finally { _gate.Release(); }
    }
}
