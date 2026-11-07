// GroundNavAuditRooftopCylinders.cs - READ-ONLY scan: which cylinder-like mesh objects stand on a building ROOF.
// Nothing is deleted, disabled, moved, re-parented or re-layered; no component, prefab, layer, modifier or teleport logic is touched; the scene is never saved.
// Geometry is evaluated from meshes and transforms (no activation needed, inactive objects included): roof = the highest non-vertical building (BATI) triangle under the cylinder at/below its base.
// Outputs: CSV (all candidates) + summary in Diagnostics/NavMeshGround/<label>/, and an EditorWindow (Tools > NavMesh Ground Audit > Rooftop cylinders) with Ping / Select buttons (selection only).
// Batch: Unity -batchmode -nographics -projectPath <p> -executeMethod NavMeshAudit.RooftopCylinders.RunBatch -auditLabel cylinders -auditOut <dir> [-auditBake 1] -quit
//   -auditBake 1 activates the groups IN MEMORY, bakes IN MEMORY (nothing saved) and reports whether a walkable NavMesh polygon sits on top of each cylinder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace NavMeshAudit
{
    public static class RooftopCylinders
    {
        public class Row
        {
            public int id; public GameObject go; public string name, path, parent, prefab, mesh, cls, building, buildingPath, anchorRoot, anchorKind, scripts, anchorComponents, anchorChildren, colliders, referencedBy, recommendation, reason, navSource, navPolys, rendererState, activeState;
            public Vector3 pos, rot, scale, size; public float roofY = float.NaN, groundY = float.NaN, offset = float.NaN; public int footOn, footTotal; public bool hasCollider, visualOnly;
        }

        static readonly string[] Groups = { "TIN_Textured", "Practice Area", "Grid Area", "Organic Area", "NavMesh", "EnvironmentConfig" };
        public static List<Row> LastRows = new List<Row>(); public static List<string> LastSummary = new List<string>();

        [MenuItem("Tools/NavMesh Ground Audit/Rooftop cylinders (read-only window)")]
        public static void OpenWindow() { var w = EditorWindow.GetWindow<CylinderWindow>("Rooftop cylinders"); w.minSize = new Vector2(760, 320); Scan(false, null); w.Repaint(); }

        public static void RunBatch()
        {
            EditorSceneManager.OpenScene(Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity", OpenSceneMode.Single);
            Scan(Common.Arg("-auditBake") == "1", Common.Arg("-auditLabel") ?? "cylinders");
        }

        // ---------------------------------------------------------------- cylinder-likeness of a mesh (any name)
        static readonly Dictionary<Mesh, string> cylCache = new Dictionary<Mesh, string>();
        static string CylinderKind(Mesh m)
        {
            string c; if (cylCache.TryGetValue(m, out c)) return c; c = null;
            try
            {
                if (m.isReadable && m.vertexCount >= 16)
                {
                    var v = m.vertices; var b = m.bounds; var ctr = b.center; var ext = b.extents;
                    for (int axis = 0; axis < 3 && c == null; axis++)
                    {
                        int a1 = (axis + 1) % 3, a2 = (axis + 2) % 3; float e1 = ext[a1], e2 = ext[a2], eh = ext[axis];
                        if (e1 < 0.02f || e2 < 0.02f || Mathf.Abs(e1 - e2) / Mathf.Max(e1, e2) > 0.05f) continue;
                        float R = 0.5f * (e1 + e2); int onCircle = 0, atEnds = 0;
                        foreach (var p in v) { float r = Mathf.Sqrt((p[a1] - ctr[a1]) * (p[a1] - ctr[a1]) + (p[a2] - ctr[a2]) * (p[a2] - ctr[a2])); if (r >= 0.97f * R && r <= 1.03f * R) onCircle++; if (Mathf.Abs(Mathf.Abs(p[axis] - ctr[axis]) - eh) < 0.03f * Mathf.Max(eh, 0.01f)) atEnds++; }
                        if (onCircle >= 0.8f * v.Length && atEnds >= 0.6f * v.Length) c = "geometry(axis " + "XYZ"[axis] + ", r=" + R.ToString("F2", Common.Inv) + ")";
                    }
                }
                else if (!m.isReadable && m.name.IndexOf("ylinder", StringComparison.OrdinalIgnoreCase) >= 0) c = "name-only(mesh not readable)";
            }
            catch (Exception) { }
            cylCache[m] = c; return c;
        }

        static Bounds WorldBounds(Mesh m, Matrix4x4 l2w)
        {
            var b = m.bounds; var c = b.center; var e = b.extents; Bounds w = new Bounds(l2w.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++) w.Encapsulate(l2w.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return w;
        }

        class Bld { public MeshFilter mf; public Bounds wb; public Vector3[] wv; public int[] tri; public string path; }

        static bool Tri(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
        {
            y = 0; float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z); if (Mathf.Abs(d) < 1e-9f) return false;
            float u = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d, v = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d, w = 1f - u - v;
            if (u < -1e-4f || v < -1e-4f || w < -1e-4f) return false; y = u * a.y + v * b.y + w * c.y; return true;
        }

        static void RoofAt(List<Bld> blds, float x, float z, float maxY, out float y, out Bld best)
        {
            y = float.NegativeInfinity; best = null;
            foreach (var bl in blds)
            {
                if (x < bl.wb.min.x || x > bl.wb.max.x || z < bl.wb.min.z || z > bl.wb.max.z || bl.wb.min.y > maxY) continue;
                var t = bl.tri; var V = bl.wv;
                for (int i = 0; i < t.Length; i += 3)
                {
                    var a = V[t[i]]; var b = V[t[i + 1]]; var c = V[t[i + 2]]; var n = Vector3.Cross(b - a, c - a); if (n.magnitude < 1e-6f || Mathf.Abs(n.y) / n.magnitude < 0.3f) continue;   // only non-vertical facets
                    float yy; if (Tri(a, b, c, x, z, out yy) && yy <= maxY && yy > y) { y = yy; best = bl; }
                }
            }
        }

        public static void Scan(bool bake, string label)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); cylCache.Clear();
            string dir = label != null ? Common.OutDir(label) : null; var sum = new List<string>(); var rows = new List<Row>();
            var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>(true);
            if (bake && surface != null)
            {
                foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (Groups.Contains(tr.name) && !tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                Physics.SyncTransforms(); surface.BuildNavMesh(); sum.Add("fresh in-memory bake done (groups activated in memory only, nothing saved)");
            }
            var collected = new HashSet<Component>(); if (surface != null && surface.isActiveAndEnabled) foreach (var s in SourcesAudit.Collect(surface)) if (s.component != null) collected.Add(s.component);
            int mask = surface != null ? surface.layerMask.value : -1;
            bool navLoaded = NavMesh.CalculateTriangulation().indices.Length > 0;

            // ---- buildings (BATI) and ground (collider meshes), inactive objects included, nothing activated
            var blds = new List<Bld>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || Common.Classify(mf.sharedMesh, mf.transform) != GeoClass.Building || !mf.sharedMesh.isReadable) continue;
                string p = Common.PathOf(mf.transform); if (p.StartsWith("BATI_Discard")) continue;
                var l2w = mf.transform.localToWorldMatrix; var lv = mf.sharedMesh.vertices; var wv = new Vector3[lv.Length]; for (int i = 0; i < lv.Length; i++) wv[i] = l2w.MultiplyPoint3x4(lv[i]);
                blds.Add(new Bld { mf = mf, wb = WorldBounds(mf.sharedMesh, l2w), wv = wv, tri = mf.sharedMesh.triangles, path = p });
            }
            var gl = new Common.GroundLookup(); int gi = 0;
            foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
            {
                if (mc.gameObject.layer != Common.GroundLayer || mc.sharedMesh == null || !mc.sharedMesh.isReadable) continue; var cls = Common.Classify(mc.sharedMesh, mc.transform); if (cls != GeoClass.TinGround && cls != GeoClass.Bridge && cls != GeoClass.GapPatch) continue;
                var l2w = mc.transform.localToWorldMatrix; var lv = mc.sharedMesh.vertices; var wv = new Vector3[lv.Length]; for (int i = 0; i < lv.Length; i++) wv[i] = l2w.MultiplyPoint3x4(lv[i]);
                gl.Add(new Common.WorldMesh { verts = wv, tris = mc.sharedMesh.triangles, cls = cls }, gi++);
            }

            // ---- candidates: every mesh object (incl. inactive) whose mesh is a cylinder, excluding the city's own building / terrain / bridge meshes
            int meshObjects = 0, cylCount = 0; var ys = new List<float>();
            // who references what (for the Visuals objects): serialized object references from every MonoBehaviour in the scene
            var cands = new List<MeshFilter>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue; meshObjects++;
                var cls = Common.Classify(mf.sharedMesh, mf.transform); if (cls == GeoClass.Building || cls == GeoClass.TinGround || cls == GeoClass.Bridge || cls == GeoClass.GapPatch) continue;
                if (CylinderKind(mf.sharedMesh) == null) continue; cylCount++; cands.Add(mf);
            }
            var candGo = new HashSet<GameObject>(cands.Select(c => c.gameObject));
            var refMap = new Dictionary<GameObject, List<string>>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb == null) continue; SerializedObject so; try { so = new SerializedObject(mb); } catch (Exception) { continue; }
                var it = so.GetIterator();
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                    GameObject tg = it.objectReferenceValue is GameObject g0 ? g0 : (it.objectReferenceValue is Component c0 ? c0.gameObject : null);
                    if (tg == null || tg == mb.gameObject) continue;
                    foreach (var cg in candGo) if (tg == cg || tg.transform.IsChildOf(cg.transform))
                    { List<string> l; if (!refMap.TryGetValue(cg, out l)) { l = new List<string>(); refMap[cg] = l; } l.Add(mb.GetType().Name + " on " + Common.PathOf(mb.transform) + " (." + it.propertyPath + ")"); }
                }
            }

            int nextId = 1;
            foreach (var mf in cands.OrderBy(c => Common.PathOf(c.transform)))
            {
                var t = mf.transform; var wb = WorldBounds(mf.sharedMesh, t.localToWorldMatrix); var mr = mf.GetComponent<MeshRenderer>();
                var row = new Row { id = nextId++, go = mf.gameObject, name = mf.name, path = Common.PathOf(t), parent = t.parent ? t.parent.name : "(root)", mesh = mf.sharedMesh.name + " [" + CylinderKind(mf.sharedMesh) + "] " + AssetDatabase.GetAssetPath(mf.sharedMesh), pos = t.position, rot = t.rotation.eulerAngles, scale = t.lossyScale, size = wb.size };
                row.prefab = PrefabUtility.IsPartOfPrefabInstance(mf.gameObject) ? AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(mf.gameObject)) : "(scene object)";
                row.rendererState = mr == null ? "no MeshRenderer" : (mr.enabled ? "enabled" : "disabled") + ", shadow " + mr.shadowCastingMode; row.activeState = "activeSelf=" + mf.gameObject.activeSelf + " inHierarchy=" + mf.gameObject.activeInHierarchy + " layer=" + LayerMask.LayerToName(mf.gameObject.layer);
                row.scripts = string.Join(", ", mf.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name));
                var cols = mf.GetComponentsInChildren<Collider>(true); row.hasCollider = mf.GetComponent<Collider>() != null;
                // roof test
                float x = wb.center.x, z = wb.center.z, bottom = wb.min.y, radius = Mathf.Max(0.05f, Mathf.Min(wb.size.x, wb.size.z) * 0.5f);
                float ry; Bld rb; RoofAt(blds, x, z, bottom + 0.6f, out ry, out rb);
                int on = 0, tot = 0; if (rb != null)
                {
                    for (int k = 0; k < 9; k++)
                    {
                        float px = k == 0 ? x : x + Mathf.Cos(k * Mathf.PI / 4f) * radius * 0.9f, pz = k == 0 ? z : z + Mathf.Sin(k * Mathf.PI / 4f) * radius * 0.9f; tot++;
                        float y2; Bld b2; RoofAt(blds, px, pz, bottom + 1.5f, out y2, out b2); if (b2 != null && Mathf.Abs(y2 - ry) <= 0.5f) on++;
                    }
                }
                gl.Query(x, z, ys, null); row.groundY = ys.Count > 0 ? ys.Min() : float.NaN; float gTop = ys.Count > 0 ? ys.Max() : float.NaN;
                row.footOn = on; row.footTotal = tot;
                if (rb != null) { row.roofY = ry; row.offset = bottom - ry; row.building = rb.mf.transform.parent != null ? rb.mf.transform.parent.name : rb.mf.name; row.buildingPath = rb.path; }
                bool realRoof = rb != null && (float.IsNaN(gTop) || ry - gTop >= 2.5f);
                if (realRoof && Mathf.Abs(row.offset) <= 1.0f && on >= 8) row.cls = "Confirmed rooftop cylinder";
                else if (realRoof && row.offset >= -1.5f && row.offset <= 3.0f && on >= 5) row.cls = "Likely rooftop cylinder";
                else
                {
                    bool near = false; for (int k = 0; k < 8 && !near; k++) { float px = x + Mathf.Cos(k * Mathf.PI / 4f) * 3f, pz = z + Mathf.Sin(k * Mathf.PI / 4f) * 3f; float y3; Bld b3; RoofAt(blds, px, pz, bottom + 3f, out y3, out b3); if (b3 != null && y3 > (float.IsNaN(gTop) ? -1e9f : gTop) + 2.5f && Mathf.Abs(bottom - y3) < 3f) near = true; }
                    row.cls = realRoof ? "Near a rooftop, but not confirmed" : (near ? "Near a rooftop, but not confirmed" : "Not on a rooftop");
                }
                // teleport anchor structure
                Transform ar = null; for (var q = t; q != null; q = q.parent) { if (q.name.StartsWith("Teleport Anchor") || q.GetComponent("TeleportationAnchor") != null || q.GetComponent("RiverTeleportPoint") != null) { ar = q; if (q.name.StartsWith("Teleport Anchor")) break; } }
                if (ar != null)
                {
                    row.anchorRoot = Common.PathOf(ar); row.anchorKind = "Teleport Anchor (" + string.Join("/", Common.PathOf(ar).Split('/').Reverse().Skip(1).Take(2).Reverse()) + ")";
                    row.anchorComponents = string.Join(", ", ar.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name));
                    var kids = new List<string>(); foreach (var c in ar.GetComponentsInChildren<Transform>(true)) if (c != ar) kids.Add(c.name + "[" + string.Join("+", c.GetComponents<Component>().Where(k => k != null && !(k is Transform)).Select(k => k.GetType().Name)) + "]"); row.anchorChildren = string.Join("; ", kids);
                    row.colliders = string.Join("; ", ar.GetComponentsInChildren<Collider>(true).Select(c => c.GetType().Name + " on " + (c.transform == ar ? "(anchor root)" : Common.PathOf(c.transform).Substring(Common.PathOf(ar).Length)) + (c.isTrigger ? " trigger" : "") + (c.enabled ? "" : " DISABLED")));
                }
                else { row.anchorRoot = ""; row.anchorKind = ""; row.colliders = string.Join("; ", cols.Select(c => c.GetType().Name + " on " + Common.PathOf(c.transform))); }
                int listedColliders = -1; if (ar != null) { var tpa = ar.GetComponent("TeleportationAnchor"); if (tpa != null) { var so2 = new SerializedObject(tpa); var pc = so2.FindProperty("m_Colliders"); if (pc != null) listedColliders = pc.arraySize; } if (listedColliders >= 0) row.colliders += " | TeleportationAnchor.Colliders list entries: " + listedColliders + (listedColliders == 0 ? " (empty -> XRI auto-collects the child colliders, i.e. this MeshCollider IS the interaction collider)" : ""); }
                List<string> rf; row.referencedBy = refMap.TryGetValue(mf.gameObject, out rf) ? string.Join(" | ", rf.Distinct()) : "";
                var onlyVisual = mf.GetComponents<Component>().Where(c => c != null && !(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer)).Count() == 0;
                row.visualOnly = onlyVisual && cols.Length == 0 && string.IsNullOrEmpty(row.referencedBy);
                row.navSource = mr != null && collected.Contains(mr) ? "collected now" : (!mf.gameObject.activeInHierarchy ? "inactive now" : "not collected");
                if (mr != null && (mask & (1 << mf.gameObject.layer)) == 0) row.navSource += " (layer excluded)";
                if (navLoaded) { NavMeshHit nh; float topY = wb.max.y; row.navPolys = NavMesh.SamplePosition(new Vector3(x, topY, z), out nh, 0.6f, NavMesh.AllAreas) && Mathf.Abs(nh.position.x - x) < radius + 0.3f && Mathf.Abs(nh.position.y - topY) < 0.6f ? "walkable polygon on top (y " + nh.position.y.ToString("F2", Common.Inv) + ")" : "none on top"; }
                else row.navPolys = "n/a (no NavMesh loaded)";
                // recommendation
                bool onRoof = row.cls.StartsWith("Confirmed") || row.cls.StartsWith("Likely");
                if (!onRoof) { row.recommendation = "Keep"; row.reason = row.cls == "Not on a rooftop" ? "not on a roof" : "near a roof only - not confirmed"; }
                else if (ar != null && row.visualOnly && row.cls.StartsWith("Confirmed")) { row.recommendation = "Safe to delete visually (the Visuals child only)"; row.reason = "mesh + renderer only: no collider, no other component, not referenced by any script/serialized field; delete ONLY this child, keep the anchor root"; }
                else if (ar != null && row.visualOnly) { row.recommendation = "Review"; row.reason = "likely on the roof; visual-only by components, but position not fully confirmed"; }
                else if (ar != null && row.hasCollider) { row.recommendation = "Review"; row.reason = "Visuals holds the MeshCollider XRI uses as the teleport interaction collider (TeleportationAnchor.Colliders entries: " + listedColliders + ") - do NOT delete Visuals or the anchor root. Visual-only part = its MeshRenderer: disabling only the MeshRenderer hides the disc and removes the walkable roof patch (verified in a bake) while teleporting still works. Delete the whole anchor only if the anchor itself must not exist on this roof."; }
                else { row.recommendation = "Review"; row.reason = (row.hasCollider ? "has its own collider (may be the interaction collider); " : "") + (!string.IsNullOrEmpty(row.referencedBy) ? "referenced by: " + row.referencedBy.Substring(0, Math.Min(120, row.referencedBy.Length)) : "") + (ar == null ? " not a teleport anchor part" : ""); }
                rows.Add(row);
            }
            LastRows = rows;
            int conf = rows.Count(r => r.cls.StartsWith("Confirmed")), lik = rows.Count(r => r.cls.StartsWith("Likely")), near2 = rows.Count(r => r.cls.StartsWith("Near")), not = rows.Count(r => r.cls == "Not on a rooftop");
            sum.Add("mesh objects scanned (incl. inactive): " + meshObjects + "; cylinder-like candidates (excluding city building / terrain / bridge meshes): " + cylCount + "; building meshes used for the roof test: " + blds.Count + " (BATI_Discard excluded)");
            sum.Add("Confirmed rooftop: " + conf + " | Likely rooftop: " + lik + " | Near a rooftop, not confirmed: " + near2 + " | Not on a rooftop: " + not);
            sum.Add("recommendations: Safe to delete visually=" + rows.Count(r => r.recommendation.StartsWith("Safe")) + " Review=" + rows.Count(r => r.recommendation == "Review") + " Keep=" + rows.Count(r => r.recommendation == "Keep"));
            sum.Add("scan time " + sw.Elapsed.TotalSeconds.ToString("F1") + " s; read-only, scene unchanged");
            LastSummary = sum;
            if (dir != null)
            {
                Func<Row, string> f = r => string.Join(",", new[] { r.id.ToString(), Common.Safe(r.name), Common.Safe(r.path), Common.Safe(r.parent), Common.Safe(r.prefab), Common.F(r.pos.x), Common.F(r.pos.y), Common.F(r.pos.z), Common.F(r.rot.x) + "|" + Common.F(r.rot.y) + "|" + Common.F(r.rot.z), Common.F(r.scale.x) + "|" + Common.F(r.scale.y) + "|" + Common.F(r.scale.z), Common.F(r.size.x) + "x" + Common.F(r.size.y) + "x" + Common.F(r.size.z), Common.Safe(r.mesh), Common.Safe(r.rendererState), Common.Safe(r.activeState), r.hasCollider ? "own collider" : "no own collider", Common.Safe(r.colliders), Common.Safe(r.scripts), Common.Safe(r.building), Common.Safe(r.buildingPath), float.IsNaN(r.roofY) ? "" : Common.F(r.roofY), float.IsNaN(r.groundY) ? "" : Common.F(r.groundY), float.IsNaN(r.offset) ? "" : Common.F(r.offset), r.footOn + "/" + r.footTotal, Common.Safe(r.navSource), Common.Safe(r.navPolys), Common.Safe(r.cls), Common.Safe(r.anchorKind), Common.Safe(r.anchorRoot), Common.Safe(r.anchorComponents), Common.Safe(r.anchorChildren), Common.Safe(r.referencedBy), r.visualOnly ? "yes" : "no", Common.Safe(r.recommendation), Common.Safe(r.reason) });
                Common.WriteCsv(dir, "rooftop_cylinders_all_candidates.csv", "ID,name,hierarchyPath,parent,prefabSource,worldX,worldY,worldZ,worldRotationEuler,worldScale,worldBoundsSize,mesh,meshRenderer,state,collider,collidersInAnchor,componentsOnObject,building,buildingPath,roofY,groundY,verticalOffsetAboveRoof,footprintOnRoof(of 9),navMeshSource,navMeshPolygonsOnTop,classification,anchorKind,anchorRoot,anchorRootComponents,anchorChildren,referencedByOtherComponents,visualOnly,recommendation,reason", rows.Select(f));
                Common.WriteCsv(dir, "rooftop_cylinders_confirmed_or_likely.csv", "ID,name,hierarchyPath,parent,prefabSource,worldX,worldY,worldZ,worldRotationEuler,worldScale,worldBoundsSize,mesh,meshRenderer,state,collider,collidersInAnchor,componentsOnObject,building,buildingPath,roofY,groundY,verticalOffsetAboveRoof,footprintOnRoof(of 9),navMeshSource,navMeshPolygonsOnTop,classification,anchorKind,anchorRoot,anchorRootComponents,anchorChildren,referencedByOtherComponents,visualOnly,recommendation,reason", rows.Where(r => r.cls.StartsWith("Confirmed") || r.cls.StartsWith("Likely")).Select(f));
                File.WriteAllText(Path.Combine(dir, "summary.txt"), string.Join("\n", sum)); Debug.Log("[RooftopCylinders]\n" + string.Join("\n", sum));
            }
        }

        public class CylinderWindow : EditorWindow
        {
            Vector2 scroll; bool onlyRoof = true;
            void OnGUI()
            {
                EditorGUILayout.HelpBox("READ-ONLY. Ping / Select only change the Editor selection. Nothing is deleted, disabled or saved.", MessageType.Info);
                if (GUILayout.Button("Rescan (read-only)")) Scan(false, null);
                onlyRoof = EditorGUILayout.ToggleLeft("Show only Confirmed / Likely rooftop cylinders", onlyRoof);
                foreach (var s in LastSummary) EditorGUILayout.LabelField(s, EditorStyles.miniLabel);
                scroll = EditorGUILayout.BeginScrollView(scroll);
                foreach (var r in LastRows)
                {
                    if (onlyRoof && !(r.cls.StartsWith("Confirmed") || r.cls.StartsWith("Likely"))) continue;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("#" + r.id + " " + r.cls.Split(' ')[0] + " | " + r.recommendation.Split('(')[0] + " | " + r.path, GUILayout.MinWidth(300));
                    if (GUILayout.Button("Ping", GUILayout.Width(50)) && r.go != null) EditorGUIUtility.PingObject(r.go);
                    if (GUILayout.Button("Select", GUILayout.Width(55)) && r.go != null) Selection.activeGameObject = r.go;
                    if (!string.IsNullOrEmpty(r.anchorRoot) && GUILayout.Button("Select anchor root", GUILayout.Width(120)) && r.go != null) { for (var q = r.go.transform; q != null; q = q.parent) if (Common.PathOf(q) == r.anchorRoot) { Selection.activeGameObject = q.gameObject; break; } }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }
        }
    }
}
