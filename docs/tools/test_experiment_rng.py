#!/usr/bin/env python3
"""Tests for experiment_rng.py (the Python reference of Assets/Scripts/ExperimentRng.cs). Run:  python3 -m unittest test_experiment_rng   from this folder.
The expected values are the same frozen vectors the Unity tests (ExperimentRngTests) assert for the C# implementation."""
import unittest

import experiment_rng as x


class RngVectors(unittest.TestCase):
    def test_pcg32_reference_vector(self):
        g = x.Pcg32(42, 54)
        self.assertEqual([g.next_uint() for _ in range(6)], [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e])

    def test_splitmix64_and_stream_vectors(self):
        self.assertEqual(x.mix(0), 0xe220a8397b1dcdaf)
        self.assertEqual(x.stream_seed(12345, "Part3", "MovementAssignment", 7), 0x3416d3f507fd82e3)
        g = x.Pcg32.for_stream(12345, "Part3", "MovementAssignment", 7)
        self.assertEqual([g.next_uint() for _ in range(4)], [0xb7e72b1d, 0x5df2b1fb, 0x286ca041, 0x20cbe59b])
        low = (1 << 32) - 987654   # a seed whose upper 32 bits are zero (an ordinary unsigned 64-bit value)
        self.assertEqual(x.stream_seed(low, "Part2", "PartSetup", -1), 0xebca185bde3bc825)
        g = x.Pcg32.for_stream(low, "Part2", "PartSetup", -1)
        self.assertEqual([g.next_uint() for _ in range(4)], [0x6f6fa57b, 0x47e9819d, 0xdb773bc4, 0x8af435a2])

    def test_every_input_changes_the_stream(self):
        seeds = {x.stream_seed(r, p, s, i) for r in (1, 2, 0xFFFFFFFF) for p in x.PARTS for s in x.STREAMS for i in (-1, 0, 1, 2, 1000)}
        self.assertEqual(len(seeds), 3 * 4 * len(x.STREAMS) * 5)

    def test_zero_first_value_seed_used_by_the_weighted_selection_test(self):
        self.assertEqual(x.Pcg32(7926344, 54).next_uint(), 0x89)   # next_float() == 0 exactly

    def test_movement_assignment_is_nested_across_probabilities_and_exact_at_the_ends(self):
        for i in range(2000):
            a25, a50, a75 = (x.predict_movement_mode(99, "Part3", i, p) for p in (0.25, 0.5, 0.75))
            if a25 == "Part3AttractorItinerary":
                self.assertEqual(a50, "Part3AttractorItinerary")
            if a50 == "Part3AttractorItinerary":
                self.assertEqual(a75, "Part3AttractorItinerary")
            self.assertEqual(x.predict_movement_mode(99, "Part3", i, 1.0), "Part3AttractorItinerary")
            self.assertEqual(x.predict_movement_mode(99, "Part3", i, 0.0), "Part2WanderingItinerary")


