import json, os, sys, warnings
warnings.filterwarnings('ignore')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from load import *
OUT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')); R = json.load(open(OUT + '/results.json'))
an = pd.read_csv(OUT + '/anomaly_samples.csv'); an = an[~an.variable.eq('combined_origin_vertical_offset')]; rows = []
for k in ['S1', 'S2', 'S3']:
    d = pd.read_csv(f'{OUT}/derived/synchronized_{k}.csv')
    big = (d.eye_hmd_dy.abs() > 0.03) & (d.gaze_origin_source == 'provider')
    for ix in np.where(big)[0]: rows.append(dict(session=k, t_abs=round(d.t_abs.iloc[ix], 4), variable='combined_origin_y - hmd_world_y', observed='%+.3f m' % d.eye_hmd_dy.iloc[ix], expected='~0 (combined origin = mean of two eyes at +-0.0325 m lateral in the head frame, ~0 vertical)',
                                       reason='provider origin accepted by the 0.12 m gate but physically implausible vertical offset: provider sample and rendered camera not from the same pose (camera moved vertically between the two; %s)' % ('within 1 s of a teleport' if d.dt_to_teleport.iloc[ix] < 1 else 'no teleport nearby'),
                                       **{'class': 'B suspicious (mixed-frame origin)'}, recommended='retain; flag gaze_origin_source/eye_hmd_dist>0.03 and exclude from eye-height analysis'))
RUN = {}
for k in ['S1', 'S2']:
    d = pd.read_csv(f'{OUT}/derived/synchronized_{k}.csv'); bad = ((d.hmd_world_z > 3000) | (d.hmd_world_y < 100) | (d.hspeed_mps > 50)).values; RUN[k] = bad
    ix = np.where(bad)[0]; rows.append(dict(session=k, t_abs=round(d.t_abs.iloc[ix[0]], 4), variable='hmd_world position (XR Origin runaway)', observed='%d consecutive rows %.2f-%.2f s: world Z %.0f -> %.0f m, Y %.1f -> %.1f m, x %.0f; no teleport event; ground raycast blank' % (len(ix), d.t_abs.iloc[ix[0]], d.t_abs.iloc[ix[-1]], d.hmd_world_z.iloc[ix[0]], d.hmd_world_z.iloc[ix[-1]], d.hmd_world_y.iloc[ix[0]], d.hmd_world_y.iloc[ix[-1]], d.hmd_world_x.iloc[ix[-1]]),
                    expected='a continuous position inside the city model', reason='the virtual rig (not the head) is displaced by roughly 980-1017 m per frame along +Z (free fall in Y in S1) with physical head height unchanged (raw_head_local_y 0.03-0.06 m): a locomotion / physics runaway, not a tracking loss; the session was quit 12.7 s (S2) / 44 s (S1) later', **{'class': 'C invalid (world position)'}, recommended='exclude all rows from this timestamp on from every world-coordinate and ground-relative analysis; head orientation, physical head height (raw_head_local_y) and gaze directions stay usable'))
