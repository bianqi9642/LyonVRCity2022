// ExplorationBoundaryEditor.cs
//
// EDITOR script (Assets/Editor). Custom Inspector + Scene View tooling for ExplorationBoundary.
// EDIT-MODE-ONLY "Generate Boundary" workflow that traces the TRUE WORLD-SPACE outer perimeter of the
// union of the component's Area Volumes and creates physical BoxCollider wall segments along it.
//
// This does NOT add a second runtime system. Generated walls are plain BoxCollider GameObjects that the
// existing ExplorationBoundary runtime discovers via its 'boundarySegmentsRoot' field.
//
// PERIMETER ALGORITHM (rotation-correct; no third-party dependency)
//   1. Each Area Volume -> its TRUE world-space XZ footprint polygon via
//      ExplorationBoundary.AppendColliderFootprintXZ (8 world box corners -> XZ convex hull; an EXACT
//      rotated rectangle for yaw-only boxes). Collider.bounds is NEVER used for the perimeter.
//   2. Take every edge of every footprint polygon.
//   3. Split each edge at every intersection / collinear-overlap endpoint with every other edge.
//   4. For each resulting sub-edge, probe a point PROBE metres to each side:
//        inside-the-union  ==  inside ANY footprint polygon (exact point-in-convex-polygon).
//      Keep the sub-edge as a boundary edge  <=>  exactly one side is inside the union.
//      (Interior seams between overlapping / touching volumes have inside-inside -> dropped.)
//   5. Merge the collinear kept pieces of each source edge -> wall runs, each with the source edge's
//      true direction. rotation = Quaternion.LookRotation(B - A, Vector3.up)  -> real per-run yaw.
//   6. Optional square "corner posts" close corners at any angle.
//
// Exact for convex BoxCollider footprints (yaw-only -> exact). Limitations: Section F of the delivery.
//
// All comments in English per project convention.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(ExplorationBoundary))]
public class ExplorationBoundaryEditor : Editor
{
    private struct WallRun { public Vector3 a, b, outward; }   // a,b world XZ (y = 0); outward = unit XZ normal

    private const float SPLIT_EPS    = 1e-3f;   // dedupe of split parameters (m)
    private const float SUB_MIN      = 0.10f;   // ignore sub-edges shorter than this (m)
    private const float PROBE        = 0.02f;   // perpendicular inside/outside probe distance (m)
    private const float COLLINEAR    = 1e-3f;   // point-to-line distance treated as "on the line" (m)
    private const float CORNER_MATCH = 0.05f;   // endpoint pairing distance for gap diagnostics (m)

    private readonly List<WallRun> _runs = new List<WallRun>();
    private readonly List<string> _warnings = new List<string>();
    private readonly List<Vector2[]> _polys = new List<Vector2[]>();   // true footprint polygons (world XZ)

    // ---------------------------------------------------------------- inspector
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var b = (ExplorationBoundary)target;
        RecomputeRuns(b);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Exploration Boundary Generator", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            int vol = b.AreaVolumes?.Count(c => c != null) ?? 0;
            EditorGUILayout.LabelField($"Area Volumes: {vol}     Footprint polys: {_polys.Count}     " +
                                       $"Perimeter runs: {_runs.Count}");

            if (vol == 0)
            {
                EditorGUILayout.HelpBox("Assign one or more Area Volume colliders to 'areaVolumes' above.",
                    MessageType.Warning);
                if (GUILayout.Button("Auto-find Area Volumes (objects named 'AreaVolume')"))
                    AutoFindAreaVolumes(b);
            }

            foreach (var w in _warnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(vol == 0 || _runs.Count == 0 || playing))
                if (GUILayout.Button("Generate Boundary", GUILayout.Height(28)))
                    GenerateBoundary(b);

            using (new EditorGUI.DisabledScope(playing))
                if (GUILayout.Button("Clear Generated Boundary"))
                    ClearGenerated(b);

