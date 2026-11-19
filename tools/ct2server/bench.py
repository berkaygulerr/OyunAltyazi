import sys, subprocess, json, time
model, dev = sys.argv[1], sys.argv[2]
beam = sys.argv[3] if len(sys.argv) > 3 else "2"
L = ["Frag out!", "Reloading!", "We need to move, now. They're flanking us from the east.",
     "Price, this is Bravo Six. We have eyes on the target.", "Stay close and keep your head down.", "Get to the chopper!"]
p = subprocess.Popen([sys.executable, __file__.replace("bench.py", "server.py"), model, "--device", dev, "--beam", beam],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
print(p.stdout.readline().strip())
tot = []; outs = {}
for r in range(5):
    for i, t in enumerate(L):
        t0 = time.perf_counter()
        p.stdin.write(json.dumps({"id": i, "texts": [t]}) + "\n"); p.stdin.flush()
        o = json.loads(p.stdout.readline()); tot.append((time.perf_counter() - t0) * 1000); outs[t] = o["translations"][0]
print("beam", beam, dev, "avg roundtrip ms: %.1f" % (sum(tot) / len(tot)))
for k, v in outs.items(): print(k, "=>", v)
if len(sys.argv) > 4: input()
p.kill()
