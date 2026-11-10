# Experiment RNG system

All project-owned randomness of the experiment (NPC placement, movement assignment, appearance, routes, hesitation, Part setup choices) comes from one
central system: **`ExperimentRngManager`** (`Assets/Scripts/ExperimentRngManager.cs`) over the deterministic core in `Assets/Scripts/ExperimentRng.cs`.
`UnityEngine.Random`, `System.Random` and clock-derived seeds are no longer used by project code (a source-scan test enforces this).

The goal is **controlled, reproducible and recorded random inputs**. It is *not* a claim that every NPC trajectory becomes identical (section 8).

## 0. What is random, what is not

| Experimental variable | Determined by |
|---|---|
| Part 2 start point (and teleport point) | **Part 2 group index** (the group designates exactly one point; no random choice) |
| Part 3 start point | **Part 3 group index** (fixed 12-row table, `ExperimentManager.Part3GroupTable`) |
| Part 3 target zones (Destination 1 and 2; always different zones) | **Part 3 group index** (same table, Table 5) |
| Target 1 / Target 2 **building** inside their zones | **two independent participant-specific streams** (`Target1Building` / `Target2Building`: Master Seed + Part 3 + stream + participant ID + group + that destination's zone), restricted to that zone |
| NPC movement and every other NPC attribute | **Master Seed** and the independent NPC streams (common to all participants) |

Group indices fix the spatial conditions; the participant ID only varies *which of a region's eligible buildings* is the target; the Master Seed fixes the common NPC
randomness. The three mechanisms use separate streams and cannot influence each other. Group index is deliberately **not** part of the NPC stream derivation (see 3a).

## 1. Master Seed

The Master Seed is an **unsigned 64-bit integer** (`ulong`, `0 <= seed <= 2^64 - 1`) and the **single root of every stream** (Part-level streams, per-NPC streams including the Part 3
MovementAssignment stream, and the two participant-specific target-selection streams). `0` is the *automatic* sentinel; every other bit pattern - including values above `long.MaxValue` -
is a valid fixed seed. It is never converted through `float` / `double`, and no step narrows it to 32 bits.

**The only external notation is exactly 16 hexadecimal characters** (`"0123456789abcdef"`). Upper or lower case is accepted on input; everything the system writes is 16
**lowercase** digits. Parsing and formatting are culture independent (`MasterSeedFormat`; Python: `experiment_rng.parse_seed_hex` / `seed_hex`). No whitespace, sign, `0x`
prefix or trimming is accepted. `"0000000000000000"` means *automatic*. **There is no numeric notation:** a JSON number - including a small integer, zero or a negative
number - is rejected.

| Setting | Where |
|---|---|
| `experimentRngMasterSeed` | top-level key of `session_config.json`, or the serialized `ExperimentManager.experimentRngMasterSeed` Inspector field |

### Inspector / scene representation

`ExperimentManager.experimentRngMasterSeed` is a serialized **`string`** (default `"0000000000000000"`), edited in the Inspector as 16 hex digits. Unity 2022.3 offers no
reliably supported unsigned-64-bit Inspector/YAML path (the default drawers handle `int`/`long`, and a `ulong` above `long.MaxValue` is not guaranteed to survive scene
YAML, domain reload and builds), so the text field is the backing store and it is parsed to `ulong` at runtime. The field keeps the **same serialized name**, so the logical
setting stays `experimentRngMasterSeed`. An `OnValidate` warning flags invalid text in the Editor; the runtime validation refuses to start the session with it. The shipped scene
(`Assets/Scenes/LyonVRVity35.unity`) holds `experimentRngMasterSeed: 0000000000000000` (automatic).

### Precedence and failure behaviour

| JSON value | Result |
|---|---|
| 16-hex-digit string, non-zero | **fixed 64-bit seed**; the Inspector value is ignored |
| `"0000000000000000"` | **one automatic seed** for this session (the Inspector value is ignored) |
| key absent | the Inspector value (`"0000000000000000"` = automatic, otherwise fixed); a missing key is not an error |
| string with fewer / more than 16 characters, non-hex characters, whitespace, `0x` prefix | **rejected** |
| **any JSON number** (`0`, `777`, `-5`, `2147483647`, `4294967296`, `1.5`, `12345.0`, `1e3`, `18446744073709551615`) | **rejected** |
| boolean, `null`, array, object | **rejected** |
| a key that looks like a mistyped seed key (`experimentRngMasterSeeed`, `masterSeed`, `rngSeed`, ...) | rejected with "did you mean 'experimentRngMasterSeed'" - never silently ignored |
| JSON missing / not parseable / invalid for any reason (external source) | the seed is uncertain: the configuration is invalid, the RNG manager is **Failed**, and no Part starts |
| invalid Inspector string (not exactly 16 hex digits) and no valid JSON override | **rejected** the same way; never an automatic seed |

"Rejected" always means: the session configuration is invalid, no Part starts, the Inspector value is **not** used instead, and **no automatic seed is generated**. The pre-existing
strict policy of the session parser (every unknown top-level key is an error) is unchanged; the typo detection only adds the specific diagnostic.

### Automatic seed

* One seed per application session from the operating-system CSPRNG (`System.Security.Cryptography.RandomNumberGenerator`), policy `session_specific_auto_seed`,
  recorded as `effective_master_seed`. **All 64 bits come from 8 entropy bytes**, assembled **little-endian** (`bytes[0]` is the least significant byte;
  `MasterSeedFormat.FromBytesLittleEndian`) - never `BitConverter`, so the machine byte order cannot matter. The result is never 0 (an all-zero draw, probability 2^-64, is
  redrawn rather than patched; four zero draws in a row fail initialisation). No `Environment.TickCount`, instance id, `DateTime` or participant ID.
* If the entropy source throws, the manager is **Failed**: every RNG request throws and `ExperimentManager.IdentityAllowsStart` refuses every Part.
* The API is standard `mscorlib`/netstandard and is expected to work under Android IL2CPP, but this has **not been verified on a device or an IL2CPP build**.

### Lifecycle

The first RNG request (pool preparation, an NPC spawn, a Part start - whichever is first) runs `EnsureInitialized()`: it loads the session configuration (idempotent, memoised),
resolves the seed as above, generates the automatic seed if requested and **freezes** the effective seed - before any stream exists. Awake/Start order therefore cannot matter, a
configuration that is missing or invalid can never freeze a guessed seed, and the seed is never regenerated at Part start or changed afterwards. A scene without any
`ExperimentManager` is a standalone (non-experiment) scene and uses an automatic seed (`config_source = no_experiment_manager`).

* **Fixed (non-zero):** that exact seed; policy `shared_fixed_seed`. All participants given the same value get the same derived NPC streams. Participant ID, session ID,
  group index and the clock are **not** mixed into any NPC stream.

### Examples (`session_config.json`)

```jsonc
// 1. Fixed 64-bit Master Seed, shared by every participant who is given this value (16 hex digits, either case on input, lowercase in all output)
{ "schemaVersion": 1, "participantId": "P001", "selectedPart": "Part3", "part2GroupIndex": 0, "part3GroupIndex": 4, "experimentRngMasterSeed": "0123456789abcdef" }

// 2. Automatic, session-specific Master Seed (explicit all-zero; the generated 64-bit value is recorded in the manifest)
{ "schemaVersion": 1, "participantId": "P002", "selectedPart": "Part3", "part2GroupIndex": 0, "part3GroupIndex": 4, "experimentRngMasterSeed": "0000000000000000" }

// 3. Key absent -> the serialized ExperimentManager.experimentRngMasterSeed (scene) value is used
{ "schemaVersion": 1, "participantId": "P003", "selectedPart": "Part3", "part2GroupIndex": 0, "part3GroupIndex": 4 }

// 4. REJECTED (the session does not start): any number (20261110, 0, -5, 12345.0), "123" / "0123456789abcdef0" / "0123456789abcdeg" / " 0123456789abcdef" (not 16 hex digits),
//    true, null, or a mistyped key such as "experimentRngMasterSeeed"
{ "schemaVersion": 1, "participantId": "P004", "selectedPart": "Part3", "part2GroupIndex": 0, "part3GroupIndex": 4, "experimentRngMasterSeed": 20261110 }
```

## 2. Algorithms (versioned)

| Item | Value | Version |
|---|---|---|
| PRNG | PCG32 (XSH-RR 64/32), reference seeding `(initstate, initseq)` | `rng_algorithm_version = 1` |
| Seed derivation | SplitMix64 finalizer chained over Master Seed, Part, stream, index (pure integer arithmetic) | `rng_seed_derivation_version = 1` |
| RNG configuration record | manifest `rng` block + `npc_lifetimes.csv` `rng_*` columns | `rng_config_version = 3` (only supported layout; earlier versions are not read) |

```
h = Mix(rootSeed ^ 0x4C594F4E52455831)            // "LYONREX1"; rootSeed = the full unsigned 64-bit Master Seed
h = Mix(h + part)                                  // None=0 Part1=1 Part2=2 Part3=3
h = Mix(h + stream * 0x100000001B3)                // stream ids below
h = Mix(h + (index + 1))                           // spawn index >= 0; -1 for Part-level streams
initstate = Mix(h + 1);  initseq = Mix(h + 2)      // Mix(x) = SplitMix64 finalizer of x + 0x9E3779B97F4A7C15
```

No `string.GetHashCode()`, no clock, no platform-dependent operation: the same inputs give the same stream on every runtime. **Test vectors** (checked in
`ExperimentRngTests`, and reproduced by `docs/tools/experiment_rng.py`):

```
pcg32(initstate 42, initseq 54)               a15c02b7 7b47f409 ba1d3330 83d2f293 bfa4784b cbed606e
SplitMix64(0)                                 e220a8397b1dcdaf
stream_seed(12345, Part3, MovementAssignment, 7) = 3416d3f507fd82e3   -> b7e72b1d 5df2b1fb 286ca041 20cbe59b
stream_seed(00000000fff0edfa, Part2, PartSetup, -1) = ebca185bde3bc825   -> 6f6fa57b 47e9819d db773bc4 8af435a2
target_selection_seed(0000000000007ab7, Part3, Target1Building, "P001", 4, "OE") = 35d409ef71e2213f   (landmark OE_B of [OE_A, OE_B])
target_selection_seed(0000000000007ab7, Part3, Target2Building, "P001", 4, "GU") = 59a822d807652adc   (landmark GU_B of [GU_A, GU_B])

full 64-bit seeds (upper 32 bits matter), stream_seed(seed, Part3, Appearance, 5) -> first four PCG32 values:
  0123456789abcdef   f2bf7e15eba8a3ae   f1e263c4 ac8a94eb 3f69c154 e2182729
  8000000000000000   d12b2071557656fd   a5d60f57 3ab404ac bdeaa517 bca9ad63
  ffffffffffffffff   b3ecaf7719f9038f   d8b86df4 0619fc42 7e08b2ee 632dc6a5
  0000000100000000   3a6aff65bc3ade19   6335d753 bf9e17bc 164906bf b5a280e1
target_selection_seed(fedcba9876543210, Part3, Target1Building, "P001", 4, "OE") = fd3479745b31e27d   (landmark OE_B)
```

`SeedDerivationVersion` stays **1**: the derivation function is unchanged (the seed is XORed whole into the first `Mix`). The manifest layout is `rng_config_version = 3`.

## 3. Streams

| Id | Stream | Responsibility | Level |
|---|---|---|---|
| 1 | `PartSetup` | **reserved, currently unused**: Part 2 start / teleport points and the Part 3 start / regions are designated by the group index, not drawn | Part |
| 2 | `SpawnPlacement` | NPC entrance choice, NavMesh spawn-position variation, placement around the entrance; pool preparation (Part `None`) | NPC (+ Part `None`) |
| 3 | `MovementAssignment` | Part 3 Attractor / Wandering assignment: **first value** of the NPC's own stream | NPC |
| 4 | `MovementAttributes` | attractor dwell time, speed multiplier, uncertain-behaviour roll | NPC |
| 5 | `Appearance` | gender, body parts, materials, accessory | NPC |
| 6 | `Destination` | attractor count and order, exit of an attractor route | NPC |
| 7 | `Wandering` | wandering-point count and selection, exit of a wandering route | NPC |
| 8 | `AttractorDwell` | standing point inside an Attractor plane (`AttractorDwell`, drawn from the visiting NPC's stream) | NPC |
| 9 | `Uncertainty` | pause / hesitate / slow / detour / look-around decisions (`UncertainBehavior`) | NPC |
| 10 | `NavigationNudge` | small navigation corrections (`MoveToTarget` target nudge) | NPC |
| 11 | `Target1Building` | participant-specific choice of the Destination 1 target landmark inside the Destination 1 zone (not an NPC stream; keyed by Master Seed + Part 3 + stream + participant + group + zone) | participant |
| 12 | `Target2Building` | participant-specific choice of the Destination 2 target landmark inside the Destination 2 zone (independent of `Target1Building`; same key structure) | participant |

The numeric ids are part of the derivation: never renumber, only append.

* **Part-level streams** (`GetPartStream(part, stream)`): keyed by Master Seed + Part + stream. Different Parts are independent. (Only pool preparation uses one today: Part `None` / `SpawnPlacement`.)
* **Per-NPC streams** (`CreateNpcRng(part, spawnIndex)` -> `NpcRngSet`): keyed additionally by the **stable spawn index**. One NPC's draws can never shift
  another NPC's streams, and an NPC drawing more values never changes anyone else.
* **Components without a spawner** (a hand-placed prefab, a test rig, `AgentProfile.ChooseGender()` called without a stream) use the shared
  `UnassignedNpcRng` (Part `None`, index -1): still deterministic and counted, never `UnityEngine.Random`.

### Spawn index

`SpawnerManager.SpawnAgent()` takes the index **before the first random draw of the attempt**: `spawnAttemptIndex++` per attempt of the running Part
(after the spawn prerequisites passed), reset to 0 by `ResetForNewPart()` at the start of every Part's NPC lifecycle. Gate-closed ticks do not consume
an index. An attempt abandoned later (no valid route this tick, no pooled object) **keeps its index**, so the next attempt has a different one - a retry
must not repeat a failing route. Because NavMesh reachability is a property of the scene, the same abandoned attempts recur for the same seed in the same
scene; but the index is attempt-based, not time-based.

### 3a. Group index and NPC streams

NPC streams are keyed by (Master Seed, Part, stream, spawn index) only. **Excluding** the group index (the implemented policy) means two participants in different groups
share the *same* NPC random inputs for the same spawn index, so the spatial condition (start point, regions) is the only systematic difference between groups; the NPC
randomness is a common background, which is what a between-group comparison wants. **Including** it would give each group its own NPC random sequence and confound
group with NPC randomness. Participant-specific NPC seeds were likewise not introduced. (Differences in the *scene* - e.g. a different start point - still change how the
same inputs play out, since routes depend on reachability.) This policy is unchanged by this revision.

### 3b. Group-controlled locations

* **Part 2:** `part2Groups[Part2GroupIndex]` must designate **exactly one** start point and **exactly one** teleport point (the shipped scene does). A missing / out-of-range
  group, an empty list, a null entry or more than one point is an error and the Part is refused (`ExperimentManager.TryResolvePart2Assignment`). The earlier random pick among
  a group's points no longer exists. No Master Seed, participant ID or NPC state is consulted.
* **Part 3** (the experimental phase with Destination 1 / Destination 2, the 10-minute D1 limit and the 15-minute total limit): `Part3GroupTable[Part3GroupIndex]` fixes the start zone, the Destination 1
  zone and the Destination 2 zone (Table 5, below); the start point is the zone's assigned point. An index outside 0..11, an unassigned start point or any zone / landmark defect (section 3c)
  is an error and the Part is refused (`TryResolvePart3Assignment`). Zone id = `GE`, `GU`, `OE`, `OU`.
* All of this is resolved and validated **before** `part_start` is raised.

**Table 5 (authoritative, implemented exactly; `ExperimentManager.Part3GroupTable`, pinned by tests in C# and Python):**

| Group | Start | Destination 1 | Destination 2 | | Group | Start | Destination 1 | Destination 2 |
|---:|---|---|---|---|---:|---|---|---|
| 0 | GE | GU | OE | | 6 | OE | GE | OU |
| 1 | GE | OE | GU | | 7 | OE | GU | OE |
| 2 | GE | OU | GE | | 8 | OE | OU | GE |
| 3 | GU | GE | OU | | 9 | OU | GE | OU |
| 4 | GU | OE | GU | | 10 | OU | GU | OE |
| 5 | GU | OU | GE | | 11 | OU | OE | GU |

**Destination 1 and Destination 2 are different zones in all 12 groups.** Groups 2, 4, 7 and 9 start in the zone of one of their destinations, but their two destination zones differ as well
(`OU/GE`, `OE/GU`, `GU/OE`, `GE/OU`). There is no same-zone group, therefore no same-zone sampling and no sampling without replacement anywhere.

### 3c. Participant-specific target selection (two independent draws)

Every participant receives **Target 1** = one of the two landmarks of the Destination 1 zone and **Target 2** = one of the two landmarks of the Destination 2 zone, as two SEPARATE selections
(two result objects, two derived seeds, two generator objects; nothing mutable is shared or reused). Because the zones differ, the selections need no coordination and the two ids are always distinct
(still validated). Each selection is uniform over its two landmarks; participants are not balanced or de-duplicated against each other (no quota): two participants may draw the same landmarks.

* **Eligible landmarks of a zone** = exactly two assigned buildings, each with a `TargetSelectable` on the building object itself (id = `ResolvedTargetId`, the stable identity used for scoring) and an
  enabled non-trigger `Collider` on that object. Identified by id, **sorted ordinally** (not by hierarchy, slot or scene order); the pick is `sorted[Range(0, 2)]` of the FIRST value of the destination's stream.
* **Scene validation before `part_start`** (all four zones, whether or not the group uses them): every zone has exactly two distinct landmark objects; ids are unique within and across zones; no object is in two zones;
  both destination panels exist and are different objects. Then: the two zones differ, Target 1 is a Destination 1 zone landmark, Target 2 a Destination 2 zone landmark, the two ids differ. Any failure refuses the
  Part with a precise message; nothing falls back or is repaired.
* **Stream per destination** (`Target1Building` for Destination 1, `Target2Building` for Destination 2; selection version 2):

```
h = Mix(MasterSeed ^ 0x4C594F4E52455832)            // "LYONREX2" (the NPC streams use ...31)
h = Mix(h + part)                                    // Part 3
h = Mix(h + stream * 0x100000001B3)                  // 11 = Target1Building, 12 = Target2Building
h = Mix(h + HashUtf8(participantId))                 // FNV-1a 64 over the UTF-8 bytes, length-terminated, then Mix; case and every character matter
h = Mix(h + (groupIndex + 1))
h = Mix(h + HashUtf8(zoneId))                        // that destination's own zone; initstate = Mix(h + 1), initseq = Mix(h + 2)
```

No `string.GetHashCode()`, no clock, no instance id, no spawn order, no `UnityEngine.Random`. A missing / blank participant ID is an error (no shared-stream fallback). The streams are counted under their own
names, never under an NPC stream, and an NPC stream can neither be shifted by nor shift them. Replaying a session with its recorded `effective_master_seed`, the same participant ID and group reproduces the pair;
a different automatic seed can give a different pair. The previous single shared `TargetBuilding` stream (selection version 1) is superseded; the readers do not support it.

### 3d. Target state during the session

`ExperimentManager.Target1BuildingId` / `Target2BuildingId` (fixed for the session) are separate; `ActivePart3Destination` ("D1" / "D2" / "") and `ActivePart3TargetId` are set **explicitly** when a search
opens and cleared when it closes, so the Destination 2 search can never read the Destination 1 target. The Destination 1 panel is prepared for Target 1's building and the Destination 2 panel for Target 2's
(`UIManager.Destination1PreviewBuilding` / `Destination2PreviewBuilding`). Only the active target (plus, when `allowWrongCandidateSelection` is on, the other landmark of the same zone) is selectable; a click is scored
`correct` only when its id equals the open search's target id. Finding Target 1 moves to Destination 2 exactly once; the 10-minute D1 timeout moves there with Target 2; the 15-minute total limit ends the phase
(timing logic unchanged).

## 4. Part 3 movement assignment

For the NPC with spawn index *i*: `r` = first value of its `MovementAssignment` stream (`Pcg32(root, Part3, MovementAssignment, i)`), Attractor when
`r < a` (`a = part3AttractorMovementProbability`), otherwise Wandering. Same Master Seed + same Part + same index = same assignment, independent of spawn
timing, of other NPCs and of every other draw; the attractor sets are nested across probabilities. Only explicit Part 3 with `npcDirectedMovement = true`
takes the value; Part 1 / Part 2 / no Part / directed movement off take **none** (verified with the per-stream draw counters).

**Root seed:** the Master Seed is the **only** root of the MovementAssignment stream. There is no separate movement-assignment seed (the former movement-assignment seed Inspector
field, its scene value, the manifest list of overrides and the corresponding setting entry were removed). The derivation is exactly
`(Effective Master Seed, Part identifier, MovementAssignment stream id, NPC spawn index)`; participant ID and group index do not enter it. `part3AttractorMovementProbability`
(default scene value 0.6) is unchanged.

## 5. What is recorded

`session_manifest.json` -> `rng` (refreshed with every manifest update; counts are final at session end):

| Field | Meaning |
|---|---|
| `rng_algorithm`, `rng_algorithm_version` | `PCG32-XSH-RR`, 1 |
| `rng_seed_derivation`, `rng_seed_derivation_version` | `SplitMix64-chain`, 1 |
| `rng_config_version` | **3** (the only supported layout) |
| `master_seed_representation` | `uint64_hex16` |
| `configured_master_seed` | the input as 16 lowercase hex digits (`0000000000000000` = automatic) |
| `effective_master_seed` | the seed actually used, 16 lowercase hex digits (never a JSON number) |
| `seed_source` | `fixed` / `auto_generated` |
| `seed_policy` | `shared_fixed_seed` / `session_specific_auto_seed` |
| `configured_seed_origin` | `EXTERNAL_JSON`, `INSPECTOR`, `SCENE_INHERITED`, or a fallback label |
| `initialization_note` | empty, or why an automatic seed was used as a fallback |
| `stream_inventory` | all stream names |
| `part_stream_seeds` | `Part:Stream=<16 hex>` for the Part-level streams created so far |
| `stream_draw_counts` | `Part:Stream=n` 32-bit values drawn per (Part, stream) - a compact summary, not individual draws |
| `npc_stream_seed_rule` | how a per-NPC stream is derived |
| `stream_versions` | `Name=1` for every stream |
| `rng_configuration_validation` | `valid`, or `failed: <reason>` / `not_initialized` |
| `automatic_entropy_source` | `os_csprng (...)` for an automatic seed, `not_used_fixed_seed` otherwise |
| `abandoned_spawn_attempt_count`, `abandoned_spawn_attempts` | spawn attempts that consumed a spawn index without creating an NPC, as `Part:index:reason` (`no_valid_route`, `pool_or_instantiate_failed`; first 500) - a gap in `rng_spawn_index` is explained here |

`session_manifest.json` -> `assignments` (group-controlled conditions and the participant-specific draw; the participant ID is the manifest's normal `participant_id`, not duplicated): `part2_group_index`, `part2_start_point_id`, `part2_teleport_point_id`, `part3_group_index`, `part3_start_zone`, `part3_start_point_id`, `destination1_zone_id`, `destination2_zone_id`, `target1_building_id`, `target2_building_id`, `destination1_candidate_ids` / `destination2_candidate_ids` (the two sorted ids), `destination1_candidate_fingerprint` / `destination2_candidate_fingerprint` (16 hex), `target1_stream` / `target2_stream`, `target1_selection_seed` / `target2_selection_seed` (16 hex), `target_selection_version` (2), `target_selection_algorithm`, `target_selection_derivation`, and the recorded self-checks `destination_zones_distinct`, `target1_in_destination1_zone`, `target2_in_destination2_zone`, `target_building_ids_distinct`. An auditor verifies after a session that Target 1 is in the Destination 1 zone, Target 2 in the Destination 2 zone, the two ids differ and the zones differ (`docs/tools/npc_movement_mix.py` -> `target_assignment_verification` re-derives all of it independently from `effective_master_seed`, `participant_id` and the recorded candidates).

**Seed representation:** every 64-bit seed is written as exactly **16 lowercase hexadecimal digits in a JSON/CSV string** (never a number; JSON numbers become doubles in many readers and would lose low bits). Python reads them with `int(x, 16)`; `docs/tools/experiment_rng.py` and `npc_movement_mix.py` handle them as exact ints. The spawn index and draw counts are plain integers (exact). `setting_entries` `experiment_rng_master_seed_configured` / `_effective` are the same 16-digit strings.

Also `setting_entries`: `experiment_rng_master_seed_configured` (source `EXTERNAL_JSON` / `SCENE_INHERITED` / `INSPECTOR`) and
`experiment_rng_master_seed_effective` (source `AUTO_GENERATED` when generated), both as 16 hex digits. `npc_random_sources` is a human-readable summary. `schema_version` stays 17; the gaze schema is untouched.

`npc_lifetimes.csv` (appended at the end): `rng_part` (`None|Part1|Part2|Part3`) and `rng_spawn_index` (`-1` = not spawned by `SpawnerManager`). With the
manifest's effective Master Seed these reconstruct every stream of that NPC (`docs/tools/experiment_rng.py`).

## 6. Compatibility

* A `session_config.json` without the key stays valid (the scene value applies). Historical manifests / CSVs are never rewritten; old `npc_lifetimes.csv` files simply lack the two
  `rng_*` columns (read by header name).
* **Historical RNG manifests are not supported by the readers.** `experiment_rng.manifest_master_seed` / `npc_movement_mix.py` reconstruct only `rng_config_version 3`
  (`master_seed_representation = uint64_hex16`). For any other manifest - an earlier version (1, 2), no version, an unknown version, an unreadable or zero seed - the reader reports
  `rng_reconstructable = false`, `rng_config_version = <detected version>` and the reason "unsupported RNG manifest format"; **no seed is guessed or converted**, and an old numeric
  seed is never reinterpreted as a hex seed. A manifest without an `rng` block (recorded before the experiment RNG existed) is reported as not reconstructable too. The files are only read.
* There is no 32-bit Master Seed input, no numeric JSON seed and no movement-assignment seed override anywhere in the active system.

## 7. Migrated processes

All former `UnityEngine.Random` calls of `SpawnerManager` (entrance, spawn jitter, pool prewarm jitter, routes, dwell), `AgentFactory` / `AgentProfile`
(gender, weighted parts, speed multiplier, uncertain roll), `AppearanceController` (materials, accessory), `UncertainBehavior`, `MoveToTarget` and
`ExperimentManager` (the former random Part 2 start / teleport choice and the shared-stream Part 3 building choice - now group-controlled and participant-specific, section 0), and the private clock-seeded `System.Random` instances (entrance placement, `AttractorDwell`,
Part 3 selector). **Outside the project's control:** third-party package code (e.g. the disabled EasyNpcs AI components, XR/Meta SDK internals) may
still use their own generators; none is on the NPC prefab's active path. Editor-only audit tools keep their explicit seeds.

## 8. What is and is not reproducible

| Level | Status with a fixed Master Seed |
|---|---|
| Stream sequences, derived seeds | Fully reproducible (test vectors, any runtime) |
| Movement-mode assignment per spawn index | Fully reproducible |
| NPC initial attributes (speed multiplier, dwell time, uncertain flag, gender, appearance) per spawn index | Reproducible given the same Agent Profile and prefab |
| Entrance, route, wandering / attractor choice per spawn index | Reproducible given the same scene (NavMesh reachability decides abandoned attempts) |
| Which NPC exists when (spawn times, count at time *t*) | **Not guaranteed**: `WaitForSeconds(spawnInterval)` is frame-quantised, caps and pool state depend on timing |
| Hesitation decisions | Per-NPC stream, but Poisson sampling draws per `Update`, so the draw count per second depends on the **frame rate** |
| Trajectories | **Not guaranteed identical**: NavMesh avoidance, physics, update order and frame rate remain. Not redesigned here |

Experimental guidance: use a fixed Master Seed for controlled comparisons (the same random inputs per spawn index), the automatic seed for independent
sessions, and always read `rng.effective_master_seed`. Hardware (Quest Pro) behaviour and timing were not tested.
