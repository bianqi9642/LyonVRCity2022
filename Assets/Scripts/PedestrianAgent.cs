// PedestrianAgent.cs
// Runtime component that drives a pedestrian along a pre-built, Transform-based itinerary.
// Movement is delegated exclusively to MoveToTarget (the single NavMeshAgent controller); this script decides
// WHERE to go next and what to do on arrival.
//
// Both Parts now use a pre-generated itinerary that SpawnerManager builds ONCE per NPC life:
//
//   NpcMovementMode.Part3AttractorItinerary   Entrance -> attractors -> Exit
//     - Every waypoint is completed through MoveToTarget.OnArrived (single arrival path, 'arriveTolerance' radius).
//     - ONLY attractors start a dwell (WaitAtAttractor, or PollForCapacity first when the attractor is full);
//       attractor capacity is managed by SpawnerManager. The entrance (and any other non-attractor, non-exit
//       waypoint) is passed through with no dwell (PassThroughWaypoint), and the last entry (the exit) finishes
//       the NPC immediately - neither the entrance nor the exit ever dwells.
//
//   NpcMovementMode.Part2WanderingItinerary   Entrance -> wandering points -> Exit
//     - Index 0 (the entrance) is only the placement point: the NPC is relocated there instantly
//       (MoveToTarget.TeleportTo) - not walked - and immediately continues to the first wandering point.
//       (See 'Entrance placement' below for WHERE near the entrance.)
//     - Intermediate wandering points are NOT completed by an arrival event and have no dwell: as soon as the NPC
//       is within 'wanderLookAheadDistance' of the current stop, the next list entry is issued (before the
//       mover's own tighter arrival threshold would bring the NPC to a stop), so movement reads as continuous.
//     - Only the last entry (the exit) is completed through MoveToTarget.OnArrived, exactly like Part 3, and
//       finishes the NPC. No AttractorDwell / capacity logic is ever applied to wandering points.
//
// Entrance placement (both modes): when SpawnerManager.entranceSpawnRadius > 0 and itinerary[0] is a configured
// Entrance, the NPC is relocated to a NavMesh position chosen by SpawnerManager.TryFindEntranceSpawnPosition inside
// that radius (preferring clearance from live NPCs) instead of onto the exact Entrance coordinate. The placement
// stands in for 'reaching' the Entrance: the itinerary continues with the entry after it, and the Entrance itself
// (a route waypoint, never moved) is not walked to. This is done explicitly here in BOTH modes because the Part 3
// first life used to be snapped onto the exact Entrance by MoveToTarget.Start() as a side effect of
// MoveTo(Transform) recording the target as 'startTransform'; SyncMoverStartToCurrentPosition() now keeps that
// snap from happening. If no valid position is found the placement is retried a few times (bounded), then the NPC
// is finished (returned to the pool) - it is never stacked on the exact Entrance coordinate. With
// entranceSpawnRadius <= 0 (or when itinerary[0] is not an Entrance) the original behaviour is unchanged.
//
// Shared rules (both modes):
//   - Arrival is decided in ONE place per waypoint type: the mover's OnArrived event (Part 3 waypoints, Part 2
//     exit) or the look-ahead (Part 2 intermediate stops) - never both for the same leg. OnMoveToArrived()
//     ignores events while UncertainBehavior is detouring and suspends UncertainBehavior BEFORE a dwell begins.
//   - The authoritative main target is 'itineraryTransforms[currentIndex]'. UncertainBehavior Detours replace
//     MoveToTarget's destination temporarily but never change the itinerary or currentIndex; while a Detour is
//     active the Part 2 look-ahead is suspended (IsDetouring guard) and, when it ends, UncertainBehavior asks
//     ResumeWanderTargetAfterDetour() to re-issue the CURRENT itinerary target (Part 3 keeps UncertainBehavior's
//     original snapshot restore).
//   - The finite lifecycle is identical in both modes: Finish() -> onAgentFinished -> SpawnerManager returns the
//     NPC to the pool. ForceFinish() remains the external termination path.
//
// Part 3 only (additive, does NOT touch route generation):
//   * When the current waypoint is an Attractor Plane carrying an AttractorDwell component, the PHYSICAL
//     destination for that leg is an independent random point inside that Plane (vertically projected onto the
//     NavMesh). The itinerary list, waypoint Transforms and currentIndex progression are unchanged.
//   * While dwelling, the agent turns yaw-only to face AttractorDwell.facingTarget (if assigned).
//   * Random sampling happens in AttractorDwell via a private System.Random, so UnityEngine.Random is not perturbed.
//
// Notes:
// - Each instance gets a persistent unique ID assigned in Awake() (NPC-000001).
// - If MoveToTarget is not present, Part 3 falls back to a simple distance check and no movement is issued.

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// How an NPC walks its pre-generated itinerary. Selected per spawn by SpawnerManager from the active Part's
/// configuration (ExperimentManager.PartCaptureConfig.npcDirectedMovement: true = Part 3, false = Part 2).
/// </summary>
public enum NpcMovementMode
{
    /// <summary>Part 2: Entrance -> ordered wandering points -> Exit. Intermediate stops advance by look-ahead, no dwell.</summary>
    Part2WanderingItinerary,

    /// <summary>Part 3: Entrance -> attractors -> Exit. Waypoints complete on arrival; entrance/attractors dwell.</summary>
    Part3AttractorItinerary
}

[DisallowMultipleComponent]
public class PedestrianAgent : MonoBehaviour
{
    // Event invoked when this agent finishes its itinerary (or is forced finished).
    public Action<PedestrianAgent> onAgentFinished;

    // internal state machine
    // NOTE (data-capture, Revision 2): enum is public read-only so the capture layer can OBSERVE the
    // existing semantic state. The state machine itself is unchanged.
    public enum State { Moving, WaitingAtAttractor, Finished }

    // Core references
    MoveToTarget moveToTarget; // The single authority for movement in this modified script

    // Itinerary (Transform-based: entrance, intermediate stops, exit), built once per life by SpawnerManager.
    List<Transform> itineraryTransforms;

    int currentIndex = 0;
    State state = State.Moving;
    SpawnerManager manager;
    float dwellTime = 5f;

    // How this life walks its itinerary. Set by Initialize(); never changes mid-life.
    NpcMovementMode movementMode = NpcMovementMode.Part3AttractorItinerary;
    bool IsWanderingItinerary => movementMode == NpcMovementMode.Part2WanderingItinerary;

    // tuning
    [Tooltip("Canonical arrival radius (m) for itinerary waypoints that complete on arrival (every Part 3 waypoint " +
             "and the Part 2 exit). Handed to MoveToTarget as the per-leg arrival radius " +
             "(MoveToTarget.SetLegArriveRadius), so MoveToTarget's OnArrived event fires at this distance. Not used by " +
             "the Part 2 look-ahead (wanderLookAheadDistance) or by UncertainBehavior detours.")]
    public float arriveTolerance = 0.6f; // consider arrived if within this distance

    // -----------------------------------------------------------------------------------------------
    // Part 3: attractor stop override + dwell facing (additive; does NOT change route generation)
    // -----------------------------------------------------------------------------------------------
    // Physical movement destination for the CURRENT leg when that leg targets an Attractor Plane that
    // carries an AttractorDwell component. null for every other leg (entrance, exit, plain waypoint), in
    // which case all movement behaviour is exactly as before. The itinerary list itself is never modified
    // - this only overrides where the agent physically stops for this one Attractor visit.
    Vector3? currentLegStopOverride = null;

    // Handle to the running "turn to face the Attractor's Facing Target" coroutine, so a new dwell can
    // cancel a stale one on fast pool reuse.
    Coroutine facingCoroutine = null;

    // -----------------------------------------------------------------------------------------------
    // Part 2: wandering-itinerary state
    // -----------------------------------------------------------------------------------------------

