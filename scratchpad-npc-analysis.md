# NPC Generation & Movement — Current Implementation Analysis

Analysis-only. No code, scene, prefab, or config was modified.
Scene: `Assets/Scenes/LyonVRVity35.unity`. Agent prefab: `Assets/Prefabs/Npc.prefab` (guid `c34a3349d586043c09271e3b19db0437`).

Legend for evidence tags used throughout:
**[C]** confirmed from code/asset · **[I]** inferred · **[U]** unknown / not determinable from inspected files.

---

## 1. Executive summary

1. **[C]** Spawning is driven by one coroutine, `SpawnerManager.SpawnLoop()`, that ticks every `spawnInterval` (scene value **0.5 s**) and spawns one agent per tick while `liveAgents < maxAgents` (scene value **5**) and a trial is active.
2. **[C]** Spawning is **trial-gated**: `gateSpawningByTrialState = true`, so new agents are only created between `ExperimentManager.OnTrialStart` and `OnTrialEnd`. Agents already alive when a trial ends are **paused in place** (`pauseLiveAgentsOutsideTrial = true`, `despawnLiveAgentsOnTrialEnd = false`).
3. **[C]** NPCs are **pooled**. `poolPrewarm = 10` instances are `Instantiate`d once at startup, deactivated, and thereafter only `SetActive(true/false)` — never destroyed (`usePooling = true`). New `Instantiate` only happens if the pool is empty.
4. **[C]** Each agent runs a **precomputed, static itinerary** of Transforms: `[entrance] + [1–4 distinct random attractors] + [exit]`, built once at spawn in `SpawnerManager.BuildItinerary()`. The number and identity of attractors and the exit are fixed at spawn; they never change dynamically.
5. **[C]** The active itinerary is the **Transform-based** path (`BuildItinerary` / `PedestrianAgent.Initialize`), **not** the position-based path. `sampleAttractorsAsPositions = true` in the scene, but it only takes effect if an attractor carries an `AttractorArea` component — none do (they carry `AttractorDwell`), so `AtLeastOneAttractorHasArea()` returns false.
6. **[C]** Movement is **NavMeshAgent-only** via `MoveToTarget`. `PedestrianAgent` is the itinerary state machine and delegates all motion to `MoveToTarget.MoveTo(...)`, which is the only caller of `NavMeshAgent.SetDestination()`.
7. **[C]** Arrival is detected two ways: `MoveToTarget.OnArrived` (event) **and** a `PedestrianAgent.Update()` distance poll (`arriveTolerance = 0.6 m`). For the very first leg of a pooled object's first life, `MoveToTarget` cannot signal arrival (see §5 / §13), so the distance poll is the effective detector there.
8. **[C]** Dwell time is **one** `Random.Range(5, 15)` value chosen per agent at spawn and reused at **every** stop in that agent's itinerary — including a stop at the entrance waypoint.
9. **[C]** `AttractorDwell` provides (a) an independent random standing point inside the attractor plane (so dwellers spread out) and (b) an optional `facingTarget` the NPC yaw-turns to face while dwelling. It uses a **private `System.Random`**, isolated from the route RNG.
10. **[C]** `UncertainBehavior` (per-agent hesitation/pause/slow/detour) is **disabled on the prefab** and enabled per-agent with probability `uncertainProbability = 0.2`. When enabled it runs in its **NavMeshAgent-fallback mode** (`moveController` is unassigned on the prefab), so it and `MoveToTarget` both drive the same `NavMeshAgent`.
11. **[C]** Every randomness source except `AttractorDwell` uses the shared, **unseeded** `UnityEngine.Random`. There is no `Random.InitState` anywhere. **NPC trajectories are not reproducible** from initial conditions.
12. **[C]** `PedestrianAgent.AgentID` (`"NPC-000001"`…) is assigned once in `Awake()` and is stable for the GameObject's lifetime = **pool-slot identity**, not per-spawn. Two sequential NPC lifetimes on the same pooled object share one `AgentID`. **No per-lifetime identifier exists.**
13. **[C]** `SpawnerManager` tracks live agents in a `private List<GameObject> activeAgents`. There is **no public accessor and no spawn event** — only `PedestrianAgent.onAgentFinished` (despawn) is publicly observable.

---

## 2. Complete NPC lifecycle (numbered)

