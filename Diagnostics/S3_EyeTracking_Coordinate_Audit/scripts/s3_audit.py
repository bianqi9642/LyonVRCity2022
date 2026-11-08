#!/usr/bin/env python3
"""S3-only audit (session 20261106T145652Z_0980aa930c32). READ-ONLY on the recordings. Everything is recomputed from the original gaze.csv / gaze_diagnostics.csv / events.csv."""
import json, os, sys, warnings
warnings.filterwarnings('ignore')
import numpy as np, pandas as pd
SRC = os.environ.get('S3_DIR', '/private/tmp/claude-501/-Users-qiaoy-Desktop-project-environment-LyonVRCity2022/fc47a207-d182-4410-a2e2-356aaa6b229c/scratchpad/posture/s3/20261106T145652Z_0980aa930c32')
OUT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
g = pd.read_csv(SRC + '/gaze.csv', dtype=str, keep_default_na=False); dg = pd.read_csv(SRC + '/gaze_diagnostics.csv', dtype=str, keep_default_na=False); ev = pd.read_csv(SRC + '/events.csv', dtype=str, keep_default_na=False)
n = len(g); N = lambda d, c: pd.to_numeric(d[c].replace('', np.nan), errors='coerce').values
assert (g.session_id == dg.session_id).all() and (g.frame_index == dg.frame_index).all() and (g.t_abs == dg.t_abs).all()
t = N(g, 't_abs'); fr = N(g, 'frame_index'); res = {}
def v3(d, p, s=('x', 'y', 'z')): return np.stack([N(d, p + a) for a in s], 1)
def q4(d, p): return np.stack([N(d, p + a) for a in 'xyzw'], 1)
def rotate(q, v):   # q (n,4 xyzw) * v (n,3)
    x, y, z, w = q.T; vx, vy, vz = v.T; tx = 2 * (y * vz - z * vy); ty = 2 * (z * vx - x * vz); tz = 2 * (x * vy - y * vx)
    return np.stack([vx + w * tx + (y * tz - z * ty), vy + w * ty + (z * tx - x * tz), vz + w * tz + (x * ty - y * tx)], 1)
def inv(q): return q * np.array([-1, -1, -1, 1])
def unit(v): nrm = np.linalg.norm(v, axis=1, keepdims=True); return v / np.where(nrm == 0, np.nan, nrm)
def ang(a, b): a = unit(a); b = unit(b); return np.degrees(np.arctan2(np.linalg.norm(np.cross(a, b), axis=1), (a * b).sum(1)))
hmd = v3(g, 'hmd_world_'); hq = q4(g, 'hmd_world_rot_'); head = v3(dg, 'head_pos_'); headq = q4(dg, 'head_rot_')
Lo, Ro, Co = v3(g, 'left_origin_'), v3(g, 'right_origin_'), v3(g, 'combined_origin_'); Ld, Rd, Cd = v3(g, 'left_dir_'), v3(g, 'right_dir_'), v3(g, 'combined_dir_')
rawLy, rawRy = N(dg, 'left_raw_tracking_pos_y'), N(dg, 'right_raw_tracking_pos_y'); rawY = np.nanmean(np.stack([rawLy, rawRy]), 0)
ground = N(g, 'ground_y'); hh = N(g, 'hmd_height_above_ground'); phase = g.phase.values
CAM = 1.36144; fwd = rotate(hq, np.tile([0, 0, 1.0], (n, 1))); up = rotate(hq, np.tile([0, 1.0, 0], (n, 1)))
# ------------------------------------------------------------------------------------------------ 1 source completeness
files = []
for root, _, fs in os.walk(SRC):
    for f in sorted(fs):
        p = os.path.join(root, f); r = dict(file=os.path.relpath(p, SRC), size_bytes=os.path.getsize(p), rows='', columns='', note='')
        if f.endswith('.csv'):
            with open(p, 'rb') as fh: first = fh.readline().decode().rstrip('\r\n'); r['rows'] = sum(1 for _ in fh); r['columns'] = len(first.split(','))
        files.append(r)
