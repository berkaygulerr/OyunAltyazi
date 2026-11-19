using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oa-set-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact] public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.Equal("auto", s.Translator); Assert.Null(s.Profiles); Assert.Null(s.LastExe);
    }

    [Fact] public void Roundtrip()
    {
        var store = new SettingsStore(_dir);
        var gp = new GameProfile("mw4.exe", new RegionF(0.1, 0.2, 0.3, 0.4), new OverlayStyle(36, 0.5, 0.1));
        store.Save(new AppSettings("google", new() { ["mw4.exe"] = gp }, "mw4.exe"));
        var s = store.Load();
        Assert.Equal("mw4.exe", s.LastExe);
        Assert.Equal(gp, s.Profiles!["mw4.exe"]);
    }

    [Fact] public void Profile_lookup_case_insensitive()
    {
        var store = new SettingsStore(_dir);
        var gp = new GameProfile("mw4.exe", new RegionF(0.1, 0.2, 0.3, 0.4), new OverlayStyle());
        store.Save(new AppSettings(Profiles: new() { ["mw4.exe"] = gp }));
        Assert.Equal(gp, store.ProfileFor("MW4.EXE"));
    }

    [Fact] public void Unknown_exe_gets_default_region()
    {
        var p = new SettingsStore(_dir).ProfileFor("Foo.exe");
        Assert.Equal(new RegionF(0.15, 0.72, 0.70, 0.22), p.Region);
        Assert.Equal("foo.exe", p.ExeName);
    }

    [Fact] public void Corrupt_json_gives_defaults_and_keeps_backup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal("auto", s.Translator);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.bak")));
    }
}
