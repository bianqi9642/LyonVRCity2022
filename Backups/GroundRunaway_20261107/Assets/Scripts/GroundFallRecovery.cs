// GroundFallRecovery.cs
// Role: SECONDARY safety net for the participant rig. It does not repair ground; it only prevents a participant from staying permanently
//   under / through the terrain if a gap in the ground collider is ever crossed (the ground itself is repaired by the TIN collision proxy meshes).
// Why it exists: AutoGroundAlign casts only 0.1 m above the rig base and 3 m down, so a rig that has dropped below the surface (or through a hole) is
//   never pulled back. The rig is moved by a CharacterController (Omnideck / XRI continuous move with gravity), so a missing ground triangle means a fall.
// Integration: lives on the XR Origin (same GameObject as AutoGroundAlign / CharacterController, = ExperimentManager.playerRig). Purely local: it does not call
//   ExperimentManager, does not touch recorders, the trial lifecycle, the NavMesh, or AutoGroundAlign. It writes only the rig transform, mirroring TeleportPlayerTo.
// Activation conditions (all must hold):
//   1. the component is enabled and a last SAFE position exists (a position where the ground ray found Ground-layer support and the rig base was on the surface, not sinking into it);
//   2. the rig has had NO ground support for >= noSupportSeconds (unscaled), and
//   3. it is either more than fallDistance below the last safe Y, or a Ground surface lies more than underSurfaceMargin ABOVE the rig base (rig is under the terrain).
// It never fires: right after a teleport (a per-frame jump > teleportJumpMeters resets the safe position), during the cooldown, or while no safe position is known.
// Participant data: the rig pose jumps back to the safe position; this is logged once per recovery and exposed through OnFallRecovered for anyone who wants to annotate data.
using System;
using UnityEngine;

