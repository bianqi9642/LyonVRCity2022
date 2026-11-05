// GroundNavAuditSources.cs - D1: which GameObjects feed the NavMesh build, and does any building / roof geometry still contribute WALKABLE area.
// Uses the real NavMeshSurface collection path (private CollectSources via reflection) so the report is what the baker would really consume.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace NavMeshAudit
{
    public static class SourcesAudit
    {
        public static List<NavMeshBuildSource> Collect(NavMeshSurface s)
        {
            var m = typeof(NavMeshSurface).GetMethod("CollectSources", BindingFlags.Instance | BindingFlags.NonPublic);
            return (List<NavMeshBuildSource>)m.Invoke(s, null);
        }

        public class Result { public int total, walkable, notWalkable; public Dictionary<string, int> byClassArea = new Dictionary<string, int>(); public int buildingWalkable; public List<string> lines = new List<string>(); }

        public static Result Run(string dir, string label)
        {
            var res = new Result();
            var surfaces = UnityEngine.Object.FindObjectsOfType<NavMeshSurface>();
            var rows = new List<string>(); var included = new HashSet<Component>();
            foreach (var s in surfaces)
            {
                foreach (var src in Collect(s))
                {
                    var comp = src.component; if (comp == null) continue; included.Add(comp);
                    Mesh mesh = src.sourceObject as Mesh;
                    var cls = Common.Classify(mesh, comp.transform);
                    string cname = cls.ToString();
                    bool walkableArea = src.area != Common.NotWalkableArea;
                    res.total++; if (walkableArea) res.walkable++; else res.notWalkable++;
                    string k = cname + (walkableArea ? " / area " + src.area : " / NOT WALKABLE"); int n; res.byClassArea.TryGetValue(k, out n); res.byClassArea[k] = n + 1;
                    if (cls == GeoClass.Building && walkableArea) res.buildingWalkable++;
                    var b = comp is Renderer ? ((Renderer)comp).bounds : (comp is Collider ? ((Collider)comp).bounds : new Bounds());
                    rows.Add(string.Join(",", new[] { "included", Common.Safe(Common.PathOf(comp.transform)), comp.gameObject.layer.ToString(), LayerMask.LayerToName(comp.gameObject.layer), comp.GetType().Name, src.shape.ToString(), Common.Safe(mesh ? mesh.name : ""), cname, src.area.ToString(), walkableArea ? "walkable" : "not_walkable", Common.F(b.min.y), Common.F(b.max.y), Common.F(b.size.x), Common.F(b.size.z), "surface=" + s.name }));
                }
            }
            // everything that renders but was NOT collected, with the reason that can be derived from the surface settings
            var s0 = surfaces.FirstOrDefault();
            foreach (var mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>(); if (mf == null || included.Contains(mr) || included.Contains(mf)) continue;
                string why = s0 != null && (s0.layerMask.value & (1 << mr.gameObject.layer)) == 0 ? "layer_not_in_include_mask" : (mr.enabled ? "ignoreFromBuild_or_other" : "renderer_disabled");
                var cls = Common.Classify(mf.sharedMesh, mf.transform); var b = mr.bounds;
                rows.Add(string.Join(",", new[] { "excluded:" + why, Common.Safe(Common.PathOf(mr.transform)), mr.gameObject.layer.ToString(), LayerMask.LayerToName(mr.gameObject.layer), "MeshFilter", "Mesh", Common.Safe(mf.sharedMesh ? mf.sharedMesh.name : ""), cls.ToString(), "", "", Common.F(b.min.y), Common.F(b.max.y), Common.F(b.size.x), Common.F(b.size.z), "" }));
            }
            Common.WriteCsv(dir, "D1_navmesh_sources.csv", "state,path,layer,layerName,component,shape,meshName,geoClass,area,walkability,yMin,yMax,sizeX,sizeZ,note", rows);

            var mods = new List<string>();
            foreach (var m in UnityEngine.Object.FindObjectsOfType<NavMeshModifier>(true))
                mods.Add(string.Join(",", new[] { Common.Safe(Common.PathOf(m.transform)), m.gameObject.layer.ToString(), m.isActiveAndEnabled.ToString(), m.ignoreFromBuild.ToString(), m.overrideArea.ToString(), m.area.ToString(), m.applyToChildren.ToString() }));
            Common.WriteCsv(dir, "D1_navmesh_modifiers.csv", "path,layer,activeAndEnabled,ignoreFromBuild,overrideArea,area,applyToChildren", mods);

            var sb = new StringBuilder();
            foreach (var s in surfaces)
                sb.AppendLine("surface " + Common.PathOf(s.transform) + ": collect=" + s.collectObjects + " useGeometry=" + s.useGeometry + " layerMask=" + s.layerMask.value + " defaultArea=" + s.defaultArea + " agentType=" + s.agentTypeID +
                    " overrideVoxel=" + s.overrideVoxelSize + " voxel=" + s.voxelSize + " overrideTile=" + s.overrideTileSize + " tile=" + s.tileSize + " minRegionArea=" + s.minRegionArea + " buildHeightMesh=" + s.buildHeightMesh + " data=" + (s.navMeshData ? UnityEditor.AssetDatabase.GetAssetPath(s.navMeshData) : "null"));
            foreach (var s in surfaces) { var bs = s.GetBuildSettings(); sb.AppendLine("buildSettings: radius=" + bs.agentRadius + " height=" + bs.agentHeight + " slope=" + bs.agentSlope + " climb=" + bs.agentClimb + " cell=" + (bs.overrideVoxelSize ? bs.voxelSize : bs.agentRadius / 3f) + " minRegion=" + bs.minRegionArea + " overrideTile=" + bs.overrideTileSize + " tile=" + bs.tileSize); }
            sb.AppendLine("sources collected: " + res.total + " walkable-area=" + res.walkable + " not-walkable-area=" + res.notWalkable);
            foreach (var kv in res.byClassArea.OrderBy(k => k.Key)) sb.AppendLine("  " + kv.Key + " = " + kv.Value);
            sb.AppendLine("BUILDING sources contributing WALKABLE area: " + res.buildingWalkable);
            res.lines.AddRange(sb.ToString().Split('\n').Select(x => x.TrimEnd('\r')));
            return res;
        }
    }
}