    /// <summary>Distance (copied from SpawnerManager.wanderLookAheadDistance at Initialize time) at which the NPC
    /// advances to the NEXT itinerary entry, before MoveToTarget's own (tighter) arrival threshold would otherwise
    /// bring it to a full stop. Only used for intermediate Part 2 stops.</summary>
    float wanderLookAheadDistance = 1.5f;

    /// <summary>True once BeginWanderingItinerary() has placed the NPC at its entrance and issued the first leg.
    /// Until then (e.g. while waiting for the mover to become ready) the look-ahead and Detour restore are inert.</summary>
    bool wanderingStarted = false;

    /// <summary>Time.time before which AdvanceWanderingItinerary() will not run again. A defensive back-off applied
    /// when no reachable next entry was found, so NavMesh.CalculatePath is never invoked every single frame.</summary>
    float nextWanderRetryTime = -1f;

    /// <summary>Reused NavMeshPath buffer for the reachability validation, so repeated checks do not allocate.</summary>
    NavMeshPath wanderPathBuffer;

    /// <summary>Back-off after a failed next-entry selection (see nextWanderRetryTime). Not Inspector-exposed - an
    /// internal pacing safeguard, not a behavioural tuning parameter.</summary>
    const float k_WanderRetryBackoffSeconds = 0.5f;

    // -----------------------------------------------------------------------------------------------
    // Entrance placement retry state (both modes; see 'Entrance placement' in the file header)
    // -----------------------------------------------------------------------------------------------

    /// <summary>True while the NPC is waiting to retry a failed entrance placement (no valid NavMesh position was
    /// found around the Entrance). Update() drives the retries; no leg has been issued yet.</summary>
    bool awaitingEntrancePlacement = false;
    int entrancePlacementRetries = 0;
    float nextEntrancePlacementTime = 0f;
    const int k_MaxEntrancePlacementRetries = 8;
    const float k_EntrancePlacementRetrySeconds = 0.25f;

    // -----------------------------
    // Unique ID implementation
    // -----------------------------
    static long s_globalAgentCounter = 0;
    [Tooltip("Read-only unique ID assigned to this agent instance at Awake.")]
    [SerializeField] string agentID = null;
    public string AgentID => agentID;

    /// <summary>
    /// Assigns the stable pooled-object ID if it is still empty (idempotent). Awake() calls it; SpawnerManager also calls it when it
    /// creates a pool object so an instance that was created inactive still has its ID before its creation is recorded.
    /// </summary>
    public string EnsureAgentId()
    {
        if (string.IsNullOrEmpty(agentID))
        {
            long id = System.Threading.Interlocked.Increment(ref s_globalAgentCounter);
            agentID = $"NPC-{id:000000}";
        }
        return agentID;
    }

    // -----------------------------
    // Lifetime identity (data capture)
    // -----------------------------
    // AgentID identifies the pooled GAMEOBJECT and never changes. A LIFETIME is one active simulated life of that object: it begins
    // when SpawnerManager hands the object a new itinerary (Initialize) and ends when the itinerary finishes (Finish / ForceFinish).
    // Each lifetime gets "<AgentID>-S<n>", n counting this object's lifetimes (1, 2, 3 ...). Prewarming, pooling and pool
    // acquisition do not consume an index. The counter lives on the object that owns the ID, so a destroyed object never hands its IDs
    // to a replacement (the replacement has a new AgentID).
    int lifetimeCounter = 0;
    bool lifetimeOpen = false;
    string lifetimeId = "";
    double lifetimeSpawnTAbs = double.NaN;
    long lifetimeSpawnTUnixMs = 0;
    string finishReason = "itinerary_complete";
    bool movementStartedThisLife = false;
    int lastLegIndexEmitted = -1;

    /// <summary>"&lt;AgentID&gt;-S&lt;n&gt;" while a lifetime is open, otherwise "" (an ended lifetime never resolves again).</summary>
    public string LifetimeId => lifetimeOpen ? lifetimeId : "";
    /// <summary>The ID of the most recent lifetime, open or already ended (used to label its closing events).</summary>
    public string LastLifetimeId => lifetimeId;
    public bool HasOpenLifetime => lifetimeOpen;
    public int LifetimeIndex => lifetimeCounter;
    public double LifetimeSpawnTAbs => lifetimeSpawnTAbs;
    public long LifetimeSpawnTUnixMs => lifetimeSpawnTUnixMs;

    // Provenance of this pooled object / this lifetime, filled by SpawnerManager (observation only).
    public double ObjectCreatedTAbs { get; set; } = double.NaN;
    public long   ObjectCreatedTUnixMs { get; set; }
    public string ObjectCreatedKind { get; set; } = "";      // "prewarm" or "runtime_instantiate"
    public double PoolAcquiredTAbs { get; set; } = double.NaN;
    public string SpawnSource { get; set; } = "";            // "pool" or "new" for the current lifetime

    // Actual profile values applied to this lifetime by AgentFactory (observation only; see AgentFactory.ApplyProfileToInstance).
    public string ProfileName { get; private set; } = "";
    public float ProfileBaseSpeed { get; private set; }
    public float SpeedMultiplier { get; private set; } = 1f;
    public float ConfiguredSpeed { get; private set; }
    public bool UncertainBehaviorEnabled { get; private set; }

    public void SetProfileAttributes(string profileName, float baseSpeed, float multiplier, float configuredSpeed, bool uncertainEnabled)
    {
        ProfileName = profileName ?? "";
        ProfileBaseSpeed = baseSpeed;
        SpeedMultiplier = multiplier;
        ConfiguredSpeed = configuredSpeed;
        UncertainBehaviorEnabled = uncertainEnabled;
    }

    // What AgentFactory/AppearanceController applied most recently (set before Initialize()). Frozen into LifetimeProfile when a lifetime begins.
    AgentProfile appliedProfileRef;
    AppliedAppearance appliedAppearanceRef;
    NpcLifetimeProfile lifetimeProfile = NpcLifetimeProfile.None;

    public void SetAppliedProfile(AgentProfile profile, AppliedAppearance appearance)
    {
        appliedProfileRef = profile;
        appliedAppearanceRef = appearance;
    }

    /// <summary>
    /// Immutable record of the profile/appearance/uncertain configuration that was in force when the CURRENT (or most recent) lifetime began. A later
    /// re-skin or re-roll of this pooled object does not change an instance already handed out.
    /// </summary>
    public NpcLifetimeProfile LifetimeProfile => lifetimeProfile;

    // Uncertain behaviour as seen by the recorder (observation only).
    /// <summary>True when the prefab carries an UncertainBehavior component at all.</summary>
    public bool HasUncertainComponent => uncertainBehavior != null;
    /// <summary>True when the UncertainBehavior component is active and enabled right now (configured ON for this lifetime by AgentFactory).</summary>
    public bool UncertainActive => uncertainBehavior != null && uncertainBehavior.isActiveAndEnabled;
    public string UncertainStateLabel => uncertainBehavior != null ? uncertainBehavior.StateLabel : "";
    public string UncertainLookPhaseLabel => uncertainBehavior != null ? uncertainBehavior.LookPhaseLabel : "";