1. **[C] Startup.** `SpawnerManager.Start()` — if `gateSpawningByTrialState` (true): resolves `experimentManager` via `FindObjectOfType<ExperimentManager>()` (inspector slot is empty) and subscribes `HandleTrialStart`/`HandleTrialEnd`. Then `StartCoroutine(InitializePoolThenSpawn())`.
2. **[C] NavMesh wait + prewarm.** `InitializePoolThenSpawn` → `WaitForNavMeshThenPrewarm()`: polls `NavMesh.SamplePosition` near `startTransform` / entrances up to `navmeshWaitTimeout` (5 s), then `PrewarmPoolCoroutine()`.
3. **[C] Pool creation.** `PrewarmPoolCoroutine` loops `poolPrewarm` (10) times: `Instantiate(agentPrefab, …)` → **`Awake()` runs on every component now** (this is where `PedestrianAgent.agentID` is assigned). Then it samples the NavMesh, warps the `NavMeshAgent`, calls `AgentFactory.ApplyProfileToInstance` (appearance + nav shape/speed + `UncertainBehavior.enabled`), sets `MoveToTarget.agent`, then `go.SetActive(false)` and `pool.Add(go)`. `MoveToTarget.Start()` / `PedestrianAgent` movement do **not** run yet (object inactive).
4. **[C] Spawn loop starts.** `poolInitializationComplete = true`; `StartCoroutine(SpawnLoop())`.
5. **[C] Trial opens the gate.** `ExperimentManager.RunPartN` teleports the player, shows the start panel, waits for confirmation, sets `spawner.defaultProfile = currentProfile`, then fires `OnTrialStart` → `SpawnerManager.HandleTrialStart()` sets `trialActive = true` (and resumes any paused agents).
6. **[C] Spawn tick.** Every `spawnInterval` (0.5 s) `SpawnLoop` checks `poolInitializationComplete && (!gate || trialActive) && liveAgents < maxAgents` (5). If OK → `SpawnAgent()`.
7. **[C] Entrance + spawn position chosen.** `entranceIndex = Random.Range(0, entrances.Length)` (10 entrances). Spawn **position** is sampled near `startTransform` (a single scene transform, `spawnSampleRadius = 1`), **not** near the chosen entrance. The chosen entrance is only used as itinerary\[0].
8. **[C] Pool fetch + placement.** Pop the last element of `pool`. `NavMesh.SamplePosition` near spawn pos → `nav.Warp(...)`, set transform, `nav.ResetPath()`.
9. **[C] Profile applied.** `AgentFactory.ApplyProfileToInstance(go, defaultProfile)`: appearance (weighted random meshes/materials); `nav.radius/height/acceleration/angularSpeed/avoidancePriority` from profile; `nav.speed = profile.speed (1.0) × weighted multiplier ∈ {0.6, 1.4, 2.2}`; `uncertain.enabled = forceUncertain(false) || Random.value < 0.2`.
10. **[C] Activation.** `go.transform.rotation = identity`, `go.SetActive(true)` → `MoveToTarget.OnEnable` (first life: `hasStartedOnce == false` → returns; `Start()` coroutine then runs and places the agent on the NavMesh; later lives: `ReinitializeAfterEnable()` re-warps).
11. **[C] Itinerary built.** `usingPositions = false` (see §1.5). `dwell = Random.Range(5, 15)` (one value). `BuildItinerary(chosenEntrance)`: `num = Random.Range(1, 5)` → 1–4; pick `num` **distinct** attractor indices from `[0..6]` by repeated `Random.Range(0, indices.Count)` + `RemoveAt`; append `exits[Random.Range(0, 10)]`; `Insert(0, entrance)`. Result: `[entrance, attr…(1–4), exit]`.
12. **[C] MoveToTarget seeded.** `mt.startTransform = startTransform`; `mt.targetTransform = itinerary[0]`; `mt.startPosition = go.transform.position`.
13. **[C] Agent initialized.** `agentScript.Initialize(itinerary, this, dwell)` → copies itinerary, `currentIndex = 0`, `state = Moving`, `currentLegStopOverride = null`, `uncertainBehavior?.ResumeAfterArrival()`, `BeginMovementWhenReady()`.
14. **[C] Bookkeeping.** `liveAgents++`; `activeAgents.Add(go)`; `agentScript.onAgentFinished += OnAgentFinished`.
15. **[C] First move command.** `WaitUntilMoverReadyThenMove()` polls `MoveToTarget.IsReadyToMove()` (`agent != null && enabled && isOnNavMesh`) each frame, up to `moveReadyPollTimeout` (3 s), then `MoveToCurrent()`.
16. **[C] MoveToCurrent (leg 0 = entrance).** `TryGetAttractorDwellStop(entrance)` → false (no `AttractorDwell` on entrances). So `mt.targetTransform = entrance`; subscribe to `OnArrived`; `mt.MoveTo(entrance)` → `NavMeshAgent.SetDestination(entrance.position)`. `externallyPaused` is **not** cleared here.
17. **[C] Travel to entrance.** `NavMeshAgent` steers. `MoveToTarget.Update` drives rotation (`agent.updateRotation = false`) and the animator from `agent.velocity`. Because `externallyPaused == true` on a pooled object's first life, `MoveToTarget`'s own arrival branch (`if (!externallyPaused && …)`) is suppressed.
18. **[C] Arrival at entrance.** `PedestrianAgent.Update()` distance poll: `Distance(transform.position, entrance.position) <= 0.6` → `OnReachedWaypoint()`.
19. **[C] Entrance "dwell".** `OnReachedWaypoint`: `isExit == false`, `manager.FindAttractorInfo(entrance) == null` → `StartCoroutine(SimpleDwellThenAdvance())`: `state = WaitingAtAttractor`; `mt.PauseExternal(true, true)`; `WaitForSeconds(dwell)`; `mt.ResumeExternal()` (**this clears `externallyPaused`**); `uncertainBehavior?.ResumeAfterArrival()`; `AdvanceItinerary()`.
20. **[C] Leg to attractor k.** `currentIndex = k`; `state = Moving`; `MoveToCurrent()`. `TryGetAttractorDwellStop(attractorPlane)` → `AttractorDwell.TryGetRandomStandingPoint()` returns a random NavMesh point inside the plane (private `System.Random`, vertical projection). `currentLegStopOverride = thatPoint`; `mt.targetTransform = null`; `mt.targetPosition = point`; `mt.MoveTo(point)`.
21. **[C] Arrival at attractor.** `externallyPaused == false` now → `MoveToTarget.OnArrived` can fire (`dist <= arriveThreshold 0.2` or `remainingDistance <= 0.2`); `PedestrianAgent.Update` poll to `currentLegStopOverride` (`<= 0.6`) is the backup. → `OnReachedWaypoint()`.
22. **[C] Capacity + dwell.** `info = manager.FindAttractorInfo(target)` (capacity 5). `manager.TryEnterAttractor(info, this)`: if `occupants.Count < 5` → add, return true → `WaitAtAttractor(info)`. Else → `PollForCapacity(info)` (retry every 1 s, 30 s timeout).
23. **[C] WaitAtAttractor.** `state = WaitingAtAttractor`; `mt.PauseExternal(true, true)`; `FaceDwellTargetIfConfigured(info)` → if `AttractorDwell.facingTarget` set, `FaceYawCoroutine` yaw-rotates toward it at `facingTurnSpeedDeg` (360°/s); `WaitForSeconds(dwell)`; `manager.ReleaseAttractor`; `mt.ResumeExternal()`; `uncertainBehavior?.ResumeAfterArrival()`; `AdvanceItinerary()`.
24. **[C] Repeat 20–23** for each remaining attractor (`currentIndex++`).
25. **[C] Leg to exit.** Last index (`count-1`). Exit has no `AttractorDwell` → normal `mt.MoveTo(exitTransform)`.
26. **[C] Arrival at exit.** `OnReachedWaypoint`: `isExit = (currentIndex == count-1)` → true → `Finish()`.
27. **[C] Finish.** `state = Finished`; unsubscribe `OnArrived`; `ReleaseCurrentAttractorIfAny()`; `mt.Stop()`; `onAgentFinished?.Invoke(this)`.
28. **[C] Despawn.** `SpawnerManager.OnAgentFinished(agent)`: unsubscribe; `liveAgents--`; `activeAgents.Remove(go)`; (pooling) re-enable nav if on-mesh, `mt.agent = nav`, `go.SetActive(false)`, `pool.Add(go)`.
29. **[C] Deactivation hooks.** `PedestrianAgent.OnDisable`: unsubscribe `OnArrived`, `mt.Stop()`, `startMovementCoroutine = null`, `facingCoroutine = null`, `currentLegStopOverride = null`. `MoveToTarget.OnDisable`: `moving = false`, stop agent, zero animator. Unity auto-stops all running coroutines on the object.
30. **[C] Reuse.** Next `SpawnAgent` pops the same GameObject. `SetActive(true)` → `MoveToTarget.OnEnable` → `ReinitializeAfterEnable()` (re-warp onto NavMesh, `SetDestination(current target)`, `moving = true`). Then `SpawnAgent` re-applies the profile and calls `Initialize(newItinerary, …)` again → `currentIndex = 0`, `state = Moving`, new `dwell`. **`agentID` is not reassigned.**

### Answers to the 20 sub-questions

| # | Question | Answer |
|---|---|---|
| 1 | What causes a spawn? | `SpawnLoop` coroutine tick (every 0.5 s) while `trialActive` and `liveAgents < 5`. **[C]** |
| 2 | Which script creates/activates? | `SpawnerManager.SpawnAgent()`. **[C]** |
| 3 | `Instantiate` or pool? | **Both, but pool-first.** 10 `Instantiate`d at prewarm; runtime spawns pop from `pool` and `SetActive(true)`. `Instantiate` at spawn only if pool empty. **[C]** |
| 4 | Where initialized? | `PedestrianAgent.Initialize(itinerary, manager, dwell)` (called from `SpawnAgent`). Movement init in `MoveToTarget.Start()` / `ReinitializeAfterEnable()`. **[C]** |
| 5 | When is `AgentID` assigned? | `PedestrianAgent.Awake()` — i.e. at `Instantiate` during prewarm, once per GameObject. **[C]** |
| 6 | When does movement become active? | After `SetActive(true)` and `MoveToTarget.IsReadyToMove()` returns true (agent on NavMesh), then `MoveToCurrent()` → `MoveTo()` → `SetDestination`. **[C]** |
| 7 | First destination selection? | `itinerary[0]` = `entrances[Random.Range(0, 10)]`, chosen in `SpawnAgent`. Agent physically walks from the `startTransform` spawn point to that entrance. **[C]** |
| 8 | How is the NavMesh destination assigned? | `MoveToTarget.MoveTo()` → `NavMeshAgent.SetDestination(pos)`, with defensive `NavMesh.SamplePosition` resampling in `MoveToTarget.Update` if `!agent.hasPath`. **[C]** |
| 9 | Who calls `SetDestination()`? | `MoveToTarget` (multiple sites: `Start`, `ReinitializeAfterEnable`, `MoveTo`, `ResumeExternal`, `Update` resample). Also `UncertainBehavior` when enabled (agent-fallback mode). **[C]** |
| 10 | Who decides "arrived"? | `MoveToTarget` (`OnArrived` event, `arriveThreshold 0.2`) **and** `PedestrianAgent.Update` distance poll (`arriveTolerance 0.6`). **[C]** |
| 11 | After arrival? | `PedestrianAgent.OnReachedWaypoint` → attractor capacity check + `WaitAtAttractor` / `PollForCapacity` / `SimpleDwellThenAdvance` (for entrance & non-registered waypoints) / `Finish` (exit). **[C]** |
| 12 | Dwell time selection? | `Random.Range(dwellTimeSec.x=5, y=15)` **once per agent** in `SpawnAgent`; same value for all its stops. **[C]** |
| 13 | Next attractor selection? | Precomputed in `BuildItinerary`. Agent just does `currentIndex++`. **[C]** |
| 14 | Exit selection? | `exits[Random.Range(0, 10)]` in `BuildItinerary`, **at spawn time**. (Scene: `exits[]` == `entrances[]`, same 10 transforms.) **[C]** |
| 15 | How does the agent know the itinerary is complete? | `AdvanceItinerary` / `OnReachedWaypoint`: `currentIndex >= itineraryTransforms.Count` or `isExit == true` → `Finish()`. **[C]** |
| 16 | What causes despawn? | `Finish()` (reached exit) or `ForceFinish()` (external) → `onAgentFinished` → `SpawnerManager.OnAgentFinished` → `SetActive(false)` + return to pool. **[C]** |
| 17 | On reuse? | `SetActive(true)` → `MoveToTarget.ReinitializeAfterEnable` re-warps; `SpawnAgent` re-applies profile; `Initialize` resets itinerary/index/state/dwell. **[C]** |
| 18 | Which components are reset on reuse? | `PedestrianAgent` (itinerary, `currentIndex`, `state`, `dwellTime`, `currentLegStopOverride`, subscriptions); `MoveToTarget` (`arrived`, `moving`, `lastPosition`, `smoothedSpeed`, resample counters, re-warp + `SetDestination`); `NavMeshAgent` (`Warp`, `ResetPath`, shape/speed re-applied). **[C]** |
| 19 | Which state variables are reset? | See §10 / §12. `agentID` and `gameObject.name` are **not** reset. **[C]** |
| 20 | Which state can leak? | `UncertainBehavior.originalSpeed` (captured once in `Start`, stale after a later profile re-roll), `UncertainBehavior.state` / timers / `detourArrivalRetryCount` (not reset), `MoveToTarget.externallyPaused` (differs between first life and reuse), any in-flight `FaceYawCoroutine` (Unity stops it but `facingCoroutine` handle cleared in `OnDisable`). `PedestrianAgent.agentID` deliberately persists. **[C/I]** |

