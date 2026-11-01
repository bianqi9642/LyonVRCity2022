// EntranceExitProjectionTool.cs
//
// EDITOR-ONLY tool: strict vertical NavMesh projection of the NPC entrance / exit waypoints under EnvironmentConfig/Npc/EntranceExit in the OPEN scene.
// Menu: Tools > NPC Wandering Points > Preview EntranceExit Projection (no changes)   and   Apply EntranceExit Projection.
// The existing wandering Preview / Apply (WanderingPointsSetupTool) is not touched; this tool reuses its geometry core (WanderingProjectionMath) and its
// settings (WanderingPointsSettings: horizontal tolerance, ambiguity tolerance, surface merge tolerance, already-on tolerance, review displacement,
// verification vertical tolerance - edit them under Tools > NPC Wandering Points > Settings...).
//
// Which objects: the direct children of EntranceExit are the waypoint roots. A root is an ACTIVE entrance / exit when SpawnerManager.entrances and/or
// SpawnerManager.exits reference it (here both arrays hold the same objects). Roots that nothing references (inactive spare markers) are reported but never
// moved. A root with children that no spawner array references is treated as a nested container and stops the tool (nothing is guessed). Only the root
// transform is ever moved; its children, components and serialized references stay as they are. Nothing is renamed, created, deleted or re-assigned.
//
// Projection (per root): the NavMesh surfaces crossed by the vertical line through the EXACT original X/Z come from NavMesh.CalculateTriangulation()
// (barycentric height inside each triangle containing the point). The surface nearest to the original Y is used unless the second nearest is almost as
// near (ambiguity tolerance) - then the point is AMBIGUOUS and left alone. A unique surface is accepted however far it is; |dY| above the review
// threshold is only flagged. The move is made in the PARENT'S space (the parent chain is not assumed to be identity) and the resulting world X/Z are checked
// for exact equality, so a move that would change X/Z is not applied.
//
// Secondary check: NavMesh.SamplePosition at the final position. It samples the detail mesh, the triangulation is the polygon mesh, so on slopes the two
// heights can differ. A triangulation intersection that is confirmed but not matched by SamplePosition is NOT reported as "no intersection": it gets its own
// status SECONDARY_CHECK_DIFFERED and is applied only if you explicitly choose to. A wider-radius SamplePosition is recorded for those points as
// DIAGNOSTIC INFORMATION ONLY; it never changes a status or a position.
//
// Statuses: PROJECTED, ALREADY_ON_NAVMESH, SECONDARY_CHECK_DIFFERED, NO_VERTICAL_INTERSECTION, AMBIGUOUS_SURFACE, INVALID_TRIANGLE, XZ_CHANGED.
// Apply is one Undo step; the scene is marked dirty and saved only if you say so. Reports: <project>/Temp/EntranceExitNavMeshProjection_preview.csv / _apply.csv.
// All comments in English per project convention.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using WPM = WanderingProjectionMath;

public static class EntranceExitProjectionTool
{
    const string GroupPath = "EnvironmentConfig/Npc/EntranceExit";
    const float DiagnosticRadius = 2f;   // wider SamplePosition radius, recorded for SECONDARY_CHECK_DIFFERED points only (information, never a pass criterion)

    const string SProjected = "PROJECTED", SAlready = "ALREADY_ON_NAVMESH", SSecondary = "SECONDARY_CHECK_DIFFERED", SNone = "NO_VERTICAL_INTERSECTION",
                 SAmbiguous = "AMBIGUOUS_SURFACE", SInvalid = "INVALID_TRIANGLE", SXz = "XZ_CHANGED";

    class Entry
    {
        public Transform t;
        public string name, path, role, status = "", reason = "", secondary = "", applied = "no";
        public bool referenced;
        public Vector3 original, finalPos;        // finalPos = the position the projection would give (original if unresolved)
        public float surfaceY = float.NaN;
        public List<WPM.Surface> surfaces = new List<WPM.Surface>();
        public int degenerateNear;
        public float secRes = float.NaN, secDy = float.NaN, wideRes = float.NaN, wideDy = float.NaN;
        public Vector3 afterPos; public bool post, postXz, postRot, postScale, postChildren; public string postNav = "";
        public bool Confirmed { get { return status == SProjected || status == SAlready; } }
        public bool HasSurface { get { return !float.IsNaN(surfaceY) && status != SNone && status != SInvalid && status != SAmbiguous; } }
        public float Dy { get { return finalPos.y - original.y; } }
    }

