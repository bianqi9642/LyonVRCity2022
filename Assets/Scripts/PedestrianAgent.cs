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
    enum State { Moving, WaitingAtAttractor, Finished }

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

    // -----------------------------
    // Unique ID implementation
    // -----------------------------
    static long s_globalAgentCounter = 0;
    [Tooltip("Read-only unique ID assigned to this agent instance at Awake.")]
    [SerializeField] string agentID = null;
    public string AgentID => agentID;

    // Keep track of whether we've subscribed to the current MoveToTarget.OnArrived
    bool subscribedToMoveToArrived = false;

    // Cached reference to UncertainBehavior (if present) so we can notify it to resume after dwell or on reuse
    UncertainBehavior uncertainBehavior = null;

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

        // Start moving to first waypoint (if any)
        MoveToCurrent();
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

        MoveToCurrent();
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

            if (moveToTarget != null)
            {
                if (moveToTarget.IsArrived)
                {
                    OnReachedWaypoint();
                    return;
                }

                // Additional distance fallback
                float d = Vector3.Distance(transform.position, t.position);
                if (d <= arriveTolerance)
                {
                    OnReachedWaypoint();
                    return;
                }
            }
            else
            {
                float d = Vector3.Distance(transform.position, t.position);
                if (d <= arriveTolerance)
                {
                    OnReachedWaypoint();
                    return;
                }
            }
        }
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
                moveToTarget.targetTransform = target;

                // Subscribe to OnArrived for immediate reaction
                SubscribeToMoveToArrived();

                // MoveToTarget will handle movement and arrival
                moveToTarget.MoveTo(target);
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
    }

    void OnDestroy()
    {
        UnsubscribeFromMoveToArrived();

        if (moveToTarget != null)
            moveToTarget.Stop();
    }
}
