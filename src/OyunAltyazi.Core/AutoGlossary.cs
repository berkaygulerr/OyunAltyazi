namespace OyunAltyazi.Core;

/// <summary>One built-in preset: short exact English expressions → Turkish, written for this app (no game dialogue copied).</summary>
public sealed record AutoGlossaryPreset(string Id, string Title, IReadOnlyDictionary<string, string> Entries);

/// <summary>
/// Small built-in, fully offline EN→TR glossary presets. A game is recognised ONLY by an exact executable name listed in <see cref="Default"/>
/// (case-insensitive, trimmed; never titles, paths or fuzzy matches). A genre preset is shared between games only when each exe is mapped explicitly.
/// Unknown or ambiguous executables (e.g. cod.exe shared by several titles) get nothing.
/// </summary>
public sealed class AutoGlossaryCatalog
{
    private readonly Dictionary<string, AutoGlossaryPreset> _byExe = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<AutoGlossaryPreset> Presets { get; }

    public AutoGlossaryCatalog(IEnumerable<AutoGlossaryPreset> presets, IEnumerable<(string Exe, string PresetId)> mappings)
    {
        Presets = presets.ToList();
        var byId = Presets.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var (exe, id) in mappings)
        {
            if (!byId.TryGetValue(id, out var p)) throw new ArgumentException("Unknown preset: " + id);
            if (!_byExe.TryAdd(exe.Trim(), p)) throw new ArgumentException("Executable mapped twice: " + exe);
        }
    }

    public AutoGlossaryPreset? Find(string? exe) => string.IsNullOrWhiteSpace(exe) ? null : _byExe.GetValueOrDefault(exe.Trim());

    private static AutoGlossaryPreset Preset(string id, string title, params (string En, string Tr)[] e) =>
        new(id, title, e.ToDictionary(x => x.En, x => x.Tr, StringComparer.Ordinal));

    public static AutoGlossaryCatalog Default { get; } = new(
    [
        Preset("military-shooter", "Askeri nişancı oyunu",
            ("Cover me!", "Beni koruyun!"), ("Reloading!", "Şarjör değiştiriyorum!"), ("Enemy down.", "Düşman etkisiz."),
            ("Contact!", "Temas var!"), ("Hold your fire.", "Ateşi kesin."), ("Move out.", "Harekete geçin."),
            ("Stay low.", "Başınızı eğik tutun."), ("Fall back!", "Geri çekilin!"), ("Watch your six.", "Arkanı kolla."),
            ("Grenade!", "El bombası!"), ("Get down!", "Yere yatın!"), ("I'm hit.", "Vuruldum."),
            ("Objective complete.", "Görev tamamlandı."), ("Mission failed.", "Görev başarısız.")),
        Preset("open-world", "Açık dünya oyunu",
            ("Follow me.", "Beni takip et."), ("Wait here.", "Burada bekle."), ("Let's go.", "Hadi gidelim."),
            ("Watch out!", "Dikkat et!"), ("Over there!", "Şurada!"), ("Get in the car.", "Arabaya bin."),
            ("They're coming.", "Geliyorlar."), ("Keep moving.", "Yürümeye devam et."), ("Are you okay?", "İyi misin?"),
            ("Stay close.", "Yakın dur."), ("Not now.", "Şimdi değil."), ("Quest updated.", "Görev güncellendi.")),
        Preset("action-rpg", "Aksiyon rol yapma oyunu",
            ("Quest completed.", "Görev tamamlandı."), ("Level up.", "Seviye atladın."), ("Item acquired.", "Eşya alındı."),
            ("Save game.", "Oyunu kaydet."), ("Rest at the camp.", "Kampta dinlen."), ("Not enough gold.", "Yeterli altın yok."),
            ("Inventory is full.", "Envanter dolu."), ("Be careful.", "Dikkatli ol."), ("Well done.", "Aferin."),
            ("Let me help you.", "Sana yardım edeyim."), ("You are not ready.", "Hazır değilsin.")),
    ],
    [
        ("modernwarfare.exe", "military-shooter"),
        ("rdr2.exe", "open-world"), ("gta5.exe", "open-world"), ("watch_dogs.exe", "open-world"), ("acshadows.exe", "open-world"),
        ("eldenring.exe", "action-rpg"), ("ff7remake_.exe", "action-rpg"),
    ]);
}

public enum AutoGlossaryState { Off, NoMatch, Ready }

/// <summary>Keeps the built-in glossary of the CURRENTLY selected game in <see cref="LocalTranslations"/>. Synchronous, offline: no network, no files.
/// Manual glossary/pack files live in separate slots and are never touched, so removing them simply uncovers this layer again.</summary>
public sealed class AutoGlossaryCoordinator(LocalTranslations local, AutoGlossaryCatalog catalog, Func<bool> enabled)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GamePack> _installed = new(StringComparer.OrdinalIgnoreCase);

    public AutoGlossaryState State { get; private set; } = AutoGlossaryState.NoMatch;
    public string? Title { get; private set; }
    public int Count { get; private set; }
    public event Action? Changed;

    public void Select(string? exe)
    {
        exe = exe?.Trim();
        lock (_gate)
        {
            var preset = catalog.Find(exe);
            foreach (var (g, p) in _installed.ToList())
                if (preset is null || !string.Equals(g, exe, StringComparison.OrdinalIgnoreCase) || !enabled()) { local.ClearAutoGlossaryIf(g, p); _installed.Remove(g); }
            Title = preset?.Title; Count = preset?.Entries.Count ?? 0;
            if (preset is null) State = AutoGlossaryState.NoMatch;
            else if (!enabled()) State = AutoGlossaryState.Off;
            else
            {
                if (!_installed.ContainsKey(exe!))
                {
                    var pack = GamePack.FromEntries(exe!, preset.Title, preset.Entries, "auto:" + preset.Id);
                    if (local.SetAutoGlossary(exe!, pack)) _installed[exe!] = pack;
                }
                State = AutoGlossaryState.Ready;
            }
        }
        Changed?.Invoke();
    }
}
