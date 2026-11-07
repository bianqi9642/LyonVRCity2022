// GroundNavAuditSourceReport.cs - read-only report: which meshes the production NavMeshSurface really collects with the scene's modifiers EXACTLY as saved
// (no modifier is enabled, disabled or edited by this tool), and how many NavMesh polygons a fresh bake produces per region (PONT / Practice / Organic / Grid).
// The groups the scene saves inactive are switched on IN MEMORY only (nothing is saved); the fresh bake replaces the surface's data IN MEMORY only (nothing is saved, no asset is written).
// Batch: Unity -batchmode -nographics -projectPath <p> -executeMethod NavMeshAudit.SourceReport.RunBatch -auditLabel sources -auditOut <dir> -quit
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
    public static class SourceReport
    {
        static readonly string[] Regions = { "PONT", "Practice", "Organic", "Grid", "Other" };
        static readonly string[] BakeGroups = { "TIN_Textured", "Practice Area", "Grid Area", "Organic Area", "NavMesh", "EnvironmentConfig" };   // NavMeshGroundProxy is deliberately NOT activated: the report shows what the scene as saved collects

        [MenuItem("Tools/NavMesh Ground Audit/Run source collection report (modifiers as saved)")]
        public static void RunMenu() { Run("sources_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")); }

        public static void RunBatch()
        {
            EditorSceneManager.OpenScene(Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity", OpenSceneMode.Single);
            Run(Common.Arg("-auditLabel") ?? "sources");
        }

        static string RegionOfPath(string path, string meshName)
        {
            if (path.StartsWith("PONT/")) return "PONT";
            if (path.StartsWith("Practice Area/")) return "Practice";
            if (path.StartsWith("Organic Area/")) return "Organic";
            if (path.StartsWith("Grid Area/")) return "Grid";
            if (path.StartsWith("NavMeshGroundProxy/")) return meshName.Contains("_PracticeArea") ? "Practice" : meshName.Contains("_OrganicArea") ? "Organic" : meshName.Contains("_GridArea") ? "Grid" : "Other";
            return "Other";
        }

        static readonly List<string> RoofRows = new List<string>();
        static List<string> LastConnectivity = new List<string>();
        class Row { public int visualTin, proxies, bridges, buildings, buildingsWalkable, others; public long tris, visTris, proxyTris, bridgeTris; public int polys, polysElevated; public double polyArea; }

        public static void Run(string label)
        {
            string dir = Common.OutDir(label); var sum = new List<string>();
            sum.Add("scene=" + EditorSceneManager.GetActiveScene().path + " time=" + DateTime.Now.ToString("s") + "  (in-memory activation only; nothing saved; no modifier touched)");
            foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (BakeGroups.Contains(tr.name) && !tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
            Physics.SyncTransforms();
            var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>(); if (surface == null) { Debug.LogError("[SourceReport] no NavMeshSurface"); return; }
            string ug = Common.Arg("-auditUseGeometry");   // optional what-if: override Use Geometry IN MEMORY only (RenderMeshes | PhysicsColliders); never saved
            if (!string.IsNullOrEmpty(ug)) { surface.useGeometry = (NavMeshCollectGeometry)Enum.Parse(typeof(NavMeshCollectGeometry), ug); sum.Add("WHAT-IF: useGeometry overridden in memory -> " + surface.useGeometry); }

            // ---- modifiers exactly as saved
            sum.Add("== NavMeshModifier components (state as saved) ==");
            var mods = UnityEngine.Object.FindObjectsOfType<NavMeshModifier>(true).OrderBy(m => Common.PathOf(m.transform)).ToList();
            foreach (var m in mods) sum.Add((m.enabled ? "ENABLED  " : "DISABLED ") + Common.PathOf(m.transform) + " | ignoreFromBuild=" + m.ignoreFromBuild + " overrideArea=" + m.overrideArea + " area=" + m.area + " applyToChildren=" + m.applyToChildren);
            sum.Add("modifiers total=" + mods.Count + " disabled=" + mods.Count(m => !m.enabled) + " enabled=" + mods.Count(m => m.enabled));
            var enabledBefore = mods.Select(m => m.enabled).ToList();

            // ---- source collection
            var rows = Regions.ToDictionary(r => r, r => new Row()); var csv = new List<string>();
            foreach (var s in SourcesAudit.Collect(surface))
            {
                var comp = s.component; if (comp == null) continue; var mesh = s.sourceObject as Mesh; string path = Common.PathOf(comp.transform); string reg = RegionOfPath(path, mesh != null ? mesh.name : ""); var row = rows[reg];
                string ap = mesh != null ? AssetDatabase.GetAssetPath(mesh) : ""; string kind = mesh == null ? "primitive_" + s.shape : ap.StartsWith("Assets/Models/TIN/Repaired/") ? "repaired_proxy" : ap.Contains("/Models/TIN/Patches/") ? "gap_patch" : ap.Contains("/Models/TIN/") ? "visual_tin" : ap.Contains("/Models/PONT/") ? "bridge" : ap.Contains("/Models/BATI/") ? "building" : "other";
                int tris = mesh != null ? (int)(mesh.GetIndexCount(0) / 3) : 0; bool walk = s.area != Common.NotWalkableArea;
                switch (kind) { case "repaired_proxy": row.proxies++; row.proxyTris += tris; break; case "visual_tin": case "gap_patch": row.visualTin++; row.visTris += tris; break; case "bridge": row.bridges++; row.bridgeTris += tris; break; case "building": row.buildings++; if (walk) row.buildingsWalkable++; break; default: row.others++; break; }
                csv.Add(string.Join(",", new[] { reg, kind, Common.Safe(path), Common.Safe(mesh ? mesh.name : ""), tris.ToString(), walk ? "walkable" : "not_walkable" }));
            }
            Common.WriteCsv(dir, "sources_collected.csv", "region,kind,path,mesh,triangles,area", csv);

            // ---- polygons of the SAVED NavMeshData (as loaded) and of a FRESH bake (in memory)
            var rmAll = Common.CollectWorldMeshes(true);   // footprint = the Ground-layer collider meshes (repaired TIN + bridges): winding independent, identical XZ footprint to the visual TIN
            RoofRows.Clear(); var saved = Polygons(); Common.WriteCsv(dir, "roof_polygons_saved.csv", "region,x,y,z,cornerAboveGroundA,B,C,areaM2", RoofRows); string covSaved = Coverage("saved", rmAll, dir); var connSaved = new List<string>(LastConnectivity); sum.Add("== saved NavMeshData (" + (surface.navMeshData ? AssetDatabase.GetAssetPath(surface.navMeshData) : "none") + ") ==");
            sum.Add(Describe(saved));
            var sw = System.Diagnostics.Stopwatch.StartNew(); surface.BuildNavMesh(); sw.Stop();
            RoofRows.Clear(); var fresh = Polygons(); Common.WriteCsv(dir, "roof_polygons_fresh.csv", "region,x,y,z,cornerAboveGroundA,B,C,areaM2", RoofRows); string covFresh = Coverage("fresh", rmAll, dir); sum.Add("== FRESH bake with the modifiers as saved (" + sw.Elapsed.TotalSeconds.ToString("F0") + " s, in memory) ==");
            sum.Add(Describe(fresh));

            sum.Add("== source collection (modifiers as saved) vs fresh-bake polygons ==");
            sum.Add("| Region | visual TIN sources (tris) | repaired sources (tris) | bridge sources (tris) | building sources (walkable) | other sources | polygons saved asset | polygons fresh bake | roof polygons saved / fresh |");
            sum.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var r in Regions) { var row = rows[r]; sum.Add("| " + r + " | " + row.visualTin + " (" + row.visTris + ") | " + row.proxies + " (" + row.proxyTris + ") | " + row.bridges + " (" + row.bridgeTris + ") | " + row.buildings + " (" + row.buildingsWalkable + ") | " + row.others + " | " + saved[r].polys + " | " + fresh[r].polys + " | " + saved[r].polysElevated + " / " + fresh[r].polysElevated + " |"); }
            sum.Add("TOTAL building sources walkable=" + rows.Values.Sum(x => x.buildingsWalkable) + "; elevated (roof) polygons fresh=" + fresh.Values.Sum(x => x.polysElevated) + "; saved=" + saved.Values.Sum(x => x.polysElevated));

            sum.Add("== sampled coverage (TIN footprint 2 m grid; bridge decks 1 m grid) =="); sum.Add(covSaved); sum.AddRange(connSaved.Select(x => "  [saved] " + x)); sum.Add(covFresh); sum.AddRange(LastConnectivity.Select(x => "  [fresh] " + x));
            var enabledAfter = mods.Select(m => m.enabled).ToList();
            sum.Add("modifier enabled-states unchanged by this report: " + enabledBefore.SequenceEqual(enabledAfter));
            File.WriteAllText(Path.Combine(dir, "summary.txt"), string.Join("\n", sum)); Debug.Log("[SourceReport]\n" + string.Join("\n", sum));
        }

        static string Describe(Dictionary<string, Row> d) { return string.Join("\n", Regions.Select(r => "  " + r + ": polygons=" + d[r].polys + " area=" + d[r].polyArea.ToString("F0", Common.Inv) + " m2, elevated(roof)=" + d[r].polysElevated)); }

        // Triangulation of the currently loaded NavMesh, attributed to the region of the highest Ground-layer collider under the polygon centroid.
        // A polygon is a ROOF only if each of its three corners is > 1 m above the highest Ground-layer surface under THAT corner (a ray per corner: large polygons on steep slopes are not roofs).
        static Dictionary<string, Row> Polygons()
        {
            var res = Regions.ToDictionary(r => r, r => new Row()); var tr = NavMesh.CalculateTriangulation(); var hits = new RaycastHit[16];
            Func<Vector3, float> topAt = q =>
            {
                int k = Physics.RaycastNonAlloc(new Vector3(q.x, 600f, q.z), Vector3.down, hits, 1200f, 1 << Common.GroundLayer, QueryTriggerInteraction.Ignore);
                float top = float.NegativeInfinity; for (int h = 0; h < k; h++) top = Mathf.Max(top, hits[h].point.y); return top;
            };
            for (int t = 0; t < tr.indices.Length / 3; t++)
            {
                Vector3 a = tr.vertices[tr.indices[3 * t]], b = tr.vertices[tr.indices[3 * t + 1]], c = tr.vertices[tr.indices[3 * t + 2]]; var ce = (a + b + c) / 3f; float area = 0.5f * Vector3.Cross(b - a, c - a).magnitude;
                int n = Physics.RaycastNonAlloc(new Vector3(ce.x, 600f, ce.z), Vector3.down, hits, 1200f, 1 << Common.GroundLayer, QueryTriggerInteraction.Ignore);
                string reg = "Other"; float best = float.NegativeInfinity;
                for (int h = 0; h < n; h++) if (hits[h].point.y > best) { best = hits[h].point.y; reg = RegionOfPath(Common.PathOf(hits[h].collider.transform), hits[h].collider is MeshCollider mc && mc.sharedMesh != null ? mc.sharedMesh.name : ""); }
                if (n == 0) reg = "Other";
                float ta = topAt(a), tb = topAt(b), tc = topAt(c);
                bool roof = !float.IsNegativeInfinity(ta) && !float.IsNegativeInfinity(tb) && !float.IsNegativeInfinity(tc) && a.y > ta + 1f && b.y > tb + 1f && c.y > tc + 1f;
                var row = res[reg]; row.polys++; row.polyArea += area; if (roof) { row.polysElevated++; RoofRows.Add(string.Join(",", new[] { reg, Common.F(ce.x), Common.F(ce.y), Common.F(ce.z), Common.F(a.y - ta), Common.F(b.y - tb), Common.F(c.y - tc), Common.F(area) })); }
            }
            return res;
        }

        // Sampled NavMesh coverage of the TIN footprint (2 m grid) per area, using the same method as the earlier audit, plus bridge decks (1 m grid).
        static string Coverage(string label, List<Common.WorldMesh> rm, string dir)
        {
            var tin = Common.BuildLookup(rm, GeoClass.TinGround, GeoClass.GapPatch); var br = Common.BuildLookup(rm, GeoClass.Bridge);
            var o = new CoverageAudit.Options { interval = 2f, steepSlope = NavMesh.GetSettingsByID(0).agentSlope };
            var cov = CoverageAudit.Sample(tin, br, o, dir, "coverage_" + label + "_missing.csv", "coverage_" + label + "_holes.csv");
            var conn = new List<string>(); CoverageAudit.Connectivity(cov, o, dir, conn); NpcPointRegression.Run(dir, conn, 0.5f);
            if (File.Exists(Path.Combine(dir, "D5_connectivity.csv"))) File.Copy(Path.Combine(dir, "D5_connectivity.csv"), Path.Combine(dir, "connectivity_" + label + ".csv"), true);
            LastConnectivity = conn.Where(x => x.StartsWith("connectivity") || x.StartsWith("D6")).ToList();
            var sb = new StringBuilder("coverage " + label + ": ");
            foreach (var kv in cov.perRegion.OrderBy(k => k.Key)) { int ok = kv.Value[3], edge = kv.Value[2], hole = kv.Value[4]; sb.Append(kv.Key + " navPresent=" + ok + "/" + (ok + edge + hole) + " (" + (100.0 * ok / Math.Max(1, ok + edge + hole)).ToString("F2", Common.Inv) + "%, excl. wall-edge band " + (100.0 * ok / Math.Max(1, ok + hole)).ToString("F2", Common.Inv) + "%); ");
                if (kv.Key == "Grid Area" || kv.Key == "Organic Area" || kv.Key == "Practice Area") { }
            }
            // bridge decks
            int deck = 0, deckOk = 0; var ys = new List<float>(); var bb = br.bounds;
            for (float x = bb.min.x; x <= bb.max.x; x += 1f) for (float z = bb.min.z; z <= bb.max.z; z += 1f)
            {
                if (br.Query(x, z, ys, null) == 0) continue; deck++;
                foreach (float y in ys) { NavMeshHit nh; if (NavMesh.SamplePosition(new Vector3(x, y, z), out nh, 0.6f, NavMesh.AllAreas) && Mathf.Abs(nh.position.x - x) <= 0.12f && Mathf.Abs(nh.position.z - z) <= 0.12f && Mathf.Abs(nh.position.y - y) <= 0.5f) { deckOk++; break; } }
            }
            sb.Append("PONT decks navPresent=" + deckOk + "/" + deck + " (" + (100.0 * deckOk / Math.Max(1, deck)).ToString("F2", Common.Inv) + "%); physical-ground-missing=" + cov.physMissing + "; candidate-hole samples=" + cov.navMissingHole);
            return sb.ToString();
        }
    }
}