---

## 3. SpawnerManager (detail)

### Spawn trigger
- **[C]** `SpawnLoop()` is a `while(true)` coroutine started after pool init. It never stops; it re-checks a gate each `spawnInterval`.
- **[C]** Spawning is **periodic**, fixed interval `spawnInterval = 0.5 s`. Not random, not event-per-spawn. It **is** event-gated at the trial boundary: `trialActive` (set by `OnTrialStart` / cleared by `OnTrialEnd`).
- **[C]** Count control: there is no target count. It spawns continuously at 2 Hz until `liveAgents == maxAgents` (**5**), then resumes as agents finish. Over a long trial this is a steady-state population of ~5 (minus travel/return gaps).
- **[C]** Max simultaneous active NPCs = `maxAgents = 5`. Pool prewarm = 10 (so up to 10 can exist as GameObjects; only 5 active).
- **[C]** Part/phase gating: yes — one continuous stimulus window per Part, coincident with `OnTrialStart`→`OnTrialEnd`. `pauseLiveAgentsOutsideTrial = true` freezes survivors between trials (`MoveToTarget.PauseExternal(true, true)`); they resume on the next `OnTrialStart`. `despawnLiveAgentsOnTrialEnd = false` so they are **not** cleared.
- **[C]** Not active "throughout the experiment" — only within trials. Before the first `OnTrialStart` and in inter-trial gaps, no new agents spawn.
- **[I]** Because Part 2 fires `OnTrialStart` once and `OnTrialEnd` once (with a mid-part teleport in between), the crowd persists across the Part 2 teleport.

### Spawn location
- **[C]** Two independent choices:
  - **Itinerary entrance:** `entrances[Random.Range(0, entrances.Length)]` — 10 candidates. Uniform. `UnityEngine.Random`.
  - **Physical spawn point:** always sampled around the single `startTransform` (inspector slot filled), via `SampleNavMeshNear(startTransform.position, spawnSampleRadius = 1, 8)`, which jitters with `Random.insideUnitCircle`. Only if `startTransform` were null would it fall back to the chosen entrance.
- **[C]** So **all agents physically appear in a ~1 m disc around one point** and then walk to their randomly assigned entrance as leg 0.
- **[C]** RNG: `UnityEngine.Random` (global, shared). **Not seeded.**
- **[C]** Different participants → different `UnityEngine.Random` stream state at trial start (depends on frame count, prior `Random` calls in appearance/UI, etc.) → **different entrance sequences, different crowds.**

### NPC creation / pooling
- **[C]** `usePooling = true`, `poolPrewarm = 10`.
- **[C]** Pool built in `PrewarmPoolCoroutine` (yields every 8 instances to avoid an editor hitch). Each: `Instantiate` → NavMesh placement → profile → `SetActive(false)` → `pool.Add`.
- **[C]** Activation: `pool.RemoveAt(last)` + `go.SetActive(true)` in `SpawnAgent`.
- **[C]** Deactivation: `go.SetActive(false)` + `pool.Add(go)` in `OnAgentFinished`.
- **[C]** Reset on reuse: profile re-applied (`ApplyProfileToInstance`), `NavMeshAgent.Warp` + `ResetPath`, `MoveToTarget.ReinitializeAfterEnable`, `PedestrianAgent.Initialize`.
- **[C]** Pool can grow: if `pool.Count == 0` at spawn, `SpawnAgent` `Instantiate`s a fresh one (which will later be `pool.Add`ed on finish → pool effectively grows to the observed concurrency). With `maxAgents = 5 < poolPrewarm = 10` this branch should not normally run.
- **[C]** Objects are **never destroyed** while pooling (only `Destroy` in the non-pooling branch of `OnAgentFinished`).

### Active-NPC tracking
- **[C]** `private List<GameObject> activeAgents` — the authoritative live set. An entry is added at the end of `SpawnAgent` (after `Initialize`) and removed in `OnAgentFinished` (and via `ForceFinishAllActiveAgents` on trial end when configured).
- **[C]** Also `private int liveAgents` (count), `private List<GameObject> pool` (inactive set).
- **[C]** **No public accessor, no property, no event exposes `activeAgents`.** The only publicly observable lifecycle hook is `PedestrianAgent.onAgentFinished` (an `Action<PedestrianAgent>` field, currently subscribed by `SpawnerManager`). There is **no spawn event**.
- **[C]** Therefore a capture system **cannot currently** read the live set without either (a) `FindObjectsByType<PedestrianAgent>()`, or (b) a (new) public accessor / spawn+despawn events on `SpawnerManager`, or (c) subscribing to a (new) per-agent spawn event.

---

## 4. PedestrianAgent (detail)

- **[C] Stored state:** `moveToTarget` ref; `itineraryTransforms` / `itineraryPositions` / `itineraryPositionAttractorInfos`; `usingPositionItinerary`; `currentIndex`; `state`; `manager`; `dwellTime`; `arriveTolerance` (0.6, serialized); `currentLegStopOverride` (`Vector3?`); `facingCoroutine`; `agentID` (serialized string); `subscribedToMoveToArrived`; `uncertainBehavior` ref; `startMovementCoroutine`; `moveReadyPollTimeout` (3, serialized); static `s_globalAgentCounter`.
- **[C] State machine:** `enum State { Moving, WaitingAtAttractor, Finished }` — **private**, `state` field is **private**. No public getter. Not serialized.
- **[C] Transitions:**
  - `Moving → WaitingAtAttractor`: in `OnReachedWaypoint` → one of `WaitAtAttractor` / `PollForCapacity` / `SimpleDwellThenAdvance` (each sets `state = WaitingAtAttractor`).
  - `WaitingAtAttractor → Moving`: `AdvanceItinerary()` (after the dwell `WaitForSeconds`) sets `state = Moving` and calls `MoveToCurrent()`.
  - `* → Finished`: `Finish()` (exit reached / itinerary exhausted) or `ForceFinish()`.
  - There is **no** explicit reset of `state` back to `Moving` on `OnDisable`; `Initialize`/`InitializePositions` set `state = Moving` on reuse.
