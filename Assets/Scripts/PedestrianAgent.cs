// PedestrianAgent.cs
// Runtime component that drives a pedestrian along a pre-built itinerary (either a list of Transforms OR a list of Vector3 positions).
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

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

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

    // Itinerary variants: either transforms OR positions
    List<Transform> itineraryTransforms;
    List<Vector3> itineraryPositions;
    // Optional parallel attractor info list for position-based itineraries (same length as itineraryPositions, entries nullable)
    List<AttractorInfo> itineraryPositionAttractorInfos;

    bool usingPositionItinerary = false;
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
    }

    /// <summary>
    /// Initialize called by SpawnerManager when spawning / reusing agent with a Transform-based itinerary.
    /// Movement is handled exclusively via MoveToTarget.
    /// </summary>
    public void Initialize(List<Transform> itinerary, SpawnerManager mgr, float dwellSeconds)
    {
        if (itinerary == null) itinerary = new List<Transform>();

        this.itineraryTransforms = new List<Transform>(itinerary);
        this.itineraryPositions = null;
        this.itineraryPositionAttractorInfos = null;
        this.usingPositionItinerary = false;

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

        // If this agent has an UncertainBehavior (pooled reuse case), ensure it is resumed
        // so uncertain behavior will run for the entire new itinerary.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        // NEW (fix for pooled-agent reactivation race): rather than calling MoveToCurrent() synchronously
        // here (which could race with MoveToTarget's own pooled-reuse re-warp onto the NavMesh, see
        // MoveToTarget.IsReadyToMove() docs), defer the first movement command until the mover reports
        // it is actually ready, or a bounded timeout elapses.
        BeginMovementWhenReady();
    }

    /// <summary>
    /// Initialize with a position-based itinerary. Optionally pass a parallel list of AttractorInfo objects that map to each position
    /// to enable capacity checks. If positionAttractorInfos is null, capacity checks are disabled for positions.
    /// </summary>
    public void InitializePositions(List<Vector3> positions, List<AttractorInfo> positionAttractorInfos, SpawnerManager mgr, float dwellSeconds)
    {
        if (positions == null) positions = new List<Vector3>();

        this.itineraryPositions = new List<Vector3>(positions);
        this.itineraryPositionAttractorInfos = positionAttractorInfos != null ? new List<AttractorInfo>(positionAttractorInfos) : null;
        this.itineraryTransforms = null;
        this.usingPositionItinerary = true;

        this.manager = mgr;
        this.dwellTime = dwellSeconds;
        this.currentIndex = 0;
        this.state = State.Moving;

        // Additive: clear any stale per-leg override (unused on the position path, cleared for safety).
        this.currentLegStopOverride = null;

        if (moveToTarget != null)
        {
            moveToTarget.startTransform = null;
            moveToTarget.startPosition = transform.position;
            try { moveToTarget.SetMoveSpeed(moveToTarget.speed); } catch (Exception) { /* ignore */ }
        }

        // If this agent has an UncertainBehavior (pooled reuse case), ensure it is resumed
        // so uncertain behavior will run for the entire new itinerary.
        if (uncertainBehavior != null)
            uncertainBehavior.ResumeAfterArrival();

        // NEW (fix for pooled-agent reactivation race): see Initialize() above for the full explanation.
        BeginMovementWhenReady();
    }

    void Update()
    {
        // Only perform arrival detection while moving
        if (state != State.Moving)
            return;

        // If we are using the MoveToTarget.OnArrived event we may have already handled arrival.
        // Keep polling logic as a fallback for robustness.
        if (usingPositionItinerary)
        {
            if (itineraryPositions == null || currentIndex >= itineraryPositions.Count) return;

            Vector3 curPosTarget = itineraryPositions[currentIndex];

            // Primary arrival check is delegated to MoveToTarget when available (we subscribe to OnArrived).
            if (moveToTarget != null)
            {
                if (moveToTarget.IsArrived)
                {
                    OnReachedWaypoint();
                    return;
                }

                // As a robustness check, also consider simple distance
                float simpleDist = Vector3.Distance(transform.position, curPosTarget);
                if (simpleDist <= arriveTolerance)
                {
                    OnReachedWaypoint();
                    return;
                }
            }
            else
            {
                // No MoveToTarget: fall back to simple distance-based arrival detection
                float d = Vector3.Distance(transform.position, curPosTarget);
                if (d <= arriveTolerance)
                {
                    OnReachedWaypoint();
                    return;
                }
            }
        }
        else
        {
            // Transform-based itinerary
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
    }

    /// <summary>
    /// NEW (fix for pooled-agent reactivation race): starts (or restarts) the coroutine that waits for
    /// MoveToTarget to report readiness before issuing the first MoveToCurrent() command for this itinerary.
    /// Cancels any previous pending wait first, in case Initialize()/InitializePositions() is called again
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
            // always calls go.SetActive(true) before Initialize()/InitializePositions(), so this should
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
    /// SpawnerManager.SpawnAgent() calls PedestrianAgent.Initialize()/InitializePositions() in the same
    /// frame as go.SetActive(true). Previously, MoveToCurrent() (-> MoveToTarget.MoveTo(...)) was invoked
    /// synchronously right there, so it could run before MoveToTarget.IsReadyToMove() was true; MoveTo(...)
    /// would then silently skip calling agent.SetDestination(...) (only logging a warning), leaving the
    /// agent internally marked as 'moving' but with no actual NavMesh destination - an intermittently
    /// "frozen" NPC that is more likely to surface under load (many simultaneous pool reuses) and on Quest,
    /// where coroutine scheduling latency is more variable than in the Editor.
    ///
    /// Fix: poll MoveToTarget.IsReadyToMove() once per frame and only call MoveToCurrent() once the mover
    /// reports it can actually accept a destination, or once 'moveReadyPollTimeout' seconds have elapsed
    /// (whichever comes first). The timeout ensures an agent is never blocked forever waiting - if the
    /// mover genuinely cannot get onto the NavMesh, MoveToTarget's own existing defensive logging/disable
    /// logic still applies exactly as before.
    /// </summary>
    IEnumerator WaitUntilMoverReadyThenMove()
    {
        if (moveToTarget == null)
        {
            // No MoveToTarget on this prefab: nothing to wait for. Preserve the original fallback
            // behaviour described in Awake() (agent relies on external movement logic).
            MoveToCurrent();
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
                    "Proceeding with MoveToCurrent() anyway; MoveToTarget will log its own warning if the " +
                    "command cannot be applied.");
                break;
            }
            yield return null;
        }

        MoveToCurrent();
        startMovementCoroutine = null;
    }

    /// <summary>
    /// MoveToCurrent: instruct MoveToTarget to move to the current waypoint.
    /// This method exclusively calls MoveToTarget.MoveTo(...).
    /// We subscribe to MoveToTarget.OnArrived to get immediate notification when the leg completes.
    /// </summary>
    void MoveToCurrent()
    {
        // Unsubscribe previous subscription if any (defensive).
        UnsubscribeFromMoveToArrived();

        // Clear any per-leg physical-destination override left over from the previous waypoint. It is
        // re-set below ONLY when the new waypoint is an Attractor Plane with an AttractorDwell component.
        currentLegStopOverride = null;

        if (usingPositionItinerary)
        {
            if (itineraryPositions == null || currentIndex >= itineraryPositions.Count)
            {
                Finish();
                return;
            }

            Vector3 posTarget = itineraryPositions[currentIndex];

            if (moveToTarget != null)
            {
                // configure MoveToTarget for a position-based leg
                moveToTarget.startTransform = null;
                moveToTarget.startPosition = transform.position;
                moveToTarget.targetTransform = null;
                moveToTarget.targetPosition = posTarget;

                // Subscribe to OnArrived so we react immediately on arrival
                SubscribeToMoveToArrived();

                // Tell MoveToTarget to perform the move (it handles its own internal arrival detection)
                moveToTarget.MoveTo(posTarget);
            }
            else
            {
                // No MoveToTarget present: nothing to command. Rely on external movement or distance checks.
            }
        }
        else
        {
            // Transform-based itinerary
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
    /// For position-based itinerary, capacity handling only occurs if itineraryPositionAttractorInfos was supplied.
    /// </summary>
    void OnReachedWaypoint()
    {
        // Defensive unsubscribe to ensure no stale subscriptions remain
        UnsubscribeFromMoveToArrived();

        if (usingPositionItinerary)
        {
            if (itineraryPositions == null || currentIndex >= itineraryPositions.Count) return;
            bool isExit = (currentIndex == itineraryPositions.Count - 1);

            if (!isExit)
            {
                AttractorInfo info = null;
                if (itineraryPositionAttractorInfos != null && currentIndex < itineraryPositionAttractorInfos.Count)
                    info = itineraryPositionAttractorInfos[currentIndex];

                if (info != null && manager != null)
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
                    // no capacity semantics for this position -> simple dwell then advance
                    StartCoroutine(SimpleDwellThenAdvance());
                    return;
                }
            }
            else
            {
                Finish();
            }
        }
        else
        {
            // transform-based itinerary
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
        if ((usingPositionItinerary && (itineraryPositions == null || currentIndex >= itineraryPositions.Count)) ||
            (!usingPositionItinerary && (itineraryTransforms == null || currentIndex >= itineraryTransforms.Count)))
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
    /// then stops movement and notifies listeners.
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
    /// Called from Finish/ForceFinish to avoid leaking a claimed slot.
    /// </summary>
    void ReleaseCurrentAttractorIfAny()
    {
        if (manager == null) return;

        if (usingPositionItinerary)
        {
            if (itineraryPositionAttractorInfos != null && currentIndex < itineraryPositionAttractorInfos.Count)
            {
                var info = itineraryPositionAttractorInfos[currentIndex];
                if (info != null) manager.ReleaseAttractor(info, this);
            }
        }
        else
        {
            if (itineraryTransforms != null && currentIndex < itineraryTransforms.Count)
            {
                var t = itineraryTransforms[currentIndex];
                var info = manager.FindAttractorInfo(t);
                if (info != null) manager.ReleaseAttractor(info, this);
            }
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
    }

    void OnDestroy()
    {
        UnsubscribeFromMoveToArrived();

        if (moveToTarget != null)
            moveToTarget.Stop();
    }
}