    [MenuItem("Tools/NPC Wandering Points/Preview EntranceExit Projection (no changes)")]
    static void Preview() { Run(false); }

    [MenuItem("Tools/NPC Wandering Points/Apply EntranceExit Projection")]
    static void Apply() { Run(true); }

    static void Run(bool apply)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { Fail("Leave Play Mode first."); return; }
        Scene scene = SceneManager.GetActiveScene();
        Transform group = FindByPath(scene, GroupPath);
        if (group == null) { Fail("Could not find '" + GroupPath + "' in the active scene '" + scene.name + "'."); return; }
        SpawnerManager spawner = Object.FindObjectOfType<SpawnerManager>(true);
        if (spawner == null || spawner.gameObject.scene != scene) { Fail("No SpawnerManager found in the active scene."); return; }

        // spawner references (read only)
        HashSet<Transform> entrances = ReadArray(spawner, "entrances"), exits = ReadArray(spawner, "exits");
        string[] entBefore = SnapshotArray(spawner, "entrances"), exitBefore = SnapshotArray(spawner, "exits"), wanderBefore = SnapshotArray(spawner, "wanderingPoints");

        // roots
        List<Entry> all = new List<Entry>();
        for (int i = 0; i < group.childCount; i++)
        {
            Transform c = group.GetChild(i);
            bool isEnt = entrances.Contains(c), isExit = exits.Contains(c);
            if (c.childCount > 0 && !isEnt && !isExit) { Fail("'" + PathOf(c) + "' has child objects and is not referenced by SpawnerManager.entrances / exits: it looks like a nested container. Nothing is guessed - tell me how to treat it."); return; }
            all.Add(new Entry { t = c, name = c.name, path = PathOf(c), original = c.position, referenced = isEnt || isExit,
                                role = isEnt && isExit ? "Entrance+Exit" : isEnt ? "Entrance" : isExit ? "Exit" : "Unreferenced" });
        }
        if (all.Count == 0) { Fail("'" + GroupPath + "' has no children."); return; }

        // projection settings (shared with the wandering tool)
        float hTol = WanderingPointsSettings.HorizontalTolerance, ambTol = WanderingPointsSettings.AmbiguityTolerance, mergeTol = WanderingPointsSettings.MergeTolerance,
              onTol = WanderingPointsSettings.AlreadyOnTolerance, vTol = WanderingPointsSettings.VerifyVerticalTolerance, review = WanderingPointsSettings.ReviewDisplacement;
        if (WanderingPointsSettings.Method != WanderingPointsSettings.ProjectionMethod.Triangulation)
        { Fail("Settings > Projection method is StrictSampling. This tool uses the NavMesh triangulation; set the method to Triangulation (Tools > NPC Wandering Points > Settings...). Nothing was changed."); return; }

