# Part 3 NPC movement probability and accessory rules

Two linked rules apply **only to Part 3**, plus one Part 2 accessory adjustment that depends on them (section 2b). Both reuse existing machinery: no movement algorithm and no Agent Profile property was added or rewritten.

1. **Movement strategy - an independent probability per spawn.** Every NPC spawned in Part 3 uses the existing *attractor* movement with probability `a` and the existing *wandering-point* movement with probability `1 - a`.
2. **Accessory - depends on the strategy.** A wandering NPC never wears the accessory **while Part 3 is explicitly the running Part**. A Part 3 attractor NPC gets its accessory from its Agent Profile exactly as before.

## 1. Movement probability

`SpawnerManager` → *Part 3 NPC Movement Configuration* → **Part 3 Attractor Movement Probability** (`part3AttractorMovementProbability`, `[Range(0,1)]`, default `1.0`). It is the single authoritative field.

| Value | Each new Part 3 NPC uses attractors | Each new Part 3 NPC uses wandering points |
|---|---|---|
| `0.0` | 0 % | 100 % |
| `0.25` | 25 % | 75 % |
| `0.5` | 50 % | 50 % |
| `0.75` | 75 % | 25 % |
| `1.0` (default) | 100 % | 0 % - identical to the behaviour before the setting existed |

Out-of-range values are clamped to `0..1` (in `OnValidate` and again where the value is read); `NaN` falls back to `1.0`.

### The rule

For every Part 3 spawn the spawner takes **one** value `r` in `[0, 1)` from a private random stream and assigns attractor movement when `r < a`, wandering movement otherwise (`Part3MovementStrategySelector.Choose`). Nothing else is consulted: not the live population, not the history, not other NPCs. There is no quota, no deficit correction and no reassignment, so the realised share **fluctuates** around `a` like any random sample (100 NPCs at `a = 0.5` will not be exactly 50/50). `a = 1` and `a = 0` are exact (`r < 1` always, `r < 0` never). One value is drawn for every decision whatever `a` is, so the position in the stream does not depend on the setting.

An earlier design (a quota allocator balancing the **active** population) was removed completely: no live counts, no deficit correction and no tie-breaks remain. Its manifest key is no longer generated, documented as a setting, or read.

### Lifetime and mid-Part changes

* The strategy is chosen once, in `SpawnerManager.SpawnAgent()` (DECIDE step), and passed to `PedestrianAgent.Initialize(...)`, which resets all per-lifetime state. It never changes during the lifetime, and spawning another NPC never changes an existing one.
* **Changing the value in the Inspector in the middle of a Part affects only NPCs spawned afterwards**; active NPCs keep their strategy.
* A pooled object is re-initialised on every acquire, so it may be an attractor NPC in one lifetime and a wandering NPC in the next.

### When it applies

The selector runs **only while `ExperimentManager.RunningPartName` is exactly `"Part3"`** and the Part's configuration selects attractor movement (`PartCaptureConfig.npcDirectedMovement`, the existing switch). It is never called merely because the Part is "not Part 1 / Part 2".

| Running Part | `npcDirectedMovement` | Selector called? | Movement |
|---|---|---|---|
| Part 1 | any | **no** | existing Part 1 behaviour (the configured itinerary) |
| Part 2 | any | **no** | existing Part 2 behaviour |
| **Part 3** | true | **yes, once per spawn** | attractor with probability `a`, else wandering |
| Part 3 | false | no | all wandering (the switch wins, no draw) |
| none (`""`) | any | **no** | existing neutral behaviour: the configured itinerary |
| unknown / no ExperimentManager | any | **no** | existing fallback (attractor itinerary) |

In every "no" row the selector's private stream is untouched: the first Part 3 spawns after any number of Part 1 / Part 2 / no-Part spawns are exactly the first draws of the seed. Part 2 never reads the setting **for movement**.

### Effective attractor probability

