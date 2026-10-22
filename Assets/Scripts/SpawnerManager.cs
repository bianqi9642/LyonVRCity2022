// SpawnerManager.cs
//
// Spawns, pools and recycles the pedestrian NPC population and decides HOW each NPC will move.
//
// ---------------------------------------------------------------------------------------------------------------
// WHAT THIS CLASS DOES (in execution order)
//
//   Start()                          resolve ExperimentManager, subscribe to its events, start the pool coroutine
//   InitializePoolThenSpawn()        wait for the NavMesh, prewarm 'poolPrewarm' inactive NPCs, then start SpawnLoop()
//   SpawnLoop()                      every 'spawnInterval': if ShouldSpawnNow() -> SpawnAgent()
//   SpawnAgent()                     the orchestrator; one NPC per call, in four steps:
//       1. DECIDE   ChooseEntrance / ResolveSpawnPosition / ActiveMovementMode / BuildItinerary
//       2. ACQUIRE  TakeFromPool or InstantiateNewAgent (placed on the NavMesh), then ApplyAgentProfile / BindMover
//       3. INIT     InitializeAgentMovement -> PedestrianAgent.Initialize(...)
//       4. REGISTER RegisterSpawnedAgent (population accounting + data-capture event)
//   OnAgentFinished()                an NPC completed its itinerary (or was force-finished): return it to the pool
//
// MOVEMENT MODES  (NpcMovementMode, chosen per spawn from ExperimentManager.CurrentPartConfig)
//
//   Part3AttractorItinerary   Entrance -> 1..4 attractors -> Exit        BuildAttractorItinerary()
//   Part2WanderingItinerary   Entrance -> 1..4 wandering points -> Exit  BuildWanderingItinerary()
//
//   Both are finite, pre-generated ONCE per NPC life and handed to PedestrianAgent, which walks them and finishes
//   at the exit (-> OnAgentFinished -> pool). How each kind of waypoint is completed (arrival event + dwell for
//   Part 3 attractors; 1.5 m look-ahead without dwell for Part 2 wandering points) is PedestrianAgent's job.
//
// SPAWN GATES  (all ANDed in ShouldSpawnNow(), evaluated every tick)
//   - End-Panel hard stop not active, NPCs enabled for the active Part (ExperimentManager.CurrentPartConfig),
//     and (if gateSpawningByTrialState) a trial or the NPC warm-up window is open, and liveAgents < maxAgents.
//   - ExperimentManager is the single source of truth for per-Part NPC settings; this class only READS them
//     (NpcsEnabledForActivePart / ActiveMovementMode) rather than caching copies.
//
// INTERACTIONS
//   ExperimentManager  -> BeginNpcWarmup / EndNpcWarmup / ResetForNewPart (public), OnTrialStart/End, OnEndPanelShown
//   AgentFactory       <- ApplyProfileToInstance (speed, appearance, NavMeshAgent shape, UncertainBehavior roll)
//   PedestrianAgent    <- Initialize; -> OnAgentFinished, TryEnterAttractor / ReleaseAttractor / FindAttractorInfo
//   MoveToTarget       <- PauseExternal / ResumeExternal (trial pause, End-Panel hard stop)
//   DataCaptureManager <- AgentSpawned / AgentDespawned / ActiveAgents (observer only)
//
// HISTORY OF NOTE (kept short; see the methods for details)
//   - Trial-state gating and the NPC warm-up window are two independent flags (trialActive, npcWarmupActive)
//     because they carry different experimental meaning; ExperimentManager sequences them so they never overlap.
//   - The End-Panel hard stop is re-asserted every frame (Update) because PedestrianAgent's dwell coroutines can
//     otherwise un-pause an agent between frames. It is cleared only by ResetForNewPart().
//   - All Debug logging is gated by 'enableDebugLogs' (inline `if`, so no strings are built when disabled).
//
// All comments in English.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[System.Serializable]
public class AttractorInfo
{
    public Transform transform;
    [Tooltip("Max number of agents that can be 'inside' the attraction (for close proximity). If <=0, unlimited.")]
    public int capacity = 0;
    // runtime tracking
    [HideInInspector] public List<PedestrianAgent> occupants = new List<PedestrianAgent>();
}

public class SpawnerManager : MonoBehaviour
{
    // =================================================================================================================
    #region 1. Serialized configuration (field names/types are scene-serialized: do not rename)
    // =================================================================================================================

    // Inspector order = declaration order. Sections, top to bottom:
    //   References | Part 3 Itinerary Stops | Part 2 Itinerary Stops | Spawn Configuration & Population Limit |
    //   Spawn Interval & Position Sampling | Object Pooling & Prewarm | Agent Profile & NPC Configuration |
    //   Experiment / Trial Spawn Gates | Debug & Diagnostics
    // The movement mode (Part 2 vs Part 3 itinerary) is NOT a field here: it is pulled per Part from
    // ExperimentManager.CurrentPartConfig.npcDirectedMovement (see ActiveMovementMode()). Attractor capacity is a
    // per-element field of 'attractors' (AttractorInfo.capacity). Part 1 spawns no NPCs by default, so no setting here
    // is Part 1-specific. Only the order/headers/tooltips are presentation; names, types and defaults are unchanged.

    [Header("References")]
    [Tooltip("Spawn start point. If assigned, agents spawn at/near this transform (NavMesh-sampled). The itinerary's " +
             "first entry (itinerary[0]) is chosen randomly from 'Entrances' regardless of this transform.")]
    public Transform startTransform;

    [Tooltip("Candidate entrances. One is drawn per spawn and becomes itinerary[0] (Part 3: the first waypoint; " +
             "Part 2: the instant placement point).")]
    public Transform[] entrances;
    [Tooltip("Candidate exits. One is chosen per itinerary as its last entry; reaching it finishes the NPC and " +
             "returns it to the pool.")]
    public Transform[] exits;

    [Header("Part 3 Itinerary Stops (Attractors)")]
    [Tooltip("Part 3 only (Entrance -> attractors -> Exit). Intermediate stops with dwell. Each element has its " +
             "own Capacity (max simultaneous occupants; <= 0 = unlimited). Not used by Part 1 or Part 2.")]
    public AttractorInfo[] attractors;
    [Tooltip("Part 3 only: minimum number of distinct attractors per itinerary (inclusive).")]
    public int minAttractorsPerAgent = 1;
    [Tooltip("Part 3 only: maximum number of distinct attractors per itinerary (inclusive).")]
    public int maxAttractorsPerAgent = 4;
    [Tooltip("Part 3 only: attractor dwell time in seconds, as a range (x = min, y = max). One value is drawn per NPC " +
             "when it spawns and is used for EVERY attractor visit of that NPC. NPCs never dwell at the entrance or " +
             "the exit, and Part 2 does not use this setting.")]
    public Vector2 dwellTimeSec = new Vector2(5f, 15f); // min/max dwell

    [Header("Part 2 Itinerary Stops (Wandering Points)")]
    [Tooltip("Predefined, experimenter-placed Transforms used ONLY as the intermediate stops of Part 2 " +
             "(Part2WanderingItinerary) routes: Entrance -> wandering points -> Exit. Anonymous movement waypoints - " +
             "NOT attractors, NOT exits, never referenced by BuildAttractorItinerary() and never given dwell or " +
             "capacity logic. Place them on/near the walkable NavMesh, spread across the area NPCs should cover. " +
             "When a route is generated, every leg is validated with NavMesh.CalculatePath; a point that cannot be " +
             "reached from the previous stop is skipped (never silently replaced by a generated position).")]
    public Transform[] wanderingPoints;
    [Tooltip("Part 2 only: minimum number of distinct wandering points per itinerary (inclusive). Defaults mirror " +
             "Part 3's attractor counts.")]
    public int minWanderingPointsPerAgent = 1;
    [Tooltip("Part 2 only: maximum number of distinct wandering points per itinerary (inclusive), capped by the " +
             "number of valid points available.")]
    public int maxWanderingPointsPerAgent = 4;
    [Tooltip("Part 2 only. Distance (metres) at which an NPC moves on to the NEXT entry of its itinerary while " +
             "travelling between wandering points, before MoveToTarget's own (much tighter) arriveThreshold would " +
             "bring it to a full stop. It is a transition rule for intermediate stops only - the entrance placement and " +
             "the exit still use the normal arrival event. Must be larger than MoveToTarget.arriveThreshold (0.2 by " +
             "default) by a comfortable margin. Too small: stop-start at every point; too large: early direction changes.")]
    public float wanderLookAheadDistance = 1.5f;

