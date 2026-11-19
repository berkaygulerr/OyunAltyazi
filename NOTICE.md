# Üçüncü taraf bileşenler / Third-party notices

## Yerel çeviri (isteğe bağlı) / Local translation (optional)

- **Model:** [Helsinki-NLP/opus-mt-tc-big-en-tr](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-tr)
  — © Language Technology Research Group at the University of Helsinki / OPUS-MT, lisans **CC BY 4.0**.
  Model `tools/ct2server/setup.ps1` ile CTranslate2 biçimine dönüştürülür (int8_float16 nicemleme) — değiştirilmiş bir türevdir.
- **CTranslate2** — © OpenNMT, MIT lisansı. https://github.com/OpenNMT/CTranslate2
- **SentencePiece** — Google, Apache-2.0.
- NVIDIA cuBLAS / cuDNN pip paketleri (GPU için) kendi NVIDIA lisansları altındadır; yalnızca kullanıcının makinesine kurulur, dağıtılmaz.

## Kurulum

`powershell -ExecutionPolicy Bypass -File tools\ct2server\setup.ps1` — Python 3.12 venv'i ve modeli `%LOCALAPPDATA%\OyunAltyazi` altına kurar.
Kurulu değilse uygulama Google çevirisine döner.

Parts adapted from GameTranslator (sefflex) / CouchSubs (Sinan Yorulmaz), AGPL-3.0
