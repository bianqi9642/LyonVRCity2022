// SpawnerManager.cs
// Extended SpawnerManager that supports:
// - Applying AgentProfile via optional AgentFactory when creating or reusing agents.
// - Minimal, non-invasive changes so existing behaviors remain unchanged if new features are not used.
//
// This version fixes compile errors caused by previous MoveToTarget refactor that removed the
// public 'useNavMeshAgent' field. All references to mt.useNavMeshAgent have been removed and
// logging/decisions now rely on the actual NavMeshAgent state (nav != null && nav.enabled && nav.isOnNavMesh).
//
// NEW (trial-state gating): SpawnLoop() previously ran continuously for the lifetime of the scene,
// independent of ExperimentManager.OnTrialStart/OnTrialEnd. This meant pedestrian presence before a
// trial officially started (or after it ended) was not delineated from "in-trial" pedestrian presence,
// which could confound the recorded trial window with a continuous, ungated pedestrian stimulus.
// SpawnerManager now optionally subscribes to ExperimentManager.OnTrialStart/OnTrialEnd and:
//   - only spawns NEW agents while a trial is active (gates SpawnLoop/SpawnAgent), and
//   - optionally pauses/resumes movement of agents that are already alive when a trial ends/starts,
//     and/or force-finishes them, so that "trial recording window" and "pedestrian stimulus window"
//     can be made to coincide exactly.
// This is opt-in via 'gateSpawningByTrialState' (default true) so it fixes the confound by default,
// but can be switched off to restore the previous continuous-spawning behaviour if that is intentional
// (e.g. for ambient background population that is not meant to be the manipulated stimulus).
//
// NEW (Quest logging fix): SpawnerManager previously logged unconditionally and verbosely on every
// spawn/despawn (LogAgentItinerary builds a multi-line StringBuilder per spawn),
// with no build-time or runtime gate - unlike MoveToTarget, which already has an 'enableDebugLogs' toggle.
// On Quest, this string-heavy per-spawn logging is a known performance drain that scales with
// maxAgents/spawnInterval. SpawnerManager now has its own 'enableDebugLogs' flag (mirroring MoveToTarget's),
// defaulting to true so existing Editor workflows are unaffected, and every Debug.Log/LogWarning/LogError
// call in this script is gated behind it so it can be switched off for Quest builds.
//
// NEW (NPC warm-up): SpawnLoop()'s spawning gate previously had exactly one "open" condition while gating
// is enabled: trialActive (set by HandleTrialStart/HandleTrialEnd, which are themselves driven only by
// ExperimentManager.OnTrialStart/OnTrialEnd). That meant NPCs could never spawn before the very first
// participant trial of a Part. ExperimentManager now runs an explicit NPC warm-up window BEFORE the
// participant-facing Start panel is shown (and therefore before OnTrialStart fires), during which it wants
// SpawnerManager to spawn and move NPCs using its completely normal runtime behaviour - same spawnInterval,
// maxAgents, itinerary generation, AttractorDwell, UncertainBehavior, MoveToTarget, and pooling - so the NPC
// population has time to form and spread out before the participant's trial begins. SpawnerManager exposes
// this as a second, independent gate flag ('npcWarmupActive', opened/closed only via the new
// BeginNpcWarmup()/EndNpcWarmup() public methods) rather than overloading 'trialActive', because the two
// represent different experimental semantics (pre-trial population settling vs. the participant's actual
// trial window) and must be distinguishable by any observer (see DataCaptureManager.NpcCaptureActive, which
// must NOT treat warm-up NPC activity as trial data). ExperimentManager sequences the two so they never
// overlap and there is no gap in spawning at the warm-up -> trial boundary. This change touches ONLY the
// spawning gate; it does not add, remove, or alter any movement/itinerary/pooling logic.
//
// NEW (per-Part NPC control, supersedes the Stage-6 SetNpcsEnabledForPart mechanism): whether NPCs may
// operate for the Part currently running is now PULLED directly from
// ExperimentManager.CurrentPartConfig.npcEnabled every SpawnLoop tick (NpcsEnabledForActivePart()), instead
// of being pushed into a locally-cached field via a dedicated method call. This removes a second,
// independently-updated copy of the same boolean - ExperimentManager remains the single source of truth for
// all per-Part NPC/capture configuration, and every other system (this one included) merely reads it.
//
// NEW (End Panel hard stop): ExperimentManager now raises OnEndPanelShown immediately before a Part's End
// Panel is shown to the participant - strictly before part_end (raised only once the participant confirms
// the panel). SpawnerManager subscribes unconditionally (regardless of gateSpawningByTrialState) to: close
// the spawning gate (endPanelActive, ANDed into SpawnLoop's gate the same way NpcsEnabledForActivePart() is)
// and freeze every currently-active agent using ONLY existing public APIs - MoveToTarget.PauseExternal() and
// disabling UncertainBehavior (see HardStopAgent()). Because a one-shot freeze is not airtight against
// PedestrianAgent's own dwell coroutines (independent of MoveToTarget's paused state), the freeze is
// continuously re-applied every frame for as long as endPanelActive is true (see Update()). The hard stop is
// cleared only when the NEXT Part's NPC lifecycle begins (ResetForNewPart(), called unconditionally from
// ExperimentManager.RunNpcWarmup()). None of this touches NavMeshAgent directly, and none of it changes the
// Stage 4 architecture (UncertainBehavior -> MoveToTarget -> NavMeshAgent).
//
// NEW (Part 2 continuous undirected wandering): SpawnAgent() now reads
// ExperimentManager.CurrentPartConfig.npcDirectedMovement (via DirectedMovementForActivePart(), mirroring
// NpcsEnabledForActivePart()'s pull pattern exactly) to decide, per spawn, whether to build a directed
// itinerary (BuildItinerary() - unchanged, Part 3's existing behaviour) or initialize the agent for
// undirected wandering instead (PedestrianAgent.Initialize(SpawnerManager, float, Transform) - no
// itinerary, no attractors, no exit). The new 'wanderingPoints' array is a plain Transform[], declared and
// used exactly like the existing entrances/exits/attractors arrays, and is NEVER referenced by
// BuildItinerary() or any other Part 3 code path. Population accounting
// (liveAgents/activeAgents/maxAgents/spawnInterval) is completely unconditional and shared between both
// modes, unchanged - this is what guarantees Part 2 and Part 3 draw from the exact same population source
// of truth.
//
// NEW (Part 2 shares Part 3's spawn origin and initial-Entrance draw): the physical spawn position
// (samplingOrigin/spawnPos, startTransform-priority) is computed ONCE, unconditionally, identically for
// BOTH modes - exactly as it always was, before ANY Part 2 wandering work. 'chosenEntrance' - the SAME
// Random.Range(0, entrances.Length) draw Part 3 already made - is passed into the undirected
// Initialize(...) overload as a one-time initial movement target: an undirected agent spawns at the shared
// startTransform-based origin, walks to chosenEntrance using MoveToTarget's normal arrival behaviour, and
// only then begins continuous wandering through wanderingPoints[]. chosenEntrance is NEVER used as the
// undirected agent's physical spawn position, and is NEVER added to any itinerary.
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
    [Header("References")]
    // NEW: spawn start point (drag a transform in inspector). If provided, agents will spawn at/near this transform.
    // The itinerary's first entry (itinerary[0]) will be chosen randomly from `entrances` regardless of this startTransform.
    public Transform startTransform;

    public Transform[] entrances;
    public Transform[] exits;
    public AttractorInfo[] attractors;

    [Header("Wandering Points (Part 2 - undirected mode only)")]
    [Tooltip("Predefined, experimenter-placed Transforms used ONLY by undirected (Part 2) NPCs as continuous " +
             "wandering destinations. Anonymous movement waypoints - NOT attractors, NOT exits, and never " +
             "referenced by BuildItinerary() or any directed (Part 3) code path. Place them on/near the " +
             "walkable NavMesh, spread across the area NPCs should wander, forming a plausible low-density " +
             "pedestrian movement network rather than a random scatter. At runtime each candidate is " +
             "validated with NavMesh.CalculatePath before being accepted as a target - an unreachable point " +
             "is skipped (with a warning), never silently replaced by a generated position.")]
    public Transform[] wanderingPoints;

    [Tooltip("Distance (metres) at which an undirected (Part 2) NPC selects its NEXT wandering point, " +
             "before MoveToTarget's own (much tighter) arriveThreshold would otherwise bring it to a full " +
             "stop. Must be larger than MoveToTarget.arriveThreshold (0.2 by default) by a comfortable " +
             "margin - see the implementation notes shipped with this feature for the reasoning. Tune " +
             "visually in Play mode: too small and NPCs visibly stop-start at every point; too large and " +
             "NPCs change direction while still far from the point they were walking toward.")]
    public float wanderLookAheadDistance = 1.5f;

    [Header("Prefab & Spawn")]
    public GameObject agentPrefab;
    public AgentFactory agentFactory;           // optional factory to create/apply profile
    public AgentProfile defaultProfile;         // optional default profile applied at spawn
    public float spawnInterval = 0.5f; // seconds between spawns
    public int maxAgents = 200;

    [Header("Itinerary / Behavior")]
    public int minAttractorsPerAgent = 1;
    public int maxAttractorsPerAgent = 4;
    public Vector2 dwellTimeSec = new Vector2(5f, 15f); // min/max dwell

    [Header("Spawn Sampling")]
    public float spawnSampleRadius = 1.0f; // sample radius around entrance on the horizontal plane
    public int spawnSampleMaxAttempts = 8;

    // New fields to control ground projection behaviour:
    [Tooltip("Layer(s) considered ground for projection raycasts. Default = Everything.")]
    public LayerMask groundLayers = ~0;
    [Tooltip("Maximum distance used when raycasting down to find ground from a sampling height.")]
    public float raycastDownDistance = 20f;
    [Tooltip("Small upward offset applied after a ground hit to avoid clipping the ground.")]
    public float groundOffset = 0.1f;
    [Tooltip("Vertical offsets (relative to entrance Y) to try when NavMesh.SamplePosition fails to find a NavMesh from the projected ground point.")]
    public float[] verticalOffsets = new float[] { 2.0f, 1.0f, 0.5f, 0.0f, -1.0f };

    [Header("Pooling")]
    public bool usePooling = true;
    public int poolPrewarm = 50;

    // time to wait for NavMesh availability before giving up (seconds)
    public float navmeshWaitTimeout = 5f;

    [Header("Trial State Gating (NEW)")]
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

    [Header("Debug")]
    [Tooltip("NEW: mirrors MoveToTarget.enableDebugLogs. When true (default), SpawnerManager logs " +
             "per-spawn/per-despawn itinerary details and warnings/errors via Debug.Log(...). Disable this " +
             "for Quest/device builds to avoid the string-building + logging cost scaling with " +
             "maxAgents/spawnInterval - it is purely a diagnostic aid and has no effect on spawn behaviour.")]
    public bool enableDebugLogs = true;

    // internal
    private List<GameObject> pool = new List<GameObject>();
    private int liveAgents = 0;

    // NEW: tracks currently-alive (spawned, not yet finished) agent GameObjects so they can be
    // paused/resumed/force-finished as a group when trial state changes.
    private List<GameObject> activeAgents = new List<GameObject>();

    // ---------------------------------------------------------------------------------------------
    // Data-capture (Revision 2) - OBSERVER hooks. These do NOT change spawning, pooling, itinerary,
    // movement, timing, or randomisation. They only expose the currently-alive agent list and fire
    // spawn/despawn notifications AFTER the internal list has already been mutated.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Read-only view of the currently-alive (spawned, not finished) agent GameObjects.</summary>
    public IReadOnlyList<GameObject> ActiveAgents => activeAgents;

    /// <summary>Raised right after an agent has been added to the active list (spawn / pooled reuse).</summary>
    public event System.Action<PedestrianAgent> AgentSpawned;

    /// <summary>Raised right after an agent has been removed from the active list (finished / force-finished).</summary>
    public event System.Action<PedestrianAgent> AgentDespawned;

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

    // NEW: whether a trial is currently active. When gateSpawningByTrialState == false this flag
    // is ignored entirely (SpawnLoop behaves exactly as it did before this change).
    private bool trialActive = false;

    // NEW (NPC warm-up): whether an NPC warm-up window is currently open. Independent of 'trialActive' -
    // ExperimentManager sequences the two so they never overlap (EndNpcWarmup() is always called immediately
    // before OnTrialStart fires), but they are tracked as two separate flags because they represent
    // different experimental semantics: pre-trial population settling vs. the participant's actual trial
    // window. Only ExperimentManager (via BeginNpcWarmup/EndNpcWarmup) ever sets this.
    private bool npcWarmupActive = false;

    /// <summary>Read-only: whether an NPC warm-up window is currently open.</summary>
    public bool NpcWarmupActive => npcWarmupActive;

    // NEW (Part-scoped NPC control, Requirement 1/7): whether the NPC system is permitted to operate for the
    // Part currently running is now PULLED directly from ExperimentManager.CurrentPartConfig.npcEnabled every
    // SpawnLoop tick (see NpcsEnabledForActivePart() below), rather than pushed/cached via a dedicated
    // SetNpcsEnabledForPart() call (the Stage-6 mechanism this supersedes). This removes a second,
    // independently-updated copy of the same boolean - ExperimentManager.CurrentPartConfig remains the sole
    // source of truth, and SpawnerManager merely reads it. Falls back to "enabled" (true) if no
    // ExperimentManager/CurrentPartConfig is available, so a scene that never configures per-Part NPC control
    // keeps unrestricted spawning (matching the behaviour of any other field left at its default).
    private bool NpcsEnabledForActivePart()
    {
        if (experimentManager == null) return true;
        var cfg = experimentManager.CurrentPartConfig;
        return cfg == null || cfg.npcEnabled;
    }

    // NEW (Part 2 undirected wandering): mirrors NpcsEnabledForActivePart() exactly - PULLED, not pushed,
    // from the same single source of truth (ExperimentManager.CurrentPartConfig.npcDirectedMovement).
    // Falls back to "directed" (true) if no ExperimentManager/CurrentPartConfig is available, so a scene
    // that never configures per-Part movement mode reproduces the original (pre-undirected-wandering)
    // behaviour exactly.
    private bool DirectedMovementForActivePart()
    {
        if (experimentManager == null) return true;
        var cfg = experimentManager.CurrentPartConfig;
        return cfg == null || cfg.npcDirectedMovement;
    }

    // NEW (End Panel hard stop, Requirement 2): true from the moment ExperimentManager.OnEndPanelShown fires
    // for the current Part until SpawnerManager.ResetForNewPart() is called at the start of the NEXT Part's
    // NPC lifecycle (see RunNpcWarmup()). Closes the spawning gate unconditionally (regardless of
    // gateSpawningByTrialState) and drives the continuous per-frame freeze in Update() below.
    private bool endPanelActive = false;

    /// <summary>Read-only: whether the End-Panel hard stop is currently in effect.</summary>
    public bool EndPanelActive => endPanelActive;

    // state to ensure we don't spawn before prewarm/initialization finished
    bool poolInitializationComplete = false;

    void Start()
    {
        // NEW: resolve ExperimentManager unconditionally (not just when gateSpawningByTrialState is true) -
        // it is now also needed for the per-Part NPC-enable/movement-mode pulls and for the End-Panel
        // hard-stop subscription below, all of which must work regardless of trial-state gating.
        if (experimentManager == null)
        {
            experimentManager = FindObjectOfType<ExperimentManager>();
        }

        // Existing trial-state gating subscription - unchanged logic, just using the reference resolved above.
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

        // NEW (End Panel hard stop): subscribed unconditionally - the hard stop must apply even in scenes
        // that run with gateSpawningByTrialState == false (continuous spawning), where trialActive alone
        // would otherwise never close the spawning gate at the End Panel boundary.
        if (experimentManager != null)
        {
            experimentManager.OnEndPanelShown += HandleEndPanelShown;
        }

        // Start sequence that waits/attempts NavMesh prewarm and only then starts spawn loop.
        StartCoroutine(InitializePoolThenSpawn());
    }

    void OnDestroy()
    {
        // NEW: unsubscribe to avoid referencing a destroyed SpawnerManager from ExperimentManager's event.
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= HandleTrialStart;
            experimentManager.OnTrialEnd -= HandleTrialEnd;
            experimentManager.OnEndPanelShown -= HandleEndPanelShown;
        }
    }

    void Update()
    {
        // NEW (End Panel hard stop): continuously re-assert the freeze on every active agent, every frame,
        // for as long as endPanelActive is true. A single one-shot freeze (applied once in
        // HandleEndPanelShown()) is not airtight: PedestrianAgent's own dwell coroutines
        // (WaitAtAttractor/PollForCapacity/SimpleDwellThenAdvance) run independently of MoveToTarget's
        // paused state, and MoveToTarget.MoveTo(...) unconditionally un-stops the NavMeshAgent regardless of
        // externallyPaused - so an agent whose dwell timer happens to elapse while the End Panel is visible
        // would otherwise resume moving until the next OnTrialStart. Re-applying the freeze every frame
        // (cheap early-return when not needed) bounds any such escape to a single frame. See
        // HardStopAllActiveAgents() for what "freeze" means and why (existing public APIs only).
        if (endPanelActive) HardStopAllActiveAgents();
    }

    /// <summary>
    /// NEW (End Panel hard stop). Called when ExperimentManager raises OnEndPanelShown for the Part currently
    /// running - i.e. immediately before that Part's End Panel is instantiated/shown to the participant
    /// (strictly before part_end, which only fires later once the participant confirms the panel). Closes
    /// the spawning gate (via endPanelActive, checked in SpawnLoop) and immediately freezes every
    /// currently-active agent. Unconditional: applies regardless of gateSpawningByTrialState,
    /// pauseLiveAgentsOutsideTrial, or despawnLiveAgentsOnTrialEnd - none of those existing toggles are
    /// modified.
    /// </summary>
    private void HandleEndPanelShown()
    {
        endPanelActive = true;
        HardStopAllActiveAgents();
        if (enableDebugLogs) Debug.Log("[SpawnerManager] End Panel shown -> NPC spawning and movement hard-stopped.");
    }

    /// <summary>
    /// NEW (End Panel hard stop). Called by ExperimentManager (via RunNpcWarmup(), unconditionally, at the
    /// very start of every Part's NPC lifecycle) to clear any End-Panel hard stop left over from whichever
    /// Part ran previously. Must happen before that new Part's own warm-up/trial spawning decisions are
    /// evaluated - resetting only on the new Part's OnTrialStart would be too late, since NPC warm-up
    /// spawning happens BEFORE OnTrialStart.
    /// </summary>
    public void ResetForNewPart()
    {
        endPanelActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] New Part starting -> End-Panel hard stop cleared.");
    }

    /// <summary>
    /// NEW (End Panel hard stop). Freezes one active agent using ONLY existing public APIs - never touches
    /// NavMeshAgent directly, and does not modify the Stage 4 movement architecture
    /// (UncertainBehavior -> MoveToTarget -> NavMeshAgent):
    ///   - MoveToTarget.PauseExternal(true, true): halts NavMeshAgent movement and freezes the animator -
    ///     the same mechanism already used by PauseAllActiveAgents()/HandleTrialEnd().
    ///   - Disabling UncertainBehavior (if present): stops ITS OWN per-second-probability state machine
    ///     (Pause/Hesitate/Slow/Detour), which would otherwise independently call
    ///     MoveToTarget.ResumeExternal()/SetTemporarySpeed() on its own timer regardless of the pause above.
    ///     UncertainBehavior.OnDisable() only unsubscribes its own event handlers - no movement side effect.
    ///     A pooled agent's UncertainBehavior is unconditionally re-enabled/re-rolled by
    ///     AgentFactory.ApplyProfileToInstance() on its next spawn, so disabling it here has no lasting effect
    ///     beyond the current (ending) Part.
    /// Applies identically to directed and undirected agents - it only touches MoveToTarget/UncertainBehavior,
    /// neither of which differs between the two movement modes.
    /// Idempotent and cheap - safe to call every frame (see Update() above).
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
    /// NEW: called when ExperimentManager fires OnTrialStart. Opens the spawning gate and, if configured,
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
    /// NEW: called when ExperimentManager fires OnTrialEnd. Closes the spawning gate so no new agents
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
    /// NEW (NPC warm-up). Called by ExperimentManager at the start of a Part, BEFORE the participant-facing
    /// Start panel is shown and BEFORE OnTrialStart fires. Opens the spawning gate exactly like a trial does
    /// (SpawnLoop below simply OR's this flag together with trialActive), using the existing
    /// spawnInterval/maxAgents/itinerary-generation-or-undirected-wandering/pooling machinery completely
    /// unchanged, so the NPC population can begin forming and distributing itself before the participant's
    /// trial is officially under way. Does NOT fire OnTrialStart, does NOT touch trialActive, and does NOT
    /// pause/resume/despawn any agent - it only flips the spawning gate.
    /// </summary>
    public void BeginNpcWarmup()
    {
        npcWarmupActive = true;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] NPC warm-up started -> NPC spawning enabled (pre-trial).");
    }

    /// <summary>
    /// NEW (NPC warm-up). Called by ExperimentManager once the configured warm-up duration has elapsed,
    /// immediately before OnTrialStart is invoked. Closes the warm-up gate. Deliberately does NOT pause,
    /// resume, or despawn any currently-alive agent - existing NPCs (walking, dwelling, mid-detour, mid-wander,
    /// etc.) simply continue exactly as they were. Because ExperimentManager calls this and then invokes
    /// OnTrialStart in the same breath (no yield in between), SpawnLoop's combined gate
    /// ('trialActive || npcWarmupActive') never goes false in between, so there is no gap in spawning across
    /// the warm-up -> trial boundary.
    /// </summary>
    public void EndNpcWarmup()
    {
        npcWarmupActive = false;
        if (enableDebugLogs) Debug.Log("[SpawnerManager] NPC warm-up ended.");
    }

    /// <summary>
    /// NEW: freezes movement (and animator) of every currently-alive agent via MoveToTarget.PauseExternal,
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

    /// <summary>
    /// NEW: resumes movement of every currently-alive agent previously paused by PauseAllActiveAgents.
    /// </summary>
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
    /// NEW: immediately terminates every currently-alive agent's itinerary (PedestrianAgent.ForceFinish()),
    /// which triggers the normal onAgentFinished -> OnAgentFinished(...) pooling/destroy pathway. Iterates
    /// over a copy of the list because OnAgentFinished mutates activeAgents while we iterate. Works
    /// identically for directed and undirected agents - ForceFinish() is unconditional in both.
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

    IEnumerator InitializePoolThenSpawn()
    {
        // Wait for NavMesh readiness and prewarm pool if configured
        if (usePooling)
        {
            yield return StartCoroutine(WaitForNavMeshThenPrewarm());
        }
        else
        {
            // Non-pooling: we still want to briefly wait for NavMesh availability (bounded by timeout),
            // so our first spawns have a chance of sampling NavMesh. This is non-blocking if NavMesh is ready.
            yield return StartCoroutine(WaitForNavMeshAvailability());
        }

        poolInitializationComplete = true;
        // start main spawn loop after prewarm completes
        StartCoroutine(SpawnLoop());
    }

    IEnumerator WaitForNavMeshAvailability()
    {
        float start = Time.time;
        bool navAvailable = false;

        Vector3 sampleOrigin = Vector3.zero;
        // Prefer user-provided startTransform as sampling origin if present, else fallback to first entrance.
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

        while (Time.time - start < navmeshWaitTimeout)
        {
            Vector3 s = SampleNavMeshNear(sampleOrigin, spawnSampleRadius, spawnSampleMaxAttempts);
            if (s != Vector3.zero)
            {
                navAvailable = true;
                break;
            }
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
            yield return null;
        }

        if (!navAvailable)
            if (enableDebugLogs) Debug.LogWarning($"SpawnerManager: NavMesh not found near entrances/startTransform within {navmeshWaitTimeout}s. Spawning will proceed but agents may be fallback to transform mode.");
        yield break;
    }

    IEnumerator WaitForNavMeshThenPrewarm()
    {
        float start = Time.time;
        bool navAvailable = false;

        // If startTransform exists prefer it as the sampling origin, otherwise use first entrance or world origin
        Vector3 sampleOrigin = Vector3.zero;
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

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

        if (!navAvailable)
            if (enableDebugLogs) Debug.LogWarning($"SpawnerManager: NavMesh not found near sample origin within {navmeshWaitTimeout}s. Prewarm will still run but any prewarmed agents that cannot find NavMesh will have their NavMeshAgent disabled as a fallback.");

        // now prewarm (coroutine)
        yield return StartCoroutine(PrewarmPoolCoroutine());
    }

    IEnumerator PrewarmPoolCoroutine()
    {
        // choose a sampling origin: prefer the user-provided startTransform, then the first entrance, otherwise world origin
        Vector3 sampleOrigin = Vector3.zero;
        if (startTransform != null)
            sampleOrigin = startTransform.position;
        else if (entrances != null && entrances.Length > 0 && entrances[0] != null)
            sampleOrigin = entrances[0].position;

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
                else
                {
                    placedOnNav = false;
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
                placedOnNav = false;
            }

            // Apply profile to prewarmed instance if factory/profile available
            if (agentFactory != null && defaultProfile != null)
            {
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }

            // Ensure MoveToTarget component knows the runtime agent reference.
            // Note: MoveToTarget was refactored to be NavMeshAgent-only and no longer exposes useNavMeshAgent;
            // here we only set the agent reference so the mover can use it.
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            if (mt != null)
            {
                mt.agent = (nav != null) ? nav : mt.agent;
                // previously code set mt.useNavMeshAgent; that field was removed in the NavMesh-only MoveToTarget.
                // Decisions about whether the agent is on navmesh should be based on nav.enabled && nav.isOnNavMesh below.
            }

            // Try to fetch PedestrianAgent.AgentID (Awake already executed)
            PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
            string agentIdStr = pa != null ? pa.AgentID : "n/a";

            // Logging: prewarm instance info (include unique AgentID for traceability)
            string navState = (nav != null) ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
            if (enableDebugLogs) Debug.Log($"[SpawnerManager][Prewarm] idx={i} placedOnNav={placedOnNav} {navState} pos={go.transform.position} prefab={agentPrefab.name} AgentID={agentIdStr}");

            // Deactivate and add to pool
            go.SetActive(false);
            pool.Add(go);

            // yield occasionally to avoid editor freeze
            if (i % 8 == 0) yield return null;
        }

        yield break;
    }

    IEnumerator SpawnLoop()
    {
        while (true)
        {
            // Only allow spawning once initialization (prewarm/wait) is complete.
            if (!poolInitializationComplete)
            {
                yield return null;
                continue;
            }

            // NEW: when gating is enabled, only spawn new agents while a trial is active OR an NPC warm-up
            // window is open (see BeginNpcWarmup/EndNpcWarmup). The loop keeps running (so it re-checks every
            // 'spawnInterval') rather than stopping, so spawning resumes automatically as soon as the next
            // warm-up or OnTrialStart fires.
            // NEW (Part-scoped NPC control, Requirement 1/3): NpcsEnabledForActivePart() is ANDed in
            // regardless of gateSpawningByTrialState, so a Part configured with npcEnabled=false in its
            // ExperimentManager.PartCaptureConfig stays NPC-free even if trialActive happens to be true
            // (every Part fires OnTrialStart/OnTrialEnd) or if gating is switched off scene-wide.
            // NEW (End Panel hard stop, Requirement 2/2D): '!endPanelActive' is ALSO ANDed in regardless of
            // gateSpawningByTrialState, so no new NPC can spawn once the current Part's End Panel has been
            // shown, even in a continuous-spawning (gateSpawningByTrialState == false) configuration where
            // trialActive would otherwise never close the gate at that boundary.
            // NOTE: DirectedMovementForActivePart() (which movement MODE a new spawn gets) is read inside
            // SpawnAgent() itself, not here - it does not affect whether spawning is allowed, only what kind
            // of agent gets spawned.
            bool allowedToSpawn = !endPanelActive && NpcsEnabledForActivePart() &&
                ((!gateSpawningByTrialState) || trialActive || npcWarmupActive);

            if (allowedToSpawn && liveAgents < maxAgents)
            {
                SpawnAgent();
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SpawnAgent()
    {
        // If neither a startTransform nor entrances are available, we cannot spawn.
        if (startTransform == null && (entrances == null || entrances.Length == 0) || agentPrefab == null)
            return;

        // Choose a random entrance - the SAME selection, used identically by both modes:
        // - directed (Part 3): becomes itinerary[0], the first waypoint of BuildItinerary()'s route.
        // - undirected (Part 2): becomes the agent's one-time INITIAL MOVEMENT TARGET (walked to from the
        //   shared startTransform-based spawn position, via PedestrianAgent's new initial-Entrance leg -
        //   see the branch inside each reuse/instantiate path below), never the physical spawn position and
        //   never added to any itinerary. Entrance selection itself is completely unconditional and shared
        //   between both modes - there is only ever one random-entrance draw per spawn.
        int entranceIndex = -1;
        Transform chosenEntrance = null;
        if (entrances != null && entrances.Length > 0)
        {
            entranceIndex = Random.Range(0, entrances.Length);
            chosenEntrance = entrances[entranceIndex];
        }

        // Determine spawn sampling origin:
        // - If user provided startTransform, sample around it (this is the new dragged-in start point).
        // - Otherwise fallback to the chosen entrance position (legacy behavior).
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

        // NEW (Part 2 continuous undirected wandering): resolve the movement mode for THIS spawn, once, up
        // front. Reused identically by both the pooled-reuse and fresh-instantiate branches below.
        bool directedMode = DirectedMovementForActivePart();

        GameObject go = null;

        if (usePooling && pool.Count > 0)
        {
            // reuse from pool
            int last = pool.Count - 1;
            go = pool[last];
            pool.RemoveAt(last);

            // reactivate and place on navmesh if possible
            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            MoveToTarget mt = go.GetComponent<MoveToTarget>();
            PedestrianAgent agentScript = go.GetComponent<PedestrianAgent>();

            // Attempt to sample & warp to a nearby NavMesh position (this is defensive; pooled objects were prewarmed but ensure correctness)
            NavMeshHit hit;
            bool placedOnNavMesh = false;
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
                        placedOnNavMesh = true;
                    }
                }
                else
                {
                    // no NavMeshAgent: just place transform
                    go.transform.position = hit.position;
                    placedOnNavMesh = false;
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
                placedOnNavMesh = false;
                if (enableDebugLogs) Debug.LogWarning("SpawnerManager: pooled agent couldn't be sampled on NavMesh at spawnPos; NavMeshAgent disabled as fallback.");
            }

            // Apply profile to pooled instance if factory/profile available
            if (agentFactory != null && defaultProfile != null)
            {
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }
            else if (defaultProfile != null)
            {
                // best-effort apply NavMeshAgent speed/shape if no factory is present
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

            // Ensure MoveToTarget knows runtime agent by setting the agent reference.
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
                // NOTE: MoveToTarget no longer exposes a 'useNavMeshAgent' boolean; it is NavMesh-only.
                // Decisions about whether the agent is active/on-navmesh should be based on the NavMeshAgent component state.
            }

            // activate
            go.transform.rotation = Quaternion.identity;
            go.SetActive(true);

            if (agentScript == null)
            {
                if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
                Destroy(go);
                return;
            }

            if (directedMode)
            {
                // ---------------- EXISTING Part 3 directed path - unchanged ----------------
                // Build itinerary (Transform-based; BuildItinerary will insert the chosen entrance as itinerary[0]).
                float dwell = Random.Range(dwellTimeSec.x, dwellTimeSec.y);

                List<Transform> itinerary = BuildItinerary(chosenEntrance);

                // --- ensure MoveToTarget start/target reflect StartTransform -> first itinerary entry ---
                if (mt != null)
                {
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
                // --- end ---

                // Log itinerary
                LogAgentItinerary(go, entranceIndex, chosenEntrance, itinerary, mt, go.GetComponent<NavMeshAgent>(), dwell);

                // Also log AgentID explicitly
                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned pooled agent AgentID={aid} (pooled reuse) at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");

                agentScript.Initialize(itinerary, this, dwell);
            }
            else
            {
                // ---------------- NEW Part 2 undirected wandering path ----------------
                // Physical spawn position uses the SAME shared startTransform-priority samplingOrigin/spawnPos
                // as Part 3 (computed once, above, unconditionally for both modes) - NOT chosenEntrance.
                // chosenEntrance is instead passed into Initialize() below as the agent's one-time initial
                // movement target: it walks FROM its startTransform-based spawn point TO chosenEntrance first,
                // then transitions to continuous wandering. It is never added to any itinerary and is never a
                // wandering destination itself (unless a wanderingPoints[] entry happens to coincide with it).
                if (mt != null)
                {
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;
                    mt.startPosition = go.transform.position;
                }

                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                string entranceLabel = chosenEntrance != null ? chosenEntrance.name : "none";
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned pooled UNDIRECTED (wandering) agent AgentID={aid} (pooled reuse) at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}, initial Entrance target={entranceLabel}");

                agentScript.Initialize(this, wanderLookAheadDistance, chosenEntrance);
            }

            liveAgents++;
            activeAgents.Add(go); // NEW: track for trial-state pause/resume/despawn
            agentScript.onAgentFinished += OnAgentFinished;
            RaiseAgentSpawned(agentScript); // data-capture observer (after list mutation)
        }
        else
        {
            // instantiate new on spawnPos
            go = Instantiate(agentPrefab, spawnPos, Quaternion.identity);

            // After instantiate, ensure NavMeshAgent is placed on NavMesh before invoking any NavMeshAgent methods on it.
            PedestrianAgent agentScript = go.GetComponent<PedestrianAgent>();
            if (agentScript == null)
            {
                if (enableDebugLogs) Debug.LogError("Agent prefab missing PedestrianAgent script");
                Destroy(go);
                return;
            }

            NavMeshAgent nav = go.GetComponent<NavMeshAgent>();
            MoveToTarget mt = go.GetComponent<MoveToTarget>();

            // Try to place nav agent on NavMesh if possible
            NavMeshHit hit;
            bool placedOnNav = false;
            if (nav != null)
            {
                if (NavMesh.SamplePosition(spawnPos, out hit, spawnSampleRadius * 1.5f, NavMesh.AllAreas))
                {
                    if (!nav.enabled) nav.enabled = true;
                    bool warped = nav.Warp(hit.position);
                    if (warped && nav.isOnNavMesh)
                    {
                        go.transform.position = hit.position;
                        nav.ResetPath();
                        placedOnNav = true;
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
            else
            {
                // no NavMeshAgent on prefab - just place transform
                go.transform.position = spawnPos;
            }

            // Apply profile to freshly instantiated instance
            if (agentFactory != null && defaultProfile != null)
            {
                // Let factory create/apply settings: but factory.CreateAgent instantiates a prefab itself.
                // Here we already instantiated. So call ApplyProfileToInstance.
                agentFactory.ApplyProfileToInstance(go, defaultProfile);
            }
            else if (defaultProfile != null && nav != null)
            {
                // best-effort apply profile values to nav if no factory provided
                nav.radius = defaultProfile.agentRadius;
                nav.height = defaultProfile.agentHeight;
                nav.speed = defaultProfile.speed;
                nav.acceleration = defaultProfile.acceleration;
                nav.angularSpeed = defaultProfile.angularSpeed;
                nav.avoidancePriority = Mathf.Clamp(defaultProfile.avoidancePriority, 0, 99);
            }

            // Ensure MoveToTarget component is updated for runtime agent
            if (mt != null)
            {
                mt.agent = nav != null ? nav : mt.agent;
                // NOTE: MoveToTarget no longer exposes 'useNavMeshAgent' flag.
            }

            if (directedMode)
            {
                // ---------------- EXISTING Part 3 directed path - unchanged ----------------
                // Build itinerary and initialize (Transform-based; BuildItinerary inserts the chosen entrance as first entry).
                float dwell = Random.Range(dwellTimeSec.x, dwellTimeSec.y);

                List<Transform> itinerary = BuildItinerary(chosenEntrance);

                if (mt != null)
                {
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

                // Log itinerary and AgentID
                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                LogAgentItinerary(go, entranceIndex, chosenEntrance, itinerary, mt, nav, dwell);
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned new agent AgentID={aid} at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}");

                agentScript.Initialize(itinerary, this, dwell);
            }
            else
            {
                // ---------------- NEW Part 2 undirected wandering path ----------------
                // Same reasoning as the pooled-reuse branch above: physical spawn stays startTransform-based
                // (shared, unconditional samplingOrigin/spawnPos), chosenEntrance is passed to Initialize()
                // as the one-time initial movement target instead.
                if (mt != null)
                {
                    mt.startTransform = (startTransform != null) ? startTransform : chosenEntrance;
                    mt.startPosition = go.transform.position;
                }

                string aid = agentScript != null ? agentScript.AgentID : "n/a";
                string entranceLabel = chosenEntrance != null ? chosenEntrance.name : "none";
                if (enableDebugLogs) Debug.Log($"[SpawnerManager] Spawned new UNDIRECTED (wandering) agent AgentID={aid} at Start={(startTransform!=null?startTransform.name:"samplingOrigin")} pos={go.transform.position}, initial Entrance target={entranceLabel}");

                agentScript.Initialize(this, wanderLookAheadDistance, chosenEntrance);
            }

            liveAgents++;
            activeAgents.Add(go); // NEW: track for trial-state pause/resume/despawn
            agentScript.onAgentFinished += OnAgentFinished;
            RaiseAgentSpawned(agentScript); // data-capture observer (after list mutation)
        }
    }

    /// <summary>
    /// Log function for the active Transform-based itinerary. DIRECTED MODE ONLY - undirected spawns log a
    /// simpler one-line message directly at their call sites above instead of calling this.
    /// </summary>
    void LogAgentItinerary(GameObject go, int entranceIndex, Transform entrance, List<Transform> itinerary, MoveToTarget mt, NavMeshAgent nav, float dwell)
    {
        // NEW: bail out before doing any string work if logging is disabled - this early-return (not
        // just gating Debug.Log) is the part that actually matters for performance.
        if (!enableDebugLogs) return;

        // Try to include the unique AgentID in logs if present
        PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
        string agentId = pa != null ? pa.AgentID : "n/a";
        string goId = $"{go.name} AgentID={agentId}";
        string entranceName = entrance != null ? entrance.name : "null";
        string entranceInfo = $"Entrance[{entranceIndex}]={entranceName}";

        // Build itinerary description
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"[SpawnerManager][AgentSpawn] {goId} spawnInfo: {entranceInfo} dwell={dwell:F2}s liveAgents={liveAgents}");

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

                // Unknown transform (could be custom, or reused)
                sb.AppendLine($"  Itinerary[{i}] = UNKNOWN name='{t.name}'");
            }
        }

        // Print runtime nav and MoveToTarget info
        string navInfo = nav != null ? $"NavMeshAgent(enabled={nav.enabled}, isOnNavMesh={nav.isOnNavMesh}, speed={nav.speed:F2})" : "NavMeshAgent=null";
        // Replace useNavMesh with agentAssigned status for MoveToTarget
        string mtInfo = mt != null ? $"MoveToTarget(start={(mt.startTransform!=null?mt.startTransform.name:"null")}, target={(mt.targetTransform!=null?mt.targetTransform.name:"null")}, agentAssigned={(mt.agent!=null? "yes":"no")})" : "MoveToTarget=null";

        sb.AppendLine($"  Runtime: {navInfo} | {mtInfo} | prefab={agentPrefab.name}");

        // Output the composed log
        if (enableDebugLogs) Debug.Log(sb.ToString());
    }

    /// <summary>
    /// Transform-based itinerary builder (the active DIRECTED itinerary system - Part 3 only).
    /// The chosen 'entrance' transform is inserted as the first itinerary element (itinerary[0]).
    /// The rest of the logic (n attractors then exit) is preserved. NEVER called for undirected (Part 2)
    /// spawns, and never reads SpawnerManager.wanderingPoints.
    /// </summary>
    List<Transform> BuildItinerary(Transform entrance)
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

    public void OnAgentFinished(PedestrianAgent agent)
    {
        agent.onAgentFinished -= OnAgentFinished;

        liveAgents--;
        activeAgents.Remove(agent.gameObject); // NEW: stop tracking this agent for trial-state pause/despawn
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
                // previously code set mt.useNavMeshAgent; that field no longer exists.
                // The MoveToTarget component is expected to be NavMeshAgent-only; runtime behavior will reflect nav state.
            }

            // Log agent finishing and pooling info (include AgentID)
            string agentId = agent != null ? agent.AgentID : "n/a";
            string goId = $"{go.name} AgentID={agentId}";
            string navState = nav != null ? $"nav.enabled={nav.enabled} nav.isOnNavMesh={nav.isOnNavMesh}" : "no NavMeshAgent";
            if (enableDebugLogs) Debug.Log($"[SpawnerManager][OnAgentFinished] {goId} returning to pool. liveAgents={liveAgents} {navState}");

            go.SetActive(false);
            pool.Add(go);
        }
        else
        {
            Destroy(agent.gameObject);
        }
    }

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

    /// <summary>
    /// Improved sampling routine (kept from original script).
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
}
