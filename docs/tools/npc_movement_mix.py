#!/usr/bin/env python3
"""
npc_movement_mix.py - offline summary of the Part 3 NPC movement strategy and of the Part 2 / Part 3 accessory (wristband) distributions of one recorded session.

WHAT IT READS
  * session_manifest.json   setting_entries:
        spawner_part3_attractor_movement_probability   the CONFIGURED per-spawn probability that a new Part 3 NPC uses attractor movement
        spawner_part3_movement_assignment_seed         the effective seed of the private random stream that drew the strategies
        part3_npc_directed_movement                    true/false: Part 3's master switch. false = every Part 3 NPC wanders, so attractor NPCs cannot occur
        Manifests of sessions recorded with an earlier allocation design may carry an obsolete key; it is not read, never interpreted as a probability.
  * npc_lifetimes.csv       one row per NPC lifetime: part, profile_name, profile_content_hash, movement_mode, app_accessories_option, app_accessories_mesh.
        app_accessories_option: 0..n-1 = element chosen from the profile's accessory list (the last element of the production profile, 8, is the 'None' option);
        -3 = FORCED no accessory (Part 3 wandering NPC only); blank / -1 / -2 = not applicable.
  * npc_profiles.csv        profile_content_hash -> profile_json (the ORIGINAL accessory weights; the runtime Part 2 adjustment never modifies the profile).

THE RULE BEING CHECKED
  effective Attractor probability   a_eff = configured probability   if Part 3 directed movement is on (or not recorded),   0   if it is off.
  Part 3 NPC: attractor with probability a_eff -> draws the accessory from the ORIGINAL profile weights w_i; wandering -> forced None.
        overall Part 3 probability of element i:   a_eff * w_i / W          (None also gets the (1 - a_eff) wandering mass)
  Part 2 NPC: draws from the ADJUSTED weights  w'_i = a_eff * w_i  (wristbands),  w'_None = W - sum(w'_i):
        probability of element i:                  a_eff * w_i / W          -> equal to the Part 3 overall probability, element by element.
  Observed rates are finite random samples and are NOT expected to equal the expectation exactly.

WHAT IT REPORTS (descriptive; nothing is asserted against the configured value)
  configured_semantics / configured_attractor_probability / effective_attractor_probability / part3_directed_movement / movement_assignment_seed
  part3_attractor_spawns, part3_wandering_spawns, realised percentages, z_score_vs_configured (informational)
  profile_distribution (Part 3), attractor_accessory_rate, part3_attractor_accessory_distribution {element: count, pct}
  part3_wandering_forced_none_count (option -3), wandering_accessory_count / wandering_accessory_violations (Part 3 wandering NPCs that DO wear one: must be 0)
  part2_spawns, part2_profile_distribution, part2_accessory_distribution {element: count, pct}, part2_forced_suppression_markers (option -3 in Part 2: must be 0)
  accessory_comparison: per profile and element - expected Part 2 probability, expected Part 3 overall probability, observed Part 2 rate, observed Part 3 rate.
  A profile that is not 'wristband options + exactly one None option' is reported with adjusted = False (the runtime then used its original weights).

An absent value stays None / 'not_recorded' (never 0 or 1). Only rows whose 'part' is Part2 / Part3 are counted. The reader never modifies the session files.
Usage:  python3 -I npc_movement_mix.py <session_dir>
"""
import csv
import json
import math
import os
import sys

PROBABILITY_KEY = "spawner_part3_attractor_movement_probability"
SEED_KEY = "spawner_part3_movement_assignment_seed"
DIRECTED_KEY = "part3_npc_directed_movement"
ATTRACTOR = "Part3AttractorItinerary"
WANDERING = "Part2WanderingItinerary"
PART2 = "Part2"
PART3 = "Part3"
SUPPRESSED = "-3"


# ---------------------------------------------------------------------------------------------------------------------------------- manifest

def _setting(manifest, key):
    for entry in manifest.get("setting_entries") or []:
        if entry.get("key") == key:
            return entry.get("value")
    return None


def _float(text):
    try:
        return float(text)
    except (TypeError, ValueError):
        return None


def _bool(text):
    if text is None:
        return None
    t = str(text).strip().lower()
    if t in ("true", "1"):
        return True
    if t in ("false", "0"):
        return False
    return None


def read_configuration(session_dir):
    """(semantics, probability, seed, part3_directed) from the manifest. A session without the setting reports not_recorded and None."""
    path = os.path.join(session_dir, "session_manifest.json")
    if not os.path.isfile(path):
        return "not_recorded", None, None, None
    with open(path, encoding="utf-8") as f:
        manifest = json.load(f)
    probability = _float(_setting(manifest, PROBABILITY_KEY))
    seed_text = _setting(manifest, SEED_KEY)
    try:
        seed = int(seed_text) if seed_text is not None else None
    except ValueError:
        seed = None
    directed = _bool(_setting(manifest, DIRECTED_KEY))
    if probability is not None:
        return "per_spawn_probability", probability, seed, directed
    return "not_recorded", None, seed, directed


