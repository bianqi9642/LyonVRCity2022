#!/usr/bin/env python3
"""Posture / head-tracking / eye-origin audit of three sessions (READ-ONLY on the recordings). Outputs under ../ : derived/*.csv, session_comparison.csv, posture_segments.csv,
eye_tracking_quality.csv, anomaly_samples.csv, results.json."""
import json, os, sys, warnings
warnings.filterwarnings('ignore')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from load import *
OUT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')); os.makedirs(OUT + '/derived', exist_ok=True)
CAMOFF = 1.36144
R = {}; SEG = []; ANOM = []; QUAL = []; CMP = []

def quat_axes(q):
    x, y, z, w = q.T
    f = np.stack([2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)], 1); r = np.stack([1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)], 1)
    return f, r
def runs(mask, t, min_len=0.0):
    out = []; i = 0; n = len(mask)
    while i < n:
        if mask[i]:
            j = i
            while j + 1 < n and mask[j + 1]: j += 1
            if t[j] - t[i] >= min_len: out.append((i, j))
            i = j + 1
        else: i += 1
    return out
def add_anom(s, t, var, val, exp, reason, cls, action):
    ANOM.append(dict(session=s, t_abs=round(float(t), 4) if t == t else '', variable=var, observed=val, expected=exp, reason=reason, class_=cls, recommended=action))

