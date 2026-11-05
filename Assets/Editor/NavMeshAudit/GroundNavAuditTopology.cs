// GroundNavAuditTopology.cs - D2: topology of the ground (TIN) meshes in world space.
// Per mesh AND for all ground meshes welded together (tile seams are shared edges only after a cross-mesh weld).
// Reports vertices / triangles / connected components / boundary edges / non-manifold edges / degenerate + duplicate triangles / down-facing triangles
// and splits boundary loops into the OUTER boundary of each component and INNER loops (hole candidates). Open boundaries are NOT automatically defects.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NavMeshAudit
{
    public static class TopologyAudit
    {
        public class Loop { public int component; public bool outer; public int edges; public float areaXZ, perimeter; public Vector3 centroid; public Bounds bounds; public string meshes; }
        public class Stats
        {
            public string name; public int rawVerts, weldedVerts, tris, components, boundaryEdges, nonManifoldEdges, inconsistentEdges, degenerate, duplicateTris, downFacing, unusedVerts;
            public double areaTotal, areaDown, areaSteep; public Bounds bounds; public List<Loop> loops = new List<Loop>();
        }

        class Welder
        {
            const float Cell = 0.02f, Tol = 0.01f;
            readonly Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();
            public readonly List<Vector3> pos = new List<Vector3>();
            public int Add(Vector3 v)
            {
                var c = new Vector3Int(Mathf.FloorToInt(v.x / Cell), Mathf.FloorToInt(v.y / Cell), Mathf.FloorToInt(v.z / Cell));
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    List<int> l; if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out l)) continue;
                    foreach (int i in l) if ((pos[i] - v).sqrMagnitude <= Tol * Tol) return i;
                }
                int id = pos.Count; pos.Add(v);
                List<int> own; if (!grid.TryGetValue(c, out own)) { own = new List<int>(); grid[c] = own; } own.Add(id);
                return id;
            }
        }

        public static Stats Analyse(string name, IList<Common.WorldMesh> meshes, float agentSlope)
        {
            var st = new Stats { name = name };
            var w = new Welder(); var tri = new List<int>(); var triMesh = new List<int>(); bool first = true;
            for (int mi = 0; mi < meshes.Count; mi++)
            {
                var m = meshes[mi]; st.rawVerts += m.verts.Length;
                var map = new int[m.verts.Length]; for (int i = 0; i < map.Length; i++) map[i] = w.Add(m.verts[i]);
                for (int i = 0; i < m.tris.Length; i += 3) { tri.Add(map[m.tris[i]]); tri.Add(map[m.tris[i + 1]]); tri.Add(map[m.tris[i + 2]]); triMesh.Add(mi); }
                if (first) { st.bounds = m.bounds; first = false; } else st.bounds.Encapsulate(m.bounds);
            }
            st.weldedVerts = w.pos.Count; int T = tri.Count / 3; st.tris = T;
            var pos = w.pos; float cosSlope = Mathf.Cos(agentSlope * Mathf.Deg2Rad);
            var edgeCount = new Dictionary<long, int>(); var edgeDir = new Dictionary<long, int>();
            var parent = new int[pos.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
            Func<int, int> find = null; find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            var triSeen = new HashSet<long>(); var used = new bool[pos.Count]; var live = new bool[T];
            for (int t = 0; t < T; t++)
            {
                int a = tri[3 * t], b = tri[3 * t + 1], c = tri[3 * t + 2];
                var n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]); float area = 0.5f * n.magnitude;
                if (a == b || b == c || a == c || area < 1e-4f) { st.degenerate++; continue; }
                live[t] = true; used[a] = used[b] = used[c] = true; st.areaTotal += area;
                var s = new[] { a, b, c }; Array.Sort(s); long tk = ((long)s[0] * 1000003L + s[1]) * 1000003L + s[2];
                if (!triSeen.Add(tk)) st.duplicateTris++;
                float ny = n.y / (2f * area);
                if (ny < -1e-3f) { st.downFacing++; st.areaDown += area; }   // |ny| < 1e-3 = vertical wall facets of the TIN: orientation is meaningless for ground
                else if (ny < cosSlope) st.areaSteep += area;
                int[] e = { a, b, b, c, c, a };
                for (int k = 0; k < 3; k++)
                {
                    int u = e[2 * k], v = e[2 * k + 1]; long key = u < v ? (long)u * 4000000L + v : (long)v * 4000000L + u; int dir = u < v ? 1 : 2;
                    int cnt; edgeCount.TryGetValue(key, out cnt); edgeCount[key] = cnt + 1;
                    int d; edgeDir.TryGetValue(key, out d); if ((d & dir) != 0) { /* same direction twice */ st.inconsistentEdges++; } edgeDir[key] = d | dir;
                }
                int ra = find(a), rb = find(b), rc = find(c); parent[rb] = ra; parent[find(rc)] = ra;
            }
            for (int i = 0; i < used.Length; i++) if (!used[i]) st.unusedVerts++;
            var comps = new HashSet<int>(); for (int i = 0; i < used.Length; i++) if (used[i]) comps.Add(find(i)); st.components = comps.Count;
            // boundary edges + loops
            var bAdj = new Dictionary<int, List<int>>(); var bEdges = new List<long>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value > 2) st.nonManifoldEdges++;
                if (kv.Value != 1) continue;
                st.boundaryEdges++; int u = (int)(kv.Key / 4000000L), v = (int)(kv.Key % 4000000L); bEdges.Add(kv.Key);
                List<int> l; if (!bAdj.TryGetValue(u, out l)) { l = new List<int>(); bAdj[u] = l; } l.Add(v);
                if (!bAdj.TryGetValue(v, out l)) { l = new List<int>(); bAdj[v] = l; } l.Add(u);
            }
            var usedEdge = new HashSet<long>(); var rawLoops = new List<Loop>(); var loopVerts = new List<List<int>>();
            foreach (long ek in bEdges)
            {
                if (usedEdge.Contains(ek)) continue;
                int start = (int)(ek / 4000000L), cur = (int)(ek % 4000000L); var verts = new List<int> { start, cur }; usedEdge.Add(ek); int prev = start; int guard = 0;
                while (cur != start && guard++ < 5000000)
                {
                    int next = -1; foreach (int cand in bAdj[cur]) { long k2 = cand < cur ? (long)cand * 4000000L + cur : (long)cur * 4000000L + cand; if (usedEdge.Contains(k2)) continue; next = cand; usedEdge.Add(k2); break; }
                    if (next < 0) break; verts.Add(next); prev = cur; cur = next;
                }
                loopVerts.Add(verts);
            }
            foreach (var verts in loopVerts)
            {
                var L = new Loop { edges = verts.Count - 1 }; float area2 = 0, per = 0; var cen = Vector3.zero; var bb = new Bounds(pos[verts[0]], Vector3.zero);
                for (int i = 0; i < verts.Count - 1; i++) { var p = pos[verts[i]]; var q = pos[verts[i + 1]]; area2 += p.x * q.z - q.x * p.z; per += Vector3.Distance(p, q); cen += p; bb.Encapsulate(p); }
                L.areaXZ = Mathf.Abs(area2) * 0.5f; L.perimeter = per; L.centroid = cen / Mathf.Max(1, verts.Count - 1); L.bounds = bb;
                L.component = find(verts[0]); rawLoops.Add(L);
            }
            foreach (var g in rawLoops.GroupBy(l => l.component))
            {
                var ordered = g.OrderByDescending(l => l.areaXZ).ToList();
                for (int i = 0; i < ordered.Count; i++) { ordered[i].outer = i == 0; st.loops.Add(ordered[i]); }
            }
            return st;
        }

        public static string Line(Stats s)
        {
            return string.Join(",", new[] { Common.Safe(s.name), s.rawVerts.ToString(), s.weldedVerts.ToString(), s.tris.ToString(), s.components.ToString(), s.boundaryEdges.ToString(), s.loops.Count(l => !l.outer).ToString(), s.nonManifoldEdges.ToString(), s.inconsistentEdges.ToString(), s.degenerate.ToString(), s.duplicateTris.ToString(), s.downFacing.ToString(), Common.F(s.areaTotal), Common.F(s.areaDown), Common.F(s.areaSteep), s.unusedVerts.ToString(), Common.F(s.bounds.min.x), Common.F(s.bounds.min.y), Common.F(s.bounds.min.z), Common.F(s.bounds.max.x), Common.F(s.bounds.max.y), Common.F(s.bounds.max.z) });
        }
        public const string Header = "name,rawVerts,weldedVerts,tris,components,boundaryEdges,innerLoops,nonManifoldEdges,sameDirectionEdges,degenerateTris,duplicateTris,downFacingTris,areaM2,downFacingAreaM2,steeperThanAgentSlopeAreaM2,unusedVerts,minX,minY,minZ,maxX,maxY,maxZ";

        public static void Run(string dir, List<Common.WorldMesh> renderMeshes, List<Common.WorldMesh> colliderMeshes, float agentSlope, List<string> summary)
        {
            var rows = new List<string>(); var holes = new List<string>();
            foreach (var m in renderMeshes.Concat(colliderMeshes)) rows.Add(Line(Analyse(m.source + ":" + m.path, new[] { m }, agentSlope)));
            Common.WriteCsv(dir, "D2_topology_per_mesh.csv", Header, rows);
            var tinOnly = renderMeshes.Where(m => m.cls == GeoClass.TinGround || m.cls == GeoClass.GapPatch).ToList();
            var tinCol = colliderMeshes.Where(m => m.cls == GeoClass.TinGround || m.cls == GeoClass.GapPatch).ToList();
            var sets = new List<KeyValuePair<string, List<Common.WorldMesh>>> {
                new KeyValuePair<string, List<Common.WorldMesh>>("ALL_TIN_render", tinOnly),
                new KeyValuePair<string, List<Common.WorldMesh>>("ALL_TIN_collider", tinCol),
                new KeyValuePair<string, List<Common.WorldMesh>>("GRID_TIN_render", tinOnly.Where(m => m.region == "Grid Area").ToList()),
                new KeyValuePair<string, List<Common.WorldMesh>>("ORGANIC_TIN_render", tinOnly.Where(m => m.region == "Organic Area").ToList()),
                new KeyValuePair<string, List<Common.WorldMesh>>("PRACTICE_TIN_render", tinOnly.Where(m => m.region == "Practice Area").ToList()) };
            var combined = new List<string>();
            foreach (var kv in sets)
            {
                if (kv.Value.Count == 0) continue;
                var st = Analyse(kv.Key, kv.Value, agentSlope); combined.Add(Line(st));
                summary.Add(kv.Key + ": meshes=" + kv.Value.Count + " tris=" + st.tris + " weldedVerts=" + st.weldedVerts + " components=" + st.components + " boundaryEdges=" + st.boundaryEdges + " innerLoops=" + st.loops.Count(l => !l.outer) + " nonManifoldEdges=" + st.nonManifoldEdges + " degenerate=" + st.degenerate + " duplicateTris=" + st.duplicateTris + " downFacingTris=" + st.downFacing + " (area " + Common.F(st.areaDown) + " m2 of " + Common.F(st.areaTotal) + ") steeper-than-slope-area=" + Common.F(st.areaSteep) + " m2");
                int li = 0;
                foreach (var l in st.loops.OrderByDescending(x => x.areaXZ))
                    holes.Add(string.Join(",", new[] { kv.Key, (li++).ToString(), l.outer ? "outer" : "INNER(hole_candidate)", l.component.ToString(), l.edges.ToString(), Common.F(l.areaXZ), Common.F(l.perimeter), Common.F(l.centroid.x), Common.F(l.centroid.y), Common.F(l.centroid.z), Common.F(l.bounds.size.x), Common.F(l.bounds.size.z) }));
            }
            Common.WriteCsv(dir, "D2_topology_combined.csv", Header, combined);
            Common.WriteCsv(dir, "D2_boundary_loops.csv", "set,index,kind,componentRoot,edges,areaXZ_m2,perimeter_m,centroidX,centroidY,centroidZ,sizeX,sizeZ", holes);
        }
    }
}
