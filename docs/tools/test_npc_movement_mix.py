#!/usr/bin/env python3
"""Tests for npc_movement_mix.py (run:  python3 -m unittest test_npc_movement_mix   from this folder). Fixtures are synthetic, written to a temp directory."""
import csv
import json
import os
import tempfile
import unittest

import npc_movement_mix as mix

A, W = mix.ATTRACTOR, mix.WANDERING


def write_session(d, lifetimes, entries=None, accessory_columns=True, profile_hash=None):
    """lifetimes: [(part, mode, profile, accessory_mesh, accessory_option)]"""
    header = ["session_id", "npc_spawn_instance_id", "part", "profile_name", "movement_mode"]
    if profile_hash is not None:
        header += ["profile_content_hash"]
    if accessory_columns:
        header += ["app_accessories_option", "app_accessories_mesh"]
    with open(os.path.join(d, "npc_lifetimes.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(header)
        for i, (part, mode, prof, mesh, opt) in enumerate(lifetimes):
            row = ["S", "L%d" % i, part, prof, mode]
            if profile_hash is not None:
                row += [profile_hash]
            if accessory_columns:
                row += [opt, mesh]
            w.writerow(row)
    with open(os.path.join(d, "session_manifest.json"), "w") as f:
        json.dump({"schema_version": 17, "setting_entries": entries or []}, f)


def write_profile(d, name, h, weights, none_index):
    """npc_profiles.csv with one profile whose accessory options have the given weights; the option at none_index has no mesh."""
    options = [{"index": i, "mesh": "" if i == none_index else "Bracelet", "weight": w, "genderRestriction": "Unisex", "materials": []} for i, w in enumerate(weights)]
    with open(os.path.join(d, "npc_profiles.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["session_id", "profile_content_hash", "profile_name", "first_seen_t_abs", "profile_json"])
        w.writerow(["S", h, name, "0", json.dumps({"name": name, "parts": {"accessories": options}})])


def entry(key, value):
    return {"key": key, "value": str(value), "source": "INSPECTOR"}


class MixReaderTests(unittest.TestCase):
    def test_probability_counts_percentages_and_profiles(self):
        with tempfile.TemporaryDirectory() as d:
            life = [("Part3", A, "ProfA", "Bracelet", "0"), ("Part3", A, "ProfA", "", "1"), ("Part3", W, "ProfB", "", "-3"), ("Part3", W, "ProfB", "", "-3")]
            write_session(d, life, [entry(mix.PROBABILITY_KEY, 0.4)])
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "per_spawn_probability")
            self.assertEqual(r["configured_attractor_probability"], 0.4)
            self.assertNotIn("movement_assignment_seed", r)
            self.assertNotIn("movement_assignment_legacy_override", r)
            self.assertEqual((r["part3_attractor_spawns"], r["part3_wandering_spawns"]), (2, 2))
            self.assertEqual((r["realised_attractor_pct"], r["realised_wandering_pct"]), (50.0, 50.0))
            self.assertEqual(r["profile_distribution"], {"ProfA": 2, "ProfB": 2})
            self.assertEqual(r["attractor_accessory_rate"], 0.5)
            self.assertEqual((r["wandering_accessory_count"], r["wandering_accessory_violations"]), (0, []))
            self.assertAlmostEqual(r["z_score_vs_configured"], (2 - 4 * 0.4) / (4 * 0.4 * 0.6) ** 0.5)

    def test_realised_share_is_not_required_to_equal_the_configured_probability(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "B", "0")] * 3 + [("Part3", W, "P", "", "-3")], [entry(mix.PROBABILITY_KEY, 0.5)])
            r = mix.summarize(d)
            self.assertEqual(r["realised_attractor_pct"], 75.0)   # reported, not asserted against 50 %

    def test_wandering_npc_with_accessory_is_reported_as_a_violation(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", W, "P", "Bracelet", "0"), ("Part3", W, "P", "", "-3")], [entry(mix.PROBABILITY_KEY, 0.0)])
            r = mix.summarize(d)
            self.assertEqual(r["wandering_accessory_count"], 1)
            self.assertEqual(r["wandering_accessory_violations"], ["L0"])

    def test_part2_wandering_npcs_with_accessories_are_not_violations(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "P", "Bracelet", "0"), ("Part3", A, "P", "Bracelet", "0")], [entry(mix.PROBABILITY_KEY, 1.0)])
            r = mix.summarize(d)
            self.assertEqual((r["part3_attractor_spawns"], r["part3_wandering_spawns"]), (1, 0))
            self.assertEqual(r["wandering_accessory_count"], 0)

    OBSOLETE = "spawner_part3_attractor_movement_proportion"   # only used to build historical-manifest fixtures; the reader never reads it

    def test_historical_manifest_with_only_the_obsolete_key_is_not_recorded(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [entry(self.OBSOLETE, 0.5)])
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "not_recorded")
            self.assertIsNone(r["configured_attractor_probability"])   # 0.5 is NOT reported as a 50 % per-spawn probability
            self.assertIsNone(r["z_score_vs_configured"])
            self.assertNotIn("legacy_active_population_proportion", r)
            self.assertFalse(hasattr(mix, "LEGACY_KEY"))

    def test_manifest_with_both_keys_uses_only_the_new_probability(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [entry(self.OBSOLETE, 0.9), entry(mix.PROBABILITY_KEY, 0.25)])
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "per_spawn_probability")
            self.assertEqual(r["configured_attractor_probability"], 0.25)

    def test_part2_only_session_has_no_part3_spawns(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "P", "Bracelet", "0"), ("Part2", W, "P", "", "1")], [])
            r = mix.summarize(d)
            self.assertEqual(r["part3_attractor_spawns"] + r["part3_wandering_spawns"], 0)
            self.assertIsNone(r["realised_attractor_pct"])
            self.assertEqual(r["wandering_accessory_violations"], [])

    def test_reader_does_not_modify_the_source_files(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [entry(mix.PROBABILITY_KEY, 0.5)])
            names = ["npc_lifetimes.csv", "session_manifest.json"]
            def snapshot():
                out = []
                for n in names:
                    with open(os.path.join(d, n), "rb") as f:
                        out.append(f.read())
                return out
            before = snapshot()
            mix.summarize(d)
            self.assertEqual(before, snapshot())

    def test_session_without_the_setting_is_not_recorded_never_zero_or_one(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [])
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "not_recorded")
            self.assertIsNone(r["configured_attractor_probability"])

    def test_older_lifetimes_without_accessory_columns_are_tolerated(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", None, None), ("Part3", W, "P", None, None)], [entry(mix.PROBABILITY_KEY, 0.5)], accessory_columns=False)
            r = mix.summarize(d)
            self.assertIsNone(r["attractor_accessory_rate"])
            self.assertIsNone(r["wandering_accessory_count"])

    def test_missing_files_are_tolerated(self):
        with tempfile.TemporaryDirectory() as d:
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "not_recorded")
            self.assertEqual(r["part3_attractor_spawns"] + r["part3_wandering_spawns"], 0)
            self.assertIsNone(r["realised_attractor_pct"])


