using System.Text.Json;

namespace OyunAltyazi.Core;

public sealed record OverlayStyle(double FontSize = 30, double BackgroundOpacity = 0.65, double BottomMargin = 0.08,
                                  bool CoverOriginal = true, double CoverOpacity = 0.92);
public sealed record GameProfile(string ExeName, RegionF Region, OverlayStyle Overlay, bool AutoDetected = false);
public sealed record AppSettings(string Translator = "auto", Dictionary<string, GameProfile>? Profiles = null, string? LastExe = null);

public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly RegionF DefaultRegion = new(0.15, 0.72, 0.70, 0.22);
    private string FilePath => Path.Combine(directory, "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
            if (s is null) throw new JsonException("null settings");
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch (IOException) { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public bool HasProfile(string exeName) =>
        Load().Profiles?.Keys.Any(k => string.Equals(k, exeName, StringComparison.OrdinalIgnoreCase)) == true;

    public GameProfile ProfileFor(string exeName)
    {
        var key = exeName.ToLowerInvariant();
        var profiles = Load().Profiles;
        if (profiles is not null)
            foreach (var (k, v) in profiles)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
        return new GameProfile(key, DefaultRegion, new OverlayStyle());
    }
}
