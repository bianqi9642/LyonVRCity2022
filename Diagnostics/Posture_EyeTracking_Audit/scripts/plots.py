#!/usr/bin/env python3
import os, sys, warnings, json
warnings.filterwarnings('ignore')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from load import *
import matplotlib; matplotlib.use('Agg'); import matplotlib.pyplot as plt
OUT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')); NAMES = {'S1': 'S1 20261106T140500Z (Part 2)', 'S2': 'S2 20261106T142958Z (Part 2)', 'S3': 'S3 20261106T145652Z (Part 3)'}
CAM = 1.36144; COL = {'S1': 'C0', 'S2': 'C1', 'S3': 'C2'}
def marks(ax, d, ev, s):
    for _, r in ev[ev.event_type == 'phase_start'].iterrows():
        tv = float(r.t_abs)
        if d.t_abs.min() - 1 <= tv <= d.t_abs.max() + 1: ax.axvline(tv, color='k', ls='--', lw=0.8)
    ph = d.phase.values; t = d.t_abs.values; i = 0
    while i < len(ph):
        j = i
        while j + 1 < len(ph) and ph[j + 1] == ph[i]: j += 1
        if ph[i] in ('p2_continue_pending', 'd1_pending', 'd2_pending'): ax.axvspan(t[i], t[j], color='gold', alpha=0.25, lw=0)
        i = j + 1
    gu = d.hmd_height_above_ground.isna().values; i = 0
    while i < len(gu):
        if gu[i]:
            j = i
            while j + 1 < len(gu) and gu[j + 1]: j += 1
            ax.axvspan(t[i], t[j] + 0.05, color='red', alpha=0.15, lw=0); i = j + 1
        else: i += 1
    for tv in num(ev[ev.event_type.isin(['teleport', 'river_crossing'])], 't_abs'): 
        if t.min() <= tv <= t.max(): ax.axvline(tv, color='tab:purple', lw=0.4, alpha=0.5)
