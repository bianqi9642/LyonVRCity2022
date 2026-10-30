# Capture schema reference (schema_version 7): NPC streams, events, gaze, trajectory, phases, timing, reliability, manifest

Reference for `npc_state.csv`, `npc_events.csv`, `npc_lifetimes.csv` and `npc_profiles.csv`, derived from the write paths in
`Assets/Scripts/Capture/DataCaptureManager.cs` (`SampleNpcs`, `AppendNpcEventLine`, `WriteLifetimeRow`, `WriteProfileRegistryRow`) and the headers in
`CaptureConstants.cs`. Column order below is the file order. **Compatibility:** schema v2 -> v3 only *appended* columns (state +3, lifetimes +25) and added
`npc_profiles.csv`; no column was renamed or reordered. Historical files were not rewritten; use `schema_version` in `session_manifest.json` to tell them apart
(v2 files have fewer columns, still end with the same prefix, and contain `npc_leg_start` rows).
**v3 -> v4 is a semantic change only (no column added or removed):** `events.csv` no longer contains NPC-subject rows (§1a). Recordings made with `schema_version` <= 3 may contain
`npc_spawn` / `npc_despawn` rows (with `npc_id` / `npc_spawn_instance_id` filled) in `events.csv`; those rows are duplicated, with the same `t_abs`, in `npc_events.csv` of the same session.
**v4 -> v5** (gaze / trajectory / manifest; see §11-§16): `gaze.csv` +11 appended columns and invalid vectors now blank; `trajectory.csv` +2 appended columns (`part`, `phase`);
`TARGET_BUILDING` now means the CURRENT target; Part 2 gained phase labels; the manifest gained stream-health and achieved-rate evidence and its legacy `*_ok` flags changed meaning.
**v5 -> v6** (`events.csv` / timing; see §17-§19): `events.csv` + trailing `interaction_id`; `value_num` now distinguishes a real 0 from "no value"; Parts 1 and 2 timers use the same monotonic clock as Part 3.
**v6 -> v7** (reliability; see §20-§23): `npc_state.csv` + trailing `los_status` and a corrected `los_clear_to_participant`; `session_id` / folder name no longer contain the participant ID (`<UTC>_<12 hex of run_guid>`); new diagnostic file `capture_errors.csv`;
`StreamHealth` blocks gain failure diagnostics; the manifest gains `manifest_revision`, identity/config fields and LOS counts and is written atomically.
`schema_version` < 7 files: `los_clear_to_participant` was 0 for most rows because the query ran from the participant to a point inside the NPC's own colliders (treat it as uninformative); folders are `<participant>_<UTC>[-rN]`; no `capture_errors.csv`; a failed stream stayed silently disabled.
`schema_version` < 6 files: no `interaction_id` column (match a click to its outcome by order/target/time only, with the ambiguity that implies), and a numeric 0 in `value_num` was written as blank (zero and missing are indistinguishable - `app_pause`/`app_focus`
resume/loss rows, zero elapsed times; treat a blank `value_num` on those event types as 0).
`schema_version` < 5 files: `gaze.csv` has 55 columns and writes fabricated default vectors for invalid eyes; `TARGET_BUILDING` meant "D1 or D2"; Part 2 rows have phase `idle`; `gaze_ok` only meant "recorder exists".
Historical files were not rewritten: when analysing mixed data, drop NPC rows from `events.csv` by `event_type` (the list in §1a) and take NPC events from `npc_events.csv`.

## 1a. `events.csv`: participant and experiment events only

`events.csv` is the **participant / experiment-workflow** stream. NPC lifecycle, movement and behaviour events are written **only** to `npc_events.csv` (and the other NPC streams); nothing is mirrored.
The rule is by subject, not by name or payload: an event whose subject is an NPC is NPC data; a participant action stays in `events.csv` even if NPC activity happens in the same frame.
The classification is the explicit set `CaptureConstants.IsNpcSubjectEvent` (no name-prefix rule); `DataCaptureManager.WriteEventRow` refuses any NPC-subject type or any row that carries an NPC identity,
whichever path produced it (generic experiment callback, bus replay, late registration, buffered events), and counts refusals in `events_csv_npc_rows_rejected` (manifest; expected 0).

| Category | `event_type` values (as written) | Notes |
|---|---|---|
| Session | `session_start`, `session_end` (`value_str` = end reason), `app_pause`, `app_focus` (`value_num` 1/0) | recorder lifecycle; pause/focus describe gaps in the participant's data |
| Part / trial / phase | `part_start`, `part_end`, `recording_marker`, `trial_start`, `trial_end`, `phase_start`, `end_panel_visible`, Part 3: `phase_result`, `phase_timeout`, `phase_end` | workflow transitions |
| Panels | `panel_shown`, `panel_confirmed`, `destination_shown`, `destination_confirmed` | participant-facing UI |
| Selection | `target_assigned`, `candidate_interactable_on/off`, `building_click`, `correct_selection`, `incorrect_selection` | participant actions and outcomes (`value_num` = response time where the existing event defines it) |
| Locomotion | `teleport`, `river_crossing` (+ `teleport_rejected` where raised) | `from_pos_*` / `to_pos_*` filled |
| NPC warm-up window | `npc_warmup_start`, `npc_warmup_end` | **Workflow markers**: they delimit the pre-trial window in which the participant waits while NPCs already walk. They carry no NPC identity, are raised by `ExperimentManager`, and are written regardless of `npcCaptureEnabled`. The NPC files label the same window with `capture_period = warmup`. |

Columns: `t_abs, t_unix_ms, event_type, part, phase, label, building_id, target_id, npc_id, npc_spawn_instance_id, from_pos_x..to_pos_z, value_num, value_str, session_id, interaction_id` (v6 appended `interaction_id`; nothing moved).
`t_abs` = `Time.realtimeSinceStartupAsDouble` at the event (same clock as every stream), `t_unix_ms` = UTC ms. Blank = not applicable (`value_num` blank rather than 0, positions blank when the event has none).
**`npc_id` and `npc_spawn_instance_id` are kept for compatibility and are always blank from `schema_version` 4.** NPC capture can be off while all of the above is recorded normally; the participant stream never depends on `npcCaptureEnabled`.
Join a participant event to NPC data by time (`t_abs`) and `session_id`, or by `gaze.csv` `hit_npc_spawn_instance_id`; there is no NPC key in `events.csv`.

## 1. What each file is

| File | Grain | Purpose | Written when |
|---|---|---|---|
| `events.csv` | one row per participant / workflow event | see §1a. **Not** an NPC file. | at each event |
| `npc_lifetimes.csv` | one row per NPC **lifetime** | Attributes that are constant for a lifetime: identity, provenance times, itinerary, configured speeds, profile and *realised* appearance, uncertain parameters. Immutable dimension table. | once, at the spawn boundary (`Initialize`) |
| `npc_events.csv` | one row per **event** | Lifecycle transitions and observed changes, stamped with the time they happened. | at each transition (buffered, flushed every frame and at lifecycle boundaries) |
| `npc_state.csv` | one row per **active NPC per snapshot** | Position / motion time series. All active NPCs share one `t_abs` per snapshot. | every `npcStateSamplingIntervalSeconds` (budget-adjusted) while the NPC master switch is on |
| `npc_profiles.csv` | one row per distinct **profile content hash** per session | Full JSON description of an AgentProfile so a hash can be reconstructed even if the asset is edited later. | first lifetime that uses that content |

Join key everywhere: **`(session_id, npc_spawn_instance_id)`**. `gaze.csv` carries the same pair (`hit_npc_spawn_instance_id`).
State rows are ordered by `(npc_spawn_instance_id, sample_seq)` or `(t_abs)`; they are never merged across lifetimes.

## 2. Identity, time, missing values

