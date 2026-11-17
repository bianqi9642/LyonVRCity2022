// AutoGroundAlign.cs
//
// Role: keeps the XR Origin on the ground height under the participant (terrain following). It does NOT move the participant horizontally and does not
// replace the CharacterController: the CharacterController (gravity, stepping, collisions) stays the mover; this component only trims the rig Y.
//
// P4 Part 3 fix (see docs/p4_ground_fix_report.md). The original implementation (a) cast the ground ray from the XR Origin ROOT, (b) interpolated with
// Lerp(y, groundY, Time.deltaTime * smoothSpeed) and (c) took any hit within 3 m. In session 20261116T142628Z_P4_Part3 the head (and therefore the
// CharacterController capsule that XRI's CharacterControllerDriver centres under the CAMERA) was 1.08 m away from the rig origin, and the application ran at
// ~9.3 FPS (deltaTime * 10 ~ 1.07 -> the Lerp parameter is clamped to 1 = an immediate snap). At a quay edge the origin crossed the edge while the capsule was
// still on the plateau; the ground under the ORIGIN was 0.3 - 1.1 m lower, the whole rig (and the capsule) was snapped down into/below the one-sided terrain
// surface, and gravity then pulled it through the terrain for ever.
//
// What is different now (all additive / defaults keep the old Inspector values working):
//   1. Sampling position = the CharacterController footprint (world-space capsule bottom centre computed from transform, center, height and scale), not
//      the XR Origin root. Falls back to the root when there is no CharacterController (previous behaviour).
//   2. A ground sample is only used when it is a finite, non-trigger hit whose normal is within the CharacterController slopeLimit.
//   3. Continuity guard: the sample must not be lower than the last accepted ground by more than stepOffset + horizontal distance * tan(slopeLimit)
//      (+ a small epsilon) - the largest drop a connected walkable surface can produce. A 4.7 m quay face therefore cannot become an alignment target; the
//      CharacterController then simply falls (or walks) as physics decides, and GroundFallRecovery is the safety net.
//   4. Frame-rate independent smoothing: alpha = 1 - exp(-smoothSpeed * dt) (~ dt*smoothSpeed at normal frame rates, never reaching 1 for dt <= 0.1 s with the
//      default speed) and a maximum vertical correction speed (default 2.5 m/s = ~1.8x the vertical speed of walking 1.4 m/s up a 45 degree slope), so even a
//      bad sample can never move the rig by more than speed * dt.
//   5. Fail-safe: no hit / invalid hit / rejected hit => the rig Y is left untouched (never zero, NaN, Infinity or an arbitrary lower position).
//   6. SuspendAlignment / ResetContinuity let GroundFallRecovery stop this component from undoing a recovery.
//
// Not changed: layers (groundLayer), raycastDistance, smoothSpeed, heightOffset, the Update-time execution, locomotion speed, tracking origin, frame rate.
//
// All comments in English per project convention.

using System;
using UnityEngine;

public class AutoGroundAlign : MonoBehaviour
{
    [Header("Raycast Settings")]
    public float raycastDistance = 3f;           // Max distance to check downward
    public LayerMask groundLayer;                // Layers considered as ground (set in Inspector)
    public float smoothSpeed = 10f;              // Smoothing rate (1/s) for vertical movement; applied frame-rate independently (1 - exp(-speed*dt))

    [Header("Offset")]
    public float heightOffset = 0.0f;            // Offset from ground (if needed)

    [Header("Footprint sampling (P4 fix)")]
    [Tooltip("Sample the ground under the CharacterController capsule footprint instead of the XR Origin root. Off = previous behaviour (root).")]
    public bool useControllerFootprint = true;

    [Tooltip("CharacterController whose footprint is sampled. Empty = the one on this GameObject (or its parent).")]
    public CharacterController controller;

    [Header("Safety (P4 fix)")]
    [Tooltip("Reject ground samples that are lower than the last accepted ground by more than stepOffset + horizontalDistance*tan(slopeLimit). " +
             "Prevents a cliff / quay edge from becoming an alignment target.")]
    public bool enableContinuityGuard = true;

    [Tooltip("Maximum vertical correction speed (m/s). 2.5 m/s is about 1.8x the vertical speed of walking at 1.4 m/s up a 45 degree slope, so legitimate " +
             "terrain following is never limited, while a bad sample cannot move the rig by more than this speed * dt.")]
    [Range(0.1f, 20f)] public float maxVerticalCorrectionSpeed = 2.5f;

    [Tooltip("Small tolerance (m) added to the continuity allowance for mesh triangulation and CharacterController skin width.")]
    [Range(0f, 0.5f)] public float continuityEpsilon = 0.1f;

    [Tooltip("A footprint move larger than this (m) between two updates is treated as a teleport: the continuity memory is reset.")]
    [Range(0.5f, 50f)] public float teleportResetMeters = 2.5f;

    [Tooltip("Log rejected samples (rate limited). Never per-frame.")]
    public bool enableDebugLogs = false;

    // ---- read-only state (diagnostics / tests) ----
    public bool HasValidGround { get; private set; }
    public float LastValidGroundY { get; private set; }
    public Vector3 LastFootprint { get; private set; }
    public string LastRejectReason { get; private set; } = "";
    public int RejectedSamples { get; private set; }
    public int AppliedCorrections { get; private set; }

    /// <summary>Optional unscaled clock (tests drive it with recorded frame times). Null = Time.realtimeSinceStartup.</summary>
    public Func<float> Clock;
    private float Now() { return Clock != null ? Clock() : Time.realtimeSinceStartup; }

    private Transform xrOrigin;
    private Vector3 lastAcceptedFootprint;
    private float suspendUntil = -1f;
    private float nextLog;

