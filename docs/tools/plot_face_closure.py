#!/usr/bin/env python3
"""
Plot the controlled-closure trace written by FaceTrackingDiagnostic (face_diag_*.csv) against CaptureClock.TAbs.

Usage:  python3 plot_face_closure.py <face_diag.csv> [--out trace.png]

Panels:  (1) left / right eyelid closure, API (direct OVRPlugin.GetFaceState2) and wrapper (OVRFaceExpressions), recovered values;
         (2) validity layers as 0/1 steps (session started, GetFaceState2 ok, Status.IsValid, wrapper valid, eye-following);
         (3) the protocol segment as shaded spans.
Blank cells are NOT plotted as zero: a gap in a line means "not measured".  No threshold is drawn or applied: the plot is evidence, the
closure protocol (open 5 s / left 3 s / open 2 s / right 3 s / open 2 s / both 3 s / open 5 s) is the reference, read by eye.
Needs matplotlib (stdlib csv otherwise).
"""
import csv
import sys


def num(cell):
    return float(cell) if cell not in ("", None) else float("nan")


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    path = argv[1]
    out = argv[argv.index("--out") + 1] if "--out" in argv else path.rsplit(".", 1)[0] + "_trace.png"
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        print("no rows")
        return 1
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    t0 = num(rows[0]["t_abs"])
    t = [num(r["t_abs"]) - t0 for r in rows]
    fig, ax = plt.subplots(3, 1, figsize=(11, 8), sharex=True, gridspec_kw={"height_ratios": [3, 2, 1]})
    series = [("api_closed_l", "API left", "tab:blue", "-"), ("api_closed_r", "API right", "tab:red", "-"),
              ("recovered_l", "recovered left", "tab:blue", "--"), ("recovered_r", "recovered right", "tab:red", "--")]
    for col, label, colour, style in series:
        ax[0].plot(t, [num(r[col]) for r in rows], style, color=colour, label=label, marker=".", ms=3)
    ax[0].set_ylabel("eyelid closure (0..1)")
    ax[0].legend(loc="upper right", ncol=2)
    ax[0].set_title("EyesClosedL / EyesClosedR vs CaptureClock.TAbs (t = 0 at the first row)")
    for col in ("session_started", "get_face_state_ok", "status_valid", "wrapper_valid", "eye_following_valid"):
        ax[1].step(t, [num(r[col]) + 0.0 for r in rows], where="post", label=col)
    ax[1].set_ylabel("layer (1 = holds, gap = not queried)")
    ax[1].legend(loc="upper right", ncol=3, fontsize=8)
    spans, start, label = [], t[0], rows[0]["segment"]
    for i, r in enumerate(rows):
        if r["segment"] != label:
            spans.append((start, t[i], label))
            start, label = t[i], r["segment"]
    spans.append((start, t[-1], label))
    palette = {"open_both": "#dddddd", "left_closed": "#9ecae1", "right_closed": "#fcae91", "both_closed": "#b2df8a"}
    for a, b, lab in spans:
        for axis in ax:
            axis.axvspan(a, b, color=palette.get(lab, "#ffffff"), alpha=0.35, lw=0)
        ax[2].text((a + b) / 2, 0.5, lab, ha="center", va="center", fontsize=8)
    ax[2].set_yticks([])
    ax[2].set_xlabel("seconds since first diagnostic row (CaptureClock.TAbs)")
    fig.tight_layout()
    fig.savefig(out, dpi=130)
    print("wrote", out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
