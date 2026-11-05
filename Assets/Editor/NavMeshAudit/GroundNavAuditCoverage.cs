// GroundNavAuditCoverage.cs - D3 (NavMesh coverage), D4 (physical ground collision) and D5 (path connectivity).
// Ground truth for "where ground is expected" = the XZ footprint of the TIN render meshes (winding independent), with the elevation interpolated from the triangles.
// NavMesh is accepted at a sample only if a polygon lies directly under/over it within a vertical tolerance, so a roof polygon 15 m higher never counts as ground coverage.
// Physics is accepted only if a Ground-layer collider is hit within the same tolerance of the expected elevation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace NavMeshAudit
{
    public static class CoverageAudit
    {
        public class Options { public float interval = 2f, vertTol = 0.5f, horizTol = 0.12f, edgeBuffer = 0.75f, probeTop = 600f; public int seed = 12345; public int pairsPerClass = 40; public float steepSlope = 45f; }

        enum Cat { Ok, Hole, EdgeBuffer, UnderBuilding }

        static Dictionary<int, bool> isBuildingCache = new Dictionary<int, bool>();
        static bool IsBuilding(Collider c)
        {
            bool b; int id = c.GetInstanceID(); if (isBuildingCache.TryGetValue(id, out b)) return b;
            var mc = c as MeshCollider; b = mc != null && Common.Classify(mc.sharedMesh, c.transform) == GeoClass.Building; isBuildingCache[id] = b; return b;
        }
        static Dictionary<int, GeoClass> classCache = new Dictionary<int, GeoClass>();
        static GeoClass ClassOf(Collider c)
        {
            GeoClass g; int id = c.GetInstanceID(); if (classCache.TryGetValue(id, out g)) return g;
            var mc = c as MeshCollider; g = mc != null ? Common.Classify(mc.sharedMesh, c.transform) : GeoClass.Other; classCache[id] = g; return g;
        }

        // ------------------------------------------------------------------ NavMesh triangulation classification (rooftops, islands)
        public class NavStats { public int tris, onGround, elevated, below, bridge, offFootprint; public double area, areaOnGround, areaElevated, areaBelow, areaBridge, areaOff; public int islands, islandsElevated, islandsGround, underBuildingTris, underBuildingMainIsland; public double underBuildingArea, underBuildingMainArea; public double largestIslandArea; public List<string> islandRows = new List<string>(); }

        public static NavStats NavTriangulation(Common.GroundLookup tin, Common.GroundLookup bridge, float vertTol, string dir, string file)
        {
            var tr = NavMesh.CalculateTriangulation(); var st = new NavStats(); var ys = new List<float>();
            int T = tr.indices.Length / 3; st.tris = T;
            var parent = new int[T]; for (int i = 0; i < T; i++) parent[i] = i;
            Func<int, int> find = null; find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            var vmap = new Dictionary<Vector3Int, int>(); var cat = new int[T]; var area = new float[T]; var cen = new Vector3[T];
            for (int t = 0; t < T; t++)
            {
                var a = tr.vertices[tr.indices[3 * t]]; var b = tr.vertices[tr.indices[3 * t + 1]]; var c = tr.vertices[tr.indices[3 * t + 2]];
                float ar = 0.5f * Vector3.Cross(b - a, c - a).magnitude; var ce = (a + b + c) / 3f; area[t] = ar; cen[t] = ce; st.area += ar;
                int k = 3; // 0 ground, 1 elevated, 2 below, 3 off footprint, 4 bridge
                bool onDeck = false;
                if (bridge != null && bridge.Query(ce.x, ce.z, ys, null) > 0) foreach (float y in ys) if (Mathf.Abs(ce.y - y) <= vertTol) onDeck = true;   // deliberate bridge deck (PONT), not a rooftop
                if (tin.Query(ce.x, ce.z, ys, null) > 0)
                {
                    float best = float.MaxValue; foreach (float y in ys) { float d = ce.y - y; if (Mathf.Abs(d) < Mathf.Abs(best)) best = d; }
                    if (Mathf.Abs(best) > vertTol && !onDeck)
                    {
                        // large polygons on steep terrain deviate from the TIN at the centroid; only call it a rooftop / below-ground polygon when ALL three corners are off the ground
                        float minAbove = float.MaxValue, maxAbove = float.MinValue;
                        for (int j = 0; j < 3; j++) { var q = tr.vertices[tr.indices[3 * t + j]]; float bd = float.MaxValue; if (tin.Query(q.x, q.z, ys, null) > 0) { foreach (float y in ys) { float d = q.y - y; if (Mathf.Abs(d) < Mathf.Abs(bd)) bd = d; } } else bd = 0f; minAbove = Mathf.Min(minAbove, bd); maxAbove = Mathf.Max(maxAbove, bd); }
                        if (minAbove > vertTol) best = minAbove; else if (maxAbove < -vertTol) best = maxAbove; else best = 0f;
                    }
                    k = Mathf.Abs(best) <= vertTol ? 0 : (onDeck ? 4 : (best > 0 ? 1 : 2));
                }
                else if (onDeck) k = 4;
                cat[t] = k;
                switch (k) { case 0: st.onGround++; st.areaOnGround += ar; break; case 1: st.elevated++; st.areaElevated += ar; break; case 2: st.below++; st.areaBelow += ar; break; case 4: st.bridge++; st.areaBridge += ar; break; default: st.offFootprint++; st.areaOff += ar; break; }
                for (int j = 0; j < 3; j++)
                {
                    var q = Common.Quant(tr.vertices[tr.indices[3 * t + j]], 0.05f); int other;
                    if (vmap.TryGetValue(q, out other)) parent[find(t)] = find(other); else vmap[q] = t;
                }
            }
            var groups = new Dictionary<int, List<int>>(); for (int t = 0; t < T; t++) { int r = find(t); List<int> l; if (!groups.TryGetValue(r, out l)) { l = new List<int>(); groups[r] = l; } l.Add(t); }
            st.islands = groups.Count; var rows = new List<string>(); int id = 0;
            foreach (var g in groups.Values.OrderByDescending(l => l.Sum(t => (double)area[t])))
            {
                double a = g.Sum(t => (double)area[t]); if (id == 0) st.largestIslandArea = a;
                var cc = new int[5]; foreach (int t in g) cc[cat[t]]++; int maj = Array.IndexOf(cc, cc.Max()); string[] nm = { "ground", "ELEVATED(roof?)", "below", "off_footprint", "bridge" };
                if (maj == 1) st.islandsElevated++; if (maj == 0) st.islandsGround++;
                var bb = new Bounds(cen[g[0]], Vector3.zero); foreach (int t in g) bb.Encapsulate(cen[t]);
                if (id < 3000) rows.Add(string.Join(",", new[] { id.ToString(), g.Count.ToString(), Common.F(a), nm[maj], Common.F(bb.center.x), Common.F(bb.center.y), Common.F(bb.center.z), Common.F(bb.size.x), Common.F(bb.size.y), Common.F(bb.size.z) }));
                id++;
            }
            // polygons under a building footprint: are they connected to the main street network (leak through walls) or isolated interior islands?
            int mainRoot = groups.OrderByDescending(kv => kv.Value.Count).First().Key; var hb = new RaycastHit[32]; int bmask = ~(1 << 2);
            for (int t = 0; t < T; t++)
            {
                var ce = cen[t]; if (cat[t] != 0 && cat[t] != 2) continue;     // ground-level polygons only (roof polygons are counted as rooftop)
                int n = Physics.RaycastNonAlloc(new Vector3(ce.x, 600f, ce.z), Vector3.down, hb, 1200f, bmask, QueryTriggerInteraction.Ignore); bool under = false;
                for (int h = 0; h < n; h++) if (IsBuilding(hb[h].collider) && hb[h].point.y > ce.y - 0.3f) { under = true; break; }
                if (!under) continue; st.underBuildingTris++; st.underBuildingArea += area[t]; if (find(t) == mainRoot) { st.underBuildingMainIsland++; st.underBuildingMainArea += area[t]; }
            }
            Common.WriteCsv(dir, file, "island,tris,areaM2,majorityCategory,centerX,centerY,centerZ,sizeX,sizeY,sizeZ", rows);
            return st;
        }

        // ------------------------------------------------------------------ sampled coverage
        public class CovResult { public int footprint, intentionalPatch, underBuilding, navUnderBuilding, edgeBuffer, navOk, navMissingHole, physOk, physMissing, navNoPhys, physNoNav, physMissingInDenominator; public double interval; public int denominator; public List<string> regionLines = new List<string>(); public Dictionary<string, int[]> perRegion = new Dictionary<string, int[]>(); public Dictionary<string, List<Vector3>> okPoints = new Dictionary<string, List<Vector3>>(); public int holeRegions; }

        public static CovResult Sample(Common.GroundLookup tin, Common.GroundLookup bridge, Options o, string dir, string file, string holeFile)
        {
            var res = new CovResult { interval = o.interval };
            var b = tin.bounds; var ys = new List<float>(); var tri = new List<int>(); var hits = new RaycastHit[64]; var rnd = new System.Random(o.seed);
            int mask = ~(1 << 2); var rows = new List<string>(); var missing = new Dictionary<long, Vector3>(); var missInfo = new Dictionary<long, string>();
            int nx = Mathf.CeilToInt(b.size.x / o.interval), nz = Mathf.CeilToInt(b.size.z / o.interval);
            Func<float, float, bool> nearBlocked = (px, pz) =>
            {
                // probe 8 directions at edgeBuffer: blocked if no ground footprint or a building above
                for (int d = 0; d < 8; d++)
                {
                    float ang = d * Mathf.PI / 4f; float qx = px + Mathf.Cos(ang) * o.edgeBuffer, qz = pz + Mathf.Sin(ang) * o.edgeBuffer;
                    if (tin.Query(qx, qz, ys, null) == 0) return true;
                    float gy = ys[0];
                    int n = Physics.RaycastNonAlloc(new Vector3(qx, o.probeTop, qz), Vector3.down, hits, o.probeTop * 2, mask, QueryTriggerInteraction.Ignore);
                    for (int h = 0; h < n; h++) if (IsBuilding(hits[h].collider) && hits[h].point.y > gy - 0.3f) return true;
                }
                return false;
            };
            for (int ix = 0; ix <= nx; ix++)
                for (int iz = 0; iz <= nz; iz++)
                {
                    float x = b.min.x + ix * o.interval, z = b.min.z + iz * o.interval;
                    if (tin.Query(x, z, ys, tri) == 0) continue;
                    res.footprint++;
                    var eys = new List<float>(ys); var t0 = tin.tris[tri[0]]; string tile = tin.tiles[t0.tile]; string region = Common.Region(null == tile ? null : GameObject.Find(tile) ? GameObject.Find(tile).transform : null);
                    // region from tile path (cheap string test, avoids Find)
                    region = tile.StartsWith("Grid Area") ? "Grid Area" : tile.StartsWith("Organic Area") ? "Organic Area" : tile.StartsWith("Practice Area") ? "Practice Area" : "other";
                    int[] rc; if (!res.perRegion.TryGetValue(region, out rc)) { rc = new int[8]; res.perRegion[region] = rc; } // 0 footprint,1 underBuilding,2 edge,3 navOk,4 hole,5 physOk,6 physMissing(denominator),7 mismatch
                    rc[0]++;
                    int n = Physics.RaycastNonAlloc(new Vector3(x, o.probeTop, z), Vector3.down, hits, o.probeTop * 2, mask, QueryTriggerInteraction.Ignore);
                    bool physOk = false, underBuilding = false; float hitY = float.NaN, hitGround = float.NaN;
                    for (int h = 0; h < n; h++)
                    {
                        var col = hits[h].collider; var g = ClassOf(col);
                        if (g == GeoClass.Building) { if (hits[h].point.y > eys.Min() - 0.3f) underBuilding = true; continue; }   // roof OR street-level footprint plate (some BATI meshes have no up-facing roof collider)
                        if (col.gameObject.layer != Common.GroundLayer) continue;
                        foreach (float ey in eys) if (Mathf.Abs(hits[h].point.y - ey) <= o.vertTol) { physOk = true; hitY = hits[h].point.y; }
                    }
                    bool navOk = false; float navY = float.NaN;
                    foreach (float ey in eys)
                    {
                        NavMeshHit nh; if (NavMesh.SamplePosition(new Vector3(x, ey, z), out nh, o.vertTol + 0.1f, NavMesh.AllAreas) && Mathf.Abs(nh.position.x - x) <= o.horizTol && Mathf.Abs(nh.position.z - z) <= o.horizTol && Mathf.Abs(nh.position.y - ey) <= o.vertTol) { navOk = true; navY = nh.position.y; break; }
                    }
                    if (underBuilding) { res.underBuilding++; rc[1]++; if (navOk) res.navUnderBuilding++; continue; }
                    bool onlyPatch = true; foreach (int ti in tri) if (tin.tris[ti].cls != (int)GeoClass.GapPatch) { onlyPatch = false; break; }
                    if (onlyPatch) { res.intentionalPatch++; rc[1]++; continue; }   // SouthRiver gap patch: physical ground only, deliberately ignoreFromBuild for the NavMesh -> intentional non-walkable
                    res.denominator++;   // connectivity test points below are chosen from the TIN footprint, independent of the NavMesh, so before / after use the same pairs
                    if (unchecked((ix * 73856093) ^ (iz * 19349663)) % 61 == 0) { List<Vector3> cl; if (!res.okPoints.TryGetValue(region, out cl)) { cl = new List<Vector3>(); res.okPoints[region] = cl; } cl.Add(new Vector3(x, eys[0], z)); }
                    if (physOk) { res.physOk++; rc[5]++; } else { res.physMissing++; rc[6]++; }
                    if (navOk)
                    {
                        res.navOk++; rc[3]++; if (!physOk) { res.navNoPhys++; rc[7]++; rows.Add(Row("nav_no_physical_ground", x, z, eys[0], navY, hitY, tile, region, "")); }
                        continue;
                    }
                    if (physOk) { res.physNoNav++; }
                    bool edge = nearBlocked(x, z);
                    var tn = Vector3.Cross(t0.b - t0.a, t0.c - t0.a).normalized; string cause;
                    if (tn.y < -1e-3f) cause = "tin_triangle_faces_down"; else if (Vector3.Angle(tn, Vector3.up) > o.steepSlope) cause = "steeper_than_agent_slope";
                    else cause = Neighbourhood(tin, x, z, o, ys, tri, hits, mask, eys[0]);
                    cause += (physOk ? "" : "|no_physical_ground");
                    if (edge) { res.edgeBuffer++; rc[2]++; rows.Add(Row("nav_missing_edge_buffer", x, z, eys[0], navY, hitY, tile, region, cause)); }
                    else { res.navMissingHole++; rc[4]++; rows.Add(Row("NAV_MISSING_CANDIDATE_HOLE", x, z, eys[0], navY, hitY, tile, region, cause)); long key = ((long)ix << 32) ^ (uint)iz; missing[key] = new Vector3(x, eys[0], z); missInfo[key] = cause; }
                    if (!physOk && !edge) rows.Add(Row("PHYSICAL_GROUND_MISSING", x, z, eys[0], navY, hitY, tile, region, cause));
                }
            Common.WriteCsv(dir, file, "kind,x,z,expectedGroundY,navY,physicsHitY,tile,region,cause", rows);
            // cluster candidate holes (8-neighbourhood on the sampling grid)
            var seen = new HashSet<long>(); var regions = new List<string>(); int rid = 0;
            foreach (var kv in missing.ToList())
            {
                if (seen.Contains(kv.Key)) continue; var q = new Queue<long>(); q.Enqueue(kv.Key); seen.Add(kv.Key); var members = new List<long>();
                while (q.Count > 0)
                {
                    long k = q.Dequeue(); members.Add(k); int cx = (int)(k >> 32), cz = (int)(k & 0xffffffffL);
                    for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) { long nk = ((long)(cx + dx) << 32) ^ (uint)(cz + dz); if (missing.ContainsKey(nk) && seen.Add(nk)) q.Enqueue(nk); }
                }
                var bb = new Bounds(missing[members[0]], Vector3.zero); var cen = Vector3.zero; var causes = new Dictionary<string, int>();
                foreach (long m in members) { bb.Encapsulate(missing[m]); cen += missing[m]; int c; causes.TryGetValue(missInfo[m], out c); causes[missInfo[m]] = c + 1; }
                cen /= members.Count; string topCause = causes.OrderByDescending(c => c.Value).First().Key;
                regions.Add(string.Join(",", new[] { (rid++).ToString(), members.Count.ToString(), Common.F(members.Count * o.interval * o.interval), Common.F(cen.x), Common.F(cen.y), Common.F(cen.z), Common.F(bb.size.x), Common.F(bb.size.z), topCause }));
            }
            res.holeRegions = rid;
            Common.WriteCsv(dir, holeFile, "region,samples,approxAreaM2,centerX,centerY,centerZ,extentX,extentZ,dominantCause", regions.OrderByDescending(r => double.Parse(r.Split(',')[2], Common.Inv)));
            return res;
        }
        // ring probes at 1 m and 2 m around a missing sample: explains erosion next to steep slopes / buildings that the 0.75 m edge buffer does not cover
        static string Neighbourhood(Common.GroundLookup tin, float x, float z, Options o, List<float> ys, List<int> tri, RaycastHit[] hits, int mask, float gy)
        {
            bool steep = false, building = false, noGround = false;
            foreach (float rad in new[] { 1f, 2f })
                for (int d = 0; d < 8; d++)
                {
                    float ang = d * Mathf.PI / 4f; float qx = x + Mathf.Cos(ang) * rad, qz = z + Mathf.Sin(ang) * rad;
                    if (tin.Query(qx, qz, ys, tri) == 0) { noGround = true; continue; }
                    var tt = tin.tris[tri[0]]; var nn = Vector3.Cross(tt.b - tt.a, tt.c - tt.a).normalized; if (nn.y >= 0 && Vector3.Angle(nn, Vector3.up) > o.steepSlope) steep = true;
                    int n = Physics.RaycastNonAlloc(new Vector3(qx, o.probeTop, qz), Vector3.down, hits, o.probeTop * 2, mask, QueryTriggerInteraction.Ignore);
                    for (int h = 0; h < n; h++) if (IsBuilding(hits[h].collider) && hits[h].point.y > ys[0] - 0.3f) building = true;
                }
            return steep ? "steep_slope_within_2m" : building ? "building_within_2m" : noGround ? "terrain_edge_within_2m" : "unexplained";
        }
        static string Row(string kind, float x, float z, float ey, float navY, float hitY, string tile, string region, string cause) { return string.Join(",", new[] { kind, Common.F(x), Common.F(z), Common.F(ey), float.IsNaN(navY) ? "" : Common.F(navY), float.IsNaN(hitY) ? "" : Common.F(hitY), Common.Safe(tile), region, cause }); }

        // ------------------------------------------------------------------ connectivity
        public static void Connectivity(CovResult cov, Options o, string dir, List<string> summary)
        {
            var rnd = new System.Random(o.seed + 1); var rows = new List<string>(); var path = new NavMeshPath();
            Func<string, Vector3, Vector3, string, int> run = (kind, a, b, label) =>
            {
                NavMeshHit ha, hb; bool sa = NavMesh.SamplePosition(a, out ha, 3f, NavMesh.AllAreas), sb = NavMesh.SamplePosition(b, out hb, 3f, NavMesh.AllAreas);
                string status; float len = 0;
                if (!sa || !sb) status = !sa && !sb ? "no_navmesh_near_both" : (!sa ? "no_navmesh_near_A" : "no_navmesh_near_B");
                else { bool ok = NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path); status = path.status.ToString(); for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]); }
                rows.Add(string.Join(",", new[] { kind, Common.Safe(label), Common.F(a.x), Common.F(a.y), Common.F(a.z), Common.F(b.x), Common.F(b.y), Common.F(b.z), status, Common.F(len), Common.F(Vector3.Distance(a, b)) }));
                return status == "PathComplete" ? 1 : 0;
            };
            var counts = new Dictionary<string, int[]>();
            Action<string, List<Vector3>, List<Vector3>> pairs = (kind, A, B) =>
            {
                if (A == null || B == null || A.Count == 0 || B.Count == 0) return; int ok = 0, n = o.pairsPerClass;
                for (int i = 0; i < n; i++) ok += run(kind, A[rnd.Next(A.Count)], B[rnd.Next(B.Count)], "random");
                counts[kind] = new[] { ok, n };
            };
            List<Vector3> g, org; cov.okPoints.TryGetValue("Grid Area", out g); cov.okPoints.TryGetValue("Organic Area", out org);
            pairs("within_grid", g, g); pairs("within_organic", org, org); pairs("grid_to_organic", g, org);
            // experiment start / transition anchors (all pairs)
            string[] names = { "StartPoint", "StartPointO", "StartPointG", "StartPointGE", "StartPointGU", "StartPointOE", "StartPointOU" }; var anchors = new List<KeyValuePair<string, Vector3>>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (names.Contains(t.name) && Common.PathOf(t).Contains("EnvironmentConfig")) anchors.Add(new KeyValuePair<string, Vector3>(t.name, t.position));
            int ao = 0, an = 0; for (int i = 0; i < anchors.Count; i++) for (int j = i + 1; j < anchors.Count; j++) { ao += run("start_anchors", anchors[i].Value, anchors[j].Value, anchors[i].Key + "->" + anchors[j].Key); an++; }
            if (an > 0) counts["start_anchors"] = new[] { ao, an };
            // can every anchor be snapped to the NavMesh at all?
            foreach (var a in anchors) { NavMeshHit h; bool s = NavMesh.SamplePosition(a.Value, out h, 1.0f, NavMesh.AllAreas); summary.Add("anchor " + a.Key + " pos=" + a.Value.ToString("F2") + " navmesh within 1 m: " + s + (s ? " (dy " + Common.F(h.position.y - a.Value.y) + ")" : "")); }
            foreach (var kv in counts) summary.Add("connectivity " + kv.Key + ": PathComplete " + kv.Value[0] + "/" + kv.Value[1]);
            Common.WriteCsv(dir, "D5_connectivity.csv", "kind,label,ax,ay,az,bx,by,bz,pathStatus,pathLength,straightDistance", rows);
        }
    }
}

