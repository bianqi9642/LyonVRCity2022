#!/usr/bin/env python3
"""
gaze_reader.py - offline reader for gaze.csv of every schema version (v11 ... v17).

WHY: schema v17 split the old 143-column gaze.csv into a 61-column analysis table (gaze.csv) and a QA stream (gaze_diagnostics.csv, one row per gaze row). Analysis code should not care.
`read_gaze(session_dir_or_gaze_csv)` returns ONE table with the v16 column names wherever the information still exists:

  * v11-v16 files are returned exactly as written (nothing is added, nothing is removed).
  * v17 files are joined with gaze_diagnostics.csv (when present) on the key (session_id, frame_index, t_abs) - one-to-one, duplicates / unmatched rows / row-count differences raise GazeAlignmentError
    (strict=False turns unmatched rows into explicit table.warnings with a left join) - and the columns v17 no longer stores because they are derivable are reconstructed:
        ray_origin_* / ray_dir_*   = combined_origin_* / combined_dir_* when combined_valid = 1, else fallback_ray_origin_* / fallback_ray_dir_* from the diagnostics (the cast head-forward ray);
                                   only if those are missing, the rendered camera (hmd_world_*, rotation applied to +Z)
        ray_is_eye_gaze            = combined_valid
        roi_hit_name               = hit_collider_name when combined_valid = 1, else ''
    The v16 eyes_closed_left/right (RAW SDK weights) exist only in gaze_diagnostics.csv; a v17 gaze.csv alone has eyes_closed_*_recovered. When the diagnostics file is missing the join is skipped and
    `table.diagnostics_missing` is True (never an error, never invented values).

Missing values stay '' (blank = not measured); 0 / 0.0000 is a value. Nothing is interpolated.

Usage:   python3 -I gaze_reader.py <session_dir | gaze.csv>      prints a one-line summary
         from gaze_reader import read_gaze
"""
import csv
import math
import os
import sys

DIAG_SUFFIX = "_diagnostics"


class GazeAlignmentError(ValueError):
    """gaze.csv and gaze_diagnostics.csv cannot be aligned reliably (row count, duplicate keys, mismatching keys, malformed rows)."""


class GazeTable:
    def __init__(self, header, rows, schema, diagnostics_missing=False, merged=False, warnings=None):
        self.header = header
        self.rows = rows            # list of lists of str, same width as header
        self.schema = schema        # 'v11-v16' | 'v17'
        self.diagnostics_missing = diagnostics_missing
        self.merged = merged
        self.warnings = list(warnings or [])   # every non-fatal limitation of the join / reconstruction; never silent
        self._idx = {n: i for i, n in enumerate(header)}

    def col(self, name):
        return self._idx[name]

    def has(self, name):
        return name in self._idx

    def get(self, row, name):
        return row[self._idx[name]]

    def floats(self, name):
        """Column as floats; blanks become None (never 0)."""
        i = self._idx[name]
        return [float(r[i]) if r[i] != "" else None for r in self.rows]

    def dicts(self):
        return [dict(zip(self.header, r)) for r in self.rows]


def _read(path):
    with open(path, newline="", encoding="utf-8") as f:
        r = csv.reader(f)
        header = next(r, [])
        rows = [row for row in r if row]
    for n, row in enumerate(rows, 2):
        if len(row) != len(header):
            raise GazeAlignmentError("%s line %d has %d fields, the header has %d" % (os.path.basename(path), n, len(row), len(header)))
    if len(set(header)) != len(header):
        raise GazeAlignmentError("%s has duplicate column names" % os.path.basename(path))
    return header, rows


def diagnostics_path(gaze_path):
    d, n = os.path.split(gaze_path)
    stem, ext = os.path.splitext(n)
    return os.path.join(d, stem + DIAG_SUFFIX + ext)


def _quat_forward(x, y, z, w):
    # q * (0,0,1)
    return (2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y))


KEYS = ("session_id", "frame_index", "t_abs")


def _keys(header, rows, name):
    for k in KEYS:
        if k not in header:
            raise GazeAlignmentError("%s has no %s column: the files cannot be joined" % (name, k))
    ix = [header.index(k) for k in KEYS]
    keys = [tuple(r[i] for i in ix) for r in rows]
    seen, dup = set(), []
    for k in keys:
        if k in seen and k not in dup:
            dup.append(k)
        seen.add(k)
    if dup:
        raise GazeAlignmentError("%s has %d duplicate join key(s) (session_id, frame_index, t_abs), e.g. %s: a one-to-one join is impossible" % (name, len(dup), dup[0]))
    return keys


