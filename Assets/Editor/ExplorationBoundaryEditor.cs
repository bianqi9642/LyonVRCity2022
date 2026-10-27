// ExplorationBoundaryEditor.cs
//
// EDITOR script (Assets/Editor). Custom Inspector + Scene View tooling for ExplorationBoundary.
// EDIT-MODE-ONLY workflow that builds the Part 2 physical boundary as ordinary, serialized Scene objects. Nothing is
// generated at runtime; ExplorationBoundary only enables / disables the colliders it finds under 'boundarySegmentsRoot'.
//
// PREVIEW (no changes) and GENERATE share one pipeline; Preview stops before touching the Scene.
//   1. Perimeter: each Area Volume -> true world XZ footprint (ExplorationBoundary.AppendColliderFootprintXZ: serialized
//      centre/size + TransformPoint, so it is valid while the volumes sit under inactive parents) -> edges split against
//      each other, kept only where exactly one side is inside the UNION -> ordered exterior "runs". No wall exists inside
//      the overlap of the volumes.
//   2. Sections: every run is cut into sections of <= maxSectionLength; sections overlap by sectionOverlap; the outer
//      ends of a run overlap the corner posts.
//   3. Ground: read DIRECTLY from the terrain meshes in the open Scene (MeshFilter / MeshCollider of the TIN tiles -
//      Textured and White -, the South-river gap patch and PONT bridges, identified by source asset path), transformed to
//      world space, sampled with vertical rays every sampleSpacing metres along the whole perimeter. No physics layer,
//      activation state or Play Mode is involved; NPCs, buildings and markers are never read.
//   4. Height of a section = lowest ground in its window - groundMarginBelow .. highest + groundMarginAbove.
//        Measured                every sample has ground.
//        Partial                 >= minValidFraction of the samples have ground: range from those samples only.
//        Inferred                fewer samples (or none) have ground: range = own valid samples + ground borrowed from valid
//                                samples within inferMaxDistance along the perimeter.
//        GlobalEnvelopeFallback  nothing usable within inferMaxDistance (or part of the section is not established):
//                                range = lowest/highest ground found ANYWHERE on the perimeter and inside the two regions
//                                (interior grid). Conservative, NOT a measurement; coverage is proven only relative to
//                                that envelope. If no ground exists anywhere, Generate aborts without changing the Scene.
//      Corner posts cover the union of the adjoining sections.
//   5. Validation before anything is touched (closed perimeter, ground available, container usable, self-checks of the
//      planned colliders against every valid sample). Any failure aborts with the Scene unchanged.
//   6. Generate then replaces ONLY generator-owned objects (children of the container carrying ExplorationBoundaryWall, plus
//      optionally legacy Segment_NN / Corner_NN BoxCollider-only objects) in one Undo group, wires 'boundarySegmentsRoot'
//      and marks the Scene dirty. Running it again replaces the set; it never accumulates duplicates.
//
// All comments in English per project convention.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
    private const float LARGE_RANGE  = 50f;     // colliders taller than this are flagged (m)

    private readonly List<WallRun> _runs = new List<WallRun>();
    private readonly List<string> _warnings = new List<string>();
    private readonly List<Vector2[]> _polys = new List<Vector2[]>();   // true footprint polygons (world XZ)

    // ---------------------------------------------------------------- plan model
    private enum Source { Measured, Partial, Inferred, GlobalEnvelopeFallback }

    private sealed class Sample
    {
        public Vector3 p;             // world XZ position (y = 0)
        public float d;               // distance from the run start (m)
        public float s;               // distance along the perimeter chain (m)
        public bool valid;
        public float lo, hi;          // ground range at this sample (valid only)
        public bool borrowed;         // invalid, ground range borrowed from valid samples within inferMaxDistance
        public float bLo, bHi;
        public bool notEstablished;   // invalid and nothing within inferMaxDistance
        public float gapLength;       // length of the perimeter-contiguous invalid stretch (m)
        public string src = "";
    }

    private sealed class RunPlan
    {
        public int index;
        public Vector3 a, b, dir, outward;
        public float length, yaw;
        public List<Sample> samples = new List<Sample>();
        public List<SectionPlan> sections = new List<SectionPlan>();
    }

    private sealed class SectionPlan
    {
        public RunPlan run;
        public int index;
        public float d0, d1;
        public Vector3 centre;        // world, XZ (y set from the range)
        public float colliderLength;
        public Source source;
        public int total, valid, missing, borrowed, notEstablished;
        public float longestGap;
        public float gMin = float.NaN, gMax = float.NaN;       // own valid samples
        public float uMin = float.NaN, uMax = float.NaN;       // ground range finally used
        public float bottom, top;
        public bool review;           // gap >= gapReviewLength
        public bool geometryOk = true;
        public int violations;
        public string sources = "";
        public string note = "";
        public string Name => $"Wall_R{run.index + 1:00}_S{index + 1:000}";
    }

    private sealed class PostPlan
    {
        public int index;
        public Vector3 at;
        public List<SectionPlan> adj = new List<SectionPlan>();
        public float bottom, top;
        public bool geometryOk = true;
        public string Name => $"Post_{index + 1:00}";
    }

    private sealed class Plan
    {
        public List<RunPlan> runs = new List<RunPlan>();
        public List<PostPlan> posts = new List<PostPlan>();
        public bool closed;
        public float perimeter;
        public float envMin, envMax;           // global ground envelope
        public int envSamples;
        public int groundHitSamples, totalSamples;
        public string terrainLog = "";
        public float postSize, thickness;
        public int layer;
        public List<string> errors = new List<string>();
        public IEnumerable<SectionPlan> Sections => runs.SelectMany(r => r.sections);
    }

    private static Plan s_plan;
    private static string s_reportText = "";
    private static string s_mode = "";
    private static Vector2 s_scroll;

    // ---------------------------------------------------------------- inspector
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var b = (ExplorationBoundary)target;
        RecomputeRuns(b);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Exploration Boundary - terrain-calibrated walls", EditorStyles.boldLabel);
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

            EditorGUILayout.HelpBox("Walls are created from the Area Volumes alone - no existing walls are needed. Generate replaces only the " +
                                    "objects it owns (marker 'ExplorationBoundaryWall'); ground is read from the terrain meshes in the Scene.",
                MessageType.Info);

            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(vol == 0 || _runs.Count == 0 || playing))
            {
                if (GUILayout.Button("Preview Generated Walls (no changes)"))
                    RunPipeline(b, write: false);
                if (GUILayout.Button("Generate / Regenerate Boundary Walls", GUILayout.Height(28)))
                    RunPipeline(b, write: true);
            }

            if (playing)
                EditorGUILayout.HelpBox("Generation is disabled in Play Mode.", MessageType.Info);

            if (!string.IsNullOrEmpty(s_reportText))
            {
                bool bad = s_plan == null || s_plan.errors.Count > 0;
                bool warn = !bad && (s_plan.Sections.Any(x => x.source != Source.Measured || x.review || x.notEstablished > 0 || !x.geometryOk
                                                             || x.top - x.bottom > LARGE_RANGE));
                EditorGUILayout.HelpBox(s_mode + (bad ? " - ABORTED, Scene unchanged." : warn ? " - warnings present, see the report." : " - all sections measured and checked."),
                    bad ? MessageType.Error : warn ? MessageType.Warning : MessageType.Info);
                s_scroll = EditorGUILayout.BeginScrollView(s_scroll, GUILayout.MaxHeight(280));
                EditorGUILayout.SelectableLabel(s_reportText, EditorStyles.textArea, GUILayout.MinHeight(24 * 14));
                EditorGUILayout.EndScrollView();
            }
        }
    }

    // ---------------------------------------------------------------- scene view
    private void OnSceneGUI()
    {
        var b = (ExplorationBoundary)target;
        var g = b.generation;
        if (g == null) return;
        RecomputeRuns(b);

        float yRef = ApproxY(b);

        if (g.showAreaPerimeter)
        {
            Handles.color = new Color(1f, 1f, 1f, 0.35f);
            foreach (var poly in _polys)
                for (int i = 0; i < poly.Length; i++)
                {
                    Vector2 p0 = poly[i], p1 = poly[(i + 1) % poly.Length];
                    Handles.DrawAAPolyLine(2f, new Vector3(p0.x, yRef, p0.y), new Vector3(p1.x, yRef, p1.y));
                }

            var endpoints = new List<Vector3>();
            foreach (var r in _runs)
            {
                Vector3 a = Flat(r.a, yRef);
                Vector3 bb = Flat(r.b, yRef);
                Handles.color = new Color(0.15f, 1f, 0.35f, 1f);
                Handles.DrawAAPolyLine(5f, a, bb);

                Vector3 mid = (a + bb) * 0.5f;
                Handles.color = new Color(0.15f, 1f, 0.35f, 0.5f);
                Handles.DrawAAPolyLine(2f, mid, mid + r.outward.normalized * Mathf.Max(1f, (bb - a).magnitude * 0.08f));
                endpoints.Add(a); endpoints.Add(bb);
            }

            Handles.color = Color.red;     // endpoint meeting no other endpoint -> open perimeter
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

        if (!g.showGeneratedBoundary) return;

        // Planned (Preview) or just generated sections: wire boxes coloured by height provenance. Editor-only drawing.
        if (s_plan != null && s_plan.errors.Count == 0)
        {
            foreach (var sec in s_plan.Sections)
            {
                Handles.color = sec.source == Source.Measured ? new Color(0.2f, 1f, 0.4f, 1f)
                              : sec.source == Source.Partial ? new Color(0.7f, 1f, 0.2f, 1f)
                              : sec.source == Source.Inferred ? new Color(1f, 0.75f, 0.1f, 1f)
                              : new Color(1f, 0.15f, 0.15f, 1f);
                Handles.matrix = Matrix4x4.TRS(new Vector3(sec.centre.x, (sec.bottom + sec.top) * 0.5f, sec.centre.z),
                                               Quaternion.LookRotation(sec.run.dir, Vector3.up), Vector3.one);
                Handles.DrawWireCube(Vector3.zero, new Vector3(s_plan.thickness, sec.top - sec.bottom, sec.colliderLength));
            }
            foreach (var post in s_plan.posts)
            {
                Handles.color = new Color(0.3f, 0.6f, 1f, 1f);
                Handles.matrix = Matrix4x4.TRS(new Vector3(post.at.x, (post.bottom + post.top) * 0.5f, post.at.z), Quaternion.identity, Vector3.one);
                Handles.DrawWireCube(Vector3.zero, new Vector3(s_plan.postSize, post.top - post.bottom, s_plan.postSize));
            }
            Handles.matrix = Matrix4x4.identity;
        }
        else
        {
            Transform root = b.BoundarySegmentsRoot != null ? b.BoundarySegmentsRoot : b.transform.Find(g.containerName);
            if (root != null)
            {
                Handles.color = new Color(0.2f, 0.7f, 1f, 1f);
                foreach (var bc in root.GetComponentsInChildren<BoxCollider>(true))
                {
                    Handles.matrix = bc.transform.localToWorldMatrix;
                    Handles.DrawWireCube(bc.center, bc.size);
                }
                Handles.matrix = Matrix4x4.identity;
            }
        }
    }

    // ---------------------------------------------------------------- pipeline
    private void RunPipeline(ExplorationBoundary b, bool write)
    {
        s_mode = write ? "GENERATE" : "PREVIEW";
        if (!b.gameObject.scene.IsValid())
        {
            Abort("Use the scene instance, not a prefab asset.", null);
            return;
        }

        Plan plan = BuildPlan(b);
        s_plan = plan;
        if (plan.errors.Count > 0)
        {
            Abort(string.Join("\n", plan.errors), plan);
            return;
        }

        string report = BuildReport(plan, b.generation, b);

        if (!write)
        {
            s_reportText = report;
            LogResult(plan, "[ExplorationBoundary] Preview: nothing was created, deleted or modified.", report);
            return;
        }

        // ---- apply, as one Undo group; everything above succeeded, nothing has been touched yet -----------------------
        int created;
        string removedInfo;
        try { created = ApplyPlan(b, plan, out removedInfo); }
        catch (Exception e)
        {
            Undo.RevertAllInCurrentGroup();
            Abort("Applying the plan failed and was reverted: " + e.Message, plan);
            return;
        }

        s_reportText = report + "\n" + removedInfo + $"\nCreated {created} GameObjects with BoxColliders under '{b.generation.containerName}'.";
        LogResult(plan, $"[ExplorationBoundary] Generate: created {created} wall GameObjects with BoxColliders. {removedInfo}", s_reportText);
    }

    private static void LogResult(Plan plan, string head, string report)
    {
        bool warn = plan.Sections.Any(x => x.source != Source.Measured || x.review || x.notEstablished > 0 || !x.geometryOk
                                            || x.top - x.bottom > LARGE_RANGE);
        if (warn) Debug.LogWarning(head + " WARNINGS - not every section is fully terrain-measured.\n" + report);
        else Debug.Log(head + "\n" + report);
    }

    private static void Abort(string msg, Plan plan)
    {
        s_reportText = "ABORTED - the Scene was not changed.\n" + msg + (plan != null ? "\n" + plan.terrainLog : "");
        Debug.LogError("[ExplorationBoundary] " + s_mode + " aborted, Scene unchanged: " + msg);
        EditorUtility.DisplayDialog("Exploration Boundary - " + s_mode + " aborted", msg + "\n\nThe Scene was not changed.", "OK");
    }

    // ---------------------------------------------------------------- plan (no Scene access except reading)
    private Plan BuildPlan(ExplorationBoundary b)
    {
        var plan = new Plan();
        var g = b.generation;
        RecomputeRuns(b);
        if (_polys.Count == 0 || _runs.Count == 0) { plan.errors.Add("No perimeter: assign valid Area Volumes."); return plan; }

        plan.thickness = Mathf.Max(0.05f, g.wallThickness);
        plan.postSize = g.cornerPostSize > 0f ? g.cornerPostSize : Mathf.Max(plan.thickness * 2f, 0.2f);
        plan.layer = ResolveWallLayer(b);

        // container must have unit scale (box sizes are written in world metres)
        Transform root = b.BoundarySegmentsRoot != null ? b.BoundarySegmentsRoot : b.transform.Find(g.containerName);
        Transform parent = root != null ? root : b.transform;
        Vector3 ls = parent.lossyScale;
        if (Mathf.Abs(ls.x - 1f) > 1e-3f || Mathf.Abs(ls.y - 1f) > 1e-3f || Mathf.Abs(ls.z - 1f) > 1e-3f)
        { plan.errors.Add($"'{parent.name}' has lossy scale {ls}; the wall container needs unit scale."); return plan; }

        // ---- runs ---------------------------------------------------------------------------------------------------
        float spacing = Mathf.Max(0.25f, g.sampleSpacing);
        int ri = 0;
        foreach (var r in _runs)
        {
            var rp = new RunPlan { index = ri++, a = r.a, b = r.b, outward = r.outward.sqrMagnitude > 1e-6f ? r.outward.normalized : Vector3.zero };
            Vector3 d = r.b - r.a; rp.length = d.magnitude; rp.dir = d / rp.length;
            rp.yaw = Mathf.Atan2(rp.dir.x, rp.dir.z) * Mathf.Rad2Deg;
            plan.perimeter += rp.length;
            plan.runs.Add(rp);
        }

        // ---- terrain ------------------------------------------------------------------------------------------------
        var log = new StringBuilder();
        var terrain = TerrainSurface.Collect(PerimeterBounds(), log);
        plan.terrainLog = log.ToString().TrimEnd();
        if (terrain.Count == 0)
        { plan.errors.Add("No readable terrain mesh (TIN tiles / gap patch / bridges) was found in the open Scene.\n" + plan.terrainLog); return plan; }

        var hits = new List<TerrainSurface.Hit>();
        foreach (var rp in plan.runs)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(rp.length / spacing) + 1);
            for (int k = 0; k < n; k++)
            {
                float t = k / (float)(n - 1);
                var s = new Sample { p = Vector3.Lerp(rp.a, rp.b, t), d = t * rp.length };
                hits.Clear();
                terrain.Query(s.p.x, s.p.z, hits);
                if (hits.Count > 0)
                {
                    s.valid = true; s.lo = float.MaxValue; s.hi = float.MinValue;
                    var srcs = new SortedSet<string>();
                    foreach (var h in hits) { s.lo = Mathf.Min(s.lo, h.y); s.hi = Mathf.Max(s.hi, h.y); srcs.Add(h.source); }
                    s.src = string.Join("+", srcs);
                }
                rp.samples.Add(s);
            }
        }
        plan.totalSamples = plan.runs.Sum(r => r.samples.Count);
        plan.groundHitSamples = plan.runs.Sum(r => r.samples.Count(s => s.valid));
        if (plan.groundHitSamples == 0)
        { plan.errors.Add("No ground was found under any point of the perimeter, so no wall height can be derived.\n" + plan.terrainLog); return plan; }

        // chain order, closure, gaps, borrowing
        var chain = BuildChain(plan);
        if (!plan.closed) { plan.errors.Add("The perimeter runs do not form one closed loop (open corner or disconnected run)."); return plan; }
        MarkGapsAndBorrow(plan, chain, g);

        // ---- global ground envelope (perimeter + interior of the two regions) ------------------------------------------
        float eMin = float.MaxValue, eMax = float.MinValue; int eN = 0;
        foreach (var s in chain) if (s.valid) { eMin = Mathf.Min(eMin, s.lo); eMax = Mathf.Max(eMax, s.hi); eN++; }
        Rect bb = PerimeterBounds();
        float step = Mathf.Max(2f, g.interiorSampleSpacing);
        for (float x = bb.xMin; x <= bb.xMax; x += step)
            for (float z = bb.yMin; z <= bb.yMax; z += step)
            {
                if (!PointInUnion(new Vector2(x, z))) continue;
                hits.Clear(); terrain.Query(x, z, hits);
                foreach (var h in hits) { eMin = Mathf.Min(eMin, h.y); eMax = Mathf.Max(eMax, h.y); }
                if (hits.Count > 0) eN++;
            }
        plan.envMin = eMin; plan.envMax = eMax; plan.envSamples = eN;

        // ---- sections ---------------------------------------------------------------------------------------------------
        foreach (var rp in plan.runs)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(rp.length / Mathf.Max(2f, g.maxSectionLength)));
            float sl = rp.length / count;
            for (int i = 0; i < count; i++)
            {
                var sec = new SectionPlan { run = rp, index = i, d0 = i * sl, d1 = (i + 1) * sl };
                float ext = g.sectionOverlap * 0.5f;
                float extA = i == 0 ? plan.thickness * 0.5f + Mathf.Max(0f, g.wallOffset) + Mathf.Max(0f, g.extraCornerOverlap) : ext;
                float extB = i == count - 1 ? plan.thickness * 0.5f + Mathf.Max(0f, g.wallOffset) + Mathf.Max(0f, g.extraCornerOverlap) : ext;
                sec.colliderLength = sl + extA + extB;
                float midD = (sec.d0 - extA + sec.d1 + extB) * 0.5f;
                Vector3 onRun = rp.a + rp.dir * midD;
                sec.centre = onRun + rp.outward * (plan.thickness * 0.5f + Mathf.Max(0f, g.wallOffset));
                ClassifySection(sec, plan, g, spacing);
                rp.sections.Add(sec);
            }
        }

        // ---- corner posts ---------------------------------------------------------------------------------------------
        if (g.generateCornerPosts)
        {
            var pts = new List<Vector3>();
            foreach (var rp in plan.runs) { pts.Add(rp.a); pts.Add(rp.b); }
            int pi = 0;
            foreach (var at in ClusterXZ(pts, Mathf.Max(plan.postSize, 0.25f)))
            {
                var post = new PostPlan { index = pi++, at = at };
                foreach (var rp in plan.runs)
                {
                    if (Flat2(rp.a, at) < 0.6f && rp.sections.Count > 0) post.adj.Add(rp.sections[0]);
                    if (Flat2(rp.b, at) < 0.6f && rp.sections.Count > 0) post.adj.Add(rp.sections[rp.sections.Count - 1]);
                }
                if (post.adj.Count == 0) continue;
                post.bottom = post.adj.Min(x => x.bottom);
                post.top = post.adj.Max(x => x.top);
                plan.posts.Add(post);
            }
        }

        // ---- self-check ------------------------------------------------------------------------------------------------
        foreach (var sec in plan.Sections) CheckSection(sec, g, spacing);
        foreach (var post in plan.posts)
            post.geometryOk = post.adj.All(a => post.bottom <= a.bottom + 0.01f && post.top >= a.top - 0.01f);

        if (plan.Sections.Any(s => float.IsNaN(s.bottom) || float.IsNaN(s.top)))
            plan.errors.Add("Internal error: a section ended without a height range.");
        return plan;
    }

    private List<Sample> BuildChain(Plan plan)
    {
        var chain = new List<Sample>();
        var used = new bool[plan.runs.Count];
        Vector3 start = plan.runs[0].a, cur = plan.runs[0].b;
        used[0] = true; chain.AddRange(plan.runs[0].samples);
        for (int guard = 0; guard < plan.runs.Count; guard++)
        {
            int next = -1; bool rev = false;
            for (int i = 0; i < plan.runs.Count; i++)
            {
                if (used[i]) continue;
                if (Flat2(plan.runs[i].a, cur) < 0.2f) { next = i; rev = false; break; }
                if (Flat2(plan.runs[i].b, cur) < 0.2f) { next = i; rev = true; break; }
            }
            if (next < 0) break;
            used[next] = true;
            var list = new List<Sample>(plan.runs[next].samples);
            if (rev) list.Reverse();
            chain.AddRange(list);
            cur = rev ? plan.runs[next].a : plan.runs[next].b;
        }
        plan.closed = used.All(u => u) && Flat2(cur, start) < 0.2f;

        float s = 0f;
        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0) s += new Vector2(chain[i].p.x - chain[i - 1].p.x, chain[i].p.z - chain[i - 1].p.z).magnitude;
            chain[i].s = s;
        }
        return chain;
    }

    private static float Flat2(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

    private static void MarkGapsAndBorrow(Plan plan, List<Sample> chain, ExplorationBoundary.GenerationSettings g)
    {
        float length = chain[chain.Count - 1].s;
        int i = 0;
        while (i < chain.Count)
        {
            if (chain[i].valid) { i++; continue; }
            int j = i;
            while (j + 1 < chain.Count && !chain[j + 1].valid) j++;
            float gap = (chain[j].s - chain[i].s) + g.sampleSpacing;
            for (int k = i; k <= j; k++) chain[k].gapLength = gap;
            i = j + 1;
        }
        var valid = chain.Where(s => s.valid).ToList();
        foreach (var s in chain)
        {
            if (s.valid) continue;
            float nearest = float.MaxValue, lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in valid)
            {
                float d = Mathf.Abs(v.s - s.s);
                d = Mathf.Min(d, length - d);                       // the perimeter is a closed loop
                if (d < nearest) nearest = d;
                if (d <= g.inferMaxDistance) { lo = Mathf.Min(lo, v.lo); hi = Mathf.Max(hi, v.hi); }
            }
            if (nearest <= g.inferMaxDistance) { s.borrowed = true; s.bLo = lo; s.bHi = hi; }
            else s.notEstablished = true;
        }
    }

    private static void ClassifySection(SectionPlan sec, Plan plan, ExplorationBoundary.GenerationSettings g, float spacing)
    {
        var win = sec.run.samples.Where(s => s.d >= sec.d0 - spacing - 1e-3f && s.d <= sec.d1 + spacing + 1e-3f).ToList();
        sec.total = win.Count;
        var valid = win.Where(s => s.valid).ToList();
        sec.valid = valid.Count; sec.missing = sec.total - sec.valid;
        sec.borrowed = win.Count(s => s.borrowed);
        sec.notEstablished = win.Count(s => s.notEstablished);
        sec.longestGap = win.Where(s => !s.valid).Select(s => s.gapLength).DefaultIfEmpty(0f).Max();
        sec.review = sec.missing > 0 && sec.longestGap >= g.gapReviewLength;
        sec.sources = string.Join("+", valid.SelectMany(s => s.src.Split('+')).Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        if (valid.Count > 0) { sec.gMin = valid.Min(s => s.lo); sec.gMax = valid.Max(s => s.hi); }

        float lo, hi;
        float frac = sec.total > 0 ? sec.valid / (float)sec.total : 0f;
        if (sec.missing == 0)
        {
            sec.source = Source.Measured; lo = sec.gMin; hi = sec.gMax;
        }
        else if (sec.valid > 0 && frac >= g.minValidFraction)
        {
            sec.source = Source.Partial; lo = sec.gMin; hi = sec.gMax;
            sec.note = $"{sec.missing} of {sec.total} samples without ground ignored";
        }
        else
        {
            var bor = win.Where(s => s.borrowed).ToList();
            lo = sec.valid > 0 ? sec.gMin : float.NaN;
            hi = sec.valid > 0 ? sec.gMax : float.NaN;
            if (bor.Count > 0)
            {
                float bl = bor.Min(s => s.bLo), bh = bor.Max(s => s.bHi);
                lo = float.IsNaN(lo) ? bl : Mathf.Min(lo, bl); hi = float.IsNaN(hi) ? bh : Mathf.Max(hi, bh);
            }
            if (sec.notEstablished > 0 || float.IsNaN(lo))
            {
                lo = float.IsNaN(lo) ? plan.envMin : Mathf.Min(lo, plan.envMin);
                hi = float.IsNaN(hi) ? plan.envMax : Mathf.Max(hi, plan.envMax);
                sec.source = Source.GlobalEnvelopeFallback;
                sec.note = $"{sec.notEstablished} of {sec.total} samples have no ground within {g.inferMaxDistance:F0} m: global envelope used";
            }
            else
            {
                sec.source = Source.Inferred;
                sec.note = $"ground borrowed from valid samples within {g.inferMaxDistance:F0} m along the perimeter";
            }
        }
        if (sec.review) sec.note += (sec.note.Length > 0 ? "; " : "") + $"perimeter gap {sec.longestGap:F0} m";
        sec.uMin = lo; sec.uMax = hi;
        sec.bottom = lo - g.groundMarginBelow;
        sec.top = hi + g.groundMarginAbove;
    }

    private static void CheckSection(SectionPlan sec, ExplorationBoundary.GenerationSettings g, float spacing)
    {
        int viol = 0;
        foreach (var s in sec.run.samples.Where(s => s.valid && s.d >= sec.d0 - 1e-3f && s.d <= sec.d1 + 1e-3f))
            if (s.lo - sec.bottom < g.groundMarginBelow - 0.01f || sec.top - s.hi < g.groundMarginAbove - 0.01f) viol++;
        sec.violations = viol; sec.geometryOk = viol == 0;
    }

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

    // ---------------------------------------------------------------- apply (only reached after a fully valid plan)
    private int ApplyPlan(ExplorationBoundary b, Plan plan, out string removedInfo)
    {
        var g = b.generation;
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Generate Exploration Boundary walls");

        // container: the one the component already uses, else a child with the configured name (created and wired)
        Transform container = b.BoundarySegmentsRoot != null ? b.BoundarySegmentsRoot : b.transform.Find(g.containerName);
        if (container == null)
        {
            var cgo = new GameObject(string.IsNullOrEmpty(g.containerName) ? "GeneratedSegments" : g.containerName);
            Undo.RegisterCreatedObjectUndo(cgo, "Generate Exploration Boundary walls");
            Undo.SetTransformParent(cgo.transform, b.transform, "Generate Exploration Boundary walls");
            cgo.transform.localPosition = Vector3.zero; cgo.transform.localRotation = Quaternion.identity; cgo.transform.localScale = Vector3.one;
            container = cgo.transform;
        }

        // remove ONLY what this generator owns
        int removedOwned = 0, removedLegacy = 0;
        for (int i = container.childCount - 1; i >= 0; i--)
        {
            Transform c = container.GetChild(i);
            if (c.GetComponent<ExplorationBoundaryWall>() != null) { Undo.DestroyObjectImmediate(c.gameObject); removedOwned++; }
            else if (g.removeLegacyWalls && IsLegacyWall(c, plan.layer)) { Undo.DestroyObjectImmediate(c.gameObject); removedLegacy++; }
        }
        int foreign = container.childCount;
        removedInfo = $"Replaced {removedOwned} generator-owned and {removedLegacy} legacy wall object(s); {foreign} other child object(s) of '{container.name}' left untouched" +
                      (foreign > 0 ? " (WARNING: unmanaged objects inside the wall container are still discovered as walls by ExplorationBoundary)." : ".");

        int created = 0;
        foreach (var rp in plan.runs)
        {
            var grp = NewGroup($"Run_{rp.index + 1:00}", container, rp.index, ref created);
            foreach (var sec in rp.sections)
            {
                Vector3 pos = new Vector3(sec.centre.x, (sec.bottom + sec.top) * 0.5f, sec.centre.z);
                var go = NewWall(sec.Name, grp, pos, Quaternion.LookRotation(rp.dir, Vector3.up),
                                 new Vector3(plan.thickness, sec.top - sec.bottom, sec.colliderLength), plan.layer, ref created);
                var m = go.AddComponent<ExplorationBoundaryWall>();
                m.kind = ExplorationBoundaryWall.Kind.Section; m.runIndex = rp.index; m.sectionIndex = sec.index;
                m.heightSource = (ExplorationBoundaryWall.HeightSource)(1 + (int)sec.source);
                m.validSamples = sec.valid; m.totalSamples = sec.total;
                m.groundMin = sec.uMin; m.groundMax = sec.uMax; m.colliderBottom = sec.bottom; m.colliderTop = sec.top; m.note = sec.note;
            }
        }
        if (plan.posts.Count > 0)
        {
            var grp = NewGroup("Posts", container, -1, ref created);
            foreach (var post in plan.posts)
            {
                var go = NewWall(post.Name, grp, new Vector3(post.at.x, (post.bottom + post.top) * 0.5f, post.at.z), Quaternion.identity,
                                 new Vector3(plan.postSize, post.top - post.bottom, plan.postSize), plan.layer, ref created);
                var m = go.AddComponent<ExplorationBoundaryWall>();
                m.kind = ExplorationBoundaryWall.Kind.Post; m.runIndex = -1; m.sectionIndex = post.index;
                m.colliderBottom = post.bottom; m.colliderTop = post.top; m.note = "union of " + string.Join("+", post.adj.Select(a => a.Name));
            }
        }

        var so = new SerializedObject(b);
        so.FindProperty("boundarySegmentsRoot").objectReferenceValue = container;   // manual 'boundarySegments' untouched
        so.ApplyModifiedProperties();
        b.EditorRefreshSegmentCache();

        EditorUtility.SetDirty(b);
        EditorSceneManager.MarkSceneDirty(b.gameObject.scene);
        Undo.CollapseUndoOperations(group);
        return created;
    }

    private static GameObject NewGroup(string name, Transform parent, int runIndex, ref int count)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Generate Exploration Boundary walls");
        Undo.SetTransformParent(go.transform, parent, "Generate Exploration Boundary walls");
        go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
        var m = go.AddComponent<ExplorationBoundaryWall>();
        m.kind = ExplorationBoundaryWall.Kind.Group; m.runIndex = runIndex;
        count++;
        return go;
    }

    private static GameObject NewWall(string name, GameObject parent, Vector3 worldPos, Quaternion worldRot, Vector3 size, int layer, ref int count)
    {
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Generate Exploration Boundary walls");
        Undo.SetTransformParent(go.transform, parent.transform, "Generate Exploration Boundary walls");
        go.transform.SetPositionAndRotation(worldPos, worldRot);
        go.layer = layer;
        var bc = go.AddComponent<BoxCollider>();
        bc.isTrigger = false; bc.center = Vector3.zero; bc.size = size;
        count++;
        return go;
    }

    // Objects left by the previous generator generation: Segment_NN / Corner_NN, BoxCollider only, on the wall layer.
    private static bool IsLegacyWall(Transform t, int wallLayer)
    {
        if (t.childCount != 0 || t.gameObject.layer != wallLayer) return false;
        string n = t.name;
        if (!(n.StartsWith("Segment_") || n.StartsWith("Corner_"))) return false;
        var comps = t.GetComponents<Component>();
        return comps.Length == 2 && t.GetComponent<BoxCollider>() != null;      // Transform + BoxCollider
    }

    // ---------------------------------------------------------------- report
    private string BuildReport(Plan plan, ExplorationBoundary.GenerationSettings g, ExplorationBoundary b)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var secs = plan.Sections.ToList();
        int boxes = secs.Count + plan.posts.Count;
        int groups = plan.runs.Count + (plan.posts.Count > 0 ? 1 : 0);
        sb.AppendLine($"Perimeter: {plan.runs.Count} exterior run(s), total {plan.perimeter.ToString("F0", ci)} m, closed = {plan.closed}. " +
                      $"Proposed: {secs.Count} wall sections + {plan.posts.Count} corner posts = {boxes} BoxColliders ({boxes + groups} GameObjects incl. {groups} group objects).");
        sb.AppendLine($"Walls: thickness {plan.thickness:F2} m, section length <= {g.maxSectionLength:F0} m, overlap {g.sectionOverlap:F2} m, layer '{LayerMask.LayerToName(plan.layer)}', " +
                      $"invisible (no renderer), non-trigger. Margins: bottom = lowest ground - {g.groundMarginBelow:F1} m, top = highest ground + {g.groundMarginAbove:F1} m.");
        sb.AppendLine(plan.terrainLog);
        sb.AppendLine($"Ground samples on the perimeter: {plan.groundHitSamples}/{plan.totalSamples} (every {g.sampleSpacing:F1} m). " +
                      $"Global ground envelope (perimeter + interior grid, {plan.envSamples} samples): {plan.envMin.ToString("F1", ci)} .. {plan.envMax.ToString("F1", ci)} m.");
        sb.AppendLine();
        sb.AppendLine("run  length yaw     sections  measured partial inferred envelopeFallback  | ground used min..max     | collider bottom..top");
        foreach (var rp in plan.runs)
        {
            int m = rp.sections.Count(x => x.source == Source.Measured), p = rp.sections.Count(x => x.source == Source.Partial);
            int i = rp.sections.Count(x => x.source == Source.Inferred), f = rp.sections.Count(x => x.source == Source.GlobalEnvelopeFallback);
            sb.AppendLine($"R{rp.index + 1:00} {rp.length,7:F1} {rp.yaw,6:F1}  {rp.sections.Count,8} {m,9} {p,7} {i,8} {f,16}  | " +
                          $"{rp.sections.Min(x => x.uMin),6:F1}..{rp.sections.Max(x => x.uMax),-6:F1}           | {rp.sections.Min(x => x.bottom),6:F1}..{rp.sections.Max(x => x.top),-6:F1}");
        }
        sb.AppendLine();
        var flagged = secs.Where(x => x.source != Source.Measured || x.review || !x.geometryOk || x.top - x.bottom > LARGE_RANGE).ToList();
        sb.AppendLine($"Sections needing attention ({flagged.Count} of {secs.Count}; fully measured sections are omitted - all rows are in the Console log when warnings exist):");
        sb.AppendLine("name              d0..d1    samples valid miss bor n/e | ground min..max  | bottom    top  height | source / notes");
        foreach (var x in flagged)
            sb.AppendLine($"{x.Name,-17} {x.d0,5:F0}..{x.d1,-5:F0} {x.total,5} {x.valid,5} {x.missing,4} {x.borrowed,3} {x.notEstablished,3} | " +
                          $"{(float.IsNaN(x.gMin) ? "    -" : x.gMin.ToString("F1", ci)),6}..{(float.IsNaN(x.gMax) ? "-" : x.gMax.ToString("F1", ci)),-6} | " +
                          $"{x.bottom,6:F1} {x.top,6:F1} {x.top - x.bottom,6:F1} | {x.source}{(x.review ? " REVIEW" : "")}{(x.top - x.bottom > LARGE_RANGE ? " TALL" : "")}{(x.geometryOk ? "" : " CHECK-FAILED")}" +
                          $"{(x.note.Length > 0 ? " - " + x.note : "")}");
        sb.AppendLine();
        int gm = secs.Count(x => x.source == Source.Measured), gp = secs.Count(x => x.source == Source.Partial),
            gi = secs.Count(x => x.source == Source.Inferred), gf = secs.Count(x => x.source == Source.GlobalEnvelopeFallback);
        float fbLen = secs.Where(x => x.source == Source.GlobalEnvelopeFallback).Sum(x => x.d1 - x.d0);
        sb.AppendLine($"Height provenance: measured {gm}, partial {gp}, inferred {gi}, global-envelope fallback {gf} (~{fbLen:F0} m of wall). " +
                      (gf > 0 ? "The fallback is NOT a terrain measurement: it only guarantees coverage relative to the known elevation envelope. " : "") +
                      $"Geometric self-check: {secs.Count(x => x.geometryOk)}/{secs.Count} sections cover every valid sample with the margins; " +
                      $"{plan.posts.Count(x => x.geometryOk)}/{plan.posts.Count} posts cover their neighbours.");
        sb.AppendLine("Preview/Generate use the Area Volumes' serialized geometry only; legacy or other objects are never used as input.");
        return sb.ToString();
    }

    private Rect PerimeterBounds()
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var poly in _polys)
            foreach (var p in poly) { x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.y); z1 = Mathf.Max(z1, p.y); }
        return Rect.MinMaxRect(x0 - 10f, z0 - 10f, x1 + 10f, z1 + 10f);
    }

    private static float ApproxY(ExplorationBoundary b)
    {
        var vols = (b.AreaVolumes ?? new Collider[0]).Where(c => c != null).ToList();
        return vols.Count == 0 ? 0f : vols.Average(c => c.transform.position.y);
    }

    // ---------------------------------------------------------------- terrain surface (editor-time, direct mesh access)
    private sealed class TerrainSurface
    {
        public struct Hit { public float y; public string source; }

        private sealed class Tile
        {
            public string name, source;
            public Vector3[] v; public int[] t;
            public float minX, maxX, minZ, maxZ;
            public int nx, nz;
            public int[] cellStart, cellTris;
        }

        private const float CELL = 25f;
        private readonly List<Tile> _tiles = new List<Tile>();
        public int Count => _tiles.Count;

        public static TerrainSurface Collect(Rect area, StringBuilder log)
        {
            var ts = new TerrainSurface();
            var seen = new HashSet<GameObject>();
            int unreadable = 0, skippedOutside = 0;
            var perSource = new SortedDictionary<string, int>();

            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
                ts.TryAdd(mf.gameObject, mf.sharedMesh, area, seen, ref unreadable, ref skippedOutside, perSource);
            foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
                ts.TryAdd(mc.gameObject, mc.sharedMesh, area, seen, ref unreadable, ref skippedOutside, perSource);

            log.AppendLine("Terrain meshes used (intersecting the perimeter): " +
                           (perSource.Count == 0 ? "none" : string.Join(", ", perSource.Select(kv => kv.Key + " x" + kv.Value))) +
                           $"; outside the perimeter area: {skippedOutside}; unreadable meshes: {unreadable}.");
            return ts;
        }

        private void TryAdd(GameObject go, Mesh mesh, Rect area, HashSet<GameObject> seen, ref int unreadable, ref int outside,
                            SortedDictionary<string, int> perSource)
        {
            if (mesh == null || seen.Contains(go)) return;
            string source = Classify(go);
            if (source == null) return;
            seen.Add(go);

            Vector3[] lv; int[] tri;
            try { lv = mesh.vertices; tri = mesh.triangles; }
            catch (Exception) { unreadable++; return; }
            if (lv == null || lv.Length == 0 || tri == null || tri.Length < 3) { unreadable++; return; }

            Matrix4x4 m = go.transform.localToWorldMatrix;
            var wv = new Vector3[lv.Length];
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < lv.Length; i++)
            {
                Vector3 w = m.MultiplyPoint3x4(lv[i]);
                wv[i] = w;
                minX = Mathf.Min(minX, w.x); maxX = Mathf.Max(maxX, w.x); minZ = Mathf.Min(minZ, w.z); maxZ = Mathf.Max(maxZ, w.z);
            }
            if (maxX < area.xMin || minX > area.xMax || maxZ < area.yMin || minZ > area.yMax) { outside++; return; }

            var tile = new Tile { name = go.name, source = source, v = wv, t = tri, minX = minX, maxX = maxX, minZ = minZ, maxZ = maxZ };
            BuildGrid(tile);
            _tiles.Add(tile);
            perSource[source] = perSource.TryGetValue(source, out int c) ? c + 1 : 1;
        }

        // Identify terrain / bridge geometry by SOURCE ASSET PATH (independent of layers and activation).
        private static string Classify(GameObject go)
        {
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject);
                string path = src != null ? AssetDatabase.GetAssetPath(src) : "";
                if (path.Contains("/TIN/Textured/")) return "Textured";
                if (path.Contains("/TIN/White/")) return "White";
                if (path.Contains("/PONT/")) return "Bridge";
                if (t.name.Contains("TIN_GapPatch")) return "GapPatch";
            }
            return null;
        }

        private static void BuildGrid(Tile t)
        {
            t.nx = Mathf.Max(1, Mathf.CeilToInt((t.maxX - t.minX) / CELL));
            t.nz = Mathf.Max(1, Mathf.CeilToInt((t.maxZ - t.minZ) / CELL));
            int cells = t.nx * t.nz;
            var counts = new int[cells + 1];
            int tris = t.t.Length / 3;
            for (int pass = 0; pass < 2; pass++)
            {
                var fill = pass == 1 ? (int[])counts.Clone() : null;
                if (pass == 1) { t.cellTris = new int[counts[cells]]; }
                for (int i = 0; i < tris; i++)
                {
                    Vector3 a = t.v[t.t[i * 3]], b = t.v[t.t[i * 3 + 1]], c = t.v[t.t[i * 3 + 2]];
                    int x0 = CellX(t, Mathf.Min(a.x, Mathf.Min(b.x, c.x))), x1 = CellX(t, Mathf.Max(a.x, Mathf.Max(b.x, c.x)));
                    int z0 = CellZ(t, Mathf.Min(a.z, Mathf.Min(b.z, c.z))), z1 = CellZ(t, Mathf.Max(a.z, Mathf.Max(b.z, c.z)));
                    for (int cz = z0; cz <= z1; cz++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            int ci = cz * t.nx + cx;
                            if (pass == 0) counts[ci + 1]++;
                            else t.cellTris[fill[ci]++] = i;
                        }
                }
                if (pass == 0)
                {
                    for (int i = 0; i < cells; i++) counts[i + 1] += counts[i];
                    t.cellStart = counts;
                }
            }
        }

        private static int CellX(Tile t, float x) => Mathf.Clamp((int)((x - t.minX) / CELL), 0, t.nx - 1);
        private static int CellZ(Tile t, float z) => Mathf.Clamp((int)((z - t.minZ) / CELL), 0, t.nz - 1);

        /// <summary>All surface heights above (x,z) (several where tiles overlap or a bridge crosses the terrain).</summary>
        public void Query(float x, float z, List<Hit> outHits)
        {
            foreach (var t in _tiles)
            {
                if (x < t.minX || x > t.maxX || z < t.minZ || z > t.maxZ) continue;
                int ci = CellZ(t, z) * t.nx + CellX(t, x);
                for (int k = t.cellStart[ci]; k < t.cellStart[ci + 1]; k++)
                {
                    int i = t.cellTris[k];
                    Vector3 a = t.v[t.t[i * 3]], b = t.v[t.t[i * 3 + 1]], c = t.v[t.t[i * 3 + 2]];
                    float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(d) < 1e-12f) continue;
                    float l1 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                    float l2 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                    float l3 = 1f - l1 - l2;
                    const float e = -1e-5f;
                    if (l1 >= e && l2 >= e && l3 >= e)
                        outHits.Add(new Hit { y = l1 * a.y + l2 * b.y + l3 * c.y, source = t.source });
                }
            }
        }
    }

    // ---------------------------------------------------------------- perimeter maths (rotation-correct, unchanged)
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
