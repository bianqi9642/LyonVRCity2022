// UncertainBehavior.cs
// Attach to an NPC GameObject that has MoveToTarget (preferred) or a NavMeshAgent.
// Implements uncertain walking: pauses, hesitates (in-place turns), slow-walk, and short detours.
// New behavior: when the agent reaches its main target, uncertain behavior is suspended/stopped.
// - If a MoveToTarget is assigned, we listen to its OnArrived event (main arrival).
// - If only a NavMeshAgent is used, we poll remainingDistance to detect arrival.
//
// All comments and logs are in English.

using System;
using UnityEngine;
using UnityEngine.AI;

public class UncertainBehavior : MonoBehaviour
{
    [Header("References (one recommended)")]
    [Tooltip("Preferred: assign the MoveToTarget component on this GameObject.")]
    public MoveToTarget moveController; // optional, preferred control API
    [Tooltip("Fallback: assign NavMeshAgent if MoveToTarget is not used.")]
    public NavMeshAgent agent;          // fallback control if moveController null

    [Header("Probabilities (per second)")]
    [Tooltip("Chance per second to start a pause")]
    public float pauseProbPerSec = 0.02f;
    [Tooltip("Chance per second to start a hesitate (in-place turn)")]
    public float hesitateProbPerSec = 0.04f;
    [Tooltip("Chance per second to start a slow-walk")]
    public float slowProbPerSec = 0.06f;
    [Tooltip("Chance per second to start a short detour")]
    public float detourProbPerSec = 0.03f;

    [Header("Durations / magnitudes")]
    public float pauseMin = 0.8f;
    public float pauseMax = 2.0f;
    public float hesitateMin = 0.5f;
    public float hesitateMax = 1.2f;
    public float slowMin = 0.8f;
    public float slowMax = 2.0f;
    [Tooltip("Speed multiplier during slow-walk")]
    public float slowFactor = 0.5f;

    [Header("Detour settings")]
    [Tooltip("Radius (m) used to sample a short detour target")]
    public float detourRadius = 1.5f;
    [Tooltip("Fallback detour duration if arrival detection is unreliable")]
    public float detourDuration = 1.0f;
    [Tooltip("Minimum horizontal distance (m) between current position and sampled detour to accept it")]
    public float minDetourDistance = 0.6f;
    [Tooltip("Maximum number of times to re-issue MoveTo on a suspected false arrival before falling back")]
    public int detourArrivalMaxRetries = 3;
    [Tooltip("Distance (m) used to verify that the visible transform is near the detour target before accepting arrival")]
    public float detourVisualThreshold = 0.45f;
    [Tooltip("Maximum sample attempts to find a reachable/navmesh-backed detour")]
    public int detourSampleMaxAttempts = 8;
    [Tooltip("NavMesh sampling radius used when trying to snap candidate to NavMesh (usually similar to detourRadius)")]
    public float detourNavMeshSampleDistance = 2.0f;

    [Header("Turn settings")]
    [Tooltip("Angular speed (deg/s) when doing in-place turn")]
    public float turnSpeedDeg = 120f;

    [Header("Arrival detection (when MoveToTarget is NOT used)")]
    [Tooltip("Threshold (m) used when polling NavMeshAgent.remainingDistance to declare arrival if MoveToTarget is not present.")]
    public float arrivalStopThreshold = 0.22f;

    [Header("Logging")]
    [Tooltip("Enable logging of uncertain behavior state transitions")]
    public bool enableLogging = true;

    // Internal state
    enum State { Walking, Pause, Hesitate, Slow, Detour }
    State state = State.Walking;

    float stateTimer = 0f;
    Vector3 detourTarget;
    float originalSpeed = 1f;

    // For logging durations
    float stateStartTime = 0f;

    // Keep the main target so we can resume it after the detour finishes
    Vector3 mainTargetOnDetourStart;
    bool haveSavedMainTarget = false;

