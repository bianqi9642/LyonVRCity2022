// MoveToTarget_NavMeshOnly.cs
// NavMeshAgent-only mover that drives a GameObject from a start Transform/position to a target Transform/position.
// - Uses NavMeshAgent exclusively for movement. The NavMeshAgent is the authoritative steering controller.
// - MoveToTarget will NOT overwrite NavMeshAgent.speed via SetMoveSpeed()/autoSetAgentSpeed by default. To
//   allow SetMoveSpeed() to write agent.speed, enable the 'autoSetAgentSpeed' flag on the component.
// - Stage 4 (Option A movement-architecture migration): SetNormalSpeed()/SetTemporarySpeed()/
//   RestoreNormalSpeed() are a SEPARATE, always-active speed API used by AgentFactory (normal baseline,
//   every spawn/reuse) and UncertainBehavior (temporary Slow-state override) now that MoveToTarget is the
//   single movement authority. They intentionally bypass the autoSetAgentSpeed gate - that flag still governs
//   only the older SetMoveSpeed() path.
// - Script keeps control of rotation (agent.updateRotation = false) so rotation smoothing is applied here.
// - External pause API available (halt movement and/or freeze animator).
// - Drives animator from measured translational speed (smoothed).
// - Defensive NavMesh sampling / resample logic retained for robustness.
// - If agent cannot be placed on NavMesh at initialization, the script disables itself and logs an error.
// Comments are in English.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(Collider), typeof(NavMeshAgent))]
public class MoveToTarget : MonoBehaviour
{
    [Header("Start / Target (Drag Transforms)")]
    // The "start" transform (semantic start of the current leg).
    public Transform startTransform;
    public Transform targetTransform;

    [Header("Fallback Positions (used when transforms are null)")]
    public bool setStartOnPlay = true;
    public Vector3 startPosition = Vector3.zero;
    public Vector3 targetPosition = new Vector3(5f, 0f, 5f);

    [Header("Movement")]
    // Local copy of desired speed; used for animator fallback and when agent is absent, and (Stage 4) as the
    // "normal walking" baseline set via SetNormalSpeed() and restored via RestoreNormalSpeed() after a
    // temporary override from SetTemporarySpeed().
    // NOTE: When a NavMeshAgent is present, that agent's speed is authoritative unless autoSetAgentSpeed ==
    // true OR one of SetNormalSpeed()/SetTemporarySpeed()/RestoreNormalSpeed() is called explicitly.
    public float speed = 1.4f;
    public float arriveThreshold = 0.2f;

    // Optional per-leg override of 'arriveThreshold' (0 = none). Set by PedestrianAgent.MoveToCurrent() for
    // directed itinerary waypoints so this component's single arrival decision uses the itinerary's canonical
    // radius (PedestrianAgent.arriveTolerance) instead of a second, tighter threshold. Every MoveTo(...) call
    // clears it, so UncertainBehavior detours, wander legs and any other caller keep using 'arriveThreshold'.
    float legArriveRadius = 0f;

    /// <summary>Radius currently used for arrival decisions: the per-leg override if set, else 'arriveThreshold'.</summary>
    public float ArriveRadius => legArriveRadius > 0f ? legArriveRadius : arriveThreshold;

    /// <summary>
    /// Sets the arrival radius for the CURRENT leg only. Call after MoveTo(...) (which resets it). Pass 0 to
    /// fall back to 'arriveThreshold'. Cleared again by the next MoveTo(...) and by OnDisable().
    /// </summary>
    public void SetLegArriveRadius(float radius)
    {
        legArriveRadius = Mathf.Max(0f, radius);
    }

    public bool faceMovementDirection = true;
    public float rotationSpeed = 10f;

    [Header("NavMeshAgent (required)")]
    // NavMeshAgent reference (RequireComponent ensures presence).
    public NavMeshAgent agent;

    // If true, MoveToTarget will write agent.speed (control speed). Default is FALSE so NavMeshAgent (or AgentFactory) can control speed.
    [Tooltip("If true, MoveToTarget will set NavMeshAgent.speed from this component's 'speed' value. " +
             "If false, MoveToTarget will not change NavMeshAgent.speed (preferred when external systems set speed).")]
    public bool autoSetAgentSpeed = false;

    [Header("Animator integration")]
    public Animator animator;
    public string animatorIsWalkingParam = "isWalking";
    public string animatorMoveSpeedParam = "MoveSpeed";

    [Header("Animation - velocity settings")]
    [Tooltip("Measured movement speed (m/s) above which animator is considered 'walking'.")]
    public float walkSpeedThreshold = 0.05f;
    [Tooltip("Smoothing factor applied to measured movement speed (higher = smoother).")]
    public float speedSmoothing = 6f;

    [Header("External Control")]
    [Tooltip("If true, MoveToTarget treats this as a behavior-driven pause and will not mark 'arrived'.")]
    public bool externallyPaused = true; // logical paused state (affects arrival checks)