def effective_probability(probability, directed):
    """a_eff: the configured probability while Part 3 directed movement is on (or not recorded), 0 when it is off. None when the probability is not recorded."""
    if probability is None:
        return None
    if directed is False:
        return 0.0
    return min(1.0, max(0.0, probability))


# ---------------------------------------------------------------------------------------------------------------------------------- weights math

def adjusted_weights(weights, none_index, a):
    """Part 2 weights: wristband elements a * w_i, the None element W - sum(a * w_i). Same rule as AccessoryWeightAdjuster.AdjustedWeights (negative weights count 0)."""
    a = min(1.0, max(0.0, a))
    w = [max(0.0, x) for x in weights]
    total = sum(w)
    out = [0.0] * len(w)
    scaled = 0.0
    for i, x in enumerate(w):
        if i == none_index:
            continue
        out[i] = a * x
        scaled += out[i]
    out[none_index] = w[none_index] if a >= 1.0 else max(0.0, total - scaled)
    return out


def find_none_index(options):
    """options: [{'index','mesh','weight','genderRestriction'}]. Returns (none_index, reason): reason is None when the structure is the expected one."""
    if not options or len(options) < 2:
        return None, "fewer than two accessory options"
    if any(o is None for o in options):
        return None, "null option"
    if any(o.get("genderRestriction", "Unisex") != "Unisex" for o in options):
        return None, "option not Unisex"
    none = [i for i, o in enumerate(options) if (o.get("mesh") or "") == ""]
    if len(none) != 1:
        return None, "expected exactly one None option, found %d" % len(none)
    weights = [o.get("weight") for o in options]
    if any(not isinstance(x, (int, float)) or x < 0 or math.isnan(x) or math.isinf(x) for x in weights) or sum(weights) <= 0:
        return None, "invalid weights"
    return none[0], None


def expected_distributions(options, a_eff):
    """(expected_part2, expected_part3_overall, adjusted, note). Probabilities per element index; None entries when a_eff is unknown."""
    if a_eff is None:
        return None, None, None, "effective probability not recorded"
    weights = [o["weight"] for o in options]
    total = sum(weights)
    base = [x / total for x in weights]
    none_index, reason = find_none_index(options)
    if none_index is None:
        # unexpected structure: the runtime used the original weights for Part 2; no Part 3 overall expectation is derived
        return base, None, False, reason
    adj = adjusted_weights(weights, none_index, a_eff)
    part2 = [x / total for x in adj]
    part3 = [a_eff * b for b in base]
    part3[none_index] += 1.0 - a_eff
    return part2, part3, True, None


# ---------------------------------------------------------------------------------------------------------------------------------- session files

def read_lifetimes(session_dir):
    """(rows of Part 2 / Part 3 lifetimes, has_accessory_columns). Empty when the file is missing."""
    path = os.path.join(session_dir, "npc_lifetimes.csv")
    if not os.path.isfile(path):
        return [], False
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        has_accessory_columns = "app_accessories_mesh" in (reader.fieldnames or [])
        rows = [r for r in reader if r.get("part") in (PART2, PART3)]
    return rows, has_accessory_columns


