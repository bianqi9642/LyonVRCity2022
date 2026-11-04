#!/usr/bin/env python3
"""
Tests for gaze_reader.py and for the consistency of the schema-v17 definition (run:  python3 -m unittest test_gaze_reader   from this folder).

* Fixtures are built from the REAL column lists: they are parsed out of Assets/Scripts/GazeRecorder.cs (PrimaryColumns / DiagnosticColumns), so a header change in the writer without a matching change here fails.
* A historical v16 session is read READ-ONLY when present (skipped otherwise). No file is ever written next to a real session.
"""
import os
import re
import tempfile
import unittest

import gaze_reader as gr

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
AUDITED_V16 = "/Users/qiaoy/Downloads/20261104T141934Z_424f001ded51/gaze.csv"


def slurp(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def parse_columns(name):
    """The ordered column list of GazeRecorder.<name> (a Cat(...) expression), parsed from the C# source."""
    s = slurp(os.path.join(REPO, "Assets", "Scripts", "GazeRecorder.cs"))
    a = s.index(name + " = Cat(")
    b = s.index("\n\n", a)
    body = re.sub(r"//[^\n]*", "", s[a:b])
    out = []
    for m in re.finditer(r'Xyz\("([a-z_]+)"\)|Quat\("([a-z_]+)"\)|"([a-z_0-9]+)"', body):
        if m.group(1):
            out += [m.group(1) + "_" + c for c in "xyz"]
        elif m.group(2):
            out += [m.group(2) + "_" + c for c in "xyzw"]
        else:
            out.append(m.group(3))
    return out


PRIMARY = parse_columns("PrimaryColumns")
DIAG = parse_columns("DiagnosticColumns")
FALLBACK = ["fallback_ray_origin_x", "fallback_ray_origin_y", "fallback_ray_origin_z", "fallback_ray_dir_x", "fallback_ray_dir_y", "fallback_ray_dir_z"]


def write(path, header, rows):
    with open(path, "w", newline="") as f:
        f.write(",".join(header) + "\r\n")
        for r in rows:
            f.write(",".join(r) + "\r\n")


def row(header, **kw):
    r = [""] * len(header)
    for k, v in kw.items():
        r[header.index(k)] = v
    return r


class SchemaDefinitionTests(unittest.TestCase):
    def test_authoritative_counts_and_uniqueness(self):
        self.assertEqual(len(PRIMARY), 61); self.assertEqual(len(DIAG), 85)
        self.assertEqual(len(set(PRIMARY)), 61); self.assertEqual(len(set(DIAG)), 85)

    def test_the_six_fallback_ray_columns_are_the_last_six_diagnostics_columns(self):
        self.assertEqual(DIAG[-6:], FALLBACK)
        self.assertEqual(DIAG[78], "blink_state", "the first 79 diagnostics columns are the original v17 set; the six fallback columns were appended")
        for n in FALLBACK:
            self.assertNotIn(n, PRIMARY)

    def test_keys_exist_in_both_files(self):
        for k in gr.KEYS:
            self.assertIn(k, PRIMARY); self.assertIn(k, DIAG)

    def test_dictionary_document_lists_exactly_the_written_columns_in_order(self):
        doc = slurp(os.path.join(REPO, "docs", "gaze_schema_v17_dictionary.md"))
        sec1 = doc.index("## 1. gaze.csv"); sec2 = doc.index("## 2. gaze_diagnostics.csv"); sec3 = doc.index("## 3. Disposition")

        def names(text):
            return re.findall(r"^\| \d+ \| `([a-z_0-9]+)` \|", text, re.M)
        self.assertEqual(names(doc[sec1:sec2]), PRIMARY, "section 1 lists gaze.csv columns in file order")
        self.assertEqual(names(doc[sec2:sec3]), DIAG, "section 2 lists gaze_diagnostics.csv columns in file order")
        # every fallback field is explained, including why it is retained
        for n in FALLBACK:
            line = [l for l in doc[sec2:sec3].split("\n") if "| `%s` |" % n in l][0]
            self.assertIn("reconstructible", line)
        # section 3 accounts for every column of the 143-column v16 header, once
        v16 = re.findall(r"^\| `([a-z_0-9]+)` \| (?:kept in gaze.csv|moved to gaze_diagnostics.csv|removed \(derivable\)) \|", doc[sec3:], re.M)
        self.assertEqual(len(v16), 143); self.assertEqual(len(set(v16)), 143)

    def test_documents_state_the_same_counts_as_the_code(self):
        for f in ("docs/gaze_schema_v17_dictionary.md", "docs/schema_v17_changes.md", "docs/npc_data_schema.md"):
            t = slurp(os.path.join(REPO, f))
            self.assertNotRegex(t, r"\b79 columns|61 \+ 79|\(79\)", f + " still states the pre-fallback count")
        self.assertIn("85 columns", slurp(os.path.join(REPO, "docs", "schema_v17_changes.md")))
        src = slurp(os.path.join(REPO, "Assets", "Scripts", "Capture", "CaptureConstants.cs"))
        self.assertIn("SchemaVersion   = 17", src); self.assertIn("85 columns", src)


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.g = os.path.join(self.d, "gaze.csv")
        self.dg = os.path.join(self.d, "gaze_diagnostics.csv")

    # one eye-gaze row (combined ray valid, with a hit) and one head-fallback row (no valid eye)
    def primary_rows(self, session="S1"):
        r0 = row(PRIMARY, t_abs="1.0000", frame_index="10", session_id=session, combined_valid="1", gaze_ray_source="both", hit_collider_name="Wall", ray_did_hit="1",
                 combined_origin_x="1.0000", combined_origin_y="2.0000", combined_origin_z="3.0000", combined_dir_x="0.0000", combined_dir_y="0.0000", combined_dir_z="1.0000",
                 eyes_closed_left_recovered="0.2500", eyes_closed_right_recovered="0.0000", face_closure_valid="1",
                 hmd_world_x="0", hmd_world_y="1", hmd_world_z="0", hmd_world_rot_x="0", hmd_world_rot_y="0", hmd_world_rot_z="0", hmd_world_rot_w="1")
        r1 = row(PRIMARY, t_abs="1.0500", frame_index="11", session_id=session, combined_valid="0", gaze_ray_source="head_fallback", hit_collider_name="Floor", ray_did_hit="1",
                 face_closure_valid="0", hmd_world_x="5.0000", hmd_world_y="6.0000", hmd_world_z="7.0000", hmd_world_rot_x="0.0000", hmd_world_rot_y="0.7071", hmd_world_rot_z="0.0000", hmd_world_rot_w="0.7071")
        return [r0, r1]

    def diag_rows(self, session="S1", fallback=True, fb_blank=False):
        r0 = row(DIAG, t_abs="1.0000", frame_index="10", session_id=session, left_confidence="0.998", eyes_closed_left="0.2000", face_state="current", face_data_source="visual", face_provider_time_s="23800.100000")
        r1 = row(DIAG, t_abs="1.0500", frame_index="11", session_id=session, face_state="not_started")
        if fallback and not fb_blank:
            for k, v in zip(FALLBACK, ["-5.0000", "1.0000", "-5.0000", "0.0000", "0.0000", "1.0000"]):
                r1[DIAG.index(k)] = v
        return [r0, r1]

    def put(self, prim=None, diag=None, diag_header=None):
        write(self.g, PRIMARY, prim if prim is not None else self.primary_rows())
        if diag is not False:
            write(self.dg, diag_header or DIAG, diag if diag is not None else self.diag_rows())

    # ------------------------------------------------------------------------------------------------ happy paths
    def test_join_preserves_both_row_counts_and_restores_derivable_columns(self):
        self.put()
        t = gr.read_gaze(self.d)
        self.assertEqual((t.schema, t.merged, t.diagnostics_missing, t.warnings), ("v17", True, False, []))
        self.assertEqual(len(t.rows), 2, "main gaze row count preserved")
        for n in DIAG:
            self.assertTrue(t.has(n), "diagnostic column %s is available after loading" % n)
        r0, r1 = t.rows
        self.assertEqual(t.get(r0, "left_confidence"), "0.998"); self.assertEqual(t.get(r0, "eyes_closed_left"), "0.2000"); self.assertEqual(t.get(r0, "eyes_closed_left_recovered"), "0.2500")
        # valid combined gaze: the cast ray is the combined ray
        self.assertEqual([t.get(r0, "ray_origin_" + a) for a in "xyz"], ["1.0000", "2.0000", "3.0000"]); self.assertEqual([t.get(r0, "ray_dir_" + a) for a in "xyz"], ["0.0000", "0.0000", "1.0000"])
        self.assertEqual((t.get(r0, "ray_is_eye_gaze"), t.get(r0, "roi_hit_name")), ("1", "Wall"))
        # fallback row: the cast ray is the recorded fallback ray (NOT re-derived from the camera: hmd_world differs on purpose)
        self.assertEqual([t.get(r1, "ray_origin_" + a) for a in "xyz"], ["-5.0000", "1.0000", "-5.0000"]); self.assertEqual([t.get(r1, "ray_dir_" + a) for a in "xyz"], ["0.0000", "0.0000", "1.0000"])
        self.assertEqual((t.get(r1, "ray_is_eye_gaze"), t.get(r1, "roi_hit_name")), ("0", ""))
        # blank stays blank, 0 stays 0
        self.assertEqual(t.get(r1, "eyes_closed_left"), ""); self.assertEqual(t.get(r1, "eyes_closed_left_recovered"), ""); self.assertEqual(t.get(r0, "eyes_closed_right_recovered"), "0.0000")
        self.assertEqual(t.floats("eyes_closed_left_recovered"), [0.25, None])

    def test_columns_are_not_duplicated_by_the_join(self):
        self.put()
        t = gr.read_gaze(self.g)
        self.assertEqual(len(t.header), len(set(t.header)))
        self.assertEqual(len(t.header), 61 + (85 - 3) + 8, "61 primary + 82 diagnostics-only + 8 reconstructed")
        for r in t.rows:
            self.assertEqual(len(r), len(t.header))

    def test_fallback_without_recorded_ray_is_blank_and_reported_not_invented(self):
        self.put(diag=self.diag_rows(fb_blank=True))
        t = gr.read_gaze(self.d); r1 = t.rows[1]
        self.assertEqual([t.get(r1, "ray_origin_" + a) for a in "xyz"], ["", "", ""])
        self.assertTrue(any("cast ray was not recorded" in w for w in t.warnings), t.warnings)

    def test_diagnostics_without_fallback_columns_approximate_from_the_view_and_say_so(self):
        keep = [i for i, n in enumerate(DIAG) if n not in FALLBACK]
        header = [DIAG[i] for i in keep]
        self.put(diag=[[r[i] for i in keep] for r in self.diag_rows()], diag_header=header)
        t = gr.read_gaze(self.d); r1 = t.rows[1]
        self.assertEqual([t.get(r1, "ray_origin_" + a) for a in "xyz"], ["5.0000", "6.0000", "7.0000"])
        x, y, z = (float(t.get(r1, "ray_dir_" + a)) for a in "xyz")
        self.assertAlmostEqual(x, 1.0, 3); self.assertAlmostEqual(y, 0.0, 3); self.assertAlmostEqual(z, 0.0, 3)   # (0, .7071, 0, .7071) turns +Z into +X
        self.assertTrue(any("APPROXIMATED" in w for w in t.warnings), t.warnings)

    def test_missing_diagnostics_file_is_flagged_not_filled(self):
        self.put(diag=False)
        t = gr.read_gaze(self.g)
        self.assertTrue(t.diagnostics_missing); self.assertFalse(t.merged); self.assertFalse(t.has("eyes_closed_left")); self.assertFalse(t.has("ray_dir_x")); self.assertTrue(t.warnings)

    # ------------------------------------------------------------------------------------------------ alignment failures (strict)
    def test_row_count_mismatch_is_an_error_not_a_silent_truncation(self):
        self.put(diag=self.diag_rows()[:1])
        with self.assertRaisesRegex(gr.GazeAlignmentError, "gaze.csv has 2 rows, gaze_diagnostics.csv has 1"):
            gr.read_gaze(self.g)
        self.put(diag=self.diag_rows() + [row(DIAG, t_abs="1.1000", frame_index="12", session_id="S1")])
        with self.assertRaisesRegex(gr.GazeAlignmentError, "3"):
            gr.read_gaze(self.g)

    def test_mismatching_keys_are_errors(self):
        for bad in ({"frame_index": "99"}, {"t_abs": "9.9999"}, {"session_id": "OTHER"}):
            d = self.diag_rows(); d[1] = row(DIAG, **{**dict(t_abs="1.0500", frame_index="11", session_id="S1", face_state="x"), **bad})
            self.put(diag=d)
            with self.assertRaises(gr.GazeAlignmentError, msg=str(bad)):
                gr.read_gaze(self.g)

    def test_duplicate_join_keys_are_rejected_in_either_file_no_many_to_many_join(self):
        p = self.primary_rows(); p[1] = list(p[0])
        self.put(prim=p, diag=self.diag_rows())
        with self.assertRaisesRegex(gr.GazeAlignmentError, "gaze.csv has 1 duplicate join key"):
            gr.read_gaze(self.g)
        d = self.diag_rows(); d[1] = list(d[0])
        self.put(diag=d)
        with self.assertRaisesRegex(gr.GazeAlignmentError, "gaze_diagnostics.csv has 1 duplicate join key"):
            gr.read_gaze(self.g)

    def test_malformed_rows_and_missing_key_columns_are_errors(self):
        self.put(); open(self.dg, "a").write("1.1,1\r\n")
        with self.assertRaisesRegex(gr.GazeAlignmentError, "fields"):
            gr.read_gaze(self.g)
        write(self.dg, [c for c in DIAG if c != "frame_index"], [[r[i] for i, c in enumerate(DIAG) if c != "frame_index"] for r in self.diag_rows()])
        with self.assertRaisesRegex(gr.GazeAlignmentError, "frame_index"):
            gr.read_gaze(self.g)

    # ------------------------------------------------------------------------------------------------ non-strict, order
    def test_non_strict_left_join_keeps_every_gaze_row_and_reports_unmatched_rows(self):
        self.put(diag=self.diag_rows()[:1] + [row(DIAG, t_abs="7.0000", frame_index="70", session_id="S1")])
        t = gr.read_gaze(self.g, strict=False)
        self.assertEqual(len(t.rows), 2, "no gaze row is dropped")
        self.assertEqual(t.get(t.rows[1], "face_state"), "", "no diagnostics for the unmatched gaze row: blank, nothing inferred")
        self.assertTrue(any("1 gaze row(s) without a diagnostics row" in w and "1 diagnostics row(s) without a gaze row" in w for w in t.warnings), t.warnings)

    def test_same_keys_in_a_different_order_are_joined_by_key_with_a_warning(self):
        self.put(diag=list(reversed(self.diag_rows())))
        t = gr.read_gaze(self.g)
        self.assertEqual(t.get(t.rows[0], "face_state"), "current"); self.assertEqual(t.get(t.rows[1], "face_state"), "not_started")
        self.assertTrue(any("different order" in w for w in t.warnings))

    # ------------------------------------------------------------------------------------------------ history
    def test_historical_files_are_returned_exactly_as_written(self):
        header = ["time_since_start_s", "left_confidence", "ray_dir_x", "face_state"]
        write(self.g, header, [["0.1", "0.998", "0.5", "current"]])
        write(self.dg, DIAG, [["1"] * len(DIAG)])   # an unrelated file beside a v11-v16 file must not be joined
        t = gr.read_gaze(self.g)
        self.assertEqual(t.schema, "v11-v16"); self.assertFalse(t.merged); self.assertEqual(t.header, header); self.assertEqual(t.rows, [["0.1", "0.998", "0.5", "current"]]); self.assertEqual(t.warnings, [])

    @unittest.skipUnless(os.path.exists(AUDITED_V16), "audited v16 session not present")
    def test_real_v16_session_reads_unchanged(self):
        t = gr.read_gaze(AUDITED_V16)
        self.assertEqual(t.schema, "v11-v16"); self.assertEqual(len(t.header), 143); self.assertEqual(len(t.rows), 1266)
        self.assertTrue(all(len(r) == 143 for r in t.rows))
        self.assertEqual(t.header[0], "time_since_start_s"); self.assertTrue(t.has("eyes_closed_left") and t.has("ray_dir_x") and not t.has("face_closure_valid"))
        self.assertEqual(sum(1 for v in t.floats("eyes_closed_left") if v is None), 19, "the 19 blank (not_started) rows stay blank")
        self.assertEqual(t.warnings, [])


if __name__ == "__main__":
    unittest.main()
