#!/usr/bin/env python3
"""
experiment_rng.py - reference implementation of the experiment RNG (Assets/Scripts/ExperimentRng.cs) for offline audit and reconstruction.

  PCG32 (XSH-RR 64/32) with the reference seeding; SeedDerivation (SplitMix64 chain) from Master Seed + Part + stream + index.
  Algorithm version 1, seed-derivation version 1 (session_manifest.json -> rng.rng_algorithm_version / rng_seed_derivation_version).

  MASTER SEED: an unsigned 64-bit integer (0 .. 2**64-1), the single root of every stream. External form: exactly 16 hexadecimal digits (parse_seed_hex / seed_hex); the current
  manifest contract is rng_config_version 3 (master_seed_representation "uint64_hex16"). Earlier layouts (numeric 32-bit seeds, version 1 / 2) are NOT supported: manifest_master_seed()
  reports them as not reconstructable with the detected version and never converts or guesses a seed. Seeds are handled as exact Python ints / hex strings, never as floats.

Given the manifest's effective Master Seed and an npc_lifetimes.csv row (rng_part, rng_spawn_index) this reproduces the NPC's streams, e.g. its Part 3 movement
assignment:  first MovementAssignment value r;  attractor when r < probability.  Pure Python 3, no dependencies.
"""

M64 = (1 << 64) - 1

PARTS = {"None": 0, "Part1": 1, "Part2": 2, "Part3": 3}
STREAMS = {"PartSetup": 1, "SpawnPlacement": 2, "MovementAssignment": 3, "MovementAttributes": 4, "Appearance": 5,
           "Destination": 6, "Wandering": 7, "AttractorDwell": 8, "Uncertainty": 9, "NavigationNudge": 10, "TargetBuilding": 11}
TARGET_BUILDING_DOMAIN = "Part3TargetBuilding"
PART_LEVEL_INDEX = -1