* **`session_id`** - session folder name `<participant>_<UTC stamp>[-r<n>]`; unique per run (`-r<n>` if two sessions start in the same second). Scope: session.
* **`npc_id`** (= `AgentID`) - `NPC-000022`; the stable *pooled GameObject*. Reused by many lifetimes. Never a lifetime key on its own.
* **`npc_spawn_instance_id`** - `NPC-000022-S2`; one ACTIVE lifetime of that object (`PedestrianAgent.BeginLifetime`). Prewarm / pool events have it blank.
* **`lifetime_index`** - `n` in `-S<n>`; counts this object's lifetimes from 1. Blank where there is no lifetime.
* **`sample_seq`** - position-snapshot counter *per lifetime*, from 1 at the lifetime's first `npc_state` row; restarts for the next lifetime.
* **`t_abs`** - `Time.realtimeSinceStartupAsDouble` seconds, 4 decimals; monotonic, the merge key across all recorders. **`t_unix_ms`** - UTC epoch milliseconds (long) at the same moment.
  Event rows carry the moment the transition happened (also for events replayed from the pre-recorder queue); state rows carry the snapshot start time.
* Numbers: `InvariantCulture`, fixed 4 decimals (`F4`), except integers and 0/1 flags. Vectors are split into `_x/_y/_z` (Unity world axes, metres, y up).
* **Missing values:** blank means *not applicable / not available / unknown*; `0` is never a stand-in. Per-column exceptions are stated in the tables.
* CSV: RFC 4180 - a field containing `,` `"` or a line break is quoted with doubled quotes; upstream sanitising already turns commas/line breaks in names into spaces.
* Terminology: **configured** = a setting handed to the NPC (profile roll, inspector value); **applied** = what the component is using now; **observed** = measured from the simulation.

## 3. `npc_state.csv`

| # | Field | Type | Unit | Definition | Source | Trigger / temporal meaning | Missing | Join |
|---|---|---|---|---|---|---|---|---|
| 1 | `t_abs` | float | s | Time of the snapshot (taken once, shared by all rows of the snapshot) | `CaptureClock.TAbs` | each snapshot | never | `gaze.csv`/`trajectory.csv` `t_abs` |
| 2 | `t_unix_ms` | int64 | ms | Same moment, UTC wall clock | `CaptureClock.TUnixMs` | each snapshot | never | - |
| 3 | `frame_index` | int | frame | Unity frame in which the snapshot ran | `Time.frameCount` | each snapshot | never | `trajectory.csv` |
| 4 | `npc_id` | string | - | `AgentID` of the pooled object | `PedestrianAgent.AgentID` | each row | never | lifetimes, events |
| 5 | `npc_spawn_instance_id` | string | - | Lifetime ID | `PedestrianAgent.LifetimeId` (registry copy) | each row | never | all NPC files + gaze |
| 6-8 | `pos_x,pos_y,pos_z` | float | m | World position of the NPC root | `Transform.position` | sample (observed) | never | - |
| 9 | `yaw` | float | deg | World Y rotation (0-360) | `Transform.eulerAngles.y` | sample (observed) | never | - |
| 10-12 | `vel_x,vel_y,vel_z` | float | m/s | NavMeshAgent velocity vector (observed) | `NavMeshAgent.velocity` | sample | `0` while the agent is off the NavMesh (`nav_on_mesh=0`) - this is a defined fill value, not a measurement | - |
| 13-15 | `nav_dest_x,y,z` | float | m | The NavMeshAgent's **current destination** (snapped onto the NavMesh) | `NavMeshAgent.destination` | sample (current state) | equals `pos` while off the NavMesh; a destination equal to `pos` is therefore ambiguous only when `nav_on_mesh=0` | `npc_events.dest_*` |
| 16 | `state` | enum | - | Itinerary-level state of `PedestrianAgent`: `moving` / `dwelling` (`WaitingAtAttractor`) / `finished` / `unknown` | `PedestrianAgent.CurrentState` via `CaptureConstants.NpcStateToString` | sample (current state) | never | - |
| 17 | `speed` | float | m/s | **Observed** speed = magnitude of `vel` | `NavMeshAgent.velocity.magnitude` | sample | `0` off the NavMesh (defined fill) | compare with `configured_speed` |
| 18-19 | `agent_desired_vel_x,z` | float | m/s | Velocity the agent *wants* (before avoidance / acceleration limits), horizontal | `NavMeshAgent.desiredVelocity` | sample | `0` off the NavMesh | - |
| 20-22 | `mt_target_x,y,z` | float | m | The mover's current target (transform position if a target transform is set, else the target point) | `MoveToTarget.targetTransform` / `targetPosition` | sample (current state) | equals `pos` if there is no mover | `target_id`, `npc_events.dest_*` |
| 23 | `nav_has_path` | 0/1 | - | Agent currently holds a path | `NavMeshAgent.hasPath` | sample | `0` off mesh | - |
| 24 | `nav_path_pending` | 0/1 | - | Path computation not finished | `NavMeshAgent.pathPending` | sample | `0` off mesh | - |
| 25 | `los_clear_to_participant` | 0/1 | - | `1` = confirmed clear line of sight NPC eye -> participant head; `0` = a genuine obstacle blocks it; **blank** = could not be answered (see `los_status`). Definition in §20 | `NpcLineOfSight.Query` | sample | **blank** whenever `los_status` is not `clear`/`blocked` (never 0) | - |
| 26 | `session_id` | string | - | see §2 | `DataCaptureManager.SessionId` | each row | never | all files |
| 27 | `part` | string | - | Part running when the snapshot was taken (`Part1/2/3`) | `DataCaptureManager.CurrentPart` | sample | never | `events.csv` |
| 28 | `phase` | string | - | Part sub-phase (`idle`, Part 3: `d1_pending`, `d1_search`, `d2_pending`, `d2_search`, `ended`) | `CurrentPhase` | sample | never | `events.csv` |
| 29 | `capture_period` | enum | - | `warmup` (NPC warm-up window open) / `trial` (between OnTrialStart/End) / `pre_trial` (otherwise: Start-panel wait etc.) | `CapturePeriod()` | sample | never | - |
| 30 | `sample_seq` | int | - | see §2 | registry counter | each row | never | - |
| 31 | `lifetime_index` | int | - | see §2 | `PedestrianAgent.LifetimeIndex` | each row | never | lifetimes |
| 32-35 | `rot_x,rot_y,rot_z,rot_w` | float | quaternion | World rotation (full orientation; `yaw` is its Euler Y) | `Transform.rotation` | sample | never | - |
| 36 | `nav_agent_speed` | float | m/s | **Configured/current NavMeshAgent speed limit** (what the agent is *allowed* to do now: normal speed, or the reduced value during an UncertainBehavior slow-walk). **Not** measured speed | `NavMeshAgent.speed` | sample (current setting) | blank off the NavMesh | `npc_lifetimes.configured_speed` |
| 37 | `itinerary_index` | int | - | Index of the itinerary entry the NPC is heading to / dwelling at (0 = entrance) | `PedestrianAgent.CurrentItineraryIndex` | sample | - | `itinerary_ids` in lifetimes |
| 38 | `itinerary_count` | int | - | Number of entries of this lifetime's itinerary | `PedestrianAgent.ItineraryCount` | sample | `0` only before an itinerary exists | lifetimes |
| 39 | `target_id` | string | - | Name of the itinerary entry at `itinerary_index` | `PedestrianAgent.CurrentTargetName` | sample | blank if out of range | events `target_id` |
| 40 | `nav_on_mesh` | 0/1 | - | Agent active and on the NavMesh at the sample | `NavMeshAgent.isOnNavMesh` | sample | never | - |
| 41 | `uncertain_enabled` | 0/1 | - | **Configured**: the `UncertainBehavior` component is enabled for this lifetime (the profile roll `forceUncertain` or `uncertainProbability`, applied by `AgentFactory`). Constant within a lifetime | `UncertainBehavior.isActiveAndEnabled` | sample | **blank** when the prefab has no `UncertainBehavior` component (not applicable) | `npc_lifetimes.uncertain_behavior_enabled` |
| 42 | `uncertain_state` | enum | - | **Runtime** state of the UncertainBehavior state machine at the sample: `walking` / `pause` / `hesitate` / `slow` / `detour` / `suspended_arrived` (see §7) | `UncertainBehavior.StateLabel` | sample (current state) | **blank iff `uncertain_enabled` is `0` or blank** (not applicable); never blank for an uncertain NPC | `npc_events` `npc_uncertain_state` |
| 43 | `uncertain_look_phase` | enum | - | Look-around sub-phase of `hesitate`: `idle` / `turning` / `holding` / `returning` | `UncertainBehavior.LookPhaseLabel` | sample | blank unless `uncertain_state=hesitate` with look-around on | - |
| 44 | `los_status` | enum | - | Why `los_clear_to_participant` has its value: `clear`, `blocked`, `missing_participant_target`, `missing_npc_origin`, `invalid_geometry`, `hit_buffer_overflow`, `query_error` (only conditions the code can really produce) | `NpcLineOfSight.Name` | sample | never blank | - |