def read_gaze(path, strict=True):
    """
    strict=True (default): any alignment problem raises GazeAlignmentError.
    strict=False: a LEFT join on the key; primary rows without a diagnostics row keep blank diagnostics fields, diagnostics rows without a primary row are dropped from the table - both are listed in
    table.warnings (never silently). Duplicate keys and malformed rows are always errors.
    """
    if os.path.isdir(path):
        path = os.path.join(path, "gaze.csv")
    header, rows = _read(path)
    if "face_closure_valid" not in header:       # a v11-v16 file: unchanged
        return GazeTable(header, rows, "v11-v16")
    dpath = diagnostics_path(path)
    pkeys = _keys(header, rows, "gaze.csv")
    if not os.path.exists(dpath):
        return GazeTable(header, rows, "v17", diagnostics_missing=True, warnings=["gaze_diagnostics.csv is missing: raw eyelid weights, face / provider diagnostics and the fallback ray are unavailable (not inferred)"])
    dh, drows = _read(dpath)
    dkeys = _keys(dh, drows, "gaze_diagnostics.csv")
    warnings = []
    dmap = dict(zip(dkeys, range(len(drows))))
    missing = [k for k in pkeys if k not in dmap]
    extra = [k for k in dkeys if k not in set(pkeys)]
    if missing or extra or len(drows) != len(rows):
        msg = "gaze.csv has %d rows, gaze_diagnostics.csv has %d; %d gaze row(s) without a diagnostics row (e.g. %s), %d diagnostics row(s) without a gaze row (e.g. %s)" % (
            len(rows), len(drows), len(missing), missing[0] if missing else "-", len(extra), extra[0] if extra else "-")
        if strict:
            raise GazeAlignmentError(msg)
        warnings.append(msg)
    if not (missing or extra) and pkeys != dkeys:
        warnings.append("the two files list the same keys in a different order; joined by key")
    extra_cols = [k for k, n in enumerate(dh) if n not in header]
    out_header = header + [dh[k] for k in extra_cols]
    blank = [""] * len(extra_cols)
    out_rows = []
    for r, key in zip(rows, pkeys):
        j = dmap.get(key)
        out_rows.append(r + ([drows[j][k] for k in extra_cols] if j is not None else list(blank)))
    ix = {n: i for i, n in enumerate(out_header)}
    derived = ["ray_origin_x", "ray_origin_y", "ray_origin_z", "ray_dir_x", "ray_dir_y", "ray_dir_z", "ray_is_eye_gaze", "roi_hit_name"]
    out_header = out_header + derived
    fo = ix.get("fallback_ray_origin_x")
    approx = unrecorded = 0
    for row in out_rows:
        eye = row[ix["combined_valid"]] == "1"
        if eye:
            row += [row[ix["combined_origin_" + a]] for a in "xyz"] + [row[ix["combined_dir_" + a]] for a in "xyz"]
        elif fo is not None:
            if row[fo] != "":   # the cast ray of a head-fallback row is recorded in the diagnostics
                row += [row[fo + k] for k in range(6)]
            else:               # the diagnostics exist but hold no fallback ray for this row (e.g. its diagnostics row is missing): do not invent one
                row += [""] * 6; unrecorded += 1
        else:                   # the diagnostics carry no fallback_ray_* columns at all: approximate from the rendered view, and say so
            pos = [row[ix["hmd_world_" + a]] for a in "xyz"]
            if all(p != "" for p in pos) and all(row[ix["hmd_world_rot_" + a]] != "" for a in "xyzw"):
                fwd = _quat_forward(*[float(row[ix["hmd_world_rot_" + a]]) for a in "xyzw"])
                row += pos + ["%.4f" % v for v in fwd]; approx += 1
            else:
                row += [""] * 6; unrecorded += 1
        row += ["1" if eye else "0", row[ix["hit_collider_name"]] if eye else ""]
    if approx:
        warnings.append("%d head-fallback row(s): ray_origin_* / ray_dir_* are APPROXIMATED from hmd_world_* (the diagnostics have no fallback_ray_* columns)" % approx)
    if unrecorded:
        warnings.append("%d head-fallback row(s): the cast ray was not recorded, ray_origin_* / ray_dir_* are blank" % unrecorded)
    return GazeTable(out_header, out_rows, "v17", merged=True, warnings=warnings)


def summarize(t):
    n = len(t.rows)
    tabs = [v for v in t.floats("t_abs") if v is not None]
    dur = (tabs[-1] - tabs[0]) if len(tabs) > 1 else 0.0
    return "schema=%s rows=%d columns=%d merged=%s diagnostics_missing=%s duration_s=%.3f rate_hz=%.3f" % (
        t.schema, n, len(t.header), t.merged, t.diagnostics_missing, dur, (n - 1) / dur if dur > 0 else float("nan"))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    t = read_gaze(sys.argv[1])
    print(summarize(t))
    for w in t.warnings:
        print("WARNING:", w)
