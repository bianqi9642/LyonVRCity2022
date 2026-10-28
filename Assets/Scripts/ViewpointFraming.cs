// ViewpointFraming.cs
//
// Pure geometry (no scene access, no physics, no rendering) used by the Part 3 target-building viewpoints:
//   - Project(...)        projects a point cloud (the building's bounds corners) through an upright pinhole camera and reports
//                         whether EVERY point is in front of the camera and inside the frame with a margin, plus the image extent.
//                         This is the same pinhole model the isolated preview camera uses (vertical FOV, aspect 1, no roll).
//   - FindClosestFit(...) walks outward from a building along one horizontal direction and returns the NEAREST pedestrian eye
//                         position from which the whole building fits the frame, aiming yaw/pitch (within head-plausible limits) to
//                         centre the silhouette. The eye is always 'eyeHeight' above the ground returned by the caller's resolver.
//   - FacadeScore(...)    how squarely the longest footprint facade faces the camera (0 = grazing, 1 = frontal).
//
// Why this exists: the preview image is rendered with ONLY the target building (isolation layer), so surrounding city geometry
// is not in the image and must not decide whether a viewpoint is acceptable. What decides it is whether the building's silhouette
// fits the camera frame. The editor tool (TargetBuildingPreviewGeneratorEditor) and the runtime cropped-viewpoint warning in
// TargetBuildingPreviewGenerator both use these functions, so they cannot disagree.
//
// Limitations: the silhouette is approximated by the 8 corners of each renderer's oriented mesh bounds (conservative for convex
// buildings, may over-estimate concave ones); the facade term uses the convex hull of the footprint.
//
// All comments in English per project convention.

using System.Collections.Generic;
using UnityEngine;

public static class ViewpointFraming
{
    public struct ProjectionResult
    {
        public bool allInFront;     // every point has positive depth beyond the near plane
        public float minX, maxX, minY, maxY;   // extent in normalised device units (frame = [-1, 1])
        public float slack;         // distance of the tightest edge to the margin boundary; >= 0 means everything is inside
        public bool Fits => allInFront && slack >= 0f;
        /// <summary>Fraction of the frame's larger dimension occupied by the silhouette (0..1).</summary>
        public float Fill => Mathf.Clamp01(Mathf.Max(maxX - minX, maxY - minY) * 0.5f);
    }

    public struct Params
    {
        public float eyeHeight;        // metres above ground
        public float margin;           // fraction of the half-frame kept free around the silhouette (0.06 = 6 %)
        public float maxDistance;      // metres, horizontal distance from the building centre
        public float maxPitchUpDeg;
        public float maxPitchDownDeg;
        public float nearClip;
    }

    public struct FitResult
    {
        public Vector3 eye;
        public float yawDeg, pitchDeg;
        public float distance;         // horizontal distance from the building centre
        public float slack, fill;
        public int groundSource;       // opaque value from the caller's resolver (for reporting)
        public Vector3 Forward => ForwardFrom(yawDeg, pitchDeg);
    }

    /// <summary>Resolve the walking surface under/near a probe position. Returns false if no acceptable ground exists there.</summary>
    public delegate bool GroundResolver(Vector3 probe, out Vector3 groundPoint, out int source);

    public static Vector3 ForwardFrom(float yawDeg, float pitchDeg)
    {
        float y = yawDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
        float cp = Mathf.Cos(p);
        return new Vector3(Mathf.Sin(y) * cp, Mathf.Sin(p), Mathf.Cos(y) * cp);
    }

