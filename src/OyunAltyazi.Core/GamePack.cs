using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OyunAltyazi.Core;

public sealed record GamePackLoadResult(GamePack? Pack, string? Error);

/// <summary>Exact-match lookup of translations for one game, shared by user glossaries and opt-in game packs.</summary>
public interface ILocalTranslations
{
    bool TryGet(string game, string text, out string translation, out TranslationSource source);
}

/// <summary>
/// Local GamePack v2 (also used for the per-game phrase glossary): {"version":2,"game":"name.exe","title":"...","entries":[{"en":"..","tr":".."} | {"key":"PackKey","tr":".."}]}.
/// Matching is exact on <see cref="TextKey.PackKey"/> (canonical text), never fuzzy. A pack is bound to one exe name and is refused for any other.
/// Any structural problem rejects the whole file: a half-applied pack is worse than none. Reads a local file only.
/// </summary>
public sealed class GamePack
{
    // Custom schema stays small (hand-written notes). The real GameTranslator packs are 2-70 MB of JSON, plain or gzip (GTA VI ~66 MB, RDR2 ~17 MB).
    public const int MaxCustomBytes = 8 * 1024 * 1024, MaxEntries = 200_000, MaxTextLength = 2000;
    public const int MaxFileBytes = 64 * 1024 * 1024, MaxDecompressedBytes = 128 * 1024 * 1024, MaxSefflexEntries = 1_000_000, MaxSefflexTextLength = 32_000;
    private readonly Dictionary<string, string> _entries;
    private GamePack(string game, string title, Dictionary<string, string> entries, string id = "") { Game = game; Title = title; _entries = entries; Id = id; }

