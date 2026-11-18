// MpRealSceneTests.cs  (EditMode, Editor-only; ISOLATED REVIEW CLONE ONLY)
// Tests against the REAL Ground-layer colliders of the experiment scene (opened additively, closed without saving; terrain groups activated in memory exactly like the existing
// StartPointGroundRealSceneTests), using positions recorded in the P3 Part 3 / P4 Part 3 sessions:
//   S1 P3 slit positions (8 x 101 lateral positions) - per-position, per-sample detail
//   S2 P4 quay-face positions - per-position, per-sample detail
//   S3 P3 replay: the recorded rig trajectory fed frame by frame (recorded timestamps) into the production Tick
//   S4 P4 replay: recorded frames until the first decision, plus a dynamic simulation (CharacterController + XRI-style gravity + the old AutoGroundAlign root snap)
//   S5 performance of the support evaluation
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using H = MpHarness;

[Category("MpRealScene")]
public class MpRealSceneTests
{
    const string ScenePath = "Assets/Scenes/LyonVRVity35.unity";
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    Scene _scene; bool _opened; readonly List<GameObject> _keep = new List<GameObject>();
    static string InputDir { get { return Environment.GetEnvironmentVariable("REVIEW_INPUT_DIR") ?? "/Users/qiaoy/Desktop/project/experiment/data_analysis/T2/analysis/outputs/ground_recovery_review/multipoint_comparison_20261118/inputs"; } }
    static string Out(string name) { Directory.CreateDirectory(H.ResultsDir); return Path.Combine(H.ResultsDir, name); }

