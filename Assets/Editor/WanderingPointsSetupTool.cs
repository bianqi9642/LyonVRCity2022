// WanderingPointsSetupTool.cs
//
// EDITOR-ONLY one-shot tool for the Part 2 NPC wandering waypoints in the OPEN scene (LyonVRVity35). Menu: Tools > NPC Wandering Points.
//   Preview (no changes)            - computes and reports everything below, touches nothing.
//   Apply: project, rename, assign  - performs it, as ONE Undo step; the scene is marked dirty and only saved if you choose so at the end.
//   Settings...                     - tolerances and method (stored in EditorPrefs, not in the scene).
//   Self-test (synthetic geometry)  - runs the projection-geometry checks of WanderingProjectionMath and logs the result.
//
// Pipeline (waypoints = the direct children of EnvironmentConfig/Npc/Wandering/Organic and .../Grid):
//   A. STRICT VERTICAL PROJECTION. For each waypoint the surfaces of the NavMesh crossed by the vertical line through its EXACT original X/Z are found
//      from NavMesh.CalculateTriangulation() (barycentric height inside every triangle whose XZ projection contains the point). No nearby point is
//      ever substituted: if the line misses the NavMesh the point fails. When several surfaces are crossed (ground, bridge deck, upper level) the one
//      nearest to the point's original Y is used unless the second nearest is nearly as near (ambiguity tolerance) - then the point is AMBIGUOUS and
//      left alone. Each result is verified independently with NavMesh.SamplePosition at the final position. Only world Y is changed; the move is made in
//      the parent's space so X/Z stay bit-identical, and that is checked.
//      Statuses: PROJECTED, ALREADY_ON_NAVMESH, NO_VERTICAL_INTERSECTION, AMBIGUOUS_SURFACE, INVALID_TRIANGLE, VERIFICATION_FAILED.
//   B. Rename by Hierarchy sibling order, one-based, per group: Organic -> O1.., Grid -> G1.. Only resolved points (PROJECTED / ALREADY_ON_NAVMESH)
//      are renamed; if some are unresolved the numbering runs over the resolved ones so it stays continuous, and unresolved points keep their names.
//   C. SpawnerManager.wanderingPoints (Part 2 Itinerary Stops > Wandering Points) = resolved Organic points, then resolved Grid points.
// Unresolved points are never moved, renamed or assigned silently: Preview lists them, Apply asks for an explicit partial-apply confirmation.
// The method needs the loaded NavMesh of the open scene. CalculateTriangulation has no agent-type argument: it returns the geometry of the loaded NavMesh
// data (this project uses the default agent type 0 with all areas), and the SamplePosition verification uses NavMesh.AllAreas.
// A CSV report (original / final position, residuals, candidate surfaces, selection reason, status, new name) is written to <project>/Temp.
// Refuses to run in Play Mode, if the groups or the SpawnerManager are missing, or if a waypoint has children (nested containers are not guessed).
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

// ---------------------------------------------------------------------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------------------------------------------------------------------
public static class WanderingPointsSettings
{
    public enum ProjectionMethod { Triangulation = 0, StrictSampling = 1 }