an = pd.concat([an, pd.DataFrame(rows)], ignore_index=True); an.insert(0, 'anomaly_id', range(1, len(an) + 1)); an.to_csv(OUT + '/anomaly_samples.csv', index=False)
cnt = an.groupby('session').size().to_dict(); seg = pd.read_csv(OUT + '/posture_segments.csv')
def pc(k): s = seg[(seg.session == k) & (seg.level.str.startswith('markedly') | seg.level.str.startswith('near') | seg.level.str.startswith('lowered') | seg.level.str.startswith('raised'))]; return len(s) - 1
cmp_ = []
meta = {'S1': dict(sid='20261106T140500Z_2a699c8c7e66', part='Part2', archive='TRUNCATED zip (npc_state.csv cut; trajectory.csv, session_manifest.json, session_config.used.json missing)'), 'S2': dict(sid='20261106T142958Z_6b2d7675e9b8', part='Part2', archive='complete folder (ended by app_quit in p2_continue_pending)'), 'S3': dict(sid='20261106T145652Z_0980aa930c32', part='Part3', archive='TRUNCATED zip (npc_state.csv cut; trajectory.csv, manifest missing; audio + all other files present)')}
for k in ['S1', 'S2', 'S3']:
    r = R[k]; d = pd.read_csv(f'{OUT}/derived/synchronized_{k}.csv'); big = int(((d.eye_hmd_dy.abs() > 0.03) & (d.gaze_origin_source == 'provider')).sum())
    cmp_.append({'session': k, 'session_id': meta[k]['sid'], 'participant_id': r['participant'][0], 'part': meta[k]['part'], 'archive_status': meta[k]['archive'], 'platform': 'WindowsEditor / OpenXR display (manifest)' if k == 'S2' else 'not recorded (manifest missing); same Editor build assumed [hypothesis]',
                 'recording_duration_s': round(r['duration_s'], 1), 'gaze_rows': r['rows'], 'trajectory_rows': r.get('traj_rows', 'n/a (file missing)'), 'gaze_rate_hz': round(r['rate_hz'], 2), 'median_interval_s': round(r['median_interval'], 3), 'frame_index_step': r['frame_delta_median'],
                 'valid_gaze_pct (combined_valid)': 100.0, 'provider_valid_flag_available': 'yes (1 on 5209/5210)' if k == 'S2' else 'NO (blank; eye_tracking_state=start_failed)', 'mean_confidence': round(float(d.combined_conf.mean()), 3), 'confidence_unique_values': 1,
                 'mean_hmd_height_above_ground_m': round(r['hmd_height_above_ground']['mean'], 3), 'median_hmd_height_above_ground_m': round(r['hmd_height_above_ground']['median'], 3), 'std_hmd_height_m': round(r['hmd_height_above_ground']['std'], 3), 'min_max_hmd_height_m': '%.3f / %.3f' % (r['hmd_height_above_ground']['min'], r['hmd_height_above_ground']['max']),
                 'median_hmd_world_y_m_excl_runaway': round(float(d.hmd_world_y[~RUN[k]].median()) if k in RUN else float(d.hmd_world_y.median()), 2), 'rig_runaway_rows': int(RUN[k].sum()) if k in RUN else 0, 'rig_runaway_start_t_abs': round(float(d.t_abs[RUN[k]].iloc[0]), 2) if k in RUN else '', 'physical_head_y_rel_recentre_median_m': round(r['raw_head_local_y']['median'], 3), 'physical_head_y_rel_recentre_std_m': round(r['raw_head_local_y']['std'], 3),
                 'mean_eye_origin_height_above_ground_m': round(r['eye_h_ground']['mean'], 3), 'median_eye_origin_height_above_ground_m': round(r['eye_h_ground']['median'], 3), 'mean_eye_hmd_vertical_offset_m': round(r['eye_hmd_dy']['mean'], 4), 'median_eye_hmd_vertical_offset_m': round(r['eye_hmd_dy']['median'], 4), 'std_eye_hmd_vertical_offset_m': round(r['eye_hmd_dy']['std'], 4),
                 'rows_ground_unavailable': r['ground_unavailable'], 'rows_origin_replaced_by_eye_anchor': int(sum(v['n'] for kk, v in r['eye_dy_by_source'].items() if kk != 'provider')), 'rows_eye_hmd_offset_gt_3cm_provider': big,
                 'head_pitch_median_deg_up': round(-r['pitch_down']['median'], 1), 'gaze_minus_head_elevation_mean_deg': round(float(np.mean(np.degrees(np.arcsin(d.combined_dir_y)) + d.pitch_down_deg)), 1),
                 'suspected_posture_changes': {'S1': 0, 'S2': 0, 'S3': 2}[k], 'posture_note': {'S1': 'physical head height constant (0.061 +- 0.009 m rel. recentre)', 'S2': 'constant (0.035 +- 0.020 m); first 8 s at +0.127 m (settling)', 'S3': 'lowered -0.38 m for 96 % of the session; two rises to +0.10 m (1143.0-1157.5 s, 1714.6 s to end) = stand-up episodes [candidate]'}[k],
                 'tracking_anomaly_rows_logged': cnt.get(k, 0), 'frozen_pose_rows': r['frozen_pose_consecutive_rows']})
pd.DataFrame(cmp_).to_csv(OUT + '/session_comparison.csv', index=False); print(pd.DataFrame(cmp_).T.to_string()); print(an.groupby(['session', 'class']).size())