    [Header("Spawn Configuration & Population Limit")]
    [Tooltip("The NPC prefab that is pooled/instantiated. Must carry PedestrianAgent, MoveToTarget and a NavMeshAgent.")]
    public GameObject agentPrefab;
    [Tooltip("Maximum number of simultaneously alive NPCs. Spawning pauses while this many are alive.")]
    public int maxAgents = 200;

    [Header("Spawn Interval & Position Sampling")]
    [Tooltip("Seconds between spawn attempts (at most one NPC per attempt).")]
    public float spawnInterval = 0.5f; // seconds between spawns
    [Tooltip("Radius (metres) on the horizontal plane around the spawn origin used to sample a NavMesh position.")]
    public float spawnSampleRadius = 1.0f; // sample radius around entrance on the horizontal plane
    [Tooltip("Number of random NavMesh sampling attempts before falling back to the spawn origin.")]
    public int spawnSampleMaxAttempts = 8;

    // Ground projection behaviour:
    [Tooltip("Layer(s) considered ground for projection raycasts. Default = Everything.")]
    public LayerMask groundLayers = ~0;
    [Tooltip("Maximum distance used when raycasting down to find ground from a sampling height.")]
    public float raycastDownDistance = 20f;
    [Tooltip("Small upward offset applied after a ground hit to avoid clipping the ground.")]
    public float groundOffset = 0.1f;
    [Tooltip("Vertical offsets (relative to entrance Y) to try when NavMesh.SamplePosition fails to find a NavMesh from the projected ground point.")]
    public float[] verticalOffsets = new float[] { 2.0f, 1.0f, 0.5f, 0.0f, -1.0f };

    [Header("Object Pooling & Prewarm")]
    [Tooltip("Reuse NPC instances instead of destroying them when they finish.")]
    public bool usePooling = true;
    [Tooltip("Number of inactive NPC instances created up front, before spawning starts.")]
    public int poolPrewarm = 50;
    [Tooltip("Seconds to wait for the NavMesh to become available before pool prewarm / first spawns proceed anyway.")]
    // time to wait for NavMesh availability before giving up (seconds)
    public float navmeshWaitTimeout = 5f;

    [Header("Agent Profile & NPC Configuration")]
    [Tooltip("Optional factory that applies the profile (appearance, speed, NavMeshAgent shape, UncertainBehavior roll) to each NPC.")]
    public AgentFactory agentFactory;           // optional factory to create/apply profile
    [Tooltip("Default AgentProfile applied at spawn. ExperimentManager.currentProfile overwrites this at the start of each NPC-enabled Part.")]
    public AgentProfile defaultProfile;         // optional default profile applied at spawn

    [Header("Experiment / Trial Spawn Gates")]
    [Tooltip("Optional: assign the scene's ExperimentManager. If left empty, it will be auto-found via " +
             "FindObjectOfType at Start() (same pattern used elsewhere in this project, e.g. TargetSelectable).")]
    public ExperimentManager experimentManager;

    [Tooltip("If true, SpawnLoop() only spawns new agents while a trial is active (between " +
             "ExperimentManager.OnTrialStart and OnTrialEnd) OR while an NPC warm-up window is open (see " +
             "BeginNpcWarmup/EndNpcWarmup). If false, spawning is continuous for the lifetime of the scene, " +
             "matching the original (ungated) behaviour.")]
    public bool gateSpawningByTrialState = true;

    [Tooltip("If true, agents that are already alive when a trial ends are paused (frozen in place, " +
             "animator frozen) via MoveToTarget.PauseExternal(...), and resumed via ResumeExternal() " +
             "when the next trial starts. If false, already-spawned agents keep moving/finishing their " +
             "current itinerary even outside the trial window.")]
    public bool pauseLiveAgentsOutsideTrial = true;

    [Tooltip("If true, in addition to pausing, agents that are still alive when a trial ends are " +
             "force-finished (PedestrianAgent.ForceFinish()) and returned to the pool / destroyed, so " +
             "the scene is fully clear of pedestrians outside the trial window. If false, paused agents " +
             "simply wait, frozen, until the next trial resumes them.")]
    public bool despawnLiveAgentsOnTrialEnd = false;

    [Header("Debug & Diagnostics")]
    [Tooltip("Mirrors MoveToTarget.enableDebugLogs. When true (default), SpawnerManager logs " +
             "per-spawn/per-despawn itinerary details and warnings/errors via Debug.Log(...). Disable this " +
             "for Quest/device builds to avoid the string-building + logging cost scaling with " +
             "maxAgents/spawnInterval - it is purely a diagnostic aid and has no effect on spawn behavior.")]
    public bool enableDebugLogs = true;

    #endregion

    // =================================================================================================================
    #region 2. Runtime state and public observers
    // =================================================================================================================

    // Inactive, ready-to-reuse NPC instances.
    private List<GameObject> pool = new List<GameObject>();

    // Population accounting: 'liveAgents' counts spawned-and-not-yet-finished NPCs (the maxAgents cap);
    // 'activeAgents' is the same set as GameObjects, so trial-state pause/resume/despawn can act on them as a group.
    private int liveAgents = 0;
    private List<GameObject> activeAgents = new List<GameObject>();

    // Spawn-gate flags (see section 4). All are written only by this class's event handlers / public methods.
    private bool trialActive = false;
    private bool npcWarmupActive = false;
    private bool endPanelActive = false;

    // The spawn loop must not run before the NavMesh wait + prewarm finished.
    private bool poolInitializationComplete = false;

    // Reused buffer for BuildWanderingItinerary()'s leg reachability checks (created lazily on first use).
    private NavMeshPath routeCheckPath;
    private float nextRouteWarnTime = 0f;

    /// <summary>Read-only view of the currently-alive (spawned, not finished) agent GameObjects.</summary>
    public IReadOnlyList<GameObject> ActiveAgents => activeAgents;

    /// <summary>Raised right after an agent has been added to the active list (spawn / pooled reuse).</summary>
    public event System.Action<PedestrianAgent> AgentSpawned;

    /// <summary>Raised right after an agent has been removed from the active list (finished / force-finished).</summary>
    public event System.Action<PedestrianAgent> AgentDespawned;

    /// <summary>Read-only: whether an NPC warm-up window is currently open.</summary>
    public bool NpcWarmupActive => npcWarmupActive;

    /// <summary>Read-only: whether the End-Panel hard stop is currently in effect.</summary>
    public bool EndPanelActive => endPanelActive;

    // Data-capture OBSERVER hooks: they never change spawning, pooling, itinerary, movement, timing or
    // randomisation. They only notify AFTER the internal list has already been mutated.
    private void RaiseAgentSpawned(PedestrianAgent pa)
    {
        try { AgentSpawned?.Invoke(pa); }
        catch (System.Exception e) { if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] AgentSpawned observer threw: " + e.Message); }
    }

    private void RaiseAgentDespawned(PedestrianAgent pa)
    {
        try { AgentDespawned?.Invoke(pa); }
        catch (System.Exception e) { if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] AgentDespawned observer threw: " + e.Message); }
    }

    #endregion

    // =================================================================================================================
    #region 3. Unity lifecycle and event subscriptions
    // =================================================================================================================