    /// <summary>Builds an in-memory pack from exact English→Turkish expressions (built-in presets). Throws on empty/conflicting entries.</summary>
    public static GamePack FromEntries(string game, string title, IEnumerable<KeyValuePair<string, string>> entries, string id = "")
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (en, tr) in entries)
        {
            if (TextKey.Canon(en).Length == 0 || string.IsNullOrWhiteSpace(tr)) throw new ArgumentException("Empty preset entry: " + en);
            var key = TextKey.PackKey(en);
            if (map.TryGetValue(key, out var old) && old != tr.Trim()) throw new ArgumentException("Conflicting preset entry: " + en);
            map[key] = tr.Trim();
        }
        return new GamePack(game.Trim(), title, map, id);
    }

    /// <summary>Pack id from the GameTranslator format (empty for the custom schema).</summary>
    public string Id { get; }

    public string Game { get; }
    public string Title { get; }
    public int Count => _entries.Count;

    public bool TryGet(string sourceText, out string translation)
    {
        translation = "";
        if (TextKey.Canon(sourceText).Length == 0) return false;
        return _entries.TryGetValue(TextKey.PackKey(sourceText), out translation!);
    }

    /// <summary>Loads a local file (plain JSON or gzip, detected by content). <paramref name="expectedGame"/> is the exe the user assigned it to.</summary>
    public static GamePackLoadResult LoadFile(string path, string expectedGame)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(null, "Dosya bulunamadı.");
            if (info.Length > MaxFileBytes) return new(null, "Dosya çok büyük.");
            return ParseBytes(File.ReadAllBytes(path), expectedGame);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return new(null, "Dosya okunamadı: " + e.Message);
        }
    }

    /// <summary>
    /// Accepts (1) the GameTranslator format {"format":2,"id","title","source","entries":{hex16:"tr"},"segments":..,"fuzzy":..}. It names no exe, so it applies
    /// to <paramref name="expectedGame"/>, the game the user explicitly assigned it to; "segments" and "fuzzy" are ignored (exact lines only). And
    /// (2) the custom schema (see <see cref="Parse"/>), which stays bound to its own "game". Gzip is unpacked with a hard output cap.
    /// </summary>
    public static GamePackLoadResult ParseBytes(byte[] data, string expectedGame)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedGame)) return new(null, "Oyun seçilmedi.");
            if (data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > MaxDecompressedBytes) return new(null, "Paket açıldığında çok büyük.");
                    ms.Write(buf, 0, n);
                }
                data = ms.ToArray();
            }
            if (data.Length > MaxDecompressedBytes) return new(null, "Dosya çok büyük.");
            int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;

            // one cheap pass over the top level; "entries" is skipped here and read afterwards
            int? format = null; bool hasVersion = false, hasGame = false, entriesDup = false; string title = "", id = ""; int eStart = -1, eEnd = -1;
            var reader = new Utf8JsonReader(data.AsSpan(start), new JsonReaderOptions { MaxDepth = 8 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return new(null, "Kök nesne bekleniyordu.");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                if (name == "entries")
                {
                    if (eStart >= 0) entriesDup = true;
                    eStart = (int)reader.TokenStartIndex;
                    reader.Skip();
                    eEnd = (int)reader.BytesConsumed;
                    continue;
                }
                switch (name)
                {
                    case "format": format = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var f) ? f : -1; break;
                    case "version": hasVersion = true; break;
                    case "game": hasGame = true; break;
                    case "id": if (reader.TokenType == JsonTokenType.String) id = reader.GetString() ?? ""; break;
                    case "title": if (reader.TokenType == JsonTokenType.String) title = reader.GetString() ?? ""; break;
                }
                reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndObject) return new(null, "Geçersiz JSON.");
            if (entriesDup) return new(null, "\"entries\" birden fazla.");

            if (format is null)
            {
                if (data.Length > MaxCustomBytes) return new(null, "Dosya çok büyük.");
                return Parse(Encoding.UTF8.GetString(data, start, data.Length - start), expectedGame);
            }
            if (format != 2) return new(null, "Yalnızca GameTranslator biçim 2 desteklenir.");
            if (hasVersion || hasGame) return new(null, "Karışık biçim: \"format\" ile \"version\"/\"game\" birlikte olamaz.");
            if (eStart < 0) return new(null, "\"entries\" eksik.");
            var map = ReadSefflexEntries(data.AsSpan(start + eStart, eEnd - eStart), out var err);
            return map is null ? new(null, err) : new(new GamePack(expectedGame.Trim(), title, map, id), null);
        }
        catch (JsonException e) { return new(null, "Geçersiz JSON: " + e.Message); }
        catch (InvalidDataException e) { return new(null, "Paket açılamadı: " + e.Message); }
    }

    private static Dictionary<string, string>? ReadSefflexEntries(ReadOnlySpan<byte> json, out string? error)
    {
        error = null;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 4 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) { error = "\"entries\" nesne olmalı."; return null; }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            i++;
            if (i > MaxSefflexEntries) { error = "Çok fazla kayıt."; return null; }
            var key = reader.GetString()!;
            if (key.Length != 16 || !key.All(Uri.IsHexDigit)) { error = $"Kayıt {i}: anahtar 16 haneli onaltılık olmalı."; return null; }
            key = key.ToLowerInvariant();
            reader.Read();
            if (reader.TokenType != JsonTokenType.String) { error = $"Kayıt {i}: metin bekleniyordu."; return null; }
            var text = reader.GetString()!.Trim();
            if (text.Length == 0 || text.Length > MaxSefflexTextLength) { error = $"Kayıt {i}: çeviri boş ya da çok uzun."; return null; }
            if (map.TryGetValue(key, out var old)) { if (old != text) { error = $"Kayıt {i}: aynı satır için çelişen çeviri."; return null; } }
            else map[key] = text;
        }
        return map;
    }

    public static GamePackLoadResult Parse(string json, string expectedGame)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedGame)) return new(null, "Oyun seçilmedi.");
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, "Kök nesne bekleniyordu.");
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 2)
                return new(null, "Yalnızca sürüm 2 desteklenir.");
            if (!root.TryGetProperty("game", out var g) || g.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(g.GetString()))
                return new(null, "\"game\" (exe adı) eksik.");
            var game = g.GetString()!.Trim();
            if (!string.Equals(game, expectedGame.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(null, $"Bu paket '{game}' oyunu içindir, '{expectedGame}' için kullanılamaz.");
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (!root.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array) return new(null, "\"entries\" listesi eksik.");
            if (arr.GetArrayLength() > MaxEntries) return new(null, "Çok fazla kayıt.");

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            foreach (var e in arr.EnumerateArray())
            {
                i++;
                if (e.ValueKind != JsonValueKind.Object) return new(null, $"Kayıt {i}: nesne bekleniyordu.");
                if (!e.TryGetProperty("tr", out var tr) || tr.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tr.GetString()) || tr.GetString()!.Length > MaxTextLength)
                    return new(null, $"Kayıt {i}: \"tr\" eksik ya da geçersiz.");
                bool hasEn = e.TryGetProperty("en", out var en), hasKey = e.TryGetProperty("key", out var k);
                if (hasEn == hasKey) return new(null, $"Kayıt {i}: \"en\" ya da \"key\" (yalnızca biri) gerekli.");
                string key;
                if (hasEn)
                {
                    if (en.ValueKind != JsonValueKind.String || en.GetString()!.Length > MaxTextLength || TextKey.Canon(en.GetString()!).Length == 0)
                        return new(null, $"Kayıt {i}: \"en\" geçersiz.");
                    key = TextKey.PackKey(en.GetString()!);
                }
                else
                {
                    key = k.ValueKind == JsonValueKind.String ? k.GetString()!.ToLowerInvariant() : "";
                    if (key.Length != 16 || !key.All(Uri.IsHexDigit)) return new(null, $"Kayıt {i}: \"key\" 16 haneli onaltılık olmalı.");
                }
                var text = tr.GetString()!.Trim();
                if (map.TryGetValue(key, out var old) && old != text) return new(null, $"Kayıt {i}: aynı satır için çelişen çeviri.");
                map[key] = text;
            }
            return new(new GamePack(game, title, map), null);
        }
        catch (JsonException e) { return new(null, "Geçersiz JSON: " + e.Message); }
    }
}

