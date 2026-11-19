using System.Text;

namespace OyunAltyazi.Core;

/// <summary>Per-read TSV log (time, gate changed, raw OCR, cleaned text, stabilizer event) so real-world misses can be replayed/diagnosed.</summary>
public sealed class DiagnosticLog : IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();

    public string Path { get; }

    public DiagnosticLog(string directory, string? game = null, int keep = 20)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}.tsv");
        _w = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        _w.WriteLine($"# OyunAltyazi tanılama günlüğü {DateTime.Now:O} {game}");
        _w.WriteLine("timeMs\tchanged\traw\tcleaned\tevent");
        try
        {
            foreach (var old in new DirectoryInfo(directory).GetFiles("*.tsv").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
                old.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string Format(ReadLogEntry e) =>
        $"{e.TimeMs}\t{(e.Changed ? 1 : 0)}\t{Clean(e.Raw)}\t{Clean(e.Cleaned)}\t{e.Event}";

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Write(ReadLogEntry e)
    {
        lock (_lock)
        {
            try { _w.WriteLine(Format(e)); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose() { lock (_lock) _w.Dispose(); }
}