    void Start()
    {
        // ExperimentManager is needed regardless of gateSpawningByTrialState: it also supplies the per-Part
        // NPC-enable / movement-mode settings and the End-Panel hard-stop event.
        if (experimentManager == null)
        {
            experimentManager = FindObjectOfType<ExperimentManager>();
        }

        // Trial-state gating subscription.
        if (gateSpawningByTrialState)
        {
            if (experimentManager != null)
            {
                experimentManager.OnTrialStart += HandleTrialStart;
                experimentManager.OnTrialEnd += HandleTrialEnd;
            }
            else
            {
                // No ExperimentManager found: fail safe by disabling gating rather than blocking all
                // spawning forever (which would silently break scenes that don't use ExperimentManager).
                if (enableDebugLogs) Debug.LogWarning("[SpawnerManager] gateSpawningByTrialState is true but no ExperimentManager " +
                    "was found in the scene. Falling back to continuous spawning (gating disabled).");
                gateSpawningByTrialState = false;
            }
        }

        // End-Panel hard stop: subscribed unconditionally - it must apply even with gateSpawningByTrialState == false
        // (continuous spawning), where trialActive alone would never close the spawning gate at that boundary.
        if (experimentManager != null)
        {
            experimentManager.OnEndPanelShown += HandleEndPanelShown;
        }

        // Start sequence that waits/attempts NavMesh prewarm and only then starts spawn loop.
        StartCoroutine(InitializePoolThenSpawn());
    }

