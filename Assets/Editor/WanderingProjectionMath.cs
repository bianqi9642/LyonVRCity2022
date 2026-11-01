// WanderingProjectionMath.cs
//
// Pure geometry for the NPC wandering-point projection (no UnityEngine types, so it can also be exercised outside Unity).
// Given a NavMesh as triangles it answers: "which NavMesh surfaces does the vertical line through (x, z) cross, and at which heights?", merges
// duplicate hits (a point on a shared edge is found in several triangles), and picks the surface to use from the point's original height.
// Used by WanderingPointsSetupTool; SelfTest() holds the synthetic-geometry checks (also reachable from the Tools menu).
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Text;

public static class WanderingProjectionMath
{
    public struct Surface { public float y; public int area; public int hits; }

    public enum Outcome { Projected, AlreadyOn, NoIntersection, Ambiguous }

    /// <summary>
    /// Triangles bucketed on an XZ grid. Only the cells that contain a query point are populated, so a city-sized NavMesh costs memory only near
    /// the points that are actually projected.
    /// </summary>
    public sealed class TriangleIndex
    {
        readonly float[] vx, vy, vz;
        readonly int[] idx, areas;
        readonly float cell;
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        public int TriangleCount { get; private set; }

        public TriangleIndex(float[] vx, float[] vy, float[] vz, int[] indices, int[] triangleAreas, float cellSize, IList<float> pointsX, IList<float> pointsZ)
        {
            this.vx = vx; this.vy = vy; this.vz = vz; idx = indices; areas = triangleAreas; cell = Math.Max(0.5f, cellSize);
            TriangleCount = indices.Length / 3;

            var needed = new HashSet<long>();
            for (int i = 0; i < pointsX.Count; i++) { needed.Add(Key(Cell(pointsX[i]), Cell(pointsZ[i]))); cells[Key(Cell(pointsX[i]), Cell(pointsZ[i]))] = new List<int>(); }

            for (int t = 0; t < TriangleCount; t++)
            {
                int a = idx[3 * t], b = idx[3 * t + 1], c = idx[3 * t + 2];
                float minX = Math.Min(vx[a], Math.Min(vx[b], vx[c])), maxX = Math.Max(vx[a], Math.Max(vx[b], vx[c]));
                float minZ = Math.Min(vz[a], Math.Min(vz[b], vz[c])), maxZ = Math.Max(vz[a], Math.Max(vz[b], vz[c]));
                int x0 = Cell(minX), x1 = Cell(maxX), z0 = Cell(minZ), z1 = Cell(maxZ);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        List<int> list;
                        if (needed.Contains(Key(cx, cz)) && cells.TryGetValue(Key(cx, cz), out list)) list.Add(t);
                    }
            }
        }

        int Cell(float v) { return (int)Math.Floor(v / cell); }
        static long Key(int cx, int cz) { return ((long)cx << 32) | (uint)cz; }

        /// <summary>
        /// Every triangle whose XZ projection contains (px, pz) (edges and vertices inclusive) contributes its plane height there, by barycentric
        /// interpolation in double precision. 'degenerateNear' counts triangles that are edge-on in XZ (zero projected area: walls) whose projected
        /// segment passes through the point; they cannot give a height and are never used.
        /// </summary>
        public void Query(double px, double pz, List<Surface> hits, out int degenerateNear)
        {
            degenerateNear = 0;
            List<int> list;
            if (!cells.TryGetValue(Key(Cell((float)px), Cell((float)pz)), out list)) return;
            const double eps = 1e-9;
            for (int k = 0; k < list.Count; k++)
            {
                int t = list[k];
                int a = idx[3 * t], b = idx[3 * t + 1], c = idx[3 * t + 2];
                double x0 = vx[a], z0 = vz[a], x1 = vx[b], z1 = vz[b], x2 = vx[c], z2 = vz[c];
                double denom = (z1 - z2) * (x0 - x2) + (x2 - x1) * (z0 - z2);
                if (Math.Abs(denom) < 1e-12)
                {
                    if (OnSegmentXZ(px, pz, x0, z0, x1, z1) || OnSegmentXZ(px, pz, x1, z1, x2, z2) || OnSegmentXZ(px, pz, x0, z0, x2, z2)) degenerateNear++;
                    continue;
                }
                double l0 = ((z1 - z2) * (px - x2) + (x2 - x1) * (pz - z2)) / denom;
                double l1 = ((z2 - z0) * (px - x2) + (x0 - x2) * (pz - z2)) / denom;
                double l2 = 1.0 - l0 - l1;
                if (l0 < -eps || l1 < -eps || l2 < -eps) continue;
                double y = l0 * vy[a] + l1 * vy[b] + l2 * vy[c];
                if (double.IsNaN(y) || double.IsInfinity(y)) { degenerateNear++; continue; }
                hits.Add(new Surface { y = (float)y, area = areas != null && t < areas.Length ? areas[t] : 0, hits = 1 });
            }
        }

