// AttractorDwell.cs
//
// PURPOSE
// Attach this component to an Attractor "Plane" GameObject (one of the Transforms already referenced by
// SpawnerManager.attractors[]). It does exactly two things and nothing else:
//
//   1. Provides an INDEPENDENT random standing position INSIDE that Plane, vertically projected onto the
//      NavMesh (X/Z preserved). PedestrianAgent uses this as the physical movement destination for the
//      current Attractor leg instead of Plane.transform.position, so multiple NPCs sent to the same
//      Attractor spread out while dwelling.
//
//   2. Holds a per-Attractor "Facing Target" Transform. When an NPC begins dwelling at this Attractor,
//      PedestrianAgent rotates it (yaw only) to look at this target.
//
// WHY A NEW COMPONENT INSTEAD OF AttractorArea
// SpawnerManager.AtLeastOneAttractorHasArea() calls GetComponent<AttractorArea>() on every attractor and,
// if it finds one, switches the whole itinerary system from BuildItinerary() (the Transform-based path
// that is currently active) to BuildPositionItinerary(). That would change NPC route generation, which
// must NOT happen. SpawnerManager has no knowledge of AttractorDwell, so adding it to the Planes cannot
// affect route generation, itinerary contents, entrance/attractor/exit selection or ordering.
//
// RANDOM-NUMBER ISOLATION
// The existing project drives entrance/attractor/exit selection, attractor count, ordering, dwell timing
// and other NPC behaviour from the shared UnityEngine.Random sequence. This component samples with a
// PRIVATE System.Random, so it never advances UnityEngine.Random and the existing route generation is
// bit-for-bit unaffected.
//
// VERTICAL PROJECTION
// The projection scans straight down the candidate's X/Z column (algorithm adapted from
// Assets/Editor/NavMeshVerticalProjector.cs) and accepts a NavMesh sample only when its X/Z stayed within
// a small tolerance of the candidate. Only Y changes. A wide "nearest point" NavMesh.SamplePosition
// search is never used as the final destination. If nothing valid is found after a bounded number of
// attempts, the caller falls back to the original Attractor Transform destination.
//
// All comments in English.

