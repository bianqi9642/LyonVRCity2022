// GroundNavAuditMeshRefs.cs - READ-ONLY audit of every mesh reference on the ground TIN objects (MeshFilter vs MeshCollider vs NavMeshGroundProxy) and of their geometry.
// Changes nothing: no component, object, layer, modifier or asset is written. The groups the scene saves inactive are switched on IN MEMORY only (nothing is saved).
// Batch: Unity -batchmode -nographics -projectPath <p> -executeMethod NavMeshAudit.MeshRefs.RunBatch -auditLabel meshrefs -auditOut <dir> -quit
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NavMeshAudit
{
    public static class MeshRefs
    {
        static readonly string[] Groups = { "TIN_Textured", "Practice Area", "Grid Area", "Organic Area", "NavMesh", "EnvironmentConfig" };   // NavMeshGroundProxy is deliberately NOT activated

        [MenuItem("Tools/NavMesh Ground Audit/Audit ground mesh references (read-only)")]
        public static void RunMenu() { Run("meshrefs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); }

        public static void RunBatch()
        {
            EditorSceneManager.OpenScene(Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity", OpenSceneMode.Single);
            Run(Common.Arg("-auditLabel") ?? "meshrefs");
        }

        static string Ref(UnityEngine.Object o)
        {
            if (o == null) return "null";
            string ap = AssetDatabase.GetAssetPath(o); string g = string.IsNullOrEmpty(ap) ? "" : AssetDatabase.AssetPathToGUID(ap);
            return o.name + " | " + ap + " | guid " + g;
        }

        static string Kind(Mesh m)
        {
            if (m == null) return "none"; string ap = AssetDatabase.GetAssetPath(m);
            return ap.StartsWith("Assets/Models/TIN/Repaired/") ? "REPAIRED" : ap.Contains("/Models/TIN/Patches/") ? "patch(original)" : ap.Contains("/Models/TIN/") ? "ORIGINAL" : "other";
        }

        // local-space triangle set keyed by quantised vertex positions (orientation-free), degenerate triangles dropped
        static HashSet<long> TriSet(Mesh m, out int total, out int down, Matrix4x4 l2w)
        {
            var v = m.vertices; var idx = m.triangles; var set = new HashSet<long>(); total = 0; down = 0;
            Func<Vector3, long> key = p => { long x = Mathf.RoundToInt(p.x * 500f), y = Mathf.RoundToInt(p.y * 500f), z = Mathf.RoundToInt(p.z * 500f); return ((x & 0x1FFFFF) << 42) ^ ((y & 0x1FFFFF) << 21) ^ (z & 0x1FFFFF); };
            for (int i = 0; i < idx.Length; i += 3)
            {
                var a = v[idx[i]]; var b = v[idx[i + 1]]; var c = v[idx[i + 2]]; var n = Vector3.Cross(l2w.MultiplyPoint3x4(b) - l2w.MultiplyPoint3x4(a), l2w.MultiplyPoint3x4(c) - l2w.MultiplyPoint3x4(a));
                if (n.magnitude < 2e-6f) continue; total++; if (n.y < -1e-3f * n.magnitude) down++;
                long k0 = key(a), k1 = key(b), k2 = key(c); long[] s = { k0, k1, k2 }; Array.Sort(s); set.Add((s[0] * 31 + s[1]) * 31 + s[2]);
            }
            return set;
        }

        public static void Run(string label)
        {
            string dir = Common.OutDir(label); var sum = new List<string>(); var csv = new List<string>();
            sum.Add("scene=" + EditorSceneManager.GetActiveScene().path + " time=" + DateTime.Now.ToString("s") + " (read-only; in-memory activation only)");
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (Groups.Contains(tr.name) && !tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
            Physics.SyncTransforms();
            var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>();
            var collected = new HashSet<Component>(); if (surface != null) foreach (var s in SourcesAudit.Collect(surface)) if (s.component != null) collected.Add(s.component);
            sum.Add("NavMeshSurface: useGeometry=" + (surface ? surface.useGeometry.ToString() : "none") + " layerMask=" + (surface ? surface.layerMask.value.ToString() : "-") + " sources=" + collected.Count);

            int A = 0, B = 0, C = 0, D = 0; var proxies = new Dictionary<string, MeshFilter>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true)) if (Common.PathOf(mf.transform).StartsWith("NavMeshGroundProxy/")) proxies[mf.sharedMesh != null ? mf.sharedMesh.name : mf.name] = mf;

            csv.Add("");   // placeholder for header replaced below
            csv.Clear();
            foreach (var col in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true).OrderBy(c => Common.PathOf(c.transform)))
            {
                if (col.gameObject.layer != Common.GroundLayer || col.sharedMesh == null) continue;
                string cap = AssetDatabase.GetAssetPath(col.sharedMesh); if (!cap.Contains("/Models/TIN/")) continue;
                var mf = col.GetComponentInChildren<MeshFilter>(true); var mr = mf != null ? mf.GetComponent<MeshRenderer>() : null;
                string colKind = Kind(col.sharedMesh), mfKind = Kind(mf != null ? mf.sharedMesh : null);
                string cfg = colKind == "REPAIRED" && mfKind == "REPAIRED" ? "A: filter+collider repaired" : colKind == "REPAIRED" ? "B: only collider repaired" : colKind != "REPAIRED" && mfKind != "REPAIRED" ? "C: neither repaired" : "D: other";
                if (cfg[0] == 'A') A++; else if (cfg[0] == 'B') B++; else if (cfg[0] == 'C') C++; else D++;
                var l2w = col.transform.localToWorldMatrix; int totO = 0, dnO = 0, totR = 0, dnR = 0; HashSet<long> so = null, sr = null;
                if (mf != null && mf.sharedMesh != null) so = TriSet(mf.sharedMesh, out totO, out dnO, mf.transform.localToWorldMatrix);
                sr = TriSet(col.sharedMesh, out totR, out dnR, l2w);
                int common = so != null ? so.Intersect(sr).Count() : 0;
                string key = col.sharedMesh.name; MeshFilter px; proxies.TryGetValue(key, out px);
                bool pxSame = px != null && px.sharedMesh == col.sharedMesh;
                csv.Add(string.Join(",", new[] { Common.Safe(Common.PathOf(col.transform)), Common.Safe(Ref(mf != null ? mf.sharedMesh : null)), Common.Safe(Ref(col.sharedMesh)), mr != null ? mr.enabled.ToString() : "n/a", col.enabled.ToString(),
                    mfKind, colKind, cfg[0].ToString(), collected.Contains(mr) ? "collected(renderer)" : "not collected", px != null ? Common.Safe(Common.PathOf(px.transform)) : "none", px != null ? (pxSame ? "proxy uses the SAME asset as the collider" : "proxy uses a different mesh") : "", px != null && collected.Contains(px.GetComponent<MeshRenderer>()) ? "collected" : "not collected",
                    (so != null ? so.Count : 0).ToString(), dnO.ToString(), sr.Count.ToString(), dnR.ToString(), common.ToString(), (so != null ? so.Count - common : 0).ToString(), (sr.Count - common).ToString() }));
            }
            Common.WriteCsv(dir, "tin_mesh_references.csv", "tileRoot,visualMeshFilterMesh,meshColliderMesh,meshRendererEnabled,meshColliderEnabled,visualKind,colliderKind,config,visualNavMeshParticipation,proxyObject,proxyVsCollider,proxyNavMeshParticipation,visualTris(nondegenerate unique),visualDownFacing,repairedTris(unique),repairedDownFacing,identicalTriangles,onlyInVisual,onlyInRepaired", csv);
            sum.Add("TIN tile roots (Ground-layer MeshColliders on TIN meshes): " + (A + B + C + D) + "  A(filter+collider repaired)=" + A + "  B(only collider repaired)=" + B + "  C(neither)=" + C + "  D(other)=" + D);
            sum.Add("repaired TIN assets on disk: " + Directory.GetFiles("Assets/Models/TIN/Repaired", "*.asset").Length);
            sum.Add("proxy objects: " + proxies.Count + "; each MeshFilter in NavMeshGroundProxy: " + string.Join("; ", proxies.Values.Select(p => (p.sharedMesh ? p.sharedMesh.name : "null") + " -> collected=" + collected.Contains(p.GetComponent<MeshRenderer>()) + " rendererEnabled=" + p.GetComponent<MeshRenderer>().enabled).Take(3)) + " ...");
            File.WriteAllText(Path.Combine(dir, "summary.txt"), string.Join("\n", sum)); Debug.Log("[MeshRefs]\n" + string.Join("\n", sum));
        }
    }
}