        static bool OnSegmentXZ(double px, double pz, double ax, double az, double bx, double bz)
        {
            double cross = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
            if (Math.Abs(cross) > 1e-6) return false;
            return px >= Math.Min(ax, bx) - 1e-9 && px <= Math.Max(ax, bx) + 1e-9 && pz >= Math.Min(az, bz) - 1e-9 && pz <= Math.Max(az, bz) + 1e-9;
        }
    }

    /// <summary>Merges hits whose heights are within 'mergeTolerance' of the first height of their cluster (shared edges / vertices), ascending in Y.</summary>
    public static List<Surface> Cluster(List<Surface> raw, float mergeTolerance)
    {
        var sorted = new List<Surface>(raw);
        sorted.Sort((p, q) => p.y.CompareTo(q.y));
        var result = new List<Surface>();
        int i = 0;
        while (i < sorted.Count)
        {
            float start = sorted[i].y, sum = 0f; int n = 0, area = sorted[i].area;
            while (i < sorted.Count && sorted[i].y - start <= mergeTolerance) { sum += sorted[i].y; n++; i++; }
            result.Add(new Surface { y = sum / n, area = area, hits = n });
        }
        return result;
    }

    /// <summary>
    /// Chooses among distinct surfaces using the point's original height as the reference (not as a rejection criterion). The nearest surface wins
    /// unless the second nearest is almost as near: when (second distance - nearest distance) &lt; ambiguityTolerance the point is Ambiguous and nothing is
    /// chosen. A unique surface is accepted however far it is. 'selected' is the nearest surface index (also reported for Ambiguous).
    /// </summary>
    public static Outcome Select(List<Surface> surfaces, float originalY, float ambiguityTolerance, float alreadyOnTolerance, out int selected, out string reason)
    {
        selected = -1; reason = "";
        if (surfaces.Count == 0) { reason = "no_surface"; return Outcome.NoIntersection; }

        float d1 = float.MaxValue, d2 = float.MaxValue; int best = -1;
        for (int i = 0; i < surfaces.Count; i++)
        {
            float d = Math.Abs(surfaces[i].y - originalY);
            if (d < d1) { d2 = d1; d1 = d; best = i; }
            else if (d < d2) d2 = d;
        }
        selected = best;

        if (surfaces.Count == 1) { reason = "single_surface"; return d1 <= alreadyOnTolerance ? Outcome.AlreadyOn : Outcome.Projected; }
        if (d2 - d1 < ambiguityTolerance) { reason = "ambiguous(nearest_dY=" + d1.ToString("F3") + ";next_dY=" + d2.ToString("F3") + ")"; return Outcome.Ambiguous; }
        reason = "nearest_to_original_Y(dY=" + d1.ToString("F3") + ";next_dY=" + d2.ToString("F3") + ")";
        return d1 <= alreadyOnTolerance ? Outcome.AlreadyOn : Outcome.Projected;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Synthetic geometry checks (no Unity needed). Returns true when all pass; 'report' lists every check.
    // ---------------------------------------------------------------------------------------------------------------------------------
    public static bool SelfTest(out string report)
    {
        var sb = new StringBuilder(); int fail = 0;
        Action<bool, string> check = (ok, name) => { sb.AppendLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fail++; };

        // Mesh A: flat 10x10 quad at y=10 (two triangles, diagonal from (0,0) to (10,10)); Mesh B: the same quad at y=5 (stacked) ; slope: one triangle.
        var vx = new List<float>(); var vy = new List<float>(); var vz = new List<float>(); var ind = new List<int>(); var ar = new List<int>();
        Action<float, float, float> v = (x, y, z) => { vx.Add(x); vy.Add(y); vz.Add(z); };
        // flat quad y=10: vertices 0..3
        v(0, 10, 0); v(10, 10, 0); v(10, 10, 10); v(0, 10, 10);
        ind.AddRange(new[] { 0, 1, 2, 0, 2, 3 }); ar.AddRange(new[] { 0, 0 });
        // stacked quad y=5 (same XZ): vertices 4..7
        v(0, 5, 0); v(10, 5, 0); v(10, 5, 10); v(0, 5, 10);
        ind.AddRange(new[] { 4, 5, 6, 4, 6, 7 }); ar.AddRange(new[] { 0, 0 });
        // sloped triangle far away (x 100..110): plane y = 0.5*(x-100) + 0.5*z  -> vertices 8..10
        v(100, 0, 0); v(110, 5, 0); v(100, 5, 10);
        ind.AddRange(new[] { 8, 9, 10 }); ar.Add(0);
        // vertical wall (degenerate in XZ) at x 200: vertices 11..13 collinear in XZ along z
        v(200, 0, 0); v(200, 5, 0); v(200, 5, 10);
        ind.AddRange(new[] { 11, 12, 13 }); ar.Add(0);

        var px = new List<float> { 3f, 5f, 10f, 10.0001f, 10.15f, 2f, 205f, 200f, 5f };
        var pz = new List<float> { 4f, 5f, 5f, 5f, 5f, 2f, 5f, 5f, 5f };
        // point 2 (5,5) is on the quad diagonal; 3 is on the outer edge x=10; 4 and 5 are just outside; 6 is on the slope at (102,2)
        px[5] = 102f; pz[5] = 2f; px[6] = 205f;
        var idxObj = new TriangleIndex(vx.ToArray(), vy.ToArray(), vz.ToArray(), ind.ToArray(), ar.ToArray(), 20f, px, pz);

        Func<double, double, List<Surface>> hitsAt = (x, z) => { var l = new List<Surface>(); int d; idxObj.Query(x, z, l, out d); return Cluster(l, 0.05f); };

        var s1 = hitsAt(3, 4);
        check(s1.Count == 2 && Math.Abs(s1[0].y - 5f) < 1e-4 && Math.Abs(s1[1].y - 10f) < 1e-4, "stacked surfaces: both heights found at an interior point (5 and 10)");
        int sel; string why;
        check(Select(s1, 6f, 0.5f, 0.01f, out sel, out why) == Outcome.Projected && Math.Abs(s1[sel].y - 5f) < 1e-4, "stacked: original Y 6 selects the y=5 surface (nearest), not the highest");
        check(Select(s1, 7.5f, 0.5f, 0.01f, out sel, out why) == Outcome.Ambiguous, "stacked: original Y 7.5 is equidistant -> AMBIGUOUS, nothing chosen");
        check(Select(s1, 5f, 0.5f, 0.01f, out sel, out why) == Outcome.AlreadyOn, "original Y on the surface -> ALREADY_ON_NAVMESH");

        var diag = hitsAt(5, 5);
        check(diag.Count == 2 && diag[0].hits >= 2, "point on the shared diagonal: triangle duplicates merge into one surface per level (hits >= 2)");
        var edge = hitsAt(10, 5);
        check(edge.Count == 2, "point exactly on the outer boundary edge counts as on the surface");
        check(hitsAt(10.0001, 5).Count == 0, "point 0.0001 m outside the mesh: no intersection (no snapping to the nearest edge)");
        check(hitsAt(10.15, 5).Count == 0, "point 0.15 m outside although a surface is within 0.2 m: no intersection");

        var slope = hitsAt(102, 2);
        check(slope.Count == 1 && Math.Abs(slope[0].y - (0.5f * 2f + 0.5f * 2f)) < 1e-4, "sloped triangle: barycentric height 2.0 at (102, 2)");
        check(Select(slope, 40f, 0.5f, 0.01f, out sel, out why) == Outcome.Projected, "unique surface 38 m from the original Y is still accepted (large displacement, flagged by the tool)");

        var l2 = new List<Surface>(); int deg;
        idxObj.Query(200, 5, l2, out deg);
        check(l2.Count == 0 && deg == 1, "vertical wall triangle (zero XZ area) never gives a height and is counted as degenerate");
        check(Select(new List<Surface>(), 0f, 0.5f, 0.01f, out sel, out why) == Outcome.NoIntersection, "empty candidate list -> NO_VERTICAL_INTERSECTION");

        report = sb.ToString() + (fail == 0 ? "ALL PASSED" : fail + " FAILED");
        return fail == 0;
    }
}
