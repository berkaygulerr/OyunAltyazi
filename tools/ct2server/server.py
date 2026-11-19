"""OyunAltyazi CTranslate2 sidecar. stdin: {"id":n,"texts":[...]} -> stdout: {"id":n,"translations":[...],"ms":x}.
Args: server.py MODEL_DIR [--device auto|cuda|cpu] [--beam N]. Logs go to stderr only."""
import sys, os, json, time, argparse


def add_dll_dirs():
    # pip nvidia-* wheels put cublas/cudnn DLLs under site-packages\nvidia\*\bin
    import site
    for sp in site.getsitepackages():
        nv = os.path.join(sp, "nvidia")
        if os.path.isdir(nv):
            for d in os.listdir(nv):
                b = os.path.join(nv, d, "bin")
                if os.path.isdir(b):
                    os.add_dll_directory(b)
                    os.environ["PATH"] = b + os.pathsep + os.environ["PATH"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("--device", default="auto")
    ap.add_argument("--beam", type=int, default=2)
    a = ap.parse_args()
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    add_dll_dirs()
    import ctranslate2, sentencepiece as spm
    dev = a.device
    if dev == "auto":
        dev = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
    ct = "int8_float16" if dev == "cuda" else "int8"
    try:
        tr = ctranslate2.Translator(a.model, device=dev, compute_type=ct)
        sp_s = spm.SentencePieceProcessor(os.path.join(a.model, "source.spm"))
        sp_t = spm.SentencePieceProcessor(os.path.join(a.model, "target.spm"))

        def run(texts):
            toks = [sp_s.encode(t, out_type=str) + ["</s>"] for t in texts]
            res = tr.translate_batch(toks, beam_size=a.beam, max_decoding_length=256)
            return [sp_t.decode(r.hypotheses[0]) for r in res]
        run(["Warm up."])
    except Exception as e:
        if dev == "cuda" and a.device == "auto":
            print("cuda failed, cpu fallback: %r" % e, file=sys.stderr)
            dev, ct = "cpu", "int8"
            tr = ctranslate2.Translator(a.model, device=dev, compute_type=ct)
            run(["Warm up."])
        else:
            raise
    print(json.dumps({"ready": True, "device": dev, "compute": ct}), flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            t0 = time.perf_counter()
            out = run(req["texts"])
            print(json.dumps({"id": req["id"], "translations": out,
                              "ms": round((time.perf_counter() - t0) * 1000, 1)}, ensure_ascii=False), flush=True)
        except Exception as e:
            print(json.dumps({"id": req.get("id") if isinstance(req, dict) else None, "error": str(e)}), flush=True)


if __name__ == "__main__":
    main()