for k in ['S1', 'S2', 'S3']:
    g, dg, ev, tr = load(k); n = len(g); r = {}; R[k] = r
    t = num(g, 't_abs').values; D = pd.DataFrame(dict(t_abs=t, frame_index=num(g, 'frame_index').values, part=g.part, phase=g.phase))
    for c in ['hmd_world_x', 'hmd_world_y', 'hmd_world_z', 'ground_y', 'hmd_height_above_ground', 'combined_valid', 'ray_did_hit']: D[c] = num(g, c).values
    q = np.stack([num(g, 'hmd_world_rot_' + a).values for a in 'xyzw'], 1); f, rr = quat_axes(q); D['pitch_down_deg'] = -np.degrees(np.arcsin(np.clip(f[:, 1], -1, 1))); D['roll_deg'] = np.degrees(np.arcsin(np.clip(rr[:, 1], -1, 1))); D['yaw_deg'] = np.degrees(np.arctan2(f[:, 0], f[:, 2]))
    for s_ in ['left', 'right', 'combined']:
        for a in 'xyz': D[f'{s_}_origin_{a}'] = num(g, f'{s_}_origin_{a}').values; D[f'{s_}_dir_{a}'] = num(g, f'{s_}_dir_{a}').values
    D['left_conf'] = num(dg, 'left_confidence').values; D['right_conf'] = num(dg, 'right_confidence').values; D['combined_conf'] = num(dg, 'combined_confidence').values
    D['gaze_origin_source'] = g.gaze_origin_source.values; D['eye_tracking_state'] = g.eye_tracking_state.values; D['origin_fallback_reason'] = dg.origin_fallback_reason.values
    D['raw_head_local_y'] = num(dg, 'left_raw_tracking_pos_y').values   # OVRPlugin eye position in its eye-level tracking space (no camera offset): head height relative to the recentre point
    D['hmd_local_y_from_ground'] = D.hmd_height_above_ground - CAMOFF
    D['eye_hmd_dy'] = D.combined_origin_y - D.hmd_world_y; D['eye_height_above_ground'] = D.combined_origin_y - D.ground_y
    D['left_eye_h'] = D.left_origin_y - D.ground_y; D['right_eye_h'] = D.right_origin_y - D.ground_y
    D['eye_hmd_dist'] = np.sqrt((D.combined_origin_x - D.hmd_world_x) ** 2 + (D.combined_origin_y - D.hmd_world_y) ** 2 + (D.combined_origin_z - D.hmd_world_z) ** 2)
    # teleports from events
    te = ev[ev.event_type.isin(['teleport', 'river_crossing'])]; tt_ = num(te, 't_abs').values; D['dt_to_teleport'] = [np.min(np.abs(tt_ - x)) if len(tt_) else np.nan for x in t]
    D['teleport_epoch_from_events'] = [int((tt_ <= x).sum()) for x in t]; D['teleport_status'] = np.where(D.dt_to_teleport < 0.5, 'teleport_within_0.5s', 'none')
    if tr is not None:
        tj = tr.sort_values('t_abs'); idx = np.searchsorted(tj.t_abs.values, t); idx = np.clip(idx, 1, len(tj) - 1); pick = np.where(np.abs(tj.t_abs.values[idx - 1] - t) <= np.abs(tj.t_abs.values[idx] - t), idx - 1, idx)
        for c in ['rig_pos_x', 'rig_pos_y', 'rig_pos_z', 'teleport_epoch', 'hmd_local_pos_y', 'hmd_local_pos_x', 'hmd_local_pos_z']: D['traj_' + c] = tj[c].values[pick]
        D['traj_dt_s'] = np.abs(tj.t_abs.values[pick] - t)
    # horizontal head speed (virtual + physical), no teleport rows
    dtv = np.diff(t); hs = np.hypot(np.diff(D.hmd_world_x), np.diff(D.hmd_world_z)) / np.maximum(dtv, 1e-6); near = (D.dt_to_teleport.values[1:] < 0.7) | (D.dt_to_teleport.values[:-1] < 0.7); hs = np.where(near, np.nan, hs); D['hspeed_mps'] = np.r_[np.nan, hs]
    vy = np.diff(D.hmd_world_y) / np.maximum(dtv, 1e-6); vy = np.where(near, np.nan, vy); D['vspeed_mps'] = np.r_[np.nan, vy]
    vr = np.diff(D.raw_head_local_y) / np.maximum(dtv, 1e-6); D['vspeed_raw_mps'] = np.r_[np.nan, vr]   # physical head vertical speed in the OVRPlugin tracking space (no terrain / AutoGroundAlign)
    D['rig_base_above_ground_est'] = D.hmd_height_above_ground - CAMOFF - D.raw_head_local_y   # ground-relative HMD height minus camera offset minus physical head height = rig base height above the ground below the camera
    D['hh_roll5s'] = D.hmd_height_above_ground.rolling(max(3, int(round(5 / np.median(dtv)))), center=True, min_periods=2).median()
    D['rawy_roll5s'] = D.raw_head_local_y.rolling(max(3, int(round(5 / np.median(dtv)))), center=True, min_periods=2).median()
    D.to_csv(f'{OUT}/derived/synchronized_{k}.csv', index=False, float_format='%.5f')
    # ------------------------------------------------------------------ stats
    hh = D.hmd_height_above_ground; hw = D.hmd_world_y; ry = D.raw_head_local_y
    st = lambda s: dict(n=int(s.notna().sum()), mean=float(s.mean()), median=float(s.median()), std=float(s.std()), min=float(s.min()), max=float(s.max()), p05=float(s.quantile(.05)), p95=float(s.quantile(.95)))
    r.update(rows=n, duration_s=float(t[-1] - t[0]), first_t=float(t[0]), last_t=float(t[-1]), rate_hz=float((n - 1) / (t[-1] - t[0])), median_interval=float(np.median(dtv)), p95_interval=float(np.percentile(dtv, 95)), max_interval=float(dtv.max()),
             gaps_gt1s=int((dtv > 1).sum()), frame_delta_median=float(np.median(np.diff(D.frame_index))), participant=g.subject_id.unique().tolist(), session=g.session_id.unique().tolist(), parts=g.part.value_counts().to_dict(), phases=g.phase.value_counts().to_dict(),
             hmd_world_y=st(hw), hmd_height_above_ground=st(hh), ground_unavailable=int(hh.isna().sum()), raw_head_local_y=st(ry), hmd_local_from_ground=st(D.hmd_local_y_from_ground),
             rawy_vs_ground_local_diff=st(ry - D.hmd_local_y_from_ground), eye_hmd_dy=st(D.eye_hmd_dy), eye_h_ground=st(D.eye_height_above_ground), eye_hmd_dist=st(D.eye_hmd_dist), pitch_down=st(D.pitch_down_deg), roll=st(D.roll_deg),
             hspeed=st(D.hspeed_mps), vspeed_abs=st(D.vspeed_mps.abs()), conf_unique=sorted(set(np.round(D.combined_conf.dropna(), 4))), origin_source=D.gaze_origin_source.value_counts().to_dict(), eye_tracking_state=D.eye_tracking_state.value_counts().to_dict(),
             provider_valid_flag=dg.left_provider_valid.value_counts().to_dict(), blink_rows=int((dg.blink_state == '1').sum()), face_state=dg.face_state.value_counts().to_dict())
    # eye origin vs source
    for src in D.gaze_origin_source.unique(): s_ = D[D.gaze_origin_source == src]; r.setdefault('eye_dy_by_source', {})[src] = dict(n=len(s_), mean=float(s_.eye_hmd_dy.mean()), std=float(s_.eye_hmd_dy.std()), min=float(s_.eye_hmd_dy.min()), max=float(s_.eye_hmd_dy.max()), dist_mean=float(s_.eye_hmd_dist.mean()), dist_max=float(s_.eye_hmd_dist.max()))
    # eye dy vs roll (nominal head-frame offsets: lateral +-0.0325 -> vertical component = 0.0325*sin(roll) per eye; combined cancels)
    prov = D[D.gaze_origin_source == 'provider']; r['left_eye_dy_vs_roll_corr'] = float(np.corrcoef((prov.left_origin_y - prov.hmd_world_y), prov.roll_deg)[0, 1]); r['combined_dy_provider_absmax'] = float(prov.eye_hmd_dy.abs().max())
    r['left_right_origin_sep_m'] = st(np.sqrt((D.left_origin_x - D.right_origin_x) ** 2 + (D.left_origin_y - D.right_origin_y) ** 2 + (D.left_origin_z - D.right_origin_z) ** 2))
    # directions
    for s_ in ['left', 'right', 'combined']:
        v = D[[f'{s_}_dir_x', f'{s_}_dir_y', f'{s_}_dir_z']].values; nm = np.linalg.norm(v, axis=1); r[f'{s_}_dir_norm_dev_max'] = float(np.nanmax(np.abs(nm - 1))); r[f'{s_}_dir_zero_or_nan'] = int(((nm < 0.5) | np.isnan(nm)).sum())
    cd = D[['combined_dir_x', 'combined_dir_y', 'combined_dir_z']].values; ang = np.degrees(np.arccos(np.clip((cd * f).sum(1), -1, 1))); D['gaze_vs_head_deg'] = ang
    r['gaze_vs_head_deg'] = st(pd.Series(ang)); same = (np.abs(np.diff(cd, axis=0)).max(1) == 0); r['gaze_dir_identical_consecutive_rows'] = int(same.sum()); dang = np.degrees(np.arccos(np.clip((cd[1:] * cd[:-1]).sum(1), -1, 1))); r['gaze_dir_step_deg'] = st(pd.Series(dang))
    r['rays_hit_pct'] = float(100 * D.ray_did_hit.mean()); 
    # frozen head tracking
    pose = g[['hmd_world_x', 'hmd_world_y', 'hmd_world_z', 'hmd_world_rot_x', 'hmd_world_rot_y', 'hmd_world_rot_z', 'hmd_world_rot_w']].values; fr = (pose[1:] == pose[:-1]).all(1); r['frozen_pose_consecutive_rows'] = int(fr.sum())
    # stationarity
    stat = (D.hspeed_mps.fillna(0) < 0.05).values; r['stationary_fraction'] = float(np.mean(stat)); r['hspeed_median'] = float(D.hspeed_mps.median())
    # per-phase
    for p, d_ in D.groupby('phase'):
        gg = g[g.phase == p]; QUAL.append(dict(session=k, part=d_.part.iloc[0], phase=p, rows=len(d_), duration_s=round(float(d_.t_abs.iloc[-1] - d_.t_abs.iloc[0]), 1), rate_hz=round(float((len(d_) - 1) / max(d_.t_abs.iloc[-1] - d_.t_abs.iloc[0], 1e-6)), 2), combined_valid_pct=round(float(100 * d_.combined_valid.mean()), 2),
            provider_origin_pct=round(float(100 * (d_.gaze_origin_source == 'provider').mean()), 2), eye_anchor_fallback_pct=round(float(100 * (d_.gaze_origin_source != 'provider').mean()), 2), eye_tracking_state=';'.join(sorted(d_.eye_tracking_state.unique())), mean_confidence=round(float(d_.combined_conf.mean()), 3),
            confidence_constant=bool(d_.combined_conf.nunique() == 1), provider_time_changed_pct=round(float(100 * (dg.loc[d_.index, 'provider_time_changed'] == '1').mean()), 2), ray_hit_pct=round(float(100 * d_.ray_did_hit.mean()), 1), ground_unavailable_pct=round(float(100 * d_.hmd_height_above_ground.isna().mean()), 2),
            hmd_h_median=round(float(d_.hmd_height_above_ground.median()), 3), hmd_h_p05=round(float(d_.hmd_height_above_ground.quantile(.05)), 3), hmd_h_p95=round(float(d_.hmd_height_above_ground.quantile(.95)), 3), eye_h_median=round(float(d_.eye_height_above_ground.median()), 3), eye_hmd_dy_median=round(float(d_.eye_hmd_dy.median()), 4), eye_hmd_dy_std=round(float(d_.eye_hmd_dy.std()), 4),
            pitch_down_median=round(float(d_.pitch_down_deg.median()), 1), blink_rows=int((dg.loc[d_.index, 'blink_state'] == '1').sum())))
    # ------------------------------------------------------------------ posture segments (relative to the OVRPlugin eye-level origin = recentre point)
    lvl = np.where(D.rawy_roll5s >= -0.10, 0, np.where(D.rawy_roll5s >= -0.30, 1, 2)); lvl = np.where(D.rawy_roll5s > 0.12, 3, lvl)   # 3 = above the recentre height
    names = {0: 'near recentre height', 1: 'lowered 10-30 cm', 2: 'markedly lowered (>30 cm)', 3: 'raised >12 cm above recentre height'}
    i = 0
    while i < n:
        j = i
        while j + 1 < n and lvl[j + 1] == lvl[i]: j += 1
        if t[j] - t[i] >= 3.0 or (i == 0 and j == n - 1):
            s_ = D.iloc[i:j + 1]; pd_ = float(s_.pitch_down_deg.mean()); sp = float(s_.hspeed_mps.median()); lv = lvl[i]
            if lv == 0: cls, conf, why = 'Standing-compatible / reference posture (standing vs sitting NOT decidable)', 'low', 'head within 10 cm of the recentre height; absolute posture depends on the unknown recentre posture'
            elif lv == 1: cls, conf, why = ('Bending forward (candidate)' if pd_ > 25 else 'Other / uncertain (small lowering)'), 'low', 'head 10-30 cm below recentre height; pitch_down mean %.0f deg' % pd_
            elif lv == 2: cls, conf, why = ('Sitting or crouching compatible (not separable)' if pd_ < 35 else 'Bending forward / crouching (candidate)'), 'low-medium', 'head >30 cm below recentre height; pitch_down mean %.0f deg' % pd_
            else: cls, conf, why = 'Other / uncertain (raised above recentre height)', 'low', 'head >12 cm above recentre height (tiptoe, leaning back or origin drift)'
            SEG.append(dict(session=k, start_t_abs=round(float(t[i]), 3), end_t_abs=round(float(t[j]), 3), duration_s=round(float(t[j] - t[i]), 1), phase=';'.join(pd.unique(s_.phase)), level=names[lv], classification=cls, confidence=conf, rows=len(s_), head_y_rel_recentre_median_m=round(float(s_.raw_head_local_y.median()), 3),
                            hmd_height_above_ground_median_m=round(float(s_.hmd_height_above_ground.median()), 3), pitch_down_mean_deg=round(pd_, 1), horiz_speed_median_mps=round(sp, 3) if sp == sp else '', evidence=why))
        i = j + 1
    # ------------------------------------------------------------------ anomalies
    for a_, b_ in runs(D.hmd_height_above_ground.isna().values, t): add_anom(k, t[a_], 'hmd_height_above_ground', 'blank (no ground below the camera)', 'ground found', 'ground raycast found no Ground-layer surface for %d rows (%.1f s: %.2f-%.2f)' % (b_ - a_ + 1, t[b_] - t[a_], t[a_], t[b_]), 'B suspicious (ground reference unavailable, not a head-tracking error)', 'retain HMD world pose; exclude from ground-relative statistics')
    jump = (D.hmd_height_above_ground.diff().abs() > 0.25) & (D.dt_to_teleport > 0.7)
    for ix in np.where(jump)[0]: add_anom(k, t[ix], 'hmd_height_above_ground', 'jump %.3f m in one row (dt %.2f s)' % (D.hmd_height_above_ground.diff().iloc[ix], dtv[ix - 1]), '< 0.25 m between rows away from teleports', 'sudden height change not near a teleport', 'B suspicious (posture change, ground-surface switch or tracking)', 'retain; inspect')
    vv = D.vspeed_raw_mps.abs() > 0.8
    for ix in np.where(vv.fillna(False))[0]: add_anom(k, t[ix], 'raw_head_local_y', 'physical vertical head speed %.2f m/s (row spacing %.2f s)' % (D.vspeed_raw_mps.iloc[ix], dtv[ix - 1]), '<0.8 m/s for ordinary head motion', 'rapid vertical head movement: standing up / sitting down / squat (or a tracking-origin step, see report)', 'B suspicious (posture change vs origin shift not separable)', 'retain; flag')
    rb = (D.rig_base_above_ground_est < -0.05).fillna(False).values
    for a_, b_ in runs(rb, t, 1.5): add_anom(k, t[a_], 'ground-relative HMD height', 'rig base %.3f m (min %.3f) relative to ground for %.1f s (%.2f-%.2f)' % (D.rig_base_above_ground_est.iloc[a_:b_ + 1].median(), D.rig_base_above_ground_est.iloc[a_:b_ + 1].min(), t[b_] - t[a_], t[a_], t[b_]), '~0 +-0.03 m (AutoGroundAlign keeps the XR Origin base on the ground)', 'the rig base sits below the ground surface below the camera: ground-relative heights are too LOW here although the physical head height (OVRPlugin) is unchanged', 'B suspicious (ground reference / AutoGroundAlign, not posture)', 'use raw_head_local_y for posture; exclude from ground-relative eye height')
    for ix in np.where(D.hmd_height_above_ground < 0.5)[0]: add_anom(k, t[ix], 'hmd_height_above_ground', float(D.hmd_height_above_ground.iloc[ix]), '>0.5 m (head cannot be below the ground)', 'HMD close to / below the ground surface', 'C invalid or crouch', 'inspect')
    for ix in np.where(D.gaze_origin_source != 'provider')[0]: add_anom(k, t[ix], 'combined_origin', 'source=%s reason=%s eye-HMD dist %.3f m' % (D.gaze_origin_source.iloc[ix], D.origin_fallback_reason.iloc[ix], D.eye_hmd_dist.iloc[ix]), 'provider origin within the 0.12 m gate', 'provider origin rejected: replaced by the rig eye anchor re-applied at the rendered view', 'B suspicious (origin substituted)', 'retain with flag gaze_origin_source != provider')
    for a_, b_ in runs(fr.astype(bool), t[1:]): add_anom(k, t[1:][a_], 'hmd_world pose', 'identical for %d consecutive rows' % (b_ - a_ + 2), 'pose changes (sensor noise)', 'frozen tracking', 'C invalid', 'exclude')
    for ix in np.where(dtv > 1.0)[0]: add_anom(k, t[ix], 't_abs', 'interval %.2f s' % dtv[ix], '<0.5 s', 'recording gap (frame hitch / loading)', 'B suspicious (gap)', 'retain; mark gap')
    for ix in np.where(D.eye_hmd_dist > 0.12)[0][:0]: pass
    for ix in np.where((D.gaze_vs_head_deg > 70).values)[0]: add_anom(k, t[ix], 'gaze_dir vs head forward', '%.0f deg' % D.gaze_vs_head_deg.iloc[ix], '< ~60 deg for eye-in-head gaze', 'gaze direction far from head forward', 'B suspicious', 'retain; inspect')
    if (D.eye_tracking_state == 'start_failed').mean() > 0.5: add_anom(k, np.nan, 'eye_tracking_state / provider validity', 'start_failed on %.0f%% of rows; left/right_provider_valid blank' % (100 * (D.eye_tracking_state == 'start_failed').mean()), 'running with provider-valid flags', 'EyeTrackingLifecycle never reached running (acquisition start failed) although every row has valid=1 from confidence only (provider validity unknown)', 'B suspicious (validity not corroborated by the provider flag)', 'retain only after verifying provider samples vary (provider_time advances, directions vary: yes); treat validity as unconfirmed')
    if D.combined_conf.nunique() == 1: add_anom(k, np.nan, 'confidence', 'constant %.3f on all rows' % D.combined_conf.iloc[0], 'varying 0..1', 'no quality information', 'B suspicious', 'do not use as a quality covariate')
    if np.median(dtv) > 0.2: add_anom(k, np.nan, 't_abs', 'median interval %.3f s (%.1f Hz) = one row per Unity frame' % (np.median(dtv), 1 / np.median(dtv)), '0.05 s (20 Hz)', 'Editor running at ~3 fps (frame_index step 1) with a very large NPC population (%d lifetimes)' % len(pd.read_csv(SESS[k] + '/npc_lifetimes.csv', usecols=['npc_id'])), 'B suspicious (temporal resolution)', 'retain; unsuitable for saccade / fixation dynamics; posture-level analysis only')
    # excursions (3 s rolling vs 60 s baseline) for posture-change annotations
    base = D.raw_head_local_y.rolling(max(5, int(round(60 / np.median(dtv)))), center=True, min_periods=5).median(); dev = (D.rawy_roll5s - base)
    ex = runs(((dev < -0.12) | (dev > 0.12)).fillna(False).values, t, 2.0); r['height_excursions_ge_12cm_ge_2s'] = len(ex)
    for a_, b_ in ex[:60]:
        s_ = D.iloc[a_:b_ + 1]; dv = float(dev.iloc[a_:b_ + 1].mean()); pdn = float(s_.pitch_down_deg.mean()); lab = ('Bending forward / looking down (candidate)' if dv < 0 and pdn > 25 else ('Crouch / lowering (candidate)' if dv < 0 else 'Raised excursion (uncertain)'))
        SEG.append(dict(session=k, start_t_abs=round(float(t[a_]), 3), end_t_abs=round(float(t[b_]), 3), duration_s=round(float(t[b_] - t[a_]), 1), phase=';'.join(pd.unique(s_.phase)), level='EXCURSION vs 60 s local median (%+.2f m)' % dv, classification=lab, confidence='low', rows=len(s_), head_y_rel_recentre_median_m=round(float(s_.raw_head_local_y.median()), 3),
                        hmd_height_above_ground_median_m=round(float(s_.hmd_height_above_ground.median()), 3), pitch_down_mean_deg=round(pdn, 1), horiz_speed_median_mps=round(float(s_.hspeed_mps.median()), 3) if s_.hspeed_mps.notna().any() else '', evidence='short-term deviation >=12 cm of the PHYSICAL head height (OVRPlugin y) from its 60 s local median, >=2 s'))
    # S2 trajectory cross-check
    if tr is not None:
        r['traj_rows'] = len(tr); tt2 = tr.t_abs.values; d2 = np.diff(tt2); r['traj_median_interval'] = float(np.median(d2)); r['traj_hmd_local_y'] = st(tr.hmd_local_pos_y); r['traj_ovr_view_offset_m'] = float((tr.view_pos_y - tr.hmd_pos_y).median())
        r['traj_hmdlocal_vs_gaze_raw_corr'] = float(np.corrcoef(D.traj_hmd_local_pos_y.fillna(0), D.raw_head_local_y.fillna(0))[0, 1]); r['traj_hmdlocal_minus_raw_median'] = float((D.traj_hmd_local_pos_y - D.raw_head_local_y).median())
    CMP.append(dict(metric_session=k))
json.dump(R, open(OUT + '/results.json', 'w'), indent=1, default=float)
pd.DataFrame(SEG).to_csv(OUT + '/posture_segments.csv', index=False); pd.DataFrame(QUAL).to_csv(OUT + '/eye_tracking_quality.csv', index=False)
an = pd.DataFrame(ANOM).rename(columns={'class_': 'class'}); an.to_csv(OUT + '/anomaly_samples.csv', index=False)
print(an.groupby(['session', 'variable']).size().to_string()); print(pd.DataFrame(SEG).groupby(['session', 'level']).agg(n=('duration_s', 'size'), dur=('duration_s', 'sum')).to_string())
