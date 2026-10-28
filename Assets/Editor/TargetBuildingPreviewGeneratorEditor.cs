// TargetBuildingPreviewGeneratorEditor.cs
//
// Inspector tooling for the per-target pedestrian viewpoints of TargetBuildingPreviewGenerator (Part 3 shrunk-panel images).
// Editor only; nothing here runs in a build or in the experiment.
//
// EDITOR EXECUTION MODEL (why the buttons never touch the scene inside OnInspectorGUI)
//   A button click is delivered inside the IMGUI pass of the Inspector. Creating GameObjects, editing arrays, Undo and dirty-marking
//   there can rebuild or destroy the Inspector while its OnGUI is still running, which is how Unity ends up reporting
//   "the GUIStateObj is deleted, but is accessed" on every following repaint. Therefore a click only SCHEDULES the operation:
//   Schedule() stores nothing but the generator's instance id, queues one EditorApplication.delayCall and calls GUIUtility.ExitGUI()
//   to leave the current GUI pass immediately. The operation then runs outside any GUI pass, as one Undo group, guarded by a
//   single static 'busy' flag (one click = one operation; clicks while busy are ignored and the buttons are disabled). Exceptions
//   are caught and reported once in the status box. The last report lives in SessionState, so it survives Inspector rebuilds and
//   script recompilation. Serialized configuration is edited through SerializedObject (undo-aware, marks the scene dirty).
//
// INCREMENTAL WORKFLOW (existing viewpoints are never overwritten)
//   A target "has a viewpoint" when a 'targetViewpoints' entry with its stable target id (TargetSelectable.ResolvedTargetId) references
//   an existing Transform (ViewpointRecords.Classify). The Transform's pose is never inspected, so manual fine-tuning needs no
//   protection: Add and Suggest simply never touch a target that already has a viewpoint, however good or bad its framing is.
//   After every operation the tool re-reads all existing viewpoints and reports "Integrity: N existing viewpoints verified unchanged"
//   (position, rotation, scale, parent, object identity), or an error naming any that changed.
//
// OPERATIONS
//   Add                    creates a 'targetViewpoints' entry ONLY for candidates without any record. Never creates viewpoint objects
//                          (a placeholder pose would count as "existing" and shield it from Suggest). If a single 'Viewpoint_<id>' object
//                          already exists without a record, the entry is linked to it (the object itself is not modified). Inconsistent
//                          targets (destroyed object, several same-named objects, duplicate entries) are reported for review and skipped.
//   Suggest                runs Add first, then generates a viewpoint ONLY for targets that are new (no entry/reference and no object): it searches an
//                          eye-level position from which the WHOLE building fits the isolated preview frame (see below) and creates a NEW
//                          'Viewpoint_<id>' under the 'Part 3 Target Viewpoints' scene root. It never reuses, repositions or recreates an
//                          existing object. Targets that do not reach the acceptance rule get NO viewpoint ('needs manual placement').
//   Check                  read-only: evaluates the stored pose of each configured viewpoint and lists inconsistent records.
//   Move to Scene view     MANUAL tool: copies the Scene view camera pose onto exactly that one viewpoint Transform (Undo-able). Nothing else
//                          changes, and later Add/Suggest runs leave the moved viewpoint alone because it still has a record.
//   Export                 read-only for the scene: renders every candidate with the legacy angle and with its configured viewpoint (the real
//                          isolated capture path) to <project>/Temp/TargetPreviews/<id>_legacy.png / <id>_viewpoint.png plus contact_sheet.png.
//                          The capture camera is the stored viewpoint pose (position + heading + pitch; only roll is dropped); the report lists
//                          the stored pose and any roll that is dropped.
//
// SUGGESTION MODEL
//   The preview image contains ONLY the target building (isolation layer, flat background, private light), so surrounding city
//   geometry cannot appear in it and is NOT used to reject a viewpoint. Hard requirements (all must hold):
//     - the eye stands 'suggestEyeHeight' above a walking surface (NavMesh point within the snap radius, else a ground raycast; the
//       building's base height only if 'suggestAllowAssumedGround'), outside the building;
//     - EVERY corner of the building's oriented mesh bounds projects inside the frame with 'suggestFramingMargin' to spare, using
//       the same pinhole model as the capture camera (ViewpointFraming), with pitch limited to 'suggestMaxPitchUp/Down';
//     - the silhouette fills at least 'suggestMinFill' of the frame, and the longest facade is at least 'suggestMinFacade' square-on.
//   The camera moves outward from the building until the whole building fits, so large buildings simply get a longer distance
//   (up to 'suggestMaxDistance'); the eye height is never lowered to make a building fit.
//   Score (0..1) = 0.4 fill + 0.4 facade + 0.2 access, accepted if >= 'suggestMinScore':
//     fill    silhouette extent / (1 - margin): how well the image is used (geometry only);
//     facade  how squarely the longest footprint facade faces the camera (geometry only);
//     access  share of the building's camera-facing bounding-box sample points visible from the eye through the real city
//             colliders. This is the ONLY term that looks at surroundings. It does not appear in the preview image and never rejects
//             a candidate; it only prefers directions a pedestrian could actually see the building from.
//   Limitations: the silhouette uses oriented mesh-bounds corners (conservative for concave buildings); the facade term uses the
//   footprint's convex hull; the score weights and thresholds are provisional - tune them against the exported PNGs.
//
// All comments in English per project convention.

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(TargetBuildingPreviewGenerator))]
public class TargetBuildingPreviewGeneratorEditor : Editor
{
    private const string ContainerName = "Part 3 Target Viewpoints";
    private const string ObjectPrefix = "Viewpoint_";
    private const string StatusKey = "TargetBuildingPreviewGeneratorEditor.Status";
    private const int DirectionCount = 24;