namespace NavMeshAudit
{
    /// <summary>D4b: what the REAL CharacterController (parameters copied from the scene's active XR rig) does over down-facing TIN triangles versus normal ones, and what AutoGroundAlign's ray sees there.</summary>
    public static class CharacterProbe
    {
        public static void Run(Common.GroundLookup tin, string dir, List<string> summary, int perClass, int seed)
        {
            CharacterController src = null;
            foreach (var cc in UnityEngine.Object.FindObjectsOfType<CharacterController>()) if (cc.gameObject.activeInHierarchy && Common.PathOf(cc.transform).Contains("XR Interaction Setup")) { src = cc; break; }
            float h = src ? src.height : 1.36144f, r = src ? src.radius : 0.2f, skin = src ? src.skinWidth : 0.08f, step = src ? src.stepOffset : 0.3f, slope = src ? src.slopeLimit : 45f; Vector3 center = src ? src.center : new Vector3(0, 0.7607f, 0);
            summary.Add("CharacterProbe CC params: height=" + h + " radius=" + r + " skin=" + skin + " step=" + step + " slopeLimit=" + slope + " center=" + center + (src ? " (read from " + Common.PathOf(src.transform) + ")" : " (defaults: no active rig found)"));
            var rnd = new System.Random(seed); var down = new List<int>(); var up = new List<int>();
            for (int i = 0; i < tin.tris.Count; i++)
            {
                var t = tin.tris[i]; if (t.cls != (int)GeoClass.TinGround) continue; var n = Vector3.Cross(t.b - t.a, t.c - t.a); float ar = 0.5f * n.magnitude; if (ar < 0.05f) continue;
                if (n.y < -1e-3f * n.magnitude) down.Add(i); else if (n.y / n.magnitude > 0.95f && rnd.NextDouble() < 0.002) up.Add(i);
            }
            var go = new GameObject("__AuditCC"); go.layer = 2; var c = go.AddComponent<CharacterController>(); c.height = h; c.radius = r; c.skinWidth = skin; c.stepOffset = step; c.slopeLimit = slope; c.center = center;
            var rows = new List<string>(); int[] landed = new int[2], tot = new int[2], rayHit = new int[2];
            Action<string, List<int>, int> test = (kind, list, k) =>
            {
                var chosen = list.OrderBy(x => rnd.Next()).Take(perClass).ToList();
                foreach (int ti in chosen)
                {
                    var t = tin.tris[ti]; var cen = (t.a + t.b + t.c) / 3f; var ys = new List<float>(); tin.Query(cen.x, cen.z, ys, null);
                    float gy = ys.Count > 0 ? ys.Max() : cen.y;     // any tile overlapping here counts as expected ground
                    go.transform.position = new Vector3(cen.x, gy + 0.4f, cen.z); Physics.SyncTransforms();
                    c.enabled = false; c.enabled = true;
                    float y0 = go.transform.position.y; for (int s = 0; s < 6; s++) c.Move(new Vector3(0, -0.5f, 0));
                    float y1 = go.transform.position.y; bool ok = y1 > gy - 0.25f; tot[k]++; if (ok) landed[k]++;
                    bool ray = Physics.Raycast(new Vector3(cen.x, gy + 0.1f, cen.z), Vector3.down, 3f, 1 << Common.GroundLayer, QueryTriggerInteraction.Ignore); if (ray) rayHit[k]++;
                    rows.Add(string.Join(",", new[] { kind, Common.F(cen.x), Common.F(gy), Common.F(cen.z), Common.F(y1), ok ? "CC_landed" : "CC_FELL_THROUGH", ray ? "AGA_ray_hit" : "AGA_ray_NO_HIT", Common.Safe(tin.tiles[t.tile]) }));
                }
            };
            test("down_facing_triangle", down, 0); test("normal_triangle_control", up, 1);
            UnityEngine.Object.DestroyImmediate(go);
            Common.WriteCsv(dir, "D4b_character_probe.csv", "kind,x,expectedGroundY,z,ccFinalY,result,autoGroundAlignRay,tile", rows);
            summary.Add("CharacterProbe down-facing triangles: " + landed[0] + "/" + tot[0] + " landed (CC), AutoGroundAlign ray hits " + rayHit[0] + "/" + tot[0] + " | control (up-facing): " + landed[1] + "/" + tot[1] + " landed, ray hits " + rayHit[1] + "/" + tot[1] + " | down-facing TIN triangles >=0.05 m2 available: " + down.Count);
        }
    }
}

namespace NavMeshAudit
{
    /// <summary>D6: every NPC route point the scene wires (EnvironmentConfig/Npc/**: StartPoint, EntranceExit/*, Wandering*) must still have NavMesh right under it.</summary>
    public static class NpcPointRegression
    {
        public static void Run(string dir, List<string> summary, float tol)
        {
            var rows = new List<string>(); int total = 0, ok = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                string path = Common.PathOf(t); if (!path.StartsWith("EnvironmentConfig/Npc/") || t.childCount != 0) continue;
                total++; NavMeshHit h; bool s = NavMesh.SamplePosition(t.position, out h, tol, NavMesh.AllAreas); if (s) ok++;
                rows.Add(string.Join(",", new[] { Common.Safe(path), Common.F(t.position.x), Common.F(t.position.y), Common.F(t.position.z), s ? "nav_within_tol" : "NO_NAVMESH", s ? Common.F(Vector3.Distance(h.position, t.position)) : "" }));
            }
            Common.WriteCsv(dir, "D6_npc_points.csv", "path,x,y,z,result,distanceToNavMesh", rows);
            summary.Add("D6 NPC route points (EnvironmentConfig/Npc/**) with NavMesh within " + tol + " m: " + ok + "/" + total);
        }
    }
}