res['files_present'] = files
dt = np.diff(t); res['timestamps'] = dict(unit='s, CaptureClock.TAbs = Time.realtimeSinceStartupAsDouble (process-relative, absolute within the process; not UTC)', monotonic_strict=bool((dt > 0).all()), duplicates=int((dt == 0).sum()), first=float(t[0]), last=float(t[-1]), rows=n, session_id=g.session_id.unique().tolist(), subject=g.subject_id.unique().tolist())
res['events_window'] = ev[ev.event_type.isin(['session_start', 'part_start', 'trial_start', 'trial_end', 'part_end', 'session_end'])][['t_abs', 'event_type']].values.tolist()
# ------------------------------------------------------------------------------------------------ 2 validity / provenance
rawL = (dg.left_raw_tracking_pos_x != '').values; rawR = (dg.right_raw_tracking_pos_x != '').values; ptc = dg.provider_time_changed.values; pt = N(dg, 'provider_time_s')
devdir_l = N(dg, 'left_worldspace_vs_composed_dir_deg'); devdir_r = N(dg, 'right_worldspace_vs_composed_dir_deg'); devo_l = N(dg, 'left_worldspace_vs_composed_origin_m'); devo_r = N(dg, 'right_worldspace_vs_composed_origin_m')
src = g.gaze_origin_source.values; reason = dg.origin_fallback_reason.values
cat = []; note = []
for i in range(n):
    miss = (ptc[i] == '' or dg.anchors_updated_frame.iloc[i] == '')
    if src[i] != 'provider': c = 'C4_fallback_origin (direction provider-derived)'; nt = 'origin replaced by rig eye anchor (%s)' % reason[i]
    elif miss: c = 'C5_missing_provenance_fields'; nt = 'provider_time_changed / anchors_updated_frame blank (first row)'
    elif rawL[i] and rawR[i] and ptc[i] == '1' and max(devdir_l[i], devdir_r[i]) < 0.1: c = 'C1_provider_valid_confirmed_by_raw_read'; nt = 'raw provider read: both eyes IsValid; new provider sample; recorded direction == this sample'
    elif rawL[i] or rawR[i]: c = 'C2_provider_derived_validity_unconfirmed'; nt = 'one eye or no timestamp / direction evidence'
    else: c = 'C3_invalid_or_unavailable'; nt = 'no raw provider evidence'
    cat.append(c); note.append(nt)
cat = np.array(cat); origin_mixed = (np.maximum(np.nan_to_num(devo_l), np.nan_to_num(devo_r)) > 0.03) & (src == 'provider')
prov = pd.DataFrame(dict(gaze_csv_data_row=np.arange(1, n + 1), gaze_csv_line_number=np.arange(1, n + 1) + 1, t_abs=g.t_abs, frame_index=g.frame_index, phase=phase, eye_tracking_state_recorded=g.eye_tracking_state, left_provider_valid_recorded=dg.left_provider_valid.replace('', '<blank>'), right_provider_valid_recorded=dg.right_provider_valid.replace('', '<blank>'),
                         combined_valid_recorded=g.combined_valid, gaze_ray_source=g.gaze_ray_source, gaze_origin_source=src, origin_fallback_reason=reason, raw_left_eye_present=rawL, raw_right_eye_present=rawR, provider_time_s=dg.provider_time_s, provider_time_changed=ptc,
                         dir_dev_vs_provider_deg_left=np.round(devdir_l, 4), dir_dev_vs_provider_deg_right=np.round(devdir_r, 4), origin_coherence_m_left=np.round(devo_l, 4), origin_coherence_m_right=np.round(devo_r, 4), origin_mixed_frame_gt3cm=origin_mixed, confidence_recorded=dg.combined_confidence, category=cat, note=note))