`a_eff` (`SpawnerManager.EffectivePart3AttractorProbability`) = the configured `part3AttractorMovementProbability` (clamped) while **Part 3 directed movement is on**, and `0` when the Part 3 configuration has `npcDirectedMovement = false` (every Part 3 NPC then wanders, so the configured value cannot produce an attractor NPC). The Part 3 draw uses the configured value (only reached when directed movement is on); the Part 2 accessory adjustment uses `a_eff`. There is no second editable probability. The Part 3 directed flag is read from `ExperimentManager.Part3NpcDirectedMovement` (the frozen Part 3 config once the session settings are frozen, otherwise the Inspector Part 3 config).

### Randomness and reproducibility

* The draw comes from a **private `System.Random`** owned by the selector. It never touches `UnityEngine.Random`, so route generation, wandering-point and attractor choice, dwell sampling and the Agent Profile rolls keep their sequences; Part 2 never calls it.
* **Seed:** `part3MovementAssignmentSeed` (Inspector). `0` (default) derives a seed from the clock and the component id, as the existing entrance-placement stream does; any other value is used as is. The *effective* seed is written to the manifest (`spawner_part3_movement_assignment_seed`, and in `npc_random_sources`).
* **Reproducibility:** the *i*-th draw of a stream is a function of the seed only, so the same seed gives the same sequence of draws (verified: the recorded `movement_mode` of real spawns equals the offline sequence exactly). Which NPC receives which draw still depends on spawn timing; a spawn attempt abandoned after the draw (no valid route that tick) consumes its draw. The rest of the simulation is not replayable (`UnityEngine.Random` is unseeded), as before.

## 2. Accessory rule

In this project the accessory is the **wristband** (`AppearanceController.accessoriesRenderer`). An Agent Profile expresses how many NPCs wear one through `accessoriesOptions`: weighted options, where an option without a mesh means "no wristband" and the share of NPCs with a wristband is the weight of the mesh options divided by the total. There is **no separate accessory-probability field**, and none was added; the Part 3 rule reuses `accessoriesOptions` unchanged.

| Running Part | Movement mode | Accessory |
|---|---|---|
| Part 1 | any | normal Agent Profile selection (rule not applied) |
| Part 2 | wandering | normal Agent Profile selection (rule not applied) |
| **Part 3** | **wandering** (including `npcDirectedMovement = false`) | **always none** - the profile is not consulted, option `-3` |
| Part 3 | attractor | the profile's `accessoriesOptions`, exactly as before (0 % none, 50 % half, 100 % all, several options keep their weights and colour alternatives) |
| none / unknown / no ExperimentManager | any | normal Agent Profile selection (never assumes Part 3) |

How it is enforced: after the strategy is chosen, `SpawnAgent()` passes `suppressAccessory = (RunningPartName == "Part3" && mode == Part2WanderingItinerary)` (`SpawnerManager.ShouldSuppressAccessory`; wandering alone is not evidence of Part 3, and an unknown / empty Part never gets the rule) through `AgentFactory.ApplyProfileToInstance` to `AppearanceController.ApplyProfile(profile, suppressAccessory: true)`. That call **does not draw** an accessory option (no `UnityEngine.Random` value is used for it) and empties the accessory renderer exactly as the normal path does before a draw (no mesh, null materials), so a pooled object cannot keep the previous lifetime's wristband. The profile is the one already selected for the spawn; it is not re-selected. Every other profile roll (gender, clothes, speed, uncertainty) is untouched. **Random-stream side effect (accepted):** skipping the accessory draw means a suppressed NPC consumes fewer values from the global `UnityEngine.Random` than an unsuppressed one, so later global draws (routes, dwell, other appearance parts) land on different values than they would have. Nothing in the project seeds `UnityEngine.Random` (`Random.InitState` is not called), so no reproducible sequence existed to disturb, and every draw keeps its distribution. A dedicated accessory stream would have changed how Part 2 consumes randomness, so it was not introduced; the movement-strategy stream remains the private `System.Random`. Part 2 calls the unchanged path, so its accessory assignment is identical to before; a Part 3 → Part 2 reuse re-draws from the profile (and, for a profile without accessory options, restores the prefab's own accessory that a suppressed lifetime had cleared).

