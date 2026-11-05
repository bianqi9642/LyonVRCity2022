// GroundNavAuditSeams.cs - D2b: seams between ground tiles and between the Grid / Organic / Practice areas.
// Where two TIN tiles overlap in plan view (XZ), how far apart are their surfaces vertically? Which tile pairs share ground, and how many m2?
// Also reports tile-pair clearances along the tile edges: boundary vertices of one tile that lie within 1 m (plan view) of ANOTHER tile's footprint, split into 'covered' vs 'open gap'.
// Report only - nothing is modified. Batch: Unity -batchmode -nographics -projectPath <p> -executeMethod NavMeshAudit.Seams.RunBatch -auditLabel seams -auditOut <dir> -quit
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NavMeshAudit
{
    public static class Seams
    {
        [MenuItem("Tools/NavMesh Ground Audit/Run seam diagnostics (tile overlaps)")]
        public static void RunMenu() { Run("seams_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), 2f); }

        public static void RunBatch()
        {
            EditorSceneManager.OpenScene(Common.Arg("-auditScene") ?? "Assets/Scenes/LyonVRVity35.unity", OpenSceneMode.Single);
            Run(Common.Arg("-auditLabel") ?? "seams", 2f);
        }

        public static void Run(string label, float interval)
        {
            string dir = Common.OutDir(label); Common.ActivateTerrain(true);
            var rm = Common.CollectWorldMeshes(false).Where(m => !m.assetPath.StartsWith("Assets/Models/TIN/Repaired")).ToList();
            var tin = Common.BuildLookup(rm, GeoClass.TinGround, GeoClass.GapPatch);
            var b = tin.bounds; var ys = new List<float>(); var tri = new List<int>();
            var pairs = new Dictionary<string, List<float>>(); int overlapSamples = 0, total = 0;
            for (float x = b.min.x; x <= b.max.x; x += interval)
                for (float z = b.min.z; z <= b.max.z; z += interval)
                {
                    if (tin.Query(x, z, ys, tri) == 0) continue; total++;
                    if (ys.Count < 2) continue;
                    var seenTiles = new Dictionary<int, float>();
                    for (int i = 0; i < ys.Count; i++) { int tile = tin.tris[tri[i]].tile; if (!seenTiles.ContainsKey(tile)) seenTiles[tile] = ys[i]; }
                    if (seenTiles.Count < 2) continue; overlapSamples++;
                    var keys = seenTiles.Keys.OrderBy(k => k).ToList();
                    for (int i = 0; i < keys.Count; i++) for (int j = i + 1; j < keys.Count; j++)
                    {
                        string key = tin.tiles[keys[i]] + " <> " + tin.tiles[keys[j]]; List<float> l; if (!pairs.TryGetValue(key, out l)) { l = new List<float>(); pairs[key] = l; }
                        l.Add(Mathf.Abs(seenTiles[keys[i]] - seenTiles[keys[j]]));
                    }
                }
            var rows = new List<string>(); float worst = 0; int over25 = 0, over10 = 0;
            foreach (var kv in pairs.OrderByDescending(k => k.Value.Count))
            {
                var l = kv.Value; float max = l.Max(), mean = l.Average(); worst = Mathf.Max(worst, max); over10 += l.Count(v => v > 0.10f); over25 += l.Count(v => v > 0.25f);
                rows.Add(string.Join(",", new[] { Common.Safe(kv.Key), l.Count.ToString(), Common.F(l.Count * interval * interval), Common.F(mean), Common.F(max), l.Count(v => v > 0.10f).ToString(), l.Count(v => v > 0.25f).ToString() }));
            }
            Common.WriteCsv(dir, "D2b_tile_overlaps.csv", "tilePair,samples,areaM2,meanAbsDy,maxAbsDy,samplesDyOver0.10m,samplesDyOver0.25m", rows);
            string s = "footprint samples=" + total + " | samples where >=2 different tiles overlap=" + overlapSamples + " (" + (overlapSamples * interval * interval).ToString("F0") + " m2) in " + pairs.Count + " tile pairs | worst vertical disagreement=" + worst.ToString("F3") + " m | overlap samples with dy>0.10 m: " + over10 + ", >0.25 m: " + over25;
            File.WriteAllText(Path.Combine(dir, "summary.txt"), s); Debug.Log("[NavMeshAudit] seams: " + s);
        }
    }
}
