using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using OyunAltyazi.Core;

namespace OyunAltyazi.Translation;

public sealed class SqliteTranslationCache : ITranslationCache, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public SqliteTranslationCache(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("CREATE TABLE IF NOT EXISTS translations(game TEXT, key TEXT, source TEXT, tr TEXT, provider TEXT, created INTEGER, PRIMARY KEY(game,key));");
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public bool TryGet(string game, string sourceText, [NotNullWhen(true)] out string? translation)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT tr FROM translations WHERE game=$g AND key=$k";
            cmd.Parameters.AddWithValue("$g", game);
            cmd.Parameters.AddWithValue("$k", TextKey.Canon(sourceText));
            translation = cmd.ExecuteScalar() as string;
            return translation is not null;
        }
    }

    public void Put(string game, string sourceText, string translation, string provider)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO translations(game,key,source,tr,provider,created) VALUES($g,$k,$s,$t,$p,$c)";
            cmd.Parameters.AddWithValue("$g", game);
            cmd.Parameters.AddWithValue("$k", TextKey.Canon(sourceText));
            cmd.Parameters.AddWithValue("$s", sourceText);
            cmd.Parameters.AddWithValue("$t", translation);
            cmd.Parameters.AddWithValue("$p", provider);
            cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public long Count()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM translations";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void Clear() { lock (_lock) Exec("DELETE FROM translations;"); }

    public void Dispose() => _conn.Dispose();
}
