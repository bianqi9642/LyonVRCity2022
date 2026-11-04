#!/usr/bin/env python3
"""
Part 3 think-aloud audio <-> multimodal alignment example (schema v15).

Usage:  python3 part3_audio_alignment_example.py <session_folder> <t0_abs> <t1_abs> [--save-wav out.wav]

What it does (standard library only; numpy is used if installed):
  1. loads audio/part3_audio.wav and audio/audio_index.csv and audio/audio_anchors.csv,
  2. rebuilds the sample_index <-> t_abs mapping: an affine least-squares fit per continuous SEGMENT of the (sample_index, t_abs) anchors,
  3. converts the half-open interval [t0_abs, t1_abs) into a sample range and extracts that audio,
  4. returns the gaze.csv, trajectory.csv, events.csv and npc_state.csv rows whose t_abs lies in the same interval.

Conventions (identical to the recorder): t_abs is CaptureClock.TAbs = Time.realtimeSinceStartupAsDouble, the key of every stream;
t_abs(sample_index) is the START of that WAV frame; intervals are half-open [t0, t1); sample_index counts WAV frames (lost frames are not counted);
a gap (interruption / overrun) is a jump of the TIME axis between two segments - the sample axis stays contiguous, no audio is invented for the gap.
Timing uncertainty: see audio/audio_metadata.json -> timing_uncertainty_ms (relative mapping) - the microphone's input latency before its position cursor is NOT included.
"""
import bisect, csv, json, os, sys, wave


def read_csv(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def fit_segments(anchors):
    """anchors: dicts with segment, sample_index, t_abs. Returns {segment: (t0, s0, seconds_per_sample)} using the same rule as the recorder (nominal rate with < 8 anchors)."""
    by = {}
    for a in anchors:
        by.setdefault(int(a["segment"]), []).append((int(a["sample_index"]), float(a["t_abs"]), int(a["sample_rate_hz"])))
    fits = {}
    for seg, pts in by.items():
        s0, t0, rate = pts[0]
        if len(pts) < 2:
            fits[seg] = (t0, s0, 1.0 / rate); continue
        xs = [p[0] - s0 for p in pts]; ys = [p[1] - t0 for p in pts]; n = len(pts)
        sx, sy = sum(xs), sum(ys); sxx = sum(x * x for x in xs); sxy = sum(x * y for x, y in zip(xs, ys))
        den = n * sxx - sx * sx
        slope = (n * sxy - sx * sy) / den if den > 0 and n >= 8 else 1.0 / rate
        icpt = (sy - slope * sx) / n if n >= 8 else sy / n - slope * (sx / n)
        fits[seg] = (t0 + icpt, s0, slope)
    return fits


class AudioClock:
    def __init__(self, session):
        d = os.path.join(session, "audio")
        self.wav_path = os.path.join(d, "part3_audio.wav")
        self.index = read_csv(os.path.join(d, "audio_index.csv"))
        self.anchors = read_csv(os.path.join(d, "audio_anchors.csv"))
        self.fits = fit_segments(self.anchors)
        # first sample of every segment, from the index (zero-length gap rows carry the next segment's first sample index)
        self.seg_first = {}
        for r in self.index:
            self.seg_first.setdefault(int(r["segment"]), int(r["sample_index_start"]))
        self.segs = sorted(self.seg_first)
        self.rate = int(self.anchors[0]["sample_rate_hz"])
        self.total = max(int(r["sample_index_end_exclusive"]) for r in self.index)

    def time_of(self, sample):
        seg = max((s for s in self.segs if self.seg_first[s] <= sample), default=self.segs[0])
        t0, s0, sps = self.fits[seg]
        return t0 + (sample - s0) * sps

    def sample_at(self, t):
        """Frame index whose start is the last not after t; clamps inside a gap to the end of the previous segment; None before the first sample."""
        result = None
        for i, seg in enumerate(self.segs):
            t0, s0, sps = self.fits[seg]
            start = t0 + (self.seg_first[seg] - s0) * sps
            if t < start: break
            nxt = self.seg_first[self.segs[i + 1]] if i + 1 < len(self.segs) else self.total
            result = min(self.seg_first[seg] + int((t - start) // sps), nxt)
        return result

    def sample_range(self, t0, t1):
        a, b = self.sample_at(t0), self.sample_at(t1)
        if a is None and b is None: return 0, 0
        a = 0 if a is None else a
        return a, (0 if b is None else b)

    def read_frames(self, a, b):
        with wave.open(self.wav_path, "rb") as w:
            w.setpos(a); raw = w.readframes(max(0, b - a)); return raw, w.getnchannels(), w.getframerate()


def rows_between(path, t0, t1, key="t_abs"):
    if not os.path.exists(path): return []
    rows = read_csv(path)
    return [r for r in rows if r.get(key, "") != "" and t0 <= float(r[key]) < t1]


def main():
    if len(sys.argv) < 4:
        print(__doc__); sys.exit(2)
    session, t0, t1 = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
    clk = AudioClock(session)
    a, b = clk.sample_range(t0, t1)
    raw, ch, rate = clk.read_frames(a, b)
    print(f"audio: frames [{a}, {b}) = {(b - a) / rate:.3f} s of {clk.total / rate:.3f} s; t_abs of those frames {clk.time_of(a):.4f} .. {clk.time_of(b):.4f}")
    if "--save-wav" in sys.argv:
        out = sys.argv[sys.argv.index("--save-wav") + 1]
        with wave.open(out, "wb") as w: w.setnchannels(ch); w.setsampwidth(2); w.setframerate(rate); w.writeframes(raw)
        print("saved", out)
    for name in ("gaze.csv", "trajectory.csv", "events.csv", "npc_state.csv"):
        rows = rows_between(os.path.join(session, name), t0, t1)
        print(f"{name}: {len(rows)} rows in [{t0}, {t1})")
    ev = rows_between(os.path.join(session, "events.csv"), t0, t1)
    for r in ev[:10]: print("  event", r["t_abs"], r["event_type"], r.get("label", ""))
    meta_path = os.path.join(session, "audio", "audio_metadata.json")
    if os.path.exists(meta_path):
        m = json.load(open(meta_path))
        print("status:", m.get("status"), "| gaps:", m.get("gaps"), "| timing uncertainty (ms, relative):", m.get("timing_uncertainty_ms"))


if __name__ == "__main__":
    main()