- **[C] `AgentID`:**
  - `public string AgentID => agentID;` (read-only property).
  - Assigned in `Awake()` **only if `string.IsNullOrEmpty(agentID)`**: `agentID = $"NPC-{Interlocked.Increment(ref s_globalAgentCounter):000000}"`.
  - `gameObject.name` gets ` [NPC-000003]` appended once (guarded by `Contains`).
  - **Persistent across pooling/reuse** — `Awake` runs once per GameObject; pooled objects are never re-`Instantiate`d, so `agentID` is fixed for the object's whole runtime life.
  - It represents **the pooled GameObject / pool slot**, *not* the NPC lifetime and *not* a fresh GameObject per spawn.
  - **Two different NPC lifetimes CAN and DO have the same `AgentID`** (every reuse of a slot).
  - **[I]** `s_globalAgentCounter` is `static`; with Unity's default "Reload Domain on Enter Play Mode" it resets to 0 each play session, so IDs restart at `NPC-000001`. **[U]** whether domain reload is disabled in this project's Editor settings (not in the inspected files) — if disabled, IDs would keep climbing across play sessions.
- **[C] Lifecycle methods present:**
  - `Awake()` — ID assignment, cache `moveToTarget` (warns if missing), `moveToTarget.SetAgentID(agentID)`, cache `uncertainBehavior`.
  - **No `Start()`.**
  - `Initialize(List<Transform>, SpawnerManager, float)` — transform itinerary path (the active one).
  - `InitializePositions(List<Vector3>, List<AttractorInfo>, SpawnerManager, float)` — position path (inactive in this scene).
  - `Update()` — arrival polling only while `state == Moving`.
  - `BeginMovementWhenReady()` / `WaitUntilMoverReadyThenMove()` (coroutine) — deferred first move.
  - `MoveToCurrent()` — issues the move for `currentIndex`, sets/clears `currentLegStopOverride`, (un)subscribes `OnArrived`.
  - `OnMoveToArrived()` / `OnReachedWaypoint()` — arrival handling.
  - `SimpleDwellThenAdvance()` / `WaitAtAttractor(info)` / `PollForCapacity(info)` (coroutines) — dwell.
  - `AdvanceItinerary()` — `currentIndex++`, `Finish()` or next leg.
  - `TryGetAttractorDwellStop(Transform, out Vector3)` / `FaceDwellTargetIfConfigured(info)` / `FaceYawCoroutine(...)` — AttractorDwell integration.
  - `Finish()` / `ForceFinish()` (public) / `ReleaseCurrentAttractorIfAny()`.
  - `OnDisable()` — unsubscribe, `mt.Stop()`, clear coroutine handles + `currentLegStopOverride`.
  - `OnDestroy()` — unsubscribe, `mt.Stop()`.
- **[C] Events:** `public Action<PedestrianAgent> onAgentFinished` (fired in `Finish`/`ForceFinish`).

---

## 5. MoveToTarget (detail)

