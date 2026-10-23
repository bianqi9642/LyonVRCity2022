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
            if (enableDebugLogs)
                Debug.Log($"[ExplorationBoundary] {activePartName} start - protection ON " +
                          $"(walls {(useBoundaryCollider ? "enabled" : "SUPPRESSED by master switch")}).");
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
        if (!usePositionSafetyCheck || !ProtectionActive || playerRig == null) return;
        if (areaVolumes == null || areaVolumes.Length == 0) return;

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
            Vector3 cp = c.ClosestPoint(p);        // respects the collider's rotation
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

    public Vector3 ClosestPointOnArea(Vector3 p)
    {
        Vector3 best = p; float bestSqr = float.PositiveInfinity;
        if (areaVolumes == null) return best;
        for (int i = 0; i < areaVolumes.Length; i++)
        {
            Collider c = areaVolumes[i];
            if (c == null || (c is MeshCollider mc && !mc.convex)) continue;
            Vector3 cp = c.ClosestPoint(p);
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