class TargetSelectionVectors(unittest.TestCase):
    """Frozen vectors of the per-destination target selection (C# ExperimentRngTests.TargetSelection_* asserts the same values)."""
    def test_hash_and_selection_seed_vectors(self):
        self.assertEqual(x.hash_utf8("P001"), 0x5ffc4b9c13046dde)
        P3, T1, T2 = "Part3", "Target1Building", "Target2Building"
        self.assertEqual(x.target_selection_seed(31415, P3, T1, "P001", 4, "OE"), 0x35d409ef71e2213f)
        self.assertEqual(x.target_selection_seed(31415, P3, T2, "P001", 4, "GU"), 0x59a822d807652adc)
        self.assertEqual(x.target_selection_seed(31415, P3, T1, "P002", 4, "OE"), 0x5d7492f2f69f46ec)
        self.assertEqual(x.target_selection_seed(31415, P3, T2, "P002", 4, "GU"), 0xce47a73862d480c2)
        self.assertEqual(x.target_selection_seed(0xFEDCBA9876543210, P3, T1, "P001", 4, "OE"), 0xfd3479745b31e27d)
        self.assertEqual(x.target_selection_seed(0xFEDCBA9876543210, P3, T2, "P001", 4, "GU"), 0x86781eb11b77f237)

    def test_selection_vectors_and_order_independence(self):
        self.assertEqual(x.choose_target(31415, 1, "P001", 4, "OE", ["OE_B", "OE_A"])[0], "OE_B")
        self.assertEqual(x.choose_target(31415, 2, "P001", 4, "GU", ["GU_B", "GU_A"])[0], "GU_B")
        self.assertEqual(x.choose_target(31415, 2, "P002", 4, "GU", ["GU_A", "GU_B"])[0], "GU_A")
        self.assertEqual(x.choose_target(31415, 2, "P002", 4, "GU", ["GU_B", "GU_A"])[0], "GU_A")
        self.assertEqual(x.choose_target(0xFEDCBA9876543210, 2, "P001", 2, "GE", ["GE_B", "GE_A"])[0], "GE_B")

    def test_the_two_destinations_use_independent_streams(self):
        for seed in (1, 31415, 0x8000000000000001):
            for pid in ("P001", "P002", "P777"):
                a = x.target_selection_seed(seed, "Part3", "Target1Building", pid, 4, "OE")
                b = x.target_selection_seed(seed, "Part3", "Target2Building", pid, 4, "OE")
                self.assertNotEqual(a, b, "same participant, group and zone, different destination stream: different seeds")
        # the stream id is part of the derivation: swapping the zones between destinations does not swap the seeds
        self.assertNotEqual(x.target_selection_seed(5, "Part3", "Target1Building", "P001", 1, "GU"), x.target_selection_seed(5, "Part3", "Target2Building", "P001", 1, "GU"))
        # no NPC stream can collide with a selection stream
        npc = {x.stream_seed(5, "Part3", st, i) for st in x.STREAMS for i in range(50)}
        self.assertNotIn(x.target_selection_seed(5, "Part3", "Target1Building", "P001", 1, "GU"), npc)

    def test_64_bit_seeds_survive_the_hex_representation_exactly(self):
        for v in (0xff0834e9791e17a2, 0xffffffffffffffff, 0x8000000000000001, 1):
            self.assertEqual(int(x.seed_hex(v), 16), v)
            self.assertEqual(len(x.seed_hex(v)), 16)
        self.assertNotEqual(int(float(0xff0834e9791e17a2)), 0xff0834e9791e17a2)   # a float would have lost low bits: seeds are never stored as numbers

    def test_participants_groups_zones_streams_and_seeds_are_separately_derived(self):
        seeds = {x.target_selection_seed(m, "Part3", st, p, g, z) for m in (1, 2) for st in ("Target1Building", "Target2Building") for p in ("P001", "P002", "p001") for g in (0, 5) for z in ("GE", "OU")}
        self.assertEqual(len(seeds), 2 * 2 * 3 * 2 * 2)

    def test_each_selection_is_uniform_over_the_two_landmarks(self):
        for dest, stream_zone in ((1, "OE"), (2, "GU")):
            n = sum(1 for p in range(2000) if x.choose_target(777, dest, "P%04d" % p, 4, stream_zone, ["A", "B"])[0] == "A")
            self.assertAlmostEqual(n, 1000, delta=5 * (2000 * 0.25) ** 0.5)

    def test_the_old_shared_domain_derivation_no_longer_exists(self):
        self.assertFalse(hasattr(x, "target_building_seed"))
        self.assertFalse(hasattr(x, "choose_target_building"))
        self.assertNotIn("TargetBuilding", x.STREAMS)


