using System.IO.Compression;
using System.Text;
using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

/// <summary>The real GameTranslator pack format: {"format":2,"id","title","source","entries":{hex16:turkish},"segments":{..},"fuzzy":{..}}. No exe inside: the user assigns it.</summary>
public class GamePackSefflexTests
{
    private const string Real = """
        {"format":2,"id":"call-of-duty-modern-warfare-2019","title":"Call of Duty: Modern Warfare (2019)","source":"x",
         "entries":{"5ad7d73f3757fa41":"Beni koru.","EDFA51E4714D70CB":"Onları parçalayın, hadi!"},
         "segments":{"ca44b1772261fb49":"Tango Mike."},"fuzzy":{"version":1,"count":5,"buckets":{"a":[1,2]}}}
        """;

    private static byte[] Gz(string s) { var ms = new MemoryStream(); using (var g = new GZipStream(ms, CompressionLevel.Fastest, true)) g.Write(Encoding.UTF8.GetBytes(s)); return ms.ToArray(); }

    [Fact] public void Accepts_sefflex_format_for_the_exe_the_user_assigned()
    {
        var r = GamePack.ParseBytes(Encoding.UTF8.GetBytes(Real), "ModernWarfare.exe");
        Assert.Null(r.Error); Assert.NotNull(r.Pack);
        Assert.Equal("ModernWarfare.exe", r.Pack!.Game); Assert.Equal(2, r.Pack.Count);   // segments/fuzzy are not entries
        Assert.Equal("Call of Duty: Modern Warfare (2019)", r.Pack.Title);
        Assert.True(r.Pack.TryGet("Cover me.", out var t)); Assert.Equal("Beni koru.", t);
        Assert.False(r.Pack.TryGet("Tango Mike.", out _));                                  // segments are never used as full lines
    }

    [Fact] public void Gzip_is_detected_by_content_not_extension()
    {
        var r = GamePack.ParseBytes(Gz(Real), "mw.exe");
        Assert.Null(r.Error); Assert.Equal(2, r.Pack!.Count);
    }

    [Fact] public void Sefflex_pack_is_not_used_for_a_game_it_was_not_assigned_to()
    {
        var l = new LocalTranslations();
        var p = GamePack.ParseBytes(Encoding.UTF8.GetBytes(Real), "mw.exe").Pack!;
        Assert.True(l.SetPack("mw.exe", p));
        Assert.False(l.SetPack("gta5.exe", p));
        Assert.False(l.TryGet("gta5.exe", "Cover me.", out _, out _));
        Assert.True(l.TryGet("mw.exe", "Cover me.", out _, out _));
    }

    [Fact] public void Custom_schema_keeps_its_own_game_binding_through_the_byte_path()
    {
        const string c = """{"version":2,"game":"mw4.exe","entries":[{"en":"Move up, now.","tr":"Yukarı."}]}""";
        Assert.NotNull(GamePack.ParseBytes(Encoding.UTF8.GetBytes(c), "mw4.exe").Pack);
        Assert.Null(GamePack.ParseBytes(Encoding.UTF8.GetBytes(c), "other.exe").Pack);
        Assert.NotNull(GamePack.ParseBytes(Gz(c), "MW4.EXE").Pack);
    }

    [Theory]
    [InlineData("""{"format":3,"entries":{"5ad7d73f3757fa41":"x"}}""")]
    [InlineData("""{"format":2}""")]
    [InlineData("""{"format":2,"entries":[]}""")]
    [InlineData("""{"format":2,"entries":{"zz":"x"}}""")]
    [InlineData("""{"format":2,"entries":{"5ad7d73f3757fa41":""}}""")]
    [InlineData("""{"format":2,"entries":{"5ad7d73f3757fa41":5}}""")]
    [InlineData("""{"format":2,"entries":{"5ad7d73f3757fa41":"a","5AD7D73F3757FA41":"b"}}""")]
    [InlineData("""{"format":2,"entries":{"5ad7d73f3757fa41":"a" """)]
    [InlineData("""{"format":2,"version":2,"game":"a.exe","entries":{}}""")]
    public void Malformed_sefflex_is_rejected_whole(string json)
    {
        var r = GamePack.ParseBytes(Encoding.UTF8.GetBytes(json), "mw.exe");
        Assert.Null(r.Pack); Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact] public void Gzip_bomb_is_bounded()
    {
        var ms = new MemoryStream();
        using (var g = new GZipStream(ms, CompressionLevel.SmallestSize, true)) { var z = new byte[1 << 20]; for (int i = 0; i < GamePack.MaxDecompressedBytes / (1 << 20) + 2; i++) g.Write(z); }
        Assert.True(ms.Length < GamePack.MaxFileBytes * 8);
        var r = GamePack.ParseBytes(ms.ToArray(), "mw.exe");
        Assert.Null(r.Pack); Assert.NotNull(r.Error);
    }

    [Fact] public void Loads_big_plain_and_gz_files_from_disk()
    {
        // ~20 MB of plain JSON (like the real RDR2/GTA packs), 120k distinct entries
        var sb = new StringBuilder("{\"format\":2,\"id\":\"t\",\"title\":\"t\",\"source\":\"s\",\"entries\":{");
        for (int i = 0; i < 120_000; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(i.ToString("x16")).Append("\":\"Çeviri satırı numara ").Append(i).Append(" ").Append('x', 100).Append('"'); }
        sb.Append("},\"segments\":{}}");
        var json = sb.ToString(); Assert.True(json.Length > 17_000_000);
        var dir = Path.Combine(Path.GetTempPath(), "gp-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Combine(dir, "p.json"); File.WriteAllText(plain, json);
            var gz = Path.Combine(dir, "p.json.gz"); File.WriteAllBytes(gz, Gz(json));
            foreach (var f in new[] { plain, gz }) { var r = GamePack.LoadFile(f, "g.exe"); Assert.Null(r.Error); Assert.Equal(120_000, r.Pack!.Count); }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Optional: set OYUNALTYAZI_REFERENCE_PACKS to a folder with genuine GameTranslator packs (*.json[.gz]). Nothing from them is bundled.</summary>
    [Fact] public void Genuine_reference_packs_load_if_available()
    {
        var dir = Environment.GetEnvironmentVariable("OYUNALTYAZI_REFERENCE_PACKS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "*.json*");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var r = GamePack.LoadFile(f, "game.exe");
            Assert.True(r.Pack is not null, f + ": " + r.Error);
            Assert.True(r.Pack!.Count > 100, f);
        }
        var mw = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("call-of-duty-modern-warfare-2019"));
        if (mw is not null) Assert.True(GamePack.LoadFile(mw, "mw.exe").Pack!.TryGet("Cover me.", out var t) && t == "Beni koru.");
    }
}
