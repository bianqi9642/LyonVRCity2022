import json,sys
for l in open(sys.argv[1]):
    d=json.loads(l)
    print(d['tag'],d['cfg'],d['npcs'],'int',d.get('interval'),'| frames',d['frames'],'mean %.1f med %.1f p95 %.1f p99 %.1f max %.1f fps %.1f'%(d['mean_ms'],d['median_ms'],d['p95_ms'],d['p99_ms'],d['max_ms'],d['fps_mean']),'| alloc/frame %.0f'%d.get('alloc_bytes_per_frame_approx',0),'| fixed/frame %.2f'%d['fixed_steps_per_frame_mean'])
    if d.get('snapshots'):
        print('   snaps/frame %.3f'%d['snapshots_per_frame'],{k[5:]:round(v,2) for k,v in d.items() if k.startswith('snap_')},'rays/snap %.0f'%d['raycasts_per_snapshot'],'B/snap %.0f'%d['bytes_per_snapshot'])
    mk={k[3:-10]:round(v,2) for k,v in d.items() if k.startswith('mk_') and isinstance(v,(int,float))}
    if mk: print('   markers:',mk)