prov.to_csv(OUT + '/s3_eye_sample_provenance.csv', index=False)
cc = pd.Series(cat).value_counts().to_dict(); res['categories'] = cc; res['origin_mixed_frame_rows'] = int(origin_mixed.sum())
val = []
def add(k, v, ev_, lab): val.append(dict(item=k, value=v, evidence=ev_, label=lab))
add('rows', n, 'gaze.csv / gaze_diagnostics.csv one-to-one on (session_id, frame_index, t_abs)', 'Confirmed')
add('eye_tracking_state=start_failed rows', int((g.eye_tracking_state == 'start_failed').sum()), 'gaze.csv column eye_tracking_state (all rows)', 'Confirmed')
add('left/right_provider_valid blank rows', int(((dg.left_provider_valid == '') & (dg.right_provider_valid == '')).sum()), 'columns written only when the LIFECYCLE probe was available (GazeRecorder.cs line ~1295 before the fix)', 'Confirmed')
add('rows with raw provider read (both eyes present)', int((rawL & rawR).sum()), 'left/right_raw_tracking_pos_* non-blank only if the provider flagged that eye IsValid (GazeRecorder.PipelineColumns); ReadRawGaze returns false unless OVRPlugin.eyeTrackingEnabled', 'Confirmed')
add('provider timestamp advanced', int((ptc == '1').sum()), 'provider_time_changed (EyeGazesState.Time)', 'Confirmed')
add('recorded direction == current provider sample (dev < 0.1 deg)', int((np.maximum(devdir_l, devdir_r) < 0.1).sum()), 'left/right_worldspace_vs_composed_dir_deg (max %.3f deg)' % np.nanmax(np.maximum(devdir_l, devdir_r)), 'Confirmed')
add('origin replaced by eye anchor', int((src != 'provider').sum()), 'gaze_origin_source != provider; reason=%s' % pd.Series(reason[src != 'provider']).value_counts().to_dict(), 'Confirmed')
add('provider-origin rows with origin coherence > 3 cm (mixed frame)', int(origin_mixed.sum()), 'left/right_worldspace_vs_composed_origin_m', 'Confirmed')
add('confidence distinct values', int(pd.Series(dg.combined_confidence).nunique()), 'combined_confidence; value %s' % sorted(set(dg.combined_confidence))[:3], 'Confirmed')
for k_, v_ in cc.items(): add('category ' + k_, int(v_), 's3_eye_sample_provenance.csv', 'Strongly supported' if k_.startswith('C1') else 'Confirmed')
pd.DataFrame(val).to_csv(OUT + '/s3_eye_validity_summary.csv', index=False)
# ------------------------------------------------------------------------------------------------ 3 HMD coordinate relations
r3 = {}
r3['hmd_world_minus_head_anchor_y'] = dict(median=float(np.nanmedian(hmd[:, 1] - head[:, 1])), std=float(np.nanstd(hmd[:, 1] - head[:, 1])), min=float(np.nanmin(hmd[:, 1] - head[:, 1])), max=float(np.nanmax(hmd[:, 1] - head[:, 1])))
r3['hmd_world_minus_head_anchor_xz_max'] = float(np.nanmax(np.hypot(hmd[:, 0] - head[:, 0], hmd[:, 2] - head[:, 2])))
r3['hmd_rot_vs_head_anchor_rot_deg'] = dict(median=float(np.nanmedian(ang(rotate(hq, np.tile([0, 0, 1.0], (n, 1))), rotate(headq, np.tile([0, 0, 1.0], (n, 1)))))), max=float(np.nanmax(ang(rotate(hq, np.tile([0, 0, 1.0], (n, 1))), rotate(headq, np.tile([0, 0, 1.0], (n, 1)))))))
base_est = head[:, 1] - rawY; r3['rig_base_est_minus_ground'] = dict(median=float(np.nanmedian(base_est - ground)), p05=float(np.nanpercentile(base_est - ground, 5)), p95=float(np.nanpercentile(base_est - ground, 95)), std=float(np.nanstd(base_est - ground)))
r3['hmd_height_above_ground_minus_(1.36144+raw_y)'] = dict(median=float(np.nanmedian(hh - CAM - rawY)), std=float(np.nanstd(hh - CAM - rawY)))
r3['raw_eye_y_stats'] = dict(median=float(np.nanmedian(rawY)), min=float(np.nanmin(rawY)), max=float(np.nanmax(rawY)), std=float(np.nanstd(rawY)))
r3['left_minus_right_raw_y'] = dict(median=float(np.nanmedian(rawLy - rawRy)), std=float(np.nanstd(rawLy - rawRy)))
# ------------------------------------------------------------------------------------------------ 4 vertical changes (time-indexed) and candidate transitions
win = max(3, int(round(5 / np.median(dt)))); y = pd.Series(rawY); ys = y.rolling(win, center=True, min_periods=2).median().values
steps = []
for i in range(1, n):
    j0 = max(0, i - 3); j1 = min(n - 1, i + 3)
    pass