    void OnDestroy()
    {
        // Unsubscribe to avoid referencing a destroyed SpawnerManager from ExperimentManager's events.
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= HandleTrialStart;
            experimentManager.OnTrialEnd -= HandleTrialEnd;
            experimentManager.OnEndPanelShown -= HandleEndPanelShown;
        }
    }

    void Update()
    {
        // End-Panel hard stop: continuously re-assert the freeze on every active agent, every frame, for as long as
        // endPanelActive is true. A one-shot freeze is not airtight: PedestrianAgent's dwell coroutines run
        // independently of MoveToTarget's paused state, and MoveToTarget.MoveTo(...) unconditionally un-stops the
        // NavMeshAgent, so an agent whose timer elapses while the End Panel is visible would otherwise resume moving
        // until the next OnTrialStart. Re-applying every frame (cheap early-out when not needed) bounds any such
        // escape to a single frame. See HardStopAgent().
        if (endPanelActive) HardStopAllActiveAgents();
    }

    #endregion

    // =================================================================================================================
    #region 4. Experiment / Part configuration and spawn gates
    // =================================================================================================================

    // Whether the NPC system may operate for the Part currently running. PULLED from
    // ExperimentManager.CurrentPartConfig.npcEnabled every tick (single source of truth, no cached copy). Falls back
    // to "enabled" when no ExperimentManager / config is available.
    private bool NpcsEnabledForActivePart()
    {
        if (experimentManager == null) return true;
        var cfg = experimentManager.CurrentPartConfig;
        return cfg == null || cfg.npcEnabled;
    }

    // Which itinerary type new spawns get. PULLED from ExperimentManager.CurrentPartConfig.npcDirectedMovement
    // (the serialized bool: true = Part 3 attractor itinerary, false = Part 2 wandering itinerary). Falls back to
    // Part 3 when unavailable, which reproduces the original default behaviour.
    private NpcMovementMode ActiveMovementMode()
    {
        if (experimentManager == null) return NpcMovementMode.Part3AttractorItinerary;
        var cfg = experimentManager.CurrentPartConfig;
        bool part3 = cfg == null || cfg.npcDirectedMovement;
        return part3 ? NpcMovementMode.Part3AttractorItinerary : NpcMovementMode.Part2WanderingItinerary;
    }

    // The single "may a new NPC be created right now?" decision. Separate from HOW an NPC is created (SpawnAgent).
    //   - End-Panel hard stop and per-Part npcEnabled apply regardless of gateSpawningByTrialState.
    //   - With gating on, spawning needs an open trial OR an open NPC warm-up window.
    //   - The population cap applies last.
    private bool ShouldSpawnNow()
    {
        bool gateOpen = !endPanelActive && NpcsEnabledForActivePart() &&
            ((!gateSpawningByTrialState) || trialActive || npcWarmupActive);

        return gateOpen && liveAgents < maxAgents;
    }

    /// <summary>
    /// Called when ExperimentManager fires OnTrialStart. Opens the spawning gate and, if configured,
    /// resumes any agents that were paused when the previous trial ended.
    /// </summary>
    private void HandleTrialStart()
    {
        trialActive = true;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] Trial started -> NPC spawning enabled.");

        if (pauseLiveAgentsOutsideTrial)
        {
            ResumeAllActiveAgents();
        }
    }

    /// <summary>
    /// Called when ExperimentManager fires OnTrialEnd. Closes the spawning gate so no new agents
    /// are created outside the trial window, and optionally pauses and/or clears agents that are still
    /// alive from the trial that just ended.
    /// </summary>
    private void HandleTrialEnd()
    {
        trialActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] Trial ended -> NPC spawning disabled.");

        if (despawnLiveAgentsOnTrialEnd)
        {
            ForceFinishAllActiveAgents();
        }
        else if (pauseLiveAgentsOutsideTrial)
        {
            PauseAllActiveAgents();
        }
    }

    /// <summary>
    /// NPC warm-up. Called by ExperimentManager at the start of a Part, BEFORE the participant-facing Start panel is
    /// shown and BEFORE OnTrialStart fires. Opens the spawning gate exactly like a trial does (ShouldSpawnNow() ORs
    /// this flag with trialActive) using the normal spawn machinery, so the NPC population can form and spread out
    /// before the trial. Does NOT fire OnTrialStart, does NOT touch trialActive, and does NOT pause/resume/despawn
    /// any agent - it only flips the spawning gate.
    /// </summary>
    public void BeginNpcWarmup()
    {
        npcWarmupActive = true;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] NPC warm-up started -> NPC spawning enabled (pre-trial).");
    }

    /// <summary>
    /// Called by ExperimentManager once the warm-up duration has elapsed, immediately before OnTrialStart is
    /// invoked. Closes the warm-up gate. Deliberately does NOT pause, resume, or despawn any live agent. Because
    /// ExperimentManager calls this and then invokes OnTrialStart in the same breath (no yield in between), the
    /// combined gate never goes false in between, so there is no gap in spawning across the boundary.
    /// </summary>
    public void EndNpcWarmup()
    {
        npcWarmupActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] NPC warm-up ended.");
    }

    /// <summary>
    /// Called by ExperimentManager (RunNpcWarmup(), unconditionally, at the very start of every Part's NPC
    /// lifecycle) to clear any End-Panel hard stop left over from whichever Part ran previously. Must happen before
    /// the new Part's own warm-up/trial spawning decisions are evaluated - resetting only on OnTrialStart would be
    /// too late, since warm-up spawning happens BEFORE OnTrialStart.
    /// </summary>
    public void ResetForNewPart()
    {
        endPanelActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] New Part starting -> End-Panel hard stop cleared.");
    }

    #endregion

    // =================================================================================================================
    #region 5. Live-agent control: trial pause, End-Panel hard stop, despawn
    // =================================================================================================================

    /// <summary>
    /// Called when ExperimentManager raises OnEndPanelShown for the Part currently running - immediately before that
    /// Part's End Panel is shown (strictly before part_end). Closes the spawning gate (endPanelActive, checked in
    /// ShouldSpawnNow) and immediately freezes every active agent. Unconditional: applies regardless of
    /// gateSpawningByTrialState, pauseLiveAgentsOutsideTrial, or despawnLiveAgentsOnTrialEnd.
    /// </summary>
    private void HandleEndPanelShown()
    {
        endPanelActive = true;
        HardStopAllActiveAgents();
        if (enableDebugLogs) Debug.Log("[SpawnerManager] End Panel shown -> NPC spawning and movement hard-stopped.");
    }

    /// <summary>
    /// Freezes one active agent using ONLY existing public APIs - never touches NavMeshAgent directly:
    ///   - MoveToTarget.PauseExternal(true, true): halts NavMeshAgent movement and freezes the animator - the same
    ///     mechanism used by PauseAllActiveAgents()/HandleTrialEnd().
    ///   - Disabling UncertainBehavior (if present): stops ITS OWN per-second-probability state machine
    ///     (Pause/Hesitate/Slow/Detour), which would otherwise independently call MoveToTarget.ResumeExternal()/
    ///     SetTemporarySpeed() on its own timer regardless of the pause above. A pooled agent's UncertainBehavior is
    ///     re-enabled/re-rolled by AgentFactory.ApplyProfileToInstance() on its next spawn.
    /// Applies identically to both movement modes. Idempotent and cheap - safe to call every frame (see Update()).
    /// </summary>
    private void HardStopAgent(GameObject go)
    {
        if (go == null) return;

        MoveToTarget mt = go.GetComponent<MoveToTarget>();
        if (mt != null) mt.PauseExternal(true, true);

        UncertainBehavior ub = go.GetComponent<UncertainBehavior>();
        if (ub != null) ub.enabled = false;
    }

    private void HardStopAllActiveAgents()
    {
        for (int i = 0; i < activeAgents.Count; i++)
        {
            HardStopAgent(activeAgents[i]);
        }
    }

    /// <summary>
    /// Freezes movement (and animator) of every currently-alive agent via MoveToTarget.PauseExternal,
    /// without changing their itinerary state, so they can be resumed exactly where they left off.
    /// </summary>
    private void PauseAllActiveAgents()
    {
        for (int i = 0; i < activeAgents.Count; i++)
        {
            GameObject go = activeAgents[i];
            if (go == null) continue;
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.PauseExternal(true, true); // haltMovement=true, freezeAnimator=true
            }
        }
    }

    /// <summary>Resumes movement of every currently-alive agent previously paused by PauseAllActiveAgents.</summary>
    private void ResumeAllActiveAgents()
    {
        for (int i = 0; i < activeAgents.Count; i++)
        {
            GameObject go = activeAgents[i];
            if (go == null) continue;
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.ResumeExternal();
            }
        }
    }

    /// <summary>
    /// Immediately terminates every currently-alive agent's itinerary (PedestrianAgent.ForceFinish()), which
    /// triggers the normal onAgentFinished -> OnAgentFinished(...) pooling/destroy pathway. Iterates over a copy of
    /// the list because OnAgentFinished mutates activeAgents while we iterate. Works identically in both modes.
    /// </summary>
    private void ForceFinishAllActiveAgents()
    {
        List<GameObject> snapshot = new List<GameObject>(activeAgents);
        for (int i = 0; i < snapshot.Count; i++)
        {
            GameObject go = snapshot[i];
            if (go == null) continue;
            PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
            if (pa != null)
            {
                pa.ForceFinish();
            }
        }
    }

    #endregion

    // =================================================================================================================
    #region 6. Pool initialisation (NavMesh wait + prewarm)
    // =================================================================================================================

    IEnumerator InitializePoolThenSpawn()
    {
        // Wait for NavMesh readiness (bounded by navmeshWaitTimeout) and prewarm the pool if configured.
        if (usePooling)
        {
            yield return StartCoroutine(WaitForNavMeshAvailability(
                "Prewarm will still run but any prewarmed agents that cannot find NavMesh will have their NavMeshAgent disabled as a fallback."));
            yield return StartCoroutine(PrewarmPoolCoroutine());
        }
        else
        {
            // Non-pooling: we still briefly wait for NavMesh availability so our first spawns have a chance of
            // sampling NavMesh. This is non-blocking if NavMesh is ready.
            yield return StartCoroutine(WaitForNavMeshAvailability(
                "Spawning will proceed but agents may be fallback to transform mode."));
        }

        poolInitializationComplete = true;
        // start main spawn loop after prewarm completes
        StartCoroutine(SpawnLoop());
    }

    // Sampling origin for pool initialisation: prefer the user-provided startTransform, then the first entrance,
    // otherwise world origin.
    private Vector3 PoolSampleOrigin()
    {
        if (startTransform != null)
            return startTransform.position;
        if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            return entrances[0].position;
        return Vector3.zero;
    }

    // Polls (once per frame, up to navmeshWaitTimeout) until the NavMesh can be sampled near the sample origin or
    // any entrance. 'failureNote' only customises the warning text when the timeout elapses.
    IEnumerator WaitForNavMeshAvailability(string failureNote)
    {
        float start = Time.time;
        bool navAvailable = false;
        Vector3 sampleOrigin = PoolSampleOrigin();

        while (Time.time - start < navmeshWaitTimeout)
        {
            Vector3 s = SampleNavMeshNear(sampleOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
            if (s != Vector3.zero)
            {
                navAvailable = true;
                break;
            }
            // try each entrance quickly if available
            if (entrances != null)
            {
                foreach (var e in entrances)
                {
                    if (e == null) continue;
                    Vector3 ss = SampleNavMeshNear(e.position, spawnSampleRadius, 2);
                    if (ss != Vector3.zero)
                    {
                        navAvailable = true;
                        break;
                    }
                }
            }
            if (navAvailable) break;

            // wait a frame before retrying
            yield return null;
        }

        if (!navAvailable && enableDebugLogs)
            Debug.LogWarning($"SpawnerManager: NavMesh not found near sample origin within {navmeshWaitTimeout}s. {failureNote}");
    }

    // Instantiates 'poolPrewarm' inactive NPCs, each placed on the NavMesh near the sample origin (or parked out of
    // sight with its NavMeshAgent disabled if no NavMesh was found) and configured with the default profile.
    IEnumerator PrewarmPoolCoroutine()
    {
        Vector3 sampleOrigin = PoolSampleOrigin();
        float sampleRadius = Mathf.Max(0.1f, spawnSampleRadius);

        for (int i = 0; i < poolPrewarm; i++)
        {
            // instantiate the prefab (Awake will run here)
            GameObject go = Instantiate(agentPrefab, sampleOrigin + Random.insideUnitSphere * 0.5f, Quaternion.identity);

            // attempt to place on NavMesh near sampleOrigin using improved sampling
            Vector3 navPos = SampleNavMeshNear(sampleOrigin, sampleRadius, spawnSampleMaxAttempts);
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();

            bool placedOnNav = false;
            if (navPos != Vector3.zero)
            {
                // place instance on navmesh
                go.transform.position = navPos;
                if (nav != null)
                {
                    // ensure agent enabled and warp to navpos
                    if (!nav.enabled) nav.enabled = true;
                    bool warped = nav.Warp(navPos);
                    // ResetPath only when agent is active & on NavMesh
                    if (warped && nav.isOnNavMesh)
                    {
                        nav.ResetPath();
                        placedOnNav = true;
                    }
                }
            }
            else
            {
                // Could not find NavMesh: disable the NavMeshAgent component to avoid exceptions in Awake/OnDisable/OnDestroy
                if (nav != null)
                {
                    nav.enabled = false;
                }
                // move slightly out of sight instead of extremely far
                go.transform.position = sampleOrigin + Vector3.down * 5f;
            }

            // Apply profile to prewarmed instance if factory/profile available
            if (agentFactory != null && defaultProfile != null)
            {
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }

            // MoveToTarget is NavMeshAgent-only: just make sure it knows the runtime agent reference.
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.agent = (nav != null) ? nav : mt.agent;
            }

            if (enableDebugLogs)
            {
                PedestrianAgent pa = go.GetComponent<PedestrianAgent>(); // Awake already executed
                string agentIdStr = pa != null ? pa.AgentID : "n/a";
                string navState = (nav != null) ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
                Debug.Log($"[SpawnerManager][Prewarm] idx={i} placedOnNav={placedOnNav} {navState} pos={go.transform.position} prefab={agentPrefab.name} AgentID={agentIdStr}");
            }

            // Deactivate and add to pool
            go.SetActive(false);
            pool.Add(go);

            // yield occasionally to avoid editor freeze
            if (i % 8 == 0) yield return null;
        }
    }

    #endregion

    // =================================================================================================================
    #region 7. Spawn loop and the SpawnAgent orchestrator
    // =================================================================================================================

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            // Only allow spawning once initialization (prewarm / wait) is complete.
            if (!poolInitializationComplete)
            {
                yield return null;
                continue;
            }

            // The loop keeps running (re-checking every 'spawnInterval') rather than stopping when the gate is
            // closed, so spawning resumes automatically as soon as the next warm-up or OnTrialStart opens it.
            if (ShouldSpawnNow())
            {
                SpawnAgent();
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    // Creates ONE NPC. DECIDE (where / which route) -> ACQUIRE (an instance on the NavMesh) -> INIT (hand the route
    // to PedestrianAgent) -> REGISTER (population accounting). The route is built before an instance is taken from
    // the pool, so a tick that cannot produce a valid route leaves the pool untouched and simply retries next tick.
    void SpawnAgent()
    {
        // If neither a startTransform nor entrances are available, we cannot spawn.
        if (!HasSpawnPrerequisites())
            return;

        // 1. DECIDE -------------------------------------------------------------------------------------------
        int entranceIndex;
        Transform chosenEntrance = ChooseEntrance(out entranceIndex);
        Vector3 spawnPos = ResolveSpawnPosition(chosenEntrance);
        NpcMovementMode mode = ActiveMovementMode();

        // Part 3 only: attractor dwell time, drawn once per NPC (before the route) and reused for each of its
        // attractor visits. Entrances and exits never dwell.
        float dwell = (mode == NpcMovementMode.Part3AttractorItinerary)
            ? Random.Range(dwellTimeSec.x, dwellTimeSec.y)
            : 0f;

        List<Transform> itinerary = BuildItinerary(mode, chosenEntrance);
        if (itinerary == null)
            return; // no valid route this tick (already reported)

        // 2. ACQUIRE ------------------------------------------------------------------------------------------
        bool reused = usePooling && pool.Count > 0;
        GameObject go = reused ? TakeFromPool(spawnPos) : InstantiateNewAgent(spawnPos);
        if (go == null)
            return;

        PedestrianAgent agentScript = go.GetComponent<PedestrianAgent>();

        ApplyAgentProfile(go);
        BindMoverToAgent(go);

        if (reused)
        {
            // Pooled instances are inactive until now. A fresh Instantiate()d instance is already active.
            go.transform.rotation = Quaternion.identity;
            go.SetActive(true);
        }

        // 3. INIT ---------------------------------------------------------------------------------------------
        InitializeAgentMovement(go, agentScript, mode, itinerary, chosenEntrance, dwell);

        // 4. REGISTER -----------------------------------------------------------------------------------------
        RegisterSpawnedAgent(go, agentScript);

        if (enableDebugLogs)
            LogSpawn(go, agentScript, mode, reused, entranceIndex, chosenEntrance, itinerary, dwell);
    }

    private bool HasSpawnPrerequisites()
    {
        // (no startTransform AND no entrances) OR no prefab -> cannot spawn
        return !((startTransform == null && (entrances == null || entrances.Length == 0)) || agentPrefab == null);
    }

    #endregion

    // =================================================================================================================
    #region 8. Spawn position selection
    // =================================================================================================================

    // Chooses a random entrance. The SAME draw serves both modes: it becomes itinerary[0] of the route (in Part 3 the
    // first waypoint, in Part 2 the placement point). Returns null (index -1) if no entrances are configured.
    private Transform ChooseEntrance(out int entranceIndex)
    {
        entranceIndex = -1;
        if (entrances == null || entrances.Length == 0)
            return null;

        entranceIndex = Random.Range(0, entrances.Length);
        return entrances[entranceIndex];
    }

    // The physical spawn position: NavMesh-sampled around startTransform (priority) or, failing that, around the
    // chosen entrance (legacy behaviour). Identical for both movement modes.
    private Vector3 ResolveSpawnPosition(Transform chosenEntrance)
    {
        Vector3 samplingOrigin = Vector3.zero;
        if (startTransform != null)
            samplingOrigin = startTransform.position;
        else if (chosenEntrance != null)
            samplingOrigin = chosenEntrance.position;

        // sample NavMesh near sampling origin using improved sampling
        Vector3 spawnPos = SampleNavMeshNear(samplingOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
        if (spawnPos == Vector3.zero)
        {
            // defensive fallback: place at samplingOrigin rather than return and leave a stray prefab instanced later
            spawnPos = samplingOrigin;
            if (enableDebugLogs) Debug.LogWarning("SpawnerManager: failed to sample NavMesh near spawn origin. Using sampling origin position as fallback for spawn position.");
        }
        return spawnPos;
    }

    /// <summary>
    /// Improved NavMesh sampling routine: random horizontal offsets around 'origin', projected onto the ground, then
    /// sampled onto the NavMesh at several vertical offsets. Returns Vector3.zero if nothing was found.
    /// </summary>
    Vector3 SampleNavMeshNear(Vector3 origin, float radius, int attempts)
    {
        NavMeshHit navHit;

        // Defensive clamps
        attempts = Mathf.Max(1, attempts);
        radius = Mathf.Max(0.01f, radius);

        for (int i = 0; i < attempts; i++)
        {
            // Sample a random point on the horizontal plane around the origin.
            Vector2 r2 = Random.insideUnitCircle * radius;
            Vector3 candidate = new Vector3(origin.x + r2.x, origin.y, origin.z + r2.y);

            // 1) Try to project the candidate down onto geometry (if colliders exist).
            Vector3 projected;
            bool projectedToGround = TryProjectToGround(candidate, out projected);

            if (projectedToGround)
            {
                // Slightly lift the point to avoid embedding into the mesh
                Vector3 probe = projected + Vector3.up * groundOffset;

                // Attempt NavMesh sampling at projected ground location first.
                if (NavMesh.SamplePosition(probe, out navHit, radius * 0.5f + 0.1f, NavMesh.AllAreas))
                {
                    return navHit.position;
                }

                // If direct sample fails, try small vertical offsets around the projected point (use verticalOffsets)
                foreach (float vo in verticalOffsets)
                {
                    Vector3 offsetProbe = new Vector3(probe.x, projected.y + vo, probe.z);
                    if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    {
                        return navHit.position;
                    }
                }
            }
            else
            {
                // 2) If ground projection failed (no colliders), fallback to sampling NavMesh at multiple vertical offsets
                // relative to the origin.y (so the sampling covers possible NavMesh heights).
                foreach (float vo in verticalOffsets)
                {
                    Vector3 offsetProbe = new Vector3(candidate.x, origin.y + vo, candidate.z);

                    // We sample with a small vertical tolerance; this helps if the NavMesh is slightly above/below the candidate
                    if (NavMesh.SamplePosition(offsetProbe, out navHit, radius * 0.5f + Mathf.Abs(vo) + 0.1f, NavMesh.AllAreas))
                    {
                        return navHit.position;
                    }
                }
            }
        }

        // As a last resort, try origin directly (project it to ground if possible)
        Vector3 originProjected;
        if (TryProjectToGround(origin, out originProjected))
        {
            if (NavMesh.SamplePosition(originProjected + Vector3.up * groundOffset, out navHit, Mathf.Max(0.2f, radius * 0.5f), NavMesh.AllAreas))
                return navHit.position;

            foreach (float vo in verticalOffsets)
            {
                Vector3 offsetProbe = new Vector3(originProjected.x, originProjected.y + vo, originProjected.z);
                if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    return navHit.position;
            }
        }
        else
        {
            // Try origin with vertical offsets even if no projection
            foreach (float vo in verticalOffsets)
            {
                Vector3 offsetProbe = new Vector3(origin.x, origin.y + vo, origin.z);
                if (NavMesh.SamplePosition(offsetProbe, out navHit, Mathf.Max(0.2f, radius * 0.5f + Mathf.Abs(vo)), NavMesh.AllAreas))
                    return navHit.position;
            }
        }

        // nothing found
        return Vector3.zero;
    }

    bool TryProjectToGround(Vector3 samplePoint, out Vector3 groundPoint)
    {
        groundPoint = Vector3.zero;

        // Compose a reasonable start height so the raycast can pass over small obstacles.
        Vector3 rayStart = samplePoint + Vector3.up * (raycastDownDistance * 0.5f + 1f);
        float rayDistance = raycastDownDistance;

        RaycastHit hit;
        if (Physics.Raycast(rayStart, Vector3.down, out hit, rayDistance, groundLayers, QueryTriggerInteraction.Ignore))
        {
            groundPoint = hit.point;
            return true;
        }

        return false;
    }

    #endregion

    // =================================================================================================================
    #region 9. Itinerary construction (one route per NPC life)
    // =================================================================================================================

    // Builds the route for the given mode, or returns null when no valid route could be produced this tick.
    // In both modes list[0] is the placement point (the entrance), the LAST entry is the exit, and the list is
    // handed to PedestrianAgent unchanged for the NPC's whole life.
    private List<Transform> BuildItinerary(NpcMovementMode mode, Transform entrance)
    {
        return mode == NpcMovementMode.Part2WanderingItinerary
            ? BuildWanderingItinerary(entrance)
            : BuildAttractorItinerary(entrance);
    }

    /// <summary>
    /// Part 3 route: Entrance -> 'num' distinct attractors (minAttractorsPerAgent..maxAttractorsPerAgent) -> Exit.
    /// The chosen 'entrance' is inserted as the first element so itinerary[0] is an entrance. Never reads
    /// wanderingPoints.
    /// </summary>
    List<Transform> BuildAttractorItinerary(Transform entrance)
    {
        List<Transform> list = new List<Transform>();

        int num = Random.Range(minAttractorsPerAgent, maxAttractorsPerAgent + 1);

        if (attractors != null && attractors.Length > 0)
        {
            List<int> indices = new List<int>();
            for (int i = 0; i < attractors.Length; i++) indices.Add(i);
            for (int k = 0; k < num && indices.Count > 0; k++)
            {
                int iSel = Random.Range(0, indices.Count);
                int idx = indices[iSel];
                indices.RemoveAt(iSel); // avoid visiting same attractor twice
                if (attractors[idx] != null && attractors[idx].transform != null)
                    list.Add(attractors[idx].transform);
            }
        }

        // finally pick an exit
        if (exits != null && exits.Length > 0)
        {
            Transform exit = exits[Random.Range(0, exits.Length)];
            list.Add(exit);
        }

        // Insert the chosen entrance as the first element so itinerary[0] is an entrance.
        if (entrance != null)
            list.Insert(0, entrance);

        return list;
    }

    /// <summary>
    /// Part 2 route: Entrance -> ordered, distinct wandering points -> Exit.
    ///
    /// Selection (all bounded, nothing is retried indefinitely):
    ///   1. Candidates = the non-null wanderingPoints. The target count is Random.Range(minWanderingPointsPerAgent,
    ///      maxWanderingPointsPerAgent + 1), capped by the number of candidates.
    ///   2. Points are drawn at random WITHOUT replacement, so no point repeats within one itinerary. Each draw is
    ///      accepted only if NavMesh.CalculatePath from the previous stop (starting at the entrance) is PathComplete;
    ///      an unreachable candidate is dropped and never tried again. Fewer valid points than wanted simply yields a
    ///      shorter route.
    ///   3. The exit is a random non-null exit reachable from the last stop; if none is, trailing stops are removed
    ///      one by one until some exit is reachable.
    ///   4. If no valid route exists at all (no usable points and/or no reachable exit) this returns null: the spawn
    ///      is skipped this tick (with a throttled warning) rather than creating an NPC whose next destination is
    ///      known to be unreachable. If 'exits' is empty the last wandering point is the route's end.
    ///
    /// Index 0 is the entrance (or startTransform if there are no entrances): the NPC is placed there instantly by
    /// PedestrianAgent, so no leg to it is validated.
    /// </summary>
    List<Transform> BuildWanderingItinerary(Transform entrance)
    {
        Transform origin = entrance != null ? entrance : startTransform;
        if (origin == null)
            return null;

        // 1. Valid candidate stops
        List<Transform> candidates = new List<Transform>();
        if (wanderingPoints != null)
        {
            for (int i = 0; i < wanderingPoints.Length; i++)
                if (wanderingPoints[i] != null) candidates.Add(wanderingPoints[i]);
        }

        int lo = Mathf.Max(0, minWanderingPointsPerAgent);
        int hi = Mathf.Max(lo, maxWanderingPointsPerAgent);
        int wanted = Mathf.Min(Random.Range(lo, hi + 1), candidates.Count);

        // 2. Ordered, distinct, leg-validated stops
        List<Transform> stops = new List<Transform>(wanted);
        Vector3 cursor = origin.position;
        while (stops.Count < wanted && candidates.Count > 0)
        {
            int pick = Random.Range(0, candidates.Count);
            Transform candidate = candidates[pick];
            candidates.RemoveAt(pick); // each candidate is considered at most once -> bounded

            if (!IsLegReachable(cursor, candidate.position))
                continue;

            stops.Add(candidate);
            cursor = candidate.position;
        }

        // 3. Exit reachable from the last stop (dropping trailing stops if necessary)
        Transform exit = null;
        bool hasExits = false;
        if (exits != null)
        {
            for (int i = 0; i < exits.Length; i++)
                if (exits[i] != null) { hasExits = true; break; }
        }

        if (hasExits)
        {
            exit = PickReachableExit(cursor);
            while (exit == null && stops.Count > 0)
            {
                stops.RemoveAt(stops.Count - 1);
                cursor = stops.Count > 0 ? stops[stops.Count - 1].position : origin.position;
                exit = PickReachableExit(cursor);
            }

            if (exit == null)
            {
                WarnNoValidRoute("no exit is reachable from the entrance/stops");
                return null;
            }
        }
        else if (stops.Count == 0)
        {
            WarnNoValidRoute("no usable wanderingPoints and no exits configured");
            return null;
        }

        // 4. Assemble: [origin, stops..., exit]
        List<Transform> list = new List<Transform>(stops.Count + 2);
        list.Add(origin);
        list.AddRange(stops);
        if (exit != null) list.Add(exit);

        if (list.Count < 2)
        {
            WarnNoValidRoute("route has no destination");
            return null;
        }

        return list;
    }

    // A random non-null exit reachable from 'from' (each exit tried at most once), or null.
    private Transform PickReachableExit(Vector3 from)
    {
        List<Transform> remaining = new List<Transform>(exits.Length);
        for (int i = 0; i < exits.Length; i++)
            if (exits[i] != null) remaining.Add(exits[i]);

        while (remaining.Count > 0)
        {
            int pick = Random.Range(0, remaining.Count);
            Transform candidate = remaining[pick];
            remaining.RemoveAt(pick);
            if (IsLegReachable(from, candidate.position))
                return candidate;
        }
        return null;
    }

    // True if the NavMesh has a complete path between the two positions (point-to-point, independent of any NPC).
    private bool IsLegReachable(Vector3 from, Vector3 to)
    {
        if (routeCheckPath == null) routeCheckPath = new NavMeshPath();
        return NavMesh.CalculatePath(from, to, NavMesh.AllAreas, routeCheckPath)
               && routeCheckPath.status == NavMeshPathStatus.PathComplete;
    }

    // Throttled (the spawn loop ticks every 'spawnInterval'): at most one warning per 5 s.
    private void WarnNoValidRoute(string reason)
    {
        if (!enableDebugLogs || Time.time < nextRouteWarnTime) return;
        nextRouteWarnTime = Time.time + 5f;
        Debug.LogWarning($"[SpawnerManager] No valid Part 2 itinerary this tick ({reason}); spawn skipped, will retry. " +
            "Check that wanderingPoints, entrances and exits lie on/near a connected walkable NavMesh.");
    }

    #endregion

    // =================================================================================================================
    #region 10. Acquiring an instance: pool reuse / instantiate, NavMesh placement, profile
    // =================================================================================================================

    // Pops an inactive instance from the pool and places it on the NavMesh. Returns null (and discards the instance)
    // if the prefab lacks PedestrianAgent.
    private GameObject TakeFromPool(Vector3 spawnPos)
    {
        int last = pool.Count - 1;
        GameObject go = pool[last];
        pool.RemoveAt(last);

        if (go.GetComponent<PedestrianAgent>() == null)
        {
            if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
            Destroy(go);
            return null;
        }

        PlacePooledAgent(go, spawnPos);
        return go;
    }

    // Pooled placement (the instance is still INACTIVE here; it is activated by SpawnAgent afterwards): sample and
    // warp; the transform is only moved when the warp succeeded. Without any NavMesh near spawnPos the instance is
    // put at spawnPos and its NavMeshAgent disabled as a fallback.
    private void PlacePooledAgent(GameObject go, Vector3 spawnPos)
    {
        NavMeshAgent nav = go.GetComponent<NavMeshAgent>();

        NavMeshHit hit;
        if (NavMesh.SamplePosition(spawnPos, out hit, spawnSampleRadius * 1.5f, NavMesh.AllAreas))
        {
            if (nav != null)
            {
                if (!nav.enabled) nav.enabled = true;
                bool warped = nav.Warp(hit.position);
                if (warped && nav.isOnNavMesh)
                {
                    // set transform to sampled nav position
                    go.transform.position = hit.position;
                    // reset path safely only if agent is on navmesh
                    nav.ResetPath();
                }
            }
            else
            {
                // no NavMeshAgent: just place transform
                go.transform.position = hit.position;
            }
        }
        else
        {
            // If no sample found, place at spawnPos (which at least is near sampling origin) and disable nav as fallback
            go.transform.position = spawnPos;
            if (nav != null)
            {
                nav.enabled = false;
            }
            if (enableDebugLogs) Debug.LogWarning("SpawnerManager: pooled agent couldn't be sampled on NavMesh at spawnPos; NavMeshAgent disabled as fallback.");
        }
    }

    // Instantiates a fresh (active) instance and places it on the NavMesh. Returns null (and destroys the instance)
    // if the prefab lacks PedestrianAgent.
    private GameObject InstantiateNewAgent(Vector3 spawnPos)
    {
        GameObject go = Instantiate(agentPrefab, spawnPos, Quaternion.identity);

        if (go.GetComponent<PedestrianAgent>() == null)
        {
            if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
            Destroy(go);
            return null;
        }

        PlaceNewAgent(go, spawnPos);
        return go;
    }

    // Fresh-instance placement (already active, so Warp works immediately). A failed sample or warp disables the
    // NavMeshAgent on this instance to avoid later exceptions.
    private void PlaceNewAgent(GameObject go, Vector3 spawnPos)
    {
        NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
        if (nav == null)
        {
            // no NavMeshAgent on prefab - just place transform
            go.transform.position = spawnPos;
            return;
        }

        NavMeshHit hit;
        if (NavMesh.SamplePosition(spawnPos, out hit, spawnSampleRadius * 1.5f, NavMesh.AllAreas))
        {
            if (!nav.enabled) nav.enabled = true;
            bool warped = nav.Warp(hit.position);
            if (warped && nav.isOnNavMesh)
            {
                go.transform.position = hit.position;
                nav.ResetPath();
            }
            else
            {
                // Warp failed defensively: keep nav disabled to avoid later exceptions
                nav.enabled = false;
                go.transform.position = spawnPos;
                if (enableDebugLogs) Debug.LogWarning("SpawnerManager: Instantiated agent but warp to sampled NavMesh position failed; disabling NavMeshAgent as fallback.");
            }
        }
        else
        {
            // Could not sample NavMesh near spawnPos: disable agent to avoid exceptions
            nav.enabled = false;
            go.transform.position = spawnPos;
            if (enableDebugLogs) Debug.LogWarning("SpawnerManager: Instantiated agent but could not sample NavMesh near spawnPos; NavMeshAgent disabled on this instance as fallback.");
        }
    }

    // Applies the default AgentProfile (appearance, speed, NavMeshAgent shape, UncertainBehavior roll) via the
    // factory; without a factory only the NavMeshAgent values are applied (best effort).
    private void ApplyAgentProfile(GameObject go)
    {
        if (agentFactory != null && defaultProfile != null)
        {
            // We already instantiated/reused the GameObject, so use ApplyProfileToInstance (not a create method).
            agentFactory.ApplyProfileToInstance(go, defaultProfile);
        }
        else if (defaultProfile != null)
        {
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            if (nav != null)
            {
                nav.radius = defaultProfile.agentRadius;
                nav.height = defaultProfile.agentHeight;
                nav.speed = defaultProfile.speed;
                nav.acceleration = defaultProfile.acceleration;
                nav.angularSpeed = defaultProfile.angularSpeed;
                nav.avoidancePriority = Mathf.Clamp(defaultProfile.avoidancePriority, 0, 99);
            }
        }
    }

    // MoveToTarget is NavMeshAgent-only; make sure it references the runtime NavMeshAgent of this instance.
    private void BindMoverToAgent(GameObject go)
    {
        MoveToTarget mt = go.GetComponent<MoveToTarget>();
        if (mt == null) return;

        NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
        mt.agent = nav != null ? nav : mt.agent;
    }

    #endregion

    // =================================================================================================================
    #region 11. Initialising movement and registering the NPC
    // =================================================================================================================

    // Hands the generated route to PedestrianAgent in the way the movement mode requires.
    private void InitializeAgentMovement(GameObject go, PedestrianAgent agentScript, NpcMovementMode mode,
                                         List<Transform> itinerary, Transform chosenEntrance, float dwell)
    {
        if (mode == NpcMovementMode.Part2WanderingItinerary)
        {
            // PedestrianAgent places the NPC at itinerary[0] (the entrance) and walks the wandering stops with the
            // look-ahead rule; no dwell. 'dwell' is unused here.
            agentScript.Initialize(itinerary, this, mode, 0f, wanderLookAheadDistance);
            return;
        }

        PrimeMoverForAttractorItinerary(go, itinerary, chosenEntrance);
        agentScript.Initialize(itinerary, this, dwell);
    }

    // Part 3's established mover pre-configuration (start = StartTransform, first target = itinerary[0]). Kept
    // exactly as before; PedestrianAgent.Initialize()/MoveToCurrent() subsequently re-assign the same fields.
    private void PrimeMoverForAttractorItinerary(GameObject go, List<Transform> itinerary, Transform chosenEntrance)
    {
        MoveToTarget mt = go.GetComponent<MoveToTarget>();
        if (mt == null) return;

        mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;

        if (itinerary != null && itinerary.Count > 0 && itinerary[0] != null)
            mt.targetTransform = itinerary[0];
        else
        {
            if (itinerary != null && itinerary.Count > 0)
                mt.targetTransform = itinerary[itinerary.Count - 1];
            else
                mt.targetTransform = null;
        }

        mt.startPosition = go.transform.position;
    }

    // Population accounting + completion hook + data-capture notification (after the list mutation).
    private void RegisterSpawnedAgent(GameObject go, PedestrianAgent agentScript)
    {
        liveAgents++;
        activeAgents.Add(go); // track for trial-state pause/resume/despawn
        agentScript.onAgentFinished += OnAgentFinished;
        RaiseAgentSpawned(agentScript);
    }

    #endregion

    // =================================================================================================================
    #region 12. Completion and pool return
    // =================================================================================================================

    /// <summary>
    /// Called (via PedestrianAgent.onAgentFinished) when an NPC reached the end of its itinerary or was
    /// force-finished. Updates the population accounting and returns the instance to the pool (or destroys it when
    /// pooling is off).
    /// </summary>
    public void OnAgentFinished(PedestrianAgent agent)
    {
        agent.onAgentFinished -= OnAgentFinished;

        liveAgents--;
        activeAgents.Remove(agent.gameObject); // stop tracking this agent for trial-state pause/despawn
        RaiseAgentDespawned(agent); // data-capture observer (after list mutation)

        if (usePooling)
        {
            GameObject go = agent.gameObject;

            // Defensive: if the NavMeshAgent is present but not in a good state, ensure it's left enabled if on NavMesh.
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            if (nav != null)
            {
                if (!nav.enabled && nav.isOnNavMesh)
                {
                    nav.enabled = true;
                }
            }

            // ensure MoveToTarget state is consistent
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
            }

            // Log agent finishing and pooling info (include AgentID)
            if (enableDebugLogs)
            {
                string goId = $"{go.name} AgentID={agent.AgentID}";
                string navState = nav != null ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
                Debug.Log($"[SpawnerManager][OnAgentFinished] {goId} returning to pool. liveAgents={liveAgents} {navState}");
            }

            go.SetActive(false);
            pool.Add(go);
        }
        else
        {
            Destroy(agent.gameObject);
        }
    }

    #endregion

    // =================================================================================================================
    #region 13. Attractor occupancy (Part 3 only; called by PedestrianAgent)
    // =================================================================================================================

    public bool TryEnterAttractor(AttractorInfo info, PedestrianAgent agent)
    {
        if (info == null || info.transform == null) return true;
        if (info.capacity <= 0)
        {
            if (!info.occupants.Contains(agent)) info.occupants.Add(agent);
            return true;
        }
        if (info.occupants.Count < info.capacity)
        {
            if (!info.occupants.Contains(agent)) info.occupants.Add(agent);
            return true;
        }
        return false;
    }

    public void ReleaseAttractor(AttractorInfo info, PedestrianAgent agent)
    {
        if (info == null) return;
        if (info.occupants.Contains(agent)) info.occupants.Remove(agent);
    }

    public AttractorInfo FindAttractorInfo(Transform t)
    {
        if (attractors == null) return null;
        foreach (var a in attractors)
            if (a != null && a.transform == t) return a;
        return null;
    }

    #endregion

    // =================================================================================================================
    #region 14. Diagnostics (all gated by enableDebugLogs)
    // =================================================================================================================

    // One summary line per spawn, plus the multi-line itinerary dump. Callers check enableDebugLogs first.
    private void LogSpawn(GameObject go, PedestrianAgent agentScript, NpcMovementMode mode, bool reused,
                          int entranceIndex, Transform chosenEntrance, List<Transform> itinerary, float dwell)
    {
        MoveToTarget mt = go.GetComponent<MoveToTarget>();
        NavMeshAgent nav = go.GetComponent<NavMeshAgent>();

        LogAgentItinerary(go, mode, entranceIndex, chosenEntrance, itinerary, mt, nav, dwell);

        string startName = startTransform != null ? startTransform.name : "samplingOrigin";
        Debug.Log($"[SpawnerManager] Spawned {(reused ? "pooled" : "new")} {mode} agent AgentID={agentScript.AgentID} " +
                  $"{(reused ? "(pooled reuse) " : "")}at Start={startName} pos={go.transform.position}, itinerary entries={itinerary.Count}");
    }

    /// <summary>Log function for the Transform-based itinerary (both movement modes).</summary>
    void LogAgentItinerary(GameObject go, NpcMovementMode mode, int entranceIndex, Transform entrance,
                           List<Transform> itinerary, MoveToTarget mt, NavMeshAgent nav, float dwell)
    {
        // Bail out before doing any string work if logging is disabled - this early-return (not just gating
        // Debug.Log) is the part that actually matters for performance.
        if (!enableDebugLogs) return;

        // Try to include the unique AgentID in logs if present
        PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
        string agentId = pa != null ? pa.AgentID : "n/a";
        string goId = $"{go.name} AgentID={agentId}";
        string entranceName = entrance != null ? entrance.name : "null";
        string entranceInfo = $"Entrance[{entranceIndex}]={entranceName}";

        // Build itinerary description
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"[SpawnerManager][AgentSpawn] {goId} mode={mode} spawnInfo: {entranceInfo} dwell={dwell:F2}s liveAgents={liveAgents}");

        if (itinerary == null || itinerary.Count == 0)
        {
            sb.AppendLine("  Itinerary: EMPTY");
        }
        else
        {
            for (int i = 0; i < itinerary.Count; i++)
            {
                Transform t = itinerary[i];
                if (t == null)
                {
                    sb.AppendLine($"  Itinerary[{i}] = NULL");
                    continue;
                }

                // If this is first entry treat it as entrance for clarity
                if (i == 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = ENTRANCE name='{t.name}'");
                    continue;
                }

                // Check if this transform is one of the exits
                int exitIdx = -1;
                if (exits != null)
                {
                    for (int xi = 0; xi < exits.Length; xi++)
                    {
                        if (exits[xi] == t) { exitIdx = xi; break; }
                    }
                }

                if (exitIdx >= 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = EXIT   [exits[{exitIdx}]] name='{t.name}'");
                    continue;
                }

                // Otherwise check attractors
                int attractorIdx = -1;
                if (attractors != null)
                {
                    for (int ai = 0; ai < attractors.Length; ai++)
                    {
                        var a = attractors[ai];
                        if (a != null && a.transform == t)
                        {
                            attractorIdx = ai;
                            break;
                        }
                    }
                }

                if (attractorIdx >= 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = ATTRACTOR [attractors[{attractorIdx}]] name='{t.name}' capacity={(attractors[attractorIdx] != null ? attractors[attractorIdx].capacity.ToString() : "n/a")}");
                    continue;
                }

                // Part 2 intermediate stops
                int wanderIdx = -1;
                if (wanderingPoints != null)
                {
                    for (int wi = 0; wi < wanderingPoints.Length; wi++)
                    {
                        if (wanderingPoints[wi] == t) { wanderIdx = wi; break; }
                    }
                }

                if (wanderIdx >= 0)
                {
                    sb.AppendLine($"  Itinerary[{i}] = WANDERING_POINT [wanderingPoints[{wanderIdx}]] name='{t.name}'");
                    continue;
                }

                // Unknown transform (could be custom, or reused)
                sb.AppendLine($"  Itinerary[{i}] = UNKNOWN name='{t.name}'");
            }
        }

        // Print runtime nav and MoveToTarget info
        string navInfo = nav != null ? $"NavMeshAgent(enabled={nav.enabled}, isOnNavMesh={nav.isOnNavMesh}, speed={nav.speed:F2})" : "NavMeshAgent=null";
        string mtInfo = mt != null ? $"MoveToTarget(start={(mt.startTransform!=null?mt.startTransform.name:"null")}, target={(mt.targetTransform!=null?mt.targetTransform.name:"null")}, agentAssigned={(mt.agent!=null? "yes":"no")})" : "MoveToTarget=null";

        sb.AppendLine($"  Runtime: {navInfo} | {mtInfo} | prefab={agentPrefab.name}");

        // Output the composed log
        Debug.Log(sb.ToString());
    }

    #endregion
}