    // For detour arrival verification retries
    int detourArrivalRetryCount = 0;

    // cached handler so we can unsubscribe cleanly (detour-specific handler)
    Action onMoveToArrivedHandler = null;

    // cached handler for main-target arrival detection when using MoveToTarget
    Action onMainArrivalHandler = null;

    // Destination compare threshold to avoid float-equality problems
    const float k_DestCompareThreshold = 0.1f;

    // Flag: when true, uncertain behavior is suspended because we've arrived at the main target
    bool suspendedBecauseArrived = false;

    void Start()
    {
            if (moveController == null)
                Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] moveController is NULL at Start. moveController component present on same GO? getcomponent = {GetComponent<MoveToTarget>() != null}");
            else
                Debug.Log($"[UncertainBehavior] [{gameObject.name}] moveController assigned at Start -> {moveController.name}");

        // Auto-assign agent if none provided
        if (moveController == null && agent == null)
            agent = GetComponent<NavMeshAgent>();

        // Save original speed (from MoveToTarget or agent)
        if (moveController != null)
        {
            originalSpeed = moveController.speed;
            // If MoveToTarget wraps a NavMeshAgent, prefer that runtime speed as authoritative
            if (moveController.agent != null)
                originalSpeed = moveController.agent.speed;
        }
        else if (agent != null)
        {
            originalSpeed = agent.speed;
        }