Snapshot semantics: every NPC with an open lifetime is in every snapshot (no sub-sampling). The interval between snapshots is `npcStateSamplingIntervalSeconds`, stretched only by the
data budget (see §9); derive the true frequency from `t_abs`. Inactive pooled objects never appear. Rows stop at the End-panel freeze.

## 4. `npc_events.csv`

This is the **authoritative stream for every NPC-subject event** (the table below is the complete list, and the exact set in `CaptureConstants.IsNpcSubjectEvent`). Each row links to `npc_lifetimes.csv`
and `npc_state.csv` by `(session_id, npc_spawn_instance_id)` (blank for pool events that precede a lifetime), and to the participant timeline by `t_abs`.

Columns: `t_abs, t_unix_ms, npc_id, npc_spawn_instance_id, event_type, pos_x, pos_y, pos_z, yaw, dest_x, dest_y, dest_z, speed_setting, path_corners, value_str, session_id, part, phase, lifetime_index, itinerary_index, target_id`

| # | Field | Type / unit | Definition and source | Missing |
|---|---|---|---|---|
| 1-2 | `t_abs`, `t_unix_ms` | float s / int64 ms | **Event time** - the moment the transition happened (`CaptureClock` stamped by the emitting component). For `npc_dest_change` it is the *snapshot* time at which the change was observed | never |
| 3 | `npc_id` | string | `AgentID` | never |
| 4 | `npc_spawn_instance_id` | string | Lifetime ID of the event. For `npc_pool_return` / `npc_despawn` it is the lifetime that just ended | blank for `prewarm_created`, `pool_object_created`, `pool_acquired` (no lifetime yet) |
| 5 | `event_type` | enum | see table below | never |
| 6-9 | `pos_x,y,z`, `yaw` | m, deg | NPC position/yaw **at the event**. For `npc_entrance_teleport` it is the position **before** the warp | never |
| 10-12 | `dest_x,y,z` | m | Meaning depends on `event_type` (table below): nav destination, itinerary target position, or the position **after** a teleport | blank when the event has no destination |
| 13 | `speed_setting` | float m/s | Written only on `npc_spawn`: `NavMeshAgent.speed` at registration (**configured**) | blank on every other event |
| 14 | `path_corners` | string | Only on `npc_spawn`: current NavMesh path corners `x;y;z` joined by `\|` | blank otherwise |
| 15 | `value_str` | string | `key=value;key=value` detail (no commas) - per event below | may be blank |
| 16-18 | `session_id`, `part`, `phase` | string | session and Part/phase **at the time the row was written** | never |
| 19 | `lifetime_index` | int | `n` of the lifetime | blank without a lifetime |
| 20 | `itinerary_index` | int | Itinerary entry index at the event | blank when n/a |
| 21 | `target_id` | string | Itinerary entry name at the event | blank when n/a |

| `event_type` | Real trigger (code) | `dest_*` | `value_str` |
|---|---|---|---|
| `prewarm_created` | `SpawnerManager.PrewarmPoolCoroutine` created an INACTIVE pool object | - | `prefab=..;object=..;index=..;kind=prewarm;active_npc=0` |
| `pool_object_created` | pool was empty, object instantiated at spawn time | - | same keys, `kind=runtime_instantiate` |
| `pool_acquired` | object taken from the pool for a new lifetime (`RecordPoolAcquired`) | - | `pool_size=..` |
| `npc_spawn` | lifetime begins, in `PedestrianAgent.Initialize`, before any movement command | - | `source=pool\|new` |
| `npc_movement_start` | first leg of the lifetime handed to `MoveToTarget` (once per lifetime) | itinerary target position | `mode=..;itinerary_count=..` |
| `npc_entrance_teleport` | NPC warped to its entrance placement | position after | `entrance=<name>` |
| `npc_target_set` | an itinerary leg was handed to `MoveToTarget` (once per new itinerary index) - the **exact-time, itinerary-level** event | itinerary target position | `kind=itinerary_leg` |
| `npc_arrived` | itinerary waypoint reached | - | `kind=exit\|attractor\|waypoint;index=..` |
| `npc_dwell_start` / `npc_dwell_end` | dwell (or capacity wait) at an attractor begins / ends | - | `dwell_s=..` \| `capacity_wait=1` \| `capacity_timeout=1` |
| `npc_dest_change` | the sampler **observed** the NavMeshAgent destination move by more than 0.5 m since the previous snapshot (see §6) | new nav destination | `cause=itinerary_target\|detour\|other;jump_m=..` |
| `npc_uncertain_state` | `UncertainBehavior.StateLabel` changed (see §7) | - | `from=..;to=..;planned_s=..` |
| `npc_itinerary_complete` | itinerary finished / force-finished - the lifetime ends | - | `reason=itinerary_complete\|force_finish\|no_itinerary\|entrance_placement_failed` |
| `npc_despawn` | population count decremented (`SpawnerManager.OnAgentFinished`) | - | `lifetime_index=..;spawn_t_abs=..;live_agents=..` |
| `npc_pool_return` | inactive object is back in the pool | - | `pool_size=..` |
| `npc_lifetime_open_at_end` | NPCs frozen at the End panel / session end while still alive (no despawn follows) | - | `reason=..` |
| `npc_lifetime_superseded` | diagnostic: a lifetime was replaced without a despawn | - | `replaced_by=..` |
| ~~`npc_leg_start`~~ | **retired in v3** (see §6); present only in v2 files | | |

## 5. `npc_lifetimes.csv`

One row per lifetime, written at registration (normally the spawn boundary). `registration=late` marks a lifetime registered after the fact by the census safety net; its
`spawn_t_abs` is still the true spawn time. All "applied" values below were **frozen when the lifetime began** (`NpcLifetimeProfile`), so re-skinning the pooled object later cannot change them.

