# OyunAltyazi MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bir PC oyun penceresindeki İngilizce altyazıyı okuyup Türkçesini titremeden, tıklamayı geçiren bir overlay'de gösteren Windows uygulamasının ilk sürümü.

**Architecture:** Saf mantık (`OyunAltyazi.Core`: normalleştirme, temizlik, sabitleyici, değişim kapısı, çeviri zinciri, boru hattı) Windows'tan bağımsız ve birim testli. Çeviri/önbellek `OyunAltyazi.Translation`'da. Windows'a özgü parçalar (WGC yakalama, Windows.Media.Ocr, overlay, ana pencere) `OyunAltyazi.App` (WPF) içinde, Core arayüzlerini uygular.

**Tech Stack:** .NET 10, C# 14, WPF, CsWinRT projeksiyonları (`net10.0-windows10.0.22621.0`), Vortice.Direct3D11, Microsoft.Data.Sqlite, xUnit.

**Spec:** `docs/specs/2026-11-19-oyunaltyazi-design.md`

## Global Constraints
- Windows 10 1903+ / 11, x64. Hedef çerçeve: Core/Translation `net10.0`, App `net10.0-windows10.0.22621.0`.
- Lisans MIT. sefflex/GameTranslator (AGPL-3.0) kodu kopyalanmaz, satır satır çevrilmez; yalnız veri biçimi (yama anahtarı) kullanılır.
- Telemetri yok; ağ çağrısı yalnız seçili çevirmene.
- Ayarlar `%APPDATA%\OyunAltyazi\` altında; API anahtarı (MVP'de yok) gelecekte DPAPI ile.
- Gecikme hedefleri: önbellek isabetinde sabitlenmeden overlay'e < 150 ms; çevrimiçi çeviride < 1 sn.
- Oyun sırasında FPS kaybı < %3; boşta CPU ~%0.
- Kullanıcıya görünen tüm metinler Türkçe.
- Kuyruk derinliği 1: OCR meşgulken gelen kareler atılır, biriktirilmez.

## Review Focus
1. **Oyun penceresi kapanır/küçültülür/yeniden boyutlanır** → yakalama hata fırlatmadan durur veya kendini yeniden kurar; overlay gizlenir. (Task 11 testi)
2. **Altyazı daktilo gibi harf harf belirir** → yarım satır çevrilmez, yalnız tamamlanmış satır gösterilir. (Task 3 testi)
3. **Çeviri isteği yavaşken yeni satır gelir** → eski istek iptal edilir, eski çeviri yeni satırın üstüne yazılmaz. (Task 8 testi)
4. **Google 429/ağ hatası** → overlay'de İngilizce kaynak satır gösterilir, program çökmez, hata tanılamada görünür. (Task 7 testi)
5. **Altyazı bölgesinde HUD/buton ipucu ("[F] Interact", "LB", tek harfler)** → çeviriye gitmez. (Task 2 testi)

---

## Dosya yapısı
```
OyunAltyazi/
  OyunAltyazi.slnx
  Directory.Build.props            (Nullable, LangVersion, TreatWarningsAsErrors)
  LICENSE (MIT), .gitignore
  src/OyunAltyazi.Core/
    Model.cs            Frame, RegionF, OcrLine, OcrResult, SubtitleText, LineTiming
    TextKey.cs          normalleştirme + yama uyumlu anahtar
    TextCleaner.cs      gürültü filtresi, OCR karışıklık düzeltmesi, konuşmacı ayırma
    Stabilizer.cs       titreme önleme / satır tamamlanma kararı
    ChangeGate.cs       bölge imzası
    Abstractions.cs     IFrameSource, IOcrEngine, ITranslator, ITranslationCache, ISubtitleSink
    TranslationChain.cs önbellek → çevirmen, yedek davranış
    LatencyStats.cs     son 50 satır ort/p95
    SubtitlePipeline.cs orkestrasyon
    Settings.cs         AppSettings, GameProfile, SettingsStore
  src/OyunAltyazi.Translation/
    GoogleTranslator.cs
    SqliteTranslationCache.cs
  src/OyunAltyazi.App/
    App.xaml(.cs), MainWindow.xaml(.cs)
    Capture/WgcFrameSource.cs, Capture/WindowEnumerator.cs
    Ocr/WindowsOcrEngine.cs
    Overlay/OverlayWindow.xaml(.cs)
    UI/RegionSelector.xaml(.cs)
    Hotkeys.cs
  tests/OyunAltyazi.Core.Tests/
  tests/OyunAltyazi.Translation.Tests/
  tests/OyunAltyazi.App.Tests/      (Windows'a özgü entegrasyon testleri)
```

---

### Task 0: Araç zinciri ve iskelet

**Files:** yukarıdaki kök dosyalar, 3 src + 3 test projesi (boş).

- [ ] **Step 1:** .NET 10 SDK kur: `winget install --id Microsoft.DotNet.SDK.10 -e --accept-source-agreements --accept-package-agreements`; yeni kabukta `dotnet --list-sdks` → `10.x` görünmeli.
- [ ] **Step 2:** `git init`; `dotnet new sln -n OyunAltyazi --format slnx`; `classlib` (Core, Translation), `wpf` (App), `xunit` (3 test) projeleri; referanslar: Translation→Core, App→Core+Translation, testler→ilgili proje.
- [ ] **Step 3:** App csproj: `<TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>`, `<UseWPF>true</UseWPF>`, `<SupportedOSPlatformVersion>10.0.18362.0</SupportedOSPlatformVersion>`, paket `Vortice.Direct3D11`. Translation: `Microsoft.Data.Sqlite`.
- [ ] **Step 4:** `.gitignore` (bin/ obj/ *.user .vs/), MIT `LICENSE` (Copyright Berkay Güler).
- [ ] **Step 5:** `dotnet build` ve `dotnet test` → derleme hatasız, şablon testleri PASS.
- [ ] **Step 6:** Commit `chore: iskelet`.

### Task 1: TextKey (yama uyumlu normalleştirme)

**Files:** Create `src/OyunAltyazi.Core/TextKey.cs`; Test `tests/OyunAltyazi.Core.Tests/TextKeyTests.cs`

**Interfaces — Produces:**
- `static string TextKey.Canon(string text)`
- `static string TextKey.PackKey(string text)` → `Canon`'un UTF-8 SHA-256'sının ilk 8 baytı, küçük harf hex (16 karakter).

Canon kuralları (tasarımdan): `<...>` en çok 40 karakterlik etiketler boşluk olur → NFKC → "İ" (U+0130) önce "i"+U+0307'ye çevrilir, sonra `ToLowerInvariant` → NFD, tüm birleşen işaretler (Mn/Mc/Me) atılır → harf/rakam dışı her karakter boşluk → ardışık boşluklar teklenir, baş/son kırpılır.

- [ ] **Step 1: Failing test**
```csharp
[Theory]
[InlineData("Frag out!", "frag out")]
[InlineData("  RELOADING...  ", "reloading")]
[InlineData("Let's go", "let s go")]
[InlineData("<i>Cover</i> me", "cover me")]
[InlineData("İstanbul café", "istanbul cafe")]
public void Canon_matches_rules(string input, string expected) => Assert.Equal(expected, TextKey.Canon(input));

[Theory]
[InlineData("Frag out!", "3b96e8a2a8dac727")]
[InlineData("Reloading", "11747904a6b688d5")]
[InlineData("Cover me.", "5ad7d73f3757fa41")]
[InlineData("Let's go", "27b5ebc897524c85")]
[InlineData("Tear gas out!", "03e5024798f115b1")]   // CoD MW2019 paketinde mevcut anahtar
public void PackKey_is_compatible(string input, string key) => Assert.Equal(key, TextKey.PackKey(input));
```
- [ ] **Step 2:** `dotnet test --filter TextKeyTests` → FAIL (TextKey yok).
- [ ] **Step 3:** `TextKey`'i uygula.
- [ ] **Step 4:** Aynı komut → PASS.
- [ ] **Step 5:** Commit `feat(core): yama uyumlu TextKey`.

### Task 2: Model + TextCleaner

**Files:** Create `src/OyunAltyazi.Core/Model.cs`, `TextCleaner.cs`; Test `TextCleanerTests.cs`

**Interfaces — Produces:**
- `record RegionF(double X, double Y, double W, double H)` (0..1, pencereye oranlı)
- `record OcrLine(string Text, double Top, double Height)` (Top/Height bölgeye oranlı 0..1)
- `record OcrResult(IReadOnlyList<OcrLine> Lines)`
- `record SubtitleText(string? Speaker, string Text)`
- `static SubtitleText? TextCleaner.Clean(OcrResult ocr)`

Kurallar: satırları yukarıdan aşağı birleştir (boşlukla); 3 harften az harf içeren satırlar, yalnız `[X]`/`(LB)`/`[F] Interact` tarzı buton ipuçları ve harf oranı < %50 olan satırlar atılır; sonuç < 2 kelime ve < 4 harfse `null`. Konuşmacı: `^([A-Z][A-Za-z0-9 .'\-]{0,24}):\s+(.+)$` → Speaker + Text. Karışıklık düzeltmesi: kelime içinde harflerin arasındaki `0`→`o`, `1`/`|`→`l` (yalnız kelimenin geri kalanı harfse).

- [ ] **Step 1: Failing tests**
```csharp
[Fact] public void Splits_speaker() => Assert.Equal(new SubtitleText("Price", "Move up, now."), Clean("Price: Move up, now."));
[Fact] public void Joins_two_lines() => Assert.Equal(new SubtitleText(null, "We need to go now before they come back."), Clean("We need to go now", "before they come back."));
[Theory][InlineData("[F] Interact")][InlineData("LB")][InlineData("x")][InlineData("|| -- ..")]
public void Drops_hud_noise(string line) => Assert.Null(Clean(line));
[Fact] public void Fixes_confusions() => Assert.Equal("Hold the line", Clean("Ho1d the 1ine")!.Text);
[Fact] public void Drops_noise_line_but_keeps_dialogue() => Assert.Equal("Get to the chopper.", Clean("[E] Interact", "Get to the chopper.")!.Text);
// Clean(params string[] lines) test yardımcı metodu, eşit aralıklı OcrLine'lar üretir.
```
- [ ] **Step 2:** FAIL doğrula. **Step 3:** Uygula. **Step 4:** PASS. **Step 5:** Commit `feat(core): TextCleaner`.

### Task 3: Stabilizer

**Files:** Create `src/OyunAltyazi.Core/Stabilizer.cs`; Test `StabilizerTests.cs`

**Interfaces — Produces:**
- `abstract record StabilizerEvent; sealed record ShowLine(SubtitleText Line) : StabilizerEvent; sealed record ClearLine : StabilizerEvent;`
- `sealed class Stabilizer(int confirmReads = 2, int clearAfterEmptyReads = 4)` → `StabilizerEvent? Feed(SubtitleText? read)`

Kurallar: Karşılaştırma `TextKey.Canon(Text)` üstünden. Aynı canon art arda `confirmReads` kez okununca `ShowLine` (bir kez). Yeni okuma öncekinin canon önekiyle başlıyor ve daha uzunsa (daktilo) sayaç sıfırlanır, gösterilmez. Gösterilen satırla Levenshtein benzerliği ≥ 0.85 olan okumalar aynı satır sayılır (OCR titremesi; yeniden gösterim yok). Art arda `clearAfterEmptyReads` boş (null) okumada `ClearLine` (yalnız bir şey gösteriliyorsa).

- [ ] **Step 1: Failing tests**
```csharp
[Fact] public void Shows_after_two_equal_reads()       // Feed(a)→null, Feed(a)→ShowLine(a)
[Fact] public void Typewriter_waits_for_full_line()    // "We nee","We need to","We need to go now","We need to go now" → yalnız sonuncuda ShowLine("We need to go now")
[Fact] public void Ocr_jitter_does_not_reshow()        // a gösterildi; "Hold the 1ine" benzeri ~%90 benzer okuma → null
[Fact] public void Clears_after_four_empty_reads()     // a gösterildi; null x3 → null, 4. null → ClearLine
[Fact] public void Single_empty_read_does_not_clear()  // a gösterildi; null, a → null (ClearLine yok, yeniden ShowLine yok)
[Fact] public void New_line_replaces_old()             // a gösterildi; b, b → ShowLine(b)
```
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(core): Stabilizer`.

### Task 4: ChangeGate

**Files:** Create `src/OyunAltyazi.Core/ChangeGate.cs` (+ `Frame` record Model.cs'e); Test `ChangeGateTests.cs`

**Interfaces — Produces:**
- `sealed record Frame(int Width, int Height, int Stride, byte[] Bgra, long TimestampMs)`
- `sealed class ChangeGate(double threshold = 3.0)` → `bool HasChanged(Frame f)`; ilk kare her zaman `true`.

Yöntem: kareyi 64×16 gri ızgaraya ortalamayla küçült; önceki imzayla ortalama mutlak fark > `threshold` (0–255 ölçeği) ise değişti, imzayı güncelle.

- [ ] **Step 1: Failing tests:** `First_frame_changed`, `Identical_frame_not_changed`, `Small_noise_not_changed` (her piksele ±1), `Text_appearing_changed` (siyah kareye 200×20 beyaz dikdörtgen), `Different_size_frame_changed`.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(core): ChangeGate`.

### Task 5: SqliteTranslationCache

**Files:** Create `src/OyunAltyazi.Core/Abstractions.cs` (yalnız `ITranslationCache` şimdilik), `src/OyunAltyazi.Translation/SqliteTranslationCache.cs`; Test `tests/OyunAltyazi.Translation.Tests/SqliteTranslationCacheTests.cs`

**Interfaces — Produces:**
```csharp
public interface ITranslationCache {
    bool TryGet(string game, string sourceText, [NotNullWhen(true)] out string? translation);
    void Put(string game, string sourceText, string translation, string provider);
}
public sealed class SqliteTranslationCache(string dbPath) : ITranslationCache, IDisposable
```
Tablo `translations(game TEXT, key TEXT, source TEXT, tr TEXT, provider TEXT, created INTEGER, PRIMARY KEY(game,key))`; `key = TextKey.Canon(sourceText)`. WAL modu. MVP'de bulanık eşleşme yok (canon zaten noktalama/büyük-küçük farkını yutar).

- [ ] **Step 1: Failing tests:** `Put_then_get_same_game`, `Canon_equivalent_text_hits` ("Frag out!" put, "FRAG OUT" get), `Other_game_misses`, `Persists_across_instances` (dispose → yeniden aç → hit), `Put_overwrites`.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(translation): sqlite önbellek`.

### Task 6: GoogleTranslator

**Files:** Abstractions.cs'e ekle; Create `src/OyunAltyazi.Translation/GoogleTranslator.cs`; Test `GoogleTranslatorTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record TranslationContext(string Game, IReadOnlyList<string> PreviousLines, string? Speaker);
public interface ITranslator { string Name { get; } Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct); }
public sealed class GoogleTranslator(HttpClient http) : ITranslator   // Name = "google"
public sealed class TranslatorException(string message, int? statusCode = null) : Exception(message)
```
İstek: `GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=tr&dt=t&q={escaped}`; yanıtın `[0]` dizisindeki her öğenin `[0]` dizesi birleştirilir. HTTP 2xx dışı veya ayrıştırma hatası → `TranslatorException` (status ile). Zaman aşımı 3 sn (ct ile birleşik).

- [ ] **Step 1: Failing tests** (sahte `HttpMessageHandler`): `Builds_expected_url`, `Joins_segments` (`[[["Merhaba. ","Hello. ",null],["Nasılsın?","How are you?",null]],null,"en"]` → `"Merhaba. Nasılsın?"`), `Throws_on_429_with_status`, `Throws_on_garbage_json`, `Honors_cancellation`.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(translation): Google çevirmen`.

### Task 7: TranslationChain + LatencyStats

**Files:** Create `src/OyunAltyazi.Core/TranslationChain.cs`, `LatencyStats.cs`; Test `TranslationChainTests.cs`, `LatencyStatsTests.cs`

**Interfaces — Produces:**
```csharp
public enum TranslationSource { Cache, Translator, Fallback }
public sealed record TranslationResult(string Text, TranslationSource Source, TimeSpan Elapsed, string? Error = null);
public sealed class TranslationChain(ITranslationCache cache, ITranslator translator)
    { Task<TranslationResult> TranslateAsync(SubtitleText line, TranslationContext ctx, CancellationToken ct); }
public sealed record LineTiming(long CapturedMs, long OcrDoneMs, long StableMs, long ShownMs, TranslationSource Source);
public sealed class LatencyStats(int capacity = 50)
    { void Add(LineTiming t); (double AvgMs, double P95Ms, int Count) StableToShown(); (double AvgMs, double P95Ms, int Count) CaptureToShown(); }
```
Davranış: önbellek isabeti → `Cache`; değilse çevirmen → başarılıysa önbelleğe `Put` ve `Translator`; `TranslatorException`/`HttpRequestException`/zaman aşımı → kaynak İngilizce metin, `Fallback`, `Error` dolu, önbelleğe yazılmaz. `OperationCanceledException` (dış ct) yukarı fırlatılır. Konuşmacı çeviriye gönderilmez, sonuç `"{Speaker}: {tr}"` olarak birleştirilmez — sink ayrı alır (Text yalnız çeviri).

- [ ] **Step 1: Failing tests:** `Cache_hit_skips_translator`, `Miss_calls_translator_and_stores`, `Translator_error_returns_source_as_fallback_without_caching` (Review Focus 4), `Outer_cancellation_propagates`; LatencyStats: `P95_of_known_values` (1..100 ms → avg 50.5, p95 95), `Keeps_last_50`.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(core): çeviri zinciri ve gecikme istatistiği`.

### Task 8: SubtitlePipeline

**Files:** Abstractions.cs'e ekle; Create `src/OyunAltyazi.Core/SubtitlePipeline.cs`; Test `SubtitlePipelineTests.cs`

**Interfaces:**
- Consumes: Task 2–7.
- Produces:
```csharp
public interface IFrameSource : IDisposable { event Action<Frame>? FrameArrived; event Action<string>? Stopped; void Start(); void Stop(); }
public interface IOcrEngine { Task<OcrResult> ReadAsync(Frame frame, CancellationToken ct); }
public interface ISubtitleSink { void Show(string? speaker, string text, TranslationSource source); void Clear(); }
public sealed class SubtitlePipeline(IFrameSource source, IOcrEngine ocr, TranslationChain chain, ISubtitleSink sink,
                                     string game, LatencyStats stats, Func<long> clockMs, int maxReadsPerSecond = 10) : IDisposable
    { void Start(); void Stop(); }
```
Davranış: kare gelince — OCR meşgulse veya son okumadan beri `1000/maxReadsPerSecond` ms geçmediyse kare atılır (Kuyruk derinliği 1). `ChangeGate` değişmedi derse OCR yapılmaz ama Stabilizer'a son okuma tekrar beslenir (sabitleme ilerlesin). `ShowLine` → çalışan çeviri iptal edilir (CancellationTokenSource), yeni çeviri başlar; sonuç gelince hâlâ en güncel satırsa `sink.Show`, değilse atılır. `ClearLine` → çeviri iptal, `sink.Clear()`. Bağlam: son 6 gösterilen kaynak satır. Her gösterimde `stats.Add`. `source.Stopped` → pipeline durur, `sink.Clear()`.

- [ ] **Step 1: Failing tests** (sahte kaynak/OCR/çevirmen/sink, el ile saat):
  - `Shows_translation_after_stable_read`
  - `Stale_translation_is_discarded_when_new_line_arrives` (yavaş çevirmen a, sonra b sabitlenir → sink yalnız b'yi görür) — Review Focus 3
  - `Frames_dropped_while_ocr_busy` (OCR bekletilirken 5 kare → OCR 1 kez çağrıldı)
  - `Unchanged_frames_skip_ocr`
  - `Clear_after_empty_reads_calls_sink_clear`
  - `Source_stopped_clears_sink` — Review Focus 1
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(core): altyazı boru hattı`.

### Task 9: Ayarlar ve oyun profili

**Files:** Create `src/OyunAltyazi.Core/Settings.cs`; Test `SettingsTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record OverlayStyle(double FontSize = 30, double BackgroundOpacity = 0.65, double BottomMargin = 0.08);
public sealed record GameProfile(string ExeName, RegionF Region, OverlayStyle Overlay);
public sealed record AppSettings(string Translator = "google", Dictionary<string, GameProfile>? Profiles = null, string? LastExe = null);
public sealed class SettingsStore(string directory)   // gerçek: %APPDATA%\OyunAltyazi
    { AppSettings Load(); void Save(AppSettings s); GameProfile ProfileFor(string exeName); }
```
`ProfileFor` exe adını küçük harfe çevirip arar; yoksa varsayılan bölge `RegionF(0.15, 0.72, 0.70, 0.22)`. Bozuk/eksik JSON → varsayılan ayarlar (dosya `.bak` olarak saklanır). Kayıt atomik (geçici dosya + `File.Move(overwrite)`).

- [ ] **Step 1: Failing tests:** `Missing_file_gives_defaults`, `Roundtrip`, `Profile_lookup_case_insensitive`, `Unknown_exe_gets_default_region`, `Corrupt_json_gives_defaults_and_keeps_backup`.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(core): ayarlar ve profiller`.

### Task 10: WindowsOcrEngine

**Files:** Create `src/OyunAltyazi.App/Ocr/WindowsOcrEngine.cs`; Test `tests/OyunAltyazi.App.Tests/WindowsOcrEngineTests.cs` (test projesi `net10.0-windows10.0.22621.0`)

**Interfaces — Produces:** `public sealed class WindowsOcrEngine : IOcrEngine` — `static WindowsOcrEngine? TryCreate()` (`OcrEngine.TryCreateFromLanguage(new Language("en-US"))`, İngilizce dil paketi yoksa `null`).

Yöntem: `Frame` → `SoftwareBitmap(Bgra8, Premultiplied)` (`CopyFromBuffer`); bölge yüksekliği < 40 px ise 2× ölçekle (OCR küçük yazıda zayıf). Satır kutularını bölgeye oranla `OcrLine`'a çevir.

- [ ] **Step 1: Failing test:** `Reads_rendered_text` — `System.Drawing` ile 800×80 siyah zemine beyaz Segoe UI 32px "Get to the chopper now" yaz, `Frame`'e çevir → `ReadAsync` sonucunun birleşik metni `TextKey.Canon` ile `"get to the chopper now"`. İngilizce OCR yoksa test `Skip` mesajıyla atlanır.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(app): Windows OCR`.

### Task 11: WGC yakalama ve pencere listesi

**Files:** Create `src/OyunAltyazi.App/Capture/WindowEnumerator.cs`, `Capture/WgcFrameSource.cs`; Test `tests/OyunAltyazi.App.Tests/WgcFrameSourceTests.cs`

**Interfaces — Produces:**
```csharp
public sealed record WindowInfo(IntPtr Hwnd, string Title, string ExeName, int ProcessId);
public static class WindowEnumerator { IReadOnlyList<WindowInfo> List(); }   // görünür, başlıklı, araç penceresi olmayan, kendi sürecimiz hariç
public sealed class WgcFrameSource(IntPtr hwnd, Func<RegionF> region, int maxFps = 15) : IFrameSource
    { static Frame? Snapshot(IntPtr hwnd, int maxWidth = 1280); }   // bölge seçici için tek tam kare
```
Yöntem: D3D11 cihazı (Vortice, BGRA desteği) → `IDirect3DDevice` (CreateDirect3D11DeviceFromDXGIDevice) → `GraphicsCaptureItem` (`IGraphicsCaptureItemInterop.CreateForWindow`) → `Direct3D11CaptureFramePool.CreateFreeThreaded(B8G8R8A8UIntNormalized, 2)`; oturumda `IsCursorCaptureEnabled=false`, mümkünse `IsBorderRequired=false`. Her karede `maxFps` sınırı; yalnız `region()` dikdörtgeni `CopySubresourceRegion` ile tek bir staging dokusuna → `Map` → `Frame`. `ContentSize` değişirse havuz `Recreate`. Öğe `Closed` → `Stopped("Pencere kapandı")`. Simge durumunda kare gelmez (sorun değil).

- [ ] **Step 1: Failing tests:** `Lists_notepad_excludes_own_process` (`notepad.exe` başlat → listede `ExeName=="notepad.exe"` var; test sürecinin kendi PID'i listede yok), `Captures_region_of_notepad` (notepad'e metin yaz, bölge (0,0,1,0.3) → 2 sn içinde en az 1 `Frame`, boyut > 0, tamamen siyah değil), `Stopped_raised_when_window_closes` (notepad'i öldür → `Stopped` 2 sn içinde) — Review Focus 1.
- [ ] **Step 2–5:** FAIL → uygula → PASS → commit `feat(app): WGC yakalama`.

### Task 12: Overlay penceresi

**Files:** Create `src/OyunAltyazi.App/Overlay/OverlayWindow.xaml(.cs)`

**Interfaces — Produces:** `public sealed class OverlayWindow : Window, ISubtitleSink` — `void Attach(IntPtr gameHwnd, OverlayStyle style)`.

Davranış: `WindowStyle=None, AllowsTransparency=true, Background=Transparent, Topmost=true, ShowInTaskbar=false, ShowActivated=false`; `SourceInitialized`'da `WS_EX_TRANSPARENT|WS_EX_LAYERED|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`. Oyun penceresinin istemci dikdörtgenine her 500 ms'de hizalanır (`GetClientRect`+`ClientToScreen`, DPI dönüşümüyle); oyun penceresi ön planda değilse gizlenir. Altta ortalı, `BottomMargin` oranında yukarıda, yarı saydam koyu zeminli metin; konuşmacı adı ayrı renkte (`#4FD1C5`) ve kalın. `Show/Clear` UI iş parçacığına `Dispatcher.InvokeAsync` ile geçer. `Fallback` kaynaklı satır italik gösterilir.

- [ ] **Step 1:** Elle doğrulama betiği: App'e `--overlay-demo` argümanı ekle — notepad'e bağlanır, 3 örnek satırı 1,5 sn arayla gösterir, sonra temizler.
- [ ] **Step 2:** `dotnet run --project src/OyunAltyazi.App -- --overlay-demo` → overlay notepad'in altında görünür, içinden tıklayınca notepad'e yazı yazılabilir (tıklama geçiyor), notepad simge durumuna küçültülünce overlay kaybolur.
- [ ] **Step 3:** Commit `feat(app): overlay`.

### Task 13: Ana pencere, bölge seçici, kısayollar, tanılama

**Files:** Create `MainWindow.xaml(.cs)`, `UI/RegionSelector.xaml(.cs)`, `Hotkeys.cs`; Modify `App.xaml.cs` (kompozisyon kökü)

**Interfaces — Consumes:** tüm önceki görevler. **Produces:** çalışan uygulama.

Ekran (tek pencere, koyu tema):
```
[Oyun: (açılır liste: pencereler)  ⟳]   [Bölge seç]
[▶ Başlat / ■ Durdur]                   Çevirmen: Google
Son satır:  Price: Yukarı çıkın, hemen.   (kaynak: önbellek)
Gecikme (son 50): sabit→ekran ort 42 ms / p95 120 ms · yakalama→ekran ort 380 ms
```
- `RegionSelector`: `WgcFrameSource.Snapshot` görüntüsünü gösterir, fareyle dikdörtgen çizilir, mevcut profil bölgesiyle açılır, Kaydet → `GameProfile` güncellenir ve `SettingsStore.Save`.
- Oyun seçilince `SettingsStore.ProfileFor(exe)` yüklenir; `LastExe` hatırlanır ve açılışta listede o exe varsa otomatik seçilir.
- Kısayollar (`RegisterHotKey`): `Ctrl+Shift+T` başlat/durdur, `Ctrl+Shift+H` overlay gizle/göster, `Ctrl+Shift+R` bölge seçici.
- Önbellek: `%APPDATA%\OyunAltyazi\cache.db`; oyun anahtarı = exe adı.
- İngilizce OCR dil paketi yoksa Başlat devre dışı ve uyarı: "Windows İngilizce OCR paketi gerekli: Ayarlar › Zaman ve dil › Dil › İngilizce (ABD) ekleyin."
- Tanılama satırı 1 sn'de bir `LatencyStats`'tan güncellenir.

- [ ] **Step 1:** Uygula; `dotnet build` uyarısız.
- [ ] **Step 2:** Notepad ile duman testi: notepad'e "Price: Move up, now." yaz, pencereyi seç, bölgeyi metnin üstüne çiz, Başlat → ≤ 2 sn içinde overlay'de "Price" + Türkçe çeviri. Metni sil → ~1 sn sonra overlay temizlenir. Aynı metni tekrar yaz → kaynak "önbellek".
- [ ] **Step 3:** Uygulamayı kapat-aç → aynı pencere ve bölge hatırlanıyor.
- [ ] **Step 4:** Commit `feat(app): ana pencere ve akış`.

### Task 14: Kabul — MW4 ile gerçek oyun testi

- [ ] **Step 1:** `dotnet publish src/OyunAltyazi.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish` → `publish\OyunAltyazi.App.exe` çalışıyor.
- [ ] **Step 2 (kullanıcı ile):** MW4 kenarlıksız pencere/tam ekran modunda; pencereyi seç, bölge çiz, başlat. Kontrol listesi:
  - Replikler titremeden ve yarım çevrilmeden geliyor.
  - Tanılama: önbellek isabetinde sabit→ekran < 150 ms; çevrimiçi çeviride yakalama→ekran < 1 sn (p95 kaydedilir).
  - Oyun içi FPS (MSI Afterburner/oyun sayacı) açıkken ve kapalıyken karşılaştırılır: fark < %3.
  - HUD ipuçları çevrilmiyor; teal konuşmacı adı ayrılıyor (ayrılmıyorsa 2. aşamaya renk maskesi notu düşülür).
  - Ağ kesilince İngilizce italik yedek görünüyor, çökme yok.
- [ ] **Step 3:** Sonuçları `docs/mvp-test-sonuclari.md`'ye yaz; gerçek tam ekran (exclusive) modda siyah kare çıkıyorsa not et (bilinen WGC sınırı; öneri: kenarlıksız mod).
- [ ] **Step 4:** Commit `docs: MVP test sonuçları`.

---

## 2. aşamaya bırakılanlar (bu planda yok)
Geçmiş paneli, oyun sözlüğü, yama paketi içe aktarma/eşleme ve önbellek dışa aktarma, bulanık önbellek eşleşmesi, PaddleOCR+DirectML, MarianMT yerel çevirmen, DeepL/Gemini (+DPAPI anahtar), konuşmacı renk maskesi, sistem tepsisi, kurulum sihirbazı/installer, otomatik güncelleme.