    // One operation at a time. Static so it also covers a second Inspector instance; reset by a domain reload.
    private static bool busy;
    private static int missingCache = -1;

    // ---------------------------------------------------------------------------------------------------------------
    // Inspector (drawing only: no scene or serialized changes happen in here)
    // ---------------------------------------------------------------------------------------------------------------

    private void OnEnable()
    {
        missingCache = CountMissing((TargetBuildingPreviewGenerator)target);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gen = (TargetBuildingPreviewGenerator)target;
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Viewpoint tools", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Add and Suggest only ever work on targets that have NO viewpoint yet; an existing viewpoint is never moved, " +
                                "recreated or reset, whether or not you edited it. Check and Export do not change the scene.", MessageType.None);

        using (new EditorGUI.DisabledScope(busy))
        {
            if (GUILayout.Button("Add entries for Part 3 candidate buildings"))
                Schedule("Add entries", OpAddEntries);

            if (GUILayout.Button("Suggest viewpoints for entries without one (review the result!)"))
                Schedule("Suggest viewpoints", OpSuggest);

            if (GUILayout.Button("Check configured viewpoints (whole building in frame?)"))
                Schedule("Check viewpoints", OpCheck);

            if (gen.targetViewpoints != null)
            {
                bool any = false;
                for (int i = 0; i < gen.targetViewpoints.Length; i++)
                {
                    TargetViewpoint e = gen.targetViewpoints[i];
                    if (e == null || e.viewpoint == null) continue;
                    if (!any)
                    {
                        EditorGUILayout.HelpBox("Manual tools: each button below copies the current Scene view camera pose onto THAT ONE viewpoint (position and rotation). " +
                                                "No other viewpoint or record changes. Undo with Edit > Undo.", MessageType.None);
                        any = true;
                    }
                    string id = e.targetId;
                    int tid = e.viewpoint.GetInstanceID();
                    if (GUILayout.Button($"Move viewpoint '{id}' to Scene view camera"))
                        Schedule("Move viewpoint", g => OpMoveToSceneView(g, tid, id), tid);
                }
            }

            if (GUILayout.Button("Export before/after PNGs to Temp/TargetPreviews"))
                Schedule("Export PNGs", OpExport);
        }

        if (missingCache > 0)
            EditorGUILayout.HelpBox($"{missingCache} Part 3 candidate building(s) have no viewpoint and will use the legacy shared angle at runtime.", MessageType.Warning);

