// TerrainProbe.cs  (NOT compiled by the project: it lives under docs/, copy it to Assets/Editor/ of a SCRATCH copy of the project to use it)
// Replays recorded positions against the scene's colliders: Editor batch mode, e.g.
//   Unity -batchmode -nographics -projectPath <scratch project> -executeMethod TerrainProbe.Run -probeIn probe_in.csv -probeOut <dir> -quit
// probe_in.csv columns: kind,t,x,y,z,dx,dy,dz,recorded_hit,recorded_class,recorded_layer   (kind = gaze | rig | anchor; see compare.py for the outputs it reads).
// START-POINT AUDIT (docs/ground_alignment_start_points.md):  ... -executeMethod TerrainProbe.RunStarts -probeScene <scene> -probeOut <dir> [-probeActivate 0] -quit
//   writes <dir>/starts.csv: one row per ExperimentManager start / transition anchor (world position via the parent chain, the Ground surface below it, the AutoGroundAlign ray from base + 0.1 m,
//   an AutoGroundAlign settle simulation, slope / collider-vs-renderer checks) plus '#' comment lines describing the XR Origin / CharacterController / AutoGroundAlign host.
//   -probeActivate 0 keeps the terrain groups as saved (inactive); by default TIN_Textured and Practice Area are activated IN MEMORY ONLY so there is terrain to measure.
// Scratch-only Editor probe (not part of the project): opens the production scene and reports the terrain colliders, then replays recorded positions against them.
using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using System.Text;
using UnityEditor; using UnityEditor.SceneManagement; using UnityEngine;

public static class TerrainProbe
{
    static string Arg(string n) { var a = Environment.GetCommandLineArgs(); for (int i = 0; i < a.Length - 1; i++) if (a[i] == n) return a[i + 1]; return null; }
    static string PathOf(Transform t) { var s = t.name; for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s; return s; }


    // AutoGroundAlign replay: the component's own loop (Update, every frame): ray from base + 0.1 m, straight down, 3 m, mask Ground, default trigger handling (global setting), closest hit,
    // base.y = Lerp(base.y, hit.y + heightOffset(0), dt * smoothSpeed(10)). Fed with the recorded rig XZ path; the rig Y restarts from the recorded Y at the start and after each teleport (jump > 0.5 m).
    public static void RunSim()
    {
        string scene = Arg("-probeScene") ?? "Assets/Scenes/LyonVRVity35.unity", inp = Arg("-probeIn"), outDir = Arg("-probeOut");
        Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        if (Arg("-probeActivate") != "0") foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (tr.name == "TIN_Textured") tr.gameObject.SetActive(true);
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
        var inv2 = CultureInfo.InvariantCulture; var sb = new StringBuilder("t,x,z,y_recorded,teleport,y_before,hit,hit_y,hit_name,region,hit_layer,hit_trigger,y_after,gap_to_target,dy_step,step_dt,horiz_step_m\n");
        double tPrev = 0; Vector3 prev = default; float ySim = 0; bool first = true;
        foreach (var line in File.ReadAllLines(inp).Skip(1))
        {
            var f = line.Split(','); if (f[0] != "rig") continue;
            double t = double.Parse(f[1], inv2); var p = new Vector3(float.Parse(f[2], inv2), float.Parse(f[3], inv2), float.Parse(f[4], inv2));
            float dt = first ? 0.05f : (float)Math.Min(0.1, t - tPrev); float horiz = first ? 0f : new Vector2(p.x - prev.x, p.z - prev.z).magnitude;
            bool tele = first || horiz > 0.5f;
            if (tele) ySim = p.y;
            float yBefore = ySim; string hn = "", reg = "", hl = "", ht = ""; string hit = "0"; float hy = float.NaN;
            if (Physics.Raycast(new Vector3(p.x, ySim + 0.1f, p.z), Vector3.down, out var h, 3f, 1 << 3))
            {
                hit = "1"; hy = h.point.y; hn = h.collider.name.Split(' ')[0]; hl = h.collider.gameObject.layer.ToString(); ht = h.collider.isTrigger ? "1" : "0";
                for (var tr = h.collider.transform; tr != null; tr = tr.parent) { string n = tr.name; if (n == "Grid Area" || n == "Organic Area" || n == "Practice Area" || n == "PONT") { reg = n; break; } }
                if (reg == "" ) reg = h.collider.name.Contains("GapPatch") ? "Organic Area" : "other";
                ySim = Mathf.Lerp(ySim, hy, Mathf.Clamp01(dt * 10f));
            }
            sb.AppendLine(string.Join(",", new[] { f[1], f[2], f[4], f[3], tele ? "1" : "0", yBefore.ToString("F4", inv2), hit, float.IsNaN(hy) ? "" : hy.ToString("F4", inv2), hn, reg, hl, ht, ySim.ToString("F4", inv2), float.IsNaN(hy) ? "" : (hy - yBefore).ToString("F4", inv2), (ySim - yBefore).ToString("F5", inv2), dt.ToString("F4", inv2), horiz.ToString("F3", inv2) }));
            tPrev = t; prev = p; first = false;
        }
        File.WriteAllText(Path.Combine(outDir, "aga_sim.csv"), sb.ToString());
        Debug.Log("[TerrainProbe] sim done");
    }


