import csv, statistics as st, collections, sys
def pct(a,p): a=sorted(a); k=(len(a)-1)*p; f=int(k); c=min(f+1,len(a)-1); return a[f]+(a[c]-a[f])*(k-f)
for name in ('sim_before_active','sim_after_active','sim_after_saved'):
    R=list(csv.DictReader(open(name+'/aga_sim.csv')))
    n=len(R); hits=[r for r in R if r['hit']=='1']
    print('==',name,'rows',n,'ground hit',len(hits),'(%.1f%%)'%(100*len(hits)/n),'teleports',sum(r['teleport']=='1' for r in R))
    gap=[float(r['gap_to_target']) for r in hits]
    if gap:
        print('  target - rig_y (m): min %.3f median %.3f mean %.3f max %.3f; |gap|>1cm: %d'%(min(gap),st.median(gap),st.fmean(gap),max(gap),sum(abs(g)>0.01 for g in gap)))
    chg=[r for r in R if r['hit']=='1' and abs(float(r['dy_step']))>1e-4]
    print('  steps that move the rig (|dy|>0.1mm):',len(chg))
    ds=[float(r['dy_step']) for r in R if r['hit']=='1']
    print('  per-step dy (m): min %.4f max %.4f ; total sim path Y span: %.3f'%(min(ds),max(ds),max(float(r['y_after']) for r in R)-min(float(r['y_after']) for r in R)))
    vs=[abs(float(r['dy_step']))/float(r['step_dt']) for r in R if r['hit']=='1' and r['teleport']=='0' and float(r['step_dt'])>0]
    print('  vertical speed due to alignment (m/s): p50 %.3f p95 %.3f max %.3f'%(pct(vs,.5),pct(vs,.95),max(vs)))
    # target discontinuities while walking (no teleport)
    jumps=[];slopes=[]
    prev=None
    for r in R:
        if r['teleport']=='1' or r['hit']!='1': prev=None if r['hit']!='1' else r; continue
        if prev is not None and prev['hit']=='1':
            d=abs(float(r['hit_y'])-float(prev['hit_y'])); h=float(r['horiz_step_m'])
            jumps.append((d,h,r['t'],prev['hit_name'][:14],r['hit_name'][:14]))
        prev=r
    if jumps:
        big=sorted(jumps,reverse=True)[:3]
        print('  target-height change between consecutive walking samples: max %.3f m; top:'%big[0][0],[(round(a,3),round(b,3),t,p,q) for a,b,t,p,q in big])
        tileSwitch=[j for j in jumps if j[3]!=j[4]]; print('  collider switches while walking:',len(tileSwitch),'max |dTarget| at a switch: %.4f'%max([j[0] for j in tileSwitch] or [0]))
    print('  region of selected surface:',dict(collections.Counter(r['region'] for r in hits)))
    print('  selected collider:',collections.Counter(r['hit_name'][:16] for r in hits).most_common(6))
    print('  hit layer:',collections.Counter(r['hit_layer'] for r in hits),' trigger hits:',sum(r['hit_trigger']=='1' for r in hits))
    byreg=collections.defaultdict(list)
    for r in hits: byreg[r['region']].append(float(r['gap_to_target']))
    for k,v in byreg.items(): print('   gap in',k,': n=%d min %.3f median %.3f mean %.3f max %.3f'%(len(v),min(v),st.median(v),st.fmean(v),max(v)))
    # post-teleport convergence: gap at teleport rows
    tg=[float(r['gap_to_target']) for r in R if r['teleport']=='1' and r['hit']=='1']
    print('  gap at teleport landing rows: n=%d min %.3f median %.3f max %.3f'%(len(tg),min(tg),st.median(tg),max(tg)) if tg else '  no landing with ground')
    # stationary: rows where horiz<0.01 : does y keep moving?
    stat=[r for r in R if r['teleport']=='0' and float(r['horiz_step_m'])<0.01 and r['hit']=='1']
    print('  stationary samples:',len(stat),'max |dy_step| while stationary: %.4f'%max([abs(float(r['dy_step'])) for r in stat] or [0]), ' (converges: gap shrinks by factor 0.5 per 50 ms step)')
