"""Shared loader (read-only). Session dirs: S2 is the complete folder in Downloads; S1 / S3 are extracted (bsdtar, truncated archives) into the scratch dir."""
import os, numpy as np, pandas as pd
SCR = '/private/tmp/claude-501/-Users-qiaoy-Desktop-project-environment-LyonVRCity2022/fc47a207-d182-4410-a2e2-356aaa6b229c/scratchpad/posture'
SESS = {'S1': SCR + '/s1/20261106T140500Z_2a699c8c7e66', 'S2': os.path.expanduser('~/Downloads/20261106T142958Z_6b2d7675e9b8'), 'S3': SCR + '/s3/20261106T145652Z_0980aa930c32'}
def num(df, c): return pd.to_numeric(df[c].replace('', np.nan), errors='coerce')
def load(k):
    d = SESS[k]; g = pd.read_csv(d + '/gaze.csv', dtype=str, keep_default_na=False); dg = pd.read_csv(d + '/gaze_diagnostics.csv', dtype=str, keep_default_na=False)
    ev = pd.read_csv(d + '/events.csv', dtype=str, keep_default_na=False); tr = pd.read_csv(d + '/trajectory.csv') if os.path.exists(d + '/trajectory.csv') else None
    return g, dg, ev, tr
