// GroundFallRecovery.cs
// Role: SECONDARY safety net for the participant rig. It does not repair ground; it prevents a participant from staying permanently
//   under / through the terrain, or flung into empty space, if something removes the ground from under the rig.
// Why it exists: the rig is moved by a CharacterController (Omnideck / XRI continuous move with gravity), so a missing / one-sided ground triangle means a fall.
// Integration: lives on the XR Origin (same GameObject as AutoGroundAlign / CharacterController, = ExperimentManager.playerRig). It writes only the rig transform,
//   mirroring TeleportPlayerTo, and reports every recovery through ExperimentManager.ReportParticipantFallRecovery (additive API -> one "ground_recovery"
//   event row in events.csv, no schema change) and its own diagnostics CSV.
//
// Recovery paths (all restore to a position that HAD valid ground support):
//   A. FALL (continuous): no ground support under the capsule footprint for >= noSupportSeconds (unscaled) and the rig is more than fallDistance below the last
//      safe Y (or a Ground surface lies more than underSurfaceMargin ABOVE the rig base = under the terrain).
//   B. VOID DISPLACEMENT: a single-frame move larger than teleportJumpMeters is treated as a teleport (the old safe position is dropped). If the DESTINATION still has no
//      Ground support voidDisplacementSeconds later the move is undone.
//   C. HARD FLOOR (new): the rig is more than hardFloorMeters below the last safe position and unsupported. Independent of support flicker, cooldowns and of the
//      jump classification; the last-resort stop for a fall that continues for ever.
//
// P4 Part 3 fix - what changed and why (session 20261116T142628Z_P4_Part3: a 22 s fall to Y = -2029 m with no recovery; the cause of the missing recovery in the lab
//   run could NOT be established from the session data, so the weaknesses found by code review were removed):
//   1. Support is measured under the CharacterController footprint (capsule x/z), not the rig root, and only counts for walkable normals (slopeLimit).
//   2. The last safe position is only advanced with a continuity check (same rule as AutoGroundAlign): a sample far below the previous safe level (quay face, cliff)
//      is NOT accepted as safe until the participant has stood on it for stableSupportSeconds. Before, the "safe" position followed a descent down a steep face.
//   3. Jump classification trap: at ~9 FPS a free fall exceeds teleportJumpMeters (2.5 m) per frame after ~2.5 s; every frame was then classified as a "teleport",
//      the safe position was dropped and the cooldown restarted each frame, so path A could never fire again. A downward move that continues an unsupported
//      descent is no longer a jump.
//   4. Restoration is verified: after a recovery the footprint must be supported again (verifyWithinSeconds); otherwise the next older safe position (small ring of
//      validated positions) is tried; a bounded number of attempts prevents a recovery loop.
//   5. AutoGroundAlign is told to stand down (SuspendAlignment / ResetContinuity) so it cannot undo the recovery.
//   6. Non-finite rig positions are handled; every recovery writes a diagnostic row with the fields needed to audit it.
// Second-stage audit changes (continuity-based; see docs/p4_second_stage_audit_report.md):
//   7. Abrupt-drop landing: a landing after an unsupported episode that is lower than the last safe ground by more than a CONNECTED walk could produce
//      (stepOffset + horizontalDistance * tan(slopeLimit) + 0.1 m, the continuity-guard rule) is an abrupt drop (quay edge, wall, cliff) and is recovered
//      ("abrupt_drop_landing") instead of being accepted as a new safe level. A gradual connected descent never loses support and is never affected.
//   8. The landing probe no longer treats ANY walkable ground within 15 m as a legitimate landing: it only holds path A for ground that lies inside the continuity floor.
//   9. A fall that is already faster than fallVelocityThreshold overrides the post-recovery / post-teleport cooldown (previously only the 20 m hard floor, ~2 s later, applied).
//  10. A downward move that is consistent with gravity for THIS frame time (first airborne frames of a long frame) is a fall, not a jump (void_displacement trap at dt >= ~0.5 s,
//      and for a single 0.34 s frame during the first 0.4 s of a fall).
// Execution: LateUpdate (after all Updates); the logic lives in Tick(now) so it can be driven with recorded frame times. Time basis: Time.realtimeSinceStartup
//   (unscaled), robust to low frame rates.
// Diagnostics (writeDiagnosticsFile, default on): event-driven CSV  ground_recovery_<UTC>.csv  in Application.persistentDataPath (open / write / close per row).
//   Header (schema 3): the original columns, then vertical_velocity,ground_valid,ground_y,head_to_origin_xz,teleport_pending,footprint_x,footprint_z,
//   attempt,session_id, then (schema 3) part,phase,recovery_index. Separate from every recorder; no recorder schema is touched.
using System;
using System.Collections.Generic;
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

    [Header("Robustness (P4 fix)")]
    [Tooltip("Measure ground support under the CharacterController footprint (capsule x/z) instead of the rig root.")]
    public bool useControllerFootprint = true;

    [Tooltip("Advance the 'last safe' position only through the continuity rule (a sudden big drop is not accepted as safe until stableSupportSeconds of stable support).")]
    public bool enableContinuityGuard = true;

    [Tooltip("Seconds of continuous support on a much lower level before it is accepted as the new safe level (a participant who legitimately stepped down stays there).")]
    [Range(0.2f, 10f)] public float stableSupportSeconds = 1.0f;

    [Tooltip("Path C: recover when the rig is this far (m) below the last safe position while unsupported, whatever the cooldown / jump state. A fall of 20 m is never legitimate in the city model.")]
    [Range(3f, 500f)] public float hardFloorMeters = 20f;

    [Tooltip("Downward speed (m/s) from which an unsupported descent counts as a fall in progress (so a large per-frame displacement is not mistaken for a teleport).")]
    [Range(1f, 30f)] public float fallVelocityThreshold = 4f;

    [Tooltip("After a recovery the footprint must be supported again within this many seconds, otherwise the next older safe position is tried.")]
    [Range(0.2f, 5f)] public float verifyWithinSeconds = 1.0f;

    [Tooltip("Escalation attempts for one incident before the escalation wraps around to the newest safe position again (never gives up: a participant must not be left falling).")]
    [Range(1, 10)] public int maxAttemptsPerIncident = 4;

    [Tooltip("Landing probe: while unsupported, a ray this long (m) from the footprint downwards that still finds walkable Ground means the participant is dropping onto lower ground (e.g. stepped off a quay) - path A waits for the landing instead of treating it as a fall. A fall with NO ground below (under the terrain / through a hole) is recovered as before.")]
    [Range(0f, 100f)] public float landingProbeMeters = 15f;

    [Tooltip("Upper bound (s) for waiting for a landing (a 15 m drop takes about 1.8 s). After this the fall is recovered even if ground is found below.")]
    [Range(0.5f, 10f)] public float maxAirborneSeconds = 3f;

    [Tooltip("Spacing (m) between validated safe positions kept in the escalation ring.")]
    [Range(0.5f, 20f)] public float safeRingSpacing = 2f;

    [Header("Continuity-based landing (second-stage audit)")]
    [Tooltip("A landing after an unsupported episode that is lower than the last safe ground by more than a connected walk could produce (stepOffset + horizontalDistance*tan(slopeLimit) + 0.1 m) is an abrupt drop (quay edge, wall, cliff): restore to an earlier validated position instead of accepting the lower level. Gradual connected descents never lose support and are not affected.")]
    public bool recoverAbruptDropLanding = true;

    [Tooltip("A fall already faster than fallVelocityThreshold is recovered even while the post-recovery / post-teleport cooldown is running (otherwise only the hard floor stops it).")]
    public bool fastFallOverridesCooldown = true;

    [Header("Diagnostics")]
    public bool enableDebugLogs = false;

    [Tooltip("Write ground_recovery_<UTC>.csv (event-driven, tiny) to Application.persistentDataPath. Independent of every recorder.")]
    public bool writeDiagnosticsFile = true;

    /// <summary>Raised after the rig was restored: (fromPosition, toPosition).</summary>
    public event Action<Vector3, Vector3> OnFallRecovered;

    public int RecoveryCount { get; private set; }
    public string LastRecoveryReason { get; private set; } = "";
    public string DiagnosticsPath { get; private set; } = "";

    /// <summary>Optional unscaled clock (tests drive it with recorded frame times). Null = Time.realtimeSinceStartup.</summary>
    public Func<float> Clock;
    float Now() { return Clock != null ? Clock() : Time.realtimeSinceStartup; }

    Vector3 lastSafe; bool hasSafe;
    readonly List<Vector3> safeRing = new List<Vector3>();
    Vector3 lastPos; bool hasLastPos; float lastNow; bool lastSupported; float lastVy;
    float unsupportedSince = -1f;
    float cooldownUntil;
    bool pendingJump; Vector3 jumpOrigin; bool jumpOriginSupported; float jumpSince;
    bool prevSupportedForLog = true; float nextStateLog;
    float levelSince = -1f;
    bool verifyActive; float verifyDeadline; float restoredAt; int attempt;
    Vector3 jumpTarget; bool jumpTargetValid;
    bool airborneSinceSafe;
    CharacterController cc;
    AutoGroundAlign aga;
    ExperimentManager experimentManager;
    DataCaptureManager capture;
    string lastHitInfo = "";
    bool lastGroundValid; float lastGroundY;

    void Awake()
    {
        if (rig == null) rig = transform;
        aga = GetComponent<AutoGroundAlign>();
        if (groundMask.value == 0)
        {
            int m = LayerMask.GetMask("Ground");
            groundMask = m != 0 ? m : (aga != null ? aga.groundLayer : (LayerMask)(1 << 3));
        }
        cc = rig.GetComponent<CharacterController>();
    }

    void Start()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (capture == null) capture = FindObjectOfType<DataCaptureManager>();
        if (experimentManager == null && enableDebugLogs) Debug.LogWarning("[GroundFallRecovery] No ExperimentManager found: recoveries are written to the diagnostics file only.", this);
        if (writeDiagnosticsFile) LogInventory();
    }

    void OnEnable() { hasLastPos = false; unsupportedSince = -1f; pendingJump = false; verifyActive = false; levelSince = -1f; attempt = 0; airborneSinceSafe = false; }

    void LateUpdate()
    {
        Tick(Now());
    }

    /// <summary>One safety evaluation. 'now' is an unscaled clock (Time.realtimeSinceStartup); tests drive it with recorded frame times.</summary>
    public void Tick(float now)
    {
        if (rig == null) return;
        Vector3 p = rig.position;
        if (!IsFinite(p))
        {
            // a non-finite rig position must never persist: restore the newest safe position if there is one
            if (hasSafe) RestoreTo(PickRestore() + Vector3.up * restoreLift, lastHadPos ? lastPos : Vector3.zero, "non_finite_position");
            return;
        }

        float dt = hasLastPos ? Mathf.Max(now - lastNow, 0f) : 0f;
        float vy = (hasLastPos && dt > 1e-4f) ? (p.y - lastPos.y) / dt : 0f;
        lastNow = now;

        bool supportedNow = HasSupport(p, out float groundYNow);
        // jump (scripted teleport, spawn ... or a faulty position write): drop the old safe position, remember where we came from, judge the destination below.
        // A downward move that CONTINUES an unsupported descent is a fall, not a jump (P4: at ~9 FPS a free fall exceeds teleportJumpMeters per frame).
        if (hasLastPos)
        {
            Vector3 d = p - lastPos; float d2 = d.sqrMagnitude;
            if (d2 > teleportJumpMeters * teleportJumpMeters)
            {
                bool continuesFall = !lastSupported && lastVy < -fallVelocityThreshold && d.y < 0f && (-d.y) >= 0.8f * Mathf.Sqrt(d2);
                // Second-stage audit: a long frame (hitch) makes the displacement of the FIRST airborne frames exceed teleportJumpMeters although the motion is exactly what gravity
                // produces for that frame time ((previous speed + g*dt) * dt, semi-implicit integration like the XRI provider). Such a move into unsupported space is a fall.
                if (!continuesFall && dt > 0f && !supportedNow && d.y < 0f && (-d.y) >= 0.8f * Mathf.Sqrt(d2))
                {
                    float g = Mathf.Abs(Physics.gravity.y);
                    if ((-d.y) <= (Mathf.Max(0f, -lastVy) + g * dt) * dt * 1.25f + 0.05f) continuesFall = true;
                }
                if (!continuesFall)
                {
                    if (!pendingJump)
                    {
                        pendingJump = true; jumpSince = now; jumpOrigin = lastPos; jumpOriginSupported = lastSupported;
                        // where to return to if the destination turns out to be void: the last safe position (a fall whose first airborne frame already exceeds teleportJumpMeters
                        // at a large frame time is indistinguishable from a teleport for one frame, and the previous frame was then already unsupported), else the previous position
                        jumpTargetValid = hasSafe || lastSupported; jumpTarget = hasSafe ? lastSafe : lastPos;
                        Diag("jump_detected", p, "jump_m=" + Mathf.Sqrt(d2).ToString("F1", CultureInfo.InvariantCulture) + ";origin_supported=" + lastSupported, vy);
                    }
                    hasSafe = false; unsupportedSince = -1f; cooldownUntil = now + cooldownSeconds; levelSince = -1f;
                    attempt = 0; verifyActive = false; safeRing.Clear();   // positions from before a teleport are not valid recovery targets after it
                    if (aga != null) aga.ResetContinuity();
                    if (enableDebugLogs) Debug.Log("[GroundFallRecovery] jump detected, safe position reset");
                }
            }
        }
        lastPos = p; hasLastPos = true; lastHadPos = true;

        bool supported = supportedNow; float groundY = groundYNow;
        lastGroundValid = supported; lastGroundY = groundY;
        if (supported != prevSupportedForLog) { Diag(supported ? "support_regained" : "support_lost", p, lastHitInfo, vy); prevSupportedForLog = supported; nextStateLog = now + 1f; }
        else if (!supported && now >= nextStateLog) { Diag("state_unsupported", p, "unsupported_s=" + (unsupportedSince < 0 ? "0" : (now - unsupportedSince).ToString("F2", CultureInfo.InvariantCulture)) + ";pending_jump=" + pendingJump, vy); nextStateLog = now + 1f; }

        // verification of a recovery that was just performed
        if (verifyActive)
        {
            if (supported && now - restoredAt >= 0.1f) { verifyActive = false; attempt = 0; Diag("recovery_verified", p, lastHitInfo, vy); }
            else if (now >= verifyDeadline) { verifyActive = false; Escalate(p, now, vy); lastSupported = true; lastVy = 0f; return; }
        }

        if (pendingJump)
        {
            if (supported) { pendingJump = false; Diag("jump_destination_supported", p, lastHitInfo, vy); }          // a real teleport: nothing to undo
            else if (recoverFromVoidDisplacement && (jumpOriginSupported || jumpTargetValid) && now - jumpSince >= voidDisplacementSeconds)
            {
                pendingJump = false;
                RestoreTo((jumpTargetValid ? jumpTarget : jumpOrigin) + Vector3.up * restoreLift, p, "void_displacement", vy);
                lastSupported = true; lastVy = 0f;
                return;
            }
        }
        lastSupported = supported; lastVy = vy;

        if (supported)
        {
            unsupportedSince = -1f;
            if (airborneSinceSafe)
            {
                airborneSinceSafe = false;
                if (recoverAbruptDropLanding && enableContinuityGuard && hasSafe && !pendingJump)
                {
                    float dxs = p.x - lastSafe.x, dzs = p.z - lastSafe.z; float drop = lastSafe.y - groundY; float allowed = ContinuityAllowance(Mathf.Sqrt(dxs * dxs + dzs * dzs));
                    if (drop > allowed)
                    {
                        Diag("abrupt_drop_landing_detected", p, "drop_m=" + drop.ToString("F2", CultureInfo.InvariantCulture) + ";allowed_m=" + allowed.ToString("F2", CultureInfo.InvariantCulture) + ";" + lastHitInfo, vy);
                        RestoreTo(PickRestore() + Vector3.up * restoreLift, p, "abrupt_drop_landing", vy);
                        lastSupported = true; lastVy = 0f;
                        return;
                    }
                }
            }
            TryAdvanceSafe(p, groundY, now);
            return;
        }
        levelSince = -1f;
        if (hasSafe) airborneSinceSafe = true;

        // path C: hard floor (independent of cooldown, support flicker and jump classification), but never while a teleport destination is still being judged
        if (hasSafe && !pendingJump && p.y < lastSafe.y - hardFloorMeters)
        {
            RestoreTo(PickRestore() + Vector3.up * restoreLift, p, "hard_floor", vy);
            lastSupported = true; lastVy = 0f;
            return;
        }

        if (!hasSafe) return;
        if (unsupportedSince < 0f) unsupportedSince = now;
        if (now < cooldownUntil && !(fastFallOverridesCooldown && vy < -fallVelocityThreshold)) return;
        if (now - unsupportedSince < noSupportSeconds) return;
        // landing probe: dropping onto lower walkable ground (a legitimate step off a quay) is not a fall; wait for the landing, bounded by maxAirborneSeconds
        if (landingProbeMeters > 0f && now - unsupportedSince < maxAirborneSeconds && WalkableGroundBelow(p)) return;

        bool below = p.y < lastSafe.y - fallDistance;
        bool underSurface = false;
        if (!below)
        {
            if (Physics.Raycast(p + Vector3.up * underSurfaceRayHeight, Vector3.down, out RaycastHit h, underSurfaceRayHeight + 5f, groundMask, QueryTriggerInteraction.Ignore))
                underSurface = h.point.y > p.y + underSurfaceMargin;
        }
        if (below || underSurface)
        {
            RestoreTo(PickRestore() + Vector3.up * restoreLift, p, below ? "fell_below_safe_y" : "under_terrain", vy);
            lastSupported = true; lastVy = 0f;
        }
    }

    bool lastHadPos;

    // ---------------------------------------------------------------- safe position bookkeeping
    float SlopeLimit() { return cc != null ? Mathf.Clamp(cc.slopeLimit, 1f, 89f) : 45f; }
    float StepOffset() { return cc != null ? cc.stepOffset : 0.5f; }
    // largest drop (m) a connected walkable path can produce over horizontalDistance: stepOffset + distance * tan(slopeLimit) + 0.1 m tolerance (same rule as AutoGroundAlign)
    float ContinuityAllowance(float horizontalDistance) { return StepOffset() + horizontalDistance * Mathf.Tan(SlopeLimit() * Mathf.Deg2Rad) + 0.1f; }

    void TryAdvanceSafe(Vector3 p, float groundY, float now)
    {
        // remember a position only when the feet are essentially ON the surface (not merely within the 'under-surface' tolerance), and remember the SURFACE height there
        if (!(now >= cooldownUntil || !hasSafe) || groundY - p.y > safeMaxSink) { levelSince = -1f; return; }
        Vector3 cand = new Vector3(p.x, groundY, p.z);
        if (hasSafe && enableContinuityGuard)
        {
            float dx = cand.x - lastSafe.x, dz = cand.z - lastSafe.z;
            float allowedDrop = ContinuityAllowance(Mathf.Sqrt(dx * dx + dz * dz));
            if (lastSafe.y - groundY > allowedDrop)
            {
                // a much lower level (quay face / cliff / genuine step down): only accepted after stable support, never while sliding down it
                if (levelSince < 0f) levelSince = now;
                else if (now - levelSince >= stableSupportSeconds) { Diag("safe_level_accepted", cand, "drop_m=" + (lastSafe.y - groundY).ToString("F2", CultureInfo.InvariantCulture), 0f); AcceptSafe(cand); levelSince = -1f; }
                return;
            }
        }
        levelSince = -1f;
        AcceptSafe(cand);
    }

    void AcceptSafe(Vector3 cand)
    {
        lastSafe = cand; hasSafe = true;
        // The ring holds ANCHORS (not the moving newest position): a new anchor is added when the route has advanced safeRingSpacing metres (or changed level by > 1 m)
        // since the previous anchor. PickRestore() then has real, spatially separated alternatives.
        if (safeRing.Count == 0) { safeRing.Add(cand); return; }
        Vector3 a = safeRing[safeRing.Count - 1];
        if (new Vector2(cand.x - a.x, cand.z - a.z).magnitude >= safeRingSpacing || Mathf.Abs(cand.y - a.y) > 1f)
        {
            safeRing.Add(cand); if (safeRing.Count > 8) safeRing.RemoveAt(0);
        }
    }

    // ---------------------------------------------------------------- support test (capsule footprint)
    Vector3 Footprint(Vector3 p)
    {
        if (!useControllerFootprint || cc == null) return p;
        Vector3 c = cc.transform.TransformPoint(cc.center);
        return new Vector3(c.x, p.y, c.z);
    }

    // Seam tolerance. The city's Ground colliders are separate TIN tiles; their edges leave open slits of 7-10 cm (measured in the real scene along the P3 street). A single zero-width
    // ray that falls into a slit finds no ground although the capsule (radius 0.1 m) stands on it, and path B then undid valid teleports (P3 Part 3: 23 of 246).
    // The footprint is therefore sampled at the centre and at four points supportProbeSpread away. The centre decides as before. If it fails, the footprint counts as supported only when
    // the side samples BRIDGE it: two samples on opposite sides of the centre (+/-x or +/-z) both find walkable ground within the vertical window at heights that differ by at most the
    // step offset. A slit between two tiles is bridged; an edge or a wall is not (all its hits lie on one side), so the support test does not reach beyond an edge.
    // requireBridgeForSideSamples=false accepts any single side hit (first review prototype), multiPointSupport=false is the unchanged single-ray behaviour.
    [Header("Seam tolerance")]
    [Tooltip("Sample the footprint at 5 points (centre + 4 at supportProbeSpread) when the centre ray finds no support, so a slit between two Ground colliders is not read as 'no ground'.")]
    public bool multiPointSupport = true;
    [Tooltip("Distance (m) of the four extra samples from the footprint centre. Must exceed the widest slit to bridge and stay at or below the capsule radius plus skin (0.18 m).")]
    [Range(0f, 0.5f)] public float supportProbeSpread = 0.15f;
    [Tooltip("true: side samples count only as a pair on opposite sides of the centre (a bridged slit); false: any single supported side sample counts.")]
    public bool requireBridgeForSideSamples = true;
    static readonly Vector2[] s_probeOffsets = { Vector2.zero, new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f) };
    static readonly bool[] s_sideOk = new bool[5];
    static readonly float[] s_sideY = new float[5];
    static readonly float[] s_sideDy = new float[5];
    static readonly RaycastHit[] s_sideHit = new RaycastHit[5];
    static readonly bool[] s_sideWalk = new bool[5];

    // One support sample: a valid hit on walkable ground inside the vertical window. No string is built here (the support test runs every frame); the diagnostic text of the decisive sample
    // is built afterwards by HitInfo(). surfaceY / dy are set whenever a finite hit exists.
    bool SampleSupport(Vector3 o, Vector3 p, out RaycastHit h, out float surfaceY, out float dy, out bool hit, out bool walkable)
    {
        surfaceY = p.y; dy = 0f; hit = false; walkable = false;
        if (!Physics.Raycast(o + Vector3.up * supportRayStart, Vector3.down, out h, supportRayStart + supportBelow, groundMask, QueryTriggerInteraction.Ignore)) return false;
        hit = true;
        if (!IsFinite(h.point) || !IsFinite(h.normal)) return false;
        surfaceY = h.point.y; dy = h.point.y - p.y;   // + = surface above the base
        walkable = h.normal.y >= Mathf.Cos(SlopeLimit() * Mathf.Deg2Rad);
        return walkable && dy <= supportAbove && dy >= -supportBelow;
    }

    string HitInfo(RaycastHit h, float dy, bool walkable)
    {
        if (!IsFinite(h.point) || !IsFinite(h.normal)) return "invalid_hit";
        string info = "hit=" + HierPath(h.collider.transform) + ";layer=" + h.collider.gameObject.layer + ";enabled=" + h.collider.enabled + ";mesh=" + MeshName(h.collider) + ";dy=" + dy.ToString("F3", CultureInfo.InvariantCulture) + ";normal_y=" + h.normal.y.ToString("F3", CultureInfo.InvariantCulture);
        return walkable ? info : info + ";not_walkable";
    }

    bool HasSupport(Vector3 p, out float groundY)
    {
        groundY = p.y;
        Vector3 f = Footprint(p);
        // the centre decides first (one ray, exactly the previous behaviour, including the diagnostic text)
        bool centreOk = SampleSupport(f, p, out RaycastHit ch, out float cy, out float cdy, out bool centreHit, out bool cwalk);
        if (centreHit) groundY = cy;
        lastHitInfo = centreHit ? HitInfo(ch, cdy, cwalk) : "no_hit";
        if (centreOk) return true;
        if (!multiPointSupport || supportProbeSpread <= 0f) return false;
        for (int i = 1; i < s_probeOffsets.Length; i++)
        {
            Vector3 o = f + new Vector3(s_probeOffsets[i].x * supportProbeSpread, 0f, s_probeOffsets[i].y * supportProbeSpread);
            s_sideOk[i] = SampleSupport(o, p, out s_sideHit[i], out s_sideY[i], out s_sideDy[i], out bool h, out s_sideWalk[i]);
        }
        int pick = -1;
        if (!requireBridgeForSideSamples)
        {
            for (int i = 1; i < s_probeOffsets.Length; i++) if (s_sideOk[i]) { pick = i; break; }
        }
        else
        {
            float tol = StepOffset(); float best = float.MaxValue;
            if (s_sideOk[1] && s_sideOk[2] && Mathf.Abs(s_sideY[1] - s_sideY[2]) <= tol) { best = Mathf.Min(Mathf.Abs(s_sideDy[1]), Mathf.Abs(s_sideDy[2])); pick = Mathf.Abs(s_sideDy[1]) <= Mathf.Abs(s_sideDy[2]) ? 1 : 2; }
            if (s_sideOk[3] && s_sideOk[4] && Mathf.Abs(s_sideY[3] - s_sideY[4]) <= tol) { float b = Mathf.Min(Mathf.Abs(s_sideDy[3]), Mathf.Abs(s_sideDy[4])); if (b < best) pick = Mathf.Abs(s_sideDy[3]) <= Mathf.Abs(s_sideDy[4]) ? 3 : 4; }
        }
        if (pick < 0) return false;
        groundY = s_sideY[pick]; lastHitInfo = HitInfo(s_sideHit[pick], s_sideDy[pick], s_sideWalk[pick]) + ";probe=" + pick;
        return true;
    }

    // ---------------------------------------------------------------- recovery
    // Where to put the participant back: the newest validated safe position is the last place the capsule was still supported - typically right AT the edge it then fell
    // over - so the first choice is the previous ring entry (>= safeRingSpacing earlier along the route); every failed verification steps one entry further back.
    Vector3 PickRestore()
    {
        if (safeRing.Count == 0) return lastSafe;
        int idx = Mathf.Clamp(safeRing.Count - 2 - attempt, 0, safeRing.Count - 1);
        return safeRing[idx];
    }

    void Escalate(Vector3 from, float now, float vy)
    {
        attempt++;
        if (attempt >= maxAttemptsPerIncident || safeRing.Count - 2 - attempt < 0)
        {
            Diag("recovery_cycle", from, "attempts=" + attempt + ";ring=" + safeRing.Count, vy);   // wrap around: keep trying from the newest safe position, never leave the participant falling
            attempt = 0;
        }
        RestoreTo(PickRestore() + Vector3.up * restoreLift, from, "verify_failed_try_older", vy);
    }

    static readonly RaycastHit[] probeHits = new RaycastHit[8];

    // True when walkable Ground (normal within slopeLimit) lies below the footprint within landingProbeMeters. Steep surfaces on the way down (a quay face the
    // participant is dropping past) do not count and do not hide the ground below them. NonAlloc: only evaluated while the rig is unsupported.
    bool WalkableGroundBelow(Vector3 p)
    {
        Vector3 f = Footprint(p) + Vector3.up * 0.1f;
        float len = landingProbeMeters;
        if (enableContinuityGuard && hasSafe)
        {
            // only ground INSIDE the continuity floor (the lowest level a connected walk from the last safe position could reach) may hold path A: a 4.7 m quay drop is not a landing
            float dxs = p.x - lastSafe.x, dzs = p.z - lastSafe.z;
            float floorY = lastSafe.y - ContinuityAllowance(Mathf.Sqrt(dxs * dxs + dzs * dzs));
            len = Mathf.Min(len, Mathf.Max(0f, (p.y + 0.1f) - floorY));
        }
        if (len <= 0f) return false;
        int n = Physics.RaycastNonAlloc(new Ray(f, Vector3.down), probeHits, len, groundMask, QueryTriggerInteraction.Ignore);
        float cosLimit = Mathf.Cos(SlopeLimit() * Mathf.Deg2Rad);
        for (int i = 0; i < n; i++)
        {
            Vector3 pt = probeHits[i].point, nr = probeHits[i].normal;
            if (IsFinite(pt) && IsFinite(nr) && nr.y >= cosLimit) return true;
        }
        return false;
    }

    void RestoreTo(Vector3 to, Vector3 from, string reason, float vy = 0f)
    {
        bool ccWasOn = cc != null && cc.enabled;
        Vector3 ccBefore = cc != null ? cc.transform.TransformPoint(cc.center) : from;
        if (ccWasOn) cc.enabled = false;           // a CharacterController overrides direct transform writes
        rig.position = to;
        if (ccWasOn) cc.enabled = true;
        Physics.SyncTransforms();
        RecoveryCount++; LastRecoveryReason = reason; unsupportedSince = -1f; cooldownUntil = Now() + cooldownSeconds; lastPos = to; lastSupported = true; lastVy = 0f;
        levelSince = -1f; verifyActive = verifyWithinSeconds > 0f; restoredAt = Now(); verifyDeadline = restoredAt + verifyWithinSeconds;
        if (aga != null) { aga.ResetContinuity(); aga.SuspendAlignment(Mathf.Max(cooldownSeconds, verifyWithinSeconds)); }   // AutoGroundAlign must not undo the recovery
        Vector3 ccAfter = cc != null ? cc.transform.TransformPoint(cc.center) : to;
        Debug.LogWarning("[GroundFallRecovery] " + reason + ": rig at " + from.ToString("F2") + " restored to " + to.ToString("F2") + " (recovery #" + RecoveryCount + ")");
        string detail = "reason=" + reason + ";from=" + from.x.ToString("F2", CultureInfo.InvariantCulture) + "|" + from.y.ToString("F2", CultureInfo.InvariantCulture) + "|" + from.z.ToString("F2", CultureInfo.InvariantCulture)
            + ";cc_before=" + ccBefore.x.ToString("F2", CultureInfo.InvariantCulture) + "|" + ccBefore.y.ToString("F2", CultureInfo.InvariantCulture) + "|" + ccBefore.z.ToString("F2", CultureInfo.InvariantCulture)
            + ";cc_after=" + ccAfter.x.ToString("F2", CultureInfo.InvariantCulture) + "|" + ccAfter.y.ToString("F2", CultureInfo.InvariantCulture) + "|" + ccAfter.z.ToString("F2", CultureInfo.InvariantCulture)
            + ";last_safe=" + (hasSafe ? lastSafe.x.ToString("F2", CultureInfo.InvariantCulture) + "|" + lastSafe.y.ToString("F2", CultureInfo.InvariantCulture) + "|" + lastSafe.z.ToString("F2", CultureInfo.InvariantCulture) : "none")
            + ";vy=" + vy.ToString("F2", CultureInfo.InvariantCulture) + ";ground_valid=" + lastGroundValid + ";ground_y=" + lastGroundY.ToString("F3", CultureInfo.InvariantCulture)
            + ";teleport_pending=" + pendingJump + ";cc_is_grounded=" + (cc != null && cc.enabled && cc.isGrounded) + ";attempt=" + attempt + ";count=" + RecoveryCount;
        Diag("recovery", to, detail, vy);
        try { if (experimentManager != null) experimentManager.ReportParticipantFallRecovery(reason, from, to, vy, detail); }
        catch (Exception e) { if (enableDebugLogs) Debug.LogWarning("[GroundFallRecovery] event report failed: " + e.Message); }
        OnFallRecovered?.Invoke(from, to);
    }

    // ---------------------------------------------------------------- helpers
    static bool IsFinite(Vector3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

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
              .Append(";cc=").Append(cc != null ? "enabled=" + cc.enabled + ",h=" + cc.height.ToString("F3", CultureInfo.InvariantCulture) + ",r=" + cc.radius.ToString("F3", CultureInfo.InvariantCulture) + ",skin=" + cc.skinWidth.ToString("F3", CultureInfo.InvariantCulture) : "none")
              .Append(";aga=").Append(aga != null ? "enabled=" + aga.enabled : "none").Append(";component_enabled=").Append(enabled).Append(";groundMask=").Append(groundMask.value);
            Diag("init", rig.position, sb.ToString(), 0f);
        }
        catch (Exception e) { Debug.LogWarning("[GroundFallRecovery] inventory failed: " + e.Message); }
    }

    void Diag(string evt, Vector3 p, string detail, float vy)
    {
        if (!writeDiagnosticsFile) return;
        try
        {
            if (string.IsNullOrEmpty(DiagnosticsPath))
            {
                DiagnosticsPath = System.IO.Path.Combine(Application.persistentDataPath, "ground_recovery_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".csv");
                File.AppendAllText(DiagnosticsPath, "t_abs,event,rig_x,rig_y,rig_z,cc_enabled,cc_is_grounded,head_x,head_y,head_z,last_safe_valid,last_safe_x,last_safe_y,last_safe_z,detail," +
                                                    "vertical_velocity,ground_valid,ground_y,head_to_origin_xz,teleport_pending,footprint_x,footprint_z,attempt,session_id,part,phase,recovery_index\n");
            }
            var cam = Camera.main; Vector3 h = cam != null ? cam.transform.position : Vector3.zero; var ci = CultureInfo.InvariantCulture;
            Vector3 f = Footprint(p); float off = cam != null ? Mathf.Sqrt((h.x - p.x) * (h.x - p.x) + (h.z - p.z) * (h.z - p.z)) : 0f;
            string line = Time.realtimeSinceStartupAsDouble.ToString("F4", ci) + "," + evt + "," + p.x.ToString("F3", ci) + "," + p.y.ToString("F3", ci) + "," + p.z.ToString("F3", ci) + ","
                + (cc != null && cc.enabled ? "1" : "0") + "," + (cc != null && cc.enabled && cc.isGrounded ? "1" : "0") + ","
                + h.x.ToString("F3", ci) + "," + h.y.ToString("F3", ci) + "," + h.z.ToString("F3", ci) + ","
                + (hasSafe ? "1" : "0") + "," + lastSafe.x.ToString("F3", ci) + "," + lastSafe.y.ToString("F3", ci) + "," + lastSafe.z.ToString("F3", ci) + ","
                + (detail ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' ') + ","
                + vy.ToString("F3", ci) + "," + (lastGroundValid ? "1" : "0") + "," + lastGroundY.ToString("F3", ci) + "," + off.ToString("F3", ci) + "," + (pendingJump ? "1" : "0") + ","
                + f.x.ToString("F3", ci) + "," + f.z.ToString("F3", ci) + "," + attempt + "," + SessionId() + "," + PartPhase() + "," + RecoveryCount + "\n";
            File.AppendAllText(DiagnosticsPath, line);   // open / write / close per row
        }
        catch (Exception e) { writeDiagnosticsFile = false; Debug.LogWarning("[GroundFallRecovery] diagnostics disabled: " + e.Message); }
    }

    string PartPhase()
    {
        try { return capture != null ? (capture.CurrentPart + "," + capture.CurrentPhase) : ","; } catch { return ","; }
    }

    string SessionId()
    {
        try { return capture != null ? capture.SessionId : ""; } catch { return ""; }
    }

    void OnDestroy() { OnFallRecovered = null; }
}
