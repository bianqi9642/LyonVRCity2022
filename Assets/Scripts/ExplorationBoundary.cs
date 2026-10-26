// ExplorationBoundary.cs
//
// RUNTIME script - the single authority for Part 2 "Exploration Area" boundary behaviour.
// This REPLACES the empty default-template stub at this path. Keep the existing .meta / guid
// (bc4f600aaa71a4c7dacef6dba3e12aae) so any scene wiring survives.
//
// ROLE
//   During the gated experiment part (default "Part2") the participant must not be able to leave a
//   designated "Exploration Area":
//     Layer 1  physical wall colliders (BoxColliders)   -> stop the CharacterController walking out
//     Layer 2  teleport-destination validation          -> RiverTeleportPoint refuses out-of-area targets
//     Layer 3  optional per-frame position safety check  -> recover the rig if it ends up outside anyway
//
//   The Exploration Area is defined by one or more "Area Volume" colliders assigned to 'areaVolumes'.
//   The inside/outside test is Collider.ClosestPoint over their UNION - NOT bounds.Contains - so an
//   irregular / L-shaped / multi-piece / ROTATED area works (ClosestPoint respects collider rotation).
//
//   The Editor generator (Assets/Editor/ExplorationBoundaryEditor.cs) traces the true world-space
//   perimeter of the union of the Area Volumes (real rotated footprints, NOT their AABBs) and creates
//   the Layer-1 wall colliders. It reuses AppendColliderFootprintXZ() below so the runtime gizmo and the
//   generator always agree on the footprint geometry.
//
// INTEGRATION (all additive; ExperimentManager is NOT modified)
//   * Part window: subscribes to ExperimentManager.OnExperimentEvent and watches
//       info.type == "part_start" / "part_end"  &&  info.part == activePartName ("Part2").
//     Subscribed in OnEnable() so the first part_start (raised from ExperimentManager.Start()'s
//     coroutine) is never missed. Always unsubscribed in OnDisable()/OnDestroy().
//   * RiverTeleportPoint.Select() already calls ExplorationBoundary.Instance.IsTeleportDestinationAllowed(..).
//   * Physical walls are discovered from 'boundarySegments' (explicit MANUAL list) PLUS every Collider
//     under 'boundarySegmentsRoot' (recursive, incl. inactive). The Editor generator creates its walls
//     under a child named "GeneratedSegments" and points 'boundarySegmentsRoot' at it.
//
// PRIORITY RULE
//   walls enabled  <=>  useBoundaryCollider (Inspector master switch)  AND  gated part running.
//   Automatic part logic NEVER forces walls on when the master switch is off.
//
// The 'generation' block holds EDITOR-ONLY parameters for the wall generator. Nothing here reads it.
//
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class ExplorationBoundary : MonoBehaviour
{
    public static ExplorationBoundary Instance { get; private set; }

    public enum SafetyRecoveryMode { ClampToClosestEdge, ReturnToLastValidPosition }

    [Header("References (explicit slot -> auto-find -> warn)")]
    [SerializeField] private ExperimentManager experimentManager;
    [Tooltip("Participant rig transform (Layer 3 only). If null, ExperimentManager.playerRig is used. " +
             "Must be the SAME transform ExperimentManager.TeleportPlayerTo() / RiverTeleportPoint move.")]
    [SerializeField] private Transform playerRig;

    [Header("Exploration Area - INSIDE region (source of truth)")]
    [Tooltip("One or more colliders whose UNION is the allowed area. Box/Sphere/Capsule or CONVEX Mesh. " +
             "Keep them Is Trigger, on a layer excluded from interactor/gaze raycasts. The Editor boundary " +
             "generator reads THIS list to trace the true rotated outer perimeter.")]
    [SerializeField] private Collider[] areaVolumes;
    [Tooltip("Ignore the vertical axis in the inside/outside test (recommended: tall area volumes, 2D " +
             "footprint containment).")]
    [SerializeField] private bool ignoreYInContainmentTest = true;
    [SerializeField] private float containmentEpsilon = 0.05f;

    [Header("Layer 1 - Physical boundary wall colliders")]
    [Tooltip("MASTER SWITCH. OFF: walls are NEVER enabled by this script, not even during the gated part. " +
             "ON: walls are enabled ONLY while the gated part runs.")]
    [SerializeField] private bool useBoundaryCollider = true;
    [Tooltip("Explicit, MANUALLY-created wall colliders. The Editor generator does not touch this list.")]
    [SerializeField] private Collider[] boundarySegments;
    [Tooltip("Every Collider under this transform (recursive, incl. inactive) is also treated as a wall. " +
             "The Editor generator sets this to the 'GeneratedSegments' child it creates.")]
    [SerializeField] private Transform boundarySegmentsRoot;

    [Header("Layer 2 - Teleport destination validation")]
    [SerializeField] private bool useTeleportRestriction = true;
    [Tooltip("Extra inward margin (m) a teleport destination must clear inside the area edge.")]
    [SerializeField] private float teleportInsetMargin = 0.5f;

    [Header("Layer 3 - Position safety check (optional backstop)")]
    [SerializeField] private bool usePositionSafetyCheck = false;
    [SerializeField] private SafetyRecoveryMode safetyRecoveryMode = SafetyRecoveryMode.ClampToClosestEdge;
    [SerializeField] private float safetyCheckSkin = 0.10f;
    [SerializeField] private float lastValidSampleInterval = 0.25f;

    [Header("Layer 1b - Runtime 3D-robust walls (built from the Area Volumes)")]
    [Tooltip("Build the physical walls at runtime from the Area Volumes (true union perimeter), following the terrain " +
             "in short overlapping segments. Complements the Editor-generated 'GeneratedSegments', whose single flat " +
             "height per long segment can leave the player a way under or over the wall on sloping ground.")]
    [SerializeField] private bool buildRuntimeWalls = true;
    [SerializeField] private float runtimeWallThickness = 0.4f;
    [Tooltip("Longest runtime wall segment (m). Each segment gets its own vertical extent from the terrain under it.")]
    [SerializeField] private float runtimeWallSegmentLength = 20f;
    [Tooltip("Spacing (m) of the terrain probes along the perimeter.")]
    [SerializeField] private float terrainSampleSpacing = 4f;
    [Tooltip("The wall extends this far BELOW the lowest ground found under (and next to) a segment.")]
    [SerializeField] private float runtimeWallBelowGround = 6f;
    [Tooltip("The wall extends this far ABOVE the highest ground found under (and next to) a segment.")]
    [SerializeField] private float runtimeWallAboveGround = 8f;
    [Tooltip("Layers probed for the ground. Empty = the 'Ground' layer. Where no ground is found for a segment, the wall " +
             "spans the full vertical range of the Area Volumes instead.")]
    [SerializeField] private LayerMask terrainProbeMask = 0;

    [Header("Layer 3b - Containment (secondary enforcement while the gated part runs)")]
    [Tooltip("Keeps the participant inside the Exploration Area even if a wall is bypassed (teleport, room-scale lean, " +
             "unbaked terrain). Active only while protection is active. Works together with 'usePositionSafetyCheck' " +
             "(either one enables it). Corrects by the smallest horizontal shift that brings the offending position " +
             "back inside, so there is no teleport loop.")]
    [SerializeField] private bool enforceContainment = true;
    [Tooltip("Also test the tracked HMD (camera) position, not only the XR Origin root, so room-scale movement cannot " +
             "carry the head outside while the root stays inside.")]
    [SerializeField] private bool containHeadPosition = true;
    [Tooltip("Corrected positions are placed this far (m) inside the area edge so the next frame is not outside again.")]
    [SerializeField] private float containmentRecoverInset = 0.1f;

    [Header("Part gating")]
    [Tooltip("Protection active only while the ExperimentManager part with this name runs. Matches the " +
             "'part' field of part_start / part_end events.")]
    [SerializeField] private string activePartName = "Part2";
    [Tooltip("TESTING ONLY: force protection active with no part running.")]
    [SerializeField] private bool forceActiveForTesting = false;

    [Header("Debug")]
    public bool enableDebugLogs = false;

    [Header("Editor boundary generation (NOT used at runtime)")]
    public GenerationSettings generation = new GenerationSettings();

    [Serializable]
    public class GenerationSettings
    {
        public enum VerticalMode { GroundRaycastPerSegment, FixedBaseY }

        [Header("Wall geometry")]
        [Tooltip("Full wall height in metres (base at ground level, extends upward).")]
        public float wallHeight = 3.0f;
        [Tooltip("Wall thickness in metres.")]
        public float wallThickness = 0.15f;
        [Tooltip("Gap (m) between the area perimeter and the wall's INNER face. 0 = inner face flush with " +
                 "the perimeter (recommended - does not shrink the usable area).")]
        public float wallOffset = 0.0f;
        [Tooltip("Extra length added to EACH end of every wall run, on top of the automatic " +
                 "(thickness/2 + max(0,offset)), so corners always overlap and never gap.")]
        public float extraCornerOverlap = 0.05f;
        [Tooltip("Wall runs shorter than this (m) are treated as numerical slivers and skipped.")]
        public float minEdgeLength = 0.05f;

        [Header("Corner closure")]
        [Tooltip("Also drop a small square post collider at every perimeter corner so that corners at any " +
                 "angle (not just 90 degrees) are guaranteed to be closed.")]
        public bool generateCornerPosts = true;
        [Tooltip("Corner post footprint size (m). 0 = auto (2 x wall thickness, min 0.2).")]
        public float cornerPostSize = 0f;

        [Header("Wall vertical placement")]
        public VerticalMode verticalMode = VerticalMode.GroundRaycastPerSegment;
        [Tooltip("Layers treated as 'ground' for per-segment ground raycasts. Empty = auto ('Ground').")]
        public LayerMask groundRaycastMask = 0;
        [Tooltip("Wall base is placed this far BELOW detected ground (skirt against slope gaps).")]
        public float wallSkirtBelowGround = 0.5f;
        [Tooltip("Used when Vertical Mode = Fixed Base Y, and as the fallback when a ground raycast misses.")]
        public float fixedBaseY = 0.0f;

        [Header("Wall object")]
        [Tooltip("Layer for generated walls. Empty = copy the first Area Volume's layer.")]
        public string wallLayerName = "Ignore Raycast";
        [Tooltip("Name of the child container that holds generated segments. Clear only ever touches this.")]
        public string containerName = "GeneratedSegments";

        [Header("Scene View visualization (editor only)")]
        public bool showAreaPerimeter = true;
        public bool showGeneratedBoundary = true;
    }

    // ---- editor accessors ----
    public Collider[] AreaVolumes => areaVolumes;
    public Transform BoundarySegmentsRoot => boundarySegmentsRoot;

    // ---- runtime state ----
    private readonly List<Collider> resolvedSegments = new List<Collider>();
    private bool partActive;
    private bool subscribed;
    private bool wallsEnabledNow;
    private bool cachedMaster;
    private Vector3 lastValid;
    private bool haveLastValid;
    private float lastValidTimer;
    private bool wasOutside;
    private readonly List<Collider> runtimeWallColliders = new List<Collider>();
    private Transform runtimeWallRoot;
    private Transform headRef;

    public bool ProtectionActive => isActiveAndEnabled && (partActive || forceActiveForTesting);
    public bool BoundaryCollidersEnabled => wallsEnabledNow;

    // ---------------------------------------------------------------- lifecycle
    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning($"[ExplorationBoundary] Multiple instances - '{name}' will not be the singleton " +
                             $"(existing '{Instance.name}').");
        else
            Instance = this;
        cachedMaster = useBoundaryCollider;
    }

    private void OnEnable()
    {
        ResolveRefs();
        RebuildSegmentCache();
        Subscribe();
        ApplyWallState(true);       // fail-safe: walls OFF until a gated part starts
    }

    private void Start()
    {
        ResolveRefs();
        BuildRuntimeWalls();
        RebuildSegmentCache();
        Subscribe();
        WarnIfMisconfigured();
        ApplyWallState(true);
    }

    private void OnDisable()
    {
        Unsubscribe();
        partActive = false;
        ForceDisableSegments();     // never leave walls up when the component is off
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (Instance == this) Instance = null;
    }

    private void ResolveRefs()
    {
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (playerRig == null && experimentManager != null) playerRig = experimentManager.playerRig;
    }

    private void Subscribe()
    {
        if (subscribed || experimentManager == null) return;
        experimentManager.OnExperimentEvent += OnExperimentEvent;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed || experimentManager == null) return;
        experimentManager.OnExperimentEvent -= OnExperimentEvent;
        subscribed = false;
    }

    private void WarnIfMisconfigured()
    {
        if (experimentManager == null)
            Debug.LogWarning("[ExplorationBoundary] No ExperimentManager - part gating cannot work.");
        if (areaVolumes == null || areaVolumes.Length == 0)
            Debug.LogWarning("[ExplorationBoundary] No Area Volumes - inside/outside test FAILS OPEN.");
        if (useBoundaryCollider && resolvedSegments.Count == 0)
            Debug.LogWarning("[ExplorationBoundary] 'Use Boundary Collider' ON but no wall segments found " +
                             "(generate them, or assign 'boundarySegments' / 'boundarySegmentsRoot').");
    }

    // ---------------------------------------------------------------- part gating
    private void OnExperimentEvent(ExperimentEventInfo info)
    {
        if (string.IsNullOrEmpty(info.part) || info.part != activePartName) return;

        if (info.type == "part_start")
        {
            partActive = true;
            haveLastValid = false; lastValidTimer = 0f; wasOutside = false;
            // Rebuild now: the terrain colliders may have been activated after Start, and the walls must follow them.
            BuildRuntimeWalls();
            RebuildSegmentCache();
            if (enableDebugLogs)
                Debug.Log($"[ExplorationBoundary] {activePartName} start - protection ON " +
                          $"(walls {(useBoundaryCollider ? "enabled" : "SUPPRESSED by master switch")}, " +
                          $"{resolvedSegments.Count} wall colliders, {runtimeWallColliders.Count} runtime).");
            ApplyWallState(true);
        }
        else if (info.type == "part_end")
        {
            partActive = false;
            if (enableDebugLogs) Debug.Log($"[ExplorationBoundary] {activePartName} end - protection OFF.");
            ApplyWallState(true);
        }
    }

    private void Update()
    {
        if (useBoundaryCollider != cachedMaster)
        {
            cachedMaster = useBoundaryCollider;
            if (enableDebugLogs) Debug.Log($"[ExplorationBoundary] master switch -> {useBoundaryCollider}");
            ApplyWallState(true);
        }
        else
        {
            ApplyWallState(false);
        }
    }

    private void LateUpdate()
    {
        if (!(usePositionSafetyCheck || enforceContainment) || !ProtectionActive || playerRig == null) return;
        if (areaVolumes == null || areaVolumes.Length == 0) return;

        // Smallest-correction containment (default mode): the tracked head is tested first (room-scale can carry it
        // outside while the root is inside), then the root. The rig is shifted horizontally by just enough to put the
        // offending position back inside, so Y (AutoGroundAlign) and head-pose alignment are untouched.
        if (safetyRecoveryMode == SafetyRecoveryMode.ClampToClosestEdge)
        {
            EnforceContainmentByShift();
            return;
        }

        Vector3 p = playerRig.position;
        if (IsInsideArea(p))
        {
            wasOutside = false;
            lastValidTimer += Time.deltaTime;
            if (!haveLastValid || lastValidTimer >= lastValidSampleInterval)
            {
                lastValid = p; haveLastValid = true; lastValidTimer = 0f;
            }
            return;
        }

        Vector3 closest = ClosestPointOnArea(p);
        float over = ignoreYInContainmentTest
            ? Vector2.Distance(new Vector2(p.x, p.z), new Vector2(closest.x, closest.z))
            : Vector3.Distance(p, closest);
        if (over <= safetyCheckSkin) return;

        Vector3 fix = (safetyRecoveryMode == SafetyRecoveryMode.ReturnToLastValidPosition && haveLastValid)
            ? new Vector3(lastValid.x, p.y, lastValid.z)
            : new Vector3(closest.x, p.y, closest.z);   // keep AutoGroundAlign's Y
        playerRig.position = fix;

        if (enableDebugLogs && !wasOutside)
            Debug.Log($"[ExplorationBoundary] safety recover: rig was {over:F2} m outside ({safetyRecoveryMode}).");
        wasOutside = true;
    }

    private void EnforceContainmentByShift()
    {
        Vector3 shift = Vector3.zero;
        string what = null;

        Transform head = containHeadPosition ? ResolveHead() : null;
        if (head != null && !IsInsideArea(head.position) && TryRecoverShift(head.position, out shift))
            what = "head";
        else
        {
            Vector3 root = playerRig.position;
            if (!IsInsideArea(root) && TryRecoverShift(root, out shift)) what = "rig root";
        }

        if (what == null) { wasOutside = false; return; }

        playerRig.position += shift;
        if (enableDebugLogs && !wasOutside)
            Debug.Log($"[ExplorationBoundary] containment: {what} was outside the Exploration Area; rig shifted by {shift.magnitude:F2} m " +
                      $"to {playerRig.position}.");
        wasOutside = true;
    }

    // Horizontal shift that moves 'q' to just inside (by containmentRecoverInset) the nearest point of the area.
    private bool TryRecoverShift(Vector3 q, out Vector3 shift)
    {
        shift = Vector3.zero;
        Vector3 closest = ClosestPointOnArea(q);
        Vector2 toIn = new Vector2(closest.x - q.x, closest.z - q.z);
        float over = toIn.magnitude;
        if (over <= safetyCheckSkin) return false;           // within tolerance: leave it to the walls
        Vector2 target = new Vector2(closest.x, closest.z) + toIn / over * containmentRecoverInset;
        shift = new Vector3(target.x - q.x, 0f, target.y - q.y);
        return true;
    }

    // The tracked HMD camera under the rig (cached); falls back to Camera.main.
    private Transform ResolveHead()
    {
        if (headRef != null && headRef.gameObject.activeInHierarchy) return headRef;
        headRef = null;
        if (playerRig != null)
        {
            Camera[] cams = playerRig.GetComponentsInChildren<Camera>(false);
            for (int i = 0; i < cams.Length; i++)
                if (cams[i] != null && cams[i].enabled) { headRef = cams[i].transform; break; }
        }
        if (headRef == null && Camera.main != null) headRef = Camera.main.transform;
        return headRef;
    }

    // ---------------------------------------------------------------- geometry API
    public bool IsInsideArea(Vector3 p)
    {
        if (areaVolumes == null) return false;
        for (int i = 0; i < areaVolumes.Length; i++)
        {
            Collider c = areaVolumes[i];
            if (c == null) continue;
            if (c is MeshCollider mc && !mc.convex)
            {
                if (c.bounds.Contains(ignoreYInContainmentTest
                        ? new Vector3(p.x, c.bounds.center.y, p.z) : p)) return true;
                continue;
            }
            Vector3 cp = ClosestPointOnCollider(c, p);   // respects the collider's rotation and scale
            if (ignoreYInContainmentTest) cp.y = p.y;
            if ((cp - p).sqrMagnitude <= containmentEpsilon * containmentEpsilon) return true;
        }
        return false;
    }

    public bool IsTeleportDestinationAllowed(Vector3 destination)
    {
        if (!useTeleportRestriction) return true;
        if (!ProtectionActive) return true;
        if (areaVolumes == null || areaVolumes.Length == 0) return true;   // fail-open (warned at Start)

        return IsInsideAreaWithInsetMargin(destination);
    }

    /// <summary>
    /// Authoritative "may a participant LAND here" test for teleports that are not routed through
    /// RiverTeleportPoint (used by GuardedTeleportationProvider for XRI anchors/areas). Same geometry and inset
    /// margin as IsTeleportDestinationAllowed, but it does NOT depend on the part-window state (the caller decides
    /// when the restriction applies, from the running Part) and it FAILS CLOSED when no Area Volume is assigned.
    /// Respects the 'useTeleportRestriction' master switch (false = no area restriction, as for river teleports).
    /// </summary>
    public bool IsLandingPositionAllowed(Vector3 landing, out string reason)
    {
        reason = "";
        if (!useTeleportRestriction) return true;
        if (areaVolumes == null || areaVolumes.Length == 0)
        {
            reason = "no_area_volumes";
            return false;
        }
        if (!IsInsideAreaWithInsetMargin(landing))
        {
            reason = "outside_exploration_area";
            return false;
        }
        return true;
    }

    private bool IsInsideAreaWithInsetMargin(Vector3 p)
    {
        if (!IsInsideArea(p)) return false;
        if (teleportInsetMargin > 0f)
        {
            if (!IsInsideArea(p + new Vector3(teleportInsetMargin, 0, 0))) return false;
            if (!IsInsideArea(p + new Vector3(-teleportInsetMargin, 0, 0))) return false;
            if (!IsInsideArea(p + new Vector3(0, 0, teleportInsetMargin))) return false;
            if (!IsInsideArea(p + new Vector3(0, 0, -teleportInsetMargin))) return false;
        }
        return true;
    }

    /// <summary>
    /// Closest point on a collider. For a BoxCollider this is computed analytically in the collider's local space
    /// (identical result to Collider.ClosestPoint for an active collider), so the inside/outside test does not depend
    /// on the physics engine having created the shape. That matters here: the Area Volumes sit under the
    /// 'Participant'/'EnvironmentConfig' organisation objects, which are inactive in the saved scene, and
    /// Collider.ClosestPoint is not defined for a collider that is inactive in the hierarchy. Other collider types
    /// still use Collider.ClosestPoint.
    /// </summary>
    private static Vector3 ClosestPointOnCollider(Collider c, Vector3 p)
    {
        BoxCollider box = c as BoxCollider;
        if (box == null) return c.ClosestPoint(p);

        Transform t = box.transform;
        Vector3 local = t.InverseTransformPoint(p);
        Vector3 half = box.size * 0.5f;
        Vector3 ctr = box.center;
        Vector3 clamped = new Vector3(
            Mathf.Clamp(local.x, ctr.x - Mathf.Abs(half.x), ctr.x + Mathf.Abs(half.x)),
            Mathf.Clamp(local.y, ctr.y - Mathf.Abs(half.y), ctr.y + Mathf.Abs(half.y)),
            Mathf.Clamp(local.z, ctr.z - Mathf.Abs(half.z), ctr.z + Mathf.Abs(half.z)));
        return t.TransformPoint(clamped);
    }

    /// <summary>Diagnostics: number of assigned Area Volumes.</summary>
    public int AreaVolumeCount => areaVolumes != null ? areaVolumes.Length : 0;

    /// <summary>Diagnostics: Area Volumes whose collider is enabled AND active in the hierarchy.</summary>
    public int ActiveAreaVolumeCount
    {
        get
        {
            int n = 0;
            if (areaVolumes == null) return 0;
            for (int i = 0; i < areaVolumes.Length; i++)
                if (areaVolumes[i] != null && areaVolumes[i].enabled && areaVolumes[i].gameObject.activeInHierarchy) n++;
            return n;
        }
    }

    public Vector3 ClosestPointOnArea(Vector3 p)
    {
        Vector3 best = p; float bestSqr = float.PositiveInfinity;
        if (areaVolumes == null) return best;
        for (int i = 0; i < areaVolumes.Length; i++)
        {
            Collider c = areaVolumes[i];
            if (c == null || (c is MeshCollider mc && !mc.convex)) continue;
            Vector3 cp = ClosestPointOnCollider(c, p);
            float d = ignoreYInContainmentTest
                ? (new Vector2(cp.x, cp.z) - new Vector2(p.x, p.z)).sqrMagnitude
                : (cp - p).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = cp; }
        }
        return best;
    }

    // ---------------------------------------------------------------- footprint geometry (shared with Editor)
    /// <summary>
    /// Appends the TRUE world-space XZ footprint of a collider (as a convex polygon, in hull order) to
    /// 'outXZ'. For a BoxCollider: the convex hull of its 8 world corners projected to XZ - an EXACT
    /// rotated rectangle for yaw-only rotation, the vertical shadow (4-6 pts) if tilted about X/Z.
    /// For any other collider type: the 4 corners of its world AABB (APPROXIMATE - the generator warns).
    /// This NEVER uses Collider.bounds for a BoxCollider.
    /// </summary>
    public static void AppendColliderFootprintXZ(Collider c, List<Vector2> outXZ)
    {
        if (c == null) return;
        var box = c as BoxCollider;
        var pts = s_fpTmp;
        pts.Clear();

        if (box != null)
        {
            Vector3 ce = box.center, ha = box.size * 0.5f;
            Matrix4x4 m = box.transform.localToWorldMatrix;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 w = m.MultiplyPoint3x4(ce + new Vector3(sx * ha.x, sy * ha.y, sz * ha.z));
                        pts.Add(new Vector2(w.x, w.z));
                    }
        }
        else
        {
            Bounds bd = c.bounds; // documented approximation for non-Box colliders only
            pts.Add(new Vector2(bd.min.x, bd.min.z));
            pts.Add(new Vector2(bd.max.x, bd.min.z));
            pts.Add(new Vector2(bd.max.x, bd.max.z));
            pts.Add(new Vector2(bd.min.x, bd.max.z));
        }

        ConvexHullXZ(pts, outXZ);
    }

    private static readonly List<Vector2> s_fpTmp = new List<Vector2>(8);

    private static void ConvexHullXZ(List<Vector2> src, List<Vector2> outHull)
    {
        outHull.Clear();
        var pts = new List<Vector2>(src);
        pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        for (int i = pts.Count - 1; i > 0; i--)
            if ((pts[i] - pts[i - 1]).sqrMagnitude < 1e-8f) pts.RemoveAt(i);
        if (pts.Count < 3) { outHull.AddRange(pts); return; }

        var hull = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++)                         // lower chain
        {
            while (hull.Count >= 2 && Cross2(hull[hull.Count - 2], hull[hull.Count - 1], pts[i]) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)                    // upper chain
        {
            while (hull.Count >= lower && Cross2(hull[hull.Count - 2], hull[hull.Count - 1], pts[i]) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        outHull.AddRange(hull);
    }

    private static float Cross2(Vector2 o, Vector2 a, Vector2 b)
        => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    // ---------------------------------------------------------------- wall state
    private void RebuildSegmentCache()
    {
        resolvedSegments.Clear();
        if (boundarySegments != null)
            foreach (var c in boundarySegments)
                if (c != null && !resolvedSegments.Contains(c)) resolvedSegments.Add(c);
        if (boundarySegmentsRoot != null)
            foreach (var c in boundarySegmentsRoot.GetComponentsInChildren<Collider>(true))
                if (c != null && !resolvedSegments.Contains(c)) resolvedSegments.Add(c);
        foreach (var c in runtimeWallColliders)
            if (c != null && !resolvedSegments.Contains(c)) resolvedSegments.Add(c);
    }

    // ---------------------------------------------------------------- runtime walls (3D-robust)
    private struct WallRun { public Vector2 a, b, outward; }

    private void ClearRuntimeWalls()
    {
        for (int i = 0; i < runtimeWallColliders.Count; i++)
        {
            Collider c = runtimeWallColliders[i];
            if (c == null) continue;
            c.enabled = false;
            Destroy(c.gameObject);
        }
        runtimeWallColliders.Clear();
        if (runtimeWallRoot != null) { Destroy(runtimeWallRoot.gameObject); runtimeWallRoot = null; }
    }

    /// <summary>
    /// Builds the physical walls from the Area Volumes: (1) the true XZ footprint polygon of every volume, (2) every polygon
    /// edge minus the parts lying inside another volume = the perimeter of the UNION, (3) each run cut into short
    /// segments of at most 'runtimeWallSegmentLength', (4) every segment gets its own vertical extent from the ground
    /// probed under it (lowest and highest hit, so bridges/overpasses are covered) minus/plus margins, and from the Area
    /// Volumes' own vertical range wherever no ground is found, (5) segments overlap along the run and a square post closes
    /// every run end. Walls sit OUTSIDE the perimeter, so the permitted area is not reduced. Uses only transform math for the
    /// volumes, so it is valid while they are inactive in the hierarchy. Colliders start disabled; ApplyWallState owns them.
    /// </summary>
    private void BuildRuntimeWalls()
    {
        ClearRuntimeWalls();
        if (!buildRuntimeWalls || areaVolumes == null || areaVolumes.Length == 0) return;

        // 1. footprints (CCW) and vertical range of the volumes
        var polys = new List<Vector2[]>();
        float volMinY = float.PositiveInfinity, volMaxY = float.NegativeInfinity;
        var tmp = new List<Vector2>();
        for (int i = 0; i < areaVolumes.Length; i++)
        {
            Collider c = areaVolumes[i];
            if (c == null) continue;
            tmp.Clear();
            AppendColliderFootprintXZ(c, tmp);
            if (tmp.Count < 3) continue;
            Vector2[] poly = tmp.ToArray();
            if (SignedArea(poly) < 0f) System.Array.Reverse(poly);
            polys.Add(poly);

            BoxCollider box = c as BoxCollider;
            if (box != null)
            {
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        Vector3 w = box.transform.TransformPoint(box.center + new Vector3(0f, sy * box.size.y * 0.5f, 0f) +
                                                                 new Vector3(sx * box.size.x * 0.5f, 0f, 0f));
                        volMinY = Mathf.Min(volMinY, w.y); volMaxY = Mathf.Max(volMaxY, w.y);
                    }
            }
            else if (c.bounds.size.y > 0f)
            {
                volMinY = Mathf.Min(volMinY, c.bounds.min.y); volMaxY = Mathf.Max(volMaxY, c.bounds.max.y);
            }
        }
        if (polys.Count == 0) return;
        if (float.IsInfinity(volMinY)) { volMinY = -1000f; volMaxY = 1000f; }

        // 2. perimeter of the union = polygon edges minus the parts inside the other polygons
        var runs = new List<WallRun>();
        var inside = new List<Vector2>();
        for (int pi = 0; pi < polys.Count; pi++)
        {
            Vector2[] poly = polys[pi];
            for (int k = 0; k < poly.Length; k++)
            {
                Vector2 a = poly[k], b = poly[(k + 1) % poly.Length];
                Vector2 d = b - a;
                if (d.sqrMagnitude < 1e-4f) continue;

                inside.Clear();
                for (int pj = 0; pj < polys.Count; pj++)
                {
                    if (pj == pi) continue;
                    if (ClipSegmentToConvex(a, b, polys[pj], out float t0, out float t1)) inside.Add(new Vector2(t0, t1));
                }
                inside.Sort((u, v) => u.x.CompareTo(v.x));

                Vector2 dir = d.normalized;
                Vector2 outward = new Vector2(dir.y, -dir.x);
                if (PointInConvex(a + d * 0.5f + outward * 0.05f, poly)) outward = -outward;

                float cursor = 0f;
                for (int n = 0; n < inside.Count; n++)
                {
                    if (inside[n].x > cursor) AddRun(runs, a, d, cursor, inside[n].x, outward);
                    cursor = Mathf.Max(cursor, inside[n].y);
                }
                if (cursor < 1f) AddRun(runs, a, d, cursor, 1f, outward);
            }
        }

        // 3. ground probes + segments
        int mask = terrainProbeMask.value != 0 ? terrainProbeMask.value : GroundMaskByName();
        float probeTop = volMaxY + 50f;
        float probeDist = (volMaxY - volMinY) + 150f;
        var hits = new RaycastHit[16];

        runtimeWallRoot = new GameObject("RuntimeWalls").transform;
        runtimeWallRoot.SetParent(transform, false);
        runtimeWallRoot.localPosition = Vector3.zero;
        int layer = LayerMask.NameToLayer(generation != null ? generation.wallLayerName : "Ignore Raycast");
        if (layer < 0) layer = 2;

        float thick = Mathf.Max(0.1f, runtimeWallThickness);
        float spacing = Mathf.Max(0.5f, terrainSampleSpacing);
        int boxes = 0, groundHit = 0, groundMiss = 0, fallbackSegs = 0;
        float gMin = float.PositiveInfinity, gMax = float.NegativeInfinity;

        for (int r = 0; r < runs.Count; r++)
        {
            WallRun run = runs[r];
            Vector2 ab = run.b - run.a;
            float len = ab.magnitude;
            Vector2 dir = ab / len;

            int nSamples = Mathf.Max(2, Mathf.CeilToInt(len / spacing) + 1);
            var sLow = new float[nSamples];
            var sHigh = new float[nSamples];
            var sOk = new bool[nSamples];
            for (int s = 0; s < nSamples; s++)
            {
                Vector2 pt = run.a + ab * (s / (float)(nSamples - 1));
                int n = Physics.RaycastNonAlloc(new Vector3(pt.x, probeTop, pt.y), Vector3.down, hits, probeDist, mask, QueryTriggerInteraction.Ignore);
                if (n <= 0) { sOk[s] = false; groundMiss++; continue; }
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                for (int h = 0; h < n; h++) { lo = Mathf.Min(lo, hits[h].point.y); hi = Mathf.Max(hi, hits[h].point.y); }
                sLow[s] = lo; sHigh[s] = hi; sOk[s] = true; groundHit++;
                gMin = Mathf.Min(gMin, lo); gMax = Mathf.Max(gMax, hi);
            }

            int segCount = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(2f, runtimeWallSegmentLength)));
            for (int sg = 0; sg < segCount; sg++)
            {
                float d0 = len * sg / segCount, d1 = len * (sg + 1) / segCount;
                // sample window = this segment plus one probe spacing each side, so neighbouring segments overlap vertically
                int i0 = Mathf.Clamp(Mathf.FloorToInt((d0 - spacing) / len * (nSamples - 1)), 0, nSamples - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt((d1 + spacing) / len * (nSamples - 1)), 0, nSamples - 1);
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                bool complete = true;
                for (int s = i0; s <= i1; s++)
                {
                    if (!sOk[s]) { complete = false; break; }
                    lo = Mathf.Min(lo, sLow[s]); hi = Mathf.Max(hi, sHigh[s]);
                }
                float bottom, top;
                if (complete) { bottom = lo - runtimeWallBelowGround; top = hi + runtimeWallAboveGround; }
                else { bottom = volMinY; top = volMaxY; fallbackSegs++; }

                // overlap along the run: extend by a thickness at both ends
                float segLen = (d1 - d0) + thick;
                Vector2 mid2 = run.a + dir * ((d0 + d1) * 0.5f) + run.outward * (thick * 0.5f);
                CreateWallBox(new Vector3(mid2.x, (bottom + top) * 0.5f, mid2.y),
                              Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y), Vector3.up),
                              new Vector3(thick, top - bottom, segLen), layer, ref boxes);

                // square post at each run end closes corners of any angle
                if (sg == 0 || sg == segCount - 1)
                {
                    Vector2 end = sg == 0 ? run.a : run.b;
                    CreateWallBox(new Vector3(end.x, (bottom + top) * 0.5f, end.y), Quaternion.identity,
                                  new Vector3(thick * 2f, top - bottom, thick * 2f), layer, ref boxes);
                }
            }
        }

        if (enableDebugLogs)
        {
            Debug.Log($"[ExplorationBoundary] runtime walls built: {runs.Count} perimeter runs, {boxes} colliders; ground probes hit/miss = " +
                      $"{groundHit}/{groundMiss}; ground y {(groundHit > 0 ? gMin.ToString("F1") + ".." + gMax.ToString("F1") : "none found")}; " +
                      $"{fallbackSegs} segments use the Area Volumes' vertical range ({volMinY:F1}..{volMaxY:F1}).");
        }
        if (groundHit == 0)
            Debug.LogWarning("[ExplorationBoundary] No ground found under the perimeter (terrain colliders inactive or on another layer?). " +
                             "Runtime walls use the full vertical range of the Area Volumes.");
    }

    private void CreateWallBox(Vector3 center, Quaternion rot, Vector3 size, int layer, ref int count)
    {
        var go = new GameObject("RW_" + count);
        go.layer = layer;
        go.transform.SetParent(runtimeWallRoot, false);
        go.transform.SetPositionAndRotation(center, rot);
        var bc = go.AddComponent<BoxCollider>();
        bc.size = size;
        bc.isTrigger = false;
        bc.enabled = false;               // ApplyWallState enables it for the gated part
        runtimeWallColliders.Add(bc);
        count++;
    }

    private static void AddRun(List<WallRun> runs, Vector2 a, Vector2 d, float t0, float t1, Vector2 outward)
    {
        if ((t1 - t0) * d.magnitude < 0.05f) return;
        runs.Add(new WallRun { a = a + d * t0, b = a + d * t1, outward = outward });
    }

    private static int GroundMaskByName()
    {
        int g = LayerMask.NameToLayer("Ground");
        return g >= 0 ? (1 << g) : Physics.DefaultRaycastLayers;
    }

    private static float SignedArea(Vector2[] p)
    {
        float s = 0f;
        for (int i = 0; i < p.Length; i++) { Vector2 u = p[i], v = p[(i + 1) % p.Length]; s += u.x * v.y - v.x * u.y; }
        return s * 0.5f;
    }

    private static bool PointInConvex(Vector2 q, Vector2[] ccw)
    {
        for (int i = 0; i < ccw.Length; i++)
        {
            Vector2 e = ccw[(i + 1) % ccw.Length] - ccw[i];
            if (e.x * (q.y - ccw[i].y) - e.y * (q.x - ccw[i].x) < 0f) return false;
        }
        return true;
    }

    // Cyrus-Beck: parameter interval [t0,t1] of segment a->b lying inside the CCW convex polygon.
    private static bool ClipSegmentToConvex(Vector2 a, Vector2 b, Vector2[] ccw, out float t0, out float t1)
    {
        t0 = 0f; t1 = 1f;
        Vector2 d = b - a;
        for (int i = 0; i < ccw.Length; i++)
        {
            Vector2 p = ccw[i];
            Vector2 e = ccw[(i + 1) % ccw.Length] - p;
            Vector2 n = new Vector2(-e.y, e.x);                  // inward normal of a CCW polygon
            float num = Vector2.Dot(n, a - p);
            float den = Vector2.Dot(n, d);
            if (Mathf.Abs(den) < 1e-9f) { if (num < 0f) return false; continue; }
            float t = -num / den;
            if (den > 0f) t0 = Mathf.Max(t0, t); else t1 = Mathf.Min(t1, t);
            if (t0 >= t1) return false;
        }
        return t1 - t0 > 1e-5f;
    }

    private void ApplyWallState(bool force)
    {
        bool desired = useBoundaryCollider && ProtectionActive;
        if (!force && desired == wallsEnabledNow) return;
        wallsEnabledNow = desired;
        for (int i = 0; i < resolvedSegments.Count; i++)
        {
            Collider c = resolvedSegments[i];
            if (c != null && c.enabled != desired) c.enabled = desired;
        }
    }

    private void ForceDisableSegments()
    {
        wallsEnabledNow = false;
        for (int i = 0; i < resolvedSegments.Count; i++)
        {
            Collider c = resolvedSegments[i];
            if (c != null && c.enabled) c.enabled = false;
        }
    }

    /// <summary>Editor generator calls this after (re)creating walls so the runtime picks them up in edit mode.</summary>
    public void EditorRefreshSegmentCache() => RebuildSegmentCache();

    private void OnValidate()
    {
        containmentEpsilon = Mathf.Max(0f, containmentEpsilon);
        teleportInsetMargin = Mathf.Max(0f, teleportInsetMargin);
        safetyCheckSkin = Mathf.Max(0f, safetyCheckSkin);
        lastValidSampleInterval = Mathf.Max(0.02f, lastValidSampleInterval);
        if (generation != null)
        {
            generation.wallHeight = Mathf.Max(0.05f, generation.wallHeight);
            generation.wallThickness = Mathf.Max(0.01f, generation.wallThickness);
            generation.minEdgeLength = Mathf.Max(0.001f, generation.minEdgeLength);
            generation.cornerPostSize = Mathf.Max(0f, generation.cornerPostSize);
        }
        if (Application.isPlaying && isActiveAndEnabled)
        {
            cachedMaster = useBoundaryCollider;
            RebuildSegmentCache();
            ApplyWallState(true);
        }
    }

    private static readonly List<Vector2> s_gizmoPoly = new List<Vector2>();

    private void OnDrawGizmos()
    {
        if (generation == null) return;

        if (generation.showAreaPerimeter && areaVolumes != null)
        {
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.9f);
            foreach (var c in areaVolumes)
            {
                if (c == null) continue;
                s_gizmoPoly.Clear();
                AppendColliderFootprintXZ(c, s_gizmoPoly);           // TRUE rotated footprint, not c.bounds
                if (s_gizmoPoly.Count < 2) continue;
                float y = c.bounds.center.y;                          // bounds used only for a draw height
                for (int i = 0; i < s_gizmoPoly.Count; i++)
                {
                    Vector2 p0 = s_gizmoPoly[i];
                    Vector2 p1 = s_gizmoPoly[(i + 1) % s_gizmoPoly.Count];
                    Gizmos.DrawLine(new Vector3(p0.x, y, p0.y), new Vector3(p1.x, y, p1.y));
                }
            }
        }

        if (generation.showGeneratedBoundary)
        {
            Transform root = boundarySegmentsRoot != null
                ? boundarySegmentsRoot : transform.Find(generation.containerName);
            if (root != null)
            {
                Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
                foreach (var bc in root.GetComponentsInChildren<BoxCollider>(true))
                {
                    Gizmos.matrix = bc.transform.localToWorldMatrix;   // already carries the real yaw
                    Gizmos.DrawWireCube(bc.center, bc.size);
                }
                Gizmos.matrix = Matrix4x4.identity;
            }
        }
    }
}