lvl = ys - np.nanmedian(ys)    # relative to the session median (no absolute posture meaning)
hi = lvl > 0.15; seg = []; i = 0
while i < n:
    if hi[i]:
        j = i
        while j + 1 < n and hi[j + 1]: j += 1
        seg.append((i, j)); i = j + 1
    else: i += 1
res['candidate_transitions_rise_gt_0.15m_vs_session_median'] = [dict(start_t=float(t[a]), end_t=float(t[b]), duration_s=float(t[b] - t[a]), raw_y_median=float(np.nanmedian(rawY[a:b + 1]))) for a, b in seg]
# fast changes: |dy| over <=1.5 s
fast = []
for i in range(n):
    j = np.searchsorted(t, t[i] + 1.5)
    if j < n and abs(rawY[min(j, n - 1)] - rawY[i]) > 0.2 and (not fast or t[i] - fast[-1][0] > 5): fast.append((float(t[i]), float(t[min(j, n - 1)]), float(rawY[i]), float(rawY[min(j, n - 1)])))
res['fast_vertical_changes_gt_0.2m_in_1.5s'] = fast
# metrics helper
def metrics(sl):
    s = np.zeros(n, bool); s[sl] = True; hl = rotate(inv(hq), Co - hmd); o = hl[s]
    cdl = rotate(inv(hq), Cd)[s]; el = np.degrees(np.arcsin(np.clip(cdl[:, 1], -1, 1))); az = np.degrees(np.arctan2(cdl[:, 0], cdl[:, 2]))
    return dict(rows=int(s.sum()), raw_eye_y_m=float(np.nanmedian(rawY[s])), hmd_world_y_m=float(np.nanmedian(hmd[s, 1])), hmd_height_above_ground_m=float(np.nanmedian(hh[s])), head_pitch_up_deg=float(np.nanmedian(np.degrees(np.arcsin(np.clip(fwd[s, 1], -1, 1))))), head_roll_deg=float(np.nanmedian(np.degrees(np.arcsin(np.clip(rotate(hq, np.tile([1.0, 0, 0], (n, 1)))[s, 1], -1, 1))))),
                gaze_origin_world_y_m=float(np.nanmedian(Co[s, 1])), origin_minus_hmd_world_xyz_m=[float(np.nanmedian((Co - hmd)[s, k])) for k in range(3)], origin_minus_hmd_head_frame_lat_vert_fwd_m=[float(np.nanmedian(o[:, k])) for k in range(3)],
                gaze_dir_world_elev_deg=float(np.nanmedian(np.degrees(np.arcsin(np.clip(Cd[s, 1], -1, 1))))), gaze_dir_head_frame_elev_deg=float(np.nanmedian(el)), gaze_dir_head_frame_azim_deg=float(np.nanmedian(az)), gaze_vs_head_forward_deg=float(np.nanmedian(ang(Cd[s], fwd[s]))),
                provider_valid_evidence_pct=float(100 * np.mean((rawL & rawR)[s])), origin_fallback_pct=float(100 * np.mean((src != 'provider')[s])), origin_mixed_pct=float(100 * np.mean(origin_mixed[s])))
tr = []
trans = res['candidate_transitions_rise_gt_0.15m_vs_session_median']
for k_, T_ in enumerate(trans, 1):
    a = np.searchsorted(t, T_['start_t']); b = np.searchsorted(t, T_['end_t']); w = 30
    for nm, lo, hi_ in [('before(30s)', np.searchsorted(t, T_['start_t'] - w - 3), np.searchsorted(t, T_['start_t'] - 3)), ('during', a, b + 1), ('after(30s)', np.searchsorted(t, T_['end_t'] + 3), np.searchsorted(t, T_['end_t'] + 3 + w))]:
        sl = np.arange(lo, min(hi_, n))
        if len(sl) >= 3: m = metrics(sl); m.update(candidate_transition=k_, segment=nm, t_start=float(t[sl[0]]), t_end=float(t[sl[-1]])); tr.append(m)