    void OnUncertainStateChanged(string from, string to, float plannedSeconds)
    {
        if (!lifetimeOpen) return;
        Emit(CaptureConstants.NpcEvtUncertainState,
            "from=" + from + ";to=" + to + ";planned_s=" + plannedSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    // Read-only view of the itinerary for the recorder.
    public NpcMovementMode MovementMode => movementMode;
    public float DwellSeconds => dwellTime;
    public float WanderLookAheadDistance => wanderLookAheadDistance;
    public int ItineraryCount => itineraryTransforms != null ? itineraryTransforms.Count : 0;
    public int CurrentItineraryIndex => currentIndex;
    public IReadOnlyList<Transform> ItineraryTransformsView => itineraryTransforms;
    public string CurrentTargetName
    {
        get
        {
            if (itineraryTransforms == null || currentIndex < 0 || currentIndex >= itineraryTransforms.Count) return "";
            Transform t = itineraryTransforms[currentIndex];
            return t != null ? t.name : "";
        }
    }

    /// <summary>
    /// Opens a new lifetime and stamps its spawn time. Idempotent within one lifetime: a repeated call while the lifetime is open
    /// returns false and changes nothing. Called by Initialize(), i.e. right after SpawnerManager activated the object and before it is
    /// registered in the live population, so the ID exists before any sampler or gaze ray can see this NPC.
    /// </summary>
    bool BeginLifetime()
    {
        if (lifetimeOpen) return false;
        EnsureAgentId();
        lifetimeCounter++;
        lifetimeId = agentID + "-S" + lifetimeCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lifetimeOpen = true;
        lifetimeSpawnTAbs = CaptureClock.TAbs;
        lifetimeSpawnTUnixMs = CaptureClock.TUnixMs;
        movementStartedThisLife = false;
        lastLegIndexEmitted = -1;
        finishReason = "itinerary_complete";
        // Freeze what was applied to this lifetime (profile content, realised appearance, uncertain parameters) now, at the spawn boundary.
        lifetimeProfile = AgentProfileSnapshot.Capture(appliedProfileRef, appliedAppearanceRef, UncertainBehaviorEnabled ? uncertainBehavior : null);
        return true;
    }

    /// <summary>Closes the current lifetime. The ID stays readable through LastLifetimeId but LifetimeId resolves to "" from now on.</summary>
    void EndLifetime() { lifetimeOpen = false; }

    /// <summary>Raises one lifecycle event stamped NOW. Observation only.</summary>
    void Emit(string type, string value = "", bool hasDest = false, Vector3 dest = default, Vector3? posOverride = null,
              double tAbsOverride = double.NaN, long tUnixOverride = 0)
    {
        NpcLifecycleBus.Raise(new NpcLifecycleEvent
        {
            type = type,
            tAbs = double.IsNaN(tAbsOverride) ? CaptureClock.TAbs : tAbsOverride,
            tUnixMs = tUnixOverride != 0 ? tUnixOverride : CaptureClock.TUnixMs,
            agent = this,
            agentId = agentID,
            lifetimeId = lifetimeId,
            lifetimeIndex = lifetimeCounter,
            pos = posOverride ?? transform.position,
            yaw = transform.eulerAngles.y,
            hasDest = hasDest,
            dest = dest,
            itineraryIndex = currentIndex,
            targetId = CurrentTargetName,
            value = value ?? ""
        });
    }

    // Raised when a leg towards itinerary[index] was really handed to MoveToTarget. The first one of a lifetime is also the movement start.
    void NoteLegIssued(int index, Transform target)
    {
        if (!lifetimeOpen) return;
        Vector3 dest = target != null ? target.position : transform.position;
        if (!movementStartedThisLife)
        {
            movementStartedThisLife = true;
            Emit(CaptureConstants.NpcEvtMovementStart, "mode=" + movementMode + ";itinerary_count=" + ItineraryCount, true, dest);
        }
        if (index != lastLegIndexEmitted)
        {
            lastLegIndexEmitted = index;
            Emit(CaptureConstants.NpcEvtTargetSet, "kind=itinerary_leg", true, dest);
        }
    }

    /// <summary>
    /// Read-only view of the existing internal state (data-capture, Revision 2). Observation only:
    /// this getter never changes NPC behaviour, itinerary, dwell timing, randomisation, or NavMesh state.
    /// </summary>
    public State CurrentState => state;

    // Keep track of whether we've subscribed to the current MoveToTarget.OnArrived
    bool subscribedToMoveToArrived = false;

    // Cached reference to UncertainBehavior (if present) so we can notify it to resume after dwell or on reuse
    UncertainBehavior uncertainBehavior = null;

    // Cached delegate for UncertainBehavior.SetMainTargetRestorer(), created once in Awake() so registering it on
    // every Initialize() does not allocate. Only registered in Part 2 mode.
    Func<bool> detourRestorer = null;

    // NEW (fix for pooled-agent reactivation race): tracks the coroutine started by BeginMovementWhenReady()
    // so a second call (e.g. rapid re-Initialize on quick pool reuse) can safely cancel the previous wait.
    Coroutine startMovementCoroutine = null;

    [Tooltip("NEW: maximum time (seconds, unscaled) to wait for MoveToTarget.IsReadyToMove() to report true " +
             "before issuing the first movement command anyway. This bounds how long a spawn can be " +
             "delayed if the mover never becomes ready (e.g. genuinely off NavMesh), so an agent is never " +
             "blocked forever - MoveToTarget's own defensive logging still applies if it truly cannot move.")]
    public float moveReadyPollTimeout = 3f;

    void Awake()
    {
        // Assign unique ID if not already assigned (prewarm / pool instantiation will call Awake once)
        EnsureAgentId();

        // Append ID to GameObject name for easier identification in Hierarchy and Debug logs.
        if (!gameObject.name.Contains(agentID))
        {
            gameObject.name = $"{gameObject.name} [{agentID}]";
        }

        // Cache MoveToTarget reference (may be null; script tolerates that but movement requires it)
        moveToTarget = GetComponent<MoveToTarget>();
        if (moveToTarget == null)
        {
            Debug.LogWarning($"[PedestrianAgent][{agentID}] MoveToTarget component not found on GameObject. This agent will not be able to move itself and will rely on external movement logic.");
        }
        else
        {
            // If MoveToTarget has a way to accept AgentID for logging, set it (best-effort).
            try { moveToTarget.SetAgentID(agentID); } catch (Exception) { /* ignore */ }
        }

        // Cache UncertainBehavior if present. We'll call ResumeAfterArrival() when movement is resumed
        // or when the agent is reinitialized (pooled reuse).
        uncertainBehavior = GetComponent<UncertainBehavior>();
        if (uncertainBehavior != null) uncertainBehavior.StateChanged += OnUncertainStateChanged;   // data capture: state transitions -> npc_events
        detourRestorer = ResumeWanderTargetAfterDetour;

        // Part 2: allocate once, reused across every reachability check for the lifetime of this instance
        // (including pooled reuse) to avoid per-call allocation.
        wanderPathBuffer = new NavMeshPath();
    }

    // ===============================================================================================
    // Initialisation
    // ===============================================================================================

    /// <summary>
    /// Part 3 initialisation (existing signature, kept for compatibility): walks 'itinerary' as an
    /// Entrance -> attractors -> Exit route. Equivalent to Initialize(itinerary, mgr, Part3AttractorItinerary, ...).
    /// </summary>
    public void Initialize(List<Transform> itinerary, SpawnerManager mgr, float dwellSeconds)
    {
        Initialize(itinerary, mgr, NpcMovementMode.Part3AttractorItinerary, dwellSeconds, 0f);
    }

    /// <summary>
    /// Called by SpawnerManager when spawning / reusing an agent. Hands over the itinerary that was generated ONCE
    /// for this life and the mode that decides how it is walked (see the file header). Movement is handled
    /// exclusively via MoveToTarget.
    /// </summary>
    /// <param name="itinerary">Entrance (index 0), intermediate stops, exit (last index).</param>
    /// <param name="mgr">The owning SpawnerManager (attractor occupancy, pooling).</param>
    /// <param name="mode">Part 2 wandering itinerary or Part 3 attractor itinerary.</param>
    /// <param name="dwellSeconds">Dwell at the entrance/attractors (Part 3 only; ignored in Part 2).</param>
    /// <param name="lookAheadDistance">Part 2 only: distance at which the next itinerary entry is issued.</param>
    public void Initialize(List<Transform> itinerary, SpawnerManager mgr, NpcMovementMode mode,
                           float dwellSeconds, float lookAheadDistance)
    {
        if (itinerary == null) itinerary = new List<Transform>();

        // New lifetime (idempotent while one is open) - before anything else can observe this NPC as part of the live population.
        bool newLifetime = BeginLifetime();

        this.movementMode = mode;
        this.itineraryTransforms = new List<Transform>(itinerary);

        this.manager = mgr;
        this.dwellTime = dwellSeconds;
        this.currentIndex = 0;
        this.state = State.Moving;

        // Pooling hygiene: a pooled instance may hold per-leg state from its previous life (possibly in the other
        // mode). Clear it so the first leg starts clean.
        this.currentLegStopOverride = null;
        this.wanderingStarted = false;
        this.nextWanderRetryTime = -1f;
        this.awaitingEntrancePlacement = false;
        this.entrancePlacementRetries = 0;
        if (IsWanderingItinerary)
            this.wanderLookAheadDistance = Mathf.Max(0.01f, lookAheadDistance);

        // Configure MoveToTarget runtime parameters if available
        if (moveToTarget != null)
        {
            // Use MoveToTarget as the single movement system.
            moveToTarget.startTransform = null;
            moveToTarget.startPosition = transform.position;
            // attempt to keep MoveToTarget speed consistent with its own configured speed (no NavMeshAgent present here)
            try { moveToTarget.SetMoveSpeed(moveToTarget.speed); } catch (Exception) { /* ignore if missing */ }
        }

        // Stage 4: fully reset UncertainBehavior's per-life runtime state (state machine, saved detour
        // target, retry count, suspend flag, event subscriptions) so a reused pooled instance starts this
        // life clean. (ResumeAfterArrival() remains used for the lighter-weight per-dwell resume in the
        // dwell coroutines below - this call site needs the full reset instead.)
        if (uncertainBehavior != null)
        {
            uncertainBehavior.ResetForReuse();

            // Part 2: the authoritative main target is itinerary[currentIndex] (not a snapshot taken at Detour
            // entry), so a Detour that ends must resume towards it - see ResumeWanderTargetAfterDetour().
            // Part 3 keeps UncertainBehavior's original snapshot-based restoration (and clears any restorer left
            // over from a previous Part 2 life of this pooled instance).
            uncertainBehavior.SetMainTargetRestorer(IsWanderingItinerary ? detourRestorer : null);
        }

        // Data capture: the spawn boundary. Raised HERE - itinerary, mode and dwell are set, no movement command has been issued yet - because
        // BeginMovementWhenReady() below can already run the first movement step synchronously (the coroutine only yields while the mover is not
        // ready), so a later event from SpawnerManager could arrive after the first movement events. Stamped with the lifetime's own begin time.
        if (newLifetime)
            Emit(CaptureConstants.NpcEvtSpawn, "source=" + SpawnSource, false, default, null, lifetimeSpawnTAbs, lifetimeSpawnTUnixMs);

        // NEW (fix for pooled-agent reactivation race): rather than issuing the first leg synchronously here
        // (which could race with MoveToTarget's own pooled-reuse re-warp onto the NavMesh, see
        // MoveToTarget.IsReadyToMove() docs), defer the first movement command until the mover reports
        // it is actually ready, or a bounded timeout elapses.
        BeginMovementWhenReady();
    }

    void Update()
    {
        // Only perform leg progression while moving
        if (state != State.Moving)
            return;

        // Waiting to retry a failed entrance placement: nothing else happens until the NPC has been placed.
        if (awaitingEntrancePlacement)
        {
            RetryEntrancePlacement();
            return;
        }

        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count) return;

        // Part 2 has its own, completely separate progression path (look-ahead for intermediate stops). It never
        // falls through into the Part 3 attractor logic below.
        if (IsWanderingItinerary)
        {
            UpdateWanderingItinerary();
            return;
        }

        // ---------------- Part 3 attractor itinerary ----------------

        Transform t = itineraryTransforms[currentIndex];
        if (t == null)
        {
            OnReachedWaypoint();
            return;
        }

        // With a MoveToTarget, arrival is event-driven only (OnMoveToArrived). Polling the distance here too
        // would be a second, independent arrival decision that could start a dwell (PauseExternal) before the
        // mover emitted - and UncertainBehavior received - its arrival notification.
        if (moveToTarget != null)
            return;

        // No MoveToTarget on this prefab: nothing can emit an arrival event, so keep the distance check.
        // When a per-leg physical stop override is active (a random point inside an Attractor Plane), measure
        // to THAT point, not to the Plane pivot 't.position'. For every other leg it is null and unchanged.
        Vector3 transformFallbackTarget = currentLegStopOverride ?? t.position;
        if (Vector3.Distance(transform.position, transformFallbackTarget) <= arriveTolerance)
            OnReachedWaypoint();
    }

    // ===============================================================================================
    // Part 2: wandering itinerary
    // ===============================================================================================

    /// <summary>
    /// Per-frame Part 2 progression for INTERMEDIATE stops (the caller already checked state and bounds).
    /// The last entry (the exit) is never advanced here - it completes through MoveToTarget.OnArrived - and no
    /// stop is ever drawn at random: progression only moves to the next entry of the pre-generated itinerary.
    /// </summary>
    void UpdateWanderingItinerary()
    {
        // Not placed / first leg not issued yet (e.g. still waiting for the mover to become ready).
        if (!wanderingStarted)
            return;

        // The final leg (exit) is completed by the arrival event, not by the look-ahead.
        if (currentIndex >= itineraryTransforms.Count - 1)
            return;

        // While an UncertainBehavior Detour is in progress, MoveToTarget's destination belongs to the temporary
        // detour point. Do not issue the next entry (which would call MoveTo() and overwrite it); the itinerary
        // index stays untouched so itinerary[currentIndex] remains the authoritative main target. When the Detour
        // ends, UncertainBehavior calls ResumeWanderTargetAfterDetour() and progression resumes from here.
        if (uncertainBehavior != null && uncertainBehavior.IsDetouring)
            return;

        // Back-off gate: prevents NavMesh.CalculatePath from being invoked every frame after a failed advance.
        if (Time.time < nextWanderRetryTime)
            return;

        Transform stop = itineraryTransforms[currentIndex];
        if (stop == null || Vector3.Distance(transform.position, stop.position) <= wanderLookAheadDistance)
        {
            // Close enough to move on NOW, before MoveToTarget's own (tighter) arrival threshold would otherwise
            // bring the NavMeshAgent to a full stop. A destroyed stop is skipped the same way.
            AdvanceWanderingItinerary();
        }
    }

    /// <summary>
    /// Part 2: first movement command of a life, issued once MoveToTarget is ready. Index 0 is the entrance the
    /// route was generated from: the NPC is relocated there instantly (TeleportTo sets the transform AND warps
    /// the NavMeshAgent, so no transit is visible), then immediately continues to the first wandering point. No
    /// dwell happens at the entrance. This is the existing Part 2 spawn behaviour, kept deliberately.
    /// </summary>
    void BeginWanderingItinerary()
    {
        if (itineraryTransforms == null || itineraryTransforms.Count < 2)
        {
            // Nothing to walk (SpawnerManager never generates this) - end the life instead of idling forever.
            finishReason = "no_itinerary";
            Finish();
            return;
        }

        Transform entrance = itineraryTransforms[0];
        if (UsesDistributedEntrancePlacement(entrance))
        {
            if (!PlaceNearEntrance(entrance))
            {
                ScheduleEntrancePlacementRetry();
                return;
            }
        }
        else if (moveToTarget != null && entrance != null)
        {
            // Original exact placement: distribution disabled (entranceSpawnRadius <= 0) or itinerary[0] is not an Entrance.
            Vector3 before = transform.position;
            moveToTarget.TeleportTo(entrance.position);
            SyncMoverStartToCurrentPosition();
            Emit(CaptureConstants.NpcEvtEntranceTeleport, "entrance=" + entrance.name, true, transform.position, before);
        }

        awaitingEntrancePlacement = false;
        wanderingStarted = true;
        AdvanceWanderingItinerary(); // currentIndex 0 (entrance) -> first reachable stop
    }

    /// <summary>
    /// Part 2: moves currentIndex to the next usable entry AFTER the current one and issues that leg.
    /// Reuses the original path validation: an entry is only issued if NavMesh.CalculatePath from the NPC's current
    /// position returns PathComplete. A destroyed or unreachable INTERMEDIATE stop is skipped (bounded: each entry
    /// is tried at most once). If the exit itself is destroyed the NPC finishes; if it is unreachable the NPC keeps
    /// its current target and retries after k_WanderRetryBackoffSeconds - never every frame.
    /// </summary>
    void AdvanceWanderingItinerary()
    {
        int last = itineraryTransforms.Count - 1;

        for (int j = currentIndex + 1; j <= last; j++)
        {
            Transform candidate = itineraryTransforms[j];

            if (candidate == null)
            {
                if (j == last) { Finish(); return; } // exit destroyed: nothing left to walk to
                continue;
            }

            bool reachable = NavMesh.CalculatePath(transform.position, candidate.position, NavMesh.AllAreas, wanderPathBuffer)
                             && wanderPathBuffer.status == NavMeshPathStatus.PathComplete;

            if (!reachable)
            {
                if (j == last) break; // the exit is mandatory: back off and retry
                continue;             // skip an unreachable intermediate stop
            }

            currentIndex = j;
            IssueWanderingLeg(j);
            return;
        }

        // No usable next entry this attempt (e.g. the agent is in an isolated pocket, or points went stale after a
        // NavMesh rebake). Back off briefly and let UpdateWanderingItinerary() retry - never a tight loop.
        Debug.LogWarning($"[PedestrianAgent][{agentID}] No reachable next itinerary entry after index {currentIndex} " +
            $"(of {itineraryTransforms.Count}); retrying in {k_WanderRetryBackoffSeconds:F1}s. If this persists, " +
            "check that SpawnerManager.wanderingPoints / exits are placed on/near the walkable NavMesh and reachable " +
            "from this agent's current position.");
        nextWanderRetryTime = Time.time + k_WanderRetryBackoffSeconds;
    }

    /// <summary>
    /// Part 2: hands itinerary[index] to MoveToTarget. Intermediate stops use the same MoveTo(Transform) overload as
    /// before and nothing else (no arrival subscription, no resume - the look-ahead moves the NPC on). The exit leg
    /// is completed through the mover's single arrival event exactly like a Part 3 waypoint: subscribe BEFORE
    /// MoveTo, make arrival detection live (ResumeExternal), and set the canonical per-leg radius AFTER MoveTo
    /// (MoveTo resets it). Also used to re-issue the current target after a Detour.
    /// </summary>
    void IssueWanderingLeg(int index)
    {
        if (moveToTarget == null) return;

        Transform target = itineraryTransforms[index];
        bool isExit = (index == itineraryTransforms.Count - 1);

        if (isExit)
        {
            SubscribeToMoveToArrived();
            moveToTarget.ResumeExternal();
        }

        moveToTarget.MoveTo(target);

        if (isExit)
            moveToTarget.SetLegArriveRadius(arriveTolerance);

        NoteLegIssued(index, target);

        // MoveTo(Transform) records the previous target as startTransform; a first-life MoveToTarget.Start() would
        // otherwise move the NPC onto it. Keep the mover's start == where the NPC actually is.
        SyncMoverStartToCurrentPosition();

        // Harmless no-op if UncertainBehavior was never suspended, but guarantees it cannot get stuck dormant if
        // MoveToTarget.OnArrived ever fires for an intermediate stop despite the look-ahead pre-emption.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();
    }

    /// <summary>
    /// Keeps MoveToTarget's start fields equal to the NPC's current position, so the one-time first-life
    /// MoveToTarget.Start() initialisation can never relocate the NPC to a stale start point or a waypoint.
    /// </summary>
    void SyncMoverStartToCurrentPosition()
    {
        if (moveToTarget == null) return;
        moveToTarget.startTransform = null;
        moveToTarget.startPosition = transform.position;
    }

    // -----------------------------------------------------------------------------------------------
    // Entrance placement (both modes)
    // -----------------------------------------------------------------------------------------------

    /// <summary>True if itinerary[0] ('first') should be replaced by a distributed position around it: distribution is
    /// enabled in SpawnerManager and 'first' really is one of its configured Entrances (not the StartPoint fallback
    /// or an attractor).</summary>
    bool UsesDistributedEntrancePlacement(Transform first)
    {
        return moveToTarget != null && first != null && manager != null
            && manager.EntranceSpawnDistributionEnabled && manager.IsEntrance(first);
    }

    /// <summary>
    /// Relocates the NPC to a distributed NavMesh position near 'entrance' (TeleportTo = transform + NavMeshAgent.Warp
    /// in one call, so no stale path or velocity survives) and keeps the mover's start fields equal to that position.
    /// Returns false if SpawnerManager found no valid position; nothing has been moved in that case.
    /// </summary>
    bool PlaceNearEntrance(Transform entrance)
    {
        Vector3 position;
        if (!manager.TryFindEntranceSpawnPosition(entrance, gameObject, out position))
            return false;

        Vector3 before = transform.position;
        moveToTarget.TeleportTo(position);
        SyncMoverStartToCurrentPosition();
        Emit(CaptureConstants.NpcEvtEntranceTeleport, "entrance=" + (entrance != null ? entrance.name : ""), true, transform.position, before);
        return true;
    }

    void ScheduleEntrancePlacementRetry()
    {
        awaitingEntrancePlacement = true;
        nextEntrancePlacementTime = Time.time + k_EntrancePlacementRetrySeconds;
    }

    // Driven from Update() while awaitingEntrancePlacement. Bounded: after k_MaxEntrancePlacementRetries failed
    // retries the NPC is finished (returned to the pool) rather than stacked on the exact Entrance coordinate. This runs
    // from Update(), i.e. after SpawnerManager has registered the NPC, so the pool return is always seen.
    void RetryEntrancePlacement()
    {
        if (Time.time < nextEntrancePlacementTime)
            return;

        if (++entrancePlacementRetries > k_MaxEntrancePlacementRetries)
        {
            awaitingEntrancePlacement = false;
            Debug.LogWarning($"[PedestrianAgent][{agentID}] No valid spawn position around its Entrance after " +
                $"{k_MaxEntrancePlacementRetries} retries; skipping this NPC (returned to the pool).");
            finishReason = "entrance_placement_failed";
            Finish();
            return;
        }

        awaitingEntrancePlacement = false; // the Begin* method re-arms it if placement fails again
        StartItinerary();
    }

    /// <summary>
    /// Part 3: first movement command of a life. With distributed entrance placement the NPC is placed near its
    /// Entrance and the placement stands in for reaching it, so the itinerary continues with the entry after the
    /// Entrance (no leg back to the exact Entrance coordinate). Otherwise (distribution disabled, or itinerary[0] is
    /// not an Entrance) the original behaviour is kept: MoveToCurrent() towards itinerary[0].
    /// </summary>
    void BeginAttractorItinerary()
    {
        Transform first = (itineraryTransforms != null && itineraryTransforms.Count >= 2) ? itineraryTransforms[0] : null;

        if (UsesDistributedEntrancePlacement(first))
        {
            if (!PlaceNearEntrance(first))
            {
                ScheduleEntrancePlacementRetry();
                return;
            }

            awaitingEntrancePlacement = false;
            AdvanceItinerary();                 // currentIndex 0 (Entrance) -> 1, then MoveToCurrent()
            SyncMoverStartToCurrentPosition();  // MoveTo(Transform) records the new target as startTransform: stop
                                                // a first-life MoveToTarget.Start() from snapping onto it
            return;
        }

        MoveToCurrent();
    }

    /// <summary>
    /// Registered with UncertainBehavior (Part 2 only) and invoked from UncertainBehavior.ExitCurrentState() when a
    /// Detour ends. Resumes movement towards the CURRENT itinerary target - itinerary[currentIndex] - instead of the
    /// position snapshot UncertainBehavior took when the Detour began. Returns true if this agent handled the
    /// restore, false to let UncertainBehavior fall back to its original snapshot restore (Part 3 / no mover).
    ///   - Not started yet: nothing to restore (BeginWanderingItinerary() has not issued a leg).
    ///   - Current entry valid: re-issue it (clears the stopped state, re-targets the Transform; for the exit leg
    ///     also re-arms the arrival radius). A Detour never changes currentIndex, so this is always the right leg.
    ///   - Current entry destroyed: advance to the next usable entry (validated, with back-off).
    /// If the NPC is already inside wanderLookAheadDistance of the stop, the next Update() simply moves on as usual.
    /// </summary>
    bool ResumeWanderTargetAfterDetour()
    {
        if (!IsWanderingItinerary || moveToTarget == null)
            return false;

        if (!wanderingStarted || itineraryTransforms == null || currentIndex >= itineraryTransforms.Count)
            return true;

        if (itineraryTransforms[currentIndex] != null)
        {
            IssueWanderingLeg(currentIndex);
            return true;
        }

        AdvanceWanderingItinerary();
        return true;
    }

    // ===============================================================================================
    // Start-up and movement commands
    // ===============================================================================================

    /// <summary>
    /// NEW (fix for pooled-agent reactivation race): starts (or restarts) the coroutine that waits for
    /// MoveToTarget to report readiness before issuing the first movement command for this itinerary.
    /// Cancels any previous pending wait first, in case Initialize() is called again
    /// in quick succession (e.g. very fast pool reuse) before the previous wait finished.
    /// </summary>
    void BeginMovementWhenReady()
    {
        if (startMovementCoroutine != null)
        {
            StopCoroutine(startMovementCoroutine);
            startMovementCoroutine = null;
        }

        if (!gameObject.activeInHierarchy)
        {
            // Cannot start a coroutine on an inactive GameObject. This is not an error: SpawnerManager
            // always calls go.SetActive(true) before Initialize(), so this should
            // not normally happen, but if it does, the first leg will simply run the next time this
            // agent is (re)activated and re-initialized.
            return;
        }

        startMovementCoroutine = StartCoroutine(WaitUntilMoverReadyThenMove());
    }

    /// <summary>
    /// NEW (fix for pooled-agent reactivation race).
    ///
    /// Problem this solves: MoveToTarget.OnEnable() -> ReinitializeAfterEnable() re-warps a pooled agent's
    /// NavMeshAgent onto the NavMesh, but that re-warp is not guaranteed to have completed by the time
    /// SpawnerManager.SpawnAgent() calls PedestrianAgent.Initialize() in the same
    /// frame as go.SetActive(true). Previously, MoveTo(...) was invoked synchronously right there, so it could run
    /// before MoveToTarget.IsReadyToMove() was true; MoveTo(...) would then silently skip calling
    /// agent.SetDestination(...) (only logging a warning), leaving the agent internally marked as 'moving' but with
    /// no actual NavMesh destination - an intermittently "frozen" NPC that is more likely to surface under load
    /// (many simultaneous pool reuses) and on Quest, where coroutine scheduling latency is more variable than in
    /// the Editor.
    ///
    /// Fix: poll MoveToTarget.IsReadyToMove() once per frame and only issue the first movement command once
    /// the mover reports it can actually accept a destination, or once 'moveReadyPollTimeout' seconds have
    /// elapsed (whichever comes first). The timeout ensures an agent is never blocked forever waiting - if
    /// the mover genuinely cannot get onto the NavMesh, MoveToTarget's own existing defensive
    /// logging/disable logic still applies exactly as before.
    /// </summary>
    IEnumerator WaitUntilMoverReadyThenMove()
    {
        if (moveToTarget == null)
        {
            // No MoveToTarget on this prefab: nothing to wait for. Preserve the original fallback
            // behaviour described in Awake() (agent relies on external movement logic).
            StartItinerary();
            startMovementCoroutine = null;
            yield break;
        }

        float waitStartTime = Time.unscaledTime;

        while (!moveToTarget.IsReadyToMove())
        {
            if (Time.unscaledTime - waitStartTime >= moveReadyPollTimeout)
            {
                Debug.LogWarning($"[PedestrianAgent][{agentID}] MoveToTarget was not ready after " +
                    $"{moveReadyPollTimeout:F1}s (NavMeshAgent not enabled / not on NavMesh yet). " +
                    "Proceeding anyway; MoveToTarget will log its own warning if the " +
                    "command cannot be applied.");
                break;
            }
            yield return null;
        }

        StartItinerary();

        startMovementCoroutine = null;
    }

    /// <summary>Issues the first movement command for this life, according to the movement mode.</summary>
    void StartItinerary()
    {
        if (IsWanderingItinerary)
            BeginWanderingItinerary();
        else
            BeginAttractorItinerary();
    }

    /// <summary>
    /// MoveToCurrent: instruct MoveToTarget to move to the current waypoint.
    /// This method exclusively calls MoveToTarget.MoveTo(...).
    /// We subscribe to MoveToTarget.OnArrived to get immediate notification when the leg completes.
    /// PART 3 ONLY - never called in Part 2 (see IssueWanderingLeg()).
    /// </summary>
    void MoveToCurrent()
    {
        // Unsubscribe previous subscription if any (defensive).
        UnsubscribeFromMoveToArrived();

        // Clear any per-leg physical-destination override left over from the previous waypoint. It is
        // re-set below ONLY when the new waypoint is an Attractor Plane with an AttractorDwell component.
        currentLegStopOverride = null;

        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count)
        {
            Finish();
            return;
        }

        Transform target = itineraryTransforms[currentIndex];
        if (target == null)
        {
            AdvanceItinerary();
            return;
        }

        if (moveToTarget != null)
        {
            moveToTarget.startTransform = null;
            moveToTarget.startPosition = transform.position;

            // Make MoveToTarget's arrival detection live for this leg. The prefab serialises
            // externallyPaused = true, and a stale pause/halt/freeze flag could also survive from a previous
            // life of a pooled instance; either would suppress OnArrived, the only arrival path for these
            // waypoints. Dwell completion already calls ResumeExternal() immediately before AdvanceItinerary(),
            // so this changes nothing for later legs.
            moveToTarget.ResumeExternal();

            // ROUTE-PRESERVING STOP OVERRIDE.
            // 'target' remains the itinerary waypoint (the Attractor Plane Transform) and is still the
            // value used by OnReachedWaypoint() / FindAttractorInfo() / the exit test / AdvanceItinerary().
            // Only the PHYSICAL destination handed to MoveToTarget is swapped for an independent random
            // point inside that same Plane, so NPCs dwelling at one Attractor spread out instead of all
            // stacking on Plane.transform.position. For entrances, exits, or Planes without an
            // AttractorDwell component this falls through to the original code path unchanged.
            Vector3 attractorStop;
            if (TryGetAttractorDwellStop(target, out attractorStop))
            {
                currentLegStopOverride = attractorStop;

                // Vector3 target: MoveToTarget reads targetPosition (targetTransform must be null).
                moveToTarget.targetTransform = null;
                moveToTarget.targetPosition = attractorStop;

                // Subscribe to OnArrived for immediate reaction
                SubscribeToMoveToArrived();

                // Existing public overload - MoveToTarget's movement implementation is NOT modified.
                moveToTarget.MoveTo(attractorStop);
                moveToTarget.SetLegArriveRadius(arriveTolerance); // after MoveTo(), which resets it
                NoteLegIssued(currentIndex, target);
            }
            else
            {
                // Original behaviour: move to the waypoint Transform itself.
                moveToTarget.targetTransform = target;

                // Subscribe to OnArrived for immediate reaction
                SubscribeToMoveToArrived();

                // MoveToTarget will handle movement and arrival
                moveToTarget.MoveTo(target);
                moveToTarget.SetLegArriveRadius(arriveTolerance); // after MoveTo(), which resets it
                NoteLegIssued(currentIndex, target);
            }
        }
        else
        {
            // No MoveToTarget present - rely on external movement and periodic distance checks.
        }
    }

