# NPC identity and recording schema (schema_version 3)

Reference for `npc_state.csv`, `npc_events.csv`, `npc_lifetimes.csv` and `npc_profiles.csv`, derived from the write paths in
`Assets/Scripts/Capture/DataCaptureManager.cs` (`SampleNpcs`, `AppendNpcEventLine`, `WriteLifetimeRow`, `WriteProfileRegistryRow`) and the headers in
`CaptureConstants.cs`. Column order below is the file order. **Compatibility:** schema v2 -> v3 only *appended* columns (state +3, lifetimes +25) and added
`npc_profiles.csv`; no column was renamed or reordered. Historical files were not rewritten; use `schema_version` in `session_manifest.json` to tell them apart
(v2 files have fewer columns, still end with the same prefix, and contain `npc_leg_start` rows).

## 1. What each file is

| File | Grain | Purpose | Written when |
|---|---|---|---|
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
| 25 | `los_clear_to_participant` | 0/1 | - | `1` = no Default/Ground collider on the line HMD -> NPC head (pos + 1.6 m); `0` = blocked | `Physics.Linecast` from `CenterEyeAnchor` | sample | **blank** when no HMD transform exists | - |
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

Snapshot semantics: every NPC with an open lifetime is in every snapshot (no sub-sampling). The interval between snapshots is `npcStateSamplingIntervalSeconds`, stretched only by the
data budget (see §9); derive the true frequency from `t_abs`. Inactive pooled objects never appear. Rows stop at the End-panel freeze.

## 4. `npc_events.csv`

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
| **false** | No rows in `npc_state.csv`, `npc_events.csv`, `npc_lifetimes.csv`, `npc_profiles.csv` (the files are not even created). No `npc_spawn` / `npc_despawn` / `npc_warmup_start` / `npc_warmup_end` rows in `events.csv`. `hit_npc_id` and `hit_npc_spawn_instance_id` are blank in `gaze.csv`. Lifecycle events are **discarded at raise time** - never queued, never replayed (the manifest only counts them: `npc_events_suppressed_by_master_switch`). The NPC simulation, gaze rows and trajectory rows are unaffected. |
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
`npc_lifetimes.csv`, `npc_profiles.csv`, `events.csv`, headers and the manifest are never throttled. Manifest keys: `npc_state_base_interval_seconds`, `npc_state_max_interval_seconds`, `npc_state_data_budget_mb_per_minute`, `npc_state_budget_scope`, `npc_master_switch_semantics`, `npc_capture_effective_enabled`,
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
