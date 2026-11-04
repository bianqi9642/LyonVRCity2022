# Compares the replay.csv of two TerrainProbe runs (before / after a layer change). Usage: cd <dir containing before/ and after/>; python3 compare.py
import csv, collections, statistics as st, sys
def load(d): return list(csv.DictReader(open(d+'/replay.csv')))
def short(n): return n.split(' (')[0]
for name in ('before','after'):
    R=load(name)
    g=[r for r in R if r['kind']=='gaze']; gh=[r for r in g if r['ground_hit']=='1']
    print('==',name,'gaze rows',len(g),'ground_y available',len(gh),'%.1f%%'%(100*len(gh)/len(g)))
    print('  ground colliders hit',collections.Counter(short(r['ground_name'])[:16] for r in gh).most_common(8))
    hh=[float(r['y'])-float(r['ground_y']) for r in gh]
    print('  hmd height above ground: min %.3f max %.3f mean %.3f sd %.3f'%(min(hh),max(hh),st.fmean(hh),st.pstdev(hh)))
    # recorded-hit agreement
    tot=same=0; layer=collections.Counter()
    for r in g:
        if r['recorded_hit'].startswith(('gml_','ID_')):
            tot+=1; same+= short(r['roi_name'])==r['recorded_hit']
    print('  ROI replay: recorded terrain/bridge hits',tot,'same collider',same)
    cl=collections.Counter()
    for r in g:
        if r['roi_hit']=='1': cl[(short(r['roi_name'])[:4], r['roi_layer'])]+=1
    print('  ROI hit layers (name prefix, layer):',cl.most_common(8))
    rig=[r for r in R if r['kind']=='rig']; rh=[r for r in rig if r['ground_hit']=='1']
    print('  rig base: Ground within 3 m (AutoGroundAlign active)',len(rh),'of',len(rig))
    if rh:
        d=[float(r['ground_y'])-float(r['y']) for r in rh]
        print('  AutoGroundAlign (ground - rig y): min %.3f max %.3f mean %.3f mean|.| %.3f'%(min(d),max(d),st.fmean(d),st.fmean([abs(x) for x in d])))
    an=[r for r in R if r['kind']=='anchor']; ah=[r for r in an if r['ground_hit']=='1']
    print('  teleport/river destinations with Ground within 12 m',len(ah),'of',len(an))