| # | Field | Type / unit | Definition | Source | Temporal meaning | Missing |
|---|---|---|---|---|---|---|
| 1 | `session_id` | string | see §2 | `SessionId` | - | never |
| 2 | `npc_spawn_instance_id` | string | lifetime ID | `PedestrianAgent.LifetimeId` | - | never |
| 3 | `npc_id` | string | `AgentID` | `PedestrianAgent.AgentID` | - | never |
| 4 | `lifetime_index` | int | `n` | `LifetimeIndex` | - | never |
| 5-6 | `part`, `phase` | string | Part/phase at registration | `CurrentPart/Phase` | registration time | never |
| 7 | `capture_period_at_spawn` | enum | `warmup`/`pre_trial`/`trial` at **registration** (= spawn unless `registration=late`) | `CapturePeriod()` | registration | never |
| 8-9 | `prefab_name`, `object_name` | string | prefab name (clone/ID suffix stripped); full GameObject name | `GameObject.name` | - | - |
| 10 | `object_created_t_abs` | float s | When the pooled object was instantiated | `PedestrianAgent.ObjectCreatedTAbs` | object creation (prewarm or runtime) | blank if unknown |
| 11 | `object_created_kind` | enum | `prewarm` / `runtime_instantiate` | `ObjectCreatedKind` | - | blank if unknown |
| 12 | `pool_acquired_t_abs` | float s | When the spawner took the object for this lifetime | `PoolAcquiredTAbs` | acquisition | blank if unknown |
| 13-14 | `spawn_t_abs`, `spawn_t_unix_ms` | float s / int64 ms | **True spawn time** (`BeginLifetime`) | `LifetimeSpawnTAbs/UnixMs` | spawn | never |
| 15 | `registered_t_abs` | float s | When the recorder registered it (>= spawn; larger only for `late`) | `CaptureClock.TAbs` | registration | never |
| 16 | `registration` | enum | `spawn` / `late` | - | - | never |
| 17 | `spawn_source` | enum | `pool` (reused object) / `new` (instantiated) | `PedestrianAgent.SpawnSource` | - | blank if unknown |
| 18-20 | `spawn_x,y,z` | m | NPC position at registration - the spawner has already placed it at the resolved entrance sample; a later warp is recorded as `npc_entrance_teleport` | `Transform.position` | registration | never |
| 21 | `movement_mode` | enum | `Part3AttractorItinerary` / `Part2WanderingItinerary` - the **configured** movement style of the lifetime | `PedestrianAgent.MovementMode` | constant | never |
| 22 | `itinerary_count` | int | Entries in the itinerary | `ItineraryTransformsView.Count` | constant | `0` = none |
| 23-25 | `entrance_id`, `first_target_id`, `final_target_id` | string | Names of itinerary entries `[0]`, `[1]`, `[n-1]` | itinerary | constant | blank if absent |
| 26 | `itinerary_ids` | string | All entry names, `\|`-joined, in order | itinerary | constant | blank if empty |
| 27 | `configured_speed` | float m/s | **Applied** normal walking speed = `profile.speed x speed_multiplier`, handed to `MoveToTarget.SetNormalSpeed` | `PedestrianAgent.ConfiguredSpeed` | constant per lifetime | **blank when no profile was applied** (unknown, not 0) |
| 28 | `profile_name` | string | `AgentProfile` asset name | `profile.name` | - | blank if no profile |
| 29 | `profile_base_speed` | float m/s | `profile.speed` | profile | - | blank if no profile |
| 30 | `speed_multiplier` | float | Realised draw from `speedOptions` (1 if none) | `AgentProfile.ChooseWeightedSpeedMultiplier` | realised once per lifetime | blank if no profile |
| 31 | `uncertain_behavior_enabled` | 0/1 | **Configured**: UncertainBehavior enabled for this lifetime (profile roll) | `uncertain.enabled` after `AgentFactory` | constant | `0` also when the prefab has no component |
| 32 | `gender` | enum | `Male`/`Female` - realised gender roll of THIS lifetime | `AppliedAppearance.gender` | realised | blank if no profile/appearance was applied |
| 33 | `dwell_time_s` | float s | Configured dwell at each attractor visit (Part 3) | `PedestrianAgent.DwellSeconds` | constant | **blank in Part 2** |
| 34 | `wander_look_ahead_m` | float m | Part 2 look-ahead distance for issuing the next waypoint | `WanderLookAheadDistance` | constant | blank in Part 3 |
| 35-39 | `nav_radius, nav_height, nav_acceleration, nav_angular_speed, nav_avoidance_priority` | m, m, m/s^2, deg/s, int | NavMeshAgent values in force at registration (applied by the profile) | `NavMeshAgent` | registration | blank without a NavMeshAgent |
| 40 | `profile_content_hash` | 16-hex | FNV-1a 64 hash of the profile's content (scalars, speed options, every part option: index, mesh name, weight, gender restriction, material names). Same content -> same hash, any instance | `AgentProfileSnapshot.Describe` | frozen at lifetime start | blank if no profile. Resolve in `npc_profiles.csv` |
| 41-43 | `profile_female_probability`, `profile_force_uncertain`, `profile_uncertain_probability` | float / 0-1 / float | Profile parameters that decide gender and whether the NPC is uncertain (the *inputs* of the realised draws) | `AgentProfile` | frozen | blank if no profile |
| 44 | `uncertain_config` | string | UncertainBehavior parameters in force: per-second probabilities, durations, slow factor, detour radius/timeout, look-around flag | `UncertainBehavior` fields | frozen | **blank unless `uncertain_behavior_enabled=1`** |
| 45 | `appearance_hash` | 16-hex | Hash of the **realised look**: gender + per-part (option index, mesh, materials). Equal look -> equal hash | `AppliedAppearance.LookHash` | realised | blank if no appearance applied |
| 46 | `appearance_applied_t_abs` | float s | Time of the `ApplyProfile` call that produced this look (the last one before the lifetime began) | `AppliedAppearance.appliedTAbs` | apply time | blank if none |
| 47-64 | `app_<part>_option`, `app_<part>_mesh`, `app_<part>_materials` for `<part>` = `body, hair, top, bottom, shoes, accessories` | int / string / string | **Realised** choice per part. `option` = index into the profile's option array of the chosen option (`-1` = options exist but none was eligible for the gender, renderer cleared; blank = the profile offers no option, the prefab's own mesh stays). `mesh` = mesh shown after application. `materials` = material **assets** applied per renderer slot (`\|`-joined, slot order; runtime "(Instance)" suffix removed) - each slot is an independent random draw | `AppearanceController.LastApplied` | realised | **all three blank when the prefab has no renderer for that part** (the shipped `Npc.prefab` has no body renderer and its profile has no body options) |

Appearance can change only by another `ApplyProfile`, which happens only between lifetimes (pool reuse); there is no mid-lifetime appearance change in the code, so no appearance event exists.
Per-slot material indices are not columns: material names identify the asset; if two materials of an option share a name, `npc_profiles.csv` (index + order) disambiguates them.

## 6. `npc_dest_change` and `npc_leg_start`

* **`npc_dest_change`** is an **observed** event written by the 10 Hz sampler (`SampleNpcs`): the NavMeshAgent destination moved by more than 0.5 m since the previous snapshot of that lifetime.
  Its `t_abs` is the snapshot time (resolution = sampling interval, which the data budget can stretch; two changes inside one interval collapse into one row; a leg that ends before the
  next snapshot is never observed). `dest_*` = the new destination; `value_str` = `cause=` (`detour`: UncertainBehavior detour in progress; `itinerary_target`: the new destination is within 1.5 m of the current
  itinerary entry; `other`: anything else, e.g. entrance placement, wander re-targeting) and `jump_m` (distance from the previous destination). A re-issued *identical* destination, or a move under 0.5 m, writes nothing.
* **`npc_target_set`** is the **exact-time** itinerary event: `PedestrianAgent` handed a leg to the mover (once per itinerary index). `npc_movement_start` is the first of them.
* **`npc_leg_start`** was emitted by the same `if` block as `npc_dest_change` with identical fields (`SampleNpcs`, up to schema v2): same lifetime, same `t_abs`, same positions, same `target_id`, only the name differed. It carried no information
  that `npc_dest_change` + `npc_target_set` do not, so it was **retired in v3** (constant kept, never emitted). Use `npc_target_set` to find leg starts exactly, `npc_dest_change` to find observed destination changes (including detours).
  In v2 files every `npc_dest_change` row has an `npc_leg_start` twin; ignore the twin.

## 7. UncertainBehavior (what is recorded and what the states mean)

Derived from `UncertainBehavior.cs`. Uncertainty acts on **movement execution** (pausing, turning the head, slowing) and on the **short-term route** (a detour to a nearby NavMesh point), **never on the itinerary / destination choice**:
the itinerary is generated once per lifetime by `SpawnerManager`, and a detour does not change `itinerary_index`.

| `uncertain_state` | Meaning (entered by) | Effect on the mover |
|---|---|---|
| `walking` | default; random events are drawn per second | normal |
| `pause` | `EnterPause` (also the fallback of a detour with no valid point, or a detour with exhausted arrival retries) | `PauseExternal(true, true)`: full halt, animator frozen |
| `hesitate` | `EnterHesitate` | `PauseExternal(false, false)`: soft pause (no hard halt, animator keeps running) plus a head look-around (`uncertain_look_phase`) |
| `slow` | `EnterSlow` | `SetTemporarySpeed(slowFactor x normal)` - `nav_agent_speed` shows the reduced limit |
| `detour` | `EnterDetour` | `MoveTo(<sampled NavMesh point within detourRadius>)`; the main target is restored when it ends |
| `suspended_arrived` | `SuspendBecauseArrived`: the NPC reached its main waypoint / dwells; the uncertain logic is parked | none |

