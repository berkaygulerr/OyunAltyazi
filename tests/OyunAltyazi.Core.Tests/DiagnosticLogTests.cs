using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class DiagnosticLogTests
{
    [Fact] public void Writes_one_tsv_line_per_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oa-diag-" + Guid.NewGuid().ToString("N"));
        string path;
        using (var log = new DiagnosticLog(dir, "game"))
        {
            path = log.Path;
            log.Write(new ReadLogEntry(1234, true, "raw	line", "Alex: Hi there.", "SHOW"));
        }
        var lines = File.ReadAllLines(path);
        Assert.Equal("1234	1	raw line	Alex: Hi there.	SHOW", lines[^1]);
        Directory.Delete(dir, true);
    }
}