PROD = [0.05] * 8 + [0.6]   # the production profile: 8 wristbands + None last


def rng_block(master, version=3):
    """The manifest rng block. Version 3 (current): the unsigned 64-bit seed as 16 lowercase hex digits + master_seed_representation. Versions 1 / 2 reproduce the UNSUPPORTED historical layouts
    (version 1: a signed 32-bit JSON number; version 2: hex without the version-3 contract) so the tests can check they are refused."""
    block = {"seed_source": "fixed", "seed_policy": "shared_fixed_seed", "rng_algorithm": "PCG32-XSH-RR", "rng_algorithm_version": 1, "rng_config_version": version}
    block["effective_master_seed"] = master if version == 1 else "%016x" % master
    if version >= 2:
        block["master_seed_representation"] = "uint64_hex16"
    return block


class TargetAssignmentReaderTests(unittest.TestCase):
    @staticmethod
    def manifest(d, pid, master, group, version=3, **override):
        import experiment_rng as xr
        start, z1, z2 = xr.GROUP_TABLE[group]
        c1, c2 = [z1 + "_A", z1 + "_B"], [z2 + "_A", z2 + "_B"]
        t1, _, s1 = xr.choose_target(master, 1, pid, group, z1, c1)
        t2, _, s2 = xr.choose_target(master, 2, pid, group, z2, c2)
        a = {"target_selection_version": 2, "part3_group_index": group, "part3_start_zone": start, "destination1_zone_id": z1, "destination2_zone_id": z2,
             "destination1_candidate_ids": c1, "destination2_candidate_ids": c2, "target1_building_id": t1, "target2_building_id": t2,
             "target1_selection_seed": s1, "target2_selection_seed": s2}
        a.update(override)
        with open(os.path.join(d, "session_manifest.json"), "w") as f:
            json.dump({"schema_version": 17, "participant_id": pid, "setting_entries": [], "assignments": a, "rng": rng_block(master, version)}, f)
        return a

    def test_every_group_of_a_current_manifest_audits_clean(self):
        for master in (31415, 0xFEDCBA9876543210):
            for g in range(12):
                with tempfile.TemporaryDirectory() as d:
                    self.manifest(d, "P%03d" % (g + 1), master, g)
                    r = mix.summarize(d)["target_assignment_verification"]
                    self.assertTrue(r and all(r.values()), (master, g, r))

    def test_a_wrong_target_zone_or_seed_is_flagged(self):
        with tempfile.TemporaryDirectory() as d:
            a = self.manifest(d, "P004", 31415, 4)
            self.manifest(d, "P004", 31415, 4, target2_building_id=a["target1_building_id"])
            r = mix.summarize(d)["target_assignment_verification"]
            self.assertFalse(r["target_ids_distinct"]); self.assertFalse(r["target2_in_destination2_zone"]); self.assertFalse(r["selections_match"])
            self.manifest(d, "P004", 31415, 4, target1_selection_seed="ff0834e9791e17a3")
            self.assertFalse(mix.summarize(d)["target_assignment_verification"]["seeds_match"])
            self.manifest(d, "P004", 31415, 4, destination2_zone_id="OE")
            self.assertFalse(mix.summarize(d)["target_assignment_verification"]["destination_zones_distinct"])

    def test_unsupported_selection_versions_and_manifests_are_not_audited(self):
        with tempfile.TemporaryDirectory() as d:
            self.manifest(d, "P004", 31415, 4, target_selection_version=1)
            self.assertIsNone(mix.summarize(d)["target_assignment_verification"])
            self.manifest(d, "P004", 31415, 4, version=1)       # historical numeric-seed manifest: not reconstructable, so not audited
            self.assertIsNone(mix.summarize(d)["target_assignment_verification"])

    def test_sessions_without_assignments_have_nothing_to_audit(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [entry(mix.PROBABILITY_KEY, 0.5)])
            self.assertIsNone(mix.summarize(d)["target_assignment_verification"])


