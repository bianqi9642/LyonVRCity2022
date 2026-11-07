// GroundFallRecovery.cs
// Role: SECONDARY safety net for the participant rig. It does not repair ground; it prevents a participant from staying permanently
//   under / through the terrain, or flung into empty space, if something removes the ground from under the rig.
// Why it exists: AutoGroundAlign casts only 0.1 m above the rig base and 3 m down, so a rig that has dropped below the surface (or through a hole) is
//   never pulled back. The rig is moved by a CharacterController (Omnideck / XRI continuous move with gravity), so a missing ground triangle means a fall.
// Integration: lives on the XR Origin (same GameObject as AutoGroundAlign / CharacterController, = ExperimentManager.playerRig). Purely local: it does not call
//   ExperimentManager, does not touch recorders, the trial lifecycle, the NavMesh, or AutoGroundAlign. It writes only the rig transform, mirroring TeleportPlayerTo.
//
// Two recovery paths (both restore to a position that HAD ground support):
//   A. FALL (continuous): the rig has had NO ground support for >= noSupportSeconds (unscaled) and is either more than fallDistance below the last safe Y
//      or a Ground surface lies more than underSurfaceMargin ABOVE the rig base (rig is under the terrain).
//   B. VOID DISPLACEMENT (added after sessions 20261106T140500Z / T142958Z): a single-frame move larger than teleportJumpMeters is first treated as a teleport
//      (the old safe position is dropped, nothing is restored). But if the DESTINATION still has no Ground support voidDisplacementSeconds later, the move is
//      undone: the rig returns to the last supported position it had BEFORE the jump. A legitimate teleport lands on ground and is never undone; a
//      displacement into empty space (e.g. a faulty position write) always is. Before this path existed, a runaway of ~1000 m per frame was classified as
//      "teleport" every frame, the safe position was reset every frame, and no recovery was ever possible.
// Never fires: while a jump destination is supported (a real teleport), during the cooldown after a recovery, or when no supported position is known.
// Participant data: the rig pose jumps back to the restored position; each recovery is logged once and exposed through OnFallRecovered / LastRecoveryReason.
// Diagnostics (writeDiagnosticsFile, default on): event-driven CSV  ground_recovery_<UTC>.csv  in Application.persistentDataPath (open / write / close per row,
//   clock = Time.realtimeSinceStartupAsDouble like the recorders). It does NOT touch any recorder schema. Rows: init (ground collider inventory), jump_detected,
//   jump_destination_supported, support_lost, support_regained, recovery (reason, from, to), plus one state row per second while support is missing.
using System;
using System.Globalization;
using System.IO;
using System.Text;
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

    [Header("Fall detection (path A)")]
    [Tooltip("Rig must be without ground support this long (unscaled seconds) before a fall is declared.")]
    [Range(0.2f, 5f)] public float noSupportSeconds = 0.6f;

    [Tooltip("Fall: rig is this far (m) below the last safe Y while unsupported.")]
    [Range(0.3f, 20f)] public float fallDistance = 1.0f;

    [Tooltip("Under-terrain: a ground surface this far (m) above the rig base, found by a ray from high above, also counts as a fall.")]
    [Range(0.2f, 5f)] public float underSurfaceMargin = 0.6f;

    [Tooltip("Height (m) above the rig base from which the under-terrain ray starts.")]
    [Range(5f, 200f)] public float underSurfaceRayHeight = 60f;

    [Header("Teleport / void displacement (path B)")]
    [Tooltip("A per-frame move larger than this (m) is a jump: the safe position is reset and the destination is judged (see voidDisplacementSeconds).")]
    [Range(0.5f, 50f)] public float teleportJumpMeters = 2.5f;

    [Tooltip("Undo a jump whose destination still has no Ground support after this many (unscaled) seconds. Real teleports land on ground and are never undone.")]
    public bool recoverFromVoidDisplacement = true;

    [Range(0f, 5f)] public float voidDisplacementSeconds = 0.5f;

    [Tooltip("Seconds after a recovery or a detected jump during which path A is not evaluated.")]
    [Range(0f, 10f)] public float cooldownSeconds = 2f;

    [Tooltip("Lift (m) applied to the restored position so the capsule is not re-seated inside the surface.")]
    [Range(0f, 0.5f)] public float restoreLift = 0.05f;

    [Header("Diagnostics")]
    public bool enableDebugLogs = false;

    [Tooltip("Write ground_recovery_<UTC>.csv (event-driven, tiny) to Application.persistentDataPath. Independent of every recorder.")]
    public bool writeDiagnosticsFile = true;

    /// <summary>Raised after the rig was restored: (fromPosition, toPosition).</summary>
    public event Action<Vector3, Vector3> OnFallRecovered;

    public int RecoveryCount { get; private set; }
    public string LastRecoveryReason { get; private set; } = "";
    public string DiagnosticsPath { get; private set; } = "";

    Vector3 lastSafe; bool hasSafe;
    Vector3 lastPos; bool hasLastPos; bool lastSupported;
    float unsupportedSince = -1f;
    float cooldownUntil;
    bool pendingJump; Vector3 jumpOrigin; bool jumpOriginSupported; float jumpSince;
    bool prevSupportedForLog = true; float nextStateLog;
    CharacterController cc;
    string lastHitInfo = "";

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

    void Start()
    {
        if (writeDiagnosticsFile) LogInventory();
    }

    void OnEnable() { hasLastPos = false; unsupportedSince = -1f; pendingJump = false; }

    void LateUpdate()
    {
        float now = Time.realtimeSinceStartup;
        Vector3 p = rig.position;

        // jump (scripted teleport, spawn ... or a faulty position write): drop the old safe position, remember where we came from, judge the destination below
        if (hasLastPos && (p - lastPos).sqrMagnitude > teleportJumpMeters * teleportJumpMeters)
        {
            if (!pendingJump)
            {
                pendingJump = true; jumpSince = now; jumpOrigin = lastPos; jumpOriginSupported = lastSupported;
                Diag("jump_detected", p, "jump_m=" + (p - lastPos).magnitude.ToString("F1", CultureInfo.InvariantCulture) + ";origin_supported=" + lastSupported);
            }
            hasSafe = false; unsupportedSince = -1f; cooldownUntil = now + cooldownSeconds;
            if (enableDebugLogs) Debug.Log("[GroundFallRecovery] jump detected, safe position reset");
        }
        lastPos = p; hasLastPos = true;

        bool supported = HasSupport(p, out float groundY);
        if (supported != prevSupportedForLog) { Diag(supported ? "support_regained" : "support_lost", p, lastHitInfo); prevSupportedForLog = supported; nextStateLog = now + 1f; }
        else if (!supported && now >= nextStateLog) { Diag("state_unsupported", p, "unsupported_s=" + (unsupportedSince < 0 ? "0" : (now - unsupportedSince).ToString("F2", CultureInfo.InvariantCulture)) + ";pending_jump=" + pendingJump); nextStateLog = now + 1f; }

        if (pendingJump)
        {
            if (supported) { pendingJump = false; Diag("jump_destination_supported", p, lastHitInfo); }          // a real teleport: nothing to undo
            else if (recoverFromVoidDisplacement && jumpOriginSupported && now - jumpSince >= voidDisplacementSeconds)
            {
                pendingJump = false;
                RestoreTo(jumpOrigin + Vector3.up * restoreLift, p, "void_displacement");
                lastSupported = true;
                return;
            }
        }
        lastSupported = supported;

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
        if (below || underSurface) RestoreTo(lastSafe + Vector3.up * restoreLift, p, below ? "fell_below_safe_y" : "under_terrain");
    }

    bool HasSupport(Vector3 p, out float groundY)
    {
        groundY = p.y;
        if (!Physics.Raycast(p + Vector3.up * supportRayStart, Vector3.down, out RaycastHit hit, supportRayStart + supportBelow, groundMask, QueryTriggerInteraction.Ignore))
        {
            lastHitInfo = "no_hit";
            return false;
        }
        groundY = hit.point.y;
        float dy = hit.point.y - p.y;   // + = surface above the base
        lastHitInfo = "hit=" + HierPath(hit.collider.transform) + ";layer=" + hit.collider.gameObject.layer + ";enabled=" + hit.collider.enabled + ";mesh=" + MeshName(hit.collider) + ";dy=" + dy.ToString("F3", CultureInfo.InvariantCulture);
        return dy <= supportAbove && dy >= -supportBelow;
    }

    void RestoreTo(Vector3 to, Vector3 from, string reason)
    {
        bool ccWasOn = cc != null && cc.enabled;
        if (ccWasOn) cc.enabled = false;           // a CharacterController overrides direct transform writes
        rig.position = to;
        if (ccWasOn) cc.enabled = true;
        Physics.SyncTransforms();
        RecoveryCount++; LastRecoveryReason = reason; unsupportedSince = -1f; cooldownUntil = Time.realtimeSinceStartup + cooldownSeconds; lastPos = to; lastSupported = true;
        Debug.LogWarning("[GroundFallRecovery] " + reason + ": rig at " + from.ToString("F2") + " restored to " + to.ToString("F2") + " (recovery #" + RecoveryCount + ")");
        Diag("recovery", to, "reason=" + reason + ";from=" + from.x.ToString("F2", CultureInfo.InvariantCulture) + "|" + from.y.ToString("F2", CultureInfo.InvariantCulture) + "|" + from.z.ToString("F2", CultureInfo.InvariantCulture) + ";count=" + RecoveryCount);
        OnFallRecovered?.Invoke(from, to);
    }

    // ---------------------------------------------------------------- diagnostics (optional, independent of every recorder)
    static string HierPath(Transform t) { string s = t.name; for (var q = t.parent; q != null; q = q.parent) s = q.name + "/" + s; return s; }
    static string MeshName(Collider c) { var mc = c as MeshCollider; return mc != null && mc.sharedMesh != null ? mc.sharedMesh.name : c.GetType().Name; }

    void LogInventory()
    {
        try
        {
            int active = 0, inactive = 0, repaired = 0; var sb = new StringBuilder();
            foreach (var mc in FindObjectsOfType<MeshCollider>(true))
            {
                if (((1 << mc.gameObject.layer) & groundMask.value) == 0 || mc.sharedMesh == null) continue;
                bool on = mc.enabled && mc.gameObject.activeInHierarchy; if (on) active++; else inactive++;
                if (mc.sharedMesh.name.StartsWith("Ground_")) repaired++;
            }
            sb.Append("ground_mesh_colliders_active=").Append(active).Append(";inactive=").Append(inactive).Append(";using_repaired_mesh=").Append(repaired)
              .Append(";cc=").Append(cc != null ? "enabled=" + cc.enabled + ",h=" + cc.height.ToString("F3", CultureInfo.InvariantCulture) + ",r=" + cc.radius.ToString("F3", CultureInfo.InvariantCulture) + ",skin=" + cc.skinWidth.ToString("F3", CultureInfo.InvariantCulture) : "none");
            Diag("init", rig.position, sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("[GroundFallRecovery] inventory failed: " + e.Message); }
    }

    void Diag(string evt, Vector3 p, string detail)
    {
        if (!writeDiagnosticsFile) return;
        try
        {
            if (string.IsNullOrEmpty(DiagnosticsPath))
            {
                DiagnosticsPath = System.IO.Path.Combine(Application.persistentDataPath, "ground_recovery_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".csv");
                File.AppendAllText(DiagnosticsPath, "t_abs,event,rig_x,rig_y,rig_z,cc_enabled,cc_is_grounded,head_x,head_y,head_z,last_safe_valid,last_safe_x,last_safe_y,last_safe_z,detail\n");
            }
            var cam = Camera.main; Vector3 h = cam != null ? cam.transform.position : Vector3.zero; var ci = CultureInfo.InvariantCulture;
            string line = Time.realtimeSinceStartupAsDouble.ToString("F4", ci) + "," + evt + "," + p.x.ToString("F3", ci) + "," + p.y.ToString("F3", ci) + "," + p.z.ToString("F3", ci) + ","
                + (cc != null && cc.enabled ? "1" : "0") + "," + (cc != null && cc.enabled && cc.isGrounded ? "1" : "0") + ","
                + h.x.ToString("F3", ci) + "," + h.y.ToString("F3", ci) + "," + h.z.ToString("F3", ci) + ","
                + (hasSafe ? "1" : "0") + "," + lastSafe.x.ToString("F3", ci) + "," + lastSafe.y.ToString("F3", ci) + "," + lastSafe.z.ToString("F3", ci) + ","
                + (detail ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ') + "\n";
            File.AppendAllText(DiagnosticsPath, line);   // open / write / close per row
        }
        catch (Exception e) { writeDiagnosticsFile = false; Debug.LogWarning("[GroundFallRecovery] diagnostics disabled: " + e.Message); }
    }

    void OnDestroy() { OnFallRecovered = null; }
}
