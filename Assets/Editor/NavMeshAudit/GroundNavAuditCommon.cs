// GroundNavAuditCommon.cs
// Editor-only diagnostics for the NavMesh / ground-collision audit (see docs/navmesh_ground_audit.md).
// Role: shared helpers for the four diagnostics (source inventory, mesh topology, NavMesh/physics coverage, connectivity).
// Integration: nothing here is referenced by runtime code; everything lives in the Editor assembly and writes CSV/TXT reports
//   OUTSIDE Assets (default <project>/Diagnostics/NavMeshGround/<label>/). The scene is never saved by these tools: terrain groups that are
//   saved inactive are switched on IN MEMORY (so the colliders / renderers exist for measuring) and restored afterwards.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NavMeshAudit
{
    /// <summary>What a mesh in the scene is, derived from the asset it was imported from (never from its name alone).</summary>
    public enum GeoClass { TinGround, GapPatch, Bridge, Building, Other }

    public static class Common
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public const int GroundLayer = 3;           // "Ground" (TagManager layer 3): TIN terrain + bridge decks; AutoGroundAlign mask
        public const int NotWalkableArea = 1;       // built-in NavMesh area index 1 ("Not Walkable")

        public static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }

        public static string PathOf(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        public static GeoClass Classify(Mesh mesh, Transform t)
        {
            string ap = mesh != null ? AssetDatabase.GetAssetPath(mesh) : "";
            if (ap.Contains("/Models/TIN/Patches/")) return GeoClass.GapPatch;
            if (ap.Contains("/Models/TIN/")) return GeoClass.TinGround;
            if (ap.Contains("/Models/PONT/")) return GeoClass.Bridge;
            if (ap.Contains("/Models/BATI/")) return GeoClass.Building;
            return GeoClass.Other;
        }

        public static string Region(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == "Grid Area" || p.name == "Organic Area" || p.name == "Practice Area" || p.name == "PONT") return p.name;
            return "none";
        }

        public static string OutDir(string label)
        {
            string root = Arg("-auditOut");
            if (string.IsNullOrEmpty(root))
                root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Diagnostics", "NavMeshGround"));
            string d = Path.Combine(root, label);
            Directory.CreateDirectory(d);
            return d;
        }

        public static string F(float v) { return v.ToString("F3", Inv); }
        public static string F(double v) { return v.ToString("F3", Inv); }
        public static string Safe(string s) { return (s ?? "").Replace(',', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        /// <summary>Switch on, IN MEMORY, the terrain groups the scene saves inactive. Returns the objects that were changed so the caller can restore them.</summary>
        public static List<GameObject> ActivateTerrain(bool includePractice)
        {
            var changed = new List<GameObject>();
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                bool want = tr.name == "TIN_Textured" || (includePractice && tr.name == "Practice Area");
                if (want && !tr.gameObject.activeSelf) { tr.gameObject.SetActive(true); changed.Add(tr.gameObject); }
            }
            Physics.SyncTransforms();      // explicit sync only: the project-wide Physics.autoSyncTransforms setting is deliberately NOT touched
            return changed;
        }

        public static void Restore(List<GameObject> changed) { foreach (var g in changed) if (g != null) g.SetActive(false); }

        public class GroundTri { public Vector3 a, b, c; public int tile; public int cls; }

        /// <summary>One ground / bridge mesh in world space (welded copy is built by TopologyAudit).</summary>
        public class WorldMesh
        {
            public string path, region, assetPath, name; public GeoClass cls; public Mesh mesh; public Matrix4x4 l2w;
            public Vector3[] verts; public int[] tris; public Bounds bounds; public string source; // source: renderer | collider
        }

        public static List<WorldMesh> CollectWorldMeshes(bool fromColliders)
        {
            var list = new List<WorldMesh>();
            if (fromColliders)
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<MeshCollider>())
                {
                    if (!c.enabled || c.sharedMesh == null || c.gameObject.layer != GroundLayer) continue;
                    var cls = Classify(c.sharedMesh, c.transform);
                    if (cls == GeoClass.Other || cls == GeoClass.Building) continue;
                    list.Add(Build(c.transform, c.sharedMesh, cls, "collider"));
                }
            }
            else
            {
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                {
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled || mf.sharedMesh == null) continue;   // TIN renderers sit on layer 0 (only the colliders are on Ground), so classify by source asset, not layer
                    var cls = Classify(mf.sharedMesh, mf.transform);
                    if (cls == GeoClass.Other || cls == GeoClass.Building) continue;
                    list.Add(Build(mf.transform, mf.sharedMesh, cls, "renderer"));
                }
            }
            return list;
        }

        static WorldMesh Build(Transform t, Mesh m, GeoClass cls, string src)
        {
            var w = new WorldMesh { path = PathOf(t), region = Region(t), assetPath = AssetDatabase.GetAssetPath(m), name = m.name, cls = cls, mesh = m, l2w = t.localToWorldMatrix, source = src };
            var lv = m.vertices; w.tris = m.triangles; w.verts = new Vector3[lv.Length];
            bool mirrored = t.localToWorldMatrix.determinant < 0;     // a mirroring transform reverses winding; undo it so 'up-facing' means what the physics engine sees
            for (int i = 0; i < lv.Length; i++) w.verts[i] = w.l2w.MultiplyPoint3x4(lv[i]);
            if (mirrored) for (int i = 0; i < w.tris.Length; i += 3) { int x = w.tris[i + 1]; w.tris[i + 1] = w.tris[i + 2]; w.tris[i + 2] = x; }
            var b = new Bounds(w.verts.Length > 0 ? w.verts[0] : Vector3.zero, Vector3.zero);
            foreach (var v in w.verts) b.Encapsulate(v);
            w.bounds = b;
            return w;
        }

        /// <summary>2D (XZ) spatial hash over ground triangles: gives the surface elevation(s) under any X/Z regardless of triangle winding.</summary>
        public class GroundLookup
        {
            const float Cell = 16f;
            public readonly List<GroundTri> tris = new List<GroundTri>();
            readonly Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            public readonly List<string> tiles = new List<string>();
            public Bounds bounds; bool hasBounds;

            static long Key(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

            public void Add(WorldMesh m, int tileId)
            {
                for (int i = 0; i < m.tris.Length; i += 3)
                {
                    var t = new GroundTri { a = m.verts[m.tris[i]], b = m.verts[m.tris[i + 1]], c = m.verts[m.tris[i + 2]], tile = tileId, cls = (int)m.cls };
                    int idx = tris.Count; tris.Add(t);
                    float minX = Mathf.Min(t.a.x, t.b.x, t.c.x), maxX = Mathf.Max(t.a.x, t.b.x, t.c.x), minZ = Mathf.Min(t.a.z, t.b.z, t.c.z), maxZ = Mathf.Max(t.a.z, t.b.z, t.c.z);
                    for (int cx = Mathf.FloorToInt(minX / Cell); cx <= Mathf.FloorToInt(maxX / Cell); cx++)
                        for (int cz = Mathf.FloorToInt(minZ / Cell); cz <= Mathf.FloorToInt(maxZ / Cell); cz++)
                        {
                            long k = Key(cx, cz); List<int> l; if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
                            l.Add(idx);
                        }
                    if (!hasBounds) { bounds = new Bounds(t.a, Vector3.zero); hasBounds = true; }
                    bounds.Encapsulate(t.a); bounds.Encapsulate(t.b); bounds.Encapsulate(t.c);
                }
            }

            /// <summary>All footprint triangles containing (x,z); y is the interpolated elevation.</summary>
            public int Query(float x, float z, List<float> ys, List<int> triIdx)
            {
                ys.Clear(); if (triIdx != null) triIdx.Clear();
                List<int> l; if (!grid.TryGetValue(Key(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell)), out l)) return 0;
                for (int k = 0; k < l.Count; k++)
                {
                    var t = tris[l[k]];
                    float d = (t.b.z - t.c.z) * (t.a.x - t.c.x) + (t.c.x - t.b.x) * (t.a.z - t.c.z);
                    if (Mathf.Abs(d) < 1e-9f) continue;
                    float u = ((t.b.z - t.c.z) * (x - t.c.x) + (t.c.x - t.b.x) * (z - t.c.z)) / d;
                    float v = ((t.c.z - t.a.z) * (x - t.c.x) + (t.a.x - t.c.x) * (z - t.c.z)) / d;
                    float w = 1f - u - v; const float e = -1e-4f;
                    if (u < e || v < e || w < e) continue;
                    ys.Add(u * t.a.y + v * t.b.y + w * t.c.y); if (triIdx != null) triIdx.Add(l[k]);
                }
                return ys.Count;
            }
        }

        // meshes are added in a deterministic order: region attribution in tile-overlap zones must not depend on scene enumeration order
        public static GroundLookup BuildLookup(List<WorldMesh> meshes, params GeoClass[] classes)
        {
            var lk = new GroundLookup(); var set = new HashSet<GeoClass>(classes);
            foreach (var m in meshes.OrderBy(x => x.path + "|" + x.assetPath + "|" + x.bounds.min.ToString("F2"))) { if (!set.Contains(m.cls)) continue; lk.tiles.Add(m.path); lk.Add(m, lk.tiles.Count - 1); }
            return lk;
        }

        public static string WriteCsv(string dir, string file, string header, IEnumerable<string> rows)
        {
            string p = Path.Combine(dir, file);
            using (var w = new StreamWriter(p, false, new UTF8Encoding(false))) { w.WriteLine(header); foreach (var r in rows) w.WriteLine(r); }
            return p;
        }

        public static Vector3Int Quant(Vector3 v, float step)
        {
            return new Vector3Int(Mathf.RoundToInt(v.x / step), Mathf.RoundToInt(v.y / step), Mathf.RoundToInt(v.z / step));
        }
    }
}