def read_profiles(session_dir):
    """{profile_content_hash: {'name', 'accessories': [options]}} from npc_profiles.csv; {} when absent or unreadable (historical sessions)."""
    path = os.path.join(session_dir, "npc_profiles.csv")
    out = {}
    if not os.path.isfile(path):
        return out
    csv.field_size_limit(1 << 24)
    with open(path, newline="", encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                data = json.loads(r.get("profile_json") or "")
                options = data["parts"]["accessories"]
            except (ValueError, KeyError, TypeError):
                continue
            out[r.get("profile_content_hash", "")] = {"name": r.get("profile_name", ""), "accessories": options}
    return out


def wears_accessory(row):
    return (row.get("app_accessories_mesh") or "") != ""


def _option(row):
    return (row.get("app_accessories_option") or "").strip()


def _distribution(rows):
    counts = {}
    for r in rows:
        counts[_option(r)] = counts.get(_option(r), 0) + 1
    return counts


def _pct_table(counts, n):
    return {k: {"count": v, "pct": (100.0 * v / n) if n else None} for k, v in sorted(counts.items(), key=lambda kv: kv[0])}


# ---------------------------------------------------------------------------------------------------------------------------------- summary

def summarize(session_dir):
    semantics, probability, seed, directed = read_configuration(session_dir)
    a_eff = effective_probability(probability, directed)
    all_rows, has_accessory_columns = read_lifetimes(session_dir)
    part3 = [r for r in all_rows if r.get("part") == PART3 and r.get("movement_mode") in (ATTRACTOR, WANDERING)]
    part2 = [r for r in all_rows if r.get("part") == PART2]
    attractors = [r for r in part3 if r["movement_mode"] == ATTRACTOR]
    wanderers = [r for r in part3 if r["movement_mode"] == WANDERING]
    n = len(part3)
    result = {
        "configured_semantics": semantics,
        "configured_attractor_probability": probability,
        "part3_directed_movement": directed,
        "effective_attractor_probability": a_eff,
        "movement_assignment_seed": seed,
        "part3_attractor_spawns": len(attractors),
        "part3_wandering_spawns": len(wanderers),
        "realised_attractor_pct": (100.0 * len(attractors) / n) if n else None,
        "realised_wandering_pct": (100.0 * len(wanderers) / n) if n else None,
        "z_score_vs_configured": None,
        "profile_distribution": {},
        "attractor_accessory_rate": None,
        "wandering_accessory_count": None,
        "wandering_accessory_violations": [],
        "part3_attractor_accessory_distribution": None,
        "part3_wandering_forced_none_count": None,
        "part2_spawns": len(part2),
        "part2_profile_distribution": {},
        "part2_accessory_distribution": None,
        "part2_forced_suppression_markers": None,
        "accessory_comparison": [],
    }
    # informational: compared with the probability that applied to the draws (a_eff = configured while directed movement was on)
    if a_eff is not None and n and 0.0 < a_eff < 1.0:
        result["z_score_vs_configured"] = (len(attractors) - n * a_eff) / math.sqrt(n * a_eff * (1.0 - a_eff))
    for r in part3:
        name = r.get("profile_name") or ""
        result["profile_distribution"][name] = result["profile_distribution"].get(name, 0) + 1
    for r in part2:
        name = r.get("profile_name") or ""
        result["part2_profile_distribution"][name] = result["part2_profile_distribution"].get(name, 0) + 1
    if has_accessory_columns:
        if attractors:
            result["attractor_accessory_rate"] = sum(1 for r in attractors if wears_accessory(r)) / len(attractors)
        bad = [r.get("npc_spawn_instance_id", "") for r in wanderers if wears_accessory(r)]
        result["wandering_accessory_count"] = len(bad)
        result["wandering_accessory_violations"] = bad
        result["part3_attractor_accessory_distribution"] = _pct_table(_distribution(attractors), len(attractors))
        result["part3_wandering_forced_none_count"] = sum(1 for r in wanderers if _option(r) == SUPPRESSED)
        result["part2_accessory_distribution"] = _pct_table(_distribution(part2), len(part2))
        result["part2_forced_suppression_markers"] = sum(1 for r in part2 if _option(r) == SUPPRESSED)
        result["accessory_comparison"] = _compare(session_dir, part2, part3, a_eff)
    return result


def _compare(session_dir, part2, part3, a_eff):
    profiles = read_profiles(session_dir)
    hashes = sorted({r.get("profile_content_hash", "") for r in part2 + part3 if r.get("profile_content_hash")})
    table = []
    for h in hashes:
        prof = profiles.get(h)
        if prof is None:
            continue
        options = prof["accessories"]
        if not options or any(o is None for o in options):
            continue
        p2_rows = [r for r in part2 if r.get("profile_content_hash") == h]
        p3_rows = [r for r in part3 if r.get("profile_content_hash") == h]
        none_index, _ = find_none_index(options)
        e2, e3, adjusted, note = expected_distributions(options, a_eff)
        for i, o in enumerate(options):
            def rate(rows, count_forced_none):
                if not rows:
                    return None
                c = sum(1 for r in rows if _option(r) == str(i))
                if count_forced_none and i == none_index:
                    c += sum(1 for r in rows if _option(r) == SUPPRESSED)
                return c / len(rows)
            table.append({
                "profile": prof["name"], "profile_content_hash": h, "element": i, "mesh": o.get("mesh", ""), "original_weight": o.get("weight"),
                "is_none": i == none_index, "adjusted": adjusted, "note": note,
                "expected_part2": e2[i] if e2 else None,
                "expected_part3_overall": e3[i] if e3 else None,
                "observed_part2": rate(p2_rows, False), "observed_part3": rate(p3_rows, True),
                "part2_spawns": len(p2_rows), "part3_spawns": len(p3_rows),
            })
    return table


def _fmt(v):
    return "-" if v is None else ("%.4f" % v if isinstance(v, float) else str(v))


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    result = summarize(argv[1])
    table = result.pop("accessory_comparison")
    for key, value in result.items():
        print("%-42s %s" % (key, value))
    if table:
        print("\naccessory_comparison  (None element: Part 3 observed includes forced -3)")
        print("%-24s %7s %5s %8s %10s %10s %10s %10s" % ("profile", "element", "none", "w_orig", "exp_part2", "exp_part3", "obs_part2", "obs_part3"))
        for t in table:
            print("%-24s %7d %5s %8s %10s %10s %10s %10s" % (t["profile"][:24], t["element"], "yes" if t["is_none"] else "", _fmt(t["original_weight"]),
                                                              _fmt(t["expected_part2"]), _fmt(t["expected_part3_overall"]), _fmt(t["observed_part2"]), _fmt(t["observed_part3"])))
    violations = bool(result["wandering_accessory_violations"]) or bool(result["part2_forced_suppression_markers"])
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