    // Ground-surface overlap scan: where two DIFFERENT Ground colliders both lie under the same XZ within 3 m of each other vertically, AutoGroundAlign's "closest hit" could switch between them.
    public static void RunOverlap()
    {
        string scene = Arg("-probeScene") ?? "Assets/Scenes/LyonVRVity35.unity", outDir = Arg("-probeOut"); Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (tr.name == "TIN_Textured") tr.gameObject.SetActive(true);
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
        var cols = UnityEngine.Object.FindObjectsOfType<MeshCollider>().Where(c => c.gameObject.layer == 3 && c.enabled && c.sharedMesh != null).ToList();
        var rnd = new System.Random(5); var hits = new RaycastHit[32]; var inv = CultureInfo.InvariantCulture;
        string Kind(Collider c) { string n = c.name.Split(' ')[0]; return n.StartsWith("ID_") ? "bridge" : "tin"; }
        // category -> (points, points with |dy| > 1 cm, max |dy| under 3 m, max |dy| <= 0.5 m with |dy| > 1 cm)
        var cat = new Dictionary<string, double[]>(); int total = 0, withGround = 0; var pairStats = new Dictionary<string, List<Vector3>>();
        void Add(string k, float dy) { if (!cat.ContainsKey(k)) cat[k] = new double[] { 0, 0, 0, 0 }; var v = cat[k]; v[0]++; if (dy > 0.01f) v[1]++; if (dy > v[2]) v[2] = dy; if (dy > 0.01f && dy <= 0.5f && dy > v[3]) v[3] = dy; }
        foreach (var c in cols)
        {
            var bb = c.bounds; if (bb.size.x < 1) continue;
            for (int i = 0; i < 1500; i++)
            {
                var p = new Vector3(bb.min.x + (float)rnd.NextDouble() * bb.size.x, 1000f, bb.min.z + (float)rnd.NextDouble() * bb.size.z); total++;
                int n = Physics.RaycastNonAlloc(p, Vector3.down, hits, 2000f, 1 << 3, QueryTriggerInteraction.Ignore); if (n == 0) continue; withGround++;
                for (int a = 0; a < n; a++) for (int d = a + 1; d < n; d++)
                {
                    if (hits[a].collider == hits[d].collider) continue;
                    float dy = Mathf.Abs(hits[a].point.y - hits[d].point.y); if (dy >= 3f) continue;
                    string ka = Kind(hits[a].collider), kd = Kind(hits[d].collider);
                    string k = ka != kd ? "tin_vs_bridge" : (hits[a].collider.name.Split(' ')[0] == hits[d].collider.name.Split(' ')[0] ? ka + "_same_name_duplicate" : ka + "_vs_" + kd + "_different_objects");
                    Add(k, dy);
                    if (k == "tin_vs_tin_different_objects" && dy > 0.01f) { string key = string.Join("|", new[] { hits[a].collider.name.Split(' ')[0].Substring(0, 12), hits[d].collider.name.Split(' ')[0].Substring(0, 12) }.OrderBy(x => x)); if (!pairStats.ContainsKey(key)) pairStats[key] = new List<Vector3>(); pairStats[key].Add(new Vector3(hits[a].point.x, hits[a].point.z, dy)); }
                }
            }
        }
        var sb = new StringBuilder("sampled_points," + total + ",with_ground," + withGround + "\ncategory,pairs,pairs_dy_gt_1cm,max_dy_m,max_dy_m_when_between_1cm_and_50cm\n");
        foreach (var kv in cat) sb.AppendLine(kv.Key + "," + kv.Value[0] + "," + kv.Value[1] + "," + kv.Value[2].ToString("F4", inv) + "," + kv.Value[3].ToString("F4", inv));
        sb.AppendLine("tin_pair,n,x_min,x_max,z_min,z_max,dy_min,dy_median,dy_max");
        foreach (var kv in pairStats) { var v = kv.Value; var dys = v.Select(q => q.z).OrderBy(q => q).ToList(); sb.AppendLine(kv.Key + "," + v.Count + "," + v.Min(q => q.x).ToString("F0", inv) + "," + v.Max(q => q.x).ToString("F0", inv) + "," + v.Min(q => q.y).ToString("F0", inv) + "," + v.Max(q => q.y).ToString("F0", inv) + "," + dys[0].ToString("F3", inv) + "," + dys[dys.Count / 2].ToString("F3", inv) + "," + dys[dys.Count - 1].ToString("F3", inv)); }
        File.WriteAllText(Path.Combine(outDir, "overlap.csv"), sb.ToString());
    }