    const string P = "WanderingPoints.";
    public static ProjectionMethod Method { get { return (ProjectionMethod)EditorPrefs.GetInt(P + "method", 0); } set { EditorPrefs.SetInt(P + "method", (int)value); } }
    /// <summary>Largest X/Z deviation (m) accepted in the verification and for StrictSampling candidates. A tolerance for ACCEPTING, never for moving a waypoint.</summary>
    public static float HorizontalTolerance { get { return EditorPrefs.GetFloat(P + "hTol", 0.01f); } set { EditorPrefs.SetFloat(P + "hTol", Mathf.Max(0f, value)); } }
    /// <summary>If the two nearest surfaces are closer than this (m) in how far they are from the original Y, the point is AMBIGUOUS.</summary>
    public static float AmbiguityTolerance { get { return EditorPrefs.GetFloat(P + "ambTol", 0.5f); } set { EditorPrefs.SetFloat(P + "ambTol", Mathf.Max(0f, value)); } }
    /// <summary>Heights within this (m) are one surface (shared triangle edges).</summary>
    public static float MergeTolerance { get { return EditorPrefs.GetFloat(P + "mergeTol", 0.05f); } set { EditorPrefs.SetFloat(P + "mergeTol", Mathf.Max(0f, value)); } }
    /// <summary>A point whose original Y is within this (m) of the selected surface is ALREADY_ON_NAVMESH and is not moved.</summary>
    public static float AlreadyOnTolerance { get { return EditorPrefs.GetFloat(P + "onTol", 0.01f); } set { EditorPrefs.SetFloat(P + "onTol", Mathf.Max(0f, value)); } }
    /// <summary>Allowed vertical difference (m) between the computed height and the NavMesh.SamplePosition check. SamplePosition uses the detail mesh.</summary>
    public static float VerifyVerticalTolerance { get { return EditorPrefs.GetFloat(P + "vTol", 0.15f); } set { EditorPrefs.SetFloat(P + "vTol", Mathf.Max(0f, value)); } }
    /// <summary>Points moved further than this (m) are counted for manual review (they are still projected).</summary>
    public static float ReviewDisplacement { get { return EditorPrefs.GetFloat(P + "review", 0.5f); } set { EditorPrefs.SetFloat(P + "review", Mathf.Max(0f, value)); } }
    /// <summary>StrictSampling only: vertical scan range (m) above and below the original Y, and step (m).</summary>
    public static float SamplingRange { get { return EditorPrefs.GetFloat(P + "range", 50f); } set { EditorPrefs.SetFloat(P + "range", Mathf.Max(1f, value)); } }
    public static float SamplingStep { get { return EditorPrefs.GetFloat(P + "step", 0.25f); } set { EditorPrefs.SetFloat(P + "step", Mathf.Max(0.05f, value)); } }

    public static void Reset()
    {
        foreach (string k in new[] { "method", "hTol", "ambTol", "mergeTol", "onTol", "vTol", "review", "range", "step" }) EditorPrefs.DeleteKey(P + k);
    }
}

public class WanderingPointsSettingsWindow : EditorWindow
{
    [MenuItem("Tools/NPC Wandering Points/Settings...")]
    static void Open() { GetWindow<WanderingPointsSettingsWindow>("Wandering points settings"); }