    [OneTimeSetUp]
    public void Open()
    {
        // Safety guard (same rule as StartPointGroundRealSceneTests): never touch the user's open, possibly unsaved scene. In batch mode the scene is not open and the tests run as validated.
        if (!File.Exists(ScenePath)) Assert.Ignore("scene file not present: " + ScenePath);
        for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).path == ScenePath) Assert.Ignore("the experiment scene is open in the Editor; close it (or run in batch mode) so this test does not touch it");
        bool ignore = LogAssert.ignoreFailingMessages; LogAssert.ignoreFailingMessages = true;
        try { _scene = EditorSceneManager.OpenScene(ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive); _opened = true; } finally { LogAssert.ignoreFailingMessages = ignore; }
        foreach (var root in _scene.GetRootGameObjects()) foreach (var tr in root.GetComponentsInChildren<Transform>(true)) if (tr.name == "TIN_Textured" || tr.name == "Practice Area") tr.gameObject.SetActive(true);
        Physics.autoSyncTransforms = true; Physics.SyncTransforms();
    }
    [OneTimeTearDown] public void Close() { H.Flush("realscene"); if (_opened && _scene.IsValid()) EditorSceneManager.CloseScene(_scene, true); }
    [TearDown] public void Cleanup() { foreach (var g in _keep) if (g != null) UnityEngine.Object.DestroyImmediate(g); _keep.Clear(); }

    static string Tile(Transform t) { string s = t.name; for (var q = t.parent; q != null; q = q.parent) s = q.name + "/" + s; var i = s.IndexOf("gml_", StringComparison.Ordinal); return i >= 0 ? s.Substring(i, Math.Min(12, s.Length - i)) : s; }
    static readonly Vector2[] Offs = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) };
    static readonly string[] SName = { "c", "+x", "-x", "+z", "-z" };

    // independent per-sample probe (same ray as the component: 0.6 m above the rig base, 1.1 m long, Ground layer; walkable normal >= cos 45)
    static string SampleDetail(Vector3 root, Vector2 off, float spread, out bool supported, out float y)
    {
        Vector3 o = root + new Vector3(off.x * spread, 0, off.y * spread); supported = false; y = float.NaN;
        if (!Physics.Raycast(o + Vector3.up * 0.6f, Vector3.down, out RaycastHit h, 1.1f, 1 << 3, QueryTriggerInteraction.Ignore)) return "miss,,,";
        float dy = h.point.y - root.y; bool walk = h.normal.y >= Mathf.Cos(45f * Mathf.Deg2Rad); supported = walk && dy <= 0.5f && dy >= -0.5f; y = h.point.y;
        return "hit," + h.point.y.ToString("F4", CI) + "," + h.normal.y.ToString("F3", CI) + "," + Tile(h.collider.transform);
    }

    string PositionTable(string file, IEnumerable<(string id, Vector3 root)> positions, string label)
    {
        var rows = new StringBuilder("position_id,x,rig_base_y,z");
        foreach (var n in SName) rows.Append(",s_" + n + "_hit,s_" + n + "_y,s_" + n + "_normal_y,s_" + n + "_tile");
        rows.Append(",samples_supported,pair_x_bridged,pair_z_bridged");
        foreach (var m in H.Modes) rows.Append(",decision_" + H.ModeLabel(m));
        rows.Append('\n');
        var rigs = new Dictionary<string, H.RigHandle>(); foreach (var m in H.Modes) rigs[m] = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, m);
        int n0 = 0;
        foreach (var (id, root) in positions)
        {
            rows.Append(id).Append(',').Append(root.x.ToString("F4", CI)).Append(',').Append(root.y.ToString("F4", CI)).Append(',').Append(root.z.ToString("F4", CI));
            var ok = new bool[5];
            for (int i = 0; i < 5; i++) { rows.Append(',').Append(SampleDetail(root, Offs[i], 0.15f, out ok[i], out _)); }
            rows.Append(',').Append(ok.Count(b => b)).Append(',').Append(ok[1] && ok[2]).Append(',').Append(ok[3] && ok[4]);
            foreach (var m in H.Modes) rows.Append(',').Append(H.SupportAt(rigs[m], root, out _) ? "supported" : "unsupported");
            rows.Append('\n'); n0++;
        }
        File.WriteAllText(Out(file + "_" + H.Impl + ".csv"), rows.ToString());
        return n0 + " positions";
    }

    static (string, Vector3)[] SlitPositions()
    {
        var xs = new (float x, float zc, float y)[] { (2410f, 807.85f, 167.74f), (2416.005f, 807.93f, 167.686f), (2425f, 807.97f, 167.64f), (2434.55f, 808.08f, 167.56f), (2453.3f, 808.20f, 167.45f), (2472f, 808.36f, 167.35f), (2490.7f, 808.50f, 166.98f), (2500f, 808.57f, 166.9f) };
        var l = new List<(string, Vector3)>(); int xi = 0;
        foreach (var c in xs) { for (int k = -50; k <= 50; k++) l.Add(("x" + xi + "_u" + k, new Vector3(c.x, c.y, c.zc + k * 0.01f))); xi++; }
        return l.ToArray();
    }

    // ---------------------------------------------------------------- S1
    [Test]
    public void S1_P3SlitPositions_RealColliders()
    {
        var fails = new List<string>(); var pos = SlitPositions();
        PositionTable("p3_slit_positions", pos, "P3");
        foreach (var mode in H.Modes)
        {
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); int unsup = 0; var perX = new int[8]; int i = 0;
            foreach (var (id, p) in pos) { if (!H.SupportAt(rh, p, out _)) { unsup++; perX[i / 101]++; } i++; }
            string res = "unsupported=" + unsup + "/" + pos.Length + ";per_x=" + string.Join("|", perX);
            H.Rec("S1/P3 slit 8x101", "P3 street slit, real Ground colliders", "tiles gml_4d5046a5 / gml_29317c3e", "0.07-0.10", "1.0", "0", "808 positions (8 x, 101 lateral 1 cm steps)", mode, res, H.IsFinal(mode) ? "unsupported=0/" + pos.Length + ";per_x=0|0|0|0|0|0|0|0" : "recorded");
            if (H.IsFinal(mode) && unsup != 0) fails.Add("S1: " + res);
        }
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- S2
    [Test]
    public void S2_P4QuayFacePositions_RealColliders()
    {
        var fails = new List<string>(); var l = new List<(string, Vector3)>();
        for (float x = 2152.0f; x <= 2153.2f + 1e-4f; x += 0.1f) for (float y = 164.0f; y <= 166.3f + 1e-4f; y += 0.1f) for (float z = 497.6f; z <= 499.6f + 1e-4f; z += 0.5f)
        {
            if (!Physics.Raycast(new Vector3(x, 200f, z), Vector3.down, out RaycastHit top, 300f, 1 << 3) || top.normal.y >= 0.5f) continue;     // keep positions whose column-top surface is a steep face
            l.Add(("q" + l.Count, new Vector3(x, y, z)));
        }
        PositionTable("p4_quay_positions", l, "P4");
        foreach (var mode in H.Modes)
        {
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode); int sup = 0; foreach (var (id, p) in l) if (H.SupportAt(rh, p, out _)) sup++;
            string res = "supported=" + sup + "/" + l.Count;
            H.Rec("S2/P4 quay face", "P4 quay wall, real Ground colliders", "near-vertical face normal_y 0.08-0.09, plateau 167.0, lower level 162.2", "", "0.08", "0", l.Count + " positions on the steep face", mode, res, "supported=0/" + l.Count, "positions with the rig base on or within +-0.7 m of the face");
            H.Check(fails, "S2", mode, res, "supported=0/" + l.Count);
        }
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // ---------------------------------------------------------------- replay helpers
    class Frame { public float t, x, y, z, rotY, lx, ly, lz; public int epoch; }
    static List<Frame> Load(string name)
    {
        var l = new List<Frame>(); var lines = File.ReadAllLines(Path.Combine(InputDir, name));
        for (int i = 1; i < lines.Length; i++)
        {
            var p = lines[i].Split(','); if (p.Length < 13) continue;
            l.Add(new Frame { t = float.Parse(p[0], CI), epoch = int.Parse(p[2], CI), x = float.Parse(p[3], CI), y = float.Parse(p[4], CI), z = float.Parse(p[5], CI), rotY = float.Parse(p[6], CI), lx = float.Parse(p[10], CI), ly = float.Parse(p[11], CI), lz = float.Parse(p[12], CI) });
        }
        return l;
    }

    // ---------------------------------------------------------------- S3
    [Test]
    public void S3_P3Replay_RecordedFramesThroughTheProductionTick()
    {
        var fails = new List<string>(); var fr = Load("p3_replay_frames.csv"); const float CLOCK_OFFSET = 0.027f;   // log-minus-trajectory time of the same frame (median, measured on the P3 landings)
        foreach (var mode in H.Modes)
            foreach (bool head in new[] { false, true })
            {
                var rh = H.MakeRig(_keep, new Vector3(fr[0].x, fr[0].y, fr[0].z), Vector3.zero, 1.36f, mode); var ev = new StringBuilder("t_abs,epoch,reason,from_x,from_y,from_z,to_x,to_y,to_z\n"); int restores = 0; var times = new List<float>(); var epochs = new HashSet<int>();
                foreach (var f in fr)
                {
                    rh.go.transform.rotation = Quaternion.Euler(0, f.rotY, 0); rh.go.transform.position = new Vector3(f.x, f.y, f.z);
                    rh.cc.center = head ? new Vector3(f.lx, 1.36f / 2f + 0.08f, f.lz) : new Vector3(0, 1.36f / 2f + 0.08f, 0);
                    float t = f.t + CLOCK_OFFSET; rh.clock[0] = t; int c0 = H.Count(rh.c); Vector3 fed = rh.go.transform.position;
                    H.Tick(rh.c, t);
                    if (H.Count(rh.c) > c0) { restores++; epochs.Add(f.epoch); times.Add(t); var to = rh.go.transform.position; ev.Append(t.ToString("F4", CI)).Append(',').Append(f.epoch).Append(',').Append(H.Reason(rh.c)).Append(',').Append(fed.x.ToString("F3", CI)).Append(',').Append(fed.y.ToString("F3", CI)).Append(',').Append(fed.z.ToString("F3", CI)).Append(',').Append(to.x.ToString("F3", CI)).Append(',').Append(to.y.ToString("F3", CI)).Append(',').Append(to.z.ToString("F3", CI)).Append('\n'); }
                }
                string cap = head ? "head" : "root"; File.WriteAllText(Out("p3_replay_events_" + H.ModeLabel(mode) + "_capsule_" + cap + ".csv"), ev.ToString());
                string res = "restores=" + restores + ";teleports_undone=" + epochs.Count + ";first=" + (times.Count > 0 ? times[0].ToString("F3") : "-") + ";last=" + (times.Count > 0 ? times[times.Count - 1].ToString("F3") : "-");
                // the log has 23 void_displacement recoveries between 569.568 and 604.433 s
                string exp = H.IsFinal(mode) ? "restores=0;teleports_undone=0" : (mode == "baseline" && !head ? "teleports_undone=27 expected for the current logic (lab log: 23 undone by the recorded logic, 569.568-604.433 s)" : "recorded");
                H.Rec("S3/P3 replay capsule under " + cap, "replay of the recorded P3 Part 3 trajectory (8398 frames, 245 relocations) through the production Tick, real Ground colliders", "real scene", "", "", head ? "0.33 (recorded hmd_local)" : "0", "recorded timestamps + 0.027 s", mode, res, exp, "events: p3_replay_events_" + H.ModeLabel(mode) + "_capsule_" + cap + ".csv");
                if (H.IsFinal(mode) && restores != 0) fails.Add("S3 " + cap + ": " + res);
                UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
            }
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    // old AutoGroundAlign (commit 010be5bd, the recorded build): ray 0.1 m above the rig ROOT, 3 m down, Ground layer, y = Lerp(y, hit.y, dt * 10) (clamped to 1 at 9 FPS = snap)
    static void OldAutoGroundAlign(GameObject rig, CharacterController cc, float dt)
    {
        var p = rig.transform.position;
        if (Physics.Raycast(p + Vector3.up * 0.1f, Vector3.down, out RaycastHit hit, 3f, 1 << 3))
        {
            float y = Mathf.Lerp(p.y, hit.point.y, dt * 10f); bool on = cc.enabled; if (on) cc.enabled = false; rig.transform.position = new Vector3(p.x, y, p.z); if (on) cc.enabled = true; Physics.SyncTransforms();
        }
    }

    // ---------------------------------------------------------------- S4
    [Test]
    public void S4_P4Replay_RecordedFramesAndDynamicQuaySimulation()
    {
        var fails = new List<string>(); var fr = Load("p4_replay_frames.csv"); const float CLOCK_OFFSET = 0.027f;
        // (a) recorded frames until the first restore decision (no physics: the rig is placed at the recorded positions, so the decision time and target are comparable with the lab log: recovery at 1259.299 s)
        foreach (var mode in H.Modes)
            foreach (bool head in new[] { false, true })
            {
                var rh = H.MakeRig(_keep, new Vector3(fr[0].x, fr[0].y, fr[0].z), Vector3.zero, 1.36f, mode); string first = "none"; var csv = new StringBuilder("t_abs,rig_x,rig_y,rig_z,root_to_head_m,support,recoveries,reason,restored_x,restored_y,restored_z,safe_restore_target\n"); bool stop = false;
                foreach (var f in fr)
                {
                    if (stop) break;
                    rh.go.transform.rotation = Quaternion.Euler(0, f.rotY, 0); rh.go.transform.position = new Vector3(f.x, f.y, f.z);
                    rh.cc.center = head ? new Vector3(f.lx, 1.36f / 2f + 0.08f, f.lz) : new Vector3(0, 1.36f / 2f + 0.08f, 0);
                    float t = f.t + CLOCK_OFFSET; rh.clock[0] = t; int c0 = H.Count(rh.c); H.Tick(rh.c, t);
                    bool sup = rh.Support(new Vector3(f.x, f.y, f.z), out _); var np = rh.go.transform.position; bool restored = H.Count(rh.c) > c0;
                    bool safe = restored && Physics.Raycast(np + Vector3.up * 0.6f, Vector3.down, out RaycastHit hh, 1.1f, 1 << 3) && hh.normal.y > 0.7f;
                    csv.Append(f.t.ToString("F4", CI)).Append(',').Append(f.x.ToString("F3", CI)).Append(',').Append(f.y.ToString("F3", CI)).Append(',').Append(f.z.ToString("F3", CI)).Append(',').Append(Mathf.Sqrt(f.lx * f.lx + f.lz * f.lz).ToString("F3", CI)).Append(',').Append(sup ? "supported" : "unsupported").Append(',').Append(H.Count(rh.c)).Append(',').Append(restored ? H.Reason(rh.c) : "").Append(',').Append(restored ? np.x.ToString("F3", CI) : "").Append(',').Append(restored ? np.y.ToString("F3", CI) : "").Append(',').Append(restored ? np.z.ToString("F3", CI) : "").Append(',').Append(restored ? (safe ? "walkable ground" : "NOT walkable ground") : "").Append('\n');
                    if (restored) { first = H.Reason(rh.c) + "@" + t.ToString("F3") + " -> (" + np.x.ToString("F2") + "," + np.y.ToString("F2") + "," + np.z.ToString("F2") + ") " + (safe ? "walkable ground" : "NOT walkable"); stop = true; }
                }
                string cap = head ? "head" : "root"; File.WriteAllText(Out("p4_replay_frames_" + H.ModeLabel(mode) + "_capsule_" + cap + ".csv"), csv.ToString());
                H.Rec("S4a/P4 recorded frames capsule under " + cap, "replay of the recorded P4 Part 3 frames 1238.7-1262 s until the first decision (rig placed at the recorded positions, no physics, no AutoGroundAlign)", "real scene, quay", "", "0.08", head ? "recorded hmd_local (1.09 m at the fall)" : "0", "recorded timestamps + 0.027 s", mode, first, "first decision before 1259.3 s with a walkable restore target (lab recovery: fell_below_safe_y 1259.299 s into the face)", "frame csv: p4_replay_frames_" + H.ModeLabel(mode) + "_capsule_" + cap + ".csv");
                UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
            }
        // (b) dynamic simulation: real CharacterController + XRI-style vertical velocity + the OLD AutoGroundAlign root snap, driven by the recorded horizontal rig motion until the recorded fall onset (1257.51 s)
        int onset = fr.FindIndex(f => f.t >= 1257.51f);
        foreach (var mode in H.Modes)
            foreach (bool head in new[] { true, false })
                foreach (bool aga in new[] { true, false })
                {
                    var f0 = fr[0]; var rh = H.MakeRig(_keep, new Vector3(f0.x, f0.y, f0.z), Vector3.zero, 1.36f, mode); float vy = 0; var csv = new StringBuilder("t_abs,rec_rig_y,sim_rig_x,sim_rig_y,sim_rig_z,support,recoveries,reason\n");
                    int recov = 0; string reason = ""; float tFirst = float.NaN; float minY = 1e9f; Vector3 end = Vector3.zero; int framesAfter = 0; Vector3 firstTo = Vector3.zero;
                    rh.go.transform.position = new Vector3(f0.x, f0.y, f0.z); Physics.SyncTransforms();
                    for (int i = 1; i < fr.Count + 60; i++)
                    {
                        Frame prev = fr[Math.Min(i - 1, fr.Count - 1)], cur = fr[Math.Min(i, fr.Count - 1)]; float dt = i < fr.Count ? cur.t - prev.t : 0.11f; if (dt <= 0f || dt > 1f) dt = 0.11f;
                        // head offset as recorded
                        rh.go.transform.rotation = Quaternion.Euler(0, cur.rotY, 0); rh.cc.center = head ? new Vector3(cur.lx, 1.36f / 2f + 0.08f, cur.lz) : new Vector3(0, 1.36f / 2f + 0.08f, 0);
                        Vector3 horiz = (i <= onset && i < fr.Count && cur.epoch == prev.epoch) ? new Vector3(cur.x - prev.x, 0, cur.z - prev.z) : Vector3.zero;
                        if (i < fr.Count && cur.epoch != prev.epoch) { rh.cc.enabled = false; rh.go.transform.position = new Vector3(cur.x, cur.y, cur.z); rh.cc.enabled = true; Physics.SyncTransforms(); vy = 0; }
                        if (rh.cc.isGrounded) vy = 0; else vy += Physics.gravity.y * dt;
                        rh.cc.Move(horiz + Vector3.up * vy * dt);
                        if (aga) OldAutoGroundAlign(rh.go, rh.cc, dt);
                        float t = cur.t + CLOCK_OFFSET + (i >= fr.Count ? (i - fr.Count + 1) * 0.11f : 0f); rh.clock[0] = t; int c0 = H.Count(rh.c); Vector3 before = rh.go.transform.position; H.Tick(rh.c, t);
                        if (H.Count(rh.c) > c0) { recov++; reason = H.Reason(rh.c); if (float.IsNaN(tFirst)) { tFirst = t; firstTo = rh.go.transform.position; } }
                        var pp = rh.go.transform.position; minY = Mathf.Min(minY, pp.y); end = pp; if (i >= onset) framesAfter++;
                        csv.Append(cur.t.ToString("F4", CI)).Append(',').Append(cur.y.ToString("F3", CI)).Append(',').Append(pp.x.ToString("F3", CI)).Append(',').Append(pp.y.ToString("F3", CI)).Append(',').Append(pp.z.ToString("F3", CI)).Append(',').Append(rh.Support(pp, out _) ? "supported" : "unsupported").Append(',').Append(H.Count(rh.c)).Append(',').Append(H.Count(rh.c) > c0 ? H.Reason(rh.c) : "").Append('\n');
                        if (pp.y < -40f) break;
                    }
                    string cap = head ? "head" : "root"; string tag = H.ModeLabel(mode) + "_capsule_" + cap + (aga ? "_oldAGA" : "_noAGA"); File.WriteAllText(Out("p4_dynamic_" + tag + ".csv"), csv.ToString());
                    string res = "min_y=" + minY.ToString("F1") + ";end=(" + end.x.ToString("F2") + "," + end.y.ToString("F2") + "," + end.z.ToString("F2") + ");recoveries=" + recov + ";first=" + (float.IsNaN(tFirst) ? "-" : reason + "@" + tFirst.ToString("F2"));
                    H.Rec("S4b/P4 dynamic capsule under " + cap + (aga ? " + old AutoGroundAlign" : ", no AutoGroundAlign"), "dynamic simulation of the P4 approach to the quay edge (real CharacterController, XRI-style gravity, recorded horizontal motion, recorded offsets)", "real scene, quay", "", "0.08", head ? "recorded hmd_local" : "0", "frames of " + fr.Count + " + 60; vy starts 0; recorded timestamps", mode, res, "recorded: no assertion (the simulation does not reproduce the lab physics exactly)", "frame csv: p4_dynamic_" + tag + ".csv");
                    UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
                }
        Assert.IsEmpty(fails, string.Join("\n", fails));
    }

    static double AllocPerCall(Action step, int perRep = 200, int reps = 40)
    {
        var d = new List<double>();
        for (int r = 0; r < reps; r++)
        {
            int gc0 = GC.CollectionCount(0); long a0 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            for (int i = 0; i < perRep; i++) step();
            long a1 = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            if (GC.CollectionCount(0) == gc0 && a1 >= a0) d.Add((a1 - a0) / (double)perRep);
        }
        if (d.Count == 0) return double.NaN; d.Sort(); return d[d.Count / 2];
    }

    // ---------------------------------------------------------------- S5 performance
    [Test]
    public void S5_SupportEvaluationCost()
    {
        const int N = 20000;
        var cases = new (string name, Vector3 p)[] { ("centre supported (typical)", new Vector3(2416.005f, 167.686f, 807.888f)), ("centre in slit, side samples decide", new Vector3(2434.681f, 167.691f, 808.049f)), ("nothing below (all 5 samples miss)", new Vector3(2434.681f, 190f, 808.049f)) };
        foreach (var mode in H.Modes)
        {
            var rh = H.MakeRig(_keep, Vector3.zero, Vector3.zero, 1.36f, mode);
            foreach (var cs in cases)
            {
                rh.go.transform.position = cs.p; Physics.SyncTransforms(); for (int i = 0; i < 2000; i++) rh.Support(cs.p, out _);                    // warm-up
                GC.Collect(); var sw = Stopwatch.StartNew(); bool s = false;
                for (int i = 0; i < N; i++) s = rh.Support(cs.p, out _);
                sw.Stop(); double bytesPer = AllocPerCall(() => rh.Support(cs.p, out _));
                double usPer = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
                H.Rec("S5/" + cs.name, "support evaluation cost (Editor, Mono, real scene colliders, " + N + " calls)", "real scene", "", "", "0", cs.name, mode, "us_per_call=" + usPer.ToString("F2") + ";alloc_bytes_per_call=" + bytesPer.ToString("F0") + ";decision=" + (s ? "supported" : "unsupported"), "recorded", "Editor measurement, not a Quest Pro measurement", usPer / 1000.0);
            }
            // per-frame Tick cost with the rig standing on ground
            var p0 = cases[0].p; rh.go.transform.position = p0; Physics.SyncTransforms(); float tt = 0; for (int i = 0; i < 500; i++) { tt += 0.011f; rh.clock[0] = tt; H.Tick(rh.c, tt); }
            GC.Collect(); var sw2 = Stopwatch.StartNew(); for (int i = 0; i < 5000; i++) { tt += 0.011f; rh.clock[0] = tt; H.Tick(rh.c, tt); }
            sw2.Stop(); double tickBytes = AllocPerCall(() => { tt += 0.011f; rh.clock[0] = tt; H.Tick(rh.c, tt); });
            H.Rec("S5/Tick on supported ground", "per-frame Tick cost (Editor, 5000 frames)", "real scene", "", "", "0", "standing on ground", mode, "us_per_tick=" + (sw2.Elapsed.TotalMilliseconds * 1000.0 / 5000).ToString("F2") + ";alloc_bytes_per_tick=" + tickBytes.ToString("F0"), "recorded", "Editor measurement, not a Quest Pro measurement");
            UnityEngine.Object.DestroyImmediate(rh.go); _keep.Remove(rh.go);
        }
        Assert.Pass();
    }
}