fig_all = plt.figure(figsize=(14, 4.5)); 
for k in ['S1', 'S2', 'S3']:
    g, dg, ev, tr = load(k); d = pd.read_csv(f'{OUT}/derived/synchronized_{k}.csv'); t = d.t_abs.values
    fig, axs = plt.subplots(8, 1, figsize=(14, 21), sharex=True)
    axs[0].plot(t, d.hmd_world_y, lw=0.7); axs[0].set_ylabel('1 HMD world Y (m)')
    axs[1].plot(t, d.hmd_height_above_ground, lw=0.7, color='C1'); axs[1].plot(t, CAM + d.raw_head_local_y, lw=0.7, color='k', alpha=0.7, label='1.36144 + physical head Y (OVRPlugin, ground-independent)'); axs[1].set_ylabel('2 HMD height above ground (m)'); axs[1].legend(fontsize=7, loc='upper right')
    axs[2].plot(t, d.left_eye_h, lw=0.5, label='left'); axs[2].plot(t, d.right_eye_h, lw=0.5, label='right'); axs[2].plot(t, d.eye_height_above_ground, lw=0.7, color='k', label='combined'); axs[2].set_ylabel('3 gaze-origin height above ground (m)'); axs[2].legend(fontsize=7, loc='upper right')
    axs[3].plot(t, d.hmd_world_y - d.ground_y, lw=0.8, label='HMD (rendered camera)'); axs[3].plot(t, d.eye_height_above_ground, lw=0.8, label='gaze origin (combined)'); axs[3].set_ylabel('4 HMD vs gaze-origin height (m)'); axs[3].legend(fontsize=7, loc='upper right')
    axs[4].plot(t, d.eye_hmd_dy * 100, lw=0.7, color='C3'); fb = d.gaze_origin_source != 'provider'; axs[4].plot(t[fb], d.eye_hmd_dy[fb] * 100, 'k.', ms=3, label='origin replaced by eye anchor (provider > 0.12 m gate)'); axs[4].set_ylabel('5 eye - HMD vertical offset (cm)'); axs[4].legend(fontsize=7, loc='upper right')
    axs[5].plot(t, -d.pitch_down_deg, lw=0.6, label='head pitch (+ = looking up)'); axs[5].plot(t, d.roll_deg, lw=0.6, label='head roll (+ = right side up)'); axs[5].set_ylabel('6 head pitch / roll (deg)'); axs[5].legend(fontsize=7, loc='upper right')
    axs[6].plot(t, d.combined_conf, lw=1); axs[6].plot(t, d.combined_valid * 0.5 + 0.2, lw=0.8, color='gray', label='combined_valid (0.7 = valid)'); axs[6].set_ylim(0, 1.1); axs[6].set_ylabel('7 confidence'); axs[6].legend(fontsize=7)
    axs[7].plot(t, d.raw_head_local_y, lw=0.8, color='k'); axs[7].axhline(0, color='gray', lw=0.5); axs[7].axhline(-0.3, color='r', ls=':', lw=0.8, label='-0.30 m (markedly lowered)'); axs[7].set_ylabel('physical head Y rel. recentre (m)'); axs[7].set_xlabel('t_abs (s)'); axs[7].legend(fontsize=7)
    for ax in axs: marks(ax, d, ev, k)
    fig.suptitle(NAMES[k] + ' - dashed = phase start, gold = Continue / destination panel, purple = teleport, red = no ground below camera (tracking-loss of the ground reference)', fontsize=10); fig.tight_layout(); fig.savefig(f'{OUT}/plots/{k}_timeseries.png', dpi=100); plt.close(fig)
    fig, axs = plt.subplots(1, 4, figsize=(18, 4.2)); axs[0].hist(d.hmd_height_above_ground.dropna(), bins=60, color=COL[k]); axs[0].set_title('8 HMD height above ground (m)'); axs[1].hist(d.eye_height_above_ground.dropna(), bins=60, color=COL[k]); axs[1].set_title('9 gaze-origin height above ground (m)')
    axs[2].scatter(d.hmd_height_above_ground, d.eye_height_above_ground, s=3, alpha=0.4, color=COL[k]); axs[2].plot([0.8, 1.7], [0.8, 1.7], 'k:', lw=0.8); axs[2].set_xlabel('HMD height (m)'); axs[2].set_ylabel('gaze-origin height (m)'); axs[2].set_title('10 gaze origin vs HMD height')
    axs[3].hist(d.eye_hmd_dy * 100, bins=80, color=COL[k]); axs[3].set_yscale('log'); axs[3].set_title('eye - HMD vertical offset (cm)')
    fig.suptitle(NAMES[k]); fig.tight_layout(); fig.savefig(f'{OUT}/plots/{k}_distributions.png', dpi=100); plt.close(fig)
fig, axs = plt.subplots(2, 3, figsize=(17, 8))
for k in ['S1', 'S2', 'S3']:
    d = pd.read_csv(f'{OUT}/derived/synchronized_{k}.csv')
    axs[0, 0].hist(d.hmd_height_above_ground.dropna(), bins=np.linspace(0.8, 1.7, 90), alpha=0.5, label=k, density=True); axs[0, 1].hist(d.eye_height_above_ground.dropna(), bins=np.linspace(0.8, 1.7, 90), alpha=0.5, label=k, density=True)
    axs[0, 2].hist(d.raw_head_local_y.dropna(), bins=np.linspace(-0.5, 0.2, 90), alpha=0.5, label=k, density=True); axs[1, 0].hist(d.eye_hmd_dy * 100, bins=np.linspace(-10, 12, 90), alpha=0.5, label=k, density=True)
    axs[1, 1].hist(d.pitch_down_deg * -1, bins=np.linspace(-45, 65, 90), alpha=0.5, label=k, density=True); axs[1, 2].hist((np.degrees(np.arcsin(d.combined_dir_y)) + d.pitch_down_deg), bins=np.linspace(-50, 20, 90), alpha=0.5, label=k, density=True)
for ax, ti in zip(axs.ravel(), ['HMD height above ground (m)', 'gaze-origin height above ground (m)', 'physical head Y relative to recentre point (m)', 'eye - HMD vertical offset (cm)', 'head pitch (deg, + = up)', 'gaze elevation minus head elevation (deg)']): ax.set_title(ti); ax.legend()
fig.suptitle('Three-session comparison (same axes)'); fig.tight_layout(); fig.savefig(f'{OUT}/plots/comparison_distributions.png', dpi=100)
print('ok')