        WPM.TriangleIndex index; int triangles;
        try
        {
            EditorUtility.DisplayProgressBar("EntranceExit projection", "Reading the NavMesh triangulation...", 0.1f);
            NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length < 3) { Fail("The loaded NavMesh has no triangulation (is the NavMesh data loaded?). Nothing was changed."); return; }
            float[] vx = new float[tri.vertices.Length], vy = new float[vx.Length], vz = new float[vx.Length];
            for (int i = 0; i < vx.Length; i++) { vx[i] = tri.vertices[i].x; vy[i] = tri.vertices[i].y; vz[i] = tri.vertices[i].z; }
            List<float> px = new List<float>(), pz = new List<float>();
            foreach (Entry e in all) { px.Add(e.original.x); pz.Add(e.original.z); }
            EditorUtility.DisplayProgressBar("EntranceExit projection", "Indexing " + (tri.indices.Length / 3) + " NavMesh triangles...", 0.4f);
            index = new WPM.TriangleIndex(vx, vy, vz, tri.indices, tri.areas, 20f, px, pz);
            triangles = index.TriangleCount;
            foreach (Entry e in all) Evaluate(e, index, mergeTol, ambTol, onTol, hTol, vTol);
        }
        finally { EditorUtility.ClearProgressBar(); }

        // ---- summary (referenced points only; unreferenced ones are listed separately) ----
        int nEnt = 0, nExit = 0, nIds = 0, nSkipped = 0, projected = 0, already = 0, secondary = 0, failed = 0, ambiguous = 0, large = 0;
        float maxUp = 0f, maxDown = 0f;
        foreach (Entry e in all)
        {
            if (!e.referenced) { nSkipped++; continue; }
            nIds++; if (e.role.StartsWith("Entrance")) nEnt++; if (e.role.EndsWith("Exit")) nExit++;
            if (e.status == SProjected) projected++; else if (e.status == SAlready) already++; else if (e.status == SSecondary) secondary++;
            else if (e.status == SAmbiguous) ambiguous++; else failed++;
            if (e.HasSurface)
            {
                float dy = e.surfaceY - e.original.y;
                if (Mathf.Abs(dy) > review) large++;
                if (dy > maxUp) maxUp = dy; if (dy < maxDown) maxDown = dy;
            }
        }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Entrance waypoints: " + nEnt + "   Exit waypoints: " + nExit + "   Identified (distinct objects): " + nIds + "   Unreferenced/skipped: " + nSkipped);
        sb.AppendLine("NavMesh triangles: " + triangles + "   horizontal tol " + hTol.ToString("0.###", CultureInfo.InvariantCulture) + " m   merge tol " + mergeTol.ToString("0.###", CultureInfo.InvariantCulture) + " m   ambiguity tol " + ambTol.ToString("0.###", CultureInfo.InvariantCulture) + " m");
        sb.AppendLine("Projectable (secondary check passed): " + (projected + already) + "  (to move: " + projected + ", already on NavMesh: " + already + ")");
        sb.AppendLine("Triangulation confirmed but SamplePosition differed: " + secondary);
        sb.AppendLine("Failed (no intersection / invalid triangle / X-Z): " + failed + "   Ambiguous: " + ambiguous);
        sb.AppendLine("|dY| > " + review.ToString("0.###", CultureInfo.InvariantCulture) + " m (review): " + large + "   Max up: " + maxUp.ToString("F2", CultureInfo.InvariantCulture) + " m   Max down: " + maxDown.ToString("F2", CultureInfo.InvariantCulture) + " m");
        int unresolved = failed + ambiguous;
        if (unresolved + secondary > 0)
        {
            sb.AppendLine("\nNot projectable / needs a decision (all are in the CSV):");
            int shown = 0;
            foreach (Entry e in all) { if (!e.referenced || e.Confirmed || shown >= 12) continue; shown++; sb.AppendLine("  " + e.status + "  " + e.name + "  (" + Fs(e.original.x) + ", " + Fs(e.original.y) + ", " + Fs(e.original.z) + ")"); }
        }
        string summary = sb.ToString();

        if (!apply)
        {
            string rp = WriteReport(all, "preview");
            Debug.Log("[EntranceExitProjection] PREVIEW (nothing changed).\n" + summary + "Report: " + rp);
            EditorUtility.DisplayDialog("EntranceExit - preview", summary + "\nNothing was changed.\nReport: " + rp, "OK");
            return;
        }

        // ---- Apply: explicit choice, never automatic ----
        bool includeSecondary = false;
        if (projected + already + secondary == 0) { Fail("No waypoint can be projected, so there is nothing to apply.\n\n" + summary); return; }
        if (secondary > 0)
        {
            int c = EditorUtility.DisplayDialogComplex("EntranceExit - apply",
                summary + "\n" + secondary + " point(s) have a confirmed triangulation surface but SamplePosition did not match it within the verification tolerance (typically detail-mesh height on slopes).\n" +
                "Apply confirmed points only, or also those " + secondary + " (using the triangulation height)?", "Confirmed only", "Cancel", "Confirmed + differing");
            if (c == 1) return;
            includeSecondary = c == 2;
        }
        else if (!EditorUtility.DisplayDialog("EntranceExit - apply", summary + "\nThis moves the Y of each confirmed point onto the NavMesh (X/Z unchanged). Nothing is renamed or re-assigned.\nOne Undo step; the scene is not saved automatically.", "Apply", "Cancel")) return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Project EntranceExit points onto NavMesh");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (Entry e in all)
        {
            if (!e.referenced) continue;
            bool doMove = e.status == SProjected || (includeSecondary && e.status == SSecondary);
            if (!doMove) { e.applied = e.status == SAlready ? "unchanged_already_on_navmesh" : "no"; continue; }

            Transform t = e.t;
            Quaternion rotBefore = t.localRotation; Vector3 scaleBefore = t.localScale; int childrenBefore = t.childCount;
            Vector3 beforeLocal = t.localPosition;
            Vector3 dWorld = new Vector3(0f, e.surfaceY - e.original.y, 0f);
            Vector3 dLocal = t.parent != null ? t.parent.InverseTransformVector(dWorld) : dWorld;
            Undo.RecordObject(t, "Project waypoint onto NavMesh");
            t.localPosition = beforeLocal + dLocal;
            if (t.position.x != e.original.x || t.position.z != e.original.z)
            {
                t.localPosition = beforeLocal;
                e.status = SXz; e.reason += ";world_xz_would_change(rolled_back)"; e.applied = "rolled_back";
                continue;
            }
            e.applied = "moved";
            e.finalPos = new Vector3(e.original.x, e.surfaceY, e.original.z);
            e.postRot = t.localRotation == rotBefore; e.postScale = t.localScale == scaleBefore; e.postChildren = t.childCount == childrenBefore;
        }
        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);

        // ---- post-application verification (independent of the move code) ----
        int moved = 0, verifiedOk = 0;
        foreach (Entry e in all)
        {
            e.afterPos = e.t.position;
            if (e.applied != "moved") continue;
            moved++;
            e.post = true;
            e.postXz = e.afterPos.x == e.original.x && e.afterPos.z == e.original.z;
            NavMeshHit h;
            bool found = NavMesh.SamplePosition(e.afterPos, out h, vTol + hTol + 0.01f, NavMesh.AllAreas);
            bool navOk = found && new Vector2(h.position.x - e.afterPos.x, h.position.z - e.afterPos.z).magnitude <= hTol && Mathf.Abs(h.position.y - e.afterPos.y) <= vTol;
            e.postNav = found ? (navOk ? "on_navmesh" : "differs(dxz=" + Fs(new Vector2(h.position.x - e.afterPos.x, h.position.z - e.afterPos.z).magnitude) + ";dy=" + Fs(h.position.y - e.afterPos.y) + ")") : "not_found_within_radius";
            if (e.postXz && e.postRot && e.postScale && e.postChildren && Mathf.Abs(e.afterPos.y - e.surfaceY) < 1e-4f) verifiedOk++;
        }
        bool refsIntact = SameArray(entBefore, SnapshotArray(spawner, "entrances")) && SameArray(exitBefore, SnapshotArray(spawner, "exits")) && SameArray(wanderBefore, SnapshotArray(spawner, "wanderingPoints"));
        string report = WriteReport(all, "apply");
        string result = summary + "\nMoved: " + moved + "   fully verified (X/Z exact, rotation, scale, children, height): " + verifiedOk +
                        "\nSpawnerManager entrances / exits / wanderingPoints unchanged: " + (refsIntact ? "yes" : "NO - CHECK") + "\nReport: " + report;
        Debug.Log("[EntranceExitProjection] APPLIED.\n" + result);
        int choice = EditorUtility.DisplayDialogComplex("EntranceExit - applied", result + "\n\nSave the scene now? (It also saves any other unsaved changes in it.)", "Save scene", "Not now", "");
        if (choice == 0) EditorSceneManager.SaveScene(scene);
    }

    // ---- one waypoint -----------------------------------------------------------------------------------------------------------

    static void Evaluate(Entry e, WPM.TriangleIndex index, float mergeTol, float ambTol, float onTol, float hTol, float vTol)
    {
        List<WPM.Surface> raw = new List<WPM.Surface>();
        index.Query(e.original.x, e.original.z, raw, out e.degenerateNear);
        e.surfaces = WPM.Cluster(raw, mergeTol);
        e.finalPos = e.original;

        int sel; string reason;
        WPM.Outcome outcome = WPM.Select(e.surfaces, e.original.y, ambTol, onTol, out sel, out reason);
        e.reason = reason;
        if (sel >= 0) e.surfaceY = e.surfaces[sel].y;

        if (outcome == WPM.Outcome.NoIntersection) { e.status = e.degenerateNear > 0 ? SInvalid : SNone; e.surfaceY = float.NaN; return; }
        if (outcome == WPM.Outcome.Ambiguous) { e.status = SAmbiguous; return; }

        float targetY = outcome == WPM.Outcome.AlreadyOn ? e.original.y : e.surfaceY;
        Vector3 fp = new Vector3(e.original.x, targetY, e.original.z);

        // exactness of the world X/Z after the move in the parent's space (predicted, no scene change)
        if (outcome == WPM.Outcome.Projected)
        {
            Transform t = e.t;
            Vector3 dWorld = new Vector3(0f, targetY - e.original.y, 0f);
            Vector3 dLocal = t.parent != null ? t.parent.InverseTransformVector(dWorld) : dWorld;
            Vector3 w0 = t.parent != null ? t.parent.TransformPoint(t.localPosition) : t.localPosition;
            Vector3 w1 = t.parent != null ? t.parent.TransformPoint(t.localPosition + dLocal) : t.localPosition + dLocal;
            if (w1.x != w0.x || w1.z != w0.z) { e.status = SXz; e.reason += ";world_xz_not_exact_after_move"; return; }
        }

        // secondary check: SamplePosition (detail mesh) at the final position
        NavMeshHit hit;
        bool found = NavMesh.SamplePosition(fp, out hit, vTol + hTol + 0.01f, NavMesh.AllAreas);
        if (found) { e.secRes = new Vector2(hit.position.x - fp.x, hit.position.z - fp.z).magnitude; e.secDy = hit.position.y - fp.y; }
        bool secOk = found && e.secRes <= hTol && Mathf.Abs(e.secDy) <= vTol;
        e.secondary = secOk ? "passed" : !found ? "samplepos_none_within_radius" : e.secRes > hTol ? "samplepos_xz_exceeds_tolerance" : "samplepos_dy_exceeds_tolerance";
        e.finalPos = fp;
        if (secOk) { e.status = outcome == WPM.Outcome.AlreadyOn ? SAlready : SProjected; return; }

        // triangulation confirmed, SamplePosition did not match: its own status, with a wider diagnostic probe (information only)
        e.status = SSecondary;
        NavMeshHit wide;
        if (NavMesh.SamplePosition(fp, out wide, DiagnosticRadius, NavMesh.AllAreas)) { e.wideRes = new Vector2(wide.position.x - fp.x, wide.position.z - fp.z).magnitude; e.wideDy = wide.position.y - fp.y; }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    static HashSet<Transform> ReadArray(SpawnerManager sm, string field)
    {
        var set = new HashSet<Transform>();
        SerializedProperty p = new SerializedObject(sm).FindProperty(field);
        if (p != null && p.isArray)
            for (int i = 0; i < p.arraySize; i++) { Transform t = p.GetArrayElementAtIndex(i).objectReferenceValue as Transform; if (t != null) set.Add(t); }
        return set;
    }

    static string[] SnapshotArray(SpawnerManager sm, string field)
    {
        SerializedProperty p = new SerializedObject(sm).FindProperty(field);
        if (p == null || !p.isArray) return new string[0];
        string[] s = new string[p.arraySize];
        for (int i = 0; i < s.Length; i++) { Object o = p.GetArrayElementAtIndex(i).objectReferenceValue; s[i] = o == null ? "null" : o.GetInstanceID().ToString(CultureInfo.InvariantCulture); }
        return s;
    }

    static bool SameArray(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    static Transform FindByPath(Scene scene, string path)
    {
        int slash = path.IndexOf('/');
        string first = path.Substring(0, slash), rest = path.Substring(slash + 1);
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name != first) continue;
            Transform t = root.transform.Find(rest);
            if (t != null) return t;
        }
        return null;
    }

    static string PathOf(Transform t)
    {
        string p = t.name;
        for (Transform a = t.parent; a != null; a = a.parent) p = a.name + "/" + p;
        return p;
    }

    static string WriteReport(List<Entry> all, string kind)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "EntranceExitNavMeshProjection_" + kind + ".csv");
        var sb = new StringBuilder("waypoint,hierarchy_path,role,active_in_hierarchy,original_x,original_y,original_z,calculated_surface_y,final_x,final_y,final_z," +
                                   "vertical_displacement,horizontal_residual,candidate_surface_count,candidate_surface_heights,selected_surface_height,projection_method,status,selection_reason," +
                                   "secondary_check,secondary_residual_xz,secondary_dy,diagnostic_wide_residual_xz,diagnostic_wide_dy,large_displacement_review,action," +
                                   "post_world_xz_exact,post_rotation_unchanged,post_scale_unchanged,post_children_unchanged,post_navmesh_check\n");
        float review = WanderingPointsSettings.ReviewDisplacement;
        foreach (Entry e in all)
        {
            Vector3 f = kind == "apply" ? e.afterPos : e.finalPos;
            float hres = new Vector2(f.x - e.original.x, f.z - e.original.z).magnitude;
            var heights = new StringBuilder();
            for (int i = 0; i < e.surfaces.Count; i++) { if (i > 0) heights.Append(';'); heights.Append(Fs(e.surfaces[i].y)); }
            bool large = e.HasSurface && Mathf.Abs(e.surfaceY - e.original.y) > review;
            sb.Append(Csv(e.name)).Append(',').Append(Csv(e.path)).Append(',').Append(e.role).Append(',').Append(e.t.gameObject.activeInHierarchy ? "1" : "0").Append(',')
              .Append(Fs(e.original.x)).Append(',').Append(Fs(e.original.y)).Append(',').Append(Fs(e.original.z)).Append(',')
              .Append(float.IsNaN(e.surfaceY) ? "" : Fs(e.surfaceY)).Append(',')
              .Append(Fs(f.x)).Append(',').Append(Fs(f.y)).Append(',').Append(Fs(f.z)).Append(',')
              .Append(Fs(f.y - e.original.y)).Append(',').Append(hres.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.surfaces.Count).Append(',').Append(heights).Append(',').Append(float.IsNaN(e.surfaceY) ? "" : Fs(e.surfaceY)).Append(',').Append("triangulation").Append(',')
              .Append(e.status).Append(',').Append(Csv(e.reason)).Append(',').Append(e.secondary).Append(',')
              .Append(float.IsNaN(e.secRes) ? "" : e.secRes.ToString("F6", CultureInfo.InvariantCulture)).Append(',').Append(float.IsNaN(e.secDy) ? "" : Fs(e.secDy)).Append(',')
              .Append(float.IsNaN(e.wideRes) ? "" : e.wideRes.ToString("F6", CultureInfo.InvariantCulture)).Append(',').Append(float.IsNaN(e.wideDy) ? "" : Fs(e.wideDy)).Append(',')
              .Append(large ? "1" : "0").Append(',').Append(e.referenced ? e.applied : "skipped_unreferenced").Append(',')
              .Append(e.post ? (e.postXz ? "1" : "0") : "").Append(',').Append(e.post ? (e.postRot ? "1" : "0") : "").Append(',').Append(e.post ? (e.postScale ? "1" : "0") : "").Append(',')
              .Append(e.post ? (e.postChildren ? "1" : "0") : "").Append(',').Append(e.postNav).Append('\n');
        }
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    static string Csv(string s) { return (s ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' '); }
    static string Fs(float v) { return v.ToString("F4", CultureInfo.InvariantCulture); }

    static void Fail(string message)
    {
        Debug.LogError("[EntranceExitProjection] " + message);
        EditorUtility.DisplayDialog("EntranceExit projection", message, "OK");
    }
}