    // ===============================================================================================
    // Arrival handling
    // ===============================================================================================

    /// <summary>
    /// Subscribe to MoveToTarget.OnArrived if available and not already subscribed.
    /// The subscribed callback triggers OnReachedWaypoint() (Part 3 waypoints, Part 2 exit).
    /// </summary>
    void SubscribeToMoveToArrived()
    {
        if (moveToTarget == null) return;
        if (subscribedToMoveToArrived) return;
        moveToTarget.OnArrived += OnMoveToArrived;
        subscribedToMoveToArrived = true;
    }

    /// <summary>
    /// Unsubscribe from MoveToTarget.OnArrived (defensive; safe to call even if not subscribed).
    /// </summary>
    void UnsubscribeFromMoveToArrived()
    {
        if (moveToTarget == null) { subscribedToMoveToArrived = false; return; }
        if (!subscribedToMoveToArrived) return;
        try
        {
            moveToTarget.OnArrived -= OnMoveToArrived;
        }
        catch (Exception) { /* ignore */ }
        subscribedToMoveToArrived = false;
    }

    /// <summary>
    /// Local adapter invoked when MoveToTarget signals arrival.
    /// We call OnReachedWaypoint() to run the existing attractor/dwell/finish logic.
    /// </summary>
    void OnMoveToArrived()
    {
        // MoveTo(Vector3) is also used by an UncertainBehavior Detour, and MoveToTarget fires OnArrived for ANY
        // target it reaches. Arrival at the temporary detour point is not arrival at the itinerary waypoint:
        // ignore it (stay subscribed for the real arrival; UncertainBehavior handles its own detour arrival).
        if (uncertainBehavior != null && uncertainBehavior.IsDetouring)
            return;

        // Process each leg's arrival once: only while still travelling (not already dwelling / finished).
        if (state != State.Moving)
            return;

        // Unsubscribe immediately — the next leg will re-subscribe in MoveToCurrent().
        UnsubscribeFromMoveToArrived();

        // Make the order explicit: suspend UncertainBehavior BEFORE the dwell pauses the mover, instead of
        // relying on which OnArrived subscriber happens to run first. Idempotent if its own handler already ran.
        if (uncertainBehavior != null)
            uncertainBehavior.NotifyMainTargetArrived();

        // The same dwell / attractor / finish logic as before, now reached through a single arrival path.
        OnReachedWaypoint();
    }