/// <summary>Per-game glossary (highest precedence) and game pack, keyed by exe name; a pack is only ever consulted for the game it was loaded for.</summary>
public sealed class LocalTranslations : ILocalTranslations
{
    private readonly ConcurrentDictionary<string, GamePack> _packs = new(StringComparer.OrdinalIgnoreCase), _glossaries = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, GamePack> _autoGlossaries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Installs the built-in glossary for a game. Lowest local layer: below manual glossary and any pack; independent of manual files.</summary>
    public bool SetAutoGlossary(string game, GamePack pack) => Set(_autoGlossaries, game, pack);
    public bool ClearAutoGlossaryIf(string game, GamePack pack) => _autoGlossaries.TryRemove(new KeyValuePair<string, GamePack>(game.Trim(), pack));
    public GamePack? AutoGlossaryFor(string game) => _autoGlossaries.GetValueOrDefault(game.Trim());

    public bool SetPack(string game, GamePack pack) => Set(_packs, game, pack);
    public bool SetGlossary(string game, GamePack pack) => Set(_glossaries, game, pack);

    private static bool Set(ConcurrentDictionary<string, GamePack> d, string game, GamePack pack)
    {
        game = game.Trim();
        if (game.Length == 0 || !string.Equals(pack.Game, game, StringComparison.OrdinalIgnoreCase)) return false;
        d[game] = pack;
        return true;
    }

    /// <summary>Installs an auto pack only when no pack (manual or otherwise) is present; atomic.</summary>
    public bool TrySetPackIfAbsent(string game, GamePack pack)
    {
        game = game.Trim();
        return game.Length > 0 && string.Equals(pack.Game, game, StringComparison.OrdinalIgnoreCase) && _packs.TryAdd(game, pack);
    }

    /// <summary>Removes the pack only if it is still exactly this instance (never a manual one that replaced it).</summary>
    public bool ClearPackIf(string game, GamePack pack) => _packs.TryRemove(new KeyValuePair<string, GamePack>(game.Trim(), pack));

    public void Clear(string game, bool glossary) => (glossary ? _glossaries : _packs).TryRemove(game.Trim(), out _);

    public GamePack? PackFor(string game) => _packs.GetValueOrDefault(game.Trim());
    public GamePack? GlossaryFor(string game) => _glossaries.GetValueOrDefault(game.Trim());

    public bool TryGet(string game, string text, out string translation, out TranslationSource source)
    {
        game = game.Trim();
        if (_glossaries.TryGetValue(game, out var g) && g.TryGet(text, out translation)) { source = TranslationSource.Glossary; return true; }
        if (_packs.TryGetValue(game, out var p) && p.TryGet(text, out translation)) { source = TranslationSource.Pack; return true; }
        if (_autoGlossaries.TryGetValue(game, out var a) && a.TryGet(text, out translation)) { source = TranslationSource.AutoGlossary; return true; }
        translation = ""; source = default;
        return false;
    }
}
