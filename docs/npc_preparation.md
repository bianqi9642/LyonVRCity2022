# NPC preparation: pool creation, time-based NPC generation, Start-gated pause (schema v10)

The design is **time-based, not population-based**. The experimental control variable is the *executed NPC generation time* (`npcWarmupDuration`); the number of NPCs that exist when it ends is a
result (spawn interval x duration), recorded but never a condition. There is no `initialActiveNpcTarget`, and **no pool timeout** (`poolPreparationTimeoutSeconds` was removed).

The whole preparation is automatic. Nothing waits for the headset being worn, eye / head tracking, controllers, participant input or the Start panel; the participant can leave the headset off, and
finds a prepared, frozen environment when they put it on.

| Stage | What happens | Setting | Ends when |
|---|---|---|---|
| 1. Pool preparation | `SpawnerManager` creates `poolPrewarm` (5000) **inactive, invisible** pooled objects (it starts at scene load, so it is usually done before the Part) | `SpawnerManager.poolPrewarm`, `prewarmFrameBudgetMs` | the pool is complete; **or** creation reports an error; **or** the Part is cancelled. No time limit |
| 2. NPC generation | NPCs spawn at `spawnInterval` (0.1 s) with their normal itinerary / behaviour | `ExperimentManager.npcWarmupDuration` (10 s, Inspector) | `npcWarmupDuration` seconds of **executed** time |
| 3. Start-gated pause | spawning stopped, every active NPC held (visible, raycastable, still sampled); the Start panel is shown once the headset is worn | `ExperimentManager.holdNpcsUntilStart` (default true), `waitForHeadsetBeforeStartPanel` (default true) | the participant presses Start (`BeginTrial`) |

## Pool validation (no timeout, no weaker checks)

The pool is accepted only if `created == poolPrewarm` after creation finished. `Failed` (and then **no NPC generation, no Start panel, the Part is aborted, `BeginTrial` refuses**):
`failed_error` (creation threw - e.g. missing prefab; `SpawnerManager.PoolFailed`, the exception text is in `pool_preparation_error`), `failed_incomplete` (created != target), `failed_no_spawner`. The spawn gate
(`BeginNpcSimulation`) is opened only after this verification, so an incomplete pool cannot produce an NPC. A pool that is merely **slow or stuck** is not declared failed and is not skipped: the state stays
`PoolPreparing`, the live progress is in the manifest (`pool_preparation_created_count`, `pool_preparation_last_progress_t_abs`) and a status line is logged every 30 s (`enableLifecycleLogs`). The Part being
cancelled / the session ending during preparation -> `Cancelled`.

## State machine (`ExperimentManager.NpcPreparation`, manifest `npc_preparation`, with `state_trail`)

`NotStarted -> PoolPreparing -> PoolReady -> NpcGenerationRunning -> NpcGenerationCompleted -> PausedAwaitingParticipantStart -> Running -> Ended`; `Failed`; `Cancelled`. `NpcGenerationCompleted` lasts one
frame (stop spawning + pause happen in one synchronous step). `npcWarmupDuration = 0` -> `PoolReady -> Running`. `holdNpcsUntilStart = false` -> `NpcGenerationCompleted -> Running` (not the intended design).

## Generation timer and the headset / application lifecycle

Generation time = sum over frames of `min(unscaled frame delta, Time.maximumDeltaTime)` - the same cap the NPC simulation obeys, so the timer advances exactly as far as the simulation did. Therefore:

| Situation (Unity keeps executing?) | Effect on the timer |
|---|---|
| Headset unworn (`OVRManager.HMDUnmounted`), Unity still executing | none - generation continues (counted in `npc_generation_hmd_unmounted_seconds`) |
| Focus lost, Unity still executing | none (`npc_generation_focus_lost_seconds`) |
| Eye / head tracking unavailable | none (not consulted) |
| Genuine suspension, Unity not executing | the stalled interval adds at most one capped step; the rest is `npc_generation_non_executing_seconds`; no NPC is "generated" in it |