class RngRecordTests(unittest.TestCase):
    """The experiment RNG block and the reconstruction of recorded movement modes."""

    @staticmethod
    def session(d, root, p, indices, directed=None, corrupt=(), version=3):
        import experiment_rng as xr
        header = ["session_id", "npc_spawn_instance_id", "part", "profile_name", "movement_mode", "rng_part", "rng_spawn_index"]
        with open(os.path.join(d, "npc_lifetimes.csv"), "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(header)
            for n, i in enumerate(indices):
                mode = xr.predict_movement_mode(root, "Part3", i, mix._f32(p))
                if n in corrupt:
                    mode = A if mode == W else W
                w.writerow(["S", "L%d" % n, "Part3", "P", mode, "Part3", i])
        entries = [entry(mix.PROBABILITY_KEY, p)]
        if directed is not None:
            entries.append(entry(mix.DIRECTED_KEY, "true" if directed else "false"))
        manifest = {"schema_version": 17, "setting_entries": entries,
                    "rng": rng_block(root, version)}
        with open(os.path.join(d, "session_manifest.json"), "w") as f:
            json.dump(manifest, f)

    def test_recorded_modes_are_reconstructed_from_the_effective_master_seed(self):
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 31415, 0.6, range(200))
            r = mix.summarize(d)
            self.assertTrue(r["rng_recorded"])
            self.assertEqual((r["rng_effective_master_seed"], r["rng_seed_source"], r["rng_algorithm_version"]), ("0000000000007ab7", "fixed", 1))
            self.assertEqual((r["rng_seed_representation"], r["rng_reconstructable"], r["rng_config_version"]), ("uint64_hex16", True, 3))
            self.assertEqual(r["movement_mode_reconstruction"], {"checked": 200, "mismatches": 0, "mismatch_ids": []})
            self.assertNotIn("movement_assignment_legacy_override", r)

    def test_a_64_bit_manifest_is_reconstructed_and_reported_as_hex(self):
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 0x8000000000000001, 0.6, range(200))
            r = mix.summarize(d)
            self.assertEqual((r["rng_effective_master_seed"], r["rng_seed_representation"], r["rng_reconstructable"]), ("8000000000000001", "uint64_hex16", True))
            self.assertEqual(r["movement_mode_reconstruction"], {"checked": 200, "mismatches": 0, "mismatch_ids": []})

    def test_the_master_seed_is_the_only_root_of_the_movement_assignment(self):
        # modes recorded from seed A do not reconstruct under seed B: there is no other seed to fall back on
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 0x0123456789ABCDEF, 0.5, range(300))
            with open(os.path.join(d, "session_manifest.json")) as f:
                m = json.load(f)
            m["rng"]["effective_master_seed"] = "8123456789abcdef"      # differs only in the top bit
            with open(os.path.join(d, "session_manifest.json"), "w") as f:
                json.dump(m, f)
            rec = mix.summarize(d)["movement_mode_reconstruction"]
            self.assertEqual(rec["checked"], 300)
            self.assertGreater(rec["mismatches"], 50, "the high bits participate in the derivation")

    def test_unsupported_historical_manifests_are_reported_not_reconstructed_and_never_converted(self):
        for version in (1, 2):
            with tempfile.TemporaryDirectory() as d:
                self.session(d, 31415, 0.5, range(50), version=version)
                if version == 2:       # version 2 without the version-3 contract
                    with open(os.path.join(d, "session_manifest.json")) as f:
                        m = json.load(f)
                    del m["rng"]["master_seed_representation"]
                    with open(os.path.join(d, "session_manifest.json"), "w") as f:
                        json.dump(m, f)
                r = mix.summarize(d)
                self.assertTrue(r["rng_recorded"])
                self.assertFalse(r["rng_reconstructable"], version)
                self.assertEqual(r["rng_config_version"], version)
                self.assertIn("unsupported RNG manifest format", r["rng_reconstruction_note"])
                self.assertIsNone(r["rng_effective_master_seed"], "an old seed is not reinterpreted as a current one")
                self.assertIsNone(r["movement_mode_reconstruction"])

    def test_an_unreadable_seed_representation_is_reported_not_reconstructable(self):
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 5, 0.5, range(10))
            with open(os.path.join(d, "session_manifest.json")) as f:
                m = json.load(f)
            m["rng"]["effective_master_seed"] = 12345          # a number where the current format has a hex string
            with open(os.path.join(d, "session_manifest.json"), "w") as f:
                json.dump(m, f)
            r = mix.summarize(d)
            self.assertFalse(r["rng_reconstructable"])
            self.assertIsNone(r["rng_effective_master_seed"])
            self.assertIsNone(r["movement_mode_reconstruction"])

    def test_a_tampered_row_is_reported_as_a_mismatch(self):
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 5, 0.5, range(50), corrupt=(3, 9))
            rec = mix.summarize(d)["movement_mode_reconstruction"]
            self.assertEqual((rec["checked"], rec["mismatches"], rec["mismatch_ids"]), (50, 2, ["L3", "L9"]))

    def test_directed_movement_off_has_nothing_to_reconstruct(self):
        with tempfile.TemporaryDirectory() as d:
            self.session(d, 5, 0.5, range(10), directed=False)
            self.assertIsNone(mix.summarize(d)["movement_mode_reconstruction"])

    def test_sessions_before_the_experiment_rng_are_not_reconstructed(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", A, "P", "", "1")], [entry(mix.PROBABILITY_KEY, 0.5)])
            r = mix.summarize(d)
            self.assertFalse(r["rng_recorded"])
            self.assertIsNone(r["rng_effective_master_seed"])
            self.assertFalse(r["rng_reconstructable"])
            self.assertIsNone(r["movement_mode_reconstruction"])


class AccessoryDistributionTests(unittest.TestCase):
    def test_effective_probability_follows_the_directed_switch(self):
        self.assertEqual(mix.effective_probability(0.5, True), 0.5)
        self.assertEqual(mix.effective_probability(0.5, None), 0.5)       # not recorded -> the configured value applies
        self.assertEqual(mix.effective_probability(0.5, False), 0.0)      # directed movement off: no attractor NPC can occur
        self.assertIsNone(mix.effective_probability(None, False))         # nothing recorded stays unknown
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part3", W, "P", "", "-3")], [entry(mix.PROBABILITY_KEY, 0.5), entry(mix.DIRECTED_KEY, "false")])
            r = mix.summarize(d)
            self.assertEqual((r["configured_attractor_probability"], r["part3_directed_movement"], r["effective_attractor_probability"]), (0.5, False, 0.0))

    def test_adjusted_weights_follow_the_documented_formula(self):
        adj = mix.adjusted_weights(PROD, 8, 0.5)
        self.assertEqual([round(x, 9) for x in adj], [0.025] * 8 + [0.8])
        self.assertAlmostEqual(sum(adj), sum(PROD))
        self.assertEqual(mix.adjusted_weights(PROD, 8, 1.0), PROD)
        self.assertEqual(mix.adjusted_weights(PROD, 8, 0.0), [0.0] * 8 + [1.0])
        total_not_one = [1, 2, 3, 4, 1, 1, 2, 1, 10]
        self.assertAlmostEqual(sum(mix.adjusted_weights(total_not_one, 8, 0.3)), 25.0)

    def test_expected_part2_equals_expected_part3_overall_for_every_element(self):
        for weights in (PROD, [0.1, 0.02, 0.03, 0.05, 0.05, 0.15, 0.01, 0.09, 0.5], [1, 2, 3, 4, 1, 1, 2, 1, 10], [0.125] * 8 + [0.0]):
            options = [{"index": i, "mesh": "" if i == 8 else "B", "weight": w, "genderRestriction": "Unisex"} for i, w in enumerate(weights)]
            for a in (0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0):
                e2, e3, adjusted, note = mix.expected_distributions(options, a)
                self.assertTrue(adjusted)
                self.assertAlmostEqual(sum(e2), 1.0); self.assertAlmostEqual(sum(e3), 1.0)
                for i in range(9):
                    self.assertAlmostEqual(e2[i], e3[i], places=9)

    def test_unexpected_profile_structure_is_flagged_and_unadjusted(self):
        two_none = [{"index": 0, "mesh": "B", "weight": 1, "genderRestriction": "Unisex"}, {"index": 1, "mesh": "", "weight": 1, "genderRestriction": "Unisex"},
                    {"index": 2, "mesh": "", "weight": 1, "genderRestriction": "Unisex"}]
        e2, e3, adjusted, note = mix.expected_distributions(two_none, 0.5)
        self.assertFalse(adjusted)
        self.assertIsNone(e3)
        self.assertAlmostEqual(e2[0], 1 / 3)

    def test_session_report_compares_part2_and_part3_per_profile_and_element(self):
        with tempfile.TemporaryDirectory() as d:
            life = []
            # Part 3: 2 attractors (element 0 and element 8) + 2 wanderers (-3); Part 2: element 8, element 8, element 0, element 3
            life += [("Part3", A, "Prod", "B", "0"), ("Part3", A, "Prod", "", "8"), ("Part3", W, "Prod", "", "-3"), ("Part3", W, "Prod", "", "-3")]
            life += [("Part2", W, "Prod", "", "8"), ("Part2", W, "Prod", "", "8"), ("Part2", W, "Prod", "B", "0"), ("Part2", W, "Prod", "B", "3")]
            write_session(d, life, [entry(mix.PROBABILITY_KEY, 0.5)], profile_hash="H1")
            write_profile(d, "Prod", "H1", PROD, 8)
            r = mix.summarize(d)
            self.assertEqual(r["part2_spawns"], 4)
            self.assertEqual(r["part2_profile_distribution"], {"Prod": 4})
            self.assertEqual(r["part2_accessory_distribution"]["8"]["count"], 2)
            self.assertEqual(r["part2_accessory_distribution"]["8"]["pct"], 50.0)
            self.assertEqual(r["part2_forced_suppression_markers"], 0)
            self.assertEqual(r["part3_wandering_forced_none_count"], 2)
            self.assertEqual({k: v["count"] for k, v in r["part3_attractor_accessory_distribution"].items()}, {"0": 1, "8": 1})
            rows = {t["element"]: t for t in r["accessory_comparison"]}
            self.assertEqual(len(rows), 9)
            self.assertAlmostEqual(rows[0]["expected_part2"], 0.025); self.assertAlmostEqual(rows[0]["expected_part3_overall"], 0.025)
            self.assertAlmostEqual(rows[8]["expected_part2"], 0.8); self.assertAlmostEqual(rows[8]["expected_part3_overall"], 0.8)
            self.assertEqual(rows[8]["observed_part2"], 0.5)
            self.assertEqual(rows[8]["observed_part3"], 0.75)       # attractor None (1) + forced -3 (2) of 4 Part 3 NPCs
            self.assertEqual(rows[0]["observed_part2"], 0.25); self.assertEqual(rows[0]["observed_part3"], 0.25)
            self.assertTrue(rows[8]["is_none"] and rows[8]["adjusted"])

    def test_part2_forced_suppression_marker_is_a_violation(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "P", "", "-3")], [entry(mix.PROBABILITY_KEY, 1.0)])
            self.assertEqual(mix.summarize(d)["part2_forced_suppression_markers"], 1)

    def test_historical_session_without_profiles_or_keys_is_tolerated(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "P", "B", "0"), ("Part3", A, "P", "B", "0")], [])
            r = mix.summarize(d)
            self.assertEqual(r["configured_semantics"], "not_recorded")
            self.assertIsNone(r["effective_attractor_probability"])
            self.assertEqual(r["accessory_comparison"], [])
            self.assertEqual(r["part2_spawns"], 1)

    def test_missing_probability_leaves_expectations_unknown(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "Prod", "", "8")], [], profile_hash="H1")
            write_profile(d, "Prod", "H1", PROD, 8)
            rows = mix.summarize(d)["accessory_comparison"]
            self.assertTrue(rows)
            self.assertTrue(all(t["expected_part2"] is None and t["expected_part3_overall"] is None for t in rows))
            self.assertEqual({t["element"]: t["observed_part2"] for t in rows}[8], 1.0)

    def test_directed_movement_off_makes_the_part2_expectation_all_none(self):
        with tempfile.TemporaryDirectory() as d:
            write_session(d, [("Part2", W, "Prod", "", "8")], [entry(mix.PROBABILITY_KEY, 1.0), entry(mix.DIRECTED_KEY, "false")], profile_hash="H1")
            write_profile(d, "Prod", "H1", PROD, 8)
            rows = {t["element"]: t for t in mix.summarize(d)["accessory_comparison"]}
            self.assertAlmostEqual(rows[8]["expected_part2"], 1.0)
            self.assertAlmostEqual(rows[0]["expected_part2"], 0.0)


if __name__ == "__main__":
    unittest.main()