            if (playing)
                EditorGUILayout.HelpBox("Generation is disabled in Play Mode.", MessageType.Info);
        }
    }

    // ---------------------------------------------------------------- scene view
    private void OnSceneGUI()
    {
        var b = (ExplorationBoundary)target;
        var g = b.generation;
        if (g == null) return;
        RecomputeRuns(b);

        if (g.showAreaPerimeter)
        {
            // TRUE rotated footprint polygons (white)
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            foreach (var poly in _polys)
                for (int i = 0; i < poly.Length; i++)
                {
                    Vector2 p0 = poly[i], p1 = poly[(i + 1) % poly.Length];
                    Handles.DrawAAPolyLine(2f,
                        new Vector3(p0.x, GroundY(b, new Vector3(p0.x, 0, p0.y)), p0.y),
                        new Vector3(p1.x, GroundY(b, new Vector3(p1.x, 0, p1.y)), p1.y));
                }

            // computed union perimeter runs (green) + outward normals
            var endpoints = new List<Vector3>();
            foreach (var r in _runs)
            {
                Vector3 a = Flat(r.a, GroundY(b, r.a));
                Vector3 bb = Flat(r.b, GroundY(b, r.b));
                Handles.color = new Color(0.15f, 1f, 0.35f, 1f);
                Handles.DrawAAPolyLine(5f, a, bb);

                Vector3 mid = (a + bb) * 0.5f;
                Handles.color = new Color(0.15f, 1f, 0.35f, 0.5f);
                Handles.DrawAAPolyLine(2f, mid, mid + r.outward.normalized * Mathf.Max(1f, (bb - a).magnitude * 0.08f));
                endpoints.Add(a); endpoints.Add(bb);

                if ((r.b - r.a).magnitude < g.minEdgeLength * 6f)
                {
                    Handles.color = Color.yellow;
                    Handles.SphereHandleCap(0, mid, Quaternion.identity,
                        HandleUtility.GetHandleSize(mid) * 0.15f, EventType.Repaint);
                }
            }

            // endpoint that meets no other endpoint -> possible gap (red)
            Handles.color = Color.red;
            for (int i = 0; i < endpoints.Count; i++)
            {
                int matches = 0;
                for (int j = 0; j < endpoints.Count; j++)
                    if (i != j && (endpoints[i] - endpoints[j]).sqrMagnitude < CORNER_MATCH * CORNER_MATCH) matches++;
                if (matches == 0)
                    Handles.CubeHandleCap(0, endpoints[i], Quaternion.identity,
                        HandleUtility.GetHandleSize(endpoints[i]) * 0.18f, EventType.Repaint);
            }
        }

        if (g.showGeneratedBoundary)
        {
            Transform root = b.BoundarySegmentsRoot != null
                ? b.BoundarySegmentsRoot : b.transform.Find(g.containerName);
            if (root != null)
            {
                Handles.color = new Color(0.2f, 0.7f, 1f, 1f);
                foreach (var bc in root.GetComponentsInChildren<BoxCollider>(true))
                {
                    Handles.matrix = bc.transform.localToWorldMatrix;   // carries the real per-segment yaw
                    Handles.DrawWireCube(bc.center, bc.size);
                }
                Handles.matrix = Matrix4x4.identity;
            }
        }
    }

    // ---------------------------------------------------------------- generation
    private void GenerateBoundary(ExplorationBoundary b)
    {
        if (!b.gameObject.scene.IsValid())
        {
            EditorUtility.DisplayDialog("Exploration Boundary", "Use the scene instance, not a prefab asset.", "OK");
            return;
        }
        RecomputeRuns(b);
        if (_runs.Count == 0) return;

        var g = b.generation;
        int wallLayer = ResolveWallLayer(b);
        float topY = VolumesTopY(b);

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Generate Exploration Boundary");

        Transform container = EnsureContainer(b, g.containerName);
        for (int i = container.childCount - 1; i >= 0; i--)                       // remove ONLY prior generated children
            Undo.DestroyObjectImmediate(container.GetChild(i).gameObject);

        int n = 1;
        var cornerPts = new List<Vector3>();
        foreach (var r in _runs)
        {
            Vector3 dir = r.b - r.a;
            float len = dir.magnitude;
            if (len < g.minEdgeLength) continue;
            dir /= len;

            float autoCorner = g.wallThickness * 0.5f + Mathf.Max(0f, g.wallOffset) + Mathf.Max(0f, g.extraCornerOverlap);
            float fullLen = len + 2f * autoCorner;

            Vector3 mid = (r.a + r.b) * 0.5f;
            Vector3 outward = r.outward.sqrMagnitude > 1e-6f ? r.outward.normalized : Vector3.zero;
            Vector3 posXZ = mid + outward * (g.wallThickness * 0.5f + g.wallOffset);
            float baseY = ResolveBaseY(b, posXZ, topY);
            Vector3 pos = new Vector3(posXZ.x, baseY + g.wallHeight * 0.5f, posXZ.z);

            var go = new GameObject($"Segment_{n:00}");
            Undo.RegisterCreatedObjectUndo(go, "Generate Exploration Boundary");
            Undo.SetTransformParent(go.transform, container, "Generate Exploration Boundary");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir, Vector3.up)); // <-- TRUE per-run yaw
            go.layer = wallLayer;

            var bc = Undo.AddComponent<BoxCollider>(go);
            bc.isTrigger = false;
            bc.center = Vector3.zero;
            bc.size = new Vector3(g.wallThickness, g.wallHeight, fullLen);   // local X=thickness, Y=height, Z=length
            n++;

            cornerPts.Add(r.a); cornerPts.Add(r.b);
        }

        int posts = 0;
        if (g.generateCornerPosts)
        {
            float postSize = g.cornerPostSize > 0f ? g.cornerPostSize : Mathf.Max(g.wallThickness * 2f, 0.2f);
            int cn = 1;
            foreach (var cpt in ClusterXZ(cornerPts, Mathf.Max(postSize, 0.25f)))
            {
                float baseY = ResolveBaseY(b, cpt, topY);
                var go = new GameObject($"Corner_{cn:00}");
                Undo.RegisterCreatedObjectUndo(go, "Generate Exploration Boundary");
                Undo.SetTransformParent(go.transform, container, "Generate Exploration Boundary");
                go.transform.SetPositionAndRotation(
                    new Vector3(cpt.x, baseY + g.wallHeight * 0.5f, cpt.z), Quaternion.identity);
                go.layer = wallLayer;
                var bc = Undo.AddComponent<BoxCollider>(go);
                bc.isTrigger = false;
                bc.center = Vector3.zero;
                bc.size = new Vector3(postSize, g.wallHeight, postSize);
                cn++; posts++;
            }
        }

        var so = new SerializedObject(b);
        so.FindProperty("boundarySegmentsRoot").objectReferenceValue = container;   // manual 'boundarySegments' untouched
        so.ApplyModifiedProperties();
        b.EditorRefreshSegmentCache();

        EditorUtility.SetDirty(b);
        EditorSceneManager.MarkSceneDirty(b.gameObject.scene);
        Undo.CollapseUndoOperations(group);

        Debug.Log($"[ExplorationBoundary] Generated {n - 1} wall run(s) + {posts} corner post(s) under " +
                  $"'{container.name}'. 'boundarySegmentsRoot' set to '{container.name}'.");
    }

    private void ClearGenerated(ExplorationBoundary b)
    {
        Transform container = b.transform.Find(b.generation.containerName);
        if (container == null) { Debug.Log("[ExplorationBoundary] Nothing to clear."); return; }

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Clear Exploration Boundary");
        for (int i = container.childCount - 1; i >= 0; i--)
            Undo.DestroyObjectImmediate(container.GetChild(i).gameObject);
        Undo.CollapseUndoOperations(group);

        b.EditorRefreshSegmentCache();
        EditorSceneManager.MarkSceneDirty(b.gameObject.scene);
        Debug.Log("[ExplorationBoundary] Cleared generated segments (container kept).");
    }

    private Transform EnsureContainer(ExplorationBoundary b, string nm)
    {
        if (string.IsNullOrEmpty(nm)) nm = "GeneratedSegments";
        Transform c = b.transform.Find(nm);
        if (c == null)
        {
            var go = new GameObject(nm);
            Undo.RegisterCreatedObjectUndo(go, "Generate Exploration Boundary");
            Undo.SetTransformParent(go.transform, b.transform, "Generate Exploration Boundary");
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            c = go.transform;
        }
        return c;
    }

    // ---------------------------------------------------------------- perimeter maths (rotation-correct)
    private void RecomputeRuns(ExplorationBoundary b)
    {
        _runs.Clear(); _warnings.Clear(); _polys.Clear();

        var cols = (b.AreaVolumes ?? new Collider[0]).Where(c => c != null).Distinct().ToList();
        if (cols.Count == 0) return;

        // 1. true world-space XZ footprint polygon per volume
        var tmp = new List<Vector2>();
        foreach (var c in cols)
        {
            tmp.Clear();
            ExplorationBoundary.AppendColliderFootprintXZ(c, tmp);   // NEVER c.bounds for a BoxCollider
            if (tmp.Count < 3) { _warnings.Add($"'{c.name}': could not build a footprint polygon."); continue; }
            _polys.Add(tmp.ToArray());

            var box = c as BoxCollider;
            if (box == null)
                _warnings.Add($"'{c.name}' is not a BoxCollider - footprint approximated by its world AABB.");
            else if (Vector3.Angle(box.transform.rotation * Vector3.up, Vector3.up) > 0.5f)
                _warnings.Add($"'{c.name}' is tilted (not yaw-only) - footprint uses its vertical shadow.");
        }
        if (_polys.Count == 0) return;

        // 2. all polygon edges
        var edges = new List<(Vector2 a, Vector2 b)>();
        foreach (var poly in _polys)
            for (int i = 0; i < poly.Length; i++)
                edges.Add((poly[i], poly[(i + 1) % poly.Length]));

        // 3. split each edge, classify each sub-edge against the union, 4. merge collinear pieces per source edge
        for (int ei = 0; ei < edges.Count; ei++)
        {
            Vector2 A = edges[ei].a, B = edges[ei].b;
            Vector2 d = B - A;
            float L = d.magnitude;
            if (L < SUB_MIN) continue;
            Vector2 dN = d / L;
            Vector2 nrm = new Vector2(-dN.y, dN.x);

            var ts = new List<float> { 0f, L };
            for (int ej = 0; ej < edges.Count; ej++)
            {
                if (ej == ei) continue;
                AddSplitParams(A, dN, L, edges[ej].a, edges[ej].b, ts);
            }
            ts.Sort();

            var kept = new List<(float lo, float hi, Vector2 outward)>();
            for (int k = 0; k + 1 < ts.Count; k++)
            {
                float t0 = Mathf.Clamp(ts[k], 0f, L);
                float t1 = Mathf.Clamp(ts[k + 1], 0f, L);
                if (t1 - t0 < SUB_MIN) continue;

                Vector2 mid = A + dN * (0.5f * (t0 + t1));
                bool inPlus = PointInUnion(mid + nrm * PROBE);
                bool inMinus = PointInUnion(mid - nrm * PROBE);
                if (inPlus == inMinus) continue;                    // interior seam / stray -> not a wall
                kept.Add((t0, t1, inPlus ? -nrm : nrm));            // outward = toward the OUTSIDE side
            }
            if (kept.Count == 0) continue;

            kept.Sort((p, q) => p.lo.CompareTo(q.lo));
            float lo = kept[0].lo, hi = kept[0].hi; Vector2 outw = kept[0].outward;
            for (int k = 1; k < kept.Count; k++)
            {
                if (kept[k].lo <= hi + SPLIT_EPS && Vector2.Dot(kept[k].outward, outw) > 0f)
                {
                    hi = Mathf.Max(hi, kept[k].hi);
                    outw = (outw + kept[k].outward).normalized;
                }
                else { EmitRun(A, dN, lo, hi, outw, b.generation.minEdgeLength); lo = kept[k].lo; hi = kept[k].hi; outw = kept[k].outward; }
            }
            EmitRun(A, dN, lo, hi, outw, b.generation.minEdgeLength);
        }

        if (_runs.Count == 0)
            _warnings.Add("No outer boundary produced (volumes fully overlap, are identical, or are degenerate).");
    }

    private void EmitRun(Vector2 A, Vector2 dN, float lo, float hi, Vector2 outward, float minLen)
    {
        if (hi - lo < minLen) return;
        Vector2 a = A + dN * lo, c = A + dN * hi;
        _runs.Add(new WallRun
        {
            a = new Vector3(a.x, 0f, a.y),
            b = new Vector3(c.x, 0f, c.y),
            outward = new Vector3(outward.x, 0f, outward.y)
        });
    }

    /// <summary>Adds split distances (along edge A + dN*t, t in [0,L]) where segment C-D crosses or touches it.</summary>
    private static void AddSplitParams(Vector2 A, Vector2 dN, float L, Vector2 C, Vector2 D, List<float> ts)
    {
        Vector2 B = A + dN * L;
        Vector2 r = B - A, s = D - C, cma = C - A;
        float rxs = r.x * s.y - r.y * s.x;

        if (Mathf.Abs(rxs) > 1e-6f)                                  // proper (non-parallel) intersection
        {
            float t = (cma.x * s.y - cma.y * s.x) / rxs;
            float u = (cma.x * r.y - cma.y * r.x) / rxs;
            if (t > -1e-4f && t < 1f + 1e-4f && u > -1e-4f && u < 1f + 1e-4f)
                ts.Add(Mathf.Clamp01(t) * L);
            return;
        }

        // parallel: only relevant if collinear -> project C and D onto the edge
        float distC = Mathf.Abs(cma.x * dN.y - cma.y * dN.x);
        if (distC > COLLINEAR) return;
        float tc = Vector2.Dot(C - A, dN);
        float td = Vector2.Dot(D - A, dN);
        if (tc > 1e-4f && tc < L - 1e-4f) ts.Add(tc);
        if (td > 1e-4f && td < L - 1e-4f) ts.Add(td);
    }

    private bool PointInUnion(Vector2 p)
    {
        for (int i = 0; i < _polys.Count; i++)
            if (PointInConvexPoly(p, _polys[i])) return true;
        return false;
    }

    private static bool PointInConvexPoly(Vector2 p, Vector2[] poly)
    {
        int n = poly.Length, sign = 0;
        for (int i = 0; i < n; i++)
        {
            Vector2 e = poly[(i + 1) % n] - poly[i];
            Vector2 g = p - poly[i];
            float cr = e.x * g.y - e.y * g.x;
            if (Mathf.Abs(cr) < 1e-4f) continue;                     // on this edge -> not decisive
            int s = cr > 0f ? 1 : -1;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return sign != 0;
    }

    private static List<Vector3> ClusterXZ(List<Vector3> pts, float dist)
    {
        var outp = new List<Vector3>();
        var used = new bool[pts.Count];
        float d2 = dist * dist;
        for (int i = 0; i < pts.Count; i++)
        {
            if (used[i]) continue;
            Vector3 sum = pts[i]; int cnt = 1; used[i] = true;
            for (int j = i + 1; j < pts.Count; j++)
            {
                if (used[j]) continue;
                Vector2 a = new Vector2(pts[i].x, pts[i].z), c = new Vector2(pts[j].x, pts[j].z);
                if ((a - c).sqrMagnitude <= d2) { sum += pts[j]; cnt++; used[j] = true; }
            }
            outp.Add(sum / cnt);
        }
        return outp;
    }

    // ---------------------------------------------------------------- vertical placement (unchanged)
    private static int ResolveWallLayer(ExplorationBoundary b)
    {
        string nm = b.generation.wallLayerName;
        if (!string.IsNullOrEmpty(nm))
        {
            int l = LayerMask.NameToLayer(nm);
            if (l >= 0) return l;
        }
        var first = (b.AreaVolumes ?? new Collider[0]).FirstOrDefault(c => c != null);
        return first != null ? first.gameObject.layer : 0;
    }

    private static float VolumesTopY(ExplorationBoundary b)
    {
        float top = 100f; bool any = false;
        foreach (var c in b.AreaVolumes ?? new Collider[0])
            if (c != null) { top = any ? Mathf.Max(top, c.bounds.max.y) : c.bounds.max.y; any = true; }  // bounds OK for a raycast start height only
        return top;
    }

    private float ResolveBaseY(ExplorationBoundary b, Vector3 posXZ, float topY)
    {
        var g = b.generation;
        if (g.verticalMode == ExplorationBoundary.GenerationSettings.VerticalMode.FixedBaseY)
            return g.fixedBaseY;

        int mask = g.groundRaycastMask.value != 0 ? g.groundRaycastMask.value : LayerMask.GetMask("Ground");
        if (mask != 0 && Physics.Raycast(new Vector3(posXZ.x, topY + 50f, posXZ.z), Vector3.down,
                out RaycastHit hit, 10000f, mask, QueryTriggerInteraction.Ignore))
            return hit.point.y - g.wallSkirtBelowGround;

        const string w = "Ground raycast missed for at least one segment - used Fixed Base Y.";
        if (!_warnings.Contains(w)) _warnings.Add(w);
        return g.fixedBaseY;
    }

    private float GroundY(ExplorationBoundary b, Vector3 p)
    {
        int mask = b.generation.groundRaycastMask.value != 0
            ? b.generation.groundRaycastMask.value : LayerMask.GetMask("Ground");
        if (mask != 0 && Physics.Raycast(new Vector3(p.x, VolumesTopY(b) + 50f, p.z), Vector3.down,
                out RaycastHit hit, 10000f, mask, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return b.generation.fixedBaseY;
    }

    private static Vector3 Flat(Vector3 p, float y) => new Vector3(p.x, y, p.z);

    private void AutoFindAreaVolumes(ExplorationBoundary b)
    {
        Transform parent = b.transform.parent != null ? b.transform.parent : b.transform;
        var found = parent.GetComponentsInChildren<Collider>(true)
            .Where(col => col.transform != b.transform
                       && !col.transform.IsChildOf(b.transform)
                       && col.name.ToLowerInvariant().Contains("areavolume"))
            .Distinct().ToList();

        var so = new SerializedObject(b);
        var arr = so.FindProperty("areaVolumes");
        arr.arraySize = found.Count;
        for (int i = 0; i < found.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = found[i];
        so.ApplyModifiedProperties();
        Debug.Log($"[ExplorationBoundary] Auto-found {found.Count} Area Volume collider(s).");
    }
}