`uncertain_enabled` (configured, per lifetime) is not the same as `uncertain_state` (runtime). A direct (non-uncertain) NPC has `uncertain_enabled=0`, blank `uncertain_state`.
`movement_mode` in `npc_lifetimes.csv` is the other configured style (attractor itinerary vs wandering itinerary) and is independent of uncertainty. Transitions are `npc_uncertain_state` events (one per label change, `from`/`to`, `planned_s` = timer of the entered state, 0 for walking / suspended); `ResetForReuse` at a new lifetime re-baselines silently and never emits.

Known quirk (existing behaviour, left unchanged): `SuspendBecauseArrived` does not reset the underlying `state`; after `ResumeAfterArrival` a previously active `pause`/`slow`/`hesitate` can still show until its own timer expires, even though the mover already resumed. The recorder reports the state machine as it is.

## 8. Master switch: `npcCaptureEnabled`

`ExperimentManager.PartCaptureConfig.npcCaptureEnabled` (one per Part: Part 1 off, Part 2 off, Part 3 on in the shipped scene) is the **only** switch for NPC-specific research data. `DataCaptureManager.NpcRecordingEnabled` resolves it: the running Part's config, or - before the first Part has started (pool
prewarm happens then) - the config of the Part selected in `ExternalSessionController`; unknown = off. It is installed as the `NpcLifecycleBus` gate in `Awake`, before any prewarm event can be raised.

| `npcCaptureEnabled` | Behaviour |
|---|---|
| **false** | No rows in `npc_state.csv`, `npc_events.csv`, `npc_lifetimes.csv`, `npc_profiles.csv` (the files are not even created). `events.csv` is unaffected (it never holds NPC rows; participant events and the warm-up window markers are still written). `hit_npc_id` and `hit_npc_spawn_instance_id` are blank in `gaze.csv`. Lifecycle events are **discarded at raise time** - never queued, never replayed (the manifest only counts them: `npc_events_suppressed_by_master_switch`). The NPC simulation, gaze rows and trajectory rows are unaffected. |
| **true** | Prewarm / pool events, every lifetime's spawn, attribute row, events and position snapshots are recorded from the moment they happen (warm-up, Start-panel wait, pre-trial included). Trial state is **not** a gate; the only stop is the End-panel freeze. |

**Gaze vs. NPC recording.** `gaze.csv` keeps being written under the Part's `gazeCaptureEnabled`. `hit_semantic_class` stays `NPC` when a ray hits an NPC collider, but the NPC identity columns are blank when NPC capture is off - "identity unavailable by configuration", **not** "not looking at an NPC".
Switching the flag while NPCs of the previous Part are alive is not supported (they are not back-registered).

## 9. State sampling interval and data budget (`DataCaptureManager` Inspector)

| Field | Default | Meaning |
|---|---|---|
| `npcStateSamplingIntervalSeconds` | 0.1 | Seconds between complete snapshots (0.05 = 20 Hz, 0.1 = 10 Hz, 0.2 = 5 Hz). NaN / infinite / <= 0 -> 0.1; < 0.01 -> 0.01. Gaze / trajectory rates are separate. |
| `npcStateDataBudgetMBPerMinute` | 0 | 0 = unlimited. > 0 = target `npc_state.csv` volume in decimal MB/min. NaN / infinite / negative -> 0 with a warning. |
| `npcStateMaxIntervalSeconds` | 2 | Upper bound of the budget-driven interval (raised to the base interval if smaller). |

Rule (`NpcStateRateController`): measured bytes per row (EMA) x active NPCs predicts the next snapshot; required interval `T = bytes x 60 / (budget x 1e6)`. Rising demand -> `1.05 x T` at once; falling demand decays x0.7 per snapshot only when `1.05 x T` is >20 % below the current interval; clamped to
`[base, max]`. If `1.05 x T` exceeds the maximum the budget is *unachievable*: the interval stays at the maximum, one warning is logged (then at most once a minute) and the manifest records `exceeded_unachievable` with the real observed rate. No NPC is ever left out of a snapshot. The budget covers `npc_state.csv` only; lifecycle events, `npc_events.csv`,
`npc_lifetimes.csv`, `npc_profiles.csv`, `events.csv`, headers and the manifest are never throttled. Manifest keys: `events_csv_scope`, `events_csv_npc_rows_rejected` (routing guard, expected 0), `npc_state_base_interval_seconds`, `npc_state_max_interval_seconds`, `npc_state_data_budget_mb_per_minute`, `npc_state_budget_scope`, `npc_master_switch_semantics`, `npc_capture_effective_enabled`,
`npc_events_suppressed_by_master_switch`, `npc_state_snapshots`, `npc_state_rows`, `npc_state_bytes_written`, `npc_state_max_active_npcs`, `npc_state_effective_interval_{last,min,max}_seconds`, `npc_state_actual_hz_mean`, `npc_state_observed_mb_per_minute_{last,peak,session_mean}`, `npc_state_budget_status`, `npc_state_throttled_snapshots`, `npc_state_budget_exceeded_snapshots`.

## 10. Storage layout decision: one long-format `npc_state.csv` per session

Evaluated against the implementation (`CaptureCsvWriter` = lazy file, open/append/close per batch) and measured in the test `StorageLayout_OneFilePerTick_Versus_OneFilePerLifetime_Measurement` (Editor, macOS, 300 NPCs, 20 ticks):

| | A: one file (current) | B: file per lifetime | C: partitioned (rotation) |
|---|---|---|---|
| Write cost per tick | **0.2 ms** (one open, 300 rows) | **18.9 ms** (300 opens/closes, 1 row each) - ~85x | as A |
| File count | 1 | one per lifetime: thousands per session (pooled reuse multiplies it), 301 after 20 ticks in the test | 1 per partition |
| Crash resilience | each tick is one append; at most the in-flight batch is lost | same per file, but a headset removal can leave thousands of files open-ended at once | same as A |
| Joins (pandas/R) | one `read_csv`; `groupby(npc_spawn_instance_id)` | glob + concat of thousands of files; join keys must be rebuilt from filenames | glob over few files |
| Archiving / reproducibility | 1 file per stream | huge directory, many tiny files, manifest must list them | few files, needs an index |
| Identity | `(session_id, npc_spawn_instance_id, sample_seq)` already separates trajectories | duplicates the key into the filename | same as A |

**Decision: keep Option A.** The composite lifetime ID already separates trajectories, the budget controller bounds the volume, and per-lifetime files would be ~85x more expensive per tick on this measurement and far worse for analysis. If a single file ever becomes unwieldy (the controller reports ~50 MB/min at 300 NPCs and 10 Hz),
prefer lowering the rate / using the budget, then Option C (rotate by Part or by size, e.g. `npc_state.001.csv`, with an index in the manifest and unchanged schema and keys) - not per-lifetime files. Not implemented: no concrete limitation was demonstrated. Quest storage / write throughput is unmeasured.


## 11. `gaze.csv` (schema v5)

Columns 1-55 are unchanged in order and name (`time_since_start_s` ... `hit_is_interactable`). Appended in v5, in this order:
`part, phase, ray_is_eye_gaze, ray_origin_x, ray_origin_y, ray_origin_z, ray_dir_x, ray_dir_y, ray_dir_z, hit_is_current_target, hit_assigned_destination`.

**Validity (F-16).** `left_valid` / `right_valid` = `OVREyeGaze.Confidence >= minConfidenceForValid`; `combined_valid` = at least one eye valid. These flags are the ONLY validity authority.
| Field group | Valid | Invalid / unavailable |
|---|---|---|
| `left_origin_*`, `left_dir_*` | the eye's world origin / forward (zeros are legitimate values and are written as `0.0000`) | **blank** (all 6 cells) |
| `right_origin_*`, `right_dir_*` | same | blank |
| `combined_origin_*`, `combined_dir_*` | mean of valid eyes / the single valid eye (`gaze_ray_source` = `both`/`left`/`right`) | **blank** (source `head_fallback`) |
| `left_confidence`, `right_confidence`, `combined_confidence` | measured value, `F3` | still written (`0.000` when the component is absent) - a confidence is a measurement, not a vector |