class Table5(unittest.TestCase):
    EXPECTED = [("GE", "GU", "OE"), ("GE", "OE", "GU"), ("GE", "OU", "GE"), ("GU", "GE", "OU"), ("GU", "OE", "GU"), ("GU", "OU", "GE"),
                ("OE", "GE", "OU"), ("OE", "GU", "OE"), ("OE", "OU", "GE"), ("OU", "GE", "OU"), ("OU", "GU", "OE"), ("OU", "OE", "GU")]

    def test_the_group_table_is_exactly_table_5(self):
        self.assertEqual(x.GROUP_TABLE, self.EXPECTED)

    def test_destination_zones_differ_for_all_twelve_groups(self):
        for g, (start, d1, d2) in enumerate(x.GROUP_TABLE):
            self.assertNotEqual(d1, d2, "group %d" % g)
        self.assertEqual({(d1, d2) for _, d1, d2 in x.GROUP_TABLE} & {(z, z) for z in ("GE", "GU", "OE", "OU")}, set())
        for g in (2, 4, 7, 9):       # the groups whose START zone equals a destination zone are NOT same-destination groups
            self.assertNotEqual(x.GROUP_TABLE[g][1], x.GROUP_TABLE[g][2])
        self.assertEqual([x.GROUP_TABLE[g][1:] for g in (2, 4, 7, 9)], [("OU", "GE"), ("OE", "GU"), ("GU", "OE"), ("GE", "OU")])


class VerifyTargetAssignment(unittest.TestCase):
    @staticmethod
    def block(master, pid, group, ids=None):
        start, z1, z2 = x.GROUP_TABLE[group]
        c1, c2 = ids or ([z1 + "_A", z1 + "_B"], [z2 + "_A", z2 + "_B"])
        t1, _, s1 = x.choose_target(master, 1, pid, group, z1, c1)
        t2, _, s2 = x.choose_target(master, 2, pid, group, z2, c2)
        return {"target_selection_version": 2, "part3_group_index": group, "part3_start_zone": start, "destination1_zone_id": z1, "destination2_zone_id": z2,
                "destination1_candidate_ids": sorted(c1), "destination2_candidate_ids": sorted(c2), "target1_building_id": t1, "target2_building_id": t2,
                "target1_selection_seed": s1, "target2_selection_seed": s2}

    def test_every_group_audits_clean(self):
        for master in (31415, 0xFEDCBA9876543210):
            for g in range(12):
                for pid in ("P001", "P002", "P003"):
                    r = x.verify_target_assignment(master, pid, self.block(master, pid, g))
                    self.assertTrue(all(r.values()), (master, g, pid, r))

    def test_every_violation_is_detected(self):
        b = self.block(31415, "P001", 4)
        for key, bad in (("target1_building_id", b["target2_building_id"]), ("target2_building_id", b["target1_building_id"]), ("destination2_zone_id", b["destination1_zone_id"]),
                         ("part3_start_zone", "OU"), ("target1_selection_seed", "0" * 16)):
            r = x.verify_target_assignment(31415, "P001", dict(b, **{key: bad}))
            self.assertFalse(all(r.values()), key)
        # the same pair of ids / zones: both targets 'distinct' and 'in zone' flags fail independently
        same = dict(b, target2_building_id=b["target1_building_id"])
        self.assertFalse(x.verify_target_assignment(31415, "P001", same)["target_ids_distinct"])
        self.assertFalse(x.verify_target_assignment(31415, "P001", dict(b, destination2_zone_id=b["destination1_zone_id"]))["destination_zones_distinct"])
        self.assertFalse(x.verify_target_assignment(31415, "P001", dict(b, destination1_candidate_ids=["OE_A"]))["target1_in_destination1_zone"], "exactly two candidates are required")

    def test_unsupported_or_missing_blocks_are_not_audited(self):
        b = self.block(31415, "P001", 4)
        self.assertIsNone(x.verify_target_assignment(31415, "P001", dict(b, target_selection_version=1)))
        self.assertIsNone(x.verify_target_assignment(31415, "P001", {k: v for k, v in b.items() if k != "target_selection_version"}))
        self.assertIsNone(x.verify_target_assignment(31415, "", b))
        self.assertIsNone(x.verify_target_assignment(31415, "P001", None))
        self.assertIsNone(x.verify_target_assignment(31415, "P001", dict(b, part3_group_index=12)))


