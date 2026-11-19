#!/usr/bin/env python3
"""score.py <prefix> : builds ground-truth segments from <prefix>.truth.tsv (OCR of every frame, no gating),
then scores <prefix>.shown.txt (pipeline output) -> recall, duplicates, latency."""
import sys,re,difflib,collections
p=sys.argv[1]
def toks(s): return re.findall(r"[a-z0-9']+",s.lower())
def sim(a,b): return difflib.SequenceMatcher(None,a,b).ratio()
rows=[]
for l in open(p+'.truth.tsv',encoding='utf-8'):
    t,_,x=l.rstrip('\n').partition('\t'); rows.append((int(t),x))
# strip speaker prefix & "(ARABIC)" noise for matching
def norm(x): return ' '.join(toks(re.sub(r'^[^:?!.]{1,30}:\s*','',x)))
segs=[];cur=None;miss=0
for t,x in rows:
    n=norm(x)
    if len(n)<8: miss+=1
    if len(n)<8:
        if cur and miss>=3: segs.append(cur);cur=None
        continue
    miss=0
    if cur and sim(n,cur['last'])>=0.6: cur['texts'].append(n);cur['end']=t;cur['last']=n
    else:
        if cur: segs.append(cur)
        cur={'start':t,'end':t,'texts':[n],'last':n}
if cur: segs.append(cur)
segs=[s for s in segs if len(s['texts'])>=4]   # >= ~0.25s at 15fps
for s in segs: s['rep'],s['cnt']=collections.Counter(s['texts']).most_common(1)[0]
segs=[s for s in segs if s['cnt']>=3]   # only lines that are rendered stably (excludes fade/transition garbage)
shown=[]
for l in open(p+'.shown.txt',encoding='utf-8'):
    t,_,x=l.rstrip('\n').partition('\t'); shown.append((int(t),norm(x)))
hit=0;lat=[];miss_l=[];used=set()
for s in segs:
    win=[(i,t,x) for i,(t,x) in enumerate(shown) if s['start']-300<=t<=s['end']+1500]
    a=set(toks(s['rep'])); u=set()
    for i,t,x in win: u|=set(toks(x))
    if a and win and len(a&u)/len(a)>=0.6:
        hit+=1; lat.append(win[0][1]-s['start']); used.update(i for i,_,_ in win)
    else: miss_l.append((s['start'],s['rep'][:60]))
print(f"segments={len(segs)} hit={hit} recall={hit/max(1,len(segs)):.1%} shown={len(shown)} extraShown={len(shown)-len(used)}")
if lat: print(f"latency avg={sum(lat)/len(lat):.0f}ms max={max(lat)}ms")
for m in miss_l[:60]: print('MISS',m)