| Pool transition (previous lifetime → new lifetime) | Result |
|---|---|
| Part 3 attractor (with / without accessory) → Part 3 wandering | none |
| Part 3 wandering → Part 3 attractor | profile rule re-evaluated |
| Part 3 wandering → Part 2 wandering | normal profile rule (for a profile without accessory options, the prefab's own accessory is restored) |
| Part 2 with accessory → no running Part | normal profile rule |
| Part 2 with accessory → Part 3 wandering | none |
| Part 3 attractor → Part 1 | normal profile rule |

The `-3` marker appears only for explicit Part 3 wandering suppression.

## 2b. Part 2 accessory distribution matching

Goal: for the same Agent Profile, Part 2 NPCs have the same expected accessory composition as the whole mixed Part 3 population. With original weights `w_0..w_8` (elements 0-7 wristbands, element 8 = `None`), `W = sum(w)` and `a = a_eff`:

* Part 3 overall: attractor NPCs (probability `a`) draw from the original weights, wandering NPCs (probability `1 - a`) get `None`. `P(element i) = a * w_i / W`; `None` also receives the `(1 - a)` wandering share.
* Part 2: `w'_i = a * w_i` for the wristband elements, `w'_None = W - sum(w'_i)` (total unchanged). `P(element i) = w'_i / W = a * w_i / W` - identical, element by element, including `None`: `1 - a * (1 - w_None / W)`.

Applied **only while `RunningPartName` is exactly `"Part2"`** (`SpawnerManager.AccessoryWeightScaleForSpawn` -> `AgentFactory.ApplyProfileToInstance(..., accessoryWeightScale)` -> `AppearanceController.ApplyProfile(profile, suppress, scale)` -> `AccessoryWeightAdjuster.GetOptions`). Part 1, Part 3 (attractor and wandering), no running Part and an unknown Part use the original weights / the forced-None rule.

| Context | Accessory |
|---|---|
| Part 1 | original profile weights |
| Part 2 | **adjusted weights** (`a_eff`) |
| Part 3 attractor | original profile weights |
| Part 3 wandering | forced None, recorded `-3` |
| no active Part / unknown Part | original profile weights |

Properties: **non-destructive** (the shared `AgentProfile` asset is never modified; a copy of the option list is used, sharing the mesh / material references, element order kept so recorded indices are profile indices); **no compounding** (always recomputed from the original weights; the one-entry cache is validated against them); **no extra random draw** (only weights change, the profile's own weighted selector draws once); `a = 1` returns the profile's own array, `a = 0` puts all weight on `None`. **Safe structure check:** the rule needs all options Unisex and non-null, finite non-negative weights with positive total, at least one wristband (mesh) option and exactly one `None` option (no mesh, no materials, any index). Any other profile keeps its original weights in Part 2 and logs one warning per profile - index-based logic is never applied to a differently structured profile.

`None` chosen by the weighted draw is recorded as its normal element index (8 in the production profile) with a blank mesh; `-3` means only "forced by the Part 3 wandering rule".

Production profile (`Assets/VRExp/Agent Profile.asset`, the only profile asset): 8 wristband options (2 meshes x 4 materials) at 0.05 each, `None` 0.6, total 1.0. Part 2 weights for representative `a`:

| `a_eff` | elements 0-7 (each) | element 8 (None) | P(any wristband) |
|---|---|---|---|
| 0 | 0 | 1.0 | 0 % |
| 0.25 | 0.0125 | 0.90 | 10 % |
| 0.5 | 0.025 | 0.80 | 20 % |
| 0.75 | 0.0375 | 0.70 | 30 % |
| 1 | 0.05 | 0.60 | 40 % |

Part 2 and Part 3 may still differ in their overall accessory counts if their Agent Profile mixtures differ; the rule matches them per profile and does not change profile allocation.

**Side effect on `UnityEngine.Random`:** none beyond what already existed - the adjustment draws nothing; Part 3 wandering suppression still skips the accessory draw (see section 2).

## 3. Recorded data

| What | Where | Notes |
|---|---|---|
| Configured probability | `session_manifest.json` → `setting_entries[spawner_part3_attractor_movement_probability]` (source `INSPECTOR`) | the only key describing this setting; it also feeds the Part 2 accessory weights (via `a_eff`, section 2b) |
| Effective seed | `setting_entries[spawner_part3_movement_assignment_seed]`, `npc_random_sources` | |
| Strategy of each spawn | `npc_lifetimes.csv` `movement_mode` | already existed (column 21) |
| Accessory of each spawn | `npc_lifetimes.csv` `app_accessories_option` / `app_accessories_mesh` / `app_accessories_materials` | already existed; mesh non-blank = wears it; option `-3` = suppressed by the Part 3 wandering rule |
| Agent Profile | `npc_lifetimes.csv` `profile_name`, `profile_content_hash` (full profile in `npc_profiles.csv`) | the profile's accessory weights are in its snapshot |

An absent probability key means *not recorded* - never 0 and never 1.

**Historical note (not a current setting).** Sessions recorded with an earlier Part 3 allocation design (balancing the active population) may contain the obsolete manifest key `spawner_part3_attractor_movement_proportion`. Current code never writes it, the reader ignores it, and its value must not be read as a per-spawn probability; those raw files are left unchanged. A manifest with only that key reports the probability as *not recorded*. **No schema version is needed**: no CSV column was added or renamed, `gaze.csv` / `gaze_diagnostics.csv` are untouched, `schema_version` stays 17. `app_accessories_option = -3` is a new value in an existing column; readers that only test "mesh blank" are unaffected.

`docs/tools/npc_movement_mix.py <session_dir>` reports the configured and effective probability (`a_eff` from the probability and `part3_npc_directed_movement`), Part 2 and Part 3 per-element accessory distributions, and per profile the expected Part 2 / expected Part 3 overall / observed Part 2 / observed Part 3 rate of every element (expectations from the profile snapshot in `npc_profiles.csv`). It also reports: configured probability (or `not_recorded`), the seed, Part 3 attractor / wandering spawn counts and realised percentages, an informational z-score, the Agent Profile distribution, the attractor accessory rate, the number of Part 3 wandering NPCs wearing an accessory and their ids (must be none). Realised percentages are never asserted to equal the configured probability.

## 4. Verification and what it does not show

`Assets/Tests/GazeRecorder/NpcMovementMixTests.cs` and `AccessoryAppearanceTests.cs` (Unity Editor PlayMode, synthetic NavMesh, production components via reflection) cover: boundaries and clamping; binomial statistics at 0.1 / 0.25 / 0.5 / 0.75 / 0.9 (no exact counts); independence of consecutive draws; seed reproducibility and independence between seeds; nesting of the attractor sets across probabilities; no influence of any counter or population; no `UnityEngine.Random` use; cost and allocations; the Inspector field; real spawns whose strategies equal the seeded draw sequence; itineraries and destinations never crossing strategies; strategy fixed for the lifetime; mid-Part probability change; hold behaviour; Part 2 isolation (no read, no draw); accessory rules for profile shares 0 / 50 / 100 % (logical record and renderer state); pool transitions across Parts; recorded `movement_mode`, accessory fields and manifest entries.

These are Unity Editor tests. Nothing was measured on a Quest or on Quest Link; the cost figures are CPU time in the Editor. The real scene (production Agent Profile asset, NPC prefab renderers, spawn rates) still needs a play-mode pass, and the headset an on-device pass, before study use.