    // Start-point audit: every Transform ExperimentManager uses to place the participant (Part 1 start, Part 2 start / transition positions, Part 3 starts), its world position, the rig it is assigned to,
    // the surfaces under it and what the AutoGroundAlign ray (base + 0.1 m, 3 m, Ground) sees from the placed position.
    public static void RunStarts()
    {
        string scene = Arg("-probeScene") ?? "Assets/Scenes/LyonVRVity35.unity", outDir = Arg("-probeOut"); Directory.CreateDirectory(outDir);
        EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        if (Arg("-probeActivate") != "0") foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (tr.name == "TIN_Textured" || tr.name == "Practice Area") tr.gameObject.SetActive(true);   // in memory only, for the measurement
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
        var em = UnityEngine.Object.FindObjectOfType<ExperimentManager>(true); var inv = CultureInfo.InvariantCulture; var sb = new StringBuilder();
        string F(float v) => v.ToString("F4", inv);
        sb.AppendLine("# playerRig: " + (em.playerRig != null ? PathOf(em.playerRig) + " pos " + em.playerRig.position.ToString("F4") + " rot " + em.playerRig.eulerAngles.ToString("F2") + " scale " + em.playerRig.lossyScale.ToString("F3") : "null"));
        if (em.playerRig != null)
        {
            foreach (var c in em.playerRig.GetComponentsInChildren<Component>(true).Where(c => c is CharacterController || c is Unity.XR.CoreUtils.XROrigin || c is AutoGroundAlign || c is OVRCameraRig || c is Camera))
                sb.AppendLine("# rig component: " + c.GetType().Name + " on " + PathOf(c.transform) + " local " + c.transform.localPosition.ToString("F4") + " world " + c.transform.position.ToString("F4"));
            var cc = em.playerRig.GetComponent<CharacterController>(); if (cc != null) sb.AppendLine("# CharacterController: enabled " + cc.enabled + " center " + cc.center.ToString("F4") + " height " + cc.height + " radius " + cc.radius + " skinWidth " + cc.skinWidth + " stepOffset " + cc.stepOffset + " slopeLimit " + cc.slopeLimit + " layer " + cc.gameObject.layer + " bounds.min.y-base.y " + (cc.bounds.min.y - em.playerRig.position.y).ToString("F4"));
            foreach (var mb in em.playerRig.root.GetComponentsInChildren<MonoBehaviour>(true)) { string tn = mb.GetType().Name; if (tn.Contains("Move") || tn.Contains("Gravity") || tn.Contains("CharacterControllerDriver") || tn.Contains("Locomotion") || tn.Contains("Teleport")) sb.AppendLine("# locomotion component: " + tn + " on " + PathOf(mb.transform) + " enabled " + mb.enabled + " activeInHierarchy " + mb.gameObject.activeInHierarchy); }
            var xo = em.playerRig.GetComponentInChildren<Unity.XR.CoreUtils.XROrigin>(true); if (xo != null) sb.AppendLine("# XROrigin on " + PathOf(xo.transform) + " CameraYOffset " + xo.CameraYOffset + " mode " + xo.RequestedTrackingOriginMode + " offsetObject " + (xo.CameraFloorOffsetObject != null ? xo.CameraFloorOffsetObject.name + " localY " + xo.CameraFloorOffsetObject.transform.localPosition.y : "null"));
            var ag = UnityEngine.Object.FindObjectOfType<AutoGroundAlign>(true); sb.AppendLine("# AutoGroundAlign host: " + PathOf(ag.transform) + " sameAsPlayerRig=" + (ag.transform == em.playerRig) + " dist " + ag.raycastDistance + " mask " + ag.groundLayer.value + " heightOffset " + ag.heightOffset);
        }
        var items = new List<(string part, string label, Transform t)>();
        items.Add(("Part1", "part1StartPosition", em.part1StartPosition));
        for (int g = 0; g < (em.part2Groups?.Length ?? 0); g++)
        {
            var grp = em.part2Groups[g];
            for (int i = 0; i < (grp.startPositions?.Length ?? 0); i++) items.Add(("Part2", "group" + g + ".startPositions[" + i + "]", grp.startPositions[i]));
            for (int i = 0; i < (grp.teleportPositions?.Length ?? 0); i++) items.Add(("Part2-transition", "group" + g + ".teleportPositions[" + i + "]", grp.teleportPositions[i]));
        }
        items.Add(("Scene-initial", "playerRig as saved (idle, before any Part)", em.playerRig)); items.Add(("Part3", "part3StartGE", em.part3StartGE)); items.Add(("Part3", "part3StartGU", em.part3StartGU)); items.Add(("Part3", "part3StartOE", em.part3StartOE)); items.Add(("Part3", "part3StartOU", em.part3StartOU));
        sb.AppendLine("part,label,name,path,active,local_x,local_y,local_z,world_x,world_y,world_z,rot_y,parent_scale,all_surfaces_below(y@dist|name|layer),aga_ray_origin_y,aga_hit,aga_hit_y,aga_hit_name,aga_hit_layer,ground_hits_from_above(y|name),navmesh_y,navmesh_dist,sphere_collider,ground_y_exact,surface_notes");
        var hits = new RaycastHit[32];
        foreach (var (part, label, t) in items)
        {
            if (t == null) { sb.AppendLine(part + "," + label + ",NULL"); continue; }
            var w = t.position; var all = new List<string>(); var gnd = new List<string>();
            int n = Physics.RaycastNonAlloc(new Vector3(w.x, w.y + 30f, w.z), Vector3.down, hits, 80f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var h in hits.Take(n).OrderByDescending(h => h.point.y)) all.Add(F(h.point.y) + "@" + F(h.distance) + "|" + h.collider.name.Split(' ')[0] + "|L" + h.collider.gameObject.layer);
            n = Physics.RaycastNonAlloc(new Vector3(w.x, w.y + 30f, w.z), Vector3.down, hits, 80f, 1 << 3, QueryTriggerInteraction.Ignore);
            foreach (var h in hits.Take(n).OrderByDescending(h => h.point.y)) gnd.Add(F(h.point.y) + "|" + h.collider.name.Split(' ')[0]);
            float oy = w.y + 0.1f; string hit = "0", hy = "", hn = "", hl = "";
            if (Physics.Raycast(new Vector3(w.x, oy, w.z), Vector3.down, out var rh, 3f, 1 << 3)) { hit = "1"; hy = F(rh.point.y); hn = rh.collider.name.Split(' ')[0]; hl = rh.collider.gameObject.layer.ToString(); }
            string nv = "", nd = ""; if (UnityEngine.AI.NavMesh.SamplePosition(w, out var nh, 3f, UnityEngine.AI.NavMesh.AllAreas)) { nv = F(nh.position.y); nd = F(nh.distance); }
            string slope = ""; { float lo = 1e9f, hi = -1e9f, lo5 = 1e9f, hi5 = -1e9f;
                foreach (var (dx, dz, r) in new[] { (0f, 0f, 0), (0.1f, 0f, 0), (-0.1f, 0f, 0), (0f, 0.1f, 0), (0f, -0.1f, 0), (0.5f, 0f, 1), (-0.5f, 0f, 1), (0f, 0.5f, 1), (0f, -0.5f, 1) })
                    if (Physics.Raycast(new Vector3(w.x + dx, w.y + 30f, w.z + dz), Vector3.down, out var sh, 80f, 1 << 3, QueryTriggerInteraction.Ignore)) { if (r == 0) { lo = Mathf.Min(lo, sh.point.y); hi = Mathf.Max(hi, sh.point.y); } lo5 = Mathf.Min(lo5, sh.point.y); hi5 = Mathf.Max(hi5, sh.point.y); }
                slope = "range0.1m=" + (hi - lo).ToString("F4", inv) + " range0.5m=" + (hi5 - lo5).ToString("F4", inv); }
            string exact = ""; if (Physics.Raycast(new Vector3(w.x, w.y + 30f, w.z), Vector3.down, out var eh, 80f, 1 << 3, QueryTriggerInteraction.Ignore))
            {
                exact = eh.point.y.ToString("F6", inv); var mc = eh.collider as MeshCollider; var mr = eh.collider.GetComponentInChildren<MeshRenderer>(true);
                if (mr != null) slope += " collider-vs-renderer bounds centre dist=" + (mr.bounds.center - eh.collider.bounds.center).magnitude.ToString("F4", inv) + " size dist=" + (mr.bounds.size - eh.collider.bounds.size).magnitude.ToString("F4", inv);
            }
            float ysim = w.y; for (int k = 0; k < 400; k++) if (Physics.Raycast(new Vector3(w.x, ysim + 0.1f, w.z), Vector3.down, out var sr, 3f, 1 << 3)) ysim = Mathf.Lerp(ysim, sr.point.y, 0.2f);
            slope += " aga_settled_y=" + ysim.ToString("F4", inv) + " aga_move=" + (ysim - w.y).ToString("F4", inv);
            var sc = t.GetComponent<SphereCollider>();
            sb.AppendLine(string.Join(",", new[] { part, label, t.name, PathOf(t), t.gameObject.activeInHierarchy ? "1" : "0", F(t.localPosition.x), F(t.localPosition.y), F(t.localPosition.z), F(w.x), F(w.y), F(w.z), F(t.eulerAngles.y), t.parent != null ? t.parent.lossyScale.ToString("F3").Replace(",", ";") : "-", string.Join(" ; ", all), F(oy), hit, hy, hn, hl, string.Join(" ; ", gnd), nv, nd, sc != null ? "r=" + sc.radius : "", exact, slope }));
        }
        File.WriteAllText(Path.Combine(outDir, "starts.csv"), sb.ToString());
    }