Earlier versions wrote the eye transform (or the head pose) into these columns even when invalid, so a plausible-looking vector next to `*_valid = 0` could be mistaken for gaze. Blank cells now mean "no eye-gaze vector", never zero.
**Fallback ray.** When no eye is valid the recorder still raycasts along the head-forward ray (unchanged behaviour). That ray is recorded in `ray_origin_*` / `ray_dir_*` (always filled; the ray actually cast) and marked by `ray_is_eye_gaze = 0` (and `gaze_ray_source = head_fallback`).
`ray_is_eye_gaze = 1` iff `combined_valid = 1`. All `hit_*` columns describe the raycast target regardless of the ray type, so **restrict fixation analyses to `ray_is_eye_gaze = 1`**; the legacy `roi_hit_name` / `roi_hit_distance` stay blank for invalid gaze (unchanged).
A row with `gaze_ray_source = head_fallback` is not a valid eye-gaze sample and is counted as `fallback` (never `valid`) in the manifest.

**`part` / `phase`** - `DataCaptureManager.CurrentPart` / `CurrentPhase` at the sample time (blank when no capture manager exists). See §13.
**`hit_is_current_target`** - `1`: the hit is (inside) the building that is the ACTIVE search target at this sample; `0`: anything else that was hit (other destination, earlier target, unassigned building, NPC, terrain ...); **blank**: nothing hit, or not determinable (no capture manager).
**`hit_assigned_destination`** - `D1` / `D2` when the hit building is one of the session's assigned destinations (whether or not it is current), else blank.

## 12. AOI classes and the current target (F-05)

`hit_semantic_class` taxonomy is unchanged: `NPC, TARGET_BUILDING, CANDIDATE_BUILDING, BUILDING, TERRAIN, WATER, UI, OTHER, NONE`. Only the meaning of `TARGET_BUILDING` changed: it is **the currently active target**,
`DataCaptureManager.CurrentTargetBuilding` = (Part 3 phase `d1_search` -> assigned D1, `d2_search` -> assigned D2) AND that building's `TargetSelectable` is enabled right now. It is derived from live state on every sample by object reference (never from a name, never cached);
the collider cache holds only target-independent facts. D1 and D2 stay assigned for the whole session, so: during `d1_search`, D1 is `TARGET_BUILDING` / `hit_is_current_target = 1`, D2 is `CANDIDATE_BUILDING` (or `BUILDING`) with `hit_is_current_target = 0` and `hit_assigned_destination = D2`;
during `d1_pending` / `d2_pending` (panel reading) and after a search ended neither is current. `hit_building_id` is still the GameObject name; `hit_semantic_class`, `hit_building_id`, `hit_is_current_target` and `hit_assigned_destination` are evaluated together for the same sample.

## 13. Phase labels (F-06)

`phase` is the experiment sub-phase in force at the row's own timestamp (set by `ExperimentManager.SetPhase`, relayed as the `phase_start` event, kept in `DataCaptureManager.CurrentPhase`). The phase is set BEFORE `BeginTrial()` so the first sample of a trial is already labelled correctly.
Where it appears: `events.csv` (`phase` column, plus a `phase_start` row at every boundary with `label` = new phase), `npc_state.csv`, `npc_events.csv`, and (v5) `gaze.csv` and `trajectory.csv`. A phase label can be reconstructed from the `phase_start` events by timestamp (that is how the tests validate it).

| Part | Phase | Starts | Ends | Meaning / what is recorded |
|---|---|---|---|---|
| any | `idle` | `part_start` | first `phase_start` | before the first labelled phase (Part 3: warm-up and Start panel; no trial is open, so no gaze/trajectory rows) |
| 2 | `p2_start_pending` | Start panel requested | participant confirms Start | **instruction / panel reading**; no trial open -> no gaze/trajectory rows (intentional gap) |
| 2 | `p2_task1` | before `trial_start` | Continue panel shown | **active task**, first timed period (`part2FirstDuration`) |
| 2 | `p2_continue_pending` | Continue panel shown | participant confirms Continue (teleport) | **panel reading, trial still open and recorded** - labelled so it is never mixed into task time |
| 2 | `p2_task2` | confirm / teleport | timer ends | **active task**, second period (`part2SecondDuration`) |
| 2, 3 | `ended` | trial ended | `part_end` | End panel / post-task |
| 3 | `d1_pending` | before `trial_start` | D1 Continue click | D1 destination panel reading (trial open, recorded, **not search time**) |
| 3 | `d1_search` | D1 Continue | D1 found or limit | D1 search (D1 current target) |
| 3 | `d2_pending` / `d2_search` | D2 panel / D2 Continue | ... | same for D2 |

**Task duration** = sum of the active-task phases' spans taken from `phase_start` timestamps (Part 2: `p2_task1` + `p2_task2`; Part 3: the existing D1/D2 search intervals, unchanged). The trial window `trial_start` -> `trial_end` still contains the reading time (the experiment timing is unchanged); subtracting the `*_pending` spans gives task time.
`npc_warmup_*` markers keep their meaning. Samples are intentionally absent while no trial is open: the recorders treat that as an intentional gap (§15), not a sensor failure.

## 14. `trajectory.csv` (schema v5)

Columns unchanged; appended `part, phase` (same meaning as §13).

## 15. Achieved sampling rates (F-18) - `rate_gaze`, `rate_trajectory` in the manifest

Nominal rates (`gaze sampleRate` = 30 Hz, `trajectoryHz` = 20 Hz) are targets only; they are written as `nominal_hz` next to the achieved values. The achieved values come from `SamplingStats`, fed only by SUCCESSFULLY written rows with their own monotonic `t_abs`
(`Time.realtimeSinceStartupAsDouble`; the Unix clock is auxiliary and not used).
* **Epoch**: a run of consecutive samples without an intentional gap. An epoch ends (`BreakEpoch`) at an application pause / focus loss, when the capture gate closes (trial not open, End panel, capture disabled for the Part) and when the recorder stops. `epochs`, `intentional_breaks` count them. The interval across a break is never counted.
* **Interval**: difference of consecutive `t_abs` inside one epoch. `observed_hz = intervals / sum(intervals)` (= 1 / mean interval); `mean_interval_s`, `max_interval_s` are exact; `median_interval_s`, `p95_interval_s` come from a ring buffer of the most recent 4096 in-epoch intervals (linear interpolation).
* **Unexpected gap**: an in-epoch interval > `gap_threshold_s = max(3 x nominal interval, 0.1 s)` (a stall, hitch or missed ticks). It stays in the statistics and is counted in `unexpected_gaps`. Non-positive intervals (duplicate / out-of-order stamps) are counted in `non_monotonic` and excluded. No per-frame warnings are printed.
* **Unknown**: with fewer than 10 in-epoch intervals `available = false`, `status = insufficient_samples` (or `no_samples`) and every numeric statistic is **-1** (unknown, never 0).
* **Gaze row rate vs valid-eye rate**: `rate_gaze` is the rate of rows written (valid + fallback). `gaze_valid_eye_fraction = gaze_rows_valid_eye / health_gaze.rows_written` and `gaze_valid_eye_rate_hz = observed_hz x fraction` (both -1 when unknown); `gaze_rows_fallback` are head-fallback rows.
Because trajectory / gaze rows are not written while no trial is open, a whole-session mean would be meaningless; these statistics are per epoch by construction.

## 16. Manifest evidence (F-09)