        string status = SessionState.GetString(StatusKey, "");
        if (busy) EditorGUILayout.HelpBox("Working...", MessageType.None);
        else if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);
    }

    // Queue ONE operation outside the GUI pass and leave the pass immediately. 'allowChangeTransformId' = the one viewpoint Transform
    // this operation is ALLOWED to change (Move); every other existing viewpoint must come out of the operation identical.
    private void Schedule(string label, System.Action<TargetBuildingPreviewGenerator> op, int allowChangeTransformId = 0)
    {
        if (busy) return;
        busy = true;
        int id = target.GetInstanceID();
        EditorApplication.delayCall += () => Run(id, label, op, allowChangeTransformId);
        GUIUtility.ExitGUI();
    }

    private static void Run(int generatorId, string label, System.Action<TargetBuildingPreviewGenerator> op, int allowChangeTransformId)
    {
        try
        {
            var gen = EditorUtility.InstanceIDToObject(generatorId) as TargetBuildingPreviewGenerator;
            if (gen == null)
            {
                SetStatus($"{label}: the TargetBuildingPreviewGenerator no longer exists (selection or scene changed). Nothing was changed.");
                return;
            }

            List<PoseRecord> before = SnapshotViewpoints(gen);
            op(gen);
            string integrity = VerifyViewpoints(gen, before, allowChangeTransformId);
            SetStatus(SessionState.GetString(StatusKey, "") + "\n" + integrity);
            if (integrity.StartsWith("INTEGRITY ERROR")) Debug.LogError("[TargetBuildingPreviewGeneratorEditor] " + integrity);
            missingCache = CountMissing(gen);
        }
        catch (System.Exception e)
        {
            SetStatus($"{label} FAILED: {e.GetType().Name}: {e.Message}\nThe operation stopped; partial changes can be undone with Edit > Undo.");
            Debug.LogError($"[TargetBuildingPreviewGeneratorEditor] {label} failed: {e}");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            busy = false;
            InternalEditorUtility.RepaintAllViews();
        }
    }

    private static void SetStatus(string text)
    {
        SessionState.SetString(StatusKey, text);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Integrity guard: every existing viewpoint must come out of an operation exactly as it went in
    // ---------------------------------------------------------------------------------------------------------------

    private struct PoseRecord
    {
        public string id;
        public int transformId;
        public Vector3 position, scale;
        public Quaternion rotation;
        public int parentId;
        public string name;
    }

    private static List<PoseRecord> SnapshotViewpoints(TargetBuildingPreviewGenerator gen)
    {
        var list = new List<PoseRecord>();
        if (gen.targetViewpoints == null) return list;
        foreach (TargetViewpoint e in gen.targetViewpoints)
        {
            if (e == null || e.viewpoint == null) continue;
            Transform t = e.viewpoint;
            list.Add(new PoseRecord
            {
                id = e.targetId, transformId = t.GetInstanceID(), position = t.position, rotation = t.rotation,
                scale = t.localScale, parentId = t.parent != null ? t.parent.GetInstanceID() : 0, name = t.name
            });
        }
        return list;
    }

    private static string VerifyViewpoints(TargetBuildingPreviewGenerator gen, List<PoseRecord> before, int allowedTransformId)
    {
        var errors = new StringBuilder();
        int verified = 0;
        foreach (PoseRecord r in before)
        {
            if (r.transformId == allowedTransformId) continue;
            var t = EditorUtility.InstanceIDToObject(r.transformId) as Transform;
            if (t == null) { errors.AppendLine($"  {r.id}: viewpoint object '{r.name}' no longer exists"); continue; }

            bool same = t.position == r.position && t.rotation == r.rotation && t.localScale == r.scale &&
                        (t.parent != null ? t.parent.GetInstanceID() : 0) == r.parentId && t.name == r.name;
            // The entry must still reference the same object.
            TargetViewpoint e = System.Array.Find(gen.targetViewpoints, x => x != null && x.targetId == r.id && x.viewpoint != null);
            bool linked = e != null && e.viewpoint.GetInstanceID() == r.transformId;
            if (!same || !linked) errors.AppendLine($"  {r.id}: {(!same ? "pose/hierarchy changed" : "")}{(!linked ? " entry no longer references the same object" : "")}");
            else verified++;
        }
        return errors.Length == 0
            ? $"Integrity: {verified} existing viewpoint(s) verified unchanged (object, position, rotation, scale, parent, entry link)."
            : "INTEGRITY ERROR - an existing viewpoint changed:\n" + errors.ToString().TrimEnd() + "\nUse Edit > Undo to restore it.";
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Candidates and record classification
    // ---------------------------------------------------------------------------------------------------------------

    private struct Candidate
    {
        public string label;        // e.g. "GE/A"
        public GameObject go;       // null = reference missing in ExperimentManager
        public string id;           // stable target id, null if unresolved
    }

    private static List<Candidate> Part3Candidates()
    {
        var list = new List<Candidate>();
        ExperimentManager em = FindObjectOfType<ExperimentManager>();
        if (em == null) return list;

        void Add(string zone, ExperimentManager.Part3ZoneTargets z)
        {
            if (z == null) return;
            list.Add(Make(zone + "/A", z.buildingA));
            list.Add(Make(zone + "/B", z.buildingB));
        }
        Add("GE", em.part3ZoneGE); Add("GU", em.part3ZoneGU); Add("OE", em.part3ZoneOE); Add("OU", em.part3ZoneOU);
        return list;
    }

    private static Candidate Make(string label, GameObject go)
    {
        return new Candidate { label = label, go = go, id = go != null ? TargetBuildingPreviewGenerator.ResolveTargetId(go) : null };
    }

    private static int CountMissing(TargetBuildingPreviewGenerator gen)
    {
        int n = 0;
        foreach (Candidate c in Part3Candidates())
            if (c.go != null && gen.FindViewpoint(c.go) == null) n++;
        return n;
    }

    private static ViewpointRecords.RefState RefStateOf(SerializedProperty viewpoint)
    {
        if (viewpoint.objectReferenceValue != null) return ViewpointRecords.RefState.Valid;
        return viewpoint.objectReferenceInstanceIDValue != 0 ? ViewpointRecords.RefState.Dangling : ViewpointRecords.RefState.Empty;
    }

    private static List<ViewpointRecords.EntryInfo> ReadEntries(SerializedObject so)
    {
        var list = new List<ViewpointRecords.EntryInfo>();
        SerializedProperty arr = so.FindProperty("targetViewpoints");
        for (int i = 0; i < arr.arraySize; i++)
        {
            SerializedProperty e = arr.GetArrayElementAtIndex(i);
            list.Add(new ViewpointRecords.EntryInfo
            {
                id = e.FindPropertyRelative("targetId").stringValue,
                reference = RefStateOf(e.FindPropertyRelative("viewpoint"))
            });
        }
        return list;
    }

    // 'Viewpoint_<id>' objects in this scene: everything under the container root(s) plus root-level objects with that prefix.
    private static Dictionary<string, List<Transform>> BuildObjectIndex(Scene scene)
    {
        var idx = new Dictionary<string, List<Transform>>();
        void AddT(Transform t)
        {
            if (!t.name.StartsWith(ObjectPrefix)) return;
            if (!idx.TryGetValue(t.name, out List<Transform> l)) idx[t.name] = l = new List<Transform>();
            l.Add(t);
        }
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == ContainerName)
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    if (t != root.transform) AddT(t);
            }
            else if (root.name.StartsWith(ObjectPrefix)) AddT(root.transform);
        }
        return idx;
    }

    private static int ObjectCount(Dictionary<string, List<Transform>> idx, string id)
    {
        return idx.TryGetValue(ObjectPrefix + id, out List<Transform> l) ? l.Count : 0;
    }

    private struct TargetPlan
    {
        public Candidate candidate;
        public ViewpointRecords.State state;
        public bool duplicateEntryWarning;
    }

    private static List<TargetPlan> PlanTargets(TargetBuildingPreviewGenerator gen, SerializedObject so, out Dictionary<string, List<Transform>> objects)
    {
        so.Update();
        List<ViewpointRecords.EntryInfo> entries = ReadEntries(so);
        objects = BuildObjectIndex(gen.gameObject.scene);
        var plans = new List<TargetPlan>();
        var seen = new HashSet<string>();
        foreach (Candidate c in Part3Candidates())
        {
            var plan = new TargetPlan { candidate = c };
            if (c.go == null || string.IsNullOrEmpty(c.id)) plan.state = ViewpointRecords.State.Unresolved;
            else if (!seen.Add(c.id)) { plan.state = ViewpointRecords.State.Unresolved; }   // same id twice among the candidates
            else plan.state = ViewpointRecords.Classify(c.id, entries, ObjectCount(objects, c.id), out plan.duplicateEntryWarning);
            plans.Add(plan);
        }
        return plans;
    }

    private static string StateText(TargetPlan p)
    {
        string id = p.candidate.id ?? "?"; string l = p.candidate.label;
        switch (p.state)
        {
            case ViewpointRecords.State.Unresolved: return $"[REVIEW] {id} ({l}): building reference or target id cannot be resolved (or the id appears twice among the candidates).";
            case ViewpointRecords.State.DanglingReference: return $"[REVIEW] {id} ({l}): the entry exists but its viewpoint object was destroyed. Nothing was recreated; assign or restore the object yourself.";
            case ViewpointRecords.State.AmbiguousObjects: return $"[REVIEW] {id} ({l}): several 'Viewpoint_{id}' objects exist; link the right one in the entry yourself.";
            case ViewpointRecords.State.DuplicateEntries: return $"[REVIEW] {id} ({l}): several entries for this id and none references a viewpoint; delete the extras yourself.";
            default: return null;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Operations (never called from inside OnInspectorGUI)
    // ---------------------------------------------------------------------------------------------------------------

    private struct AddResult { public int createdEntries, linked; }

    // The Add rules. Touches only targets whose state is NewTarget (create the missing entry) or ObjectWithoutRecord (create the missing
    // entry / fill its EMPTY reference with the single existing object). Existing entries and objects are never modified.
    private static AddResult ApplyAdd(TargetBuildingPreviewGenerator gen, SerializedObject so, List<TargetPlan> plans, Dictionary<string, List<Transform>> objects)
    {
        var res = new AddResult();
        SerializedProperty arr = so.FindProperty("targetViewpoints");
        var existingIds = new HashSet<string>();
        for (int i = 0; i < arr.arraySize; i++) existingIds.Add(arr.GetArrayElementAtIndex(i).FindPropertyRelative("targetId").stringValue);

        bool changed = false;
        foreach (TargetPlan p in plans)
        {
            string id = p.candidate.id;
            if (p.state != ViewpointRecords.State.NewTarget && p.state != ViewpointRecords.State.ObjectWithoutRecord) continue;

            SerializedProperty entry = null;
            if (existingIds.Contains(id)) entry = FindEntry(so, id);
            else
            {
                int idx = arr.arraySize;
                arr.InsertArrayElementAtIndex(idx);
                entry = arr.GetArrayElementAtIndex(idx);
                entry.FindPropertyRelative("targetId").stringValue = id;
                entry.FindPropertyRelative("viewpoint").objectReferenceValue = null;
                entry.FindPropertyRelative("fieldOfView").floatValue = 0f;
                existingIds.Add(id);
                res.createdEntries++;
                changed = true;
            }

            if (p.state == ViewpointRecords.State.ObjectWithoutRecord && entry != null
                && entry.FindPropertyRelative("viewpoint").objectReferenceValue == null)
            {
                entry.FindPropertyRelative("viewpoint").objectReferenceValue = objects[ObjectPrefix + id][0];
                res.linked++;
                changed = true;
            }
        }
        if (changed) so.ApplyModifiedProperties();      // no change -> nothing is written, the scene is not dirtied
        return res;
    }

    private static void OpAddEntries(TargetBuildingPreviewGenerator gen)
    {
        var so = new SerializedObject(gen);
        List<TargetPlan> plans = PlanTargets(gen, so, out var objects);
        AddResult r = ApplyAdd(gen, so, plans, objects);

        int existing = 0, review = 0;
        var sb = new StringBuilder();
        foreach (TargetPlan p in plans)
        {
            if (p.state == ViewpointRecords.State.Existing) existing++;
            string t = StateText(p);
            if (t != null) { review++; sb.AppendLine(t); }
            if (p.duplicateEntryWarning) sb.AppendLine($"[WARNING] {p.candidate.id}: more than one entry for this id (the first valid one is used).");
        }

        string head = $"Add: {plans.Count} candidate(s) discovered; {existing} already have a viewpoint (skipped); {r.createdEntries} entr{(r.createdEntries == 1 ? "y" : "ies")} created" +
                      $"{(r.linked > 0 ? $", {r.linked} linked to an existing object" : "")}; {review} need review.";
        if (r.createdEntries == 0 && r.linked == 0) head += " Nothing to add - no scene change was made.";
        SetStatus(head + (sb.Length > 0 ? "\n" + sb.ToString().TrimEnd() : ""));
    }

    private static void OpSuggest(TargetBuildingPreviewGenerator gen)
    {
        var so = new SerializedObject(gen);
        List<TargetPlan> plans = PlanTargets(gen, so, out var objects);
        AddResult add = ApplyAdd(gen, so, plans, objects);

        var report = new StringBuilder();
        int existing = 0, review = 0, accepted = 0, manual = 0, failed = 0, linked = add.linked;
        int newTargets = 0;
        foreach (TargetPlan p in plans) if (p.state == ViewpointRecords.State.NewTarget) newTargets++;

        Physics.SyncTransforms();
        int undoGroup = -1;

        for (int i = 0; i < plans.Count; i++)
        {
            TargetPlan p = plans[i];
            Candidate c = p.candidate;

            if (p.state == ViewpointRecords.State.Existing)
            {
                existing++;
                if (p.duplicateEntryWarning) report.AppendLine($"[WARNING] {c.id}: more than one entry for this id (the first valid one is used).");
                continue;
            }
            if (p.state == ViewpointRecords.State.ObjectWithoutRecord)
            {
                report.AppendLine($"[LINKED] {c.id} ({c.label}): an existing 'Viewpoint_{c.id}' object had no record; the entry now references it (object untouched).");
                continue;
            }
            if (p.state != ViewpointRecords.State.NewTarget)
            {
                review++;
                report.AppendLine(StateText(p));
                continue;
            }

            EditorUtility.DisplayProgressBar("Suggest viewpoints", $"{c.label} {c.id}", i / (float)plans.Count);

            so.Update();
            SerializedProperty entry = FindEntry(so, c.id);
            if (entry == null) { failed++; report.AppendLine($"[FAILED] {c.id} ({c.label}): no entry could be created."); continue; }
            if (entry.FindPropertyRelative("viewpoint").objectReferenceValue != null || RefStateOf(entry.FindPropertyRelative("viewpoint")) != ViewpointRecords.RefState.Empty)
            {
                existing++;   // defensive: it acquired a viewpoint in the meantime
                continue;
            }

            float entryFov = entry.FindPropertyRelative("fieldOfView").floatValue;
            float fov = entryFov > 0f ? entryFov : gen.viewpointFieldOfView;
            SuggestResult r = Suggest(gen, c.go, fov);
            report.AppendLine(r.Describe(c.id, c.label, fov));

            if (r.status == Status.Accepted)
            {
                if (undoGroup < 0)
                {
                    Undo.IncrementCurrentGroup();
                    Undo.SetCurrentGroupName("Suggest target viewpoints");
                    undoGroup = Undo.GetCurrentGroup();
                }

                Transform vp = CreateViewpointObject(gen, c.id);   // always a NEW object; null if a same-named one appeared
                if (vp == null) { failed++; report.AppendLine($"[REVIEW] {c.id}: a 'Viewpoint_{c.id}' object already exists; nothing was written."); continue; }
                vp.SetPositionAndRotation(r.eye, Quaternion.LookRotation(ViewpointFraming.ForwardFrom(r.yaw, r.pitch), Vector3.up));
                entry.FindPropertyRelative("viewpoint").objectReferenceValue = vp;
                so.ApplyModifiedProperties();
                accepted++;
            }
            else if (r.status == Status.NeedsManual) manual++;
            else failed++;
        }

        if (undoGroup >= 0)
        {
            EditorSceneManager.MarkSceneDirty(gen.gameObject.scene);
            Undo.CollapseUndoOperations(undoGroup);
        }

        string head = $"Suggest: {plans.Count} candidate(s) discovered; {existing} with an existing viewpoint skipped; {newTargets} new; {accepted} viewpoint(s) created; " +
                      $"{manual} need manual placement; {review} inconsistent skipped for review; {failed} failed" +
                      $"{(add.createdEntries > 0 ? $"; {add.createdEntries} entr{(add.createdEntries == 1 ? "y" : "ies")} added" : "")}{(linked > 0 ? $"; {linked} linked" : "")}.";
        if (accepted == 0 && add.createdEntries == 0 && linked == 0) head += " No viewpoint-related scene change was made.";
        head += $"  (min score {gen.suggestMinScore:F2}, min fill {gen.suggestMinFill:F2}, min facade {gen.suggestMinFacade:F2}, margin {gen.suggestFramingMargin:F2})";
        string full = head + (report.Length > 0 ? "\n" + report.ToString().TrimEnd() : "");
        SetStatus(full);
        Debug.Log("[TargetBuildingPreviewGeneratorEditor] " + full);
    }

    private static void OpCheck(TargetBuildingPreviewGenerator gen)
    {
        // Read-only: reads transforms, raycasts and the NavMesh; writes only the status text (SessionState).
        Physics.SyncTransforms();
        var report = new StringBuilder();
        int ok = 0, bad = 0, none = 0;

        var so = new SerializedObject(gen);
        List<TargetPlan> plans = PlanTargets(gen, so, out _);
        foreach (TargetPlan pl in plans)
        {
            string t = StateText(pl);
            if (t != null) { bad++; report.AppendLine(t); }
            else if (pl.state == ViewpointRecords.State.ObjectWithoutRecord)
                report.AppendLine($"[REVIEW] {pl.candidate.id} ({pl.candidate.label}): a 'Viewpoint_{pl.candidate.id}' object exists but no entry references it (Add will link it).");
        }

        foreach (Candidate c in Part3Candidates())
        {
            if (c.go == null || string.IsNullOrEmpty(c.id)) continue;
            TargetViewpoint vp = gen.FindViewpoint(c.go);
            if (vp == null) { none++; report.AppendLine($"[NO VIEWPOINT] {c.id} ({c.label}): legacy shared angle will be used."); continue; }

            List<Vector3> corners = TargetBuildingPreviewGenerator.CollectBuildingCorners(c.go);
            if (corners.Count == 0) { bad++; report.AppendLine($"[INVALID] {c.id} ({c.label}): building has no active renderer."); continue; }

            float fov = vp.fieldOfView > 0f ? vp.fieldOfView : gen.viewpointFieldOfView;
            Vector3 eye = vp.viewpoint.position;
            Vector3 fwd = vp.viewpoint.forward;
            ViewpointFraming.ProjectionResult r = ViewpointFraming.Project(corners, eye, fwd, fov, 0f, 0.3f);
            Vector3 centre = BoundsOf(corners).center;
            Vector3 toCam = new Vector3(eye.x - centre.x, 0f, eye.z - centre.z);
            float facade = ViewpointFraming.FacadeScore(corners, toCam);
            float height = GroundHeightBelow(eye, c.go.transform);

            string cam = $"dist {toCam.magnitude:F1} m, eye {(height >= 0f ? height.ToString("F1") + " m above ground" : "ground unknown")}, fov {fov:F0}, fill {r.Fill:F2}, facade {facade:F2}";
            if (r.Fits) { ok++; report.AppendLine($"[OK] {c.id} ({c.label}): whole building in frame; {cam}"); }
            else { bad++; report.AppendLine($"[CROPPED] {c.id} ({c.label}): {(r.allInFront ? "building is cut off by the frame" : "part of the building is behind the camera")}; {cam}"); }
        }

        string full = $"Check (read-only): {ok} ok, {bad} problem(s), {none} without viewpoint. Nothing was changed.\n" + report.ToString().TrimEnd();
        SetStatus(full);
        Debug.Log("[TargetBuildingPreviewGeneratorEditor] " + full);
    }

    // MANUAL tool: copies the Scene view camera pose onto exactly one viewpoint Transform (identified by instance id, not by list order).
    private static void OpMoveToSceneView(TargetBuildingPreviewGenerator gen, int transformInstanceId, string targetId)
    {
        SceneView sv = SceneView.lastActiveSceneView;
        var t = EditorUtility.InstanceIDToObject(transformInstanceId) as Transform;
        if (sv == null || t == null)
        {
            SetStatus($"Move viewpoint '{targetId}': no Scene view or the viewpoint object no longer exists. Nothing was changed.");
            return;
        }
        Undo.RecordObject(t, "Move viewpoint to Scene view");
        t.SetPositionAndRotation(sv.camera.transform.position, sv.camera.transform.rotation);
        EditorUtility.SetDirty(t);
        EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
        SetStatus($"Viewpoint '{t.name}' (target '{targetId}') now has the Scene view camera pose. No other viewpoint or record was changed. Run 'Check' to confirm the whole building is in frame.");
    }

    private static void OpExport(TargetBuildingPreviewGenerator gen)
    {
        string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "TargetPreviews");
        Directory.CreateDirectory(dir);

        const int tile = 256;
        List<Candidate> candidates = Part3Candidates();
        var report = new StringBuilder();
        int written = 0, failed = 0, missing = 0;
        var sheetTiles = new List<Texture2D[]>();

        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate c = candidates[i];
            EditorUtility.DisplayProgressBar("Export previews", $"{c.label} {c.id}", i / (float)candidates.Count);
            var pair = new Texture2D[2];

            if (c.go == null || string.IsNullOrEmpty(c.id))
            {
                failed++; report.AppendLine($"[INVALID] {c.label}: building reference or target id cannot be resolved.");
                sheetTiles.Add(pair); continue;
            }

            for (int mode = 0; mode < 2; mode++)
            {
                bool configured = mode == 1;
                string suffix = configured ? "viewpoint" : "legacy";
                TargetViewpoint stored = configured ? gen.FindViewpoint(c.go) : null;
                if (configured && stored == null)
                {
                    missing++; report.AppendLine($"[MISSING VIEWPOINT] {c.id} ({c.label}): no {suffix} image exported.");
                    continue;
                }

                Texture2D tex = null;
                try
                {
                    tex = gen.CapturePreview(c.go, configured);
                    if (tex == null) { failed++; report.AppendLine($"[FAILED] {c.id} {suffix}: capture returned no image (building inactive or without renderers?)."); continue; }

                    string path = Path.Combine(dir, $"{SafeName(c.id)}_{suffix}.png");
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    var fi = new FileInfo(path);
                    if (!fi.Exists || fi.Length == 0) { failed++; report.AppendLine($"[FAILED] {c.id} {suffix}: file was not written."); continue; }

                    written++;
                    pair[mode] = Downscale(tex, tile);

                    if (configured)
                    {
                        // The capture camera uses the stored Transform's position and its heading/pitch with roll removed (the pre-existing convention).
                        Transform vt = stored.viewpoint;
                        float rollDropped = Quaternion.Angle(vt.rotation, Quaternion.LookRotation(vt.forward, Vector3.up));
                        report.AppendLine($"[POSE] {c.id}: stored position {vt.position:F2}, euler {vt.rotation.eulerAngles:F1}; capture camera = same position and heading/pitch" +
                                          (rollDropped > 0.5f ? $", roll of {rollDropped:F1} deg not applied" : ", no roll to drop") + ".");
                    }
                }
                catch (System.Exception ex)
                {
                    failed++; report.AppendLine($"[FAILED] {c.id} {suffix}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (tex != null) Object.DestroyImmediate(tex);
                }
            }
            sheetTiles.Add(pair);
        }

        // Contact sheet: one row per candidate, left = legacy angle, right = configured viewpoint (grey = not available).
        string sheetNote = "";
        try
        {
            if (sheetTiles.Count > 0)
            {
                var sheet = new Texture2D(tile * 2, tile * sheetTiles.Count, TextureFormat.RGBA32, false, false);
                var grey = new Color[tile * 2 * tile * sheetTiles.Count];
                for (int k = 0; k < grey.Length; k++) grey[k] = new Color(0.35f, 0.35f, 0.35f, 1f);
                sheet.SetPixels(grey);
                for (int row = 0; row < sheetTiles.Count; row++)
                    for (int col = 0; col < 2; col++)
                        if (sheetTiles[row][col] != null)
                            sheet.SetPixels(col * tile, (sheetTiles.Count - 1 - row) * tile, tile, tile, sheetTiles[row][col].GetPixels());
                sheet.Apply(false, false);
                string sheetPath = Path.Combine(dir, "contact_sheet.png");
                File.WriteAllBytes(sheetPath, sheet.EncodeToPNG());
                Object.DestroyImmediate(sheet);
                sheetNote = new FileInfo(sheetPath).Length > 0 ? "\nContact sheet: contact_sheet.png (row order = candidate order below, left legacy, right viewpoint)." : "\n[FAILED] contact sheet was not written.";
            }
        }
        catch (System.Exception ex)
        {
            sheetNote = $"\n[FAILED] contact sheet: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            foreach (Texture2D[] pair in sheetTiles)
                foreach (Texture2D t in pair)
                    if (t != null) Object.DestroyImmediate(t);
        }

        var order = new StringBuilder();
        foreach (Candidate c in candidates) order.Append($"{c.id ?? "?"}  ");
        string full = $"Export (scene not modified): {written} PNG(s) written to {dir}; {failed} failed; {missing} viewpoint(s) missing." + sheetNote +
                      "\nRow order: " + order.ToString().TrimEnd() + "\n" + report.ToString().TrimEnd();
        SetStatus(full.TrimEnd());
        Debug.Log("[TargetBuildingPreviewGeneratorEditor] " + full.TrimEnd());
    }

    private static string SafeName(string s)
    {
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s;
    }

    private static Texture2D Downscale(Texture2D src, int size)
    {
        RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            t.Apply(false, false);
            return t;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Scene objects
    // ---------------------------------------------------------------------------------------------------------------

    private static SerializedProperty FindEntry(SerializedObject so, string id)
    {
        SerializedProperty arr = so.FindProperty("targetViewpoints");
        for (int i = 0; i < arr.arraySize; i++)
        {
            SerializedProperty e = arr.GetArrayElementAtIndex(i);
            if (e.FindPropertyRelative("targetId").stringValue == id) return e;
        }
        return null;
    }

    // Creates a NEW 'Viewpoint_<id>' object under the container (the existing container is reused; one is created only if none exists).
    // Never returns an existing object: if a same-named one is found, returns null and changes nothing.
    private static Transform CreateViewpointObject(TargetBuildingPreviewGenerator gen, string id)
    {
        Scene scene = gen.gameObject.scene;
        if (ObjectCount(BuildObjectIndex(scene), id) > 0) return null;

        GameObject container = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == ContainerName) { container = root; break; }

        if (container == null)
        {
            container = new GameObject(ContainerName);
            SceneManager.MoveGameObjectToScene(container, scene);
            Undo.RegisterCreatedObjectUndo(container, "Create viewpoint container");
        }

        var go = new GameObject(ObjectPrefix + id);
        Undo.RegisterCreatedObjectUndo(go, "Create viewpoint");
        go.transform.SetParent(container.transform, false);
        return go.transform;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Suggestion
    // ---------------------------------------------------------------------------------------------------------------

    private enum Status { Accepted, NeedsManual, Invalid }

    private struct SuggestResult
    {
        public Status status;
        public string reason;
        public Vector3 eye;
        public float yaw, pitch, distance, eyeHeight, score, fill, facade, access;
        public string ground;

        public string Describe(string id, string label, float fov)
        {
            switch (status)
            {
                case Status.Accepted:
                    return $"[ACCEPTED] {id} ({label}): distance {distance:F1} m, eye {eyeHeight:F1} m, fov {fov:F0}, score {score:F2} " +
                           $"(fill {fill:F2}, facade {facade:F2}, access {access:F2}), ground: {ground}";
                case Status.NeedsManual:
                    return $"[NEEDS MANUAL PLACEMENT] {id} ({label}): {reason}";
                default:
                    return $"[INVALID] {id} ({label}): {reason}";
            }
        }
    }

    private static Bounds BoundsOf(List<Vector3> pts)
    {
        Vector3 mn = pts[0], mx = pts[0];
        for (int i = 1; i < pts.Count; i++) { mn = Vector3.Min(mn, pts[i]); mx = Vector3.Max(mx, pts[i]); }
        return new Bounds((mn + mx) * 0.5f, mx - mn);
    }

    private static SuggestResult Suggest(TargetBuildingPreviewGenerator gen, GameObject building, float fov)
    {
        var res = new SuggestResult();
        if (!building.activeInHierarchy)
        {
            res.status = Status.Invalid; res.reason = "building is inactive in the scene; activate it so its bounds can be read.";
            return res;
        }

        List<Vector3> corners = TargetBuildingPreviewGenerator.CollectBuildingCorners(building);
        if (corners.Count == 0)
        {
            res.status = Status.Invalid; res.reason = "building has no active renderer.";
            return res;
        }

        Bounds bounds = BoundsOf(corners);
        Transform root = building.transform;
        float baseY = bounds.min.y;
        float snap = gen.suggestGroundSnapRadius;

        var p = new ViewpointFraming.Params
        {
            eyeHeight = gen.suggestEyeHeight,
            margin = gen.suggestFramingMargin,
            maxDistance = gen.suggestMaxDistance,
            maxPitchUpDeg = gen.suggestMaxPitchUp,
            maxPitchDownDeg = gen.suggestMaxPitchDown,
            nearClip = 0.3f
        };

        // Walking surface for a probe position: 2 = NavMesh (may move the position by up to the snap radius), 1 = ground raycast,
        // 0 = assumed (building base height; only if allowed). NavMesh is a preference, never a requirement.
        ViewpointFraming.GroundResolver ground = (Vector3 probe, out Vector3 gp, out int source) =>
        {
            if (snap > 0f && NavMesh.SamplePosition(new Vector3(probe.x, baseY, probe.z), out NavMeshHit nh, snap, NavMesh.AllAreas))
            {
                gp = nh.position; source = 2; return true;
            }
            if (Physics.Raycast(new Vector3(probe.x, baseY + 8f, probe.z), Vector3.down, out RaycastHit rh, 16f, ~0, QueryTriggerInteraction.Ignore)
                && rh.normal.y > 0.8f && !rh.collider.transform.IsChildOf(root))
            {
                gp = rh.point; source = 1; return true;
            }
            if (gen.suggestAllowAssumedGround)
            {
                gp = new Vector3(probe.x, baseY, probe.z); source = 0; return true;
            }
            gp = default; source = -1; return false;
        };

        float bestScore = -1f, bestRejectedScore = -1f;
        SuggestResult best = default, bestRejected = default;
        string lastFail = null;
        int fitted = 0;

        for (int a = 0; a < DirectionCount; a++)
        {
            float ang = a * (360f / DirectionCount) * Mathf.Deg2Rad;
            Vector3 h = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));

            if (!ViewpointFraming.FindClosestFit(corners, bounds, h, fov, p, ground, out ViewpointFraming.FitResult fit, out string why))
            {
                lastFail = why;
                continue;
            }
            fitted++;

            Vector3 toCam = new Vector3(fit.eye.x - bounds.center.x, 0f, fit.eye.z - bounds.center.z);
            float facade = ViewpointFraming.FacadeScore(corners, toCam);
            float access = StreetAccess(fit.eye, bounds, root);
            float fillTerm = Mathf.Clamp01(fit.fill / Mathf.Max(0.01f, 1f - p.margin));
            float score = 0.4f * fillTerm + 0.4f * facade + 0.2f * access;

            var cand = new SuggestResult
            {
                eye = fit.eye, yaw = fit.yawDeg, pitch = fit.pitchDeg, distance = fit.distance, eyeHeight = p.eyeHeight,
                score = score, fill = fit.fill, facade = facade, access = access,
                ground = fit.groundSource == 2 ? "NavMesh" : fit.groundSource == 1 ? "raycast" : "ASSUMED (building base)"
            };

            bool gates = fit.fill >= gen.suggestMinFill && facade >= gen.suggestMinFacade;
            if (gates && (score > bestScore || (Mathf.Approximately(score, bestScore) && fit.distance < best.distance)))
            { bestScore = score; best = cand; }
            if (score > bestRejectedScore) { bestRejectedScore = score; bestRejected = cand; }
        }

        if (fitted == 0)
        {
            res.status = Status.NeedsManual;
            res.reason = lastFail ?? "no direction produced a position from which the whole building fits.";
            return res;
        }
        if (bestScore < 0f)
        {
            res.status = Status.NeedsManual;
            res.reason = $"every fitting view fails a hard gate (fill >= {gen.suggestMinFill:F2}, facade >= {gen.suggestMinFacade:F2}); best was " +
                         $"score {bestRejected.score:F2} (fill {bestRejected.fill:F2}, facade {bestRejected.facade:F2}) at {bestRejected.distance:F0} m.";
            return res;
        }
        if (bestScore < gen.suggestMinScore)
        {
            res.status = Status.NeedsManual;
            res.reason = $"best score {bestScore:F2} < minimum {gen.suggestMinScore:F2} (fill {best.fill:F2}, facade {best.facade:F2}, access {best.access:F2}, " +
                         $"{best.distance:F0} m, ground {best.ground}).";
            return res;
        }

        best.status = Status.Accepted;
        return best;
    }

    // Share of sample points on the camera-facing vertical faces of the bounds that are seen from 'eye' through the real city
    // colliders (a ray whose first hit belongs to the target counts as visible). This does NOT describe the preview image (which
    // renders the building alone); it only reflects whether a pedestrian standing there could really see the building.
    private static float StreetAccess(Vector3 eye, Bounds b, Transform buildingRoot)
    {
        int total = 0, visible = 0;
        float[] hv = { 0.2f, 0.5f, 0.85f };
        for (int face = 0; face < 4; face++)
        {
            Vector3 n = face == 0 ? Vector3.right : face == 1 ? Vector3.left : face == 2 ? Vector3.forward : Vector3.back;
            Vector3 faceCenter = b.center + Vector3.Scale(n, b.extents);
            if (Vector3.Dot(n, eye - faceCenter) <= 0f) continue;

            bool alongX = face >= 2;
            for (int i = 0; i < 5; i++)
            {
                float u = (i + 0.5f) / 5f - 0.5f;
                for (int j = 0; j < hv.Length; j++)
                {
                    Vector3 pt = faceCenter;
                    if (alongX) pt.x += u * b.size.x; else pt.z += u * b.size.z;
                    pt.y = b.min.y + hv[j] * b.size.y;
                    pt -= n * 0.05f;

                    Vector3 d = pt - eye;
                    float len = d.magnitude;
                    total++;
                    if (Physics.Raycast(eye, d / len, out RaycastHit hit, len + 1f, ~0, QueryTriggerInteraction.Ignore)
                        && hit.collider.transform.IsChildOf(buildingRoot))
                        visible++;
                }
            }
        }
        return total == 0 ? 0f : visible / (float)total;
    }

    // Height of 'eye' above the first up-facing collider below it that is not the target, or -1 if none.
    private static float GroundHeightBelow(Vector3 eye, Transform buildingRoot)
    {
        if (Physics.Raycast(eye, Vector3.down, out RaycastHit hit, 50f, ~0, QueryTriggerInteraction.Ignore)
            && hit.normal.y > 0.8f && !hit.collider.transform.IsChildOf(buildingRoot))
            return eye.y - hit.point.y;
        return -1f;
    }
}
