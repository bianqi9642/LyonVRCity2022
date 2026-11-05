// GroundNavAuditRunner.cs - entry points. Menu: Tools > NavMesh Ground Audit > Run diagnostics (current scene state).
// Batch:  Unity -batchmode -nographics -projectPath <p> -executeMethod NavMeshAudit.Runner.RunBatch -auditLabel before -auditOut <dir> [-auditInterval 2] [-auditPractice 0|1] -quit
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
    public static class Runner
    {
        [MenuItem("Tools/NavMesh Ground Audit/Run diagnostics (D1-D5, current scene)")]
        public static void RunMenu() { Run("manual_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), 2f, false, true); EditorUtility.RevealInFinder(Common.OutDir("")); }

        public static void RunBatch()
        {
            string scene = Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity";
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            float iv = 2f; float.TryParse(Common.Arg("-auditInterval") ?? "2", System.Globalization.NumberStyles.Float, Common.Inv, out iv);
            Run(Common.Arg("-auditLabel") ?? "run", iv, Common.Arg("-auditPractice") == "1", false);
        }

        public static void Run(string label, float interval, bool includePractice, bool restoreActivation)
        {
            string dir = Common.OutDir(label); var summary = new List<string>(); summary.Add("label=" + label + " scene=" + EditorSceneManager.GetActiveScene().path + " time=" + DateTime.Now.ToString("s"));
            var changed = Common.ActivateTerrain(includePractice);
            try
            {
                var surface = UnityEngine.Object.FindObjectOfType<NavMeshSurface>();
                var bs = surface != null ? surface.GetBuildSettings() : NavMesh.GetSettingsByID(0);
                // D1
                var src = SourcesAudit.Run(dir, label); summary.Add("== D1 NavMesh sources =="); summary.AddRange(src.lines);
                // D2
                var rmAll = Common.CollectWorldMeshes(false); var cm = Common.CollectWorldMeshes(true);
                var rmProxy = rmAll.Where(m => m.assetPath.StartsWith("Assets/Models/TIN/Repaired")).ToList(); var rm = rmAll.Except(rmProxy).ToList();   // rm = the ORIGINAL visual meshes (ground truth footprint)
                summary.Add("== D2 ground mesh topology (world space, welded 1 cm) ==");
                TopologyAudit.Run(dir, rm, cm, bs.agentSlope, summary);
                if (rmProxy.Count > 0) { var ps = TopologyAudit.Analyse("NAV_PROXY_repaired", rmProxy, bs.agentSlope); summary.Add("NAV_PROXY_repaired: meshes=" + rmProxy.Count + " tris=" + ps.tris + " weldedVerts=" + ps.weldedVerts + " components=" + ps.components + " boundaryEdges=" + ps.boundaryEdges + " nonManifoldEdges=" + ps.nonManifoldEdges + " degenerate=" + ps.degenerate + " duplicateTris=" + ps.duplicateTris + " downFacingTris=" + ps.downFacing + " area=" + Common.F(ps.areaTotal) + " m2"); }
                // D3 / D4 / D5
                var tin = Common.BuildLookup(rm, GeoClass.TinGround, GeoClass.GapPatch); var br = Common.BuildLookup(rm, GeoClass.Bridge);
                var o = new CoverageAudit.Options { interval = interval, steepSlope = bs.agentSlope };
                summary.Add("== D3 current NavMesh triangulation vs ground footprint ==");
                var ns = CoverageAudit.NavTriangulation(tin, br, o.vertTol, dir, "D3_navmesh_islands.csv");
                summary.Add("NavMesh tris=" + ns.tris + " area=" + Common.F(ns.area) + " m2 | on-ground " + ns.onGround + " (" + Common.F(ns.areaOnGround) + " m2) | ELEVATED above ground (>" + o.vertTol + " m; rooftop candidates) " + ns.elevated + " (" + Common.F(ns.areaElevated) + " m2) | below ground " + ns.below + " (" + Common.F(ns.areaBelow) + " m2) | over bridge decks " + ns.bridge + " (" + Common.F(ns.areaBridge) + " m2) | outside any TIN/bridge footprint " + ns.offFootprint + " (" + Common.F(ns.areaOff) + " m2)");
                summary.Add("NavMesh ground-level polygons under building footprints: " + ns.underBuildingTris + " tris (" + Common.F(ns.underBuildingArea) + " m2), of which connected to the main street network: " + ns.underBuildingMainIsland + " tris (" + Common.F(ns.underBuildingMainArea) + " m2)");
                summary.Add("NavMesh connected islands=" + ns.islands + " (majority on ground: " + ns.islandsGround + ", majority elevated: " + ns.islandsElevated + "), largest island " + Common.F(ns.largestIslandArea) + " m2");
                summary.Add("== D3/D4 sampled coverage (interval " + interval + " m, vertical tolerance " + o.vertTol + " m, edge buffer " + o.edgeBuffer + " m) ==");
                var cov = CoverageAudit.Sample(tin, br, o, dir, "D3_D4_missing_samples.csv", "D3_candidate_hole_regions.csv");
                summary.Add("TIN footprint samples=" + cov.footprint + " | intentional SouthRiver gap patch (excluded)=" + cov.intentionalPatch + " | under buildings (excluded)=" + cov.underBuilding + " (of which WITH NavMesh = " + cov.navUnderBuilding + ") | denominator=" + cov.denominator);
                summary.Add("NavMesh present=" + cov.navOk + " (" + Pct(cov.navOk, cov.denominator) + ") | missing in agent-radius edge buffer=" + cov.edgeBuffer + " | missing as CANDIDATE HOLE=" + cov.navMissingHole + " in " + cov.holeRegions + " region(s) | coverage excl. edge buffer=" + Pct(cov.navOk, cov.navOk + cov.navMissingHole));
                summary.Add("Physical Ground collider present=" + cov.physOk + " (" + Pct(cov.physOk, cov.denominator) + ") | missing=" + cov.physMissing + " | NavMesh but NO physical ground=" + cov.navNoPhys + " | physical ground but NO NavMesh=" + cov.physNoNav);
                foreach (var kv in cov.perRegion) summary.Add("  region " + kv.Key + ": footprint=" + kv.Value[0] + " underBuilding=" + kv.Value[1] + " navOk=" + kv.Value[3] + " edgeBuffer=" + kv.Value[2] + " candidateHole=" + kv.Value[4] + " physOk=" + kv.Value[5] + " physMissing=" + kv.Value[6] + " navButNoPhys=" + kv.Value[7]);
                CharacterProbe.Run(tin, dir, summary, 150, o.seed);
                NpcPointRegression.Run(dir, summary, 0.5f);
                summary.Add("== D5 connectivity ==");
                CoverageAudit.Connectivity(cov, o, dir, summary);
            }
            finally { if (restoreActivation) Common.Restore(changed); }
            File.WriteAllText(Path.Combine(dir, "summary.txt"), string.Join("\n", summary));
            Debug.Log("[NavMeshAudit] report written to " + dir + "\n" + string.Join("\n", summary));
        }
        static string Pct(int a, int b) { return b == 0 ? "n/a" : (100.0 * a / b).ToString("F2", Common.Inv) + "%"; }
    }
}
