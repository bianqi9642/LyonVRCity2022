// GroundNavFix.cs - the repair pipeline that the audit (GroundNavAudit*.cs) justified. Idempotent: running it twice yields the same scene.
//
//  1. TIN COLLISION / NAV PROXY MESHES  (Assets/Models/TIN/Repaired/*.asset): derived copies of every TIN tile mesh, built in the tile's LOCAL space,
//     vertices welded (1 mm), degenerate + duplicate triangles dropped, and every triangle whose world normal faces DOWN re-wound to face up.
//     The imported glTF meshes are never modified. Geometry (positions) is identical, so terrain height and slope are preserved; nothing is filled in.
//  2. PHYSICS: each Ground-layer TIN MeshCollider gets the repaired mesh as sharedMesh (one-sided colliders no longer have 'see-through' down-facing triangles).
//  3. NAVMESH SOURCE: a new scene root "NavMeshGroundProxy" holds one invisible (material-less) MeshRenderer per repaired tile at the tile's world transform.
//     The visual TIN renderers get NavMeshModifier(ignoreFromBuild) so the visual terrain is no longer a NavMesh source (visual geometry untouched).
//  4. ROOFS: every maximal pure-building hierarchy root (BATI) gets NavMeshModifier(overrideArea, area = Not Walkable, applyToChildren). Buildings STAY in the
//     build as obstacles (walls, footprints) but their roofs can never be walkable. (Ignore From Build would let the ground under a building connect to the street.)
//  5. XR rig subtree gets NavMeshModifier(ignoreFromBuild): the rig's head / poke geometry is not environment.
//  6. NavMeshSurface: layer mask Default + Ground + TargetPreview; everything else (collection mode, geometry, agent type, voxel / tile / region settings) is left as it was.
//  7. A NEW NavMeshData asset is baked and assigned; the old NavMesh-NavMesh 2.asset stays on disk untouched (rollback = re-assign it on the surface).
//  8. GroundFallRecovery is added to the rig object (secondary safety net).
// Terrain / practice groups that the scene saves INACTIVE are switched on in memory only while baking, then restored.
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
            m.ignoreFromBuild = ignore; m.overrideArea = overrideArea; m.area = area; m.applyToChildren = true; m.enabled = true; EditorUtility.SetDirty(m);   // a disabled modifier is silently ignored by the baker
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

        /// <summary>Local-space copy of 'src': welded, de-duplicated, down-facing triangles re-wound (decided with the instance's real local-to-world matrix).</summary>
        static Mesh BuildRepaired(Mesh src, Matrix4x4 l2w, out int flipped, out int dropped)
        {
            var v = src.vertices; var idx = src.triangles; bool mirrored = l2w.determinant < 0f;
            var map = new int[v.Length]; var pos = new List<Vector3>(); var grid = new Dictionary<Vector3Int, List<int>>(); const float cell = 0.002f, tol = 0.001f;
            for (int i = 0; i < v.Length; i++)
            {
                var c = new Vector3Int(Mathf.FloorToInt(v[i].x / cell), Mathf.FloorToInt(v[i].y / cell), Mathf.FloorToInt(v[i].z / cell)); int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++) for (int dy = -1; dy <= 1 && found < 0; dy++) for (int dz = -1; dz <= 1 && found < 0; dz++)
                { List<int> l; if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out l)) continue; foreach (int j in l) if ((pos[j] - v[i]).sqrMagnitude <= tol * tol) { found = j; break; } }
                if (found < 0) { found = pos.Count; pos.Add(v[i]); List<int> own; if (!grid.TryGetValue(c, out own)) { own = new List<int>(); grid[c] = own; } own.Add(found); }
                map[i] = found;
            }
            var outTris = new List<int>(); var seen = new HashSet<long>(); flipped = 0; dropped = 0;
            for (int t = 0; t < idx.Length; t += 3)
            {
                int a = map[idx[t]], b = map[idx[t + 1]], c = map[idx[t + 2]];
                var wa = l2w.MultiplyPoint3x4(pos[a]); var wb = l2w.MultiplyPoint3x4(pos[b]); var wc = l2w.MultiplyPoint3x4(pos[c]);
                var nrm = Vector3.Cross(wb - wa, wc - wa); if (mirrored) nrm = -nrm;
                if (a == b || b == c || a == c || nrm.magnitude < 2e-6f) { dropped++; continue; }
                var s = new[] { a, b, c }; Array.Sort(s); long key = ((long)s[0] * 1000003L + s[1]) * 1000003L + s[2];
                if (!seen.Add(key)) { dropped++; continue; }
                if (nrm.y < -1e-3f * nrm.magnitude) { int x = b; b = c; c = x; flipped++; }   // near-vertical facets (|ny| < 1e-3) are left alone
                outTris.Add(a); outTris.Add(b); outTris.Add(c);
            }
            var m = new Mesh { indexFormat = pos.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(pos); m.SetTriangles(outTris, 0, true); m.RecalculateBounds(); m.RecalculateNormals();
            return m;
        }
    }
}