class Full64BitSeeds(unittest.TestCase):
    """Frozen vectors for seeds whose UPPER 32 bits matter; ExperimentRngTests (C#) asserts the very same values."""
    def test_stream_seed_and_pcg_vectors(self):
        for seed, ss, vals in (
            (0x0123456789ABCDEF, 0xf2bf7e15eba8a3ae, [0xf1e263c4, 0xac8a94eb, 0x3f69c154, 0xe2182729]),
            (0x8000000000000000, 0xd12b2071557656fd, [0xa5d60f57, 0x3ab404ac, 0xbdeaa517, 0xbca9ad63]),
            (0xFFFFFFFFFFFFFFFF, 0xb3ecaf7719f9038f, [0xd8b86df4, 0x0619fc42, 0x7e08b2ee, 0x632dc6a5]),
            (0x0000000100000000, 0x3a6aff65bc3ade19, [0x6335d753, 0xbf9e17bc, 0x164906bf, 0xb5a280e1]),
        ):
            self.assertEqual(x.stream_seed(seed, "Part3", "Appearance", 5), ss)
            g = x.Pcg32.for_stream(seed, "Part3", "Appearance", 5)
            self.assertEqual([g.next_uint() for _ in range(4)], vals)

    def test_seeds_differing_only_in_the_upper_32_bits_derive_different_streams(self):
        low = 0x00000000DEADBEEF
        seeds = {x.stream_seed(low | (hi << 32), "Part2", "Wandering", 3) for hi in (0, 1, 2, 0x80000000, 0xFFFFFFFF)}
        self.assertEqual(len(seeds), 5)
        self.assertEqual(len({x.target_selection_seed(low | (hi << 32), "Part3", "Target1Building", "P001", 4, "OE") for hi in (0, 1, 0xFFFFFFFF)}), 3)

    def test_target_selection_vector_with_upper_bits(self):
        self.assertEqual(x.target_selection_seed(0xFEDCBA9876543210, "Part3", "Target1Building", "P001", 4, "OE"), 0xfd3479745b31e27d)
        self.assertEqual(x.choose_target(0xFEDCBA9876543210, 1, "P001", 4, "OE", ["OE_B", "OE_A"])[0], "OE_B")

    def test_no_legacy_32bit_helpers_remain(self):
        self.assertFalse(hasattr(x, "legacy_int32_to_u64"))
        with open(x.__file__, encoding="utf-8") as f:
            src = f.read()
        for forbidden in ("legacy_int32", "zero-extend", "0xFFFFFFFF)"):
            self.assertNotIn(forbidden, src)

    def test_a_32bit_value_is_just_a_small_unsigned_seed_not_a_converted_one(self):
        # 0x00000000ffffffff is an ordinary 64-bit seed; a negative number is not a seed at all
        self.assertNotEqual(x.stream_seed(0xFFFFFFFF, "Part1", "Destination", 0), x.stream_seed(0xFFFFFFFFFFFFFFFF, "Part1", "Destination", 0))
        with self.assertRaises(ValueError):
            x.stream_seed(-1, "Part1", "Destination", 0)

    def test_hex_parsing_is_strict_and_exact(self):
        self.assertEqual(x.parse_seed_hex("0123456789abcdef"), 0x0123456789ABCDEF)
        self.assertEqual(x.parse_seed_hex("0123456789ABCDEF"), 0x0123456789ABCDEF)
        self.assertEqual(x.parse_seed_hex("ffffffffffffffff"), (1 << 64) - 1)
        self.assertEqual(x.parse_seed_hex("0000000000000000"), 0)
        for bad in ("", "0", "123456789abcdef", "0123456789abcdef0", " 0123456789abcdef", "0123456789abcdef ", "0x23456789abcdef", "0123456789abcdeg", "-123456789abcdef", "0123456789abcde\n"):
            with self.assertRaises(ValueError, msg=repr(bad)):
                x.parse_seed_hex(bad)
        for bad in (123, 1.5, None, True, b"0123456789abcdef"):
            with self.assertRaises(TypeError):
                x.parse_seed_hex(bad)

    def test_seeds_must_be_exact_unsigned_ints(self):
        for bad in (-1, 1 << 64):
            with self.assertRaises(ValueError):
                x.stream_seed(bad, "Part1", "Destination", 0)
        for bad in (1.0, "5", None, True):
            with self.assertRaises(TypeError):
                x.stream_seed(bad, "Part1", "Destination", 0)

    def test_little_endian_entropy_bytes(self):
        self.assertEqual(x.bytes_to_seed_le(bytes([1, 2, 3, 4, 5, 6, 7, 8])), 0x0807060504030201)
        self.assertEqual(x.bytes_to_seed_le(bytes([0xFF] * 8)), (1 << 64) - 1)
        self.assertEqual(x.bytes_to_seed_le(bytes([0, 0, 0, 0, 1, 0, 0, 0])), 0x0000000100000000)

    def test_current_manifest_format_is_read_with_full_precision(self):
        for seed in (0x8000000000000001, 0xFFFFFFFFFFFFFFFF, 0x0123456789ABCDEF, 1):
            r = x.manifest_master_seed({"rng_config_version": 3, "master_seed_representation": "uint64_hex16", "effective_master_seed": x.seed_hex(seed)})
            self.assertEqual((r["seed"], r["representation"], r["reconstructable"], r["rng_config_version"]), (seed, "uint64_hex16", True, 3))
        r = x.manifest_master_seed({"rng_config_version": 3, "master_seed_representation": "uint64_hex16", "effective_master_seed": "FEDCBA9876543210"})
        self.assertEqual(r["seed"], 0xFEDCBA9876543210)

    def test_unsupported_historical_manifest_versions_are_not_reconstructable_and_never_converted(self):
        for block in ({"rng_config_version": 1, "effective_master_seed": 31415}, {"rng_config_version": 1, "effective_master_seed": -5},
                      {"rng_config_version": 2, "effective_master_seed": "0000000000007ab7"}, {"rng_config_version": 2, "effective_master_seed": 12345},
                      {"effective_master_seed": 31415}, {"effective_master_seed": "0000000000007ab7"},
                      {"rng_config_version": 4, "master_seed_representation": "uint64_hex16", "effective_master_seed": "0123456789abcdef"},
                      {"rng_config_version": True, "effective_master_seed": "0123456789abcdef"}):
            r = x.manifest_master_seed(block)
            self.assertFalse(r["reconstructable"], block)
            self.assertIsNone(r["seed"], "no seed is guessed or converted: %r" % (block,))
            self.assertIn("unsupported RNG manifest format", r["reason"])
            self.assertEqual(r["rng_config_version"], block.get("rng_config_version"))

    def test_current_version_with_an_unreadable_or_missing_seed_is_not_reconstructable(self):
        base = {"rng_config_version": 3, "master_seed_representation": "uint64_hex16"}
        for eff in (12345, 1.5, None, "123", "0123456789abcdeg", "0000000000000000", " 0123456789abcdef", "0123456789abcdef0"):
            r = x.manifest_master_seed(dict(base, effective_master_seed=eff))
            self.assertFalse(r["reconstructable"], eff); self.assertIsNone(r["seed"]); self.assertTrue(r["reason"])
        r = x.manifest_master_seed({"rng_config_version": 3, "effective_master_seed": "0123456789abcdef"})   # representation missing
        self.assertFalse(r["reconstructable"])
        for block in (None, {}, {"seed_source": "fixed"}):
            r = x.manifest_master_seed(block)
            self.assertFalse(r["reconstructable"]); self.assertIsNone(r["seed"]); self.assertEqual(r["representation"], "none")


if __name__ == "__main__":
    unittest.main()