    void Start()
    {
        xrOrigin = this.transform;
        if (controller == null) controller = GetComponent<CharacterController>();
        if (controller == null) controller = GetComponentInParent<CharacterController>();
    }

    void Update()
    {
        Step(Time.deltaTime, Now());
    }

    /// <summary>Stops alignment for the given number of (unscaled) seconds, e.g. right after GroundFallRecovery restored the rig.</summary>
    public void SuspendAlignment(float seconds) { suspendUntil = Now() + Mathf.Max(0f, seconds); }

    /// <summary>Forgets the continuity memory (teleport / recovery): the next valid sample becomes the reference.</summary>
    public void ResetContinuity() { HasValidGround = false; }

    /// <summary>One alignment step with an explicit frame time (Update calls it with Time.deltaTime; tests call it with recorded frame times).</summary>
    public void Step(float dt, float now)
    {
        if (xrOrigin == null) { xrOrigin = this.transform; if (controller == null) controller = GetComponent<CharacterController>(); }
        if (now < suspendUntil) return;
        if (!(dt > 0f) || float.IsInfinity(dt)) return;                      // paused / invalid frame time: nothing to do (also rejects NaN)
        dt = Mathf.Min(dt, 1f);

        Vector3 root = xrOrigin.position;
        if (!IsFinite(root)) return;

        Vector3 foot = Footprint(root);
        LastFootprint = foot;
        if (!IsFinite(foot)) { Reject("footprint_not_finite"); return; }

        // a large horizontal footprint move between updates is a teleport / recovery: the old ground memory no longer applies
        if (HasValidGround)
        {
            float dx = foot.x - lastAcceptedFootprint.x, dz = foot.z - lastAcceptedFootprint.z;
            if (dx * dx + dz * dz > teleportResetMeters * teleportResetMeters) HasValidGround = false;
        }

        // Same start height semantics as the original (0.1 m above the rig base), but at the footprint's x/z.
        Vector3 up = Vector3.up;
        Vector3 start = new Vector3(foot.x, Mathf.Min(foot.y, root.y) + 0.1f, foot.z);
        if (!Physics.Raycast(new Ray(start, -up), out RaycastHit hit, raycastDistance, groundLayer, QueryTriggerInteraction.Ignore))
        { Reject("no_hit"); return; }

        if (hit.collider == null || !IsFinite(hit.point) || !IsFinite(hit.normal)) { Reject("invalid_hit"); return; }

        float slope = controller != null ? controller.slopeLimit : 45f;
        if (hit.normal.y < Mathf.Cos(Mathf.Clamp(slope, 1f, 89f) * Mathf.Deg2Rad)) { Reject("slope_too_steep"); return; }

        float groundY = hit.point.y;
        if (HasValidGround && enableContinuityGuard)
        {
            float dx = foot.x - lastAcceptedFootprint.x, dz = foot.z - lastAcceptedFootprint.z;
            float horiz = Mathf.Sqrt(dx * dx + dz * dz);
            float step = controller != null ? controller.stepOffset : 0.5f;
            float allowedDrop = step + horiz * Mathf.Tan(Mathf.Clamp(slope, 1f, 89f) * Mathf.Deg2Rad) + continuityEpsilon;
            if (LastValidGroundY - groundY > allowedDrop) { Reject("discontinuity_drop_" + (LastValidGroundY - groundY).ToString("F2")); return; }
        }

        // accepted ground sample
        HasValidGround = true; LastValidGroundY = groundY; lastAcceptedFootprint = foot;

        float target = groundY + heightOffset;
        if (!IsFinite(target)) { Reject("target_not_finite"); return; }
        float alpha = 1f - Mathf.Exp(-Mathf.Max(0f, smoothSpeed) * dt);       // frame-rate independent form of dt*smoothSpeed
        float newY = Mathf.Lerp(root.y, target, alpha);
        float delta = newY - root.y;
        float maxDelta = maxVerticalCorrectionSpeed * dt;
        delta = Mathf.Clamp(delta, -maxDelta, maxDelta);
        if (Mathf.Abs(delta) < 1e-6f) return;

        xrOrigin.position = new Vector3(root.x, root.y + delta, root.z);
        AppliedCorrections++;
    }

    /// <summary>World-space bottom centre of the CharacterController capsule (x/z of the real collision footprint, y of the rig base). Root when there is no controller.</summary>
    private Vector3 Footprint(Vector3 root)
    {
        if (!useControllerFootprint || controller == null) return root;
        Transform t = controller.transform;
        Vector3 s = t.lossyScale;
        float h = Mathf.Max(controller.height * Mathf.Abs(s.y), 2f * controller.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)));
        Vector3 centre = t.TransformPoint(controller.center);
        Vector3 bottom = centre - t.up * (h * 0.5f);
        return new Vector3(bottom.x, bottom.y, bottom.z);
    }

    private void Reject(string reason)
    {
        RejectedSamples++; LastRejectReason = reason;
        if (enableDebugLogs && Now() >= nextLog)
        {
            nextLog = Now() + 2f;
            Debug.Log("[AutoGroundAlign] sample rejected: " + reason + " (rig unchanged).", this);
        }
    }

    private static bool IsFinite(float f) { return !(float.IsNaN(f) || float.IsInfinity(f)); }

    private static bool IsFinite(Vector3 v)
    {
        return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }

    private void OnDrawGizmosSelected()
    {
        // Gizmo to visualize the raycast (footprint when available)
        Gizmos.color = Color.yellow;
        Vector3 startPos = (useControllerFootprint && controller != null ? Footprint(transform.position) : transform.position) + Vector3.up * 0.1f;
        Gizmos.DrawLine(startPos, startPos + Vector3.down * raycastDistance);
    }
}
