// PedestrianAgent.cs
// Runtime component that drives a pedestrian along a pre-built Transform-based itinerary.
// This modified version exclusively delegates movement to MoveToTarget and removes direct NavMeshAgent usage.
// Arrival detection and pause/resume use MoveToTarget APIs.
//
// Change: When an agent arrives at an attractor and is waiting/dwelling (including polling for capacity),
// the agent now freezes its animator (freezeAnimator = true) so it does not "step" while waiting.
// This is implemented by passing freezeAnimator:true to MoveToTarget.PauseExternal in waiting coroutines.
//
// Improvements in this file:
// - Subscribe to MoveToTarget.OnArrived when instructing movement, so arrival is handled immediately via the event.
// - Keeps distance-based fallback checks in Update() for robustness if the event is missed.
// - Preserves attractor capacity/dwell semantics and all existing public APIs.
//
// Notes:
// - Each instance still gets a persistent unique ID assigned in Awake() (NPC-000001).
// - If MoveToTarget is not present, the agent will still use simple distance checks to consider arrival,
//   but will not attempt to move itself (so in practice you should attach MoveToTarget to the prefab).
// - Attractor capacity semantics are preserved: when arriving at an attractor the agent requests entry from SpawnerManager.
//
// -------------------------------------------------------------------------------------------------------
// ADDITIVE CHANGE (Attractor stop position + dwell facing) - does NOT touch route generation:
//   * When the current itinerary waypoint is an Attractor Plane carrying an AttractorDwell component,
//     the PHYSICAL movement destination for that leg is overridden with an independent random point
//     inside that Plane (vertically projected onto the NavMesh, X/Z preserved). The itinerary list, the
//     waypoint Transforms, currentIndex progression, entrance/attractor/exit selection and order are all
//     unchanged - only where the agent physically stops for this one Attractor visit differs.
//   * While dwelling, the agent turns yaw-only to face AttractorDwell.facingTarget (if assigned).
//   * All random sampling happens in AttractorDwell via a private System.Random, so the global
//     UnityEngine.Random sequence used by SpawnerManager route generation is never perturbed.
// -------------------------------------------------------------------------------------------------------
//
// -------------------------------------------------------------------------------------------------------
// ADDITIVE CHANGE (Part 2 continuous undirected wandering, with an instant initial-Entrance relocation) -
// does NOT touch any of the above:
//   * A second, completely separate initialization path - Initialize(SpawnerManager, float, Transform) -
//     puts the agent into 'undirectedMode'. In this mode the agent NEVER builds or consumes an itinerary,
//     NEVER calls MoveToCurrent()/OnReachedWaypoint()/AdvanceItinerary()/WaitAtAttractor()/
//     SimpleDwellThenAdvance()/Finish(), and NEVER references attractors or exits. It stays in
//     State.Moving for its entire life and only ever ends via the EXISTING external ForceFinish() path
//     (called by SpawnerManager when the trial/Part/End-Panel lifecycle requires it) - it never
//     self-terminates.
//   * The agent's physical spawn position is the SAME startTransform-based origin SpawnerManager already
//     uses for Part 3 (unchanged - see SpawnerManager.SpawnAgent()). Immediately after spawning, exactly
//     like Part 3, it is INSTANTLY relocated to a randomly-chosen entrance (the 'initialEntrance' parameter
//     - the SAME Random.Range(0, entrances.Length) draw Part 3 uses for itinerary[0]) via
//     MoveToTarget.TeleportTo() - NOT walked there. TeleportTo() sets transform.position directly and warps
//     the NavMeshAgent in the same call, so no path is queued and no transit is ever visible to the
//     participant; the agent simply appears already standing at its Entrance. Once relocated, the Entrance
//     has no further role whatsoever - not added to any itinerary, never revisited deliberately, no dwell,
//     no Finish() - and the agent goes straight into continuous wandering the same frame.
//   * From that point, Update() repeatedly picks a random Transform from SpawnerManager.wanderingPoints
//     (excluding, where possible, the immediately-previous point), validates it with
//     NavMesh.CalculatePath, and hands it to the SAME MoveToTarget.MoveTo(Transform) overload directed mode
//     uses. The NEXT point is selected as soon as the agent comes within 'wanderLookAheadDistance' of its
//     current one - BEFORE MoveToTarget's own (much tighter) arriveThreshold triggers a full stop - so
//     movement reads as continuous wandering, never a deliberate dwell.
//   * Arrival at each wandering leg is detected purely by comparing transform.position against a LOCALLY
//     REMEMBERED Transform reference ('currentWanderPoint') - never by reading MoveToTarget.OnArrived/
//     IsArrived back. This is deliberate: UncertainBehavior may temporarily redirect MoveToTarget's live
//     target during a Detour, and this locally-remembered reference must stay correct regardless of that
//     temporary substitution (UncertainBehavior itself already saves/restores the real target correctly via
//     its own GetCurrentMainTarget()/mainTargetOnDetourStart mechanism - see UncertainBehavior.cs,
//     unmodified).
//   * UncertainBehavior is not modified and is not duplicated: it continues to read/restore whatever
//     MoveToTarget's current target is, so Pause/Hesitate/Slow/Detour all keep working unchanged during
//     continuous wandering, exactly as they do under the directed itinerary. The one-time entrance
//     relocation happens via TeleportTo() before any wandering target is ever set, so it is never subject
//     to a Detour redirect in the first place.
// -------------------------------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

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

    // Itinerary (Transform-based; the entrance/attractor/exit sequence built by SpawnerManager.BuildItinerary()).
    // Null while in undirected mode - see the ADDITIVE CHANGE block above.
    List<Transform> itineraryTransforms;

    int currentIndex = 0;
    State state = State.Moving;
    SpawnerManager manager;
    float dwellTime = 5f;

    // tuning
    public float arriveTolerance = 0.6f; // consider arrived if within this distance

    // -----------------------------------------------------------------------------------------------
    // Attractor stop override + dwell facing (additive; does NOT change route generation)
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
    // Part 2 continuous undirected wandering (additive - see ADDITIVE CHANGE block at top of file).
    // -----------------------------------------------------------------------------------------------

    /// <summary>True when this agent is in continuous undirected-wandering mode (Part 2), false for the
    /// existing directed itinerary mode (Part 3). Set exclusively by whichever Initialize() overload is
    /// called; never changes mid-life.</summary>
    bool undirectedMode = false;

    /// <summary>The wandering point (a Transform from SpawnerManager.wanderingPoints) this agent is
    /// currently walking toward, in undirected mode. Locally remembered - never re-read from
    /// MoveToTarget - so a temporary UncertainBehavior Detour cannot desynchronise it. Null only before
    /// the first point has ever been selected, or if a selection attempt failed (see SelectNextWanderPoint).</summary>
    Transform currentWanderPoint = null;

    /// <summary>NEW: the one-time initial Entrance target for an undirected (Part 2) agent, set once at
    /// Initialize() from the same random-entrance draw SpawnerManager already uses for Part 3
    /// (chosenEntrance). Consumed and cleared to null the moment BeginUndirectedMovement() runs (the agent
    /// is INSTANTLY relocated there via MoveToTarget.TeleportTo() - not walked - before continuous wandering
    /// begins), so it is never read again after that point. Null immediately if Initialize() was given a
    /// null entrance (see Requirement: no valid Entrance -&gt; do not crash, start wandering from the spawn
    /// position instead).</summary>
    Transform initialEntranceTarget = null;

    /// <summary>Inspector-tunable distance (copied from SpawnerManager.wanderLookAheadDistance at
    /// Initialize time) at which the agent picks its NEXT wandering point, before MoveToTarget's own
    /// (tighter) arriveThreshold would otherwise bring it to a full stop.</summary>
    float wanderLookAheadDistance = 1.5f;

    /// <summary>Time.time before which UpdateUndirectedWandering() will not attempt another
    /// SelectNextWanderPoint() call. Used both as a defensive backoff when no reachable candidate was
    /// found, and as a brief grace period after being forced to reselect the same point (the
    /// single-valid-point case) - in both cases it prevents NavMesh.CalculatePath from being invoked
    /// every single frame.</summary>
    float nextWanderRetryTime = -1f;

    /// <summary>One-time guard so "no wandering points configured" is logged once per agent life, not
    /// every retry.</summary>
    bool warnedNoWanderingPoints = false;

    /// <summary>Reused NavMeshPath buffer for SelectNextWanderPoint()'s NavMesh.CalculatePath validation,
    /// so repeated candidate checks do not allocate a new NavMeshPath each time.</summary>
    NavMeshPath wanderPathBuffer;

    /// <summary>Backoff applied after a failed/degenerate wander-point selection (see nextWanderRetryTime).
    /// Not Inspector-exposed - this is an internal pacing safeguard, not a behavioural tuning parameter.</summary>
    const float k_WanderRetryBackoffSeconds = 0.5f;

    // -----------------------------
    // Unique ID implementation
    // -----------------------------
    static long s_globalAgentCounter = 0;
    [Tooltip("Read-only unique ID assigned to this agent instance at Awake.")]
    [SerializeField] string agentID = null;
    public string AgentID => agentID;

    /// <summary>
    /// Read-only view of the existing internal state (data-capture, Revision 2). Observation only:
    /// this getter never changes NPC behaviour, itinerary, dwell timing, randomisation, or NavMesh state.
    /// </summary>
    public State CurrentState => state;

    // Keep track of whether we've subscribed to the current MoveToTarget.OnArrived
    bool subscribedToMoveToArrived = false;

    // Cached reference to UncertainBehavior (if present) so we can notify it to resume after dwell or on reuse
    UncertainBehavior uncertainBehavior = null;

    // NEW (fix for pooled-agent reactivation race): tracks the coroutine started by BeginMovementWhenReady()
    // so a second call (e.g. rapid re-Initialize on quick pool reuse) can safely cancel the previous wait.
    Coroutine startMovementCoroutine = null;

    [Tooltip("NEW: maximum time (seconds, unscaled) to wait for MoveToTarget.IsReadyToMove() to report true " +
             "before issuing the first MoveToCurrent() command anyway. This bounds how long a spawn can be " +
             "delayed if the mover never becomes ready (e.g. genuinely off NavMesh), so an agent is never " +
             "blocked forever - MoveToTarget's own defensive logging still applies if it truly cannot move.")]
    public float moveReadyPollTimeout = 3f;

    void Awake()
    {
        // Assign unique ID if not already assigned (prewarm / pool instantiation will call Awake once)
        if (string.IsNullOrEmpty(agentID))
        {
            long id = System.Threading.Interlocked.Increment(ref s_globalAgentCounter);
            agentID = $"NPC-{id:000000}";
        }

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

        // Part 2 undirected wandering: allocate once, reused across every SelectNextWanderPoint() call
        // for the lifetime of this instance (including pooled reuse) to avoid per-call allocation.
        wanderPathBuffer = new NavMeshPath();
    }

    /// <summary>
    /// Initialize called by SpawnerManager when spawning / reusing agent with a Transform-based itinerary.
    /// Movement is handled exclusively via MoveToTarget. This is the DIRECTED (Part 3) initialization path -
    /// completely unchanged except for the two new lines at the top that reset undirected-mode state, so a
    /// pooled instance previously used for Part 2 wandering starts this life cleanly in directed mode.
    /// </summary>
    public void Initialize(List<Transform> itinerary, SpawnerManager mgr, float dwellSeconds)
    {
        // Part 2/Part 3 pooling hygiene (Requirement: a pooled instance must not retain undirected-mode
        // state when reused for a directed spawn). Everything below this block is the ORIGINAL,
        // unmodified directed-mode initialization.
        this.undirectedMode = false;
        this.currentWanderPoint = null;
        this.initialEntranceTarget = null;

        if (itinerary == null) itinerary = new List<Transform>();

        this.itineraryTransforms = new List<Transform>(itinerary);

        this.manager = mgr;
        this.dwellTime = dwellSeconds;
        this.currentIndex = 0;
        this.state = State.Moving;

        // Additive: a pooled instance may still hold a stop override from its previous life. Clear it so
        // the first leg (always the entrance) starts on the unchanged code path.
        this.currentLegStopOverride = null;

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
        // life clean, then re-enable it for the new itinerary. (ResumeAfterArrival() remains used for the
        // lighter-weight per-dwell resume in the WaitAtAttractor/PollForCapacity/SimpleDwellThenAdvance
        // coroutines below - this call site needs the full reset instead.)
        if (uncertainBehavior != null)
            uncertainBehavior.ResetForReuse();

        // NEW (fix for pooled-agent reactivation race): rather than calling MoveToCurrent() synchronously
        // here (which could race with MoveToTarget's own pooled-reuse re-warp onto the NavMesh, see
        // MoveToTarget.IsReadyToMove() docs), defer the first movement command until the mover reports
        // it is actually ready, or a bounded timeout elapses.
        BeginMovementWhenReady();
    }

    /// <summary>
    /// NEW: Initialize called by SpawnerManager when spawning / reusing agent for Part 2's continuous
    /// UNDIRECTED wandering mode. No itinerary is built or consumed - instead the agent is INSTANTLY
    /// RELOCATED (teleported, not walked) to a single one-time 'initialEntrance' target (the SAME random
    /// entrance draw SpawnerManager already makes for Part 3 - see SpawnerManager.SpawnAgent()) via
    /// MoveToTarget.TeleportTo() (an existing public API - see BeginUndirectedMovement()), then immediately
    /// transitions into the existing continuous-wandering loop: repeatedly picking a random point from
    /// 'mgr.wanderingPoints', walking to it, and immediately picking another, indefinitely, until
    /// externally stopped (ForceFinish(), called by SpawnerManager's existing trial-end / End-Panel
    /// hard-stop mechanisms - never by this agent itself). The Entrance has no role whatsoever once the
    /// teleport happens: it is not added to any itinerary, never revisited deliberately, and never
    /// triggers attractor dwell, exit logic, or Finish(). See the ADDITIVE CHANGE block at the top of this
    /// file for the full design rationale.
    /// </summary>
    /// <param name="mgr">The SpawnerManager that owns wanderingPoints and this agent's pooling lifecycle.</param>
    /// <param name="lookAheadDistance">Copied from SpawnerManager.wanderLookAheadDistance at spawn time.</param>
    /// <param name="initialEntrance">The randomly-chosen entrance (SpawnerManager's existing
    /// Random.Range(0, entrances.Length) draw - the SAME one Part 3 uses for itinerary[0]) this agent
    /// should be instantly relocated to before beginning continuous wandering. Pass null if no valid
    /// entrance is available (e.g. entrances[] is empty) - the agent then skips the teleport entirely and
    /// starts wandering from wherever it was spawned, rather than crashing.</param>
    public void Initialize(SpawnerManager mgr, float lookAheadDistance, Transform initialEntrance)
    {
        // Directed-mode pooling hygiene, mirrored: a pooled instance previously used for Part 3 must not
        // retain its itinerary/index when reused for Part 2 undirected wandering.
        this.itineraryTransforms = null;
        this.currentIndex = 0;
        this.currentLegStopOverride = null;

        this.undirectedMode = true;
        this.manager = mgr;
        this.dwellTime = 0f; // unused in undirected mode - SimpleDwellThenAdvance()/WaitAtAttractor() are never called
        this.state = State.Moving;
        this.currentWanderPoint = null;
        this.wanderLookAheadDistance = Mathf.Max(0.01f, lookAheadDistance);
        this.nextWanderRetryTime = -1f;
        this.warnedNoWanderingPoints = false;

        // NEW: the one-time initial-Entrance teleport target, consumed by BeginUndirectedMovement() once
        // MoveToTarget is confirmed ready. If null (no valid entrance was supplied), the teleport is simply
        // skipped there and wandering starts from the spawn position instead.
        this.initialEntranceTarget = initialEntrance;

        if (moveToTarget != null)
        {
            moveToTarget.startTransform = null;
            moveToTarget.startPosition = transform.position;
            try { moveToTarget.SetMoveSpeed(moveToTarget.speed); } catch (Exception) { /* ignore if missing */ }
        }

        // Same full per-life reset as directed mode - a reused pooled instance starts this life clean.
        if (uncertainBehavior != null)
            uncertainBehavior.ResetForReuse();

        // Same "wait for mover ready" deferral as directed mode (see WaitUntilMoverReadyThenMove below),
        // just branching to the initial-Entrance leg (or straight to SelectNextWanderPoint() if there is
        // none) instead of MoveToCurrent() once ready.
        BeginMovementWhenReady();
    }

    void Update()
    {
        // Part 2 continuous undirected wandering has its own, completely separate update path. It never
        // falls through into the directed itinerary logic below (MoveToCurrent/OnReachedWaypoint/
        // AdvanceItinerary/WaitAtAttractor/SimpleDwellThenAdvance/Finish are never reached from here).
        if (undirectedMode)
        {
            UpdateUndirectedWandering();
            return;
        }

        // ---------------- Everything below this point is the ORIGINAL, unmodified directed-mode logic ----------------

        // Only perform arrival detection while moving
        if (state != State.Moving)
            return;

        // If we are using the MoveToTarget.OnArrived event we may have already handled arrival.
        // Keep polling logic as a fallback for robustness.
        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count) return;
        Transform t = itineraryTransforms[currentIndex];
        if (t == null)
        {
            OnReachedWaypoint();
            return;
        }

        // When a per-leg physical stop override is active (a random point inside an Attractor Plane),
        // the distance fallback must measure to THAT point, not to the Plane pivot 't.position' -
        // otherwise it could fire early as the agent passes near the pivot on the way to the random
        // point. For every non-attractor leg 'currentLegStopOverride' is null and this is unchanged.
        Vector3 transformFallbackTarget = currentLegStopOverride ?? t.position;

        if (moveToTarget != null)
        {
            if (moveToTarget.IsArrived)
            {
                OnReachedWaypoint();
                return;
            }

            // Additional distance fallback
            float d = Vector3.Distance(transform.position, transformFallbackTarget);
            if (d <= arriveTolerance)
            {
                OnReachedWaypoint();
                return;
            }
        }
        else
        {
            float d = Vector3.Distance(transform.position, transformFallbackTarget);
            if (d <= arriveTolerance)
            {
                OnReachedWaypoint();
                return;
            }
        }
    }

    // -----------------------------------------------------------------------------------------------
    // Part 2 continuous undirected wandering - implementation (additive; never called in directed mode).
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Per-frame undirected-mode update. Never touches itineraryTransforms/currentIndex, never calls
    /// MoveToCurrent()/OnReachedWaypoint()/AdvanceItinerary()/Finish(). The one-time initial Entrance
    /// relocation is an instant teleport handled entirely in BeginUndirectedMovement() (called once, before
    /// this method ever runs for a given life) - by the time this method first executes, the agent is
    /// already at the Entrance (or its spawn position, if no entrance was supplied) and purely in the
    /// continuous-wandering phase. Wandering-leg arrival is detected by comparing transform.position
    /// directly against 'currentWanderPoint' - deliberately NOT via MoveToTarget.OnArrived/IsArrived (see
    /// the ADDITIVE CHANGE block at the top of this file for why).
    /// </summary>
    void UpdateUndirectedWandering()
    {
        // Defensive - an undirected agent should always be State.Moving (it never transitions to
        // WaitingAtAttractor or Finished on its own), but guard in case ForceFinish() ran this frame.
        if (state != State.Moving)
            return;

        // Cooldown gate: prevents SelectNextWanderPoint() (and therefore NavMesh.CalculatePath) from being
        // invoked every single frame, both when no reachable candidate was found and in the degenerate
        // single-valid-point case (see SelectNextWanderPoint).
        if (Time.time < nextWanderRetryTime)
            return;

        if (currentWanderPoint == null)
        {
            // No point selected yet (first frame after BeginUndirectedMovement()'s teleport, or a previous
            // point was destroyed at runtime and Unity's null-comparison correctly caught it) - select one now.
            SelectNextWanderPoint();
            return;
        }

        float d = Vector3.Distance(transform.position, currentWanderPoint.position);
        if (d <= wanderLookAheadDistance)
        {
            // Close enough to retarget NOW, before MoveToTarget's own (tighter) arriveThreshold would
            // otherwise bring the NavMeshAgent to a full stop. See wanderLookAheadDistance doc comment.
            SelectNextWanderPoint();
        }
    }

    /// <summary>
    /// Selects the next wandering target from SpawnerManager.wanderingPoints and immediately issues it to
    /// MoveToTarget, or applies a short defensive backoff if no valid candidate could be found this
    /// attempt. Never generates a random position - the candidate is always one of the predefined
    /// Transforms; NavMesh.SamplePosition is not used at all here (only NavMesh.CalculatePath, purely to
    /// validate reachability of an already-chosen candidate).
    ///
    /// Algorithm (matches SpawnerManager.BuildItinerary()'s existing "pick without replacement" style):
    ///   1. Candidate index list = every non-null entry in wanderingPoints, excluding the entry equal to
    ///      currentWanderPoint if any OTHER valid entry exists (so A-&gt;A repeats are avoided whenever
    ///      possible, but a single-point list is still usable - see step 1b).
    ///   1b. If step 1 leaves zero candidates (either the array is entirely null/empty, or the ONLY valid
    ///       entry IS currentWanderPoint), rebuild the candidate list WITHOUT excluding currentWanderPoint.
    ///       If it is still empty, there are no configured wandering points at all - warn once and back off.
    ///   2. Shuffle by repeatedly drawing a random remaining candidate and removing it (Random.Range +
    ///      RemoveAt), so every remaining candidate is tried at most once per call - never a fixed
    ///      "max attempts" counter with possible repeats.
    ///   3. For each shuffled candidate, validate with NavMesh.CalculatePath(currentPos, candidate.position,
    ///      NavMesh.AllAreas, ...) and require NavMeshPathStatus.PathComplete. Accept the first that passes.
    ///   4. If none pass, apply a short backoff (k_WanderRetryBackoffSeconds) and warn - UpdateUndirectedWandering()
    ///      will try again after the cooldown, never in a tight per-frame loop.
    /// </summary>
    void SelectNextWanderPoint()
    {
        Transform[] points = (manager != null) ? manager.wanderingPoints : null;

        if (points == null || points.Length == 0)
        {
            WarnNoWanderingPointsOnce();
            nextWanderRetryTime = Time.time + k_WanderRetryBackoffSeconds;
            return;
        }

        // Step 1: candidates excluding the current point where possible.
        List<int> candidates = new List<int>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i] == null) continue;
            if (points[i] == currentWanderPoint) continue;
            candidates.Add(i);
        }

        // Step 1b: fallback when the only valid point IS the current one (or nothing was excluded because
        // currentWanderPoint was null and everything else is also null).
        bool forcedSameCandidate = false;
        if (candidates.Count == 0)
        {
            for (int i = 0; i < points.Length; i++)
                if (points[i] != null) candidates.Add(i);

            if (candidates.Count == 0)
            {
                // Every entry is null - genuinely nothing configured.
                WarnNoWanderingPointsOnce();
                nextWanderRetryTime = Time.time + k_WanderRetryBackoffSeconds;
                return;
            }

            // If the only non-null entry is the current point, we are about to reselect it (allowed, per
            // Requirement 6 - a single-point list must still work). Flag this so the success branch below
            // applies a short cooldown instead of retriggering on the very next frame.
            forcedSameCandidate = (candidates.Count == 1 && points[candidates[0]] == currentWanderPoint);
        }

        // Step 2 + 3: shuffle-and-validate, one attempt per remaining candidate, no repeats.
        Vector3 fromPos = transform.position;
        while (candidates.Count > 0)
        {
            int pick = UnityEngine.Random.Range(0, candidates.Count);
            int idx = candidates[pick];
            candidates.RemoveAt(pick);

            Transform cand = points[idx];
            if (cand == null) continue; // defensive; already filtered above, but a point could be destroyed mid-loop

            bool reachable = NavMesh.CalculatePath(fromPos, cand.position, NavMesh.AllAreas, wanderPathBuffer)
                              && wanderPathBuffer.status == NavMeshPathStatus.PathComplete;

            if (!reachable)
                continue;

            currentWanderPoint = cand;

            if (moveToTarget != null)
                moveToTarget.MoveTo(cand); // same MoveToTarget.MoveTo(Transform) overload directed mode uses

            // Mirrors the existing dwell coroutines' defensive call: harmless no-op if UncertainBehavior was
            // never suspended, but guarantees it cannot get stuck dormant if MoveToTarget.OnArrived ever
            // fires despite the look-ahead pre-emption (see the ADDITIVE CHANGE block at the top of this file).
            if (uncertainBehavior != null)
                uncertainBehavior.ResumeAfterArrival();

            if (forcedSameCandidate)
            {
                // Degenerate single-point case: give the agent a brief grace period before re-evaluating,
                // instead of immediately re-triggering SelectNextWanderPoint() (and another
                // NavMesh.CalculatePath call) on the very next frame while still within look-ahead range
                // of the point it is already standing next to.
                nextWanderRetryTime = Time.time + k_WanderRetryBackoffSeconds;
            }

            return;
        }

        // No candidate validated this attempt (e.g. the agent is in an isolated pocket, or every remaining
        // point has gone stale after a NavMesh rebake). Back off briefly and let UpdateUndirectedWandering()
        // retry next eligible frame - never an unbounded loop, never dozens of CalculatePath calls per frame.
        Debug.LogWarning($"[PedestrianAgent][{agentID}] No reachable wandering point found this attempt " +
            $"(of {points.Length} configured); retrying in {k_WanderRetryBackoffSeconds:F1}s. If this persists, " +
            "check that SpawnerManager.wanderingPoints are placed on/near the walkable NavMesh and reachable " +
            "from this agent's current position.");
        nextWanderRetryTime = Time.time + k_WanderRetryBackoffSeconds;
    }

    void WarnNoWanderingPointsOnce()
    {
        if (warnedNoWanderingPoints) return;
        warnedNoWanderingPoints = true;
        Debug.LogWarning($"[PedestrianAgent][{agentID}] Undirected mode: SpawnerManager.wanderingPoints is " +
            "empty or unassigned. This agent will remain stationary until wandering points are configured " +
            "in the Inspector.");
    }

    /// <summary>
    /// NEW (fix for pooled-agent reactivation race): starts (or restarts) the coroutine that waits for
    /// MoveToTarget to report readiness before issuing the first MoveToCurrent() command for this itinerary.
    /// Cancels any previous pending wait first, in case Initialize() is called again
    /// in quick succession (e.g. very fast pool reuse) before the previous wait finished.
    /// Shared by both directed and undirected Initialize() overloads - see WaitUntilMoverReadyThenMove().
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
            // not normally happen, but if it does, MoveToCurrent() will simply run the next time this
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
    /// frame as go.SetActive(true). Previously, MoveToCurrent() (-> MoveToTarget.MoveTo(...)) was invoked
    /// synchronously right there, so it could run before MoveToTarget.IsReadyToMove() was true; MoveTo(...)
    /// would then silently skip calling agent.SetDestination(...) (only logging a warning), leaving the
    /// agent internally marked as 'moving' but with no actual NavMesh destination - an intermittently
    /// "frozen" NPC that is more likely to surface under load (many simultaneous pool reuses) and on Quest,
    /// where coroutine scheduling latency is more variable than in the Editor.
    ///
    /// Fix: poll MoveToTarget.IsReadyToMove() once per frame and only issue the first movement command once
    /// the mover reports it can actually accept a destination, or once 'moveReadyPollTimeout' seconds have
    /// elapsed (whichever comes first). The timeout ensures an agent is never blocked forever waiting - if
    /// the mover genuinely cannot get onto the NavMesh, MoveToTarget's own existing defensive
    /// logging/disable logic still applies exactly as before.
    ///
    /// NEW: branches on 'undirectedMode' for which "first movement command" to issue - MoveToCurrent() for
    /// directed itineraries (unchanged), BeginUndirectedMovement() for Part 2 (instantly teleports to the
    /// one-time initial Entrance if one was supplied, then starts continuous wandering immediately via
    /// SelectNextWanderPoint() - see BeginUndirectedMovement()).
    /// </summary>
    IEnumerator WaitUntilMoverReadyThenMove()
    {
        if (moveToTarget == null)
        {
            // No MoveToTarget on this prefab: nothing to wait for. Preserve the original fallback
            // behaviour described in Awake() (agent relies on external movement logic).
            if (undirectedMode) BeginUndirectedMovement();
            else MoveToCurrent();
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

        if (undirectedMode)
            BeginUndirectedMovement();
        else
            MoveToCurrent();

        startMovementCoroutine = null;
    }

    /// <summary>
    /// NEW: issues the first movement command for undirected (Part 2) mode, once MoveToTarget is confirmed
    /// ready (see WaitUntilMoverReadyThenMove). Mirrors Part 3's spawn behaviour: the agent is instantiated
    /// at startTransform and must appear to the participant as already standing at its randomly-chosen
    /// Entrance - not walk there. If an initial Entrance was supplied to Initialize(), relocates to it
    /// instantly via MoveToTarget.TeleportTo() (sets transform.position AND warps the NavMeshAgent, so no
    /// stray path is queued and no walked leg is ever visible). Then, in either case (entrance supplied or
    /// not - the "no valid Entrance -&gt; do not crash" defensive fallback), goes straight into continuous
    /// wandering via SelectNextWanderPoint(). There is no more "awaiting arrival" phase to track, since the
    /// relocation is instantaneous.
    /// </summary>
    void BeginUndirectedMovement()
    {
        if (initialEntranceTarget != null && moveToTarget != null)
        {
            // Instant relocation - not a walked MoveTo(). TeleportTo() sets transform.position directly and
            // calls NavMeshAgent.Warp(), so the agent simply appears at the Entrance with no visible transit.
            moveToTarget.TeleportTo(initialEntranceTarget.position);
        }

        initialEntranceTarget = null;
        SelectNextWanderPoint();
    }

    /// <summary>
    /// MoveToCurrent: instruct MoveToTarget to move to the current waypoint.
    /// This method exclusively calls MoveToTarget.MoveTo(...).
    /// We subscribe to MoveToTarget.OnArrived to get immediate notification when the leg completes.
    /// DIRECTED MODE ONLY - never called while undirectedMode is true.
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
            }
            else
            {
                // Original behaviour: move to the waypoint Transform itself.
                moveToTarget.targetTransform = target;

                // Subscribe to OnArrived for immediate reaction
                SubscribeToMoveToArrived();

                // MoveToTarget will handle movement and arrival
                moveToTarget.MoveTo(target);
            }
        }
        else
        {
            // No MoveToTarget present - rely on external movement and periodic distance checks.
        }
    }

    /// <summary>
    /// Subscribe to MoveToTarget.OnArrived if available and not already subscribed.
    /// The subscribed callback triggers OnReachedWaypoint() which advances the itinerary.
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
    /// We call OnReachedWaypoint() to run the existing attractor/dwell logic.
    /// </summary>
    void OnMoveToArrived()
    {
        // Unsubscribe immediately — the next leg will re-subscribe in MoveToCurrent().
        UnsubscribeFromMoveToArrived();

        // Safety: only call the same logic as the polling path.
        OnReachedWaypoint();
    }

    /// <summary>
    /// Called when a waypoint is considered reached.
    /// Handles attractor capacity/dwell or finishing when exit reached.
    /// DIRECTED MODE ONLY - never called while undirectedMode is true (undirected agents never subscribe
    /// to MoveToTarget.OnArrived in the first place - see SelectNextWanderPoint).
    /// </summary>
    void OnReachedWaypoint()
    {
        // Defensive unsubscribe to ensure no stale subscriptions remain
        UnsubscribeFromMoveToArrived();

        if (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count) return;
        Transform target = itineraryTransforms[currentIndex];
        bool isExit = (currentIndex == itineraryTransforms.Count - 1);

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
                StartCoroutine(SimpleDwellThenAdvance());
                return;
            }
        }
        else
        {
            Finish();
        }
    }

    IEnumerator SimpleDwellThenAdvance()
    {
        state = State.WaitingAtAttractor;

        // Pause movement via MoveToTarget and freeze animator so agent does not step while waiting.
        if (moveToTarget != null)
            moveToTarget.PauseExternal(haltMovement: true, freezeAnimator: true);

        yield return new WaitForSeconds(dwellTime);

        // Resume movement via MoveToTarget (if present)
        if (moveToTarget != null)
            moveToTarget.ResumeExternal();

        // Notify UncertainBehavior (if present) that movement has resumed so it can restart its random behaviors.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        AdvanceItinerary();
    }

    IEnumerator WaitAtAttractor(AttractorInfo info)
    {
        state = State.WaitingAtAttractor;

        // Pause and freeze animator while waiting at attractor
        if (moveToTarget != null)
            moveToTarget.PauseExternal(haltMovement: true, freezeAnimator: true);

        // Additive: yaw-only turn toward this Attractor's configured Facing Target (if any) for the dwell.
        // No-op when the Plane has no AttractorDwell or no facingTarget (existing rotation is preserved).
        FaceDwellTargetIfConfigured(info);

        yield return new WaitForSeconds(dwellTime);

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

        float timeout = 30f;
        float start = Time.time;
        while (Time.time - start < timeout)
        {
            if (manager != null && manager.TryEnterAttractor(info, this))
            {
                // once admitted, dwell and then release + advance
                yield return new WaitForSeconds(dwellTime);

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
        if (moveToTarget != null)
            moveToTarget.ResumeExternal();

        // Notify UncertainBehavior (if present). Even if no MoveToTarget is present, calling ResumeAfterArrival
        // is harmless and allows UncertainBehavior to resume if appropriate.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        AdvanceItinerary();
    }

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

    /// <summary>
    /// Finish: mark finished, stop movement via MoveToTarget, and notify listeners (SpawnerManager will return to pool).
    /// Also releases any attractor slot currently held by this agent to avoid leaks.
    /// Only ever reached in directed mode (via AdvanceItinerary()/MoveToCurrent() at the itinerary's last
    /// index) or externally via ForceFinish() (both modes) - undirected wandering never calls this itself.
    /// </summary>
    void Finish()
    {
        state = State.Finished;

        // Ensure unsubscribe to avoid lingering event handlers
        UnsubscribeFromMoveToArrived();

        // Release any claimed attractor slot for the current index if present
        ReleaseCurrentAttractorIfAny();

        if (moveToTarget != null)
            moveToTarget.Stop();

        onAgentFinished?.Invoke(this);
    }

    /// <summary>
    /// ForceFinish: external immediate termination. Releases any claimed attractor slot for the current waypoint if necessary,
    /// then stops movement and notifies listeners. This is the ONLY way an undirected (Part 2) agent's life
    /// ends - it is called by SpawnerManager's existing, Part-agnostic external lifecycle mechanisms
    /// (trial-end despawn, End-Panel hard stop, etc.), never by this agent itself.
    /// </summary>
    public void ForceFinish()
    {
        if (state == State.Finished) return;

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
    /// Called from Finish/ForceFinish to avoid leaking a claimed slot. Safe to call in undirected mode:
    /// itineraryTransforms is null there, so this is a guaranteed no-op (no attractor slot can ever be
    /// held by an undirected agent, since it never calls TryEnterAttractor()).
    /// </summary>
    void ReleaseCurrentAttractorIfAny()
    {
        if (manager == null) return;

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

        // Part 2 undirected wandering: drop the remembered wander point too, so a stale reference is never
        // held across a deactivation (the next Initialize() call - directed or undirected - always resets
        // this explicitly anyway; this is a defensive tidy-up, not load-bearing).
        currentWanderPoint = null;

        // Same reasoning for the one-time initial-Entrance relocation state.
        initialEntranceTarget = null;
    }

    void OnDestroy()
    {
        UnsubscribeFromMoveToArrived();

        if (moveToTarget != null)
            moveToTarget.Stop();
    }
}
