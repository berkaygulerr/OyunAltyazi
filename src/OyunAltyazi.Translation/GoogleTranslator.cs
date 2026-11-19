using System.Text;
using System.Text.Json;
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

public sealed class GoogleTranslator(HttpClient http) : ITranslator
{
    public string Name => "google";

    public async Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=tr&dt=t&q=" + Uri.EscapeDataString(text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var resp = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new TranslatorException($"Google çeviri hatası: HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslatorException("Google çeviri zaman aşımına uğradı.");
        }
    }

    private static string Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            foreach (var seg in doc.RootElement[0].EnumerateArray())
                sb.Append(seg[0].GetString());
            if (sb.Length == 0) throw new TranslatorException("Google yanıtı boş.");
            return sb.ToString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new TranslatorException("Google yanıtı ayrıştırılamadı.");
        }
    }
}