    public static void Run()
    {
        string scene = Arg("-probeScene") ?? "Assets/Scenes/LyonVRVity35.unity", inp = Arg("-probeIn"), outDir = Arg("-probeOut");
        Directory.CreateDirectory(outDir);
        var sc = EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
        // The saved scene keeps both TIN_Textured groups inactive; the audited session evidently ran with them active (its gaze rays hit gml_* colliders). Activate them in memory only.
        if (Arg("-probeActivate") != "0") foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>(true)) if (tr.name == "TIN_Textured") tr.gameObject.SetActive(true);
        // Scratch-only: the Textured gltf variants do not import in this scratch project, so a collider without a mesh gets the geometry of the White variant with the same gml id (same tile, same triangulation).
        int patched = 0;
        foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
        {
            if (mc.sharedMesh != null || !mc.name.StartsWith("gml_")) continue;
            string id = mc.name.Split(' ')[0];
            var files = System.IO.Directory.GetFiles("Assets/Models/TIN/White", id + ".gltf", System.IO.SearchOption.AllDirectories);
            if (files.Length == 0) continue;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(files[0].Replace("\\", "/"))) if (o is Mesh m) { mc.sharedMesh = m; patched++; break; }
        }
        Debug.Log("[TerrainProbe] patched meshes: " + patched);
        foreach (var path in new[] { "Assets/Models/TIN/Textured/gml_4811c913-de34-43c1-ae8a-51304bcd7a24.gltf", "Assets/Models/TIN/White/gml_4811c913-de34-43c1-ae8a-51304bcd7a24.gltf", "Assets/Models/TIN/White/gml_198275d7-1842-4ff1-abc3-123f14b587d4.gltf" })
        { var all = AssetDatabase.LoadAllAssetsAtPath(path); Debug.Log("[TerrainProbe] assets at " + path + ": " + all.Length + " " + string.Join(",", all.Select(a => a.GetType().Name + ":" + a.name).Take(6)) + " exists=" + System.IO.File.Exists(path)); }
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
        var inv = new StringBuilder("name,path,layer,active,collider,mesh,boundsCenter,boundsSize,isTrigger\n");
        foreach (var mc in UnityEngine.Object.FindObjectsOfType<MeshCollider>(true))
        {
            string mp = mc.sharedMesh != null ? AssetDatabase.GetAssetPath(mc.sharedMesh) : "";
            string cat = mp.Contains("/TIN/") ? "TIN" : mp.Contains("/PONT/") ? "PONT" : mc.name.Contains("GapPatch") ? "GapPatch" : (mc.name.StartsWith("gml_") || mc.name.StartsWith("ID_")) ? "TIN?nomesh" : null;
            if (cat == null) continue;
            var b = mc.bounds;
            inv.AppendLine(string.Join(",", new[] { mc.name, PathOf(mc.transform), mc.gameObject.layer.ToString(), mc.gameObject.activeInHierarchy ? "1" : "0", cat + ":MeshCollider", Path.GetFileName(mp), b.center.ToString("F2").Replace(",", ";"), b.size.ToString("F2").Replace(",", ";"), mc.isTrigger ? "1" : "0" }));
        }
        File.WriteAllText(Path.Combine(outDir, "terrain_inventory.csv"), inv.ToString());
        // layer histogram of every collider in the scene (what a layer change could touch)
        var hist = new SortedDictionary<string, int>();
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>(true)) { string k = c.GetType().Name + ":L" + c.gameObject.layer; hist[k] = hist.ContainsKey(k) ? hist[k] + 1 : 1; }
        File.WriteAllLines(Path.Combine(outDir, "collider_layer_histogram.txt"), hist.Select(kv => kv.Key + "=" + kv.Value));
        // replay
        int ground = 1 << 3, roi = 123;
        var sb = new StringBuilder("kind,t,x,y,z,ground_hit,ground_y,ground_dist,ground_name,ground_layer,roi_hit,roi_name,roi_layer,roi_dist,recorded_hit,recorded_layer\n");
        var hits = new RaycastHit[16]; var inv2 = CultureInfo.InvariantCulture;
        foreach (var line in File.ReadAllLines(inp).Skip(1))
        {
            var f = line.Split(','); string kind = f[0]; double t = double.Parse(f[1], inv2);
            var p = new Vector3(float.Parse(f[2], inv2), float.Parse(f[3], inv2), float.Parse(f[4], inv2));
            string gh = "0", gy = "", gd = "", gn = "", gl = "", rh = "", rn = "", rl = "", rd = "";
            float dist = kind == "rig" ? 3f : 12f; Vector3 from = kind == "rig" ? p + Vector3.up * 0.1f : p;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, dist, ground, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; int bi = -1; for (int i = 0; i < n; i++) if (hits[i].distance < best) { best = hits[i].distance; bi = i; }
            if (bi >= 0) { gh = "1"; gy = hits[bi].point.y.ToString("F4", inv2); gd = best.ToString("F4", inv2); gn = hits[bi].collider.name; gl = hits[bi].collider.gameObject.layer.ToString(); }
            if (kind == "gaze")
            {
                var d = new Vector3(float.Parse(f[5], inv2), float.Parse(f[6], inv2), float.Parse(f[7], inv2));
                if (Physics.Raycast(p, d, out var h, 50f, roi)) { rh = "1"; rn = h.collider.name; rl = h.collider.gameObject.layer.ToString(); rd = h.distance.ToString("F3", inv2); } else rh = "0";
            }
            sb.AppendLine(string.Join(",", new[] { kind, f[1], f[2], f[3], f[4], gh, gy, gd, gn, gl, rh, rn, rl, rd, f[8], f[10] }));
        }
        File.WriteAllText(Path.Combine(outDir, "replay.csv"), sb.ToString());
        // what lies below the teleport destinations that have no Ground within 12 m: any collider, any layer, 80 m
        var sb2 = new StringBuilder("x,y,z,any_hit,name,layer,dist,point_y\n");
        foreach (var line in File.ReadAllLines(inp).Skip(1)) { var f = line.Split(','); if (f[0] != "anchor") continue; var p = new Vector3(float.Parse(f[2], inv2), float.Parse(f[3], inv2), float.Parse(f[4], inv2));
            if (Physics.Raycast(p, Vector3.down, out var h, 80f, ~0, QueryTriggerInteraction.Ignore)) sb2.AppendLine(string.Join(",", new[] { f[2], f[3], f[4], "1", h.collider.name.Split(' ')[0], h.collider.gameObject.layer.ToString(), h.distance.ToString("F2", inv2), h.point.y.ToString("F2", inv2) })); else sb2.AppendLine(string.Join(",", new[] { f[2], f[3], f[4], "0", "", "", "", "" })); }
        File.WriteAllText(Path.Combine(outDir, "anchors_any_layer.csv"), sb2.ToString());
        Debug.Log("[TerrainProbe] done " + scene);
    }
}
