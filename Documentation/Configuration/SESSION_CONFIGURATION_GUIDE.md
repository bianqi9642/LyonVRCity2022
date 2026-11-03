# Session configuration guide

How to configure one experiment session (participant, Part, group, conditions) either from the Unity Inspector (**Internal**) or from an external
`session_config.json` (**External**) without rebuilding the project.

The templates in this folder (`session_config.example*.json`) are **templates only**. The application never reads them. The runtime reads exactly one file:

```
<Application.persistentDataPath>/session_config.json
```

(`ExternalSessionController.externalFileName` can rename it; the folder is always `Application.persistentDataPath`.)
`Assets/Scripts/session_config.json` is a legacy sample and is **not** read by any code.

---

## 1. Internal vs External mode

Chosen by one switch: `ExternalSessionController.useExternalJson` (on the `SessionController` object in `LyonVRVity35.unity`).

| | Internal (`useExternalJson = false`) | External (`useExternalJson = true`) |
|---|---|---|
| Source of truth | Saved Inspector / scene values | A successfully validated `session_config.json` |
| Participant, Part, group indices, autoStart | Inspector fields | JSON (Inspector values are ignored) |
| Timing and per-Part NPC/capture flags | `ExperimentManager` fields in the scene | Scene values, unless the JSON has `timing` / `partConfig` entries (those win) |
| JSON file | Never read | Required. Missing/invalid = session blocked |
| Fallback | – | **None.** An invalid JSON never falls back to the Inspector |
| Validation | Same rules (participant, Part, group index of the selected Part) | Same rules, plus strict JSON schema |
| Manifest `config_source` | `inspector` | `external_json` (+ `config_hash`, `config_file_name`) |

The configuration is resolved **once** at startup into an immutable snapshot. Editing the Inspector or the JSON file during a run does **not** change the
running session; restart the application (or leave and re-enter Play Mode) to apply a change.

**Startup lifecycle.** `ExternalSessionController` runs first and resolves once (`Pending` → `Valid` or `Invalid`; any component may also trigger the same single
resolution earlier, so Awake order cannot matter). `ExperimentManager.EnsureSessionOverridesApplied()` then freezes the **effective settings** exactly once: the JSON
overrides merged with the Scene-owned values, each tagged with its source. It runs at the latest from `ExperimentManager.Start`, before auto-start, before the
NPC recording gate, the manifest, the NPC pool prewarm hand-off and every Part read them. If there is no controller or the configuration is `Invalid`, nothing is frozen
and Part start, auto-start and recording are all refused (fail closed). After the freeze, durations, per-Part NPC/capture flags, the wrong-candidate protocol and the
river-teleport restriction are read from the frozen copy: editing the Inspector, the Scene or the JSON file during the run has no effect on it.
(Spawner population settings — `maxAgents`, `spawnInterval`, dwell times, `poolPrewarm` — and the Agent Profile asset remain owned and read by `SpawnerManager` /
the asset itself; their values at freeze time are recorded for provenance, but they are not copied into a private runtime copy.)

If the external configuration is invalid, the console shows `Session configuration rejected (UnresolvedConfig): ...` with the file path and the failed
field, no Part starts (including auto-start) and `DataCaptureManager` refuses to record (`RECORDING BLOCKED`).

## 2. Where to put `session_config.json`

Use the menu **Tools > Session Configuration > Reveal External Config Folder** (it opens `Application.persistentDataPath` and logs the exact path and whether
the file exists). Typical locations:

