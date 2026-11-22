# Üçüncü taraf bileşenler / Third-party notices

OyunAltyazı, **GNU AGPL-3.0-only** lisanslıdır (bkz. `LICENSE`). Aşağıdaki projelere borçludur:

## Uyarlanan kod / Adapted code (AGPL-3.0)

- **[GameTranslator](https://github.com/sefflex/GameTranslator)** — © sefflex, AGPL-3.0. Arayüz tasarımı ve OCR sonrası metin işleme bölümleri (metin temizleme, tuş ipuçları, kararlılık/eşleştirme vb.) bu projeden uyarlanmıştır.
- **CouchSubs** — © Sinan Yorulmaz, AGPL-3.0. GameTranslator'ın temelini oluşturan özgün proje; fikir ve bazı bölümler oradan gelir.
- **english-words.txt.gz** (`src/OyunAltyazi.Core/Resources`) — SCOWL/ESDB size-60 İngilizce sözcük listesi, GameTranslator'dan alınmıştır. SCOWL lisansı: http://wordlist.aspell.net/scowl-readme/
- **ONNX çeviri motoru / ONNX translation engine** (`src/OyunAltyazi.Translation/Onnx/`: `MarianTokenizer.cs`, `OnnxMarianEngine.cs`, `OnnxModelStore.cs`) — GameTranslator'ın `windows/src/GameTranslator.Translation` altındaki `MarianTokenizer`, `OnnxMarianEngine` ve `ModelStore` kodundan uyarlanmıştır (© sefflex, AGPL-3.0, SPDX başlıkları korunmuştur). Değişiklikler / changes: hedef önek zorlama (cümle devamı), ayrı source/target SentencePiece, ayarlanabilir iş parçacığı, her dosya için zorunlu SHA-256.
- **GameTranslator paket biçimi / pack format** (`format: 2`, `entries: {hex16: tr}`) — `GamePack.cs` bu açık veri biçimini okur; paket içeriği dağıtılmaz.

## Kütüphaneler / Libraries

- **[Velopack](https://github.com/velopack/velopack)** — MIT. Kurulum ve otomatik güncelleme.
- **[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)** (Vortice.Direct3D11) — MIT. Ekran yakalama.
- **Microsoft.Data.Sqlite** — MIT. Çeviri önbelleği.
- **Python 3.12 (embeddable)** — PSF License; ilk açılışta python.org'dan indirilir, dağıtılmaz.

## Yerel çeviri (isteğe bağlı) / Local translation (optional)

- **Model:** [Helsinki-NLP/opus-mt-tc-big-en-tr](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-tr)
  — © Language Technology Research Group at the University of Helsinki / OPUS-MT, lisans **CC BY 4.0**.
  Model `tools/ct2server/setup.ps1` ile CTranslate2 biçimine dönüştürülür (int8_float16 nicemleme) — değiştirilmiş bir türevdir.
- **CTranslate2** — © OpenNMT, MIT lisansı. https://github.com/OpenNMT/CTranslate2
- **SentencePiece** — Google, Apache-2.0.
- **[ONNX Runtime](https://github.com/microsoft/onnxruntime)** (Microsoft.ML.OnnxRuntime 1.31.1) ve **Microsoft.ML.Tokenizers** 2.0.1 — MIT. İsteğe bağlı yerel ONNX CPU çevirisi.
- **ONNX modeli:** [onnx-community/opus-mt-tc-big-en-tr](https://huggingface.co/onnx-community/opus-mt-tc-big-en-tr) (revizyon `ce128ea834a090aca240e4e113e7756e06cc78e0` sabitlenmiş; int8 kodlayıcı + birleşik kod çözücü) — Helsinki-NLP modelinin değiştirilmemiş ONNX dönüşümü, CC BY 4.0. Dosyalar indirilirken SHA-256 ile doğrulanır; depoda dağıtılmaz.
- NVIDIA cuBLAS / cuDNN pip paketleri (GPU için) kendi NVIDIA lisansları altındadır; yalnızca kullanıcının makinesine kurulur, dağıtılmaz.

## Kurulum

`powershell -ExecutionPolicy Bypass -File tools\ct2server\setup.ps1` — Python 3.12 venv'i ve modeli `%LOCALAPPDATA%\OyunAltyazi` altına kurar.
Kurulu değilse uygulama Google çevirisine döner.


- **english-words.txt.gz** (src/OyunAltyazi.Core/Resources) — SCOWL/ESDB size-60 English word list taken from GameTranslator (sefflex), AGPL-3.0; SCOWL license: http://wordlist.aspell.net/scowl-readme/
- **Hazır oyun paketleri / ready game packs** — `src/OyunAltyazi.Core/AutoPack.cs` yalnızca küçük bir katalog (commit sabitli URL + SHA-256 + exe adı) içerir; paket içeriği depoya konmaz, kullanıcı bir oyunu seçince [sefflex/GameTranslator `yamalar/`](https://gitlab.com/sefflex/GameTranslator/-/tree/70e12ef2e3a0f5122b099ce9d8a682cc32c37d82/yamalar) üzerinden indirilip `%AppData%\OyunAltyaziutopacks` altına önbelleğe alınır. Paketlerin hakları kendi sahiplerine aittir.
