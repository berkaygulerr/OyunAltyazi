# Native ONNX CPU vs existing CTranslate2 (opus-mt-tc-big-en-tr)

Harness: `tools/onnxbench` (one mode per process). Cases: `tools/onnxbench/cases.txt` (20 single lines, 5 continuation pairs, original generic text).
Machine: 32 logical cores, RTX 4060 Laptop. Reps 5 => n=100 single calls, 25 continuation calls. Latency is what the app sees
(`TranslateAsync` / `TranslateContinuationAsync`, incl. IPC for CT2). Raw output: the `*.json` files here.
ONNX model: onnx-community/opus-mt-tc-big-en-tr @ ce128ea834a090aca240e4e113e7756e06cc78e0, encoder_model_int8 + decoder_model_merged_int8 (SHA-256 pinned in `OnnxModelStore`).

| backend | load (ms) | warm p50 / p95 (ms) | continuation p50 / p95 (ms) | working set after load / peak (MB) |
|---|---|---|---|---|
| ONNX CPU beams 4, 2 threads (reference default) | 1738 | 1335 / 1858 | 1362 / 1571 | 1163 / 1208 |
| ONNX CPU beams 2, 4 threads | 1701 | 870 / 1273 | 803 / 989 | 1163 / 1192 |
| ONNX CPU beams 2, 8 threads | 1648 | 768 / 1129 | 708 / 865 | 1164 / 1192 |
| CT2 CPU int8, beam 2 | 505 | 125 / 232 | 217 / 309 | 281 / 431 |
| CT2 CPU int8, beam 4 | 503 | 160 / 299 | 275 / 378 | 280 / 431 |
| CT2 auto (CUDA, current default here), beam 2 | 763 | 31 / 156 | 167 / 232 | 739 / 755 |

(load = construct/start + first translation; an earlier, shorter run of CT2 CPU beam 2 gave p50 70 ms, so CT2 numbers vary ~2x run to run; the gap to ONNX stays 6-19x.)

Setup bytes: ONNX 534,578,564 (5 model files) + 17.3 MB onnxruntime.dll; CT2 installed 240.8 MB model + 1.118 GB python runtime
(of which 959 MB are NVIDIA CUDA wheels; ~159 MB CPU-only).

Quality (same 20 lines): ONNX beam 2 == CT2 beam 2 on 20/20 (ONNX beam 4 differs on 3 lines, wording only: "teşekkürler, sana borçluyum" vs
"sağ ol, sana borçlandım"; formal "-ın/-iniz" vs informal). Both drop the first sentence of one two-sentence input
("Did you hear that? Something is moving..."): model behaviour, not tokenizer.
Continuations: 5/5 return only the new part and read as one sentence with the shown prefix; ONNX matches CT2 on 4/5, on the fifth ONNX is
"Düşman takviye kuvvetleri gelmeden önce." and CT2 "düşman takviye kuvvetleri gelmeden." (CT2 drops "önce").
Tokenizer: C# ids equal Python sentencepiece+vocab.json (HF MarianTokenizer behaviour) on 7 source and 3 target strings incl. unknown characters.

Decision: ONNX is opt-in (`"onnx"` setting); Auto is unchanged (CT2 if installed, else Google). ONNX is 6-19x slower than CT2 CPU and ~4x more memory.
