# OyunAltyazi — masaüstü arayüz yönü

**Yüzey: Operate (çalıştır ve kontrol et).** Kullanıcı oyunu seçer, okuma alanını belirler ve çeviriyi başlatır. Bu üç adım ilk bakışta anlaşılır olmalı. Ayar ekranı ikincil, seçili kaynak ve çalışma durumu sürekli görünür.

## Kompozisyon
- Ana ekranın odak noktası mevcut gerçek pencere önizlemesi ve okuma alanı. Kaynak boşsa `Oyun penceresi seç` gibi somut yönlendirme; dekoratif görsel veya uydurma oyun verisi yok.
- Ana işlem (`Çeviriyi başlat` / `Durdur`) tek vurgulu düğme. Pencere seçici ve bölge düzenleme ona yakın; mevcut dört sekmeye doğrudan, klavyeyle erişim.
- Son altyazı ve teknik durum aynı ağırlıkta kutular yığını olmasın. Son altyazıyı geniş ve okunur, gecikme ve OCR/aygıt durumunu kısa bir durum şeridi olarak düzenle. Gecikme yalnız ölçüm varsa sayı göstersin.
- Çeviri ayarları: motor ve aygıt bir grup; hazır paket/sözlük bir grup. Otomatik ayarları görünür, elle içe aktarma ve önbellek gibi daha seyrek işlemleri ikincil tut.

## Görsel dil
Koyu kömür ve soğuk gri, sıcak fildişi metin ve tek amber vurgu. Yeşil, kırmızı ve sarı yalnız durum için. Yerel Segoe UI / Segoe UI Variable, sayılar ve kısayollarda gerektiğinde Cascadia Mono veya yerel monospace. Ortak hizalar, 8/12/16/24 DIP boşluk ritmi, ince ayrımlar. Gradyan, cam efekti, dekoratif neon, gereksiz ikon ve sürekli animasyon yok. Görünür klavye odağı; renk tek başına durum taşımasın.

## Uygulama ve kabul
Mevcut WPF projesinde, tarayıcı veya yeni çalışma zamanı eklemeden geliştir. Yakalama ve çeviri davranışını değiştirme; CPU/GPU, Gemini, otomatik paket/sözlük, bölge düzenleme ve güncelleme akışlarını koru. Gerçek WPF ekranında başlangıç ve ayar sekmelerini aç; min. pencere genişliğinde ve mevcut yüksek DPI'da taşma, kontrast, açık menü ve klavye odağını kontrol et. Gerçek sayfa ekran görüntülerini kişisel pencere adları/oyun içeriği olmadan al. Önce ve sonra UI hiyerarşisini denetle: eşit kutular, sahte metrik, merkez yığını ve aşırı dekorasyon bulunmamalı.