    // Internal flags controlling pause behavior:
    bool externallyHaltMovement = false;
    bool externallyFreezeAnimator = false;

    [Header("Resample / Logging Controls")]
    public float resampleCooldown = 0.5f;
    public int maxResampleAttempts = 6;
    public bool suppressRepeatedNoPathLogs = true;

    [Header("Debug")]
    public bool enableDebugLogs = true;

    [Header("Dynamic target update")]
    public float targetUpdateInterval = 0.12f;

    // Public arrival event for external hooks
    public event Action OnArrived;

    // Public read-only status properties
    public bool IsMoving => moving;
    public bool IsArrived => arrived;

    /// <summary>
    /// NEW (fix for pooled-agent reactivation race): public, read-only readiness check for external callers
    /// (e.g. PedestrianAgent) that need to know whether this mover's NavMeshAgent is currently enabled and
    /// placed on the NavMesh, and therefore able to actually act on a MoveTo(...) call.
    ///
    /// Background: on pooled reuse, MoveToTarget.OnEnable() -> ReinitializeAfterEnable() re-warps the
    /// NavMeshAgent onto the NavMesh asynchronously (it needs at least one frame after SetActive(true)/
    /// OnEnable before agent.isOnNavMesh becomes true again). A caller that invokes MoveTo(...) synchronously
    /// in the same frame as activation - as SpawnerManager.SpawnAgent() -> PedestrianAgent.Initialize() ->
    /// MoveToCurrent() previously did - could have its MoveTo(...) call silently ignored (only a warning log),
    /// because AgentCanControl() was still false at that instant.
    ///
    /// Exposing this as a public wrapper around the existing internal AgentCanControl() check lets callers
    /// poll for real readiness instead of assuming a fixed number of frames, which is unreliable especially
    /// on Quest where coroutine scheduling latency can vary under load.
    /// </summary>
    public bool IsReadyToMove()
    {
        return AgentCanControl();
    }

    // Internal state
    bool moving = false;
    bool arrived = false;

    // Sampling and retry constants
    const float k_NavMeshSampleDistance = 2.0f;
    const int k_MaxSampleRetries = 6;
    const float k_NudgeDistance = 0.6f;

    // Resample state
    float lastResampleTime = -10f;
    int resampleAttempts = 0;
    bool loggedNoPathOnce = false;

    // Target update throttle state
    float lastTargetUpdateTime = -10f;

    // Velocity tracking for animator
    Vector3 lastPosition;
    float smoothedSpeed = 0f;

    // Current Walk/Idle state of the hysteresis in Update(). Reset whenever the animator is forced to Idle.
    bool walkAnimActive = false;
    // Walk returns to Idle below walkSpeedThreshold * this factor (it starts above walkSpeedThreshold).
    const float k_WalkStopThresholdFactor = 0.5f;

    // ---- Stalled-movement detector (ANIMATION ONLY) ----
    // Catches an NPC that is not making real progress although its instantaneous speed is not exactly zero: pinned by
    // neighbours, sliding or jittering under local avoidance, repeated contacts. It watches the horizontal Transform
    // position over a sliding window of k_StallBins bins of k_StallBinSeconds (1.0 s). The window is "stalled" when both
    // the net displacement and the distance actually walked are tiny (average speed well below even the slowest
    // legitimate walk: UncertainBehavior Slow on a 0.6 m/s walker is 0.3 m/s). Net displacement alone is not used,
    // because a Detour turning back on itself has ~0 net displacement while walking at full speed.
    // Effect: only forces Idle through the existing Walk/Idle decision. It never touches the NavMeshAgent (path,
    // destination, isStopped), the itinerary or the Transform, so the NPC keeps trying to move and Walk resumes by itself.
    const int k_StallBins = 4;
    const float k_StallBinSeconds = 0.25f;
    const float k_StallMaxNetSpeed = 0.10f;    // m/s average net displacement below which the window has no progress
    const float k_StallMaxTravelSpeed = 0.20f; // m/s average distance walked below which the window has no progress
    readonly Vector2[] stallBinStart = new Vector2[k_StallBins];
    readonly float[] stallBinTravel = new float[k_StallBins];
    readonly float[] stallBinTime = new float[k_StallBins];
    int stallHead, stallFilled;
    float stallCurTravel, stallCurTime;
    Vector2 stallCurStart, stallLastPos;
    bool stallTracking = false;
    bool animStalled = false;

    /// <summary>True while the stalled-movement detector forces Idle (animation only; navigation is untouched).</summary>
    public bool IsAnimationStalled => animStalled;

    // Optional AgentID for better logs and traceability.
    [Tooltip("Optional unique identifier for this mover instance (set by factory or PedestrianAgent).")]
    public string AgentID = null;

    // Flag to detect first-time Start execution vs later OnEnable reinitialization (pool reuse)
    bool hasStartedOnce = false;