    /// <summary>
    /// Called when a waypoint that completes on arrival is reached.
    /// Part 3: the exit (last entry) finishes the NPC; an attractor starts its capacity check / dwell; any other
    /// waypoint (the entrance) is passed through without dwelling.
    /// Part 2: only the exit subscribes to OnArrived, so this just finishes the life.
    /// </summary>
    void OnReachedWaypoint()
    {
        // Defensive unsubscribe to ensure no stale subscriptions remain
        UnsubscribeFromMoveToArrived();

        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count) return;
        Transform target = itineraryTransforms[currentIndex];
        bool isExit = (currentIndex == itineraryTransforms.Count - 1);

        if (lifetimeOpen)
        {
            string kind = isExit ? "exit" : (manager != null && manager.FindAttractorInfo(target) != null ? "attractor" : "waypoint");
            Emit(CaptureConstants.NpcEvtArrived, "kind=" + kind + ";index=" + currentIndex, false, default);
        }

        // Part 2: wandering points never dwell and are never attractors; the exit ends the life like Part 3's.
        if (IsWanderingItinerary)
        {
            if (isExit)
                Finish();
            return;
        }

        if (!isExit)
        {
            AttractorInfo info = manager != null ? manager.FindAttractorInfo(target) : null;
            if (info != null)
            {
                bool accepted = manager.TryEnterAttractor(info, this);
                if (accepted)
                {
                    StartCoroutine(WaitAtAttractor(info));
                    return;
                }
                else
                {
                    StartCoroutine(PollForCapacity(info));
                    return;
                }
            }
            else
            {
                // Entrance (or any other waypoint that is neither an attractor nor the exit): no dwell, no timer, no
                // capacity check - go straight on to the next itinerary entry.
                PassThroughWaypoint();
                return;
            }
        }
        else
        {
            Finish();
        }
    }

    /// <summary>
    /// Part 3: continue immediately from a waypoint that does not dwell (the entrance). The arrival event was
    /// already processed exactly once (OnMoveToArrived unsubscribed first). UncertainBehavior was suspended by
    /// OnMoveToArrived, and nothing will hold this NPC at the waypoint, so re-arm it here - the same call the dwell
    /// coroutines make when they release the NPC. AdvanceItinerary() then issues the next leg through
    /// MoveToCurrent(), which also makes the mover's arrival detection live and re-subscribes for that leg.
    /// </summary>
    void PassThroughWaypoint()
    {
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        AdvanceItinerary();
    }

    // ===============================================================================================
    // Part 3: attractor dwell coroutines and itinerary progression
    // ===============================================================================================

    IEnumerator WaitAtAttractor(AttractorInfo info)
    {
        state = State.WaitingAtAttractor;

        // Pause and freeze animator while waiting at attractor
        if (moveToTarget != null)
            moveToTarget.PauseExternal(haltMovement: true, freezeAnimator: true);

        // Additive: yaw-only turn toward this Attractor's configured Facing Target (if any) for the dwell.
        // No-op when the Plane has no AttractorDwell or no facingTarget (existing rotation is preserved).
        FaceDwellTargetIfConfigured(info);

        if (lifetimeOpen) Emit(CaptureConstants.NpcEvtDwellStart, "dwell_s=" + dwellTime.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

        yield return new WaitForSeconds(dwellTime);

        if (lifetimeOpen) Emit(CaptureConstants.NpcEvtDwellEnd);

        // release slot in attractor
        if (manager != null && info != null)
            manager.ReleaseAttractor(info, this);

        // Resume movement
        if (moveToTarget != null)
            moveToTarget.ResumeExternal();

        // Notify UncertainBehavior (if present) that movement has resumed.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        AdvanceItinerary();
    }

    IEnumerator PollForCapacity(AttractorInfo info)
    {
        state = State.WaitingAtAttractor;

        // Pause movement and freeze animator while polling for capacity (don't step in place)
        if (moveToTarget != null)
            moveToTarget.PauseExternal(haltMovement: true, freezeAnimator: true);

        // Additive: the agent is already standing at its random stop point, so face the target while it waits.
        FaceDwellTargetIfConfigured(info);

        if (lifetimeOpen) Emit(CaptureConstants.NpcEvtDwellStart, "capacity_wait=1");

        float timeout = 30f;
        float start = Time.time;
        while (Time.time - start < timeout)
        {
            if (manager != null && manager.TryEnterAttractor(info, this))
            {
                // once admitted, dwell and then release + advance
                yield return new WaitForSeconds(dwellTime);

                if (lifetimeOpen) Emit(CaptureConstants.NpcEvtDwellEnd);

                if (manager != null)
                    manager.ReleaseAttractor(info, this);

                if (moveToTarget != null)
                    moveToTarget.ResumeExternal();

                // Notify UncertainBehavior (if present) that movement has resumed.
                if (uncertainBehavior != null)
                    uncertainBehavior.ResumeAfterArrival();

                AdvanceItinerary();
                yield break;
            }
            yield return new WaitForSeconds(1f);
        }

        // timed out waiting -> give up and advance
        if (lifetimeOpen) Emit(CaptureConstants.NpcEvtDwellEnd, "capacity_timeout=1");

        if (moveToTarget != null)
            moveToTarget.ResumeExternal();

        // Notify UncertainBehavior (if present). Even if no MoveToTarget is present, calling ResumeAfterArrival
        // is harmless and allows UncertainBehavior to resume if appropriate.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        AdvanceItinerary();
    }

    /// <summary>Part 3: advance to the next itinerary entry after a dwell (or finish at the end).</summary>
    void AdvanceItinerary()
    {
        currentIndex++;
        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count)
        {
            Finish();
            return;
        }

        state = State.Moving;
        MoveToCurrent();
    }

    /// <summary>
    /// Additive helper. Returns true and outputs an independent random NavMesh stop position INSIDE the
    /// given Attractor Plane when 'waypoint' carries an AttractorDwell component and a valid point was
    /// found. Returns false for entrances/exits, Planes without AttractorDwell, when random stopping is
    /// disabled on that component, or when AttractorDwell exhausted its bounded attempts - in every false
    /// case the caller keeps its original destination (the waypoint Transform).
    ///
    /// AttractorDwell samples with a PRIVATE System.Random, so this never advances UnityEngine.Random and
    /// the existing entrance/attractor/exit selection, attractor count/order and dwell timing are unchanged.
    /// </summary>
    bool TryGetAttractorDwellStop(Transform waypoint, out Vector3 stop)
    {
        stop = default;
        if (waypoint == null) return false;

        AttractorDwell dwell = waypoint.GetComponent<AttractorDwell>();
        if (dwell == null) return false;

        return dwell.TryGetRandomStandingPoint(out stop);
    }

    /// <summary>
    /// Additive helper. Starts a yaw-only rotation toward the Attractor's configured Facing Target for the
    /// duration of the dwell. The Facing Target lives on the AttractorDwell component of the Attractor
    /// Plane (info.transform) and is NEVER added to the itinerary - it only affects orientation while
    /// dwelling. No-op (existing rotation behaviour preserved) when there is no AttractorDwell or no
    /// Facing Target.
    /// </summary>
    void FaceDwellTargetIfConfigured(AttractorInfo info)
    {
        if (info == null || info.transform == null) return;

        AttractorDwell dwell = info.transform.GetComponent<AttractorDwell>();
        if (dwell == null || dwell.facingTarget == null) return;

        if (facingCoroutine != null) StopCoroutine(facingCoroutine);
        facingCoroutine = StartCoroutine(FaceYawCoroutine(dwell.facingTarget, dwell.facingTurnSpeedDeg));
    }

    /// <summary>
    /// Additive helper. Rotates this agent about the world Y axis only (never pitch/roll) toward 'lookAt'
    /// while the agent is dwelling (State.WaitingAtAttractor). This is safe because during the dwell:
    ///  - MoveToTarget.Update() early-returns (the mover is externally paused + halted) and never touches
    ///    transform.rotation;
    ///  - UncertainBehavior has suspended itself on arrival at the main target.
    /// Once the goal orientation is reached the coroutine stops updating rotation. When the dwell ends
    /// (state leaves WaitingAtAttractor) the loop exits and normal movement-facing resumes on the next leg.
    /// </summary>
    IEnumerator FaceYawCoroutine(Transform lookAt, float degreesPerSecond)
    {
        while (state == State.WaitingAtAttractor && lookAt != null)
        {
            Vector3 dir = lookAt.position - transform.position;
            dir.y = 0f; // horizontal / yaw only - do not tilt the agent up or down
            if (dir.sqrMagnitude < 1e-6f)
                break; // Facing Target is directly above/below the agent: nothing meaningful to face

            Quaternion goal = Quaternion.LookRotation(dir.normalized, Vector3.up);

            if (degreesPerSecond <= 0f)
            {
                transform.rotation = goal; // instant snap
                break;
            }

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, goal, degreesPerSecond * Time.deltaTime);

            if (Quaternion.Angle(transform.rotation, goal) <= 0.1f)
            {
                transform.rotation = goal; // reached - stop changing orientation
                break;
            }

            yield return null;
        }

        facingCoroutine = null;
    }

    // ===============================================================================================
    // Completion
    // ===============================================================================================

    /// <summary>
    /// Finish: mark finished, stop movement via MoveToTarget, and notify listeners (SpawnerManager will return to pool).
    /// Also releases any attractor slot currently held by this agent to avoid leaks.
    /// Reached at the itinerary's last entry in both modes (Part 3 via AdvanceItinerary()/OnReachedWaypoint(),
    /// Part 2 via the exit's arrival event), or externally via ForceFinish().
    /// </summary>
    void Finish()
    {
        state = State.Finished;

        // Close the lifetime BEFORE notifying the spawner so every observer of onAgentFinished sees an ended lifetime.
        if (lifetimeOpen)
        {
            Emit(CaptureConstants.NpcEvtItineraryComplete, "reason=" + finishReason);
            EndLifetime();
        }

        // Ensure unsubscribe to avoid lingering event handlers
        UnsubscribeFromMoveToArrived();

        // Release any claimed attractor slot for the current index if present
        ReleaseCurrentAttractorIfAny();

        if (moveToTarget != null)
            moveToTarget.Stop();

        onAgentFinished?.Invoke(this);
    }

    /// <summary>
    /// ForceFinish: external immediate termination (SpawnerManager trial-end despawn, etc.). Releases any claimed
    /// attractor slot for the current waypoint if necessary, then stops movement and notifies listeners.
    /// </summary>
    public void ForceFinish()
    {
        if (state == State.Finished) return;

        if (lifetimeOpen)
        {
            Emit(CaptureConstants.NpcEvtItineraryComplete, "reason=force_finish");
            EndLifetime();
        }

        // Release any claimed attractor for current index if present
        ReleaseCurrentAttractorIfAny();

        // Ensure unsubscribe
        UnsubscribeFromMoveToArrived();

        // Stop movement and notify
        if (moveToTarget != null)
            moveToTarget.Stop();

        onAgentFinished?.Invoke(this);
    }

    /// <summary>
    /// Helper: release the attractor slot corresponding to the current waypoint (if any).
    /// Called from Finish/ForceFinish to avoid leaking a claimed slot. A no-op in Part 2, where no attractor slot
    /// can ever be held (wandering points are never attractors and never call TryEnterAttractor()).
    /// </summary>
    void ReleaseCurrentAttractorIfAny()
    {
        if (manager == null) return;
        if (IsWanderingItinerary) return;

        if (itineraryTransforms != null && currentIndex < itineraryTransforms.Count)
        {
            var t = itineraryTransforms[currentIndex];
            var info = manager.FindAttractorInfo(t);
            if (info != null) manager.ReleaseAttractor(info, this);
        }
    }

    void OnDisable()
    {
        // Stop movement safely and unsubscribe
        UnsubscribeFromMoveToArrived();

        if (moveToTarget != null)
            moveToTarget.Stop();

        // NEW (fix for pooled-agent reactivation race): Unity automatically stops any coroutines started
        // on this component when the GameObject is deactivated, so WaitUntilMoverReadyThenMove() (if still
        // pending) is already halted at this point - we just clear the stale reference here so the next
        // BeginMovementWhenReady() call doesn't attempt to StopCoroutine() a handle that's already dead.
        startMovementCoroutine = null;

        // Additive: same reasoning for the dwell-facing coroutine; also drop the per-leg stop override so
        // a pooled reuse starts on the unchanged code path.
        facingCoroutine = null;
        currentLegStopOverride = null;

        // Part 2: drop the per-life progression state so a stale value is never held across a deactivation (the
        // next Initialize() call always resets this explicitly anyway; this is a defensive tidy-up).
        wanderingStarted = false;
        nextWanderRetryTime = -1f;
        awaitingEntrancePlacement = false;
        entrancePlacementRetries = 0;
    }

    void OnDestroy()
    {
        if (uncertainBehavior != null) uncertainBehavior.StateChanged -= OnUncertainStateChanged;
        UnsubscribeFromMoveToArrived();

        if (moveToTarget != null)
            moveToTarget.Stop();
    }
}