    void OnGUI()
    {
        EditorGUILayout.HelpBox("Used by Preview and Apply. Stored in EditorPrefs (not in the scene).", MessageType.None);
        WanderingPointsSettings.Method = (WanderingPointsSettings.ProjectionMethod)EditorGUILayout.EnumPopup(new GUIContent("Projection method",
            "Triangulation: exact intersection with the NavMesh triangles (recommended). StrictSampling: fallback using NavMesh.SamplePosition probes along the vertical line."), WanderingPointsSettings.Method);
        WanderingPointsSettings.HorizontalTolerance = EditorGUILayout.FloatField(new GUIContent("Horizontal tolerance (m)",
            "Verification and StrictSampling: largest X/Z deviation of the NavMesh check from the original X/Z. Accepts a candidate; never moves a waypoint. Default 0.01."), WanderingPointsSettings.HorizontalTolerance);
        WanderingPointsSettings.AmbiguityTolerance = EditorGUILayout.FloatField(new GUIContent("Ambiguity tolerance (m)",
            "If the two surfaces nearest to the original Y differ in distance by less than this, the point is AMBIGUOUS_SURFACE and is not moved. Default 0.5."), WanderingPointsSettings.AmbiguityTolerance);
        WanderingPointsSettings.MergeTolerance = EditorGUILayout.FloatField(new GUIContent("Surface merge tolerance (m)",
            "Heights closer than this count as one surface (a point on a shared triangle edge is found in several triangles). Default 0.05."), WanderingPointsSettings.MergeTolerance);
        WanderingPointsSettings.AlreadyOnTolerance = EditorGUILayout.FloatField(new GUIContent("Already-on-NavMesh tolerance (m)",
            "Original Y within this of the selected surface -> ALREADY_ON_NAVMESH, the point is not moved. Default 0.01."), WanderingPointsSettings.AlreadyOnTolerance);
        WanderingPointsSettings.VerifyVerticalTolerance = EditorGUILayout.FloatField(new GUIContent("Verification vertical tolerance (m)",
            "Allowed height difference between the triangle-derived Y and NavMesh.SamplePosition at the final position (which uses the detail mesh). Default 0.15."), WanderingPointsSettings.VerifyVerticalTolerance);
        WanderingPointsSettings.ReviewDisplacement = EditorGUILayout.FloatField(new GUIContent("Review displacement (m)", "Moves larger than this are counted for review. Default 0.5."), WanderingPointsSettings.ReviewDisplacement);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("StrictSampling only", EditorStyles.boldLabel);
        WanderingPointsSettings.SamplingRange = EditorGUILayout.FloatField(new GUIContent("Scan range up/down (m)", "Default 50."), WanderingPointsSettings.SamplingRange);
        WanderingPointsSettings.SamplingStep = EditorGUILayout.FloatField(new GUIContent("Scan step (m)", "Default 0.25. Surfaces thinner than the step can be missed; prefer Triangulation."), WanderingPointsSettings.SamplingStep);
        EditorGUILayout.Space();
        if (GUILayout.Button("Reset to defaults")) WanderingPointsSettings.Reset();
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------
// Tool
// ---------------------------------------------------------------------------------------------------------------------------------------
public static class WanderingPointsSetupTool
{
    const string OrganicPath = "EnvironmentConfig/Npc/Wandering/Organic";
    const string GridPath = "EnvironmentConfig/Npc/Wandering/Grid";

    const string SProjected = "PROJECTED", SAlready = "ALREADY_ON_NAVMESH", SNone = "NO_VERTICAL_INTERSECTION", SAmbiguous = "AMBIGUOUS_SURFACE",
                 SInvalid = "INVALID_TRIANGLE", SVerify = "VERIFICATION_FAILED";

    class Entry
    {
        public string group, oldName, oldPath, newName = "", status = "", method = "", reason = "";
        public Transform t;
        public Vector3 original, finalPos;
        public List<WPM.Surface> surfaces = new List<WPM.Surface>();
        public int selected = -1, selectedArea = -1, degenerateNear;
        public float verifyResidual = float.NaN, verifyDy = float.NaN;
        public bool Resolved { get { return status == SProjected || status == SAlready; } }
        public float Dy { get { return finalPos.y - original.y; } }
    }

    [MenuItem("Tools/NPC Wandering Points/Preview (no changes)")]
    static void Preview() { Run(false); }

    [MenuItem("Tools/NPC Wandering Points/Apply: project, rename, assign")]
    static void Apply() { Run(true); }

    [MenuItem("Tools/NPC Wandering Points/Self-test (synthetic geometry)")]
    static void SelfTest()
    {
        string report;
        bool ok = WPM.SelfTest(out report);
        if (ok) Debug.Log("[WanderingPointsSetup] Self-test\n" + report); else Debug.LogError("[WanderingPointsSetup] Self-test\n" + report);
        EditorUtility.DisplayDialog("Wandering points - self-test", report, "OK");
    }

    static void Run(bool apply)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) { EditorUtility.DisplayDialog("Wandering points", "Leave Play Mode first.", "OK"); return; }

        Scene scene = SceneManager.GetActiveScene();
        Transform organic = FindByPath(scene, OrganicPath), grid = FindByPath(scene, GridPath);
        if (organic == null || grid == null) { Fail("Could not find '" + OrganicPath + "' and '" + GridPath + "' in the active scene '" + scene.name + "'."); return; }

        SpawnerManager spawner = Object.FindObjectOfType<SpawnerManager>(true);
        if (spawner == null || spawner.gameObject.scene != scene) { Fail("No SpawnerManager found in the active scene."); return; }
        SerializedObject so = new SerializedObject(spawner);
        SerializedProperty points = so.FindProperty("wanderingPoints");
        if (points == null || !points.isArray) { Fail("SpawnerManager has no serialized array 'wanderingPoints'."); return; }

        string err;
        List<Entry> org = Collect(organic, "Organic", out err); if (err != null) { Fail(err); return; }
        List<Entry> grd = Collect(grid, "Grid", out err); if (err != null) { Fail(err); return; }
        List<Entry> all = new List<Entry>(org); all.AddRange(grd);

        // ---- A. strict projection (computed for Preview and Apply alike) ----
        WanderingPointsSettings.ProjectionMethod method = WanderingPointsSettings.Method;
        float hTol = WanderingPointsSettings.HorizontalTolerance, ambTol = WanderingPointsSettings.AmbiguityTolerance, mergeTol = WanderingPointsSettings.MergeTolerance,
              onTol = WanderingPointsSettings.AlreadyOnTolerance, vTol = WanderingPointsSettings.VerifyVerticalTolerance, review = WanderingPointsSettings.ReviewDisplacement;
        int triangles = 0;
        try
        {
            WPM.TriangleIndex index = null;
            if (method == WanderingPointsSettings.ProjectionMethod.Triangulation)
            {
                EditorUtility.DisplayProgressBar("Wandering points", "Reading the NavMesh triangulation...", 0f);
                NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
                if (tri.vertices == null || tri.vertices.Length == 0 || tri.indices == null || tri.indices.Length < 3)
                { Fail("The loaded NavMesh has no triangulation (is the NavMesh data loaded for this scene?). Nothing was changed. You can try the StrictSampling method in Settings."); return; }
                float[] vx = new float[tri.vertices.Length], vy = new float[vx.Length], vz = new float[vx.Length];
                for (int i = 0; i < vx.Length; i++) { vx[i] = tri.vertices[i].x; vy[i] = tri.vertices[i].y; vz[i] = tri.vertices[i].z; }
                List<float> px = new List<float>(all.Count), pz = new List<float>(all.Count);
                foreach (Entry e in all) { px.Add(e.original.x); pz.Add(e.original.z); }
                EditorUtility.DisplayProgressBar("Wandering points", "Indexing " + (tri.indices.Length / 3) + " NavMesh triangles...", 0.2f);
                index = new WPM.TriangleIndex(vx, vy, vz, tri.indices, tri.areas, 20f, px, pz);
                triangles = index.TriangleCount;
            }

            for (int i = 0; i < all.Count; i++)
            {
                if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar("Wandering points", "Projecting " + (i + 1) + " / " + all.Count, 0.3f + 0.7f * i / all.Count)) { Fail("Cancelled; nothing was changed."); return; }
                Evaluate(all[i], method, index, mergeTol, ambTol, onTol, hTol, vTol);
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        int nOrg = org.Count, nGrid = grd.Count, already = 0, projected = 0, failed = 0, ambiguous = 0, large = 0, multi = 0;
        foreach (Entry e in all)
        {
            if (e.status == SAlready) already++;
            else if (e.status == SProjected) projected++;
            else if (e.status == SAmbiguous) ambiguous++;
            else failed++;
            if (e.Resolved && Mathf.Abs(e.Dy) > review) large++;
            if (e.surfaces.Count >= 2) multi++;
        }
        int resolved = projected + already, unresolved = all.Count - resolved;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Organic: " + nOrg + "   Grid: " + nGrid + "   Total: " + all.Count);
        sb.AppendLine("Method: " + method + (method == WanderingPointsSettings.ProjectionMethod.Triangulation ? " (" + triangles + " NavMesh triangles)" : "") + "   horizontal tol " + hTol.ToString("0.###", CultureInfo.InvariantCulture) + " m   ambiguity tol " + ambTol.ToString("0.###", CultureInfo.InvariantCulture) + " m");
        sb.AppendLine("Projectable: " + resolved + "  (to move: " + projected + ", already on NavMesh: " + already + ")");
        sb.AppendLine("FAILED (no intersection / invalid triangle / verification): " + failed);
        sb.AppendLine("AMBIGUOUS (several similar surfaces): " + ambiguous);
        sb.AppendLine("Large vertical move (|dY| > " + review.ToString("0.###", CultureInfo.InvariantCulture) + " m, for review): " + large);
        sb.AppendLine("Points with more than one candidate surface: " + multi);
        sb.AppendLine("SpawnerManager.wanderingPoints currently has " + points.arraySize + " entries.");
        if (unresolved > 0)
        {
            sb.AppendLine("\nUnresolved points (first 12; all are in the CSV):");
            int shown = 0;
            foreach (Entry e in all) { if (e.Resolved || shown >= 12) continue; shown++; sb.AppendLine("  " + e.status + "  " + e.oldPath + "  (" + Fs(e.original.x) + ", " + Fs(e.original.y) + ", " + Fs(e.original.z) + ")"); }
        }
        string summary = sb.ToString();

        if (!apply)
        {
            string rp = WriteReport(all, "preview");
            Debug.Log("[WanderingPointsSetup] PREVIEW (nothing changed).\n" + summary + "Report: " + rp);
            EditorUtility.DisplayDialog("Wandering points - preview", summary + "\nNothing was changed.\nReport: " + rp, "OK");
            return;
        }

        if (resolved == 0) { Fail("No waypoint could be projected, so there is nothing to apply.\n\n" + summary); return; }

        if (unresolved == 0)
        {
            if (!EditorUtility.DisplayDialog("Wandering points - apply", summary + "\nThis will (1) move each waypoint's Y onto the NavMesh, (2) rename them O1..On / G1..Gm, (3) set Wandering Points to O1..On then G1..Gm.\nOne Undo step; the scene is not saved automatically.", "Apply", "Cancel")) return;
        }
        else
        {
            if (!EditorUtility.DisplayDialog("Wandering points - PARTIAL apply",
                summary + "\n" + unresolved + " point(s) are unresolved. Applying will handle ONLY the " + resolved + " resolved points: they are projected, renamed (numbered continuously among the resolved ones) and assigned.\n" +
                "The unresolved points are not moved, not renamed and not assigned.", "Apply resolved points only", "Cancel")) return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Wandering points: project, rename, assign");
        int undoGroup = Undo.GetCurrentGroup();

        // A. move resolved points (Y only), in the parent's space so X/Z stay bit-identical; verify and roll back a point whose X/Z changed
        foreach (Entry e in all)
        {
            if (!e.Resolved || e.status == SAlready) continue;
            Transform t = e.t;
            Vector3 beforeLocal = t.localPosition;
            Vector3 dWorld = new Vector3(0f, e.finalPos.y - e.original.y, 0f);
            Vector3 dLocal = t.parent != null ? t.parent.InverseTransformVector(dWorld) : dWorld;
            Undo.RecordObject(t, "Project waypoint onto NavMesh");
            t.localPosition = beforeLocal + dLocal;
            if (t.position.x != e.original.x || t.position.z != e.original.z)
            {
                t.localPosition = beforeLocal;
                e.status = SVerify; e.reason += ";xz_changed_by_move(rolled_back)";
            }
        }

        // B. rename resolved points: one-based, continuous per group, sibling order
        int oi = 0, gi = 0;
        foreach (Entry e in all)
        {
            if (!e.Resolved) continue;
            e.newName = (e.group == "Organic" ? "O" + (++oi) : "G" + (++gi)).ToString();
            Undo.RecordObject(e.t.gameObject, "Rename waypoint");
            e.t.gameObject.name = e.newName;
        }

        // C. assign: resolved Organic in order, then resolved Grid in order
        List<Transform> assigned = new List<Transform>();
        foreach (Entry e in all) if (e.Resolved) assigned.Add(e.t);
        points.arraySize = assigned.Count;
        for (int i = 0; i < assigned.Count; i++) points.GetArrayElementAtIndex(i).objectReferenceValue = assigned[i];
        so.ApplyModifiedProperties();

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);

        SerializedObject check = new SerializedObject(spawner);
        SerializedProperty cp = check.FindProperty("wanderingPoints");
        bool ok = cp.arraySize == assigned.Count;
        HashSet<Object> seen = new HashSet<Object>();
        for (int i = 0; ok && i < cp.arraySize; i++)
        {
            Object o = cp.GetArrayElementAtIndex(i).objectReferenceValue;
            ok = o != null && o == assigned[i] && seen.Add(o);
        }

        string report = WriteReport(all, "apply");
        string result = summary + "\nAssigned " + assigned.Count + " entries (" + (ok ? "read-back OK: order, no nulls, no duplicates" : "READ-BACK MISMATCH - check the Inspector") + ").\nNot handled: " + (all.Count - assigned.Count) + ".\nReport: " + report;
        Debug.Log("[WanderingPointsSetup] APPLIED.\n" + result);

        int choice = EditorUtility.DisplayDialogComplex("Wandering points - applied", result + "\n\nSave the scene now? (It also saves any other unsaved changes in it.)", "Save scene", "Not now", "");
        if (choice == 0) EditorSceneManager.SaveScene(scene);
    }

    // ---- projection of one waypoint -------------------------------------------------------------------------------------------

    static void Evaluate(Entry e, WanderingPointsSettings.ProjectionMethod method, WPM.TriangleIndex index, float mergeTol, float ambTol, float onTol, float hTol, float vTol)
    {
        List<WPM.Surface> raw = new List<WPM.Surface>();
        if (method == WanderingPointsSettings.ProjectionMethod.Triangulation)
        {
            e.method = "triangulation";
            index.Query(e.original.x, e.original.z, raw, out e.degenerateNear);
        }
        else
        {
            e.method = "strict_sampling";
            SampleVertical(e.original, hTol, raw);
        }
        e.surfaces = WPM.Cluster(raw, mergeTol);
        e.finalPos = e.original;

        int sel; string reason;
        WPM.Outcome outcome = WPM.Select(e.surfaces, e.original.y, ambTol, onTol, out sel, out reason);
        e.selected = sel; e.reason = reason;
        if (sel >= 0) e.selectedArea = e.surfaces[sel].area;

        if (outcome == WPM.Outcome.NoIntersection) { e.status = e.degenerateNear > 0 ? SInvalid : SNone; return; }
        if (outcome == WPM.Outcome.Ambiguous) { e.status = SAmbiguous; return; }

        float targetY = outcome == WPM.Outcome.AlreadyOn ? e.original.y : e.surfaces[sel].y;
        Vector3 fp = new Vector3(e.original.x, targetY, e.original.z);

        // independent check 1: NavMesh.SamplePosition at the final X/Z
        NavMeshHit hit;
        bool found = NavMesh.SamplePosition(fp, out hit, vTol + hTol + 0.01f, NavMesh.AllAreas);
        if (found)
        {
            e.verifyResidual = new Vector2(hit.position.x - fp.x, hit.position.z - fp.z).magnitude;
            e.verifyDy = hit.position.y - fp.y;
        }
        bool navOk = found && e.verifyResidual <= hTol && Mathf.Abs(e.verifyDy) <= vTol;

        // independent check 2: the move, made in the parent's space, must leave world X/Z exactly as they are
        bool xzOk = true;
        if (outcome == WPM.Outcome.Projected)
        {
            Transform t = e.t;
            Vector3 dWorld = new Vector3(0f, targetY - e.original.y, 0f);
            Vector3 dLocal = t.parent != null ? t.parent.InverseTransformVector(dWorld) : dWorld;
            Vector3 w0 = t.parent != null ? t.parent.TransformPoint(t.localPosition) : t.localPosition;
            Vector3 w1 = t.parent != null ? t.parent.TransformPoint(t.localPosition + dLocal) : t.localPosition + dLocal;
            xzOk = w1.x == w0.x && w1.z == w0.z;
        }

        if (!navOk || !xzOk) { e.status = SVerify; e.reason += !navOk ? ";navmesh_check_failed" : ";xz_not_exact_after_move"; return; }
        e.finalPos = fp;
        e.status = outcome == WPM.Outcome.AlreadyOn ? SAlready : SProjected;
    }

    // Fallback: probes along the vertical line; a probe counts only if the NavMesh point it returns is within the horizontal tolerance of the original X/Z.
    static void SampleVertical(Vector3 p, float hTol, List<WPM.Surface> into)
    {
        float range = WanderingPointsSettings.SamplingRange, step = WanderingPointsSettings.SamplingStep;
        float radius = step * 0.5f + hTol;
        for (float y = p.y + range; y >= p.y - range; y -= step)
        {
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(new Vector3(p.x, y, p.z), out hit, radius, NavMesh.AllAreas)) continue;
            if (new Vector2(hit.position.x - p.x, hit.position.z - p.z).magnitude > hTol) continue;
            into.Add(new WPM.Surface { y = hit.position.y, area = 0, hits = 1 });
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    static List<Entry> Collect(Transform parent, string group, out string error)
    {
        error = null;
        var list = new List<Entry>();
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform c = parent.GetChild(i);
            if (c.childCount > 0) { error = "Waypoint '" + PathOf(c) + "' has child objects. Nested containers are not guessed: tell me how they should be handled."; return null; }
            list.Add(new Entry { group = group, t = c, oldName = c.name, oldPath = PathOf(c), original = c.position });
        }
        return list;
    }

    static Transform FindByPath(Scene scene, string path)
    {
        int slash = path.IndexOf('/');
        string first = path.Substring(0, slash), rest = path.Substring(slash + 1);
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name != first) continue;
            Transform t = root.transform.Find(rest);   // finds inactive children too
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
        string file = Path.Combine(dir, "WanderingPointsSetupReport_" + kind + ".csv");
        var sb = new StringBuilder("group,original_name,original_hierarchy_path,original_x,original_y,original_z,projected_x,projected_y,projected_z," +
                                   "vertical_displacement,horizontal_residual,candidate_surface_count,candidate_surface_heights,selected_surface_height,projection_method,status,new_name," +
                                   "selection_reason,selected_area,navmesh_check_residual_xz,navmesh_check_dy\n");
        foreach (Entry e in all)
        {
            Vector3 f = kind == "apply" ? e.t.position : e.finalPos;
            float hres = new Vector2(f.x - e.original.x, f.z - e.original.z).magnitude;
            var heights = new StringBuilder();
            for (int i = 0; i < e.surfaces.Count; i++) { if (i > 0) heights.Append(';'); heights.Append(Fs(e.surfaces[i].y)); }
            sb.Append(e.group).Append(',').Append(Csv(e.oldName)).Append(',').Append(Csv(e.oldPath)).Append(',')
              .Append(Fs(e.original.x)).Append(',').Append(Fs(e.original.y)).Append(',').Append(Fs(e.original.z)).Append(',')
              .Append(Fs(f.x)).Append(',').Append(Fs(f.y)).Append(',').Append(Fs(f.z)).Append(',')
              .Append(Fs(f.y - e.original.y)).Append(',').Append(hres.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.surfaces.Count).Append(',').Append(heights).Append(',')
              .Append(e.selected >= 0 ? Fs(e.surfaces[e.selected].y) : "").Append(',').Append(e.method).Append(',').Append(e.status).Append(',').Append(e.newName).Append(',')
              .Append(Csv(e.reason)).Append(',').Append(e.selectedArea >= 0 ? e.selectedArea.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(float.IsNaN(e.verifyResidual) ? "" : e.verifyResidual.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
              .Append(float.IsNaN(e.verifyDy) ? "" : Fs(e.verifyDy)).Append('\n');
        }
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    static string Csv(string s) { return (s ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' '); }
    static string Fs(float v) { return v.ToString("F4", CultureInfo.InvariantCulture); }

    static void Fail(string message)
    {
        Debug.LogError("[WanderingPointsSetup] " + message);
        EditorUtility.DisplayDialog("Wandering points", message, "OK");
    }
}