- **[C] What activates movement:** `MoveTo(Vector3)` / `MoveTo(Transform)` (called by `PedestrianAgent.MoveToCurrent` and by `UncertainBehavior`), `ResumeExternal()`, and initial `Start()` / `ReinitializeAfterEnable()`. Each sets `moving = true`, `arrived = false` and (if `AgentCanControl()`) calls `NavMeshAgent.SetDestination`.
- **[C] Target representation:** `Transform targetTransform` **or** `Vector3 targetPosition` (used when `targetTransform == null`). `GetTargetPosition()` returns whichever is active. Attractor legs use `targetPosition` (transform nulled); entrance/exit legs use `targetTransform`.
- **[C] Target position source:** `targetTransform.position` or the literal `targetPosition`. For attractor legs it is the `AttractorDwell` random standing point passed in by `PedestrianAgent`.
- **[C] NavMeshAgent config (`Start` / `ReinitializeAfterEnable`):** `agent.updatePosition = true`, `agent.updateRotation = false` (script owns yaw). `agent.speed` is **only** written if `autoSetAgentSpeed == true` — it is **false** on the prefab, so speed comes from `AgentFactory`/profile. `agent.isStopped` toggled via `SafeSetIsStopped` (guards `enabled` + `isOnNavMesh`).
- **[C] Where `SetDestination` is called:** `Start()` (initial, with up to `k_MaxSampleRetries = 6` `NavMesh.SamplePosition` nudge attempts), `ReinitializeAfterEnable()`, `MoveTo(Vector3)`, `MoveTo(Transform)`, `ResumeExternal()`, and `Update()` resample branch (when `!agent.hasPath`).
- **[C] Continuous destination update:** only if `targetTransform != null`, and then throttled by `targetUpdateInterval` — **scene value `10` s** (prefab), so effectively near-static (fine, since entrance/exit transforms don't move). Attractor legs (`targetPosition`) are set once.
- **[C] Completion detection:** in `Update()`, when `!externallyPaused`:
  `arrived = true; moving = false; SafeSetIsStopped(true); OnArrived?.Invoke()` when
  `dist <= arriveThreshold (0.2)` **or** `(!agent.pathPending && GetRemainingDistanceSafe(agent) <= 0.2)`.
  Also a "no path but already at target" branch marks arrival.
  **Important:** `externallyPaused` defaults to **`true`** (field initializer *and* prefab). It is only set false by `ResumeExternal()`. On a pooled object's **first** life it is still `true` during leg 0 (entrance), so `MoveToTarget` never fires `OnArrived` for that leg — `PedestrianAgent`'s 0.6 m poll handles it. After the first `SimpleDwellThenAdvance`→`ResumeExternal`, `externallyPaused` is false for the rest of that life. On **reuse**, it is already false (left over from the previous life's last `ResumeExternal`).
- **[C] Unreachable / no path:** `Update()` resample logic — up to `maxResampleAttempts` (prefab **10**) `NavMesh.SamplePosition` retries with `resampleCooldown` (prefab **5 s**) between them; if the sampled point is basically the current position it marks arrived; otherwise it logs "No active path…" once (`suppressRepeatedNoPathLogs`) and gives up until the target changes. The agent then **sits still, still marked `moving`, never `arrived`** → `PedestrianAgent` won't advance and (unless its 0.6 m poll trips) the agent is stuck until trial end.
- **[C] Stuck agent:** no dedicated stuck timer/teleport. `PedestrianAgent` has no watchdog either (only `PollForCapacity` has a 30 s timeout, and that's for capacity, not pathing).
- **[C] Path status checked:** `agent.hasPath`, `agent.pathPending`, `agent.pathStatus` (logged), `agent.path.corners` (in `GetRemainingDistanceSafe` and the velocity fallback). `NavMeshPathStatus` is **not** explicitly branched on in `MoveToTarget` (it is in `UncertainBehavior.EnterDetour`).
- **[C] Velocity:** `agent.velocity.magnitude` is the authoritative instantaneous speed (smoothed into `smoothedSpeed` for the animator). `agent.desiredVelocity` is **not** read. A fallback synthesises a direction from `agent.path.corners[1]` when `agent.velocity` is ~0 but a path exists.
- **[C] Frame-rate dependence:** rotation uses `Quaternion.Slerp(..., rotationSpeed * Time.deltaTime)` and speed smoothing uses `dt * speedSmoothing` — both `deltaTime`-scaled (frame-rate independent in the limit). `NavMeshAgent` translation is Unity-internal (fixed-ish). Resample cooldowns use `Time.time`.
- **[C] Fields useful for logging:** `IsMoving` (`moving`), `IsArrived` (`arrived`), `IsReadyToMove()` (`AgentCanControl`), `agent` (→ `velocity`, `desiredVelocity`, `remainingDistance`, `pathStatus`, `hasPath`, `pathPending`, `destination`, `steeringTarget`, `path.corners`), `targetTransform` / `targetPosition` / `GetTargetPosition()` (private — but `targetPosition` and `targetTransform` are public), `startTransform` / `startPosition`, `speed`, `externallyPaused`, `AgentID`, `smoothedSpeed` (private), `OnArrived` event.

---

## 6. AttractorDwell (detail)

- **[C] Entering dwell:** not driven by `AttractorDwell` itself. `PedestrianAgent.OnReachedWaypoint` decides arrival; if the waypoint is a registered `AttractorInfo` it runs `WaitAtAttractor` / `PollForCapacity`, which call `FaceDwellTargetIfConfigured(info)` → `dwell = info.transform.GetComponent<AttractorDwell>()`.
- **[C] Arrival detection:** entirely in `MoveToTarget` + `PedestrianAgent` (see §5). `AttractorDwell` only supplies the *destination point* for the leg via `TryGetRandomStandingPoint`.
- **[C] Dwell duration:** **not** in `AttractorDwell`. It is the single `dwellTime` from `SpawnerManager.SpawnAgent` (`Random.Range(5, 15)`, `UnityEngine.Random`, unseeded), reused for every stop.
- **[C] Random standing point:** `TryGetRandomStandingPoint` → up to `maxAttempts` (24) candidates: `GetRandomPointInArea` (uniform in the MeshCollider's local bounds, transformed to world) → `TryVerticalProjectToNavMesh` (top-down column scan, `verticalStep 0.3`, `navMeshSampleRadius 0.5`, `horizontalTolerance 0.25`, keeps random X/Z, takes Y from NavMesh). On total failure the caller keeps the plane's `transform.position`.
- **[C] RNG:** **private `System.Random`**, seeded per instance: `new System.Random(unchecked(Environment.TickCount * 397) ^ GetInstanceID())`. Time-based → **different every run**, and **not recorded anywhere**. Deliberately isolated so it doesn't perturb the route `UnityEngine.Random` sequence.
- **[C] Facing during dwell:** if `facingTarget` is assigned, `PedestrianAgent.FaceYawCoroutine` yaw-rotates the agent toward it at `facingTurnSpeedDeg` (360°/s, near-instant) and then holds. Yaw only (`dir.y = 0`). If `facingTarget` is null, the NPC keeps its arrival heading.
- **[C] What `facingTarget` is:** a single `Transform` reference authored **per attractor plane, in the Inspector**. Every NPC that dwells at that plane faces the **same** target regardless of where it randomly stopped.
- **[C] `enableRandomStop`** (default true) gates only the random-standing-point feature; `facingTarget` still works when it is false.
- **[C] How dwell ends:** the `WaitForSeconds(dwellTime)` in `PedestrianAgent`'s dwell coroutine elapses → `ReleaseAttractor` → `mt.ResumeExternal()` → `uncertainBehavior?.ResumeAfterArrival()` → `AdvanceItinerary()`. `FaceYawCoroutine`'s loop condition (`state == WaitingAtAttractor`) fails once `AdvanceItinerary` sets `state = Moving`.

### Is `facingTarget` a directional-majority manipulation?

**[C] No — it is category (2): an environmental / static, authored directional configuration, realised as an NPC behavioural rule (category 3).**

- It is **not** a formal experimental manipulation in code: nothing ties `facingTarget` to participant group, Part, condition, trial index, or any randomisation. `ExperimentManager` never references `AttractorDwell`. There is no "directional majority" computation, no counting of NPC headings, no per-condition assignment.
- It is a **fixed scene authoring choice**: whoever places the attractor planes optionally drags one Transform per plane; all dwellers at that plane then look the same way. Its experimental meaning (if any) lives entirely in how the scene was authored, **[U]** which is outside the inspected files.
- Mechanically it is an **NPC behavioural rule** (yaw-to-face while dwelling), nothing more.

---

## 7. NavMesh / movement environment

- **[C]** NPCs use `UnityEngine.AI.NavMeshAgent` (root of `Npc.prefab`), `m_AgentTypeID: 0` (the default "Humanoid" agent type). **[U]** the baked NavMesh settings file for this scene was not opened in this pass; CLAUDE.md notes `Assets/Scenes/LyonVRVity35/NavMesh-NavMesh 2.asset` is already modified in the tree.
- **[C] Prefab NavMeshAgent values:** `radius 0.2`, `speed 1.4`, `acceleration 8`, `angularSpeed 180`, `stoppingDistance 0.1`, `height 2`, `avoidancePriority 50`, `obstacleAvoidanceType 4` (**High Quality** avoidance), `autoBraking 1`, `autoRepath 1`, `autoTraverseOffMeshLink 1`, `areaMask 0xFFFFFFFF` (all areas).
- **[C] Runtime overrides (`AgentFactory` from `Agent Profile.asset`):** `radius 0.3`, `height 1.8`, `acceleration 8`, `angularSpeed 120`, `avoidancePriority 50`, `speed = 1.0 × {0.6 | 1.4 | 2.2}` (weighted 0.25 / 0.5 / 0.25). So effective walking speed per agent ∈ **{0.6, 1.4, 2.2} m/s**. `stoppingDistance` is left at the prefab's `0.1` (profile has no field for it). `MoveToTarget.autoSetAgentSpeed = false` so `MoveToTarget` does not fight the profile speed.
- **[C] Dynamic obstacles:** the scene contains **1490 `NavMeshObstacle` components**, all `m_Shape: 1` (box), `m_Extents 0.5`, `m_Carve: 1`, `m_CarveOnlyStationary: 1`, `m_MoveThreshold 0.1`, `m_TimeToStationary 0.5`. **[I]** these are on street props / building-adjacent objects (identical size, all carving). Carving obstacles cut holes in the NavMesh → NPC paths route around them.
- **[C] Participant `NavMeshObstacle`:** CLAUDE.md states the `OVRCameraRig` carries a scene-added `NavMeshObstacle`. **[U]** I could not positively locate that component's parameters in the scene YAML in this pass (the rig's `m_AddedComponents` list I found held 2 `OVREyeGaze` + `UniversalAdditionalCameraData`). If it exists and carves, the moving player would repeatedly re-carve the NavMesh under/around themselves; if `CarveOnlyStationary` it only carves when the player holds still ≥ `TimeToStationary`. **This needs on-scene verification.**
- **[C]** Buildings/river/terrain: NPC navigation is defined by the baked NavMesh (built from the TIN terrain colliders + carving obstacles). **[I]** buildings are excluded (carved / not walkable), the river is not walkable (that's why the experiment uses `RiverTeleportPoint` for the *player*), terrain is the walkable surface. **[U]** exact bake include/exclude — not inspected.
- **[C]** Can NPCs be blocked by the participant? **Only if** the rig has an enabled carving `NavMeshObstacle`. A plain (non-carving) obstacle or none → NPCs treat the player as nothing and can walk through the rig (the rig has no solid collider blocking agents; agent-vs-agent uses RVO avoidance, agent-vs-player does not unless the player is also an agent/obstacle).
- **[C]** Local avoidance nondeterminism: `obstacleAvoidanceType 4` (High Quality RVO) means when ≥2 agents are near each other, each one's sidestep depends on the others' exact positions/velocities that frame → **order- and timing-sensitive, not reproducible**. With `maxAgents = 5` this happens often near attractors (capacity 5, all dwelling in one plane) and near the shared spawn point.
- **[C] Interaction NPC agent ↔ player:** `UncertainBehavior` (when enabled) and `MoveToTarget` both call `SetDestination`/`isStopped` on the **same** `NavMeshAgent` → competing controllers, extra nondeterminism for ~20% of agents. The player's locomotion (`ExperimentManager.TeleportPlayerTo`, `RiverTeleportPoint`, Omnideck, `AutoGroundAlign`) only moves the rig transform; it influences NPCs solely through the rig's `NavMeshObstacle` (if any).

---

## 8. Randomness table

All RNG is the shared global `UnityEngine.Random` unless noted. **Nothing calls `Random.InitState`. No seed is stored.**

| Random process | Script / method | Generator | Seeded? | When used |
|---|---|---|---|---|
| Physical spawn point jitter | `SpawnerManager.SampleNavMeshNear` (`Random.insideUnitCircle`) | `UnityEngine.Random` | No | every `SpawnAgent`, per NavMesh sample attempt |
| Entrance selection (itinerary\[0]) | `SpawnerManager.SpawnAgent` (`Random.Range(0, entrances.Length)`) | `UnityEngine.Random` | No | every spawn |
| Number of attractors (1–4) | `SpawnerManager.BuildItinerary` (`Random.Range(min=1, max+1=5)`) | `UnityEngine.Random` | No | every spawn |
| Attractor selection (distinct subset, order) | `SpawnerManager.BuildItinerary` (`Random.Range(0, indices.Count)` + `RemoveAt`) | `UnityEngine.Random` | No | every spawn |
| Exit selection | `SpawnerManager.BuildItinerary` (`exits[Random.Range(0, exits.Length)]`) | `UnityEngine.Random` | No | every spawn |
| Dwell duration (one per agent) | `SpawnerManager.SpawnAgent` (`Random.Range(5, 15)`) | `UnityEngine.Random` | No | every spawn |
| Per-agent speed multiplier | `AgentProfile.ChooseWeightedSpeedMultiplier` (`Random.value`) via `AgentFactory.ApplyProfileToInstance` | `UnityEngine.Random` | No | every spawn / reuse |
| Appearance (mesh + material per part) | `AppearanceController.ApplyPart` (`ChooseWeightedOption` → `Random.value`; material `Random.Range`) | `UnityEngine.Random` | No | every spawn / reuse |
| `UncertainBehavior` enable (p = 0.2) | `AgentFactory.ApplyProfileToInstance` (`Random.value < uncertainProbability`) | `UnityEngine.Random` | No | every spawn / reuse |
| Attractor dwell standing point | `AttractorDwell.GetRandomPointInArea` / `RandRange` | **`System.Random`** (per-instance) | **seeded from `Environment.TickCount ^ GetInstanceID()`** — time-based, not recorded | every attractor visit |
| Movement uncertainty: pause/hesitate/slow/detour triggers | `UncertainBehavior.Update` (`Random.value < prob*dt`) | `UnityEngine.Random` | No | **every frame** while enabled + not suspended (frame-rate dependent) |
| Movement uncertainty: state durations | `UncertainBehavior.Enter*` (`Random.Range(min,max)`) | `UnityEngine.Random` | No | on each uncertain-state entry |
| Movement uncertainty: detour target sampling | `UncertainBehavior.EnterDetour` (`Random.insideUnitCircle`) | `UnityEngine.Random` | No | on detour entry, up to 8 attempts |
| Movement uncertainty: hesitate turn direction | `UncertainBehavior.DoHesitate` (`Random.value > 0.5`) | `UnityEngine.Random` | No | every frame during Hesitate |
| `MoveToTarget` initial-destination nudge | `MoveToTarget.Start` (`Random.insideUnitCircle`) | `UnityEngine.Random` | No | only when first `SamplePosition` fails |
| RVO local avoidance | `NavMeshAgent` (`obstacleAvoidanceType 4`) | Unity internal | n/a | continuously when agents are close |
| Part 2 / Part 3 start & teleport & building picks | `ExperimentManager` (`UnityEngine.Random.Range`) | `UnityEngine.Random` | No | at part start (also consumes the shared stream) |

---

## 9. State machine (ASCII)

`PedestrianAgent.State` has only **three** values. The richer picture below annotates the sub-states that live inside `WaitingAtAttractor` (as coroutines) and the itinerary index.

```
                         Instantiate (prewarm)
                                 │  Awake(): assign agentID, cache refs
                                 ▼
                        ┌─────────────────┐
                        │  POOLED (inactive)   │◄──────────────────────────┐
                        └─────────────────┘                           │
                                 │ SpawnAgent(): SetActive(true),           │
                                 │ ApplyProfile, Initialize(itinerary,dwell)│
                                 ▼                                         │
                        ┌─────────────────┐                           │
                        │ WAIT-FOR-MOVER-READY │  (coroutine, ≤3 s)        │
                        │ poll IsReadyToMove() │                           │
                        └─────────────────┘                           │
                                 │ MoveToCurrent()                         │
                                 ▼                                         │
   currentIndex = 0    ┌──────────────────────┐                        │
   ┌──────────────────►│  state = Moving          │                        │
   │                   │  leg = itinerary[idx]    │                        │
   │                   │  NavMeshAgent.SetDestination                       │
   │                   └──────────────────────┘                        │
   │                             │ arrival:                                │
   │                             │  • MoveToTarget.OnArrived (if !externallyPaused)
   │                             │  • OR PedestrianAgent.Update dist ≤ 0.6  │
   │                             ▼                                         │
   │                   ┌──────────────────────┐                        │
   │                   │  OnReachedWaypoint()      │                        │
   │                   └──────────────────────┘                        │
   │            ┌───────────────┼──────────────────────┐               │
   │       idx==0 or not a      idx is a registered       idx == last    │
   │       registered attractor  AttractorInfo            (exit)          │
   │            │                │                          │            │
   │            ▼                ▼                          ▼            │
   │  ┌──────────────┐  ┌──────────────────┐      ┌────────────┐  │
   │  │ state =          │  │ state =              │      │  Finish()      │  │
   │  │ WaitingAtAttractor│  │ WaitingAtAttractor   │      │  state=Finished│  │
   │  │ SimpleDwellThen  │  │ TryEnterAttractor:   │      │  mt.Stop()     │  │
   │  │ Advance:         │  │  ok → WaitAtAttractor │      │  onAgentFinished│ │
   │  │  PauseExternal   │  │  full→ PollForCapacity│      └────────────┘  │
   │  │  wait(dwell)     │  │ FaceYawCoroutine     │              │           │
   │  │  ResumeExternal  │  │  (if facingTarget)   │              ▼           │
   │  └──────────────┘  │  wait(dwell)         │      SpawnerManager        │
   │            │            │  ReleaseAttractor    │      .OnAgentFinished:     │
   │            │            │  ResumeExternal      │      liveAgents--,         │
   │            │            └──────────────────┘      activeAgents.Remove,   │
   │            │                    │                    SetActive(false),      │
   │            └────────┬───────────┘                    pool.Add ──────────────┘
   │                     ▼
   │           ┌──────────────────┐
   │           │ AdvanceItinerary()   │  currentIndex++
   │           │  idx < count → Moving │
   │           │  idx >= count → Finish│
   │           └──────────────────┘
   │                     │ idx < count
   └─────────────────────┘
```

`UncertainBehavior` (when enabled) runs a **parallel, independent** state machine on the same GameObject:
`Walking → {Pause | Hesitate | Slow | Detour} → Walking`, plus a terminal `suspendedBecauseArrived` latch set on the first main-target arrival and cleared by `ResumeAfterArrival()` (called from `PedestrianAgent` after every dwell and on `Initialize`). It has no `Finished` state and no awareness of the itinerary.

---

## 10. Pooling and identity

**[C] The architecture is exactly:**

```
NPC GameObject  (created once at prewarm; never destroyed)
      │  fixed for the object's whole runtime life:
      │     • PedestrianAgent.agentID   e.g. "NPC-000003"
      │     • gameObject.name           "... [NPC-000003]"
      │     • the component instances (NavMeshAgent, MoveToTarget, …)
      ▼
Pool slot / AgentID
      │  reused by:
      ▼
multiple sequential NPC lifetimes
   (each = one Initialize(itinerary,dwell) → … → Finish() → SetActive(false))
```

`agentID` is assigned in `Awake()` guarded by `if (string.IsNullOrEmpty(agentID))`. `Awake` runs once per GameObject (at `Instantiate`). Pooled objects are only `SetActive`-toggled, so `agentID` **cannot** change between lifetimes.

**[C] Concrete example:**

```
Pool object:  agentID = "NPC-000003"   (assigned once, at prewarm Instantiate)

Lifetime 1:
    agentID              = "NPC-000003"
    itinerary            = [entranceA, attr2, attr5, exitB]     (BuildItinerary, life 1)
    dwellTime            = 8.3 s                                (Random.Range, life 1)
    speed multiplier     = 1.4                                  (life 1 profile roll)
    UncertainBehavior    = disabled                             (Random.value >= 0.2)
    spawn-instance ID    = (none — does not exist)
  → walks route, Finish(), SetActive(false), pool.Add

Lifetime 2 (same GameObject popped again):
    agentID              = "NPC-000003"    ← SAME
    itinerary            = [entranceD, attr1, exitD]            (BuildItinerary, life 2)
    dwellTime            = 12.9 s                               (Random.Range, life 2)
    speed multiplier     = 0.6                                  (life 2 profile roll)
    UncertainBehavior    = enabled                              (Random.value < 0.2)
    spawn-instance ID    = (none — does not exist)
```

**[C] Which existing variable distinguishes lifetime 1 from lifetime 2?**
**None.** There is no spawn counter, no spawn timestamp, no lifetime GUID, no "activation index". `agentID`, `gameObject.name`, `GetInstanceID()`, and the transform are all identical across the two lifetimes. The itinerary contents, `dwellTime`, appearance and speed differ, but those are not identifiers and are not logged in a way that ties a row to a lifetime. **A per-lifetime identifier does not currently exist and must be added for the capture layer.**

---

## 11. Determinism / replayability

> **Can an NPC trajectory currently be reproduced from the initial conditions alone?**

**[C] No.**

Reasons, in rough order of impact:

1. **[C] No RNG seeding.** `UnityEngine.Random` is never seeded (`Random.InitState` appears nowhere). The stream state at `OnTrialStart` depends on frame count, editor vs build, and every prior `Random` call (UI, appearance, `ExperimentManager` part setup). Two runs → different entrance / attractor-count / attractor-set / exit / dwell / speed / appearance sequences.
2. **[C] `AttractorDwell` uses a time-seeded `System.Random`** (`Environment.TickCount ^ GetInstanceID()`) that is **never recorded**. Even if you fixed `UnityEngine.Random`, every NPC's actual standing point inside each attractor would still differ every run.
3. **[C] Spawn timing is wall-clock/frame coupled.** `SpawnLoop` uses `WaitForSeconds(0.5)` and gates on `liveAgents`; the exact frame an agent finishes (and thus the next spawn) depends on frame rate and path lengths, which feeds back into which `Random` call lands on which spawn.
4. **[C] RVO local avoidance (`obstacleAvoidanceType 4`)** makes concurrent agents' paths depend on each other's sub-frame state — chaotic, not reproducible, especially with 5 agents converging on capacity-5 attractor planes.
5. **[C] `UncertainBehavior`** (≈20% of agents) samples `UnityEngine.Random` **every frame** (`prob * Time.deltaTime`), so its perturbations are frame-rate dependent even given a fixed seed.
6. **[C] `MoveToTarget` defensive resampling** (`NavMesh.SamplePosition` nudges, `Random.insideUnitCircle`) is triggered conditionally on transient "no path" states.
7. **[C] Frame-rate–dependent control:** rotation slerp and animator smoothing are `Time.deltaTime`-scaled; `NavMeshAgent` stepping is tied to the sim tick. Different frame pacing → slightly different trajectories that then diverge.
8. **[C] Pooled-object state leaks** (`UncertainBehavior.originalSpeed`, un-reset `state`/timers; `MoveToTarget.externallyPaused` first-life vs reuse) make even a single object's behaviour depend on its own history.

**[I] What *would* be sufficient for offline reconstruction** (not a request to implement — just the implication): record, per NPC lifetime, at ≥10 Hz: position, rotation, `NavMeshAgent.velocity`, `NavMeshAgent.desiredVelocity`, `destination`/current target, `pathStatus`, `hasPath`, and the discrete state (`Moving` / `WaitingAtAttractor` / dwell target id) — plus one-time spawn metadata (entrance, full itinerary, dwell, speed, uncertain on/off). With a dense pose+velocity log you can reconstruct the path by interpolation without replaying the RNG. Without such a log, the trajectory is **not** recoverable.

---

## 12. Data-capture integration points (safest, no code written)

| Signal | Safest integration point | Notes |
|---|---|---|
| **`npc_spawn`** | End of `SpawnerManager.SpawnAgent()`, right after `agentScript.Initialize(...)` / `InitializePositions(...)` and `activeAgents.Add(go)`. | At that instant the itinerary, `dwell`, profile speed, entrance index and `AgentID` are all known and the object is active. A new spawn event on `SpawnerManager` (or a hook there) is the natural place. `PedestrianAgent.Awake` is **too early** (no itinerary, runs during prewarm). |
| **`npc_despawn`** | `SpawnerManager.OnAgentFinished(PedestrianAgent)` (fires for both `Finish` and `ForceFinish`), before `go.SetActive(false)`. Equivalently, subscribe to `PedestrianAgent.onAgentFinished`. | This is the single choke point for every termination path (exit reached, trial-end force-finish). `OnDisable` also fires but also fires on trial-end pause scenarios / scene unload — less clean. |
| **List of active NPCs (10 Hz)** | `SpawnerManager.activeAgents` — but it is `private`. Cleanest: a new public read-only accessor (`IReadOnlyList<GameObject>` / `IReadOnlyList<PedestrianAgent>`) or spawn/despawn events, maintained exactly where `activeAgents.Add/Remove` already happen. | Avoids `FindObjectsByType` each sample. The list is already correct (added after `Initialize`, removed in `OnAgentFinished`). |
| **NPC state** | `PedestrianAgent.state` (`Moving` / `WaitingAtAttractor` / `Finished`) — currently **private**, needs a public getter. Combine with `currentIndex` and `itineraryTransforms[currentIndex]` to know *which* leg / *which* attractor. | The 3-value enum is the real state. `MoveToTarget.IsMoving` / `IsArrived` are a coarser secondary signal. Dwell vs travel = `state == WaitingAtAttractor`. |
| **Position** | `PedestrianAgent.transform.position` (the visible mesh root; `NavMeshAgent.updatePosition = true` so transform == agent position). | Same transform `TrajectoryRecorder` would use. |
| **Velocity** | `NavMeshAgent.velocity` (world-space, this is what `MoveToTarget` already treats as authoritative). | `MoveToTarget.smoothedSpeed` is animator-smoothed — not for analysis. |
| **Desired velocity** | `NavMeshAgent.desiredVelocity`. | Not currently read anywhere; useful to distinguish "wants to move but blocked" (RVO / carve) from "intentionally stopped". Log **both** `velocity` and `desiredVelocity`. |
| **Destination / current target** | `NavMeshAgent.destination` (post-resample truth) and/or `MoveToTarget.targetPosition` / `MoveToTarget.targetTransform` (intent). For attractor legs, `PedestrianAgent.currentLegStopOverride` is the exact `AttractorDwell` point (private → needs a getter). | `destination` and the `MoveToTarget` target can differ briefly during resampling; capture the `MoveToTarget` intent plus `NavMeshAgent.destination`. |
| **Dwell start** | Entry of `WaitAtAttractor` / `PollForCapacity` / `SimpleDwellThenAdvance` (all set `state = WaitingAtAttractor` and call `mt.PauseExternal(true, true)`). A single hook = the `state` transition to `WaitingAtAttractor`, or subscribe to `MoveToTarget` gaining `externallyPaused && externallyHaltMovement`. | Also record whether it's an entrance stop (`currentIndex == 0`), an attractor dwell (with attractor id + the `AttractorDwell` point + `facingTarget`), or a capacity wait. |
| **Dwell end** | The line after `WaitForSeconds(dwellTime)` in those coroutines, i.e. the `mt.ResumeExternal()` call, immediately before `AdvanceItinerary()`. Equivalently the `state` transition `WaitingAtAttractor → Moving` inside `AdvanceItinerary`. | `PollForCapacity` has two exits (admitted, or 30 s timeout) — both reach `ResumeExternal` + `AdvanceItinerary`. |
| **Destination / target change** | `PedestrianAgent.MoveToCurrent()` (fires once per leg, sets `currentLegStopOverride` and calls `mt.MoveTo`). Also `MoveToTarget.MoveTo(Vector3)` / `MoveTo(Transform)` (called by `MoveToCurrent` **and** by `UncertainBehavior` detours). | Hooking `MoveToTarget.MoveTo` catches both itinerary legs and uncertain detours; hooking `MoveToCurrent` catches only itinerary legs. |
| **Spawn-instance (lifetime) ID** | Assign in `SpawnerManager.SpawnAgent()` at the same point as `npc_spawn` (a session-scoped `long`/`int` counter, or `agentID + "#" + lifetimeCount`). Store it on `PedestrianAgent` (new field) so every later sample and the despawn event can read it. | Must **not** reuse `agentID` alone. `Awake` is the wrong place (prewarm, one-time). |
| **Lifetime ID invalidation** | `SpawnerManager.OnAgentFinished` (emit `npc_despawn` with the id, then clear the field / mark it stale). Also clear defensively in `PedestrianAgent.OnDisable`. | The capture layer should treat a sample with a stale/blank lifetime id as "no live NPC in this slot". |

---

## 13. Potential issues (relevant to logging / reconstruction / validity / pooling / randomness / NavMesh)

**Identity & pooling**
1. **[C]** No per-lifetime NPC id — `AgentID` = pool slot. Any log keyed only on `AgentID` will silently merge many pedestrians into one "track". *(Blocking for the capture layer.)*
2. **[C]** `PedestrianAgent.state` and `currentIndex` and `currentLegStopOverride` are private — no read path for a capture system without modifying the class.
3. **[C]** `SpawnerManager.activeAgents` is private with no accessor and there is no spawn event — capture would be forced onto `FindObjectsByType` unless the class is extended.
4. **[C]** `UncertainBehavior.originalSpeed` is captured once in `Start()`. On pooled reuse with a different speed-multiplier roll, `originalSpeed` is stale, so a post-`Slow`/`Detour` `ExitCurrentState()` restores the **wrong** speed for the rest of that life.
5. **[C]** `UncertainBehavior.state` / `stateTimer` / `detourArrivalRetryCount` are not reset on reuse — an object pooled mid-`Detour` starts its next life with a stale uncertain-state.
6. **[C]** `MoveToTarget.externallyPaused` defaults `true`; on a pooled object's **first** life the entrance leg cannot raise `OnArrived` (only the 0.6 m `PedestrianAgent` poll works), but on every **reuse** it can. First vs subsequent lifetimes of the same slot are not behaviourally identical.

**Randomness / reproducibility**
7. **[C]** `UnityEngine.Random` unseeded → crowds differ per participant/run (§11).
8. **[C]** `AttractorDwell` `System.Random` is time-seeded and unrecorded → standing points unrecoverable.
9. **[C]** `UncertainBehavior` samples `Random` per-frame (`prob * dt`) → frame-rate-dependent behaviour; a Quest frame-rate dip changes the perturbation pattern.
10. **[C]** `ExperimentManager` Part 2/3 randomisation draws from the **same** `UnityEngine.Random` stream as NPC generation, so experiment-condition rolls and crowd rolls are entangled.

**NavMesh / movement**
11. **[C]** RVO `obstacleAvoidanceType 4` + `maxAgents 5` + capacity-5 attractor planes → up to 5 agents dwelling in one plane, continuously avoiding each other; trajectories there are chaotic.
12. **[C]** No stuck-agent watchdog. If `MoveToTarget` exhausts `maxResampleAttempts` (10) it leaves the agent `moving = true, arrived = false` forever; `PedestrianAgent` never advances that leg. Such an agent occupies one of the 5 live slots for the rest of the trial and (if at an attractor) can hold a capacity slot until `Finish`.
13. **[C]** `UncertainBehavior` (agent-fallback mode, since `moveController` is unassigned on the prefab) and `MoveToTarget` both write `NavMeshAgent.isStopped` / `SetDestination` on the same agent — for the ~20% of agents with it enabled, "who set the current destination" is ambiguous and can flip frame to frame.
14. **[C]** All agents spawn in a ~1 m disc around a single `startTransform`, then fan out to 10 entrances — a visible "clump then disperse" pattern at the spawn point, and repeated NavMesh contention there.
15. **[C]** Entrance leg triggers a full `dwellTime` (5–15 s) stop at `entrances[i]` because the entrance is `itinerary[0]` and not a registered `AttractorInfo` → `SimpleDwellThenAdvance`. **[I]** likely unintended (entrances read as "walk in from", not "loiter at the doorway for 10 s").
16. **[C]** `exits[]` and `entrances[]` are the **same 10 transforms** in the scene, so an NPC can be assigned an "exit" identical to its "entrance", and the exit stop has no dwell (it's `isExit` → `Finish`) while the identical entrance did dwell.
17. **[C]** `dwellTime` is one value per agent for **every** stop — no per-attractor variation, so dwell duration cannot be used as an independent per-location manipulation without changing the spawner.
18. **[U]** Whether the participant rig's `NavMeshObstacle` exists/carves and thus whether the participant can block NPC paths — must be checked in-scene. If it carves while moving, NPC paths near the participant are nondeterministic in a participant-position-dependent way (a validity concern for a wayfinding study).
19. **[C]** `MoveToTarget.targetUpdateInterval = 10 s` on the prefab (vs 0.12 s default) — harmless for static entrance/exit transforms, but if any future waypoint transform moves, the agent would chase it only every 10 s.

**Trial gating**
20. **[C]** With `pauseLiveAgentsOutsideTrial = true` + `despawnLiveAgentsOnTrialEnd = false`, agents frozen at trial end **resume mid-itinerary** at the next `OnTrialStart` — so the first seconds of a trial can contain pedestrians that "teleport into motion" from wherever the last trial left them, with dwell timers that elapsed in real time during the gap (the dwell `WaitForSeconds` is not `Time.timeScale`-affected but the `MoveToTarget` halt is; **[I]** a dwell coroutine that was mid-`WaitForSeconds` when the object was paused via `MoveToTarget.PauseExternal` still counts down, because `PauseExternal` does not stop `PedestrianAgent`'s coroutine).
21. **[C]** `SpawnerManager.experimentManager` inspector slot is empty → resolved by `FindObjectOfType<ExperimentManager>()`. Fine, but if the scene ever has two, the wrong one could be picked.

**Files that write NPC data today**
22. **[C]** `TrajectoryRecorder` *can* be attached to an NPC root (it auto-detects player vs NPC and names the file from `gameObject.name`), but it is a per-object component subscribing to `OnTrialStart/End`; it is **not** placed on the pooled prefab and would need one instance per pooled object, each producing a file named by the slot name — again merging lifetimes. There is **no** central NPC recorder today.

---

## 14. Files / assets / components inspected

Scripts (`Assets/Scripts/`):
- `SpawnerManager.cs`
- `PedestrianAgent.cs`
- `MoveToTarget.cs`
- `AttractorDwell.cs`
- `AttractorArea.cs`
- `AgentFactory.cs`
- `AgentProfile.cs`
- `AppearanceController.cs`
- `UncertainBehavior.cs`
- `ExperimentManager.cs`
- `AutoGroundAlign.cs`
- `TrajectoryRecorder.cs` (partial — header, `Start`, metadata handling, `FindObjectOfTypeByName`)
- referenced but not opened in full: `TrajectoryRecordingManager.cs`, `GazeRecorder.cs`, `ExternalSessionController.cs`, `RiverTeleportPoint.cs`, `TargetSelectable.cs`, `UIManager.cs` (only their interaction with the above was traced)

Scene:
- `Assets/Scenes/LyonVRVity35.unity` — `SpawnerManager` component values; `AgentFactory` component; 7 attractor `Plane` GameObjects (each `MeshCollider` + `AttractorDwell`, **no `AttractorArea`**); NavMeshObstacle census (1490, all carving 0.5³ boxes); `OVRCameraRig` prefab-instance added-components list.

Prefabs / assets:
- `Assets/Prefabs/Npc.prefab` — root `NavMeshAgent` (radius 0.2 / speed 1.4 / accel 8 / angular 180 / stopDist 0.1 / height 2 / obstacleAvoidanceType 4), `PedestrianAgent` (`arriveTolerance 0.6`), `MoveToTarget` (`autoSetAgentSpeed 0`, `externallyPaused 1`, `arriveThreshold 0.2`, `resampleCooldown 5`, `maxResampleAttempts 10`, `targetUpdateInterval 10`, `agent` wired), `UncertainBehavior` (**`m_Enabled: 0`**, `moveController` = none, `agent` wired), `Animator` (`applyRootMotion 0`), 15 `SkinnedMeshRenderer`s, limb capsule colliders.
- `Assets/Prefabs/Npc Variant.prefab` — exists, **not referenced by the scene spawner**.
- `Assets/VRExp/Agent Profile.asset` — `speed 1`, `speedOptions {0.6@0.25, 1.4@0.5, 2.2@0.25}`, `acceleration 8`, `angularSpeed 120`, `avoidancePriority 50`, `forceUncertain 0`, `uncertainProbability 0.2`, `agentRadius 0.3`, `agentHeight 1.8`, weighted appearance option lists.
- `.meta` files for the above scripts (GUID confirmation).

Not inspected this pass (flagged **[U]** where relevant): the baked NavMesh asset / NavMesh bake settings, `ProjectSettings` domain-reload setting, `GazeRecorder.cs` full body, the exact scene transforms of entrances/exits/attractors/`startTransform`, and the `AttractorDwell.facingTarget` assignments per plane.