using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class AttractorDwell : MonoBehaviour
{
    [Header("Random stopping position")]
    [Tooltip("When true, NPCs heading to this Attractor stop at an independent random point inside the " +
             "Plane instead of at Plane.transform.position. When false, this component only supplies the " +
             "Facing Target and NPCs stop exactly as they did before.")]
    public bool enableRandomStop = true;

    [Tooltip("Collider whose geometry defines the stopping area. Auto-filled from this GameObject's " +
             "Collider in Reset()/OnValidate(); for the standard Attractor Plane this is its MeshCollider.")]
    public Collider areaCollider;

    [Tooltip("Maximum number of random candidates to try for a single Attractor visit. Each attempt " +
             "generates a fresh random X/Z inside the Plane and vertically projects it onto the NavMesh. " +
             "Bounded, so there is never an infinite loop. If all attempts fail the NPC uses its original " +
             "destination (the Attractor transform).")]
    [Min(1)]
    public int maxAttempts = 24;

    [Header("Vertical NavMesh projection")]
    [Tooltip("Metres to scan UPWARD from the random point when looking for the NavMesh surface.")]
    public float verticalSearchUp = 30f;

    [Tooltip("Metres to scan DOWNWARD from the random point when looking for the NavMesh surface.")]
    public float verticalSearchDown = 30f;

    [Tooltip("Vertical step (metres) between NavMesh probes while scanning the column. Smaller is more " +
             "accurate but slower. Keep this <= NavMesh Sample Radius so a thin surface is not skipped.")]
    public float verticalStep = 0.3f;

    [Tooltip("Max distance passed to NavMesh.SamplePosition at each probe. Kept small so a hit only " +
             "registers when the NavMesh really is near this X/Z column. This is a per-probe tolerance, " +
             "NOT a search radius for the final result.")]
    public float navMeshSampleRadius = 0.5f;

    [Tooltip("A NavMesh probe hit is rejected if its X/Z moved further than this (metres) from the random " +
             "point. This is what guarantees the projection changes Y only and keeps the random X/Z.")]
    public float horizontalTolerance = 0.25f;

    [Header("Dwell facing")]
    [Tooltip("Optional. While an NPC dwells at this Attractor it turns (yaw only) to face this Transform. " +
             "Every NPC at this Attractor faces the same target regardless of where it randomly stopped. " +
             "Leave empty to keep the existing rotation behaviour (NPC holds its arrival heading).")]
    public Transform facingTarget;

    [Tooltip("Yaw turn rate in degrees/second while turning to the Facing Target. Large values look " +
             "almost instant; 0 snaps immediately.")]
    public float facingTurnSpeedDeg = 360f;

    [Header("Debug")]
    [Tooltip("Log a warning (only when a visit fails to find any valid NavMesh point) so you can spot a " +
             "misplaced Plane. Off by default; never logs per-frame.")]
    public bool enableDebugLogs = false;

    // Private RNG. Using System.Random (NOT UnityEngine.Random) keeps the project's existing
    // UnityEngine.Random sequence - and therefore entrance/attractor/exit selection, attractor count,
    // ordering and dwell timing - completely unchanged. Seeded per-instance so two components created in
    // the same frame still diverge.
    System.Random _rng;
    System.Random Rng
    {
        get
        {
            if (_rng == null)
                _rng = new System.Random(unchecked(System.Environment.TickCount * 397) ^ GetInstanceID());
            return _rng;
        }
    }

    void Reset()
    {
        areaCollider = GetComponent<Collider>();
    }

    void OnValidate()
    {
        if (areaCollider == null) areaCollider = GetComponent<Collider>();
        if (verticalStep < 0.01f) verticalStep = 0.01f;
        if (navMeshSampleRadius < 0.01f) navMeshSampleRadius = 0.01f;
        if (horizontalTolerance < 0.001f) horizontalTolerance = 0.001f;
        if (maxAttempts < 1) maxAttempts = 1;
    }

    Collider ResolveCollider()
    {
        if (areaCollider != null) return areaCollider;
        areaCollider = GetComponent<Collider>();
        return areaCollider;
    }

    /// <summary>
    /// Produces an independent random NavMesh standing position inside this Attractor's Plane.
    /// Returns false when random stopping is disabled, there is no usable collider, or no candidate could
    /// be vertically projected onto the NavMesh within 'maxAttempts'. On false the caller MUST fall back
    /// to its existing destination (Plane.transform.position).
    /// </summary>
    public bool TryGetRandomStandingPoint(out Vector3 point)
    {
        point = default;

        if (!enableRandomStop) return false;

        Collider col = ResolveCollider();
        if (col == null)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[AttractorDwell] {name}: no areaCollider - cannot sample a random stop. " +
                                 "NPC will use the Attractor transform instead.");
            return false;
        }

        int attempts = Mathf.Max(1, maxAttempts);
        for (int i = 0; i < attempts; i++)
        {
            Vector3 candidate = GetRandomPointInArea(col);
            if (TryVerticalProjectToNavMesh(candidate, out point))
                return true;
        }

        if (enableDebugLogs)
            Debug.LogWarning($"[AttractorDwell] {name}: no NavMesh point found beneath any of {attempts} " +
                             "random points inside the Plane. NPC will use the Attractor transform instead.");
        return false;
    }

    /// <summary>
    /// Uniformly random point inside the Plane, in world space. Sampling is done in the collider's LOCAL
    /// space and then transformed, so the Plane's position, rotation and (possibly non-uniform) scale are
    /// all respected and the point is always within the actual Plane rectangle.
    /// </summary>
    Vector3 GetRandomPointInArea(Collider col)
    {
        // MeshCollider (the standard Attractor Plane): use the mesh's own local-space bounds.
        MeshCollider mc = col as MeshCollider;
        if (mc != null && mc.sharedMesh != null)
        {
            Bounds lb = mc.sharedMesh.bounds;
            Vector3 local = new Vector3(
                RandRange(lb.min.x, lb.max.x),
                lb.center.y,
                RandRange(lb.min.z, lb.max.z));
            return transform.TransformPoint(local);
        }

        // BoxCollider: sample its local box (X/Z only).
        BoxCollider bc = col as BoxCollider;
        if (bc != null)
        {
            Vector3 half = bc.size * 0.5f;
            Vector3 local = bc.center + new Vector3(
                RandRange(-half.x, half.x),
                0f,
                RandRange(-half.z, half.z));
            return transform.TransformPoint(local);
        }

        // Fallback for any other collider type: sample its world-space axis-aligned bounds. Rotation is
        // not captured by an AABB, but this branch only runs for non-standard attractor colliders.
        Bounds wb = col.bounds;
        return new Vector3(
            RandRange(wb.min.x, wb.max.x),
            wb.center.y,
            RandRange(wb.min.z, wb.max.z));
    }

    /// <summary>
    /// Scans straight down the X/Z column of 'world' for a NavMesh surface. A probe hit is accepted only
    /// when its X/Z are within 'horizontalTolerance' of the candidate, so the returned position keeps the
    /// candidate's X/Z and only takes Y from the NavMesh:
    ///     (X_random, Y_plane, Z_random)  ->  (X_random, Y_navmesh, Z_random)
    /// Adapted from Assets/Editor/NavMeshVerticalProjector.VerticalProjectAtXZ, made runtime-safe.
    /// </summary>
    bool TryVerticalProjectToNavMesh(Vector3 world, out Vector3 result)
    {
        result = world;

        float top = world.y + Mathf.Abs(verticalSearchUp);
        float bottom = world.y - Mathf.Abs(verticalSearchDown);
        float step = Mathf.Max(0.01f, verticalStep);
        float radius = Mathf.Max(0.01f, navMeshSampleRadius);
        float tol = Mathf.Max(0.001f, horizontalTolerance);

        // Top-down so a raised walkable surface (e.g. a bridge deck over a road) is preferred.
        for (float y = top; y >= bottom; y -= step)
        {
            Vector3 probe = new Vector3(world.x, y, world.z);
            NavMeshHit hit;
            if (NavMesh.SamplePosition(probe, out hit, radius, NavMesh.AllAreas))
            {
                float horiz = Vector2.Distance(
                    new Vector2(hit.position.x, hit.position.z),
                    new Vector2(world.x, world.z));

                if (horiz <= tol)
                {
                    // Keep the random X/Z exactly; take only Y from the NavMesh.
                    result = new Vector3(world.x, hit.position.y, world.z);
                    return true;
                }
                // Probe snapped sideways to a nearby edge: ignore it and keep scanning down.
            }
        }

        return false;
    }

    float RandRange(float min, float max)
    {
        return min + (float)Rng.NextDouble() * (max - min);
    }

    // Scene-view helper: draw the sampling rectangle and a line to the Facing Target when selected.
    void OnDrawGizmosSelected()
    {
        Collider col = areaCollider != null ? areaCollider : GetComponent<Collider>();

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        MeshCollider mc = col as MeshCollider;
        BoxCollider bc = col as BoxCollider;

        if (mc != null && mc.sharedMesh != null)
        {
            DrawLocalRect(mc.sharedMesh.bounds);
        }
        else if (bc != null)
        {
            DrawLocalRect(new Bounds(bc.center, bc.size));
        }
        else if (col != null)
        {
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
        }

        if (facingTarget != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, facingTarget.position);
            Gizmos.DrawWireSphere(facingTarget.position, 0.3f);
        }
    }

    void DrawLocalRect(Bounds localBounds)
    {
        Vector3 c = localBounds.center;
        Vector3 e = localBounds.extents;
        Vector3 p0 = transform.TransformPoint(c + new Vector3(-e.x, 0f, -e.z));
        Vector3 p1 = transform.TransformPoint(c + new Vector3(-e.x, 0f, e.z));
        Vector3 p2 = transform.TransformPoint(c + new Vector3(e.x, 0f, e.z));
        Vector3 p3 = transform.TransformPoint(c + new Vector3(e.x, 0f, -e.z));
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p0);
    }
}