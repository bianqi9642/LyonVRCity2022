// GroundNavFix.cs - the repair pipeline that the audit (GroundNavAudit*.cs) justified. Idempotent: running it twice yields the same scene.
//
//  1. TIN COLLISION / NAV PROXY MESHES  (Assets/Models/TIN/Repaired/*.asset): derived copies of every TIN tile mesh, built in the tile's LOCAL space,
//     vertices welded (1 mm), degenerate + duplicate triangles dropped, and every triangle whose world normal faces DOWN re-wound to face up.
//     The imported glTF meshes are never modified. Geometry (positions) is identical, so terrain height and slope are preserved; nothing is filled in.
//  2. PHYSICS: each Ground-layer TIN MeshCollider gets the repaired mesh as sharedMesh (one-sided colliders no longer have 'see-through' down-facing triangles).
//  3. NAVMESH SOURCE: a new scene root "NavMeshGroundProxy" holds one invisible (material-less) MeshRenderer per repaired tile at the tile's world transform.
//     The visual TIN renderers get NavMeshModifier(ignoreFromBuild) added when MISSING. This tool never writes Behaviour.enabled: in the production scene those modifiers (8 TIN + the gap patch)
//     are INTENTIONALLY saved disabled, so the visual TIN AND the proxies are both collected; the pipeline must keep working that way (see docs/navmesh_modifier_config.md).
//  4. ROOFS: every maximal pure-building hierarchy root (BATI) gets NavMeshModifier(overrideArea, area = Not Walkable, applyToChildren). Buildings STAY in the
//     build as obstacles (walls, footprints) but their roofs can never be walkable. (Ignore From Build would let the ground under a building connect to the street.)
//  5. XR rig subtree gets NavMeshModifier(ignoreFromBuild): the rig's head / poke geometry is not environment.
//  6. NavMeshSurface: layer mask Default + Ground + TargetPreview; everything else (collection mode, geometry, agent type, voxel / tile / region settings) is left as it was.
//  7. A NEW NavMeshData asset is baked and assigned; the old NavMesh-NavMesh 2.asset stays on disk untouched (rollback = re-assign it on the surface).
//  8. GroundFallRecovery is added to the rig object (secondary safety net).
// Terrain / practice groups that the scene saves INACTIVE are switched on in memory only while baking, then restored.
//
// 2026-11-07: SUPERSEDED for baking by UseRepairedTin() (bottom of this file): the repaired meshes now keep UV / normals, the tiles' own MeshFilters reference them, and the bake needs no NavMeshGroundProxy.
// Apply() (proxy pipeline) is kept only for reference; it still works with the same repaired assets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace NavMeshAudit
{
    public static class Fix
    {
        public const string RepairedDir = "Assets/Models/TIN/Repaired";
        public const string ProxyRoot = "NavMeshGroundProxy";
        public const string NewNavMeshPath = "Assets/Scenes/LyonVRVity35/NavMesh-NavMesh_GroundOnly.asset";

        [MenuItem("Tools/NavMesh Ground Audit/Apply ground fix (repair meshes, modifiers, proxy, bake NEW NavMeshData)")]
        public static void ApplyMenu()
        {
            if (!EditorUtility.DisplayDialog("Apply ground fix", "This modifies the open scene (colliders, modifiers, proxy root, NavMeshSurface) and bakes a NEW NavMeshData asset. The original NavMesh asset is kept. Back up the scene first. Continue?", "Apply", "Cancel")) return;
            Apply(false);
        }

        public static void ApplyBatch()
        {
            string scene = Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity";
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            Apply(true);
        }

        public static void Apply(bool saveScene)
        {
            var log = new List<string>();
            var scene = EditorSceneManager.GetActiveScene();
            var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>();
            if (surface == null) { Debug.LogError("[GroundNavFix] no NavMeshSurface in the active scene"); return; }
            var oldData = surface.navMeshData;

            // ---- 1 + 2: repaired meshes and collider assignment
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Models/TIN/Repaired"));
            var made = new Dictionary<string, Mesh>(); var proxies = new List<KeyValuePair<MeshCollider, Mesh>>(); int flipped = 0, dropped = 0, assigned = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
            {
                if (c.gameObject.layer != Common.GroundLayer || c.sharedMesh == null) continue;
                var src = c.sharedMesh; var cls = Common.Classify(src, c.transform);
                if (cls != GeoClass.TinGround) continue;                       // bridges and the gap patch have no down-facing triangles: left as they are
                string srcPath = AssetDatabase.GetAssetPath(src);
                if (srcPath.StartsWith(RepairedDir)) { var orig = FindOriginal(c); if (orig == null) { proxies.Add(new KeyValuePair<MeshCollider, Mesh>(c, src)); continue; } src = orig; }
                string key = AssetDatabase.GetAssetPath(src) + "|" + src.name + "|" + c.transform.localToWorldMatrix;
                Mesh rep;
                if (!made.TryGetValue(key, out rep))
                {
                    int f, d; rep = BuildRepaired(src, c.transform.localToWorldMatrix, out f, out d); flipped += f; dropped += d;
                    string name = "Ground_" + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(src)) + "_" + Common.Region(c.transform).Replace(" ", "");
                    rep.name = name; string ap = RepairedDir + "/" + name + ".asset";
                    var existing = AssetDatabase.LoadAssetAtPath<Mesh>(ap);
                    if (existing != null) { EditorUtility.CopySerialized(rep, existing); UnityEngine.Object.DestroyImmediate(rep); rep = existing; EditorUtility.SetDirty(rep); }
                    else AssetDatabase.CreateAsset(rep, ap);
                    made[key] = rep; log.Add("repaired mesh " + ap + " (flipped " + f + ", dropped degenerate/duplicate " + d + ")");
                }
                if (c.sharedMesh != rep) { Undo.RecordObject(c, "Assign repaired ground collider"); c.sharedMesh = rep; EditorUtility.SetDirty(c); assigned++; }
                proxies.Add(new KeyValuePair<MeshCollider, Mesh>(c, rep));
            }
            AssetDatabase.SaveAssets();
            log.Add("TIN colliders re-assigned: " + assigned + " | triangles flipped to face up: " + flipped + " | degenerate/duplicate triangles dropped: " + dropped);

            // ---- 3: invisible nav proxies (one per unique tile pose)
            var root = GameObject.Find(ProxyRoot); if (root == null) { root = new GameObject(ProxyRoot); }
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); root.transform.localScale = Vector3.one; root.SetActive(true);
            var wanted = new HashSet<string>(); int n = 0;
            foreach (var kv in proxies)
            {
                var t = kv.Key.transform; string pname = kv.Value.name + "__navproxy" + "_" + ((int)(t.position.x * 10f)).ToString() + "_" + ((int)(t.position.z * 10f)).ToString();   // one proxy per unique tile pose (exact duplicate tile instances share it)
                if (!wanted.Add(pname)) continue;
                var existing = root.transform.Find(pname); var go = existing != null ? existing.gameObject : new GameObject(pname);   // re-use on re-runs: stable object IDs, clean scene diffs
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(t.position, t.rotation); go.transform.localScale = t.lossyScale;
                var mf = go.GetComponent<MeshFilter>(); if (mf == null) mf = go.AddComponent<MeshFilter>(); mf.sharedMesh = kv.Value;
                var mr = go.GetComponent<MeshRenderer>(); if (mr == null) mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterials = new Material[0];
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false; mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off; mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                n++;
            }
            foreach (Transform ch in root.transform.Cast<Transform>().ToList()) if (!wanted.Contains(ch.name)) UnityEngine.Object.DestroyImmediate(ch.gameObject);
            log.Add("nav proxy objects under '" + ProxyRoot + "': " + n);

            // ---- 3b/4/5: modifiers
            int mods = 0;
            // visual TIN renderers: group roots named TIN_Textured, and each Practice-area tile root, no longer feed the NavMesh
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (tr.name == "TIN_Textured") { SetModifier(tr.gameObject, true, false, 0); mods++; }
                else if (tr.parent != null && tr.parent.name == "Practice Area" && HasClass(tr, GeoClass.TinGround) && !HasClass(tr, GeoClass.Building)) { SetModifier(tr.gameObject, true, false, 0); mods++; }
            }
            foreach (var r in BuildingRoots()) { SetModifier(r.gameObject, false, true, Common.NotWalkableArea); mods++; }
            var xr = GameObject.Find("XR Interaction Setup"); if (xr != null) { SetModifier(xr, true, false, 0); mods++; }
            log.Add("NavMeshModifiers set: " + mods + " (" + BuildingRoots().Count + " building roots = Not Walkable)");

            // ---- 6: surface
            Undo.RecordObject(surface, "NavMeshSurface ground-only");
            surface.layerMask = (1 << 0) | (1 << Common.GroundLayer) | (1 << 6);
            EditorUtility.SetDirty(surface);

            // ---- fall recovery
            var aga = UnityEngine.Object.FindObjectOfType<AutoGroundAlign>(true);
            if (aga != null && aga.GetComponent<GroundFallRecovery>() == null) { aga.gameObject.AddComponent<GroundFallRecovery>(); log.Add("GroundFallRecovery added to " + Common.PathOf(aga.transform)); }

            // ---- 7: bake into a NEW asset with the terrain groups on (as the original bake had)
            var changed = Common.ActivateTerrain(true);
            try
            {
                Physics.SyncTransforms();
                surface.BuildNavMesh();                       // replaces surface.navMeshData in memory
                var data = surface.navMeshData;
                if (data == null) { Debug.LogError("[GroundNavFix] bake produced no NavMeshData"); return; }
                var prev = AssetDatabase.LoadAssetAtPath<NavMeshData>(NewNavMeshPath);
                if (prev != null) { surface.RemoveData(); EditorUtility.CopySerialized(data, prev); EditorUtility.SetDirty(prev); AssetDatabase.SaveAssets(); surface.navMeshData = prev; surface.AddData(); }   // keeps the asset GUID stable across re-runs
                else { AssetDatabase.CreateAsset(data, NewNavMeshPath); surface.RemoveData(); surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(NewNavMeshPath); surface.AddData(); }
                EditorUtility.SetDirty(surface);
                log.Add("new NavMeshData: " + NewNavMeshPath + " (previous: " + (oldData ? AssetDatabase.GetAssetPath(oldData) : "none") + " kept on disk)");
            }
            finally { Common.Restore(changed); }

            EditorSceneManager.MarkSceneDirty(scene);
            if (saveScene) EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[GroundNavFix]\n" + string.Join("\n", log));
            File.WriteAllText(Path.Combine(Common.OutDir("fix_log"), "fix_log.txt"), string.Join("\n", log));
        }

        static Mesh FindOriginal(MeshCollider c)
        {
            // when the collider already carries a repaired mesh, the matching render mesh of the same tile is the original source
            var mf = c.GetComponentInChildren<MeshFilter>(true); if (mf != null && mf.sharedMesh != null && !AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(RepairedDir)) return mf.sharedMesh;
            return null;
        }

        static bool HasClass(Transform t, GeoClass cls)
        {
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true)) if (Common.Classify(mf.sharedMesh, mf.transform) == cls) return true;
            return false;
        }

        static void SetModifier(GameObject go, bool ignore, bool overrideArea, int area)
        {
            var m = go.GetComponent<NavMeshModifier>(); if (m == null) m = go.AddComponent<NavMeshModifier>();
            Undo.RecordObject(m, "NavMeshModifier");
            m.ignoreFromBuild = ignore; m.overrideArea = overrideArea; m.area = area; m.applyToChildren = true; EditorUtility.SetDirty(m);   // NOTE: m.enabled is deliberately NOT touched - several modifiers are intentionally saved disabled and must stay so
        }

        /// <summary>Maximal hierarchy nodes whose whole subtree holds only building (BATI) geometry (scripts / empty nodes do not matter, any other mesh class does).</summary>
        public static List<Transform> BuildingRoots()
        {
            var result = new List<Transform>(); var pure = new Dictionary<Transform, bool>(); var hasBuilding = new Dictionary<Transform, bool>();
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()) Eval(root.transform, pure, hasBuilding);
            foreach (var kv in pure)
            {
                if (!kv.Value || !hasBuilding[kv.Key]) continue;
                var p = kv.Key.parent; if (p != null && pure.ContainsKey(p) && pure[p] && hasBuilding[p]) continue;
                result.Add(kv.Key);
            }
            return result.OrderBy(t => Common.PathOf(t)).ToList();
        }
        static void Eval(Transform t, Dictionary<Transform, bool> pure, Dictionary<Transform, bool> hasB)
        {
            bool p = true, hb = false;
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && t.GetComponent<MeshRenderer>() != null) { var c = Common.Classify(mf.sharedMesh, t); if (c == GeoClass.Building) hb = true; else p = false; }
            if (t.GetComponent<MeshCollider>() != null && t.GetComponent<MeshCollider>().sharedMesh != null) { var c = Common.Classify(t.GetComponent<MeshCollider>().sharedMesh, t); if (c == GeoClass.Building) hb = true; else p = false; }
            foreach (Transform ch in t) { Eval(ch, pure, hasB); p &= pure[ch]; hb |= hasB[ch]; }
            pure[t] = p; hasB[t] = hb;
        }

        /// <summary>
        /// Local-space copy of 'src' that keeps EVERYTHING the renderer needs (position, normal, UV0, one sub-mesh, same transform): only the index buffer differs.
        /// Corners are welded only when position AND normal AND uv are identical; degenerate and duplicate triangles are dropped; every triangle whose world normal faces DOWN is re-wound
        /// (decided with the instance's real local-to-world matrix) and gets its own vertex copies with the normal turned up, so shading stays consistent. No vertex moves.
        /// The result is used by the MeshCollider, the MeshFilter and (through the filter) the NavMesh bake, so one asset is the single repaired ground source.
        /// </summary>
        static Mesh BuildRepaired(Mesh src, Matrix4x4 l2w, out int flipped, out int dropped)
        {
            var v = src.vertices; var nrm = src.normals; var uv = new List<Vector2>(); src.GetUVs(0, uv); var idx = src.triangles; bool mirrored = l2w.determinant < 0f;
            bool hasN = nrm != null && nrm.Length == v.Length, hasUv = uv.Count == v.Length;
            var pos = new List<Vector3>(); var nn = new List<Vector3>(); var uu = new List<Vector2>(); var map = new Dictionary<string, int>();
            Func<int, bool, int> corner = (i, flipN) =>
            {
                Vector3 n = hasN ? nrm[i] : Vector3.up; if (flipN && n.y < 0f) n = -n; Vector2 t2 = hasUv ? uv[i] : Vector2.zero;
                string k = Mathf.RoundToInt(v[i].x * 1000f) + "," + Mathf.RoundToInt(v[i].y * 1000f) + "," + Mathf.RoundToInt(v[i].z * 1000f) + "|" + Mathf.RoundToInt(n.x * 1000f) + "," + Mathf.RoundToInt(n.y * 1000f) + "," + Mathf.RoundToInt(n.z * 1000f) + "|" + Mathf.RoundToInt(t2.x * 100000f) + "," + Mathf.RoundToInt(t2.y * 100000f) + (flipN ? "|F" : "");
                int id; if (map.TryGetValue(k, out id)) return id; id = pos.Count; pos.Add(v[i]); nn.Add(n); uu.Add(t2); map[k] = id; return id;
            };
            var outTris = new List<int>(); var seen = new HashSet<string>(); flipped = 0; dropped = 0;
            for (int t = 0; t < idx.Length; t += 3)
            {
                int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
                var wa = l2w.MultiplyPoint3x4(v[ia]); var wb = l2w.MultiplyPoint3x4(v[ib]); var wc = l2w.MultiplyPoint3x4(v[ic]);
                var n3 = Vector3.Cross(wb - wa, wc - wa); if (mirrored) n3 = -n3;
                if (n3.magnitude < 2e-6f) { dropped++; continue; }
                long ka = Mathf.RoundToInt(v[ia].x * 1000f) * 73856093L ^ Mathf.RoundToInt(v[ia].y * 1000f) * 19349663L ^ Mathf.RoundToInt(v[ia].z * 1000f) * 83492791L;
                long kb = Mathf.RoundToInt(v[ib].x * 1000f) * 73856093L ^ Mathf.RoundToInt(v[ib].y * 1000f) * 19349663L ^ Mathf.RoundToInt(v[ib].z * 1000f) * 83492791L;
                long kc = Mathf.RoundToInt(v[ic].x * 1000f) * 73856093L ^ Mathf.RoundToInt(v[ic].y * 1000f) * 19349663L ^ Mathf.RoundToInt(v[ic].z * 1000f) * 83492791L;
                var s = new[] { ka, kb, kc }; Array.Sort(s); string tk = s[0] + "_" + s[1] + "_" + s[2];
                if (!seen.Add(tk)) { dropped++; continue; }
                bool flip = n3.y < -1e-3f * n3.magnitude;     // near-vertical facets (|ny| < 1e-3) are left alone
                int a = corner(ia, flip), b = corner(ib, flip), c = corner(ic, flip);
                if (flip) { int x = b; b = c; c = x; flipped++; }
                outTris.Add(a); outTris.Add(b); outTris.Add(c);
            }
            var m = new Mesh { indexFormat = pos.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(pos); if (hasN) m.SetNormals(nn); if (hasUv) m.SetUVs(0, uu); m.SetTriangles(outTris, 0, true); m.RecalculateBounds();
            return m;
        }

        static readonly string[] BakeGroups = { "TIN_Textured", "Practice Area", "Grid Area", "Organic Area", "NavMesh", "EnvironmentConfig" };
        public const string SingleSourceNavMeshPath = "Assets/Scenes/LyonVRVity35/NavMesh-NavMesh_RepairedTIN.asset";

        [MenuItem("Tools/NavMesh Ground Audit/Use repaired TIN as the single NavMesh source (MeshFilter = repaired mesh, bake NEW NavMeshData)")]
        public static void UseRepairedTinMenu()
        {
            if (!EditorUtility.DisplayDialog("Single repaired source", "Assigns the repaired TIN meshes (UV / normals preserved) to the tiles' MeshFilters and bakes a NEW NavMeshData asset. The NavMeshGroundProxy is not used for the bake. No modifier is changed. Back up the scene first.", "Apply", "Cancel")) return;
            UseRepairedTin(false);
        }

        public static void UseRepairedTinBatch()
        {
            EditorSceneManager.OpenScene(Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity", OpenSceneMode.Single);
            UseRepairedTin(true);
        }

        /// <summary>
        /// 1. For every Ground-layer TIN tile whose MeshFilter still shows the ORIGINAL glTF mesh: rebuild the tile's repaired asset in place (same asset, same GUID) with UV / normals kept, and assign it to the MeshFilter
        ///    (the MeshCollider already references the same asset). 2. Bake a NEW NavMeshData with the NavMeshGroundProxy switched off for the bake, so each ground surface is collected exactly once. 3. Point the surface at it.
        /// Never touches: any NavMeshModifier (enabled states included), layers, groups' saved active states (restored), the old NavMeshData assets.
        /// </summary>
        public static void UseRepairedTin(bool saveScene)
        {
            var log = new List<string>(); var scene = EditorSceneManager.GetActiveScene();
            var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>(true); if (surface == null) { Debug.LogError("[GroundNavFix] no NavMeshSurface"); return; }
            var mods = UnityEngine.Object.FindObjectsOfType<NavMeshModifier>(true); var enabledBefore = mods.Select(m => m.enabled).ToList();
            int changedFilters = 0, skipped = 0, flipped = 0, dropped = 0; var done = new HashSet<Mesh>();
            foreach (var col in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
            {
                if (col.gameObject.layer != Common.GroundLayer || col.sharedMesh == null) continue;
                string cap = AssetDatabase.GetAssetPath(col.sharedMesh); if (!cap.StartsWith(RepairedDir)) continue;
                var mf = col.GetComponentInChildren<MeshFilter>(true); if (mf == null || mf.sharedMesh == null) continue;
                if (AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(RepairedDir)) { skipped++; continue; }       // already the repaired asset
                var rep = col.sharedMesh;
                if (done.Add(rep))
                {
                    int f, d; var built = BuildRepaired(mf.sharedMesh, col.transform.localToWorldMatrix, out f, out d); flipped += f; dropped += d;
                    built.name = rep.name; EditorUtility.CopySerialized(built, rep); UnityEngine.Object.DestroyImmediate(built); EditorUtility.SetDirty(rep);   // same asset object -> GUID and every reference preserved
                    log.Add("rebuilt " + cap + " (uv/normals kept; flipped " + f + ", dropped " + d + ")");
                }
                Undo.RecordObject(mf, "Repaired TIN as visual mesh"); mf.sharedMesh = rep; EditorUtility.SetDirty(mf); changedFilters++;
            }
            AssetDatabase.SaveAssets();
            log.Add("MeshFilters now referencing the repaired asset: " + changedFilters + " (already repaired: " + skipped + ")");

            // ---- bake: groups on IN MEMORY, proxy off for the bake, everything restored afterwards
            var touched = new List<KeyValuePair<GameObject, bool>>();
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                bool want = BakeGroups.Contains(tr.name); bool isProxy = tr.name == ProxyRoot;
                if ((want && !tr.gameObject.activeSelf) || (isProxy && tr.gameObject.activeSelf)) { touched.Add(new KeyValuePair<GameObject, bool>(tr.gameObject, tr.gameObject.activeSelf)); tr.gameObject.SetActive(want); }
            }
            Physics.SyncTransforms();
            try
            {
                surface.BuildNavMesh(); var data = surface.navMeshData;
                if (data == null) { Debug.LogError("[GroundNavFix] bake produced no NavMeshData"); return; }
                var prev = AssetDatabase.LoadAssetAtPath<NavMeshData>(SingleSourceNavMeshPath);
                if (prev != null) { surface.RemoveData(); EditorUtility.CopySerialized(data, prev); EditorUtility.SetDirty(prev); AssetDatabase.SaveAssets(); surface.navMeshData = prev; surface.AddData(); }
                else { AssetDatabase.CreateAsset(data, SingleSourceNavMeshPath); surface.RemoveData(); surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(SingleSourceNavMeshPath); surface.AddData(); }
                EditorUtility.SetDirty(surface);
                log.Add("new NavMeshData: " + SingleSourceNavMeshPath + " (previous assets untouched)");
            }
            finally { foreach (var kv in touched) if (kv.Key != null) kv.Key.SetActive(kv.Value); }

            var enabledAfter = UnityEngine.Object.FindObjectsOfType<NavMeshModifier>(true).Select(m => m.enabled).ToList();
            log.Add("NavMeshModifier enabled states unchanged: " + enabledBefore.SequenceEqual(enabledAfter) + " (" + enabledBefore.Count(e => !e) + " disabled)");
            EditorSceneManager.MarkSceneDirty(scene); if (saveScene) EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[GroundNavFix] single source\n" + string.Join("\n", log));
            File.WriteAllText(Path.Combine(Common.OutDir("single_source_log"), "single_source_log.txt"), string.Join("\n", log));
        }
    }
}