`OnApplicationPause`, focus and HMD events are **diagnostics only** (`npc_generation_app_pause_count / _seconds`); `npc_generation_unreported_stalls` counts frame gaps beyond the step cap that no pause callback
explained (a suspension the callbacks did not report). `npc_generation_wall_clock_seconds` = completed - started; `actual + non_executing = wall`.

**Quest Pro reality (Meta's lifecycle documentation, not verified on this project's hardware):** `HMDUnmounted` fires when the headset is taken off; an application *pause* follows only after the headset's
**Auto Sleep** delay (Settings > Power), unless the headset is worn again first. After that pause Unity executes nothing, so NPCs cannot be generated or simulated during it. Quest 3S has no proximity sensor.
A community report describes `OnApplicationPause` / focus callbacks not firing during rapid proximity cycles on Quest 3 (Unity 2022.3, SDK v201) - the stall counter above exists to catch that. See the final
report of this change for the operating recommendation. `Application.runInBackground` is `1` in this project; it is a desktop setting and does not keep an Android activity executing once the system pauses it
(and Meta's review guidance asks for it to be off).

## Start panel and the headset

Panels are positioned once, at show time, in front of the current head pose. When generation finished with the headset unworn, showing the panel immediately would place it wherever the headset lies, so only the
**panel** waits until `OVRPlugin.userPresent` (`start_panel_headset_wait_seconds`); the NPCs are already paused and visible. Unknown presence (no runtime, Editor) never waits. Start still resumes the NPCs
first and then raises `OnTrialStart` in the same frame.

## Pause / resume (what is frozen, what is not)

`SpawnerManager.PauseNpcBehaviour / ResumeNpcBehaviour` (idempotent) + `NpcSimulationGate` (never `Time.timeScale`): movement (`MoveToTarget.SetSimulationHold`: NavMeshAgent stopped, path / destination intact,
independent of `externallyPaused`), dwell / capacity polling / dwell facing on `NpcSimulationGate.SimTime`, `UncertainBehavior` timers and random rolls, spawning. Untouched: GameObjects, renderers, colliders,
identities, lifetime registration, `npc_state` sampling, gaze rays, trajectory and every recorder. Released by Start, and by `ResetForNewPart`, shutdown and `OnDestroy`.

## Recorded

events.csv: `npc_pool_preparation_started / _completed / _failed`, `npc_warmup_start` (= generation start) / `npc_warmup_end` (= generation end, value = executed seconds), `npc_behaviour_paused`,
`npc_behaviour_resumed`, `npc_preparation_part_aborted`. Manifest `npc_preparation`: `pool_preparation_{started_t_abs, completed_t_abs, duration_seconds, target_count, created_count, last_progress_t_abs, status,
error}`; `npc_generation_{configured_duration_seconds, started_t_abs, completed_t_abs, actual_duration_seconds, wall_clock_seconds, non_executing_seconds, max_frame_gap_seconds, unreported_stalls, app_pause_count,
app_pause_seconds, focus_lost_seconds, hmd_unmount_count, hmd_unmounted_seconds, spawn_count}`, `active_npcs_at_generation_end`; `npc_behaviour_{paused,resumed}_t_abs`, `participant_start_t_abs`,
`active_npcs_at_pause / _at_resume`, `start_panel_headset_wait_seconds`, `early_start_attempts`, `state`, `state_trail`. Pool counts are INACTIVE objects, never the NPCs the participant sees. After the Start click
spawning continues under the existing experiment rules (the generation period is not restarted).

## What is and is not standardised

The generation **duration** and the stage order are identical for Part 2 and Part 3 (same code path; movement modes unchanged). The **population** at the pause follows from duration and spawn interval and is
recorded; it is not guaranteed. NPC **trajectories are NOT identical** across sessions (`UnityEngine.Random` unseeded; entrance placement and `AttractorDwell` use TickCount-seeded `System.Random`;
`npc_random_sources`).