[DisallowMultipleComponent]
public class GroundFallRecovery : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform that is restored. Default: this transform (the XR Origin, = ExperimentManager.playerRig).")]
    public Transform rig;

    [Tooltip("Layers that count as walkable ground. Default (0 = unset): the project's 'Ground' layer.")]
    public LayerMask groundMask;

    [Header("Ground support test")]
    [Tooltip("Ray origin height above the rig base for the 'is there ground under my feet' test (m).")]
    [Range(0.2f, 2f)] public float supportRayStart = 0.6f;

    [Tooltip("Support counts when a ground hit lies within this distance BELOW the rig base (m).")]
    [Range(0.1f, 2f)] public float supportBelow = 0.5f;

    [Tooltip("...or within this distance ABOVE the rig base (m; AutoGroundAlign smoothing / step tolerance).")]
    [Range(0.05f, 1f)] public float supportAbove = 0.5f;

    [Tooltip("A position is stored as 'last safe' only if the surface is at most this far (m) above the rig base (i.e. the rig is not sinking into the ground).")]
    [Range(0f, 0.5f)] public float safeMaxSink = 0.1f;

    [Header("Fall detection")]
    [Tooltip("Rig must be without ground support this long (unscaled seconds) before a fall is declared.")]
    [Range(0.2f, 5f)] public float noSupportSeconds = 0.6f;

    [Tooltip("Fall: rig is this far (m) below the last safe Y while unsupported.")]
    [Range(0.3f, 20f)] public float fallDistance = 1.0f;

    [Tooltip("Under-terrain: a ground surface this far (m) above the rig base, found by a ray from high above, also counts as a fall.")]
    [Range(0.2f, 5f)] public float underSurfaceMargin = 0.6f;

    [Tooltip("Height (m) above the rig base from which the under-terrain ray starts.")]
    [Range(5f, 200f)] public float underSurfaceRayHeight = 60f;

    [Header("Teleport / transitions")]
    [Tooltip("A per-frame move larger than this (m) is a scripted teleport (TeleportPlayerTo, river point): the safe position is reset, nothing is restored.")]
    [Range(0.5f, 50f)] public float teleportJumpMeters = 2.5f;

    [Tooltip("Seconds after a recovery or a detected teleport during which nothing is triggered.")]
    [Range(0f, 10f)] public float cooldownSeconds = 2f;

    [Tooltip("Lift (m) applied to the restored position so the capsule is not re-seated inside the surface.")]
    [Range(0f, 0.5f)] public float restoreLift = 0.05f;

    [Header("Diagnostics")]
    public bool enableDebugLogs = false;

    /// <summary>Raised after the rig was restored: (fromPosition, toPosition).</summary>
    public event Action<Vector3, Vector3> OnFallRecovered;

    public int RecoveryCount { get; private set; }

    Vector3 lastSafe; bool hasSafe;
    Vector3 lastPos; bool hasLastPos;
    float unsupportedSince = -1f;
    float cooldownUntil;
    CharacterController cc;

    void Awake()
    {
        if (rig == null) rig = transform;
        if (groundMask.value == 0)
        {
            int m = LayerMask.GetMask("Ground");
            var aga = GetComponent<AutoGroundAlign>();
            groundMask = m != 0 ? m : (aga != null ? aga.groundLayer : (LayerMask)(1 << 3));
        }
        cc = rig.GetComponent<CharacterController>();
    }

    void OnEnable() { hasLastPos = false; unsupportedSince = -1f; }

    void LateUpdate()
    {
        float now = Time.realtimeSinceStartup;
        Vector3 p = rig.position;

        // scripted teleport / spawn: forget the old safe position, wait for fresh support before arming again
        if (hasLastPos && (p - lastPos).sqrMagnitude > teleportJumpMeters * teleportJumpMeters)
        {
            hasSafe = false; unsupportedSince = -1f; cooldownUntil = now + cooldownSeconds;
            if (enableDebugLogs) Debug.Log("[GroundFallRecovery] teleport detected, safe position reset");
        }
        lastPos = p; hasLastPos = true;

        bool supported = HasSupport(p, out float groundY);
        if (supported)
        {
            unsupportedSince = -1f;
            // remember a position only when the feet are essentially ON the surface (not merely within the 'under-surface' tolerance), and remember the SURFACE height there
            if ((now >= cooldownUntil || !hasSafe) && groundY - p.y <= safeMaxSink) { lastSafe = new Vector3(p.x, groundY, p.z); hasSafe = true; }
            return;
        }
        if (!hasSafe || now < cooldownUntil) return;
        if (unsupportedSince < 0f) unsupportedSince = now;
        if (now - unsupportedSince < noSupportSeconds) return;

        bool below = p.y < lastSafe.y - fallDistance;
        bool underSurface = false;
        if (!below)
        {
            if (Physics.Raycast(p + Vector3.up * underSurfaceRayHeight, Vector3.down, out RaycastHit h, underSurfaceRayHeight + 5f, groundMask, QueryTriggerInteraction.Ignore))
                underSurface = h.point.y > p.y + underSurfaceMargin;
        }
        if (below || underSurface) Recover(p);
    }

    bool HasSupport(Vector3 p, out float groundY)
    {
        groundY = p.y;
        if (!Physics.Raycast(p + Vector3.up * supportRayStart, Vector3.down, out RaycastHit hit, supportRayStart + supportBelow, groundMask, QueryTriggerInteraction.Ignore))
            return false;
        groundY = hit.point.y;
        float dy = hit.point.y - p.y;   // + = surface above the base
        return dy <= supportAbove && dy >= -supportBelow;
    }

    void Recover(Vector3 from)
    {
        Vector3 to = lastSafe + Vector3.up * restoreLift;
        bool ccWasOn = cc != null && cc.enabled;
        if (ccWasOn) cc.enabled = false;           // a CharacterController overrides direct transform writes
        rig.position = to;
        if (ccWasOn) cc.enabled = true;
        Physics.SyncTransforms();
        RecoveryCount++; unsupportedSince = -1f; cooldownUntil = Time.realtimeSinceStartup + cooldownSeconds; lastPos = to;
        Debug.LogWarning("[GroundFallRecovery] rig fell through / under the ground at " + from.ToString("F2") + "; restored to last safe position " + to.ToString("F2") + " (recovery #" + RecoveryCount + ")");
        OnFallRecovered?.Invoke(from, to);
    }

    void OnDestroy() { OnFallRecovered = null; }
}
