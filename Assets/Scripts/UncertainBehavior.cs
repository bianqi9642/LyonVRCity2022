// UncertainBehavior.cs
// Attach to an NPC GameObject alongside its MoveToTarget component.
// Implements uncertain walking: pauses, hesitates (in-place turns), slow-walk, and short detours.
// Stage 4 (Option A movement-architecture migration): MoveToTarget is now the ONLY component that may
// touch NavMeshAgent for movement control (SetDestination/isStopped/speed). UncertainBehavior expresses
// behavioural intent exclusively through MoveToTarget's public API (PauseExternal/ResumeExternal/MoveTo/
// SetTemporarySpeed/RestoreNormalSpeed/OnArrived). The 'agent' field is retained only as a read-only NavMesh
// helper reference for EnterDetour()'s path-planning sample/verify step - it is never used to issue a
// movement command.
// When the agent reaches its main target (via MoveToTarget.OnArrived), uncertain behavior is suspended/stopped.
//
// All comments and logs are in English.

using System;
using UnityEngine;
using UnityEngine.AI;

public class UncertainBehavior : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Required: the MoveToTarget component on this GameObject. All movement (pause/resume/detour/" +
             "temporary speed) is expressed through this reference; there is no NavMeshAgent-direct fallback.")]
    public MoveToTarget moveController;
    [Tooltip("Read-only helper (NOT used for movement control): only EnterDetour() reads agent.nextPosition " +
             "here for NavMesh path-planning. Movement is always routed through moveController (MoveToTarget).")]
    public NavMeshAgent agent;

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

    [Header("Turn settings (legacy - no longer read)")]
    [Tooltip("No longer used: Hesitate used to rotate the ROOT by +/- this many deg/s with a new random sign " +
             "every frame (rapid shaking, and it fought MoveToTarget's facing). Hesitate is now a smooth, " +
             "target-based head look-around - see 'Look-around (Hesitate)'. Kept only so this Inspector value " +
             "is not silently dropped from the prefab.")]
    public float turnSpeedDeg = 120f;

    [Header("Look-around (Hesitate)")]
    [Tooltip("If false, Hesitate is a plain soft pause with no look-around motion.")]
    public bool lookAroundEnabled = true;
    [Tooltip("Minimum yaw (deg) of a look target away from the walking direction.")]
    public float lookAngleMin = 25f;
    [Tooltip("Maximum yaw (deg) of a look target away from the walking direction.")]
    public float lookAngleMax = 45f;
    [Tooltip("Minimum / maximum time (s) of one turn to a look target. Turns across the forward direction " +
             "(left <-> right) are stretched up to 1.6x so the angular speed stays gentle.")]
    public float lookTurnDurationMin = 0.5f;
    public float lookTurnDurationMax = 1.2f;
    [Tooltip("Minimum / maximum time (s) the gaze is held on a look target before turning to the other side.")]
    public float lookHoldMin = 0.4f;
    public float lookHoldMax = 1.0f;
    [Tooltip("Time (s) to ease the head back to the walking direction after Hesitate ends.")]
    public float lookReturnDuration = 0.7f;
    [Tooltip("A full look-around (left, hold, right, hold) needs more time than hesitateMin/Max (0.5-1.2 s on the " +
             "current prefab). Hesitate is only a soft pause - the NPC keeps walking - so its duration is raised " +
             "to at least this value when look-around is active. Set to 0 to keep hesitateMin/Max unchanged " +
             "(the look-around is then simply cut short and eased back).")]
    public float hesitateLookAroundMinDuration = 3.0f;

    [Header("Arrival detection (legacy - no longer read)")]
    [Tooltip("No longer used after Stage 4: arrival is now detected exclusively via MoveToTarget.OnArrived. " +
             "Kept only so this Inspector value is not silently dropped from the prefab.")]
    public float arrivalStopThreshold = 0.22f;

    [Header("Logging")]
    [Tooltip("Enable logging of uncertain behavior state transitions")]
    public bool enableLogging = true;

    // Internal state
    enum State { Walking, Pause, Hesitate, Slow, Detour }
    State state = State.Walking;

    float stateTimer = 0f;
    Vector3 detourTarget;

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

    // ---- Look-around (Hesitate) runtime state ----
    // The look-around only offsets the yaw of the head/neck bone in LateUpdate (on top of the animated pose).
    // The root Transform is never rotated here, so MoveToTarget stays the sole writer of root rotation.
    enum LookPhase { Idle, Turning, Holding, Returning }
    LookPhase lookPhase = LookPhase.Idle;
    Transform lookBone;                 // head/neck bone, resolved lazily
    bool lookBoneResolved = false;
    float lookYaw;                      // current yaw offset in degrees (+ = right, - = left)
    float lookYawVelocity;              // SmoothDamp state (keeps angular velocity continuous across targets)
    float lookTargetYaw;                // current look target, stable for the whole turn + hold
    float lookSmoothTime;               // SmoothDamp time derived from the chosen turn duration
    float lookPhaseTimer;               // time left in the current Turning / Holding phase
    float lookSide = 1f;                // side of the current target (+1 right, -1 left)
    const float k_LookReferenceArc = 40f;
    const float k_LookSmoothDampFactor = 2.4f; // critically damped: ~95% of the way after 2.4 * smoothTime

    void Start()
    {
            if (moveController == null)
                Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] moveController is NULL at Start. moveController component present on same GO? getcomponent = {GetComponent<MoveToTarget>() != null}");
            else
                Debug.Log($"[UncertainBehavior] [{gameObject.name}] moveController assigned at Start -> {moveController.name}");

        // Auto-assign the read-only NavMeshAgent helper reference if none provided. Movement itself is
        // always routed through moveController (MoveToTarget); 'agent' is used only for the nextPosition
        // read in EnterDetour()'s path-planning step.
        if (agent == null)
            agent = GetComponent<NavMeshAgent>();

        // Subscribe to MoveToTarget's OnArrived to detect arrival at the main target.
        // Guarded by 'onMainArrivalHandler == null' (Stage 4): ResetForReuse() can already have subscribed
        // this handler by the time Start() runs (Start() is deferred by Unity, while PedestrianAgent.Initialize()
        // -> ResetForReuse() can run synchronously right after Instantiate()/SetActive(true)), so this guard
        // prevents a duplicate subscription on an NPC's very first activation.
        if (moveController != null && onMainArrivalHandler == null)
        {
            onMainArrivalHandler = () => OnMainArrivedFromMoveController();
            moveController.OnArrived += onMainArrivalHandler;
        }
    }

    /// <summary>
    /// Stage 4: full per-life runtime reset for pool reuse / a fresh spawn. Called once by
    /// PedestrianAgent.Initialize() before the new itinerary begins - NOT after every dwell (see
    /// ResumeAfterArrival() for that lighter-weight case, still used by the dwell coroutines). Clears every
    /// piece of state that belongs to the PREVIOUS lifetime so a reused NPC always starts this life in a
    /// clean Walking state, with no leftover behavioural state, saved detour target, retry count, or stale
    /// event subscription. Does not touch configuration values (probabilities/durations/factors/references) -
    /// only runtime state. Does not need to reset any "temporary speed" state because Stage 4 no longer
    /// stores that here at all - the normal-speed baseline lives in MoveToTarget.speed, freshly set every
    /// spawn/reuse by AgentFactory.ApplyProfileToInstance() -> MoveToTarget.SetNormalSpeed().
    /// </summary>
    public void ResetForReuse()
    {
        // Defensive unsubscribe of a detour handler left over from a previous life (e.g. if the object was
        // pooled mid-Detour without a clean ExitCurrentState() pass).
        if (moveController != null && onMoveToArrivedHandler != null)
        {
            moveController.OnArrived -= onMoveToArrivedHandler;
            onMoveToArrivedHandler = null;
        }

        state = State.Walking;
        stateTimer = 0f;
        detourArrivalRetryCount = 0;
        haveSavedMainTarget = false;
        mainTargetOnDetourStart = Vector3.zero;
        suspendedBecauseArrived = false;
        ResetLookAroundImmediate();

        // Re-subscribe the main-arrival handler for this life (idempotent: only subscribes if not already).
        if (moveController != null && onMainArrivalHandler == null)
        {
            onMainArrivalHandler = () => OnMainArrivedFromMoveController();
            moveController.OnArrived += onMainArrivalHandler;
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // If we've been suspended due to arrival, do nothing — uncertain behavior is stopped.
        if (suspendedBecauseArrived)
            return;

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
                // Detour flow is driven by MoveToTarget and OnArrived verification.
                // We only use stateTimer as a fallback timeout; no manual movement here.
                break;
            // Pause and Slow are handled by PauseExternal/ResumeExternal or speed scaling; no per-frame action needed here.
        }
    }

    // ---- State entry functions ----

    void EnterPause()
    {
        EndLookAround(); // safety: external Trigger*Now() can enter a state without leaving Hesitate first
        state = State.Pause;
        stateTimer = UnityEngine.Random.Range(pauseMin, pauseMax);
        stateStartTime = Time.time;

        // Use PauseExternal so MoveToTarget won't treat this as 'arrived'
        if (moveController != null)
        {
            // Full halt + freeze animator
            moveController.PauseExternal(true, true);
        }
        else if (enableLogging)
        {
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] EnterPause: moveController is null; cannot pause movement.");
        }

        LogStateEnter(State.Pause, stateTimer);
    }

    void EnterHesitate()
    {
        state = State.Hesitate;
        stateTimer = UnityEngine.Random.Range(hesitateMin, hesitateMax);
        stateStartTime = Time.time;

        // Start the smooth look-around. Only when it can actually run (a head bone exists) is the soft-pause
        // duration raised to fit a full left/hold/right/hold sequence.
        if (BeginLookAround() && hesitateLookAroundMinDuration > stateTimer)
            stateTimer = hesitateLookAroundMinDuration;

        if (moveController != null)
        {
            // Hesitate uses a "soft pause": don't halt movement and don't freeze animator.
            moveController.PauseExternal(false, false);
        }
        else if (enableLogging)
        {
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] EnterHesitate: moveController is null; cannot apply soft pause.");
        }

        LogStateEnter(State.Hesitate, stateTimer);
    }

    void EnterSlow()
    {
        EndLookAround();
        state = State.Slow;
        stateTimer = UnityEngine.Random.Range(slowMin, slowMax);
        stateStartTime = Time.time;

        if (moveController != null)
        {
            // moveController.speed is MoveToTarget's own normal-walking baseline (kept current by
            // AgentFactory.SetNormalSpeed() every spawn/reuse) - read it live rather than caching a copy.
            moveController.SetTemporarySpeed(moveController.speed * slowFactor);
        }
        else if (enableLogging)
        {
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] EnterSlow: moveController is null; cannot reduce speed.");
        }

        LogStateEnter(State.Slow, stateTimer);
    }

    void EnterDetour()
    {
        EndLookAround();
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
                // Read-only NavMesh planning helper: prefer the agent's own nextPosition as the path-calc
                // start when available. This is an informational read only - it never issues a movement
                // command against the NavMeshAgent.
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
            else if (enableLogging)
                Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] EnterDetour: no valid detour point found and moveController is null; cannot pause movement.");

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
        else if (enableLogging)
        {
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] EnterDetour: moveController is null; cannot move to detour target.");
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

                LogStateEnter(State.Pause, stateTimer);
                return;
            }
        }
    }

    // ---- State exit / restore ----

    void ExitCurrentState()
    {
        // Called when a non-walking state expires and we should restore normal walking behavior.

        // Ease any look-around offset back to the walking direction (no-op if none is active).
        EndLookAround();

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
            // Restore the normal-walking speed baseline (owned by MoveToTarget; set on every spawn/reuse by
            // AgentFactory via SetNormalSpeed()).
            moveController.RestoreNormalSpeed();

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
        else if (enableLogging)
        {
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] ExitCurrentState: moveController is null; cannot restore movement.");
        }

        // Reset detour retry counter
        detourArrivalRetryCount = 0;
    }

    // Helper to determine the main target (the goal that MoveToTarget should go to after interruptions)
    Vector3 GetCurrentMainTarget()
    {
        if (moveController != null)
        {
            // MoveToTarget exposes public targetTransform/targetPosition fields in earlier code
            return moveController.targetTransform != null ? moveController.targetTransform.position : moveController.targetPosition;
        }
        return transform.position;
    }

    // ---- Per-frame behaviors for certain states ----

    // Hesitate = the pedestrian briefly looks around while still walking: turn to one side, hold the gaze,
    // turn to the other side, hold, ... and ease back to forward when the state ends (EndLookAround()).
    // This method only advances the sequence timers and picks a NEW target at the end of each hold; the
    // actual smoothing and bone write happen in LateUpdate(). Nothing here touches the root rotation.
    void DoHesitate(float dt)
    {
        if (lookPhase != LookPhase.Turning && lookPhase != LookPhase.Holding)
            return;

        lookPhaseTimer -= dt;
        if (lookPhaseTimer > 0f)
            return;

        if (lookPhase == LookPhase.Turning)
        {
            lookPhase = LookPhase.Holding;
            lookPhaseTimer = RandomRange(lookHoldMin, lookHoldMax);
        }
        else
        {
            StartLookTurn(-lookSide); // alternate sides
        }
    }

    // ---- Look-around helpers ----

    // Starts the sequence (first side random). Returns false if look-around is disabled or no head bone exists,
    // in which case Hesitate stays a plain soft pause.
    bool BeginLookAround()
    {
        if (!lookAroundEnabled) return false;
        if (!ResolveLookBone()) return false;

        StartLookTurn(UnityEngine.Random.value < 0.5f ? -1f : 1f);
        return true;
    }

    void StartLookTurn(float side)
    {
        lookSide = side;
        float a0 = Mathf.Min(lookAngleMin, lookAngleMax);
        float a1 = Mathf.Max(lookAngleMin, lookAngleMax);
        lookTargetYaw = side * RandomRange(a0, a1);

        // Longer travel (e.g. left -> right) gets a proportionally longer turn, capped, to keep speed gentle.
        float stretch = Mathf.Clamp(Mathf.Abs(lookTargetYaw - lookYaw) / k_LookReferenceArc, 1f, 1.6f);
        float duration = RandomRange(lookTurnDurationMin, lookTurnDurationMax) * stretch;

        lookPhase = LookPhase.Turning;
        lookPhaseTimer = duration;
        lookSmoothTime = Mathf.Max(0.05f, duration / k_LookSmoothDampFactor);
    }

    // Hesitate (or any other state change) is over: ease the head back to forward from wherever it currently is.
    // SmoothDamp keeps the angular velocity continuous, so there is no snap even if this happens mid-turn.
    void EndLookAround()
    {
        if (lookPhase == LookPhase.Idle || lookPhase == LookPhase.Returning) return;

        lookPhase = LookPhase.Returning;
        lookTargetYaw = 0f;
        lookSmoothTime = Mathf.Max(0.05f, lookReturnDuration / k_LookSmoothDampFactor);
    }

    // Hard reset (pool reuse / disable): no residual offset, velocity or phase is carried into the next life.
    void ResetLookAroundImmediate()
    {
        lookPhase = LookPhase.Idle;
        lookYaw = 0f;
        lookYawVelocity = 0f;
        lookTargetYaw = 0f;
        lookPhaseTimer = 0f;
    }

    bool ResolveLookBone()
    {
        if (lookBoneResolved) return lookBone != null;
        lookBoneResolved = true;

        // Humanoid avatar first (the NPC avatar maps its "Neck" transform to Head), then name-based fallback.
        Animator anim = GetComponentInChildren<Animator>();
        if (anim != null && anim.isHuman)
            lookBone = anim.GetBoneTransform(HumanBodyBones.Head);

        if (lookBone == null)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Neck" || t.name == "Head") { lookBone = t; if (t.name == "Neck") break; }
            }
        }

        if (lookBone == null && enableLogging)
            Debug.LogWarning($"[UncertainBehavior] [{gameObject.name}] No head/neck bone found; Hesitate will not look around.");

        return lookBone != null;
    }

    // Runs after the Animator (and Animation Rigging) have posed the skeleton this frame, so the yaw offset is
    // applied on top of the animated pose and never accumulates. Runs even while uncertain behaviour is
    // suspended, so the return-to-forward always completes.
    void LateUpdate()
    {
        if (lookPhase == LookPhase.Idle) return;

        if (lookBone == null)
        {
            ResetLookAroundImmediate();
            return;
        }

        lookYaw = Mathf.SmoothDamp(lookYaw, lookTargetYaw, ref lookYawVelocity, lookSmoothTime,
                                   Mathf.Infinity, Time.deltaTime);

        if (lookPhase == LookPhase.Returning &&
            Mathf.Abs(lookYaw) < 0.05f && Mathf.Abs(lookYawVelocity) < 0.05f)
        {
            ResetLookAroundImmediate();
            return;
        }

        // Yaw about the NPC's up axis (world space), so it is independent of the bone's local axes.
        lookBone.rotation = Quaternion.AngleAxis(lookYaw, transform.up) * lookBone.rotation;
    }

    static float RandomRange(float a, float b)
    {
        return UnityEngine.Random.Range(Mathf.Min(a, b), Mathf.Max(a, b));
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

    // Called when MoveToTarget.OnArrived fires (subscribed in Start()/ResetForReuse()).
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
        // Drop any look-around state so a re-enabled (pooled) NPC never resumes a stale offset.
        ResetLookAroundImmediate();

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