    public static ProjectionResult Project(IList<Vector3> points, Vector3 eye, Vector3 forward, float fovDeg, float margin, float nearClip)
    {
        var r = new ProjectionResult { allInFront = true, minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue };
        Vector3 f = forward.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, f);
        if (right.sqrMagnitude < 1e-8f) { r.allInFront = false; r.slack = -1f; return r; }
        right.Normalize();
        Vector3 up = Vector3.Cross(f, right);
        float tanHalf = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(fovDeg, 1f, 170f) * 0.5f);

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 v = points[i] - eye;
            float z = Vector3.Dot(v, f);
            if (z <= nearClip) { r.allInFront = false; r.slack = -1f; return r; }
            float x = Vector3.Dot(v, right) / (z * tanHalf);
            float y = Vector3.Dot(v, up) / (z * tanHalf);
            if (x < r.minX) r.minX = x; if (x > r.maxX) r.maxX = x;
            if (y < r.minY) r.minY = y; if (y > r.maxY) r.maxY = y;
        }

        float lim = 1f - margin;
        r.slack = Mathf.Min(Mathf.Min(lim - r.maxX, lim + r.minX), Mathf.Min(lim - r.maxY, lim + r.minY));
        return r;
    }

    /// <summary>
    /// Best aim (yaw about the direction to the centre, pitch within limits) for a fixed eye: maximises the slack, i.e. centres the
    /// silhouette. Returns the projection of the best aim; check .Fits.
    /// </summary>
    public static ProjectionResult BestAim(IList<Vector3> points, Vector3 eye, Vector3 center, float fovDeg, Params p,
                                           out float yawDeg, out float pitchDeg)
    {
        Vector3 d = center - eye;
        float baseYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        ProjectionResult best = default; best.slack = float.MinValue; best.allInFront = false;
        yawDeg = baseYaw; pitchDeg = 0f;

        for (float dy = -20f; dy <= 20.01f; dy += 2.5f)
        {
            for (float pitch = -p.maxPitchDownDeg; pitch <= p.maxPitchUpDeg + 0.001f; pitch += 1f)
            {
                ProjectionResult r = Project(points, eye, ForwardFrom(baseYaw + dy, pitch), fovDeg, p.margin, p.nearClip);
                if (!r.allInFront) continue;
                if (r.slack > best.slack) { best = r; yawDeg = baseYaw + dy; pitchDeg = pitch; }
            }
        }
        return best;
    }

    /// <summary>
    /// Walk outward from the building centre along 'horizontalDir' (from building towards the camera) and return the nearest eye
    /// position from which the WHOLE building (all 'points') fits. The eye stands 'eyeHeight' above the resolver's ground, outside
    /// 'bounds' (expanded by 1 m). Fails if no such position exists within p.maxDistance.
    /// </summary>
    public static bool FindClosestFit(IList<Vector3> points, Bounds bounds, Vector3 horizontalDir, float fovDeg, Params p,
                                      GroundResolver ground, out FitResult fit, out string failReason)
    {
        fit = default; failReason = null;
        Vector3 h = new Vector3(horizontalDir.x, 0f, horizontalDir.z);
        if (h.sqrMagnitude < 1e-8f) { failReason = "invalid direction"; return false; }
        h.Normalize();

        Vector3 c = bounds.center;
        float reach = 0f;
        for (int i = 0; i < points.Count; i++) reach = Mathf.Max(reach, Vector3.Dot(points[i] - c, h));

        Vector3 outerMin = bounds.center - bounds.extents - Vector3.one;   // bounds expanded by 1 m on every side
        Vector3 outerMax = bounds.center + bounds.extents + Vector3.one;
        int noGround = 0, inside = 0, noFit = 0;

        for (float d = reach + 1f; d <= p.maxDistance; d = Mathf.Max(d * 1.03f, d + 0.75f))
        {
            Vector3 probe = new Vector3(c.x + h.x * d, c.y, c.z + h.z * d);
            if (!ground(probe, out Vector3 gp, out int src)) { noGround++; continue; }

            Vector3 eye = new Vector3(gp.x, gp.y + p.eyeHeight, gp.z);
            if (eye.x >= outerMin.x && eye.x <= outerMax.x && eye.y >= outerMin.y && eye.y <= outerMax.y && eye.z >= outerMin.z && eye.z <= outerMax.z)
            { inside++; continue; }

            ProjectionResult r = BestAim(points, eye, c, fovDeg, p, out float yaw, out float pitch);
            if (!r.Fits) { noFit++; continue; }

            fit = new FitResult
            {
                eye = eye, yawDeg = yaw, pitchDeg = pitch,
                distance = new Vector2(eye.x - c.x, eye.z - c.z).magnitude,
                slack = r.slack, fill = r.Fill, groundSource = src
            };
            return true;
        }

        failReason = $"no position within {p.maxDistance:F0} m fits the whole building at {p.eyeHeight:F1} m eye height " +
                     $"(no ground {noGround}, inside building {inside}, does not fit {noFit})";
        return false;
    }

    /// <summary>
    /// 0..1: how squarely the longest facade of the (convex hull of the) footprint faces the camera. 'dirToCamera' is the horizontal
    /// direction from the building towards the camera. 1 = looking straight at the longest facade, 0 = every long facade is grazing.
    /// </summary>
    public static float FacadeScore(IList<Vector3> points, Vector3 dirToCamera)
    {
        Vector3 v = new Vector3(dirToCamera.x, 0f, dirToCamera.z);
        if (v.sqrMagnitude < 1e-8f) return 0f;
        v.Normalize();

        List<Vector2> hull = ConvexHullXZ(points);
        if (hull.Count < 3) return 0f;

        float maxLen = 0f;
        for (int i = 0; i < hull.Count; i++)
            maxLen = Mathf.Max(maxLen, (hull[(i + 1) % hull.Count] - hull[i]).magnitude);
        if (maxLen < 1e-4f) return 0f;

        float best = 0f;
        for (int i = 0; i < hull.Count; i++)
        {
            Vector2 a = hull[i], b = hull[(i + 1) % hull.Count];
            Vector2 e = b - a;
            float len = e.magnitude;
            if (len < 1e-4f) continue;
            Vector2 n = new Vector2(e.y, -e.x) / len;           // outward normal of a counter-clockwise hull
            float facing = n.x * v.x + n.y * v.z;
            if (facing > 0f) best = Mathf.Max(best, (len / maxLen) * facing);
        }
        return Mathf.Clamp01(best);
    }

    // Andrew's monotone chain on (x, z); counter-clockwise result.
    public static List<Vector2> ConvexHullXZ(IList<Vector3> points)
    {
        var pts = new List<Vector2>(points.Count);
        for (int i = 0; i < points.Count; i++) pts.Add(new Vector2(points[i].x, points[i].z));
        pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        if (pts.Count < 3) return pts;

        var hull = new List<Vector2>(pts.Count * 2);
        for (int i = 0; i < pts.Count; i++)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], pts[i]) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], pts[i]) <= 0f) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }
}