def mix(x):
    """SplitMix64 finalizer of x + the golden-ratio increment. mix(0) == 0xe220a8397b1dcdaf."""
    x = (x + 0x9E3779B97F4A7C15) & M64
    x = ((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9) & M64
    x = ((x ^ (x >> 27)) * 0x94D049BB133111EB) & M64
    return x ^ (x >> 31)


def check_u64(value, what="seed"):
    """The value as an exact unsigned 64-bit int; floats, bools, negatives and values >= 2**64 are rejected (no silent masking or narrowing)."""
    if isinstance(value, bool) or not isinstance(value, int):
        raise TypeError("%s must be an int, got %r" % (what, type(value).__name__))
    if value < 0 or value > M64:
        raise ValueError("%s %d is outside the unsigned 64-bit range" % (what, value))
    return value


def parse_seed_hex(text):
    """Strict parse of the canonical external form: exactly 16 hexadecimal characters (either case), nothing else (no sign, '0x', whitespace). Returns an exact int."""
    if not isinstance(text, str):
        raise TypeError("a hex seed must be a str, got %r" % type(text).__name__)
    if len(text) != 16 or any(c not in "0123456789abcdefABCDEF" for c in text):
        raise ValueError("expected exactly 16 hexadecimal characters, got %r" % text)
    return int(text, 16)


def bytes_to_seed_le(data):
    """Eight entropy bytes -> seed, LITTLE-ENDIAN (data[0] least significant): the rule of MasterSeedFormat.FromBytesLittleEndian."""
    if len(data) != 8:
        raise ValueError("exactly 8 bytes are required")
    return int.from_bytes(bytes(data), "little")


def stream_seed(root_seed, part, stream, index):
    """64-bit stream seed. root_seed: the unsigned 64-bit Master Seed; part/stream: names or numbers; index: spawn index or -1 (Part level)."""
    part = PARTS.get(part, part)
    stream = STREAMS.get(stream, stream)
    h = mix(check_u64(root_seed, "root seed") ^ 0x4C594F4E52455831)
    h = mix((h + part) & M64)
    h = mix((h + stream * 0x100000001B3) & M64)
    h = mix((h + ((index + 1) & M64)) & M64)
    return h


def hash_utf8(text):
    """FNV-1a 64 over the UTF-8 bytes of an identifier, then the length, then mix() - identical to SeedDerivation.HashUtf8."""
    data = (text or "").encode("utf-8")
    h = 0xCBF29CE484222325
    for b in data:
        h = ((h ^ b) * 0x100000001B3) & M64
    h = ((h ^ len(data)) * 0x100000001B3) & M64
    return mix(h)


def target_building_seed(master_seed, participant_id, part3_group_index, region_id):
    """64-bit stream seed of the participant-specific Part 3 target-building draw (SeedDerivation.TargetBuildingSeed). Seeds are always handled as exact ints / 16-digit hex strings."""
    h = mix(check_u64(master_seed, "master seed") ^ 0x4C594F4E52455832)
    h = mix((h + hash_utf8(TARGET_BUILDING_DOMAIN)) & M64)
    h = mix((h + hash_utf8(participant_id)) & M64)
    h = mix((h + (part3_group_index + 1)) & M64)
    h = mix((h + hash_utf8(region_id)) & M64)
    return h


def choose_target_building(master_seed, participant_id, part3_group_index, region_id, candidate_ids):
    """(selected id, sorted candidate ids, derived seed hex). Candidates are sorted by code point (UTF-8 byte order == ordinal order for ASCII ids), then index = Range(0, n) of the first value."""
    ids = sorted(candidate_ids, key=lambda t: t.encode("utf-8"))
    seed = target_building_seed(master_seed, participant_id, part3_group_index, region_id)
    g = Pcg32(mix((seed + 1) & M64), mix((seed + 2) & M64))
    index = g.next_range(len(ids))
    return ids[index], ids, "%016x" % seed


def seed_hex(value):
    """The documented representation of a 64-bit seed: exactly 16 lowercase hex digits."""
    return "%016x" % check_u64(value)


SUPPORTED_RNG_CONFIG_VERSION = 3
SEED_REPRESENTATION = "uint64_hex16"


def manifest_master_seed(rng_block):
    """
    The effective Master Seed of a manifest 'rng' block as an exact int, and whether the session can be reconstructed.
    Returns {"seed", "representation", "reconstructable", "reason", "rng_config_version"}.
      * rng_config_version 3 with a 16-hex-digit effective_master_seed ... reconstructable
      * no 'rng' block ......................................................... not reconstructable (recorded before the experiment RNG existed)
      * any other version (1, 2, absent, unknown) ............................... not reconstructable: "unsupported RNG manifest format", with the detected version
      * a zero / unreadable seed (RNG failed or never initialised) .............. not reconstructable
    Nothing is converted or guessed: a numeric seed is never reinterpreted as a hex seed. Historical files are only read, never rewritten.
    """
    def no(reason, version=None, rep="unsupported"):
        return {"seed": None, "representation": rep, "reconstructable": False, "reason": reason, "rng_config_version": version}
    if not isinstance(rng_block, dict) or "effective_master_seed" not in rng_block:
        return no("no rng block: recorded before the experiment RNG existed", None, "none")
    version = rng_block.get("rng_config_version")
    if isinstance(version, bool) or version != SUPPORTED_RNG_CONFIG_VERSION:
        detected = "not recorded" if version is None else repr(version)
        return no("unsupported RNG manifest format: rng_config_version %s (only version %d, %s, is supported); the seed is not converted or guessed"
                  % (detected, SUPPORTED_RNG_CONFIG_VERSION, SEED_REPRESENTATION), version)
    if rng_block.get("master_seed_representation") != SEED_REPRESENTATION:
        return no("rng_config_version 3 requires master_seed_representation %r, got %r" % (SEED_REPRESENTATION, rng_block.get("master_seed_representation")), version)
    try:
        seed = parse_seed_hex(rng_block["effective_master_seed"])
    except (TypeError, ValueError) as e:
        return no("effective_master_seed is not a 16-digit hex string: %s" % e, version, SEED_REPRESENTATION)
    if seed == 0:
        return no("no effective seed recorded (the RNG had not been initialised or had failed)", version, SEED_REPRESENTATION)
    return {"seed": seed, "representation": SEED_REPRESENTATION, "reconstructable": True, "reason": "current manifest format", "rng_config_version": version}


class Pcg32:
    def __init__(self, init_state, init_seq):
        self.state = 0
        self.inc = ((init_seq << 1) | 1) & M64
        self._step()
        self.state = (self.state + init_state) & M64
        self._step()

    @classmethod
    def for_stream(cls, root_seed, part, stream, index):
        s = stream_seed(root_seed, part, stream, index)
        return cls(mix((s + 1) & M64), mix((s + 2) & M64))

    def _step(self):
        old = self.state
        self.state = (old * 6364136223846793005 + self.inc) & M64
        return old

    def next_uint(self):
        old = self._step()
        xorshifted = (((old >> 18) ^ old) >> 27) & 0xFFFFFFFF
        rot = old >> 59
        return ((xorshifted >> rot) | (xorshifted << ((-rot) & 31))) & 0xFFFFFFFF

    def next_range(self, n):
        """Unbiased integer in [0, n) (Lemire), the algorithm of Pcg32.Range(int, int)."""
        if n <= 0:
            return 0
        m = self.next_uint() * n
        low = m & 0xFFFFFFFF
        if low < n:
            threshold = ((1 << 32) - n) % n
            while low < threshold:
                m = self.next_uint() * n
                low = m & 0xFFFFFFFF
        return m >> 32

    def next_float(self):
        """Uniform in [0, 1) with 24 bits, as float32 would hold it."""
        return (self.next_uint() >> 8) * (1.0 / 16777216.0)


def predict_movement_mode(root_seed, part, spawn_index, probability):
    """'Part3AttractorItinerary' or 'Part2WanderingItinerary' for an NPC (the rule of Part3MovementStrategySelector.Predict)."""
    r = Pcg32.for_stream(root_seed, part, "MovementAssignment", spawn_index).next_float()
    p = 1.0 if probability != probability else min(1.0, max(0.0, probability))
    return "Part3AttractorItinerary" if r < p else "Part2WanderingItinerary"


if __name__ == "__main__":
    print("hash_utf8('P001'): %016x" % hash_utf8("P001"))
    print("target_building_seed(31415, 'P001', 4, 'OE'): %016x" % target_building_seed(31415, "P001", 4, "OE"))
    print("target_building_seed(31415, 'P002', 4, 'OE'): %016x" % target_building_seed(31415, "P002", 4, "OE"))
    print("target_building_seed(0xfffffffb, 'P001', 11, 'GU'): %016x" % target_building_seed(0xFFFFFFFB, "P001", 11, "GU"))
    print("choose(31415,'P001',4,'OE',[b,a]):", choose_target_building(31415, "P001", 4, "OE", ["OE_B", "OE_A"]))
    print("choose(31415,'P002',4,'OE',[b,a]):", choose_target_building(31415, "P002", 4, "OE", ["OE_B", "OE_A"]))
    ref = Pcg32(42, 54)
    print("pcg32(42,54):", " ".join("%08x" % ref.next_uint() for _ in range(6)))
    print("mix(0): %016x" % mix(0))
    print("stream_seed(12345, Part3, MovementAssignment, 7): %016x" % stream_seed(12345, "Part3", "MovementAssignment", 7))
    g = Pcg32.for_stream(12345, "Part3", "MovementAssignment", 7)
    print("first four values:", " ".join("%08x" % g.next_uint() for _ in range(4)))
    low = (1 << 32) - 987654   # a seed whose upper 32 bits are zero
    g = Pcg32.for_stream(low, "Part2", "PartSetup", PART_LEVEL_INDEX)
    print("part-level (%s, Part2, PartSetup):" % seed_hex(low), "%016x" % stream_seed(low, "Part2", "PartSetup", -1), " ".join("%08x" % g.next_uint() for _ in range(4)))
    # Full 64-bit reference vectors (C# ExperimentRngTests asserts the same values)
    for seed in (0x0123456789ABCDEF, 0x8000000000000000, 0xFFFFFFFFFFFFFFFF, 0x0000000100000000):
        g = Pcg32.for_stream(seed, "Part3", "Appearance", 5)
        print("64-bit seed %s: stream_seed(Part3, Appearance, 5) %016x  pcg %s" % (seed_hex(seed), stream_seed(seed, "Part3", "Appearance", 5), " ".join("%08x" % g.next_uint() for _ in range(4))))
    seed = 0xFEDCBA9876543210
    print("target_building_seed(%s, 'P001', 4, 'OE'): %016x" % (seed_hex(seed), target_building_seed(seed, "P001", 4, "OE")))
    print("choose(%s,'P001',4,'OE',[OE_B,OE_A]):" % seed_hex(seed), choose_target_building(seed, "P001", 4, "OE", ["OE_B", "OE_A"]))