    void Awake()
    {
        // Ensure agent reference (RequireComponent should ensure presence; still get reference).
        if (agent == null)
            agent = GetComponent<NavMeshAgent>();

        // If animator not assigned, try to find on children
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        // Try to read AgentID from a sibling PedestrianAgent if present (best-effort).
        var ped = GetComponent<PedestrianAgent>();
        if (ped != null && !string.IsNullOrEmpty(ped.AgentID))
        {
            AgentID = ped.AgentID;
        }

        if (agent == null)
        {
            // This should not happen because of RequireComponent, but handle defensively.
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} NavMeshAgent missing even though required. Disabling this component.");
            enabled = false;
            return;
        }

        if (enableDebugLogs && animator == null)
            Debug.LogWarning($"{LogPrefix()} No Animator found on GameObject or children. Animator parameters will not be set.");

        // Initialize lastPosition from transform; primary speed source will be agent.velocity.
        lastPosition = transform.position;
        smoothedSpeed = 0f;
    }

    /// <summary>
    /// Start coroutine: initialization.
    /// This variant assumes NavMeshAgent will be used exclusively.
    /// If we cannot place the agent on the NavMesh, the component disables itself.
    /// </summary>
    IEnumerator Start()
    {
        // Initialize position using startTransform if present
        if (startTransform != null)
            transform.position = startTransform.position;
        else if (setStartOnPlay)
            transform.position = startPosition;

        lastPosition = transform.position;
        smoothedSpeed = 0f;

        if (agent == null)
        {
            // Defensive check, should not happen.
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} NavMeshAgent missing on Start. Disabling component.");
            enabled = false;
            yield break;
        }

        // Only set NavMeshAgent.speed from this component when autoSetAgentSpeed is enabled.
        if (autoSetAgentSpeed)
            agent.speed = speed;

        // Agent should drive position; script handles rotation smoothing.
        agent.updatePosition = true;
        agent.updateRotation = false;

        // Attempt placing agent on NavMesh. If placement fails, we will disable the component,
        // because this script is NavMeshAgent-only and must not fallback to transform movement.
        yield return TryEnsureAgentOnNavMesh();

        if (!AgentCanControl())
        {
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} Agent cannot control (not enabled or not on NavMesh) after placement attempts. Disabling component.");
            enabled = false;
            yield break;
        }

        // Unpause agent if it was stopped.
        SafeSetIsStopped(false);

        // Sample & set initial destination robustly (same sampling logic as original).
        Vector3 originalDesiredTarget = GetTargetPosition();
        Vector3 sampledTarget = originalDesiredTarget;
        bool sampledOk = false;

        for (int attempt = 0; attempt < k_MaxSampleRetries; attempt++)
        {
            Vector3 tryPos = originalDesiredTarget;

            if (attempt > 0)
            {
                Vector3 dir = (originalDesiredTarget - transform.position);
                if (dir.sqrMagnitude < 1e-4f)
                {
                    Vector2 rnd = UnityEngine.Random.insideUnitCircle.normalized;
                    tryPos = transform.position + new Vector3(rnd.x, 0f, rnd.y) * k_NudgeDistance * attempt;
                }
                else
                {
                    dir = dir.normalized;
                    tryPos = originalDesiredTarget + dir * (k_NudgeDistance * attempt);
                }
            }

            NavMeshHit hit;
            if (NavMesh.SamplePosition(tryPos, out hit, k_NavMeshSampleDistance, NavMesh.AllAreas))
            {
                sampledTarget = hit.position;
                float distToStart = Vector3.Distance(sampledTarget, transform.position);
                if (distToStart <= Mathf.Max(arriveThreshold * 0.5f, 0.05f))
                {
                    if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} Sampled target is very close to start (dist {distToStart:F3}) on attempt {attempt}. Retrying with nudge.");
                    sampledOk = false;
                    continue;
                }

                sampledOk = true;
                if (enableDebugLogs) Debug.Log($"{LogPrefix()} Sampled target accepted at {sampledTarget} (attempt {attempt}).");
                break;
            }
            else
            {
                if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} NavMesh.SamplePosition failed for candidate on attempt {attempt}.");
            }
        }

        if (!sampledOk)
        {
            sampledTarget = originalDesiredTarget;
            if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} Could not find a distinct sampled NavMesh target after retries. Using original target position (may be unreachable).");
        }

        yield return null;

        if (AgentCanControl())
        {
            agent.SetDestination(sampledTarget);
            lastTargetUpdateTime = Time.time;
            yield return new WaitForSeconds(0.05f);
            if (!agent.hasPath)
            {
                agent.SetDestination(sampledTarget);
                if (enableDebugLogs) Debug.Log($"{LogPrefix()} Retried SetDestination.");
            }

            if (enableDebugLogs)
            {
                Debug.Log($"{LogPrefix()} Start pos: {transform.position}, Target pos (sampled): {sampledTarget}");
                Debug.Log($"{LogPrefix()} Agent state after SetDestination - hasPath:{(agent != null ? agent.hasPath.ToString() : "n/a")} pathStatus:{(agent != null ? agent.pathStatus.ToString() : "n/a")} remainingDistance:{(agent != null ? agent.remainingDistance.ToString() : "n/a")}");
            }

            moving = true;
            arrived = false;

            lastPosition = transform.position;
            smoothedSpeed = 0f;
        }
        else
        {
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} Unexpected agent unavailability after SetDestination attempt. Disabling component.");
            enabled = false;
            yield break;
        }

        hasStartedOnce = true;
    }

    /// <summary>
    /// OnEnable: handle pool reuse re-initialization.
    /// </summary>
    void OnEnable()
    {
        // If Start has not yet executed, let Start handle initialization.
        if (!hasStartedOnce)
            return;

        // Start a short coroutine to reinitialize on enable.
        StartCoroutine(ReinitializeAfterEnable());
    }

    IEnumerator ReinitializeAfterEnable()
    {
        yield return null;

        // refresh last position / speed
        lastPosition = transform.position;
        smoothedSpeed = 0f;
        walkAnimActive = false;
        ResetStallDetector();
        arrived = false;

        if (agent == null)
        {
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} NavMeshAgent missing during Reinitialize. Disabling component.");
            enabled = false;
            yield break;
        }

        if (autoSetAgentSpeed)
            agent.speed = speed;

        // Ensure agent will drive transform
        agent.updatePosition = true;
        agent.updateRotation = false;

        // Attempt to ensure agent is on NavMesh; this may warp the agent to a valid sample.
        yield return TryEnsureAgentOnNavMesh();

        // If agent can control, set destination to current target and un-stop it.
        if (AgentCanControl())
        {
            SafeSetIsStopped(false);

            Vector3 desired = GetTargetPosition();
            // sample and set dest defensively
            NavMeshHit hit;
            if (NavMesh.SamplePosition(desired, out hit, k_NavMeshSampleDistance, NavMesh.AllAreas))
            {
                agent.SetDestination(hit.position);
            }
            else
            {
                agent.SetDestination(desired);
            }

            lastTargetUpdateTime = Time.time;
        }
        else
        {
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} Agent still not on NavMesh after Reinitialize. Disabling component.");
            enabled = false;
            yield break;
        }

        // ensure mover's moving flag is set so Update monitors agent properly
        moving = true;
        arrived = false;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) dt = 0.0001f;

        // If agent cannot control, nothing to do (component should normally be disabled in that case).
        if (!AgentCanControl())
        {
            ResetStallDetector();
            return;
        }

        // Velocity / speed measurement:
        // - Use the NavMeshAgent velocity as the authoritative instantaneous velocity.
        float instantSpeed = agent.velocity.magnitude;

        // Smooth the speed for animator blending
        float alpha = Mathf.Clamp01(dt * speedSmoothing);
        smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantSpeed, alpha);

        // Animator decision. Walk follows ACTUAL movement, never movement intent: 'moving && !arrived' (a destination
        // has been issued) and desiredVelocity are deliberately NOT used. While the NavMeshAgent waits for a path
        // (pathPending, hasPath == false - which can take seconds when many NPCs request paths) its velocity is ~0, so
        // the NPC shows Idle instead of walking on the spot, and returns to Walk as soon as it really moves.
        // Hysteresis on the smoothed actual speed (start at walkSpeedThreshold, stop at half of it) avoids flicker
        // around the threshold, e.g. when crowd avoidance nudges a standing NPC. An explicit animator freeze
        // (PauseExternal(.., freezeAnimator: true): Pause, Attractor dwell, End-Panel hard stop) always wins.
        bool shouldWalk;

        if (externallyFreezeAnimator)
        {
            walkAnimActive = false;
            ResetStallDetector();
        }
        else
        {
            UpdateStallDetector(dt);

            walkAnimActive = walkAnimActive
                ? smoothedSpeed > walkSpeedThreshold * k_WalkStopThresholdFactor
                : smoothedSpeed > walkSpeedThreshold;

            // No real progress (pinned / jittering under avoidance, or waiting): Idle even if the speed is non-zero.
            if (animStalled)
                walkAnimActive = false;
        }
        shouldWalk = walkAnimActive;

        if (animator != null)
        {
            animator.SetBool(animatorIsWalkingParam, shouldWalk);

            // Walk cadence follows the measured speed (so UncertainBehavior's Slow state and 0.6 m/s walkers play
            // proportionally slower); no fallback to the nominal agent.speed, which would play a stationary NPC at
            // full cadence. Idle always reports 0.
            animator.SetFloat(animatorMoveSpeedParam, shouldWalk ? smoothedSpeed : 0f);
        }

        // Early out if not actively moving or already arrived
        if (!moving || arrived) return;

        // If externally paused *and* caller requested movement halt, don't apply movement this frame.
        if (externallyPaused && externallyHaltMovement)
        {
            SafeSetIsStopped(true);
            return;
        }

        // ---------- Agent-controlled movement (NavMeshAgent-only) ----------
        // dynamic target throttle
        if (targetTransform != null)
        {
            if (Time.time - lastTargetUpdateTime >= targetUpdateInterval)
            {
                Vector3 targetPos = targetTransform.position;
                agent.SetDestination(targetPos);
                lastTargetUpdateTime = Time.time;
                resampleAttempts = 0;
                loggedNoPathOnce = false;
            }
        }

        // Keep earlier resample/no-path logic intact
        if (!agent.hasPath)
        {
            Vector3 curTarget = GetTargetPosition();
            float distToTarget = Vector3.Distance(transform.position, curTarget);

            if (distToTarget <= Mathf.Max(ArriveRadius, 0.05f))
            {
                if (externallyPaused && externallyHaltMovement)
                {
                    if (enableDebugLogs) Debug.Log($"{LogPrefix()} Agent has no path but target is at/near current position -> externally paused (halt).");
                    moving = true;
                    arrived = false;
                    SafeSetIsStopped(true);
                    return;
                }
                else if (externallyPaused)
                {
                    return;
                }
                else
                {
                    arrived = true;
                    moving = false;
                    if (enableDebugLogs) Debug.Log($"{LogPrefix()} Agent has no path but target is at/near current position -> marking as arrived.");
                    SafeSetIsStopped(true);
                    OnArrived?.Invoke();
                    return;
                }
            }

            if (Time.time - lastResampleTime < resampleCooldown)
                return;

            if (resampleAttempts >= maxResampleAttempts)
            {
                if (!loggedNoPathOnce && suppressRepeatedNoPathLogs && enableDebugLogs)
                {
                    Debug.Log($"{LogPrefix()} No active path after multiple attempts - giving up resample for now. See earlier diagnostics.");
                    loggedNoPathOnce = true;
                }
                return;
            }

            lastResampleTime = Time.time;
            resampleAttempts++;

            NavMeshHit th;
            if (NavMesh.SamplePosition(curTarget, out th, k_NavMeshSampleDistance, NavMesh.AllAreas))
            {
                float sampledDist = Vector3.Distance(transform.position, th.position);
                if (sampledDist <= Mathf.Max(arriveThreshold * 0.5f, 0.05f))
                {
                    if (externallyPaused && externallyHaltMovement)
                    {
                        if (enableDebugLogs) Debug.Log($"{LogPrefix()} Sampled target is at/near current position while externally paused; keeping paused state.");
                        moving = true;
                        arrived = false;
                        SafeSetIsStopped(true);
                        return;
                    }
                    else
                    {
                        arrived = true;
                        moving = false;
                        if (enableDebugLogs) Debug.Log($"{LogPrefix()} Sampled target is at/near current position -> marking as arrived.");
                        SafeSetIsStopped(true);
                        OnArrived?.Invoke();
                        return;
                    }
                }

                if (AgentCanControl())
                {
                    agent.SetDestination(th.position);
                    lastTargetUpdateTime = Time.time;
                }

                if (!loggedNoPathOnce && enableDebugLogs)
                    Debug.Log($"{LogPrefix()} No active path but target differs. Sampled target and resent SetDestination.");

                return;
            }
            else
            {
                if (!loggedNoPathOnce && enableDebugLogs)
                    Debug.LogWarning($"{LogPrefix()} No active path and target cannot be sampled on NavMesh. Check NavMesh and target accessibility.");
            }

            return;
        }
        else
        {
            if (resampleAttempts > 0 || loggedNoPathOnce)
            {
                resampleAttempts = 0;
                loggedNoPathOnce = false;
            }
        }

        // Use agent.velocity as the authoritative movement vector for rotation/animation
        Vector3 movementVel = agent.velocity; // world-space velocity
        if (movementVel.sqrMagnitude < 1e-6f && agent.hasPath && agent.path.corners.Length >= 2)
        {
            Vector3 nextCorner = agent.path.corners[1];
            movementVel = (nextCorner - transform.position);
            movementVel.y = 0f;
            movementVel = movementVel.normalized * Mathf.Max(agent.speed, 0.0001f);
        }

        // Do not manually move transform.position here — NavMeshAgent updates position.
        // We still optionally rotate the transform toward movement direction, since agent.updateRotation = false.
        if (faceMovementDirection && movementVel.sqrMagnitude > 1e-6f)
        {
            Quaternion targetRot = Quaternion.LookRotation(movementVel.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
        }

        // Arrival check using a safe remaining distance calculation
        float remainingSafe = GetRemainingDistanceSafe(agent);
        Vector3 currentTarget = GetTargetPosition();
        float dist = Vector3.Distance(transform.position, currentTarget);

        float arriveRadius = ArriveRadius;
        if (!externallyPaused && (dist <= arriveRadius || (!agent.pathPending && remainingSafe <= arriveRadius)))
        {
            arrived = true;
            moving = false;
            SafeSetIsStopped(true);
            if (enableDebugLogs) Debug.Log($"{LogPrefix()} Arrived at target.");
            OnArrived?.Invoke();
        }
        else if (externallyPaused && externallyHaltMovement)
        {
            SafeSetIsStopped(true);
        }
    }

    // Public API: pause movement from external behavior without triggering 'arrived'.
    public void PauseExternal(bool haltMovement = true, bool freezeAnimator = true)
    {
        externallyPaused = true;
        externallyHaltMovement = haltMovement;
        externallyFreezeAnimator = freezeAnimator;

        moving = true;
        arrived = false;

        if (AgentCanControl() && haltMovement)
            SafeSetIsStopped(true);

        if (animator != null && freezeAnimator)
        {
            walkAnimActive = false;
            ResetStallDetector();
            animator.SetBool(animatorIsWalkingParam, false);
            animator.SetFloat(animatorMoveSpeedParam, 0f);
        }

        if (haltMovement)
        {
            smoothedSpeed = 0f;
        }
    }

    // Public API to resume movement after an external pause.
    public void ResumeExternal()
    {
        externallyPaused = false;
        externallyHaltMovement = false;
        externallyFreezeAnimator = false;

        if (AgentCanControl())
            SafeSetIsStopped(false);

        moving = true;
        arrived = false;

        if (AgentCanControl())
        {
            Vector3 dest = GetTargetPosition();
            agent.SetDestination(dest);
            lastTargetUpdateTime = Time.time;
            resampleAttempts = 0;
            loggedNoPathOnce = false;
            lastResampleTime = -10f;
        }
    }

    void ResetStallDetector()
    {
        stallTracking = false;
        animStalled = false;
        stallFilled = 0;
        stallHead = 0;
    }

    // Called once per frame while the animator is not frozen. Updates 'animStalled'.
    void UpdateStallDetector(float dt)
    {
        Vector3 wp = transform.position;
        Vector2 pos = new Vector2(wp.x, wp.z);

        if (!stallTracking)
        {
            stallTracking = true;
            stallLastPos = pos;
            stallCurStart = pos;
            stallCurTravel = 0f;
            stallCurTime = 0f;
            stallFilled = 0;
            stallHead = 0;
            return;
        }

        stallCurTravel += (pos - stallLastPos).magnitude;
        stallLastPos = pos;
        stallCurTime += dt;
        if (stallCurTime < k_StallBinSeconds)
            return;

        // Close the current bin.
        stallBinStart[stallHead] = stallCurStart;
        stallBinTravel[stallHead] = stallCurTravel;
        stallBinTime[stallHead] = stallCurTime;
        stallHead = (stallHead + 1) % k_StallBins;
        if (stallFilled < k_StallBins) stallFilled++;
        stallCurStart = pos;
        stallCurTravel = 0f;
        stallCurTime = 0f;

        if (stallFilled < k_StallBins)
            return; // not a full observation window yet

        // Full window (stallHead now indexes the oldest bin).
        float time = 0f, travel = 0f;
        for (int i = 0; i < k_StallBins; i++) { time += stallBinTime[i]; travel += stallBinTravel[i]; }
        float net = (pos - stallBinStart[stallHead]).magnitude;
        bool windowStalled = net < k_StallMaxNetSpeed * time && travel < k_StallMaxTravelSpeed * time;

        if (windowStalled)
        {
            animStalled = true;
        }
        else if (animStalled)
        {
            // Release only on movement sustained over the two most recent bins (0.5 s) at a real walking pace, so a
            // brief shove does not flip the NPC back to Walk, yet a genuinely free NPC resumes within ~0.5 s.
            int newest = (stallHead + k_StallBins - 1) % k_StallBins;
            int prev = (stallHead + k_StallBins - 2) % k_StallBins;
            float rt = stallBinTime[newest] + stallBinTime[prev];
            float rtravel = stallBinTravel[newest] + stallBinTravel[prev];
            float rnet = (pos - stallBinStart[prev]).magnitude;
            if (rnet >= k_StallMaxNetSpeed * rt && rtravel >= k_StallMaxTravelSpeed * rt)
                animStalled = false;
        }
    }

    /// <summary>
    /// Public helper to set movement speed at runtime.
    /// This updates the local 'speed' field. It will only set NavMeshAgent.speed if autoSetAgentSpeed == true.
    /// That preserves AgentFactory/profile control over NavMeshAgent.speed in the typical workflow.
    /// </summary>
    public void SetMoveSpeed(float s)
    {
        speed = s;
        if (agent != null && autoSetAgentSpeed)
        {
            agent.speed = s;
        }
    }

    /// <summary>
    /// Stage 4 (Option A movement-architecture migration): sets the authoritative "normal walking" speed
    /// baseline. Called by AgentFactory.ApplyProfileToInstance() every spawn/pool reuse, after the current
    /// AgentProfile's applied speed (base speed * speedOptions multiplier) has been computed. Unlike
    /// SetMoveSpeed()/autoSetAgentSpeed (a separate, opt-in mechanism left unchanged for any other caller),
    /// this ALWAYS writes NavMeshAgent.speed immediately - MoveToTarget is the single component allowed to
    /// do so, and 'speed' becomes what RestoreNormalSpeed() returns to after a temporary override.
    /// </summary>
    public void SetNormalSpeed(float s)
    {
        speed = s;
        if (agent != null)
            agent.speed = s;
    }

    /// <summary>
    /// Stage 4: immediately overrides the live NavMeshAgent speed for a temporary behavioural interruption
    /// (e.g. UncertainBehavior's Slow state). Does NOT change the normal-speed baseline ('speed') - call
    /// RestoreNormalSpeed() to return to it. Independent of autoSetAgentSpeed by design.
    /// </summary>
    public void SetTemporarySpeed(float s)
    {
        if (agent != null)
            agent.speed = s;
    }

    /// <summary>
    /// Stage 4: restores NavMeshAgent.speed to the current normal-walking baseline (the last value set via
    /// SetNormalSpeed()), ending a temporary override from SetTemporarySpeed().
    /// </summary>
    public void RestoreNormalSpeed()
    {
        if (agent != null)
            agent.speed = speed;
    }

    /// <summary>
    /// Public helper to set the unique AgentID used in logs/tracing.
    /// </summary>
    public void SetAgentID(string id)
    {
        AgentID = id;
    }

    IEnumerator TryEnsureAgentOnNavMesh()
    {
        if (agent == null)
            yield break;

        // small sample at current position
        NavMeshHit initialHit;
        if (NavMesh.SamplePosition(transform.position, out initialHit, 0.1f, NavMesh.AllAreas))
        {
            if (agent.enabled)
            {
                bool warped = agent.Warp(initialHit.position);
                if (warped)
                {
                    // When using agent.updatePosition = true, Warp should place the transform as well.
                    transform.position = initialHit.position;
                    if (enableDebugLogs)
                    {
                        Debug.Log(
                            $"{LogPrefix()} Agent already on NavMesh (tiny sample). " +
                            $"transformPos={transform.position}, " +
                            $"agentNextPos={agent.nextPosition}, " +
                            $"navHitPos={initialHit.position}"
                        );
                    }
                    yield break;
                }
                else
                {
                    if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} agent.Warp returned false for tiny sample.");
                }
            }
        }

        float[] radii = new float[] { 0.2f, 0.5f, 1f, 2f, 5f };
        bool warpedSuccess = false;
        for (int i = 0; i < radii.Length; i++)
        {
            float r = radii[i];
            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, r, NavMesh.AllAreas))
            {
                if (agent.enabled)
                {
                    bool ok = agent.Warp(hit.position);
                    if (ok)
                    {
                        // Warp should move agent and transform when agent.updatePosition = true.
                        transform.position = hit.position;
                        if (enableDebugLogs) Debug.Log($"{LogPrefix()} Warped agent to NavMesh (radius {r}): {hit.position}");
                        warpedSuccess = true;
                        break;
                    }
                    else
                    {
                        if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} agent.Warp returned false for sampled point at radius {r}: {hit.position}");
                    }
                }
            }
        }

        if (!warpedSuccess)
        {
            // Because this script is NavMeshAgent-only, we do not fallback to transform movement.
            if (enableDebugLogs) Debug.LogError($"{LogPrefix()} Failed to place agent on NavMesh after multiple sample attempts. Disabling this component (NavMeshAgent-only).");
            // Disable this component so it doesn't try to operate without a valid NavMeshAgent footing.
            enabled = false;
        }

        yield break;
    }

    void SafeSetIsStopped(bool stopped)
    {
        if (agent == null) return;
        if (!agent.enabled) return;
#if UNITY_2019_1_OR_NEWER
        if (!agent.isOnNavMesh) return;
#endif
        agent.isStopped = stopped;
    }

    bool AgentCanControl()
    {
        if (agent == null) return false;
        if (!agent.enabled) return false;
#if UNITY_2019_1_OR_NEWER
        if (!agent.isOnNavMesh) return false;
#endif
        return true;
    }

    float GetRemainingDistanceSafe(NavMeshAgent agent)
    {
        if (agent == null) return Mathf.Infinity;

        float rd = agent.remainingDistance;
        if (!float.IsInfinity(rd) && !float.IsNaN(rd))
            return rd;

        if (agent.pathPending)
            return Mathf.Infinity;

        NavMeshPath path = agent.path;
        if (path == null || path.corners == null || path.corners.Length == 0)
            return Mathf.Infinity;

        float sum = 0f;
        Vector3 prev = agent.nextPosition;
        for (int i = 0; i < path.corners.Length; i++)
        {
            Vector3 corner = path.corners[i];
            if (float.IsNaN(corner.x) || float.IsNaN(corner.y) || float.IsNaN(corner.z))
                return Mathf.Infinity;
            if (float.IsInfinity(corner.x) || float.IsInfinity(corner.y) || float.IsInfinity(corner.z))
                return Mathf.Infinity;

            sum += Vector3.Distance(prev, corner);
            prev = corner;
        }

        if (sum <= 0f || float.IsInfinity(sum) || float.IsNaN(sum))
            return Mathf.Infinity;

        return sum;
    }

    public void MoveTo(Vector3 newTarget)
    {
        legArriveRadius = 0f; // a new target never inherits the previous leg's arrival radius
        startTransform = null;
        startPosition = transform.position;

        targetTransform = null;
        targetPosition = newTarget;
        arrived = false;
        moving = true;

        if (AgentCanControl())
        {
            SafeSetIsStopped(false);
            agent.SetDestination(newTarget);
            lastTargetUpdateTime = Time.time;
            resampleAttempts = 0;
            loggedNoPathOnce = false;
            lastResampleTime = -10f;
        }
        else
        {
            if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} MoveTo(Vector3) called but agent cannot control. Ignoring.");
        }
    }

    public void MoveTo(Transform newTargetTransform)
    {
        legArriveRadius = 0f; // a new target never inherits the previous leg's arrival radius
        if (targetTransform != null)
        {
            startTransform = targetTransform;
        }
        else
        {
            startTransform = null;
            startPosition = transform.position;
        }

        targetTransform = newTargetTransform;
        arrived = false;
        moving = true;

        if (AgentCanControl() && targetTransform != null)
        {
            SafeSetIsStopped(false);
            agent.SetDestination(targetTransform.position);
            lastTargetUpdateTime = Time.time;
            resampleAttempts = 0;
            loggedNoPathOnce = false;
            lastResampleTime = -10f;
        }
        else
        {
            if (enableDebugLogs) Debug.LogWarning($"{LogPrefix()} MoveTo(Transform) called but agent cannot control or new target is null. Ignoring.");
        }
    }

    public void Stop()
    {
        moving = false;
        SafeSetIsStopped(true);

        walkAnimActive = false;

        ResetStallDetector();
        if (animator != null)
        {
            animator.SetBool(animatorIsWalkingParam, false);
            animator.SetFloat(animatorMoveSpeedParam, 0f);
        }
    }

    public void TeleportTo(Vector3 pos)
    {
        // Teleport both transform and agent
        ResetStallDetector();
        transform.position = pos;
        if (agent != null && agent.enabled)
        {
            agent.Warp(pos);
        }

        lastPosition = transform.position;
        smoothedSpeed = 0f;
    }

    Vector3 GetTargetPosition()
    {
        return targetTransform != null ? targetTransform.position : targetPosition;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Vector3 drawTarget = GetTargetPosition();
        Gizmos.DrawWireSphere(drawTarget, 0.12f);

        Gizmos.color = Color.cyan;
        Vector3 drawStart = startTransform != null ? startTransform.position : startPosition;
        Gizmos.DrawWireCube(drawStart + Vector3.up * 0.01f, Vector3.one * 0.2f);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(transform.position, drawTarget);
    }

    void OnDisable()
    {
        legArriveRadius = 0f; // pooled reuse starts without the previous life's per-leg radius

        // Pooled reuse must not inherit a halt/animator-freeze left over from the previous life (e.g. returned
        // to the pool while paused): a stale externallyFreezeAnimator keeps isWalking forced to false and a stale
        // externallyHaltMovement keeps re-stopping the agent. Cleared here (end of life), not in
        // ReinitializeAfterEnable(), so a dwell/pause applied during the new life's first frames is never undone.
        // externallyPaused is intentionally left alone (it only gates arrival detection).
        externallyHaltMovement = false;
        externallyFreezeAnimator = false;

        moving = false;
        SafeSetIsStopped(true);

        walkAnimActive = false;

        ResetStallDetector();
        if (animator != null)
        {
            animator.SetBool(animatorIsWalkingParam, false);
            animator.SetFloat(animatorMoveSpeedParam, 0f);
        }
    }

    void OnDestroy()
    {
        moving = false;
        SafeSetIsStopped(true);

        walkAnimActive = false;

        ResetStallDetector();
        if (animator != null)
        {
            animator.SetBool(animatorIsWalkingParam, false);
            animator.SetFloat(animatorMoveSpeedParam, 0f);
        }
    }

    // Helper to generate consistent log prefix including AgentID if available.
    string LogPrefix()
    {
        if (!string.IsNullOrEmpty(AgentID))
            return $"[MoveToTarget][AgentID={AgentID}]";
        return "[MoveToTarget]";
    }
}