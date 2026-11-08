import json,sys,collections
def load(p):
    out={}
    try:
        for l in open(p):
            d=json.loads(l); out[(d['npcs'],d['cfg'],d.get('interval',0.1) if d['cfg']=='D' else 0.1)]=d
    except FileNotFoundError: pass
    return out
base=load(sys.argv[1]); opt=load(sys.argv[2]) if len(sys.argv)>2 else {}
keys=sorted(set(base)|set(opt),key=lambda k:(k[0],{'A':0,'B':1,'D':2}[k[1]]))
def f(d,k,fmt='%.1f'):
    return (fmt%d[k]) if d and k in d and d[k] is not None else '–'
print('| NPC | 配置 | 版本 | 帧数 | 平均 ms | 中位 ms | P95 ms | P99 ms | 最大 ms | FPS(均) | 每帧托管分配 KB | 快照/帧 | 单次快照 ms | 其中 LOS | 其中 行格式化 | 其中 写盘 |')
print('|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
for k in keys:
    for nm,src in (('基线',base),('优化后',opt)):
        d=src.get(k)
        if not d: continue
        cfg={'A':'A 仅仿真','B':'B/C 默认采集(0.1 s)','D':'D 采集 %.1f s'%k[2]}[k[1]]
        al=d.get('alloc_bytes_per_frame_approx'); 
        print('| %d | %s | %s | %d | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |'%(k[0],cfg,nm,d['frames'],f(d,'mean_ms'),f(d,'median_ms'),f(d,'p95_ms'),f(d,'p99_ms'),f(d,'max_ms'),f(d,'fps_mean'),('%.0f'%(al/1024)) if al is not None else '–',f(d,'snapshots_per_frame','%.2f'),f(d,'snap_total_ms'),f(d,'snap_los_ms'),f(d,'snap_format_ms'),f(d,'snap_write_ms','%.2f')))
