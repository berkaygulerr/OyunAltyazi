# OyunAltyazi — Tasarım (2026-11-19)

> Ad geçici. Windows PC oyunları için OCR tabanlı, gerçek zamanlı İngilizce → Türkçe altyazı çevirici.

## Amaç ve neden
PC'de Türkçe desteği olmayan oyunları, oyunun kendi altyazısını okuyup Türkçesini üstüne basarak oynamak.
Odak: **hız** (ekranda satır göründükten sonra Türkçesi olabildiğince çabuk) ve **rahatlık** (oyunu seç, çalışsın; oyun başına ayar hatırlansın).
GameSubTranslate (Electron) ve sefflex/GameTranslator'dan öğrenilenlerle **ayrı, sıfırdan** yazılan bir program. Yayınlanacak, kendi lisansıyla.

## Hedefler
- Pencere seçimiyle oyun yakalama; tam ekran DX11/DX12/Vulkan oyunlarda siyah/donuk kare olmadan.
- Altyazı bölgesinde OCR → filtre/sabitleyici → çeviri → tıklamayı geçiren şeffaf overlay.
- Gecikme hedefi: önbellek/yama isabetinde < 150 ms, çevrimiçi çeviride < 1 sn (satır ilk göründükten overlay'e).
- Oyun dışında boşta CPU ~%0; oyun sırasında FPS kaybı hissedilmeyecek düzeyde (hedef < %3).
- Tek kurulum: tek exe/klasör, ilk açılış sihirbazı.

## Hedef dışı
- TV, konsol (PS5/Xbox Remote Play), mobil, OBS, ikinci ekran, dublaj/TTS.
- macOS/Linux.
- Ortak havuza satır gönderme veya herhangi bir telemetri.

## Anti-hedefler (çalışsa bile başarısızlık sayılır)
- Oyunda FPS düşürmek ya da girdi gecikmesi eklemek.
- Altyazının titremesi / yarım cümlenin çevrilip ekranda zıplaması.
- Kullanıcının haberi olmadan metin/anahtar dışarı göndermek; API anahtarını düz metin saklamak.
- sefflex/GameTranslator (AGPL-3.0) kodundan kopyalama veya satır satır çeviri. Yalnız fikirler ve açık veri biçimi kullanılır.

## Kısıtlar
- Windows 10 1903+ / 11, x64. Geliştirme makinesi: RTX 4060 8 GB. Oyun VRAM'in çoğunu kullanır.
- Kullanıcının test oyunu yeni bir CoD (MW4); bunun için hazır yama yok → çevrimiçi/yerel çeviri yolu MVP'de birincil yol.
- Lisans: AGPL-3.0-only (sonradan değişti: kodun bir kısmı sefflex/GameTranslator'dan uyarlandı).

## Yaklaşımlar
| Yaklaşım | Artı | Eksi |
|---|---|---|
| **C# .NET 10 + WPF (seçilen)** | Windows API'leri (WGC, Windows.Media.Ocr, DPAPI) birinci sınıf; ONNX Runtime + DirectML hazır; hızlı geliştirme | Rust'a göre biraz daha fazla RAM |
| Rust + egui/Tauri | En hafif ikili | WinRT OCR/overlay için çok daha fazla iş; gecikmede ölçülebilir kazanç yok (darboğaz OCR ve ağ) |
| Electron (GameSubTranslate'i genişletmek) | Mevcut kod | Kullanıcı ayrı program istedi; ağır, tesseract.js yavaş |

Seçim gerekçesi: gecikmenin %95'i OCR (30–150 ms) ve ağ (300–1000 ms); dil seçimi birkaç ms fark ettirir. C# Windows entegrasyonunda en az riskli yol.

## Mimari
```
Oyun penceresi
  └─ Yakalama: Windows.Graphics.Capture (D3D11 frame pool), yalnız altyazı bölgesi CPU'ya kopyalanır
      └─ Değişim kapısı: bölgenin küçük gri/kenar imzası öncekiyle aynıysa OCR yok
          └─ OCR: Windows.Media.Ocr (varsayılan) | PaddleOCR ONNX + DirectML (zor yazı tipleri, isteğe bağlı)
              └─ Temizlik: buton/HUD gürültüsü, OCR karışıklıkları (l/1, O/0), konuşmacı adı ayırma (renk maskesi + "Ad:" kalıbı)
                  └─ Sabitleyici: aynı metin N kare görülünce yayınla; boş kare eşiğiyle silme; büyüyen (daktilo) satırları birleştir
                      └─ Çeviri zinciri: yama tam → yama bulanık → yerel önbellek (SQLite) → seçili çevirmen
                          └─ Overlay (WPF, WS_EX_TRANSPARENT|LAYERED, topmost) + geçmiş paneli
```
Her aşama ayrı bir arayüz arkasında, birim testli. Yakalama/OCR bir arka plan iş parçacığında, kuyruk derinliği 1 (eski kare biriktirilmez).

## Çeviri
- **Çevirmenler:** Google (ücretsiz, anahtarsız), DeepL (anahtar), Gemini / OpenAI uyumlu API (anahtar), yerel MarianMT (opus-mt-en-tr, CTranslate2 veya ONNX, isteğe bağlı GPU — birkaç yüz MB VRAM).
- **Yapay zekâ bağlamı:** son 6–8 satır (kaynak + çeviri) ve konuşmacı adı; oyun adı; oyun sözlüğü. Çıktı JSON dizi, bozuk yanıta tolerans.
- **Sözlük:** oyun başına JSON (`"Price": "Price"`, rütbe/kod adları). Hem istemde hem çeviri sonrası zorunlu değiştirmede kullanılır.
- **Önbellek:** SQLite; anahtar = normalleştirilmiş kaynak metin + oyun; bulanık eşleşme (kısa satırlarda kapalı). Kalıcı.
- **Hız hilesi:** yazı daktilo gibi beliriyorsa sabitleyici tam satırı bekler; satır görünür görünmez önbellek/yama sorgulanır, çevirmen isteği paralel başlar, eskiyen istek iptal edilir.

## Topluluk yama paketleri (uyumluluk)
- Biçim (yalnız veri biçimi, kod değil): `{format:2, id, title, source, entries:{ "<16 hex>": "Türkçe" }}`; anahtar = İngilizce satırın sadeleştirilmiş hâlinin SHA-256 ilk 8 baytı.
- Sadeleştirme kuralları biçim belgesinden (etiket sil → NFKC → küçük harf, "İ" özel durumu → NFD ile işaret sil → harf/rakam dışı boşluk → boşlukları tekle/kırp) bağımsız yazılır; paketlerde İngilizce metin olmadığından doğruluk, oyundan bilinen İngilizce satırların anahtarlarının pakette bulunmasıyla test edilir (ör. GTA V/RDR2 bilinen replikleri; test sırasında paket indirilir, repoya konmaz).
- Bulanık eşleşme kendi yöntemimizle (ör. OCR varyantları üretip her birinin anahtarını denemek); onların bulanık indeksi kullanılmaz.
- Paketler programla **dağıtılmaz**; kullanıcı "Paket ekle" ile dosya/URL verir. Oyun exe adına göre paket eşlemesi. Paketin lisans/kaynak bilgisi arayüzde gösterilir.
- Kendi önbelleğimiz aynı biçimde **dışa aktarılabilir** (MW4 gibi paketi olmayan oyunlar için kişisel paket).

## Rahatlık
- İlk açılış: pencere seç → bölgeyi sürükle → çevirmen seç → bitti.
- Oyun profili (exe adına göre otomatik): bölge, konuşmacı rengi, overlay konumu/boyutu, çevirmen, sözlük.
- Kısayollar: aç/kapat, bölge seç, overlay'i gizle, son satırı tekrar göster/geçmişi aç.
- Sistem tepsisi; oyun kapanınca boşta bekler.
- Ayarlar `%APPDATA%\OyunAltyazi`; API anahtarları DPAPI ile.

## MVP (1. aşama) — kabul yolculuğu
Kullanıcı programı açar, MW4 penceresini seçer, altyazı bölgesini çizer, Google çevirmeni seçili. Oyunda İngilizce bir replik çıkınca Türkçesi overlay'de, satır sabitlendikten sonra < 1 sn içinde, titremeden görünür; aynı replik tekrar çıkınca önbellekten anında gelir. Oyunun FPS'i belirgin düşmez. Program kapatılıp açılınca bölge ve ayarlar hatırlanır.

MVP'de olmayanlar (2. aşama): geçmiş paneli, sözlük, yama paketleri, PaddleOCR/DirectML, yerel MarianMT, DeepL/Gemini, kurulum sihirbazı/installer, otomatik güncelleme.

## Ölçüm
Her satır için zaman damgaları (yakalama, OCR, sabitleme, çeviri, gösterim) hata ayıklama günlüğüne yazılır; "Tanılama" ekranında son 50 satırın ortalama/p95 gecikmesi görünür. Hız iddiaları bu ölçümle doğrulanır.

## Yapımcıya bırakılanlar
Proje/klasör yapısı, WGC sarmalayıcısı (CsWinRT veya Vortice), test çatısı (xUnit), sabitleyici eşik değerleri (ölçümle ayarlanır), overlay görsel detayları (koyu yarı saydam bar, ayarlanabilir punto).
