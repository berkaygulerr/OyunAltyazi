#!/usr/bin/env python3
"""score.py <prefix> : builds ground-truth segments from <prefix>.truth.tsv (OCR of every frame, no gating),
then scores <prefix>.shown.txt (pipeline output) -> recall, duplicates, latency."""
import sys,re,difflib,collections,os
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
shown=[];shown_raw=[]
for l in open(p+'.shown.txt',encoding='utf-8'):
    c=l.rstrip('\r\n').split('\t'); shown.append((int(c[0]),norm(c[1].replace(' § ',' ')))); shown_raw.append(c)
changes=[];hit=0;lat=[];miss_l=[];used=set()
for s in segs:
    win=[(i,t,x) for i,(t,x) in enumerate(shown) if s['start']-300<=t<=s['end']+1500]
    a=set(toks(s['rep'])); u=set()
    for i,t,x in win: u|=set(toks(x))
    if a and win and len(a&u)/len(a)>=0.6:
        hit+=1; lat.append(win[0][1]-s['start']); used.update(i for i,_,_ in win)
        prev=None; ch=0; seq=[]
        for i,_,_ in win:
            if len(a & set(toks(shown[i][1])))/len(a) < 0.5: continue   # only displays of THIS line
            us=shown_raw[i][1].split(' § ')
            cur=max(us,key=lambda u: len(a & set(toks(u)))/len(a))      # the utterance that is this line (other speakers on screen do not count)
            if cur!=prev: ch+=1; seq.append((shown[i][0],cur[:60]))
            prev=cur
        if os.environ.get('DBG') and ch>2: print('SEG',s['start'],s['rep'][:50],ch); [print('   ',q) for q in seq]
        changes.append(ch)
    else: miss_l.append((s['start'],s['rep'][:60]))
print(f"segments={len(segs)} hit={hit} recall={hit/max(1,len(segs)):.1%} shown={len(shown)} extraShown={len(shown)-len(used)}")
if changes: print(f"displayChangesPerLine avg={sum(changes)/len(changes):.2f} max={max(changes)} lines>1.3={sum(1 for c in changes if c>1.3)}")
if lat: print(f"latency avg={sum(lat)/len(lat):.0f}ms max={max(lat)}ms")
for m in miss_l[:60]: print('MISS',m)

# --- garbage + jitter ---
import statistics,os
W=set(w.strip() for w in open(os.environ.get('TMP_WORDS','/tmp/words_alpha.txt')))
OK2={'a','i','am','an','as','at','be','by','do','go','he','hi','if','in','is','it','me','my','no','of','oh','ok','on','or','so','to','up','us','we'}
SYM=re.compile(r"[<>|\[\]{}~^*_=\@#$%\u2022\u00bb\u00ab]")
def garbage1(x):
    x=re.sub(r"^[A-Z][\w .'-]{0,20}?:\s+","",x.strip())
    if SYM.search(x): return True
    ws=re.findall(r"[A-Za-z']+",x)
    if not ws: return True
    good=0
    for w in ws:
        lw=w.lower().strip("'"); lw2=lw.split("'")[0]
        if (len(lw)<=2 and lw in OK2) or (len(lw)>2 and (lw in W or lw2 in W or lw.replace("'","") in W or w[0].isupper())): good+=1
    return good/len(ws)<0.5
def garbage(x): return any(garbage1(u) for u in x.split(' § '))
g=[c for c in shown_raw if garbage(c[1])]
print(f"garbageShows={len(g)}/{len(shown_raw)}")
for c in g[:15]: print('GARB',c[0],c[1][:70])
bs=[];cx=[]
for c in shown_raw:
    if len(c)>2 and c[2]:
        x,y,w,h=map(float,c[2].split(',')); top=len(c)>4 and c[4]=='1'; bs.append(y if top else y+h); cx.append(x+w/2)
if len(bs)>1:
    H=float(os.environ.get('FRAME_H','140')); Wd=float(os.environ.get('FRAME_W','1344'))
    seg=[(float(c[2].split(',')[1]) if len(c)>4 and c[4]=='1' else float(c[2].split(',')[1])+float(c[2].split(',')[3])) for c in shown_raw if len(c)>2 and c[2] and int(c[0])<100000]
    if len(seg)>1: print(f"jitterMainScene(<100s) anchorEdgeStd={statistics.pstdev(seg)*H:.2f}px range={(max(seg)-min(seg))*H:.1f}px")
    jumps=sum(1 for a,b in zip(bs,bs[1:]) if abs(a-b)*H>4)
    print(f"anchorJumps(>4px between consecutive shows)={jumps}/{len(bs)-1}")
    print(f"jitter anchorEdgeStd={statistics.pstdev(bs)*H:.2f}px centerXStd={statistics.pstdev(cx)*Wd:.2f}px boxes={len(bs)} edgeRange={(max(bs)-min(bs))*H:.1f}px")