/// <summary>
/// Pure decision rules for the Part 3 viewpoint records (no Unity objects, so they can be unit-tested outside the Editor).
/// A target "has a viewpoint" when a targetViewpoints entry with its stable id references an existing Transform. Nothing here looks
/// at a Transform's pose, so manual edits never need to be detected: an existing viewpoint is simply never touched by Add/Suggest.
/// </summary>
public static class ViewpointRecords
{
    public enum RefState { Empty = 0, Valid = 1, Dangling = 2 }   // Dangling: the reference points to a destroyed object

    public struct EntryInfo
    {
        public string id;
        public RefState reference;
    }

    public enum State
    {
        Existing,            // has a valid viewpoint: never touched
        NewTarget,           // no entry (or an empty one) and no viewpoint object: Add creates the entry, Suggest the object
        ObjectWithoutRecord, // a 'Viewpoint_<id>' object exists but the entry/reference is missing: Add may LINK it (never modifies the object)
        DanglingReference,   // the entry exists but its object was destroyed: review, nothing is recreated
        AmbiguousObjects,    // several 'Viewpoint_<id>' objects exist: review
        DuplicateEntries,    // several entries for the id and none valid: review
        Unresolved           // the building or its id cannot be resolved: review
    }

    /// <summary>Classify one target id against the current records. 'objectsNamedForId' = number of 'Viewpoint_&lt;id&gt;' objects in the scene.</summary>
    public static State Classify(string id, IList<EntryInfo> entries, int objectsNamedForId, out bool duplicateEntryWarning)
    {
        duplicateEntryWarning = false;
        if (string.IsNullOrEmpty(id)) return State.Unresolved;

        int n = 0; bool anyValid = false, anyDangling = false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].id != id) continue;
            n++;
            if (entries[i].reference == RefState.Valid) anyValid = true;
            else if (entries[i].reference == RefState.Dangling) anyDangling = true;
        }
        duplicateEntryWarning = n > 1;

        if (anyValid) return State.Existing;
        if (n > 1) return State.DuplicateEntries;
        if (anyDangling) return State.DanglingReference;
        if (objectsNamedForId > 1) return State.AmbiguousObjects;
        if (objectsNamedForId == 1) return State.ObjectWithoutRecord;
        return State.NewTarget;
    }
}