| Platform | Folder |
|---|---|
| Unity Editor / macOS standalone | `~/Library/Application Support/<Company>/<Product>/` |
| Windows standalone | `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\` |
| Quest / Android | `/sdcard/Android/data/<package id>/files/` (copy with `adb push session_config.json ...`) |

`<Company>/<Product>` come from Player Settings (currently `DefaultCompany/LyonVRCity2022`). The application logs the resolved path at startup:
`[ExternalSessionController] External configuration file: <path> (exists: True/False)`.

The application never creates, edits, or overwrites this file. (`SaveToExternalJson()` exists for manual use and refuses to overwrite an existing file.)

## 3. Fields

Unknown fields are **rejected** (a typo such as `part3GroupIdx` must not silently fall back to a default). Comments are not allowed in JSON.

### Required

| Field | Type | Meaning |
|---|---|---|
| `participantId` | string | e.g. `"P001"`, `"P061"`. Kept exactly (only surrounding spaces are trimmed); leading zeros are preserved. A JSON number is rejected. |
| `selectedPart` | string | `"Part1"`, `"Part2"` or `"Part3"` (case-insensitive). Anything else is rejected. |
| `part2GroupIndex` | integer | Zero-based index into `ExperimentManager.part2Groups`. |
| `part3GroupIndex` | integer | Zero-based Part 3 group (0–11). **Group Number = Group Index** (there is no +1 anywhere). |

Both indices must always be present and be integers, but **only the index of the selected Part is range-checked**: Part 1 ignores both, Part 2 ignores
`part3GroupIndex`, Part 3 ignores `part2GroupIndex`. Invalid indices are never clamped to another group.

### Optional

| Field | Default when absent |
|---|---|
| `schemaVersion` | Legacy file (no key): treated as schema 1. If present it must be `1`. |
| `autoStart` | `false` |
| `timing.part1Duration`, `part2FirstDuration`, `part2SecondDuration`, `part3Destination1MaxSeconds`, `part3TotalMaxSeconds` | Value saved in the scene (`ExperimentManager`). Numbers of seconds, `> 0`, `<= 86400`. Part 3: Destination 1 max must not exceed the total. |
| `timing.npcWarmupDuration` | Scene value. Seconds, `>= 0` (`0` = skip the warm-up wait). |
| `partConfig.part1 / part2 / part3.{npcEnabled, npcDirectedMovement, npcCaptureEnabled, gazeCaptureEnabled, trajectoryCaptureEnabled}` | Scene values. Booleans. |

Each optional entry is independent; specify only what you want to change. Everything that is **not** listed here (prefabs, spawn points, NPC counts and
speeds, Agent Profile, sampling rates, debug switches) is infrastructure and stays authored in the scene.

The example values in `session_config.example.full.json` are the values **currently saved in the scene** (`15 / 15 / 15 / 30 / 60 / 10` seconds look like
short test values; they are illustrative, not protocol values). Replace them with your protocol values.

## 4. Part 2 and Part 3 groups (zero-based; Group Number = Group Index)

**Part 2** (`part2GroupIndex`, valid range = number of groups in `ExperimentManager.part2Groups`; currently 2 → `0` or `1`):

| Group Index = Group Number | Condition |
|---:|---|
| 0 | StartPointO → StartPointG |
| 1 | StartPointG → StartPointO |

**Part 3** (`part3GroupIndex`, authoritative table is `ExperimentManager.Part3GroupTable`, 12 rows):

| Group Index = Group Number | Start | D1 | D2 |
|---:|---|---|---|
| 0 | GE | GU | OE |
| 1 | GE | OE | GU |
| 2 | GE | OU | GE |
| 3 | GU | GE | OU |
| 4 | GU | OE | GU |
| 5 | GU | OU | GE |
| 6 | OE | GE | OU |
| 7 | OE | GU | OE |
| 8 | OE | OU | GE |
| 9 | OU | GE | OU |
| 10 | OU | GU | OE |
| 11 | OU | OE | GU |

Example: `"part3GroupIndex": 1` is **Part 3 Group 1**: Start = GE, D1 = OE, D2 = GU. The startup log prints `Part 3 Group: 1` and the resolved zones.
GE / GU / OE / OU are zone identifiers, unrelated to the group number.

## 5. Step by step

1. **Pick the mode.** On `SessionController`, tick `useExternalJson` for External, untick it for Internal.
2. **Internal:** set `participantId`, `selectedPart`, `part2GroupIndex`, `part3GroupIndex`, `autoStart` in the Inspector and save the scene.
3. **External:** copy the template for your Part (`session_config.example.part1/2/3.json`, or `.full.json`) to the folder from section 2 and rename it
   `session_config.json`. Edit `participantId`, `selectedPart` and the group index of that Part.
4. **Choose the participant ID.** Use the study convention `P001`, `P002`, … `P061`. Placeholders (`0`, `P000`, `UNKNOWN`, …), empty IDs and IDs
   containing `/ \ : * ? " < > |` are rejected. IDs are never generated or incremented automatically.
5. **Validate before starting.** In the Editor use **Tools > Session Configuration > Validate External session_config.json**: it runs the same
   validator on the real file and the open scene's group counts, and reports the resolved values or the exact error. Nothing is applied.
6. **Start.** Press Play / launch the build. The console shows a `SESSION CONFIGURATION` block (source, file, participant, Part, active group, zones,
   overrides, `Status: VALID`). If it says `INVALID`, nothing will start or record.
7. **Next participant.** Close the application, edit `participantId` (and Part / group) in `session_config.json`, start again. Every run creates its own
   session folder; a participant ID is not required to be unique per folder, so keep your own session log.
8. **Back to Internal.** Untick `useExternalJson` (the file is then ignored but left untouched).

## 6. What is recorded (manifest schema 8)

`session_manifest.json` records: `schema_version` (8), `participant_id`, `selected_part`, `config_source` (`inspector` / `external_json`), `config_state`,
`config_hash` (hash of the applied JSON text), `config_file_name` (file name only, no folder), `config_schema_version`, `config_schema_declared`,
`config_overrides` (the JSON entries that were applied, as `key=value;...`), `auto_start`, `part2_group_index`, `part3_group_index`, `active_group_index`
(index of the group relevant to the selected Part; `-1` for Part 1), `part3_group_number` (**equals `part3_group_index` in a Part 3 session, `-1` = not
applicable in Part 1/2**; Group 0 is a real value), `part2_group_count`, `part3_group_count`, the effective durations, the three `part*_config` snapshots (now
including `npc_directed_movement`), `allow_wrong_candidate_selection`, `restrict_river_teleport_to_part3`, `max_agents`, `spawn_interval`, `dwell_time_min/max`,
`agent_profile_name` / `agent_profile_hash`, the Part 3 zone assignment (`part3_start_zone`, `part3_d1_zone`, `part3_d2_zone`, assigned building ids) and
`effective_settings_status` (`frozen` when the list below is complete).

`setting_entries` lists every effective setting as `{ key, value, source }` where `source` is one of `EXTERNAL_JSON` (given by the JSON), `SCENE_INHERITED`
(External mode, taken from the saved Scene), `INSPECTOR` (Internal mode) or `DOCUMENTED_DEFAULT` (`autoStart` / `schemaVersion` absent from the JSON):

```json
{ "key": "part3_total_max_seconds", "value": "60", "source": "SCENE_INHERITED" }
```

The same entries, with the identity/selection values, are saved as `session_config.used.json` in the session folder: the configuration the session actually
used (never a re-read of the external file, which is not touched).

`events.csv` carries `group=<index>` in the Part 3 assignment row, zero-based like everything else. CSV column layouts are unchanged.

## 7. Compatibility notes

* The External JSON schema is unchanged (`schemaVersion` 1): existing five-field files still load, no field was added, no optional field became required.
* Group numbering changed: Group Number now equals the zero-based Group Index (Part 2: 0–1, Part 3: 0–11). Sessions recorded before manifest schema 8 used a
  different (one-based) `part3_group_number`; the project is in testing, so no migration or legacy reader exists. All newly generated sessions use schema 8.
* An out-of-range group index of the **selected** Part blocks the session in Internal mode as well (it is never clamped); an out-of-range index of a **non-selected**
  Part blocks nothing.
* Files containing keys outside the schema above are rejected.
* The experiment-condition values that are not in the JSON (timings, per-Part NPC flags, `allowWrongCandidateSelection`, `restrictRiverTeleportToPart3`, spawner and
  Agent Profile settings) are read from the saved Scene when the settings are frozen. Fix and save them in the Scene before formal sessions and check `setting_entries`.