`session_manifest.json` is rewritten at checkpoints (session start, part start, trial start/end, application pause, resume, a write error, session end) and never per sample. Configuration, capability and evidence are separate:
* **Configuration**: `part1_config..part3_config` (static), `StreamHealth.configured_now` / `ever_configured` (the Part's flag at the last checkpoint / at any checkpoint).
* **Capability**: `eye_tracking_supported_state`, `eye_tracking_enabled_state`, `eye_tracking_permission_state` = `"true"` / `"false"` / `"unknown"` (unknown: not queryable on this platform - the Android runtime permission is only queryable in an Android player; in the Editor a `false` from the SDK is reported as `unknown`; or the SDK threw), re-queried at every checkpoint (`eye_tracking_queried_t_abs`). The legacy bools (`eye_tracking_supported/enabled/permission_granted`) are startup snapshots and cannot express unknown.
* **Evidence** (`health_events`, `health_trajectory`, `health_gaze`, `health_npc_state`, `health_npc_events`, `health_npc_lifetimes`, `health_npc_profiles`), each a `StreamHealth`: `status`, `writer_initialised`, `rows_written`, `write_errors`, `first_row_t_abs`, `last_row_t_abs` (-1 = none), `last_error` (exception type + message, no paths).
  Counters come from the writers (`CaptureCsvWriter.RowsWritten`, `GazeRecorder.RowsWritten`) and are cumulative: a later failure does not erase earlier history.
  `status`: `not_configured` (never enabled at any checkpoint) | `disabled_by_master_switch` (NPC streams while `npcCaptureEnabled` was never true - expected absence, not a failure) | `configured_no_rows` | `ok` (rows written, no error) | `write_error` (errors > 0; `rows_written` keeps the earlier rows).
* **Gaze counters**: `gaze_recorder_present`, `health_gaze.*`, `gaze_rows_valid_eye`, `gaze_rows_fallback`, `gaze_rows_left_valid`, `gaze_rows_right_valid`, `gaze_recording_starts`, `gaze_pause_count`, `gaze_resume_count`. Existence of the recorder, a header on disk, or `npcCaptureEnabled = true` is never treated as success.
* **Lifecycle**: `trials_started/ended`, `app_pause_count`, `app_resume_count`, `manifest_updates`, `last_manifest_update_reason`, `last_manifest_update_t_abs`. A resume checkpoint clears `ended_utc` / `end_reason` that the pause snapshot had set.
* **Legacy flags**: `gaze_ok`, `trajectory_ok`, `events_ok`, `npc_state_ok`, `npc_events_ok`, `npc_lifetimes_ok` are now `status in {ok, not_configured, disabled_by_master_switch}`: true = "nothing expected is missing and nothing failed"; false = a configured stream never wrote a row, or a write failed. Read `health_*` for the detail.


## 17. `events.csv`: `value_num` (F-07) and `interaction_id` (F-11)

**`value_num`** is a number only when the event has one. The writer takes `double?` (presence is explicit; the value is never used to guess presence): a supplied value, **including 0**, is written fixed 4 decimals, invariant culture (`0.0000`, `12.5000`, `-3.2500`);
an absent value is an **empty field**. Examples: `app_pause` / `app_focus` carry 1 or **0** (0 = resumed / focus lost); `phase_result` / `correct_selection` / `incorrect_selection` / `phase_end` carry an elapsed time that may be a real 0
(`d2_skipped` records 0 s); `panel_shown`, `session_start`, `part_start`, `phase_start`, `trial_start/end`, `building_click`, `teleport`, `river_crossing`, `target_assigned` carry none and stay blank. `NaN` is never written (blank = unknown).

**Click -> outcome linkage.** One accepted physical click -> one `building_click` row -> **zero or one** outcome row (`correct_selection` or `incorrect_selection`). They stay two records with different meanings:
* `building_click`: the raw interaction (`t_abs` = the click). `building_id`/`target_id` = the clicked building.
* `correct_selection` / `incorrect_selection`: the experiment's assessment (`t_abs` = when the scoring loop committed it, the same frame or a later one; `value_num` = seconds since that destination's search started, measured from the CLICK's own stamp; `label` D1/D2).
* `interaction_id` (`C000001`, `C000002`, ...) is allocated once in `TargetSelectable.Select()` (session-scoped counter, reset when a capture session starts), recorded on the click and carried through `ExperimentManager.RegisterTargetSelection` into the outcome. It is blank on every other event type.
  The id - not the timestamp - is the link. Different clicks (even on the same building) always get different ids.
* A second callback for the same physical click (OnPointerClick + OnMouseDown, two interactors; same building, same frame, or re-entrant) is ignored: no second click, no second outcome (`duplicate_clicks_ignored` in the manifest). A second outcome for an id that already has one is refused (`duplicate_outcomes_rejected`).
* A click may legitimately have **no** outcome: it happened while no scoring window was open (Part 1, Part 2, Part 3 panel-reading phases, after a search ended), it came after the correct answer in the same frame (the rest of that frame's queue is discarded), or it came after the search limit. Clicks while the participant is locked (before Start, End panel) are not recorded at all.

**Counting rules**: clicks = distinct `interaction_id` among `building_click`; evaluated selections = distinct ids among `correct_selection` + `incorrect_selection`; correct / incorrect = distinct ids of that type; unresolved clicks = click ids without an outcome. Do not de-duplicate by time, building or participant.
(Event-row count > click count is expected: each evaluated click is two rows.)

## 18. Experiment timing (F-08)

* **One clock.** Every experiment timer and stamp uses `CaptureClock.TAbs` = `Time.realtimeSinceStartupAsDouble`: monotonic, independent of `Time.timeScale`, not clamped by `Time.maximumDeltaTime`; the same basis as every `t_abs` in every stream. `t_unix_ms` is wall-clock metadata (alignment only), never used for durations.
* **Timers on it** (`ExperimentManager.WaitExperimentSeconds` and the Part 3 `Part3ValidTiming` calls): NPC warm-up wait, Part 1 duration, Part 2 first and second durations, Part 3 D1 limit, total budget, D2 budget, click stamps, `trial_start`/`trial_end`, `phase_*`. Before v6 Parts 1, 2 and the warm-up accumulated `Time.deltaTime`, which Unity clamps (0.333 s here): a 2 s hitch advanced the accumulator by 0.333 s, so those tasks ran longer than configured.
  Movement, animation and physics keep using `Time.deltaTime`; configured durations and thresholds are unchanged.
* **Pause policy (all Parts).** Application suspension **counts** as elapsed experiment time (wall-clock deadlines, as Part 3 always did). It is not hidden: `app_pause` (`value_num` 1) / `app_pause` (0) and `app_focus` rows carry their own `t_abs`, `app_pause_count` / `gaze_pause_count` are in the manifest, so
  *participant-active duration = measured span - sum of suspended intervals* can be reconstructed. (Before v6 Parts 1/2 effectively excluded a long suspension because the first frame after it is clamped; that implicit behaviour is gone.)
* **Quantities (do not call them all "response time")**: monotonic elapsed time (`t_abs` differences); wall-clock time (`t_unix_ms`); *phase/task duration* (differences of `phase_start` rows, e.g. `p2_continue_pending - p2_task1`); *trial duration* (`trial_end - trial_start`, includes panel reading); *configured maximum* (Inspector); *actual timeout / completion* (`phase_timeout` / outcome rows with their `value_num` elapsed);
  *response time* proper = `value_num` of `correct_selection` / `incorrect_selection` (seconds from the destination's Continue click to the click). Event stamps are taken when the event is raised; a timer overshoots its deadline by at most one frame.
* Phase is set before `BeginTrial()`, so `trial_start` is never preceded by an unlabelled sample.

## 19. Data-integrity checks that the tests make

`value_num`: zero vs missing parse differently; `interaction_id`: every click has one, every outcome points at a click, no id has two outcomes; timing: a 5.2 s injected clock jump ends a 5 s Part 2 task, a 5.3 s jump a 5 s Part 1 trial and a 6 s jump a 5 s Part 3 D1 limit within one frame; suspension counts and is reconstructable.


## 20. NPC line of sight (F-10) - `los_clear_to_participant`, `los_status`

* **Query** (`NpcLineOfSight.Query`, once per NPC per snapshot): segment from the NPC's **eye point** to the participant's **head target** (the `CenterEyeAnchor`, i.e. the HMD). Eye point = the humanoid Animator's head bone when present, otherwise 90 % of the NavMeshAgent height (or of the top of its non-trigger colliders) above the NPC root.
  No fixed 1.6 m guess is used. The shipped `Npc.prefab` carries 9 capsule, 1 sphere and 2 box colliders, all non-trigger on layer Default under a humanoid skeleton.
* **Obstacles**: every non-trigger collider in the LOS mask (Default | Ground) that is not (a) any collider under the querying NPC's own transform, nor (b) any collider under the participant rig or the head target. Other NPCs, buildings, terrain and props **do** occlude; trigger colliders are ignored.
  All hits within the segment are examined (hits are not assumed sorted), so an NPC's own colliders in front of a wall never mask the wall. A reusable 32-entry hit buffer is used (retried once with 256); no per-query allocation.
* **Values**: `1` / `clear`: no obstacle among the hits. `0` / `blocked`: at least one obstacle (conclusive even if the buffer filled). **Blank**: `missing_participant_target` (no HMD transform), `missing_npc_origin`, `invalid_geometry` (NaN or target at the origin), `hit_buffer_overflow` (256 hits and no obstacle among them: not decidable), `query_error`. Unknown is never reported as blocked.
* **Cause of the pre-v7 defect**: the linecast ran from the participant to `NPC position + 1.6 m`, a point inside the NPC's own capsule, so it hit the NPC's own Default-layer colliders on almost every sample (`los = 0`). Old files: ignore `los_clear_to_participant`.
* **Limits**: line of sight is geometric (collider based), not visual: it ignores field of view, lighting, transparent materials without colliders and colliders on other layers. The participant's endpoint is the HMD position, not an eye-tracked gaze point. The manifest carries cumulative counts
  (`npc_los_clear`, `npc_los_blocked`, `npc_los_unavailable` + one counter per unavailable reason) as a quick distribution check; there is no target distribution.

## 21. Recording failures (F-13) - `capture_errors.csv` and `StreamHealth`

* **Policy** (`CaptureCsvWriter`, used by every CSV stream including gaze): the first I/O error puts the stream in state **Failed**; the failing step is classified `open | header | append | flush`. An unconditional `Debug.LogError` (independent of `enableDebugLogs`) is issued once per failure and once on permanent failure; recoveries and failed retries are warnings; dropped rows are never logged individually.
  A failed batch is never retried (a partial write could duplicate rows) and `partial_output_possible` is set for append/flush failures (the last line of the file may be incomplete). After a 2 s cool-down the writer makes up to 3 **recovery attempts** on the next offered row (re-create the file header if the file is empty/missing, terminate a possibly partial last line, probe-append);
  success => state **Recovered** (never silently `ok`), `MaxRecoveryAttempts` failures => **permanently failed** (state stays Failed; no more attempts). Streams are independent: a failure never touches another stream.
* **Counters** (per stream; each changes only at the point it becomes true): `rows_attempted` = `rows_written` + `rows_failed` (the batch that raised the error) + `rows_dropped_after_failure` (offered while Failed); `write_errors`; `first_row_t_abs` / `last_row_t_abs` (first/last SUCCESSFUL write); `first_error_t_abs` / `last_error_t_abs`;
  `last_error_operation`, `last_error_type`, `last_error` (type + sanitised message, no paths); `recovery_attempts`; `writer_state` (`operational | failed | permanently_failed | recovered`); `partial_output_possible`.
* **Manifest status**: `ok`, `write_error` (currently failed), `recovered` (failed earlier, writing again - rows were lost in the gap), `configured_no_rows`, `not_configured`, `disabled_by_master_switch`. Intentional disabling is never `write_error`. A transition triggers an immediate manifest checkpoint written by the manager, not by the failed stream.
* **`capture_errors.csv`** (diagnostic channel; not a participant or NPC event stream): `session_id, t_abs, t_utc, stream_name, operation, error_type, error_message, rows_written_before_failure, rows_dropped, status, recovery_attempt`, one row per transition (`failed`, `recovery_failed`, `permanently_failed`, `recovered`; also `session_manifest` transitions).
  It is a plain writer with no transition observer, so its own failure never reports itself. The in-memory `DataCaptureManager.FailureRegistry` and the Error log hold the same facts. **No disk-based channel can record a failure of the disk itself**: if the whole storage is gone, only the log and the in-memory registry exist.

## 22. Participant identity and sessions (F-14)

* **Participant ID** (`SessionIdentity.ValidateParticipantId`): kept exactly (trimmed). Valid: any non-empty text up to 64 characters without control characters or `/ \ : * ? " < > |`. `P012`, `12`, `A-07` are all valid; there is no required prefix or digit count. **Rejected**: empty/whitespace; known placeholders (`0`, `P000`, `UNKNOWN`, `UNKNOWN_SUBJECT`, `DEFAULT`, `NONE`, `NULL`, `TBD`, `PARTICIPANT`, `SUBJECT`, case-insensitive) and any all-zero ID (`00`, `p0000`). The list lives in one place (`SessionIdentity.PlaceholderIds`).
* **Configuration source** (`ExternalSessionController`): `useExternalJson = true` => the file `<persistentDataPath>/<externalFileName>` is the ONLY source. It must exist, be valid JSON, contain `participantId`, `selectedPart` (Part1-3), `part2GroupIndex`, `part3GroupIndex` (0..11) and a valid ID; values are applied only if all validate. Any failure => `UnresolvedConfig`, **no fallback to Inspector values**.
  `useExternalJson = false` => the Inspector values are validated on their own; no JSON file is read (the file `Assets/Scripts/session_config.json` in the repository is not read by any code). The source (`config_source`), the file name and an FNV-1a hash of the applied JSON text (`config_hash`) are in the manifest.
* **Fail closed**: if the identity/configuration is invalid (or there is no `ExternalSessionController`), `DataCaptureManager` creates **no** folder, CSV, manifest or `capture_errors.csv`, discards queued NPC lifecycle events (they are never replayed under a later identity), drops later ones at the gate, refuses to start the gaze recorder (also no legacy fall-back file), and logs `RECORDING BLOCKED`;
  `ExperimentManager` refuses `StartSelectedPart` / `StartPart1-3` (so auto-start does not run). NPC simulation and the scene otherwise keep working. The controller shows `identityStatus` / `identityMessage` in the Inspector. **The scene as shipped at the time of this change has `participantId: 0` with `autoStart: 1`: it is blocked until a real ID is set.**
* **Session identity**: `run_guid` (32 hex, once per session, in the manifest) -> `session_id` = `<UTC yyyyMMddTHHmmssZ>_<first 12 hex of run_guid>` = the folder name = the `session_id` column of every stream. The participant ID is **not** part of it and is never sanitised; it is stored exactly in the manifest (`participant_id`) and in `gaze.csv` (`subject_id`).
  The folder must not exist; up to 5 GUID draws; an existing directory is never reused (after 5 collisions the session is refused). Several sessions (Parts) of one participant are linked by the identical `participant_id` in their manifests; there is no shared "study run" id because the experiment has no such concept.

## 23. Manifest persistence (F-19)

`session_manifest.json` is built in memory and installed by `ManifestFileWriter.WriteAtomic`: unique temp file in the same folder (UTF-8, no BOM) -> `Flush(true)` -> read back and strictly validated (complete JSON object with an integer `manifest_revision`) -> a valid current file is copied to `session_manifest.json.bak`, an invalid one is renamed `*.corrupt-<time>` ->
install with `File.Move` (no final yet) or `File.Replace(tmp, final, null)` (a single rename on POSIX, `ReplaceFile` on NTFS). Only if the runtime reports the replace as unsupported, the **non-atomic fallback** `File.Copy(tmp, final, true)` is used (`manifest_last_install_mode = copy_fallback`); the validated temp file and the `.bak` remain for recovery.
The final file is never deleted before its replacement exists, and a failure at any step leaves the previous valid manifest untouched. Writes are serialised (main thread only); `manifest_revision` increases per attempt; failures are counted (`manifest_write_failures`, `manifest_last_error`), logged once per failure episode, listed in `capture_errors.csv` (`session_manifest`) and retried at the next checkpoint.
**Recovery** (`ManifestFileWriter.Recover`): among the final, the `.bak` and any `*.tmp`, the valid file with the highest `manifest_revision` wins (ties: final > backup > tmp); an invalid final is preserved as `.corrupt-*`; a newer valid generation is never replaced by an older one; an incomplete temp file is never a candidate.
**Durability**: the replace makes the swap atomic with respect to process termination on POSIX/NTFS, and `Flush(true)` asks the OS to write through; neither is a guarantee against sudden power loss, and the Android IL2CPP behaviour of `File.Replace` was not tested (hence the explicit fallback).
