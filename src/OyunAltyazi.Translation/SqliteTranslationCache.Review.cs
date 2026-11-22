using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

public sealed record ReviewRow(string Original, string Draft, string? Approved, string? CorrectedSource)
{
    public string State => Approved is null ? "Taslak" : "Onaylı";
    public string Display => Approved ?? Draft;
}

public sealed partial class SqliteTranslationCache
{
    private const string ReviewSchema = """
        CREATE TABLE IF NOT EXISTS reviewed(game TEXT, key TEXT, original TEXT NOT NULL, source TEXT NOT NULL,
            tr TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY(game,key));
        """;

    public IReadOnlyList<ReviewRow> ReviewRows(string game)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                WITH drafts AS (
                    SELECT key, source, tr, created,
                           row_number() OVER (PARTITION BY key ORDER BY created DESC, game, source) AS rn
                    FROM translations
                    WHERE (lower(game)=$game OR lower(substr(game,1,instr(game,char(1))-1))=$game)
                      AND instr(source,char(1))=0 AND length(source) BETWEEN 1 AND 2000
                      AND length(tr) BETWEEN 1 AND 2000 AND key<>''
                )
                , recent AS (
                    SELECT source,tr,created FROM drafts d WHERE rn=1 AND NOT EXISTS
                        (SELECT 1 FROM reviewed r WHERE r.game=$game AND r.key=d.key)
                    ORDER BY created DESC, source LIMIT 2000
                )
                SELECT original, coalesce(d.tr,''), r.tr, r.source, r.updated AS sort FROM reviewed r
                    LEFT JOIN drafts d ON d.key=r.key AND d.rn=1 WHERE r.game=$game
                UNION ALL
                SELECT source,tr,NULL,NULL,created*1000 FROM recent
                ORDER BY sort DESC, 1
                """;
            cmd.Parameters.AddWithValue("$game", NormalizeGame(game));
            using var reader = cmd.ExecuteReader();
            var rows = new List<ReviewRow>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            return rows;
        }
    }

    public void SaveReviewed(string game, string original, string turkish, string? correctedSource = null)
    {
        var source = (correctedSource ?? original).Trim();
        turkish = turkish.Trim();
        var key = TextKey.Canon(original);
        if (key.Length == 0 || TextKey.Canon(source).Length == 0 || source.Contains('\u0001') ||
            original.Contains('\u0001') || original.Length > GamePack.MaxTextLength || source.Length > GamePack.MaxTextLength ||
            turkish.Length is 0 or > GamePack.MaxTextLength)
            throw new ArgumentException("İngilizce ve Türkçe metin dolu olmalı ve 2.000 karakteri aşmamalı.");
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO reviewed(game,key,original,source,tr,updated) VALUES($game,$key,$original,$source,$tr,$updated)";
            cmd.Parameters.AddWithValue("$game", NormalizeGame(game));
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$original", original);
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$tr", turkish);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveReviewed(string game, string original)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM reviewed WHERE game=$game AND key=$key";
            cmd.Parameters.AddWithValue("$game", NormalizeGame(game));
            cmd.Parameters.AddWithValue("$key", TextKey.Canon(original));
            cmd.ExecuteNonQuery();
        }
    }

    public string? ExportReviewedPack(string game)
    {
        var entries = new List<PackEntry>();
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT source,tr FROM reviewed WHERE game=$game ORDER BY updated DESC,key";
            cmd.Parameters.AddWithValue("$game", NormalizeGame(game));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        if (entries.Count == 0) return null;
        var json = JsonSerializer.Serialize(new { version = 2, game = NormalizeGame(game), entries },
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) });
        if (Encoding.UTF8.GetByteCount(json) > GamePack.MaxCustomBytes) throw new InvalidOperationException("Oyun paketi dosya boyutu sınırını aşıyor.");
        var checkedPack = GamePack.Parse(json, game);
        if (checkedPack.Pack is null) throw new InvalidOperationException(checkedPack.Error ?? "Paket doğrulanamadı.");
        return json;
    }

    private static string NormalizeGame(string game) => game.Trim().ToLowerInvariant();
    private sealed record PackEntry(string en, string tr);
}