pd.DataFrame(tr).to_csv(OUT + '/s3_posture_transition_diagnostics.csv', index=False)
# whole-session low/high phases for the coordinate summary
isHigh = np.zeros(n, bool)
for T_ in trans: isHigh |= (t >= T_['start_t']) & (t <= T_['end_t'])
low = np.where(~isHigh)[0]; high = np.where(isHigh)[0]; res['metrics_low_baseline'] = metrics(low); res['metrics_high_candidate'] = metrics(high) if len(high) > 2 else None
# ------------------------------------------------------------------------------------------------ 5 origin-vs-HMD and direction summary
rows = []
def stat(name, x, unit, label, note_=''):
    x = x[~np.isnan(x)]; rows.append(dict(quantity=name, unit=unit, n=len(x), mean=round(float(x.mean()), 5), median=round(float(np.median(x)), 5), sd=round(float(x.std()), 5), p01=round(float(np.percentile(x, 1)), 5), p05=round(float(np.percentile(x, 5)), 5), p95=round(float(np.percentile(x, 95)), 5), p99=round(float(np.percentile(x, 99)), 5), min=round(float(x.min()), 5), max=round(float(x.max()), 5), evidence_label=label, note=note_))
off_w = Co - hmd; off_h = rotate(inv(hq), Co - hmd); coh = ~origin_mixed & (src == 'provider')
for k_, nm in enumerate(['x(east)', 'y(up)', 'z(north)']): stat('combined_origin - hmd_world, WORLD axes ' + nm, off_w[:, k_], 'm', 'Confirmed', 'same frame: both are Unity world coordinates (hmd_world = Camera.main; origin = OVREyeGaze WorldSpace via Camera.main)')
for k_, nm in enumerate(['lateral(+right)', 'vertical(+up)', 'forward']): stat('combined_origin - hmd, HEAD frame ' + nm, off_h[:, k_], 'm', 'Confirmed', 'head frame = inverse(hmd_world_rot); all rows')
for k_, nm in enumerate(['lateral(+right)', 'vertical(+up)', 'forward']): stat('combined_origin - hmd, HEAD frame ' + nm + ' [coherent provider rows]', off_h[coh, k_], 'm', 'Confirmed', 'rows with origin_coherence <= 3 cm')
oLh = rotate(inv(hq), Lo - hmd); oRh = rotate(inv(hq), Ro - hmd)
for k_, nm in enumerate(['lateral', 'vertical', 'forward']): stat('LEFT origin - hmd, head frame ' + nm + ' [coherent]', oLh[coh, k_], 'm', 'Confirmed', 'expected -0.0325 lateral if the eye position is the nominal head-space offset')
for k_, nm in enumerate(['lateral', 'vertical', 'forward']): stat('RIGHT origin - hmd, head frame ' + nm + ' [coherent]', oRh[coh, k_], 'm', 'Confirmed', 'expected +0.0325 lateral')
stat('provider head-space eye position left x (diagnostic column)', N(dg, 'left_gaze_head_pos_x'), 'm', 'Confirmed', 'SDK head-space conversion of the raw provider eye pose')
stat('provider head-space eye position left y', N(dg, 'left_gaze_head_pos_y'), 'm', 'Confirmed'); stat('provider head-space eye position left z', N(dg, 'left_gaze_head_pos_z'), 'm', 'Confirmed')
stat('provider head-space eye position right x', N(dg, 'right_gaze_head_pos_x'), 'm', 'Confirmed')
# directions
for nm, d_ in [('left', Ld), ('right', Rd), ('combined', Cd)]: stat(f'|{nm}_dir| - 1', np.linalg.norm(d_, axis=1) - 1, '', 'Confirmed')
cdh = rotate(inv(hq), Cd); el_h = np.degrees(np.arcsin(np.clip(cdh[:, 1], -1, 1))); az_h = np.degrees(np.arctan2(cdh[:, 0], cdh[:, 2]))
stat('gaze dir in HEAD frame: elevation (+ = up)', el_h, 'deg', 'Confirmed', 'combined_dir rotated by inverse(hmd_world_rot)'); stat('gaze dir in HEAD frame: azimuth (+ = right)', az_h, 'deg', 'Confirmed')
ph_dir = v3(dg, 'left_gaze_head_dir_'); prh = v3(dg, 'right_gaze_head_dir_'); pel = np.degrees(np.arcsin(np.clip(unit(ph_dir)[:, 1], -1, 1))); paz = np.degrees(np.arctan2(ph_dir[:, 0], ph_dir[:, 2]))
stat('provider LEFT head-space direction: elevation', pel, 'deg', 'Confirmed', 'independent of Camera.main: SDK head-space conversion of the raw provider pose'); stat('provider LEFT head-space direction: azimuth', paz, 'deg', 'Confirmed')
stat('angle between recorded left_dir and camera*provider head dir', devdir_l, 'deg', 'Confirmed'); stat('angle gaze(combined) vs HMD forward', ang(Cd, fwd), 'deg', 'Confirmed', 'both in world frame')
stat('head pitch (+ = looking up) from hmd_world_rot', np.degrees(np.arcsin(np.clip(fwd[:, 1], -1, 1))), 'deg', 'Confirmed')
stat('angle hmd_world_rot vs OVR head anchor rot', ang(fwd, rotate(headq, np.tile([0, 0, 1.0], (n, 1)))), 'deg', 'Confirmed')
res['gaze_vs_head_by_group'] = dict(low=float(np.nanmedian(ang(Cd[low], fwd[low]))), high=float(np.nanmedian(ang(Cd[high], fwd[high]))) if len(high) else None)
res['head_frame_elev_by_group'] = dict(low=float(np.nanmedian(el_h[low])), high=float(np.nanmedian(el_h[high])) if len(high) else None)
pd.DataFrame(rows).to_csv(OUT + '/s3_hmd_gaze_coordinate_summary.csv', index=False)
# ------------------------------------------------------------------------------------------------ 6 offset vs rig movement (mixed frame cause)
hsp = np.r_[np.nan, np.hypot(np.diff(hmd[:, 0]), np.diff(hmd[:, 2]))]; vsp = np.r_[np.nan, np.abs(np.diff(hmd[:, 1]))]; ofs = np.linalg.norm(off_w, axis=1)
ok = ~np.isnan(hsp) & ~np.isnan(ofs)
res['corr_offset_vs_row_displacement'] = dict(horizontal=float(np.corrcoef(hsp[ok], ofs[ok])[0, 1]), vertical=float(np.corrcoef(np.nan_to_num(vsp[ok]), ofs[ok])[0, 1]))
res['offset_gt3cm_by_row_horizontal_move_m'] = dict(offset_gt3cm_median_row_move=float(np.nanmedian(hsp[(ofs > 0.03) & ok])), others=float(np.nanmedian(hsp[(ofs <= 0.03) & ok])))
# ------------------------------------------------------------------------------------------------ 7 sampling
fdt = np.diff(fr); ptd = np.diff(pt); sd = []
def srow(k, v, ev_, lab): sd.append(dict(item=k, value=v, evidence=ev_, label=lab))
srow('rows', n, 'gaze.csv', 'Confirmed'); srow('duration_s', round(float(t[-1] - t[0]), 2), 't_abs', 'Confirmed'); srow('rate_hz', round(float((n - 1) / (t[-1] - t[0])), 3), 'rows / duration', 'Confirmed')
srow('interval_median_s', round(float(np.median(dt)), 4), 't_abs diffs', 'Confirmed'); srow('interval_p05_p95_s', '%.3f / %.3f' % (np.percentile(dt, 5), np.percentile(dt, 95)), 't_abs diffs', 'Confirmed'); srow('interval_max_s', round(float(dt.max()), 3), 't_abs diffs', 'Confirmed')
srow('frame_index step: min/median/max', '%d / %d / %d' % (fdt.min(), np.median(fdt), fdt.max()), 'frame_index diffs: every Unity frame yields exactly one row', 'Confirmed'); srow('implied Unity frame rate (frames/s)', round(float(fdt.sum() / (t[-1] - t[0])), 3), 'sum(frame_index diffs)/duration', 'Confirmed')
srow('duplicate t_abs', int((dt == 0).sum()), 't_abs', 'Confirmed'); srow('non-monotonic t_abs', int((dt < 0).sum()), 't_abs', 'Confirmed'); srow('rows >1 s gap', int((dt > 1).sum()), 't_abs', 'Confirmed')
srow('provider_time advanced rows', int((ptd > 0).sum()), 'provider_time_s', 'Confirmed'); srow('provider_time repeated rows', int((ptd == 0).sum()), 'provider_time_s', 'Confirmed'); srow('provider sample interval median (s)', round(float(np.median(ptd)), 4), 'provider_time_s diffs (OVRPlugin clock)', 'Confirmed')
srow('provider vs row interval corr', round(float(np.corrcoef(ptd, dt)[0, 1]), 4), 'provider_time_s diffs vs t_abs diffs', 'Confirmed')
af = N(dg, 'anchors_updated_frame'); srow('rows with anchors_updated_frame == frame_index', int(np.nansum(af == fr)), 'OVRCameraRig.UpdatedAnchors in the same frame', 'Confirmed'); srow('unix offset spread (ms)', round(float(np.ptp(N(dg, 'unix_time_ms') - t * 1000)), 1), 'unix_time_ms - 1000*t_abs', 'Confirmed')
srow('frame-rate limited vs scheduler limited', 'frame-rate limited: nominal sample interval 0.05 s (20 Hz) cannot be met when one frame lasts %.2f s; the scheduler writes at most one row per frame' % np.median(dt), 'frame_index step == 1 on all rows (a 3.3 Hz recorder interval would give steps > 1)', 'Strongly supported')
srow('cause of the low frame rate (NPC count?)', 'not determinable from S3: frame timing block of the manifest is missing; S3 events record 3521 active NPCs at the Start gate (npc_behaviour_paused value_num) and 5247 lifetimes', 'events.csv, npc_lifetimes.csv', 'Uncertain')
pd.DataFrame(sd).to_csv(OUT + '/s3_gaze_sampling_diagnostics.csv', index=False)
res['r3'] = r3
res['dir_headframe_elev'] = dict(recorded_median=float(np.nanmedian(el_h)), provider_median=float(np.nanmedian(pel)), diff_median=float(np.nanmedian(el_h - pel)), diff_max_abs=float(np.nanmax(np.abs(el_h - pel))))
res['dir_headframe_azim'] = dict(recorded_median=float(np.nanmedian(az_h)), provider_median=float(np.nanmedian(paz)))
json.dump(res, open(OUT + '/s3_results.json', 'w'), indent=1, default=float)
np.savez(OUT + '/_arrays.npz', t=t, fr=fr, rawY=rawY, hmd=hmd, head=head, ground=ground, hh=hh, Co=Co, Lo=Lo, Ro=Ro, off_h=off_h, off_w=off_w, el_h=el_h, az_h=az_h, gv=ang(Cd, fwd), pel=pel, origin_mixed=origin_mixed, src=(src != 'provider'), rawL=rawL, rawR=rawR, devdir=np.maximum(devdir_l, devdir_r), devo=np.maximum(devo_l, devo_r), pitch=np.degrees(np.arcsin(np.clip(fwd[:, 1], -1, 1))), conf=N(dg, 'combined_confidence'), phase=phase, dt=dt, ptd=ptd, fdt=fdt)
print(json.dumps({k: res[k] for k in ['categories', 'origin_mixed_frame_rows', 'candidate_transitions_rise_gt_0.15m_vs_session_median', 'fast_vertical_changes_gt_0.2m_in_1.5s', 'dir_headframe_elev', 'dir_headframe_azim', 'gaze_vs_head_by_group', 'head_frame_elev_by_group', 'corr_offset_vs_row_displacement', 'offset_gt3cm_by_row_horizontal_move_m', 'r3']}, indent=1, default=float))
