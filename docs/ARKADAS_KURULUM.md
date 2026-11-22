# OyunAltyazı — Kurulum (arkadaşlar için)

1. Şu bağlantıdan indir ve çalıştır:
   https://github.com/berkaygulerr/OyunAltyazi-releases/releases/latest/download/OyunAltyazi-win-Setup.exe
2. **Windows SmartScreen** "Windows bilgisayarınızı korudu" derse: **Ek bilgi → Yine de çalıştır**. (Uygulama imzasız olduğu için çıkar, zararsızdır.) Yönetici izni gerekmez; Başlat menüsü ve masaüstüne kısayol eklenir.
3. İlk açılışta **Kurulum** penceresi yerel çeviri bileşenlerini indirir (Python, CTranslate2, çeviri modeli): **yaklaşık 300 MB (GPU yoksa) – 900 MB (NVIDIA ekran kartı varsa)**, bir kez. Kesilirse yeniden dener / kaldığı yerden devam eder. Bu sırada Google çeviri ile kullanılabilir; bittiğinde yerel çeviri otomatik seçilir.
4. Windows'ta İngilizce OCR paketi yoksa pencerede **"Windows İngilizce OCR paketini kur"** düğmesi çıkar (yönetici izni ister).
5. Sorun olursa: **Sistem → Bileşenleri onar**.

## Güncellemeler
Uygulama açıkken arka planda 30 dakikada bir yeni sürüm arar, sessizce indirir ve kapanınca uygular. Hazır olunca üstte "Yeni sürüm hazır — Yeniden başlat" bandı çıkar; çeviri sırasında hiçbir şey kendiliğinden kapanmaz. Yeniden kurmaya gerek yok.