        // If we have a MoveToTarget, subscribe to its OnArrived to detect arrival at the main target.
        // We keep a separate handler (onMainArrivalHandler) to distinguish from the detour-specific handler.
        if (moveController != null)
        {
            onMainArrivalHandler = () => OnMainArrivedFromMoveController();
            moveController.OnArrived += onMainArrivalHandler;
        }
    }

    /// <summary>
    /// Resets the "normal walking speed" baseline that EnterSlow() scales down from and ExitCurrentState()
    /// restores to. Must be called every time this (possibly pooled) instance is spawned/reused with a
    /// (possibly new) AgentProfile - Start() only runs once per GameObject lifetime, so it cannot detect a
    /// later pool reuse on its own. Calling this does not touch the current state machine, isStopped,
    /// NavMeshAgent, or MoveToTarget - it only updates the internal baseline used the NEXT time a
    /// Slow/Detour state starts or ends.
    /// </summary>
    public void ResetBaselineSpeed(float speed)
    {
        originalSpeed = speed;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // If we've been suspended due to arrival, do nothing — uncertain behavior is stopped.
        if (suspendedBecauseArrived)
            return;

        // If MoveToTarget is not present, but we have a NavMeshAgent, poll for main-target arrival.
        // This detects arrival and suspends uncertain behavior when using agent-only navigation.
        if (moveController == null && agent != null)
        {
            // Only evaluate arrival when agent has a finite remainingDistance and path is not pending.
            if (!agent.pathPending)
            {
                float rd = agent.remainingDistance;
                if (!float.IsInfinity(rd) && rd <= arrivalStopThreshold)
                {
                    // Agent has arrived at its destination -> suspend uncertain behavior.
                    SuspendBecauseArrived("agent.remainingDistance");
                    return;
                }
            }
        }

        // Normal state timer handling (only runs when not suspended)
        if (state != State.Walking)
        {
            stateTimer -= dt;
            if (stateTimer <= 0f)
            {
                LogStateExit(state);
                ExitCurrentState();
                state = State.Walking;
            }
        }
        else
        {
            // In walking state: sample random events (Poisson approximation)
            if (UnityEngine.Random.value < pauseProbPerSec * dt)
            {
                EnterPause();
                return;
            }
            if (UnityEngine.Random.value < hesitateProbPerSec * dt)
            {
                EnterHesitate();
                return;
            }
            if (UnityEngine.Random.value < slowProbPerSec * dt)
            {
                EnterSlow();
                return;
            }
            if (UnityEngine.Random.value < detourProbPerSec * dt)
            {
                EnterDetour();
                return;
            }
        }

        // Per-state runtime behavior
        switch (state)
        {
            case State.Hesitate:
                DoHesitate(dt);
                break;
            case State.Detour:
                // Detour flow is driven by MoveToTarget / agent and OnArrived verification.
                // We only use stateTimer as a fallback timeout; no manual movement here.
                break;
            // Pause and Slow are handled by PauseExternal/ResumeExternal or speed scaling; no per-frame action needed here.
        }
    }

    // ---- State entry functions ----

    void EnterPause()
    {
        state = State.Pause;
        stateTimer = UnityEngine.Random.Range(pauseMin, pauseMax);
        stateStartTime = Time.time;
        // Use PauseExternal so MoveToTarget won't treat this as 'arrived'
        if (moveController != null)
        {
            // Full halt + freeze animator
            moveController.PauseExternal(true, true);
        }
        else if (agent != null)
        {
            // Fallback: stop the agent directly
            agent.isStopped = true;
        }

        LogStateEnter(State.Pause, stateTimer);
    }

    void EnterHesitate()
    {
        state = State.Hesitate;
        stateTimer = UnityEngine.Random.Range(hesitateMin, hesitateMax);
        stateStartTime = Time.time;

        if (moveController != null)
        {
            // Hesitate uses a "soft pause": don't halt movement and don't freeze animator.
            moveController.PauseExternal(false, false);
        }
        else if (agent != null)
        {
            // For agent-only fallback, we intentionally do not stop the agent so small translational motion can happen.
            // If you prefer strict in-place turning for Hesitate, uncomment the next line:
            // agent.isStopped = true;
        }

        LogStateEnter(State.Hesitate, stateTimer);
    }

    void EnterSlow()
    {
        state = State.Slow;
        stateTimer = UnityEngine.Random.Range(slowMin, slowMax);
        stateStartTime = Time.time;

        // When MoveToTarget drives the NPC, ensure we set both its public speed field and its internal agent runtime speed.
        if (moveController != null)
        {
            moveController.speed = originalSpeed * slowFactor;
            if (moveController.agent != null)
                moveController.agent.speed = originalSpeed * slowFactor;
        }
        else if (agent != null)
        {
            agent.speed = originalSpeed * slowFactor;
        }

        LogStateEnter(State.Slow, stateTimer);
    }

    void EnterDetour()
    {
        state = State.Detour;

        // Try to sample a detour target that:
        //  - snaps to the NavMesh (NavMesh.SamplePosition)
        //  - is meaningfully away from the current horizontal position
        //  - is reachable from current position (NavMesh.CalculatePath PathComplete)
        bool found = false;
        NavMeshHit chosenHit = new NavMeshHit();

        for (int attempt = 0; attempt < detourSampleMaxAttempts; attempt++)
        {
            // sample a random candidate within detourRadius
            Vector2 rnd = UnityEngine.Random.insideUnitCircle * detourRadius;
            Vector3 candidate = transform.position + new Vector3(rnd.x, 0f, rnd.y);

            // snap candidate to NavMesh within detourNavMeshSampleDistance
            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, detourNavMeshSampleDistance, NavMesh.AllAreas))
            {
                // ensure horizontal distance is meaningful
                float horizDist = Vector2.Distance(new Vector2(transform.position.x, transform.position.z),
                                                   new Vector2(hit.position.x, hit.position.z));
                if (horizDist < Mathf.Max(minDetourDistance, 0.01f))
                {
                    // too close; try again
                    continue;
                }

                // verify reachability with a full path calculation from current position to hit.position
                NavMeshPath testPath = new NavMeshPath();
                Vector3 startPos = transform.position;
                // If we have a NavMeshAgent and updatePosition==false, it's safer to use agent.nextPosition as start for path calc.
                if (agent != null)
                    startPos = agent.nextPosition;

                bool pathOk = NavMesh.CalculatePath(startPos, hit.position, NavMesh.AllAreas, testPath);
                if (!pathOk || testPath.status != NavMeshPathStatus.PathComplete || testPath.corners.Length == 0)
                {
                    // unreachable or incomplete path; try again
                    continue;
                }

                // Found a valid detour on the NavMesh and reachable
                chosenHit = hit;
                found = true;
                break;
            }
            // else sampling failed; try again
        }

        if (!found)
        {
            // fallback: short pause instead of detour
            state = State.Pause;
            stateTimer = UnityEngine.Random.Range(pauseMin, pauseMax);
            stateStartTime = Time.time;
            if (moveController != null)
                moveController.PauseExternal(true, true);
            else if (agent != null)
                agent.isStopped = true;

            LogStateEnter(State.Pause, stateTimer);
            return;
        }

        // Save main target so we can resume it after detour
        Vector3 mainTarget = GetCurrentMainTarget();
        mainTargetOnDetourStart = mainTarget;
        haveSavedMainTarget = true;
        detourArrivalRetryCount = 0;

        // Set the chosen (navmesh-backed + reachable) detour target
        detourTarget = chosenHit.position;

        if (moveController != null)
        {
            // Ensure MoveToTarget is active (resume external control if needed)
            moveController.ResumeExternal();

            // Subscribe to MoveToTarget.OnArrived to verify arrival. Cache the handler for unsubscribe.
            // Note: this is a detour-specific handler; OnMainArrivedFromMoveController (subscribed at Start)
            // will ignore events while state==Detour so we don't suspend on detour completion.
            onMoveToArrivedHandler = () => OnMoveToArrivedForDetour();
            moveController.OnArrived += onMoveToArrivedHandler;

            // Ask MoveToTarget to navigate to detour target
            moveController.MoveTo(detourTarget);
        }
        else if (agent != null)
        {
            // Fallback: use the agent to go to detour
            agent.isStopped = false;
            agent.SetDestination(detourTarget);
        }

        // fallback timer so detour doesn't run forever if something blocks movement visually
        stateTimer = detourDuration;
        stateStartTime = Time.time;

        LogStateEnter(State.Detour, stateTimer);
    }

    // Called when MoveToTarget.OnArrived fires (only subscribed during a detour).
    void OnMoveToArrivedForDetour()
    {
        // Only handle if we're currently in Detour state
        if (state != State.Detour) return;

        // Verify visible transform proximity to the detourTarget
        float visDist = Vector3.Distance(transform.position, detourTarget);

        if (visDist <= detourVisualThreshold)
        {
            // legitimate arrival: mark detour complete by expiring the timer (Exit will handle resuming main target)
            if (enableLogging) Debug.Log($"[UncertainBehavior] [{gameObject.name}] MoveToTarget reported arrival and transform is within {visDist:F3}m -> accept detour completion.");
            // trigger exit next frame
            stateTimer = 0f;
            return;
        }
        else
        {
            // False arrival: MoveToTarget thinks it's arrived but the transform is still far away visually.
            detourArrivalRetryCount++;

            if (enableLogging)
            {
                Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] Detour false-arrival detected. transform->detourTarget = {visDist:F3}m. Retry {detourArrivalRetryCount}/{detourArrivalMaxRetries}.");
            }

            if (detourArrivalRetryCount <= detourArrivalMaxRetries)
            {
                // Re-issue MoveToTarget to the same detourTarget to encourage actual movement.
                if (moveController != null)
                {
                    moveController.MoveTo(detourTarget);
                }
                else if (agent != null)
                {
                    agent.SetDestination(detourTarget);
                }

                // extend a little time so we give it another chance
                stateTimer = Mathf.Max(0.5f, stateTimer);
                return;
            }
            else
            {
                // Retries exhausted: fallback to a short pause to avoid locking up.
                if (enableLogging) Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] Detour retries exhausted; falling back to Pause.");
                // Unsubscribe from OnArrived (cleanup will happen in ExitCurrentState)
                if (moveController != null && onMoveToArrivedHandler != null)
                {
                    moveController.OnArrived -= onMoveToArrivedHandler;
                    onMoveToArrivedHandler = null;
                }

                state = State.Pause;
                stateTimer = UnityEngine.Random.Range(pauseMin, pauseMax);
                stateStartTime = Time.time;

                if (moveController != null)
                {
                    moveController.PauseExternal(true, true);
                }
                else if (agent != null)
                {
                    agent.isStopped = true;
                }

                LogStateEnter(State.Pause, stateTimer);
                return;
            }
        }
    }

    // ---- State exit / restore ----

    void ExitCurrentState()
    {
        // Called when a non-walking state expires and we should restore normal walking behavior.

        // If we were in a detour and subscribed to MoveToTarget.OnArrived, unsubscribe now.
        if (moveController != null && onMoveToArrivedHandler != null)
        {
            moveController.OnArrived -= onMoveToArrivedHandler;
            onMoveToArrivedHandler = null;
        }

        // Restore speed and resume movement. Also re-issue main target so agent resumes toward it.
        Vector3 mainTarget = GetCurrentMainTarget();

        // If we saved a main target at detour start, prefer that (ensures we resume original goal).
        if (haveSavedMainTarget)
        {
            mainTarget = mainTargetOnDetourStart;
            haveSavedMainTarget = false;
        }

        if (moveController != null)
        {
            // restore speed (ensure runtime agent speed if present matches)
            moveController.speed = originalSpeed;
            if (moveController.agent != null)
                moveController.agent.speed = originalSpeed;

            // resume external control
            moveController.ResumeExternal();

            // re-assert main target to ensure MoveToTarget will set destination/path again
            // Avoid re-issuing MoveTo if already heading to roughly the same target (distance threshold).
            Vector3 currentTarget = moveController.targetTransform != null ? moveController.targetTransform.position : moveController.targetPosition;
            if (Vector3.Distance(currentTarget, mainTarget) > k_DestCompareThreshold)
            {
                moveController.MoveTo(mainTarget);
            }
        }
        else if (agent != null)
        {
            // restore speed and resume agent
            agent.speed = originalSpeed;
            agent.isStopped = false;

            // re-assign destination if it's meaningfully different from current
            Vector3 agentDest = agent.destination;
            if (float.IsNaN(agentDest.x) || float.IsNaN(agentDest.y) || float.IsNaN(agentDest.z) ||
                Vector3.Distance(agentDest, mainTarget) > k_DestCompareThreshold)
            {
                agent.SetDestination(mainTarget);
            }

            // ensure agent internal nextPosition synced to transform to avoid a large warp
            agent.nextPosition = transform.position;
        }

        // Reset detour retry counter
        detourArrivalRetryCount = 0;
    }

    // Helper to determine the main target (the goal that MoveToTarget or agent should go to after interruptions)
    Vector3 GetCurrentMainTarget()
    {
        if (moveController != null)
        {
            // MoveToTarget exposes public targetTransform/targetPosition fields in earlier code
            return moveController.targetTransform != null ? moveController.targetTransform.position : moveController.targetPosition;
        }
        else if (agent != null)
        {
            return agent.destination;
        }
        return transform.position;
    }

    // ---- Per-frame behaviors for certain states ----

    void DoHesitate(float dt)
    {
        // Rotate in place with small jitter to simulate looking around
        float ang = turnSpeedDeg * dt;
        float dir = (UnityEngine.Random.value > 0.5f) ? 1f : -1f;
        transform.Rotate(Vector3.up, dir * ang);
    }

    // ---- External triggers ----

    public void TriggerHesitateNow(float duration = -1f)
    {
        EnterHesitate();
        if (duration > 0f) stateTimer = duration;
    }

    public void TriggerPauseNow(float duration = -1f)
    {
        EnterPause();
        if (duration > 0f) stateTimer = duration;
    }

    // ---- Arrival handling helpers ----

    // Called when MoveToTarget.OnArrived fires (subscribed in Start()).
    // We treat this as the main-target arrival signal, but only if we are not currently performing a detour.
    void OnMainArrivedFromMoveController()
    {
        // If we're in Detour state, this event is likely for the detour completion; ignore it.
        if (state == State.Detour)
            return;

        // Otherwise, suspend uncertain behavior
        SuspendBecauseArrived("MoveToTarget.OnArrived");
    }

    // Centralized suspend logic when arrival at main target is detected.
    void SuspendBecauseArrived(string reason)
    {
        if (suspendedBecauseArrived) return;

        if (enableLogging) Debug.Log($"[UncertainBehavior] [{gameObject.name}] Suspending uncertain behavior due to arrival ({reason}).");

        // Cleanly exit any current state (unsubscribes detour handlers, restores speed, etc.)
        try
        {
            ExitCurrentState();
        }
        catch (Exception ex)
        {
            if (enableLogging) Debug.LogWarning($"[UncertainBehavior] Error while exiting state during suspend: {ex.Message}");
        }

        // Unsubscribe the detour handler if set
        if (moveController != null && onMoveToArrivedHandler != null)
        {
            moveController.OnArrived -= onMoveToArrivedHandler;
            onMoveToArrivedHandler = null;
        }

        // Mark as suspended so Update() will early-out and no new states will be entered.
        suspendedBecauseArrived = true;

        // Optionally, unsubscribe the main arrival handler as well to avoid future calls.
        if (moveController != null && onMainArrivalHandler != null)
        {
            moveController.OnArrived -= onMainArrivalHandler;
            onMainArrivalHandler = null;
        }
    }

    // Public API to resume uncertain behavior if you want to re-enable it after arrival (not automatic).
    public void ResumeAfterArrival()
    {
        if (!suspendedBecauseArrived) return;
        if (enableLogging) Debug.Log($"[UncertainBehavior] [{gameObject.name}] Resuming uncertain behavior after prior arrival.");

        suspendedBecauseArrived = false;

        // Re-subscribe main arrival handler if we have a MoveToTarget
        if (moveController != null && onMainArrivalHandler == null)
        {
            onMainArrivalHandler = () => OnMainArrivedFromMoveController();
            moveController.OnArrived += onMainArrivalHandler;
        }
    }

    // ---- Logging helpers ----

    void LogStateEnter(State s, float plannedDuration)
    {
        if (!enableLogging) return;
        string namePrefix = GetLogPrefix();
        Debug.Log($"{namePrefix} ENTER {s} (planned {plannedDuration:F2}s) at t={Time.time:F2}");
    }

    void LogStateExit(State s)
    {
        if (!enableLogging) return;
        float duration = Time.time - stateStartTime;
        string namePrefix = GetLogPrefix();
        Debug.Log($"{namePrefix} EXIT {s} (actual {duration:F2}s) at t={Time.time:F2}");
    }

    string GetLogPrefix()
    {
        // include GameObject name and rounded position for easier debugging
        Vector3 p = transform.position;
        return $"[UncertainBehavior] [{gameObject.name}] pos=({p.x:F2},{p.y:F2},{p.z:F2})";
    }

    void OnDisable()
    {
        // Ensure we clean up subscription if object disabled unexpectedly
        if (moveController != null && onMoveToArrivedHandler != null)
        {
            moveController.OnArrived -= onMoveToArrivedHandler;
            onMoveToArrivedHandler = null;
        }

        if (moveController != null && onMainArrivalHandler != null)
        {
            moveController.OnArrived -= onMainArrivalHandler;
            onMainArrivalHandler = null;
        }
    }
}