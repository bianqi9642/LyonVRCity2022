// Scratch-only benchmark harness (NOT part of the repository). Runs the REAL SpawnerManager + PedestrianAgent + MoveToTarget + DataCaptureManager with the production Npc.prefab
// on a runtime NavMesh plane with a synthetic box "city", and records frame-time statistics.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public class FixedCounter : MonoBehaviour { public static int Count; void FixedUpdate() { Count++; } }

public partial class NpcLifecycleTests
{
    static readonly string[] BenchMarkers = {
        "Update.ScriptRunBehaviourUpdate", "Update.ScriptRunDelayedDynamicFrameRate", "FixedUpdate.PhysicsFixedUpdate", "FixedUpdate.ScriptRunBehaviourFixedUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate", "PreLateUpdate.DirectorUpdateAnimationBegin", "PreLateUpdate.DirectorUpdateAnimationEnd", "PreLateUpdate.AIUpdatePostScript",
        "PreUpdate.AIUpdate", "Update.DirectorUpdate", "PostLateUpdate.UpdateAllRenderers", "PostLateUpdate.PlayerUpdateCanvases", "EarlyUpdate.UpdatePreloading",
        "FixedUpdate.AIUpdate", "FixedUpdate.DirectorFixedUpdate", "Physics.Simulate", "Physics.SyncTransforms", "Physics.UpdateBodies", "Physics.Raycast"
    };

    [UnityTest, Timeout(7200000)]
    public IEnumerator Bench()
    {
        string runs = Environment.GetEnvironmentVariable("NPCBENCH_RUNS") ?? "100:B";
        string outPath = Environment.GetEnvironmentVariable("NPCBENCH_OUT") ?? "/tmp/npcbench.jsonl";
        float warm = float.Parse(Environment.GetEnvironmentVariable("NPCBENCH_WARM") ?? "8", CultureInfo.InvariantCulture);
        float meas = float.Parse(Environment.GetEnvironmentVariable("NPCBENCH_MEAS") ?? "20", CultureInfo.InvariantCulture);
        string tag = Environment.GetEnvironmentVariable("NPCBENCH_TAG") ?? "baseline";

        // list of available profiler marker names once (so unknown names can be corrected)
        var avail = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(avail);
        var names = new List<string>(); foreach (var h in avail) { var d = ProfilerRecorderHandle.GetDescription(h); string n = d.Name; if (n.IndexOf("Nav", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("AI", StringComparison.Ordinal) >= 0 || n.IndexOf("Animat", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0) names.Add(d.Category.Name + "|" + n); }
        File.WriteAllText(outPath + ".markers.txt", string.Join("\n", names.Distinct().OrderBy(x => x)));

        foreach (string run in runs.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = run.Split(':'); int n = int.Parse(kv[0]); string cfg = kv[1];
            float interval = 0.1f; if (kv.Length > 2) interval = float.Parse(kv[2], CultureInfo.InvariantCulture);
            var res = new Dictionary<string, object>(); res["tag"] = tag; res["npcs"] = n; res["cfg"] = cfg; res["interval"] = interval;
            yield return BenchOne(n, cfg, interval, warm, meas, res);
            File.AppendAllText(outPath, MiniJson(res) + "\n");
            // cleanup between runs
            foreach (var o in _spawned) if (o != null) UnityEngine.Object.Destroy(o);
            _spawned.Clear(); yield return null;
            foreach (var leftover in UnityEngine.Object.FindObjectsOfType(PedType)) UnityEngine.Object.Destroy(((Component)leftover).gameObject);
            yield return null; yield return null;
            if (_navInstance.valid) NavMesh.RemoveNavMeshData(_navInstance);
            CallStatic(BusType, "ResetForTests"); CallStatic(T("NpcSimulationGate"), "Reset");
            GC.Collect(); yield return null;
        }
    }

    IEnumerator BenchOne(int n, string cfg, float interval, float warm, float meas, Dictionary<string, object> res)
    {
        UnityEngine.Random.InitState(20261106);
        // participant target for the LOS query + synthetic city
        var eye = Make("CenterEyeAnchor"); eye.transform.position = new Vector3(-100f, 1.7f, -100f); if (Environment.GetEnvironmentVariable("NPCBENCH_PCOL") == "1") { var sc = eye.AddComponent<SphereCollider>(); sc.radius = 0.15f; }
        var city = Make("BenchCity"); var rnd = new System.Random(77);
        for (int i = 0; i < 2500; i++)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube); b.transform.SetParent(city.transform, false);
            b.transform.position = new Vector3((float)(rnd.NextDouble() * 220 - 110), 12f, (float)(rnd.NextDouble() * 220 - 110));
            b.transform.localScale = new Vector3(5f + (float)rnd.NextDouble() * 10f, 10f + (float)rnd.NextDouble() * 25f, 5f + (float)rnd.NextDouble() * 10f);
            b.isStatic = true;
        }
        var prefab = LoadNpcPrefab();
        var holder = Make("BenchHolder", active: false);
        var template = UnityEngine.Object.Instantiate(prefab, holder.transform); template.name = "NpcTemplate"; template.SetActive(false); template.transform.SetParent(null); _spawned.Add(template);
        var fcGo = Make("FixedCounterHost"); fcGo.AddComponent<FixedCounter>(); FixedCounter.Count = 0;

        Rig rig = null;
        var o = new RigOptions { prewarm = n, maxAgents = n, npcCaptureEnabled = cfg[0] != 'A', samplingInterval = interval, attractor = true, attractorCount = 8, spawnInterval = 0.002f, entranceRadius = 60f,
            npcSpeed = 0.6f, prewarmBudgetMs = 25f, templateOverride = template };
        yield return BuildRig(o, r => rig = r);
        double tBuild = Time.realtimeSinceStartupAsDouble;
        yield return WaitUntil(() => PoolReady(rig), 900f, "pool ready");
        res["pool_ready_s"] = Time.realtimeSinceStartupAsDouble - tBuild;
        yield return WaitUntil(() => Live(rig) >= n, 2400f, "live agents");
        res["spawn_complete_s"] = Time.realtimeSinceStartupAsDouble - tBuild;

        string abl = (cfg.Length > 1 && cfg[0] == 'A') ? cfg.Substring(1) : "";
        // warm-up
        float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < warm) yield return null;

        // measurement

        // ablation (measurement of marginal cost centres): applied after the warm-up, restored at the end
        bool physWas = Physics.autoSimulation;
        if (abl.Contains("anim")) foreach (var pa in ActiveAgents(rig)) foreach (var an in pa.GetComponentsInChildren<Animator>()) an.enabled = false;
        if (abl.Contains("phys")) Physics.autoSimulation = false;
        if (abl.Contains("move")) foreach (var pa in ActiveAgents(rig)) { var mt = pa.GetComponent(MoveType) as Behaviour; if (mt != null) mt.enabled = false; }
        if (abl.Contains("nav")) foreach (var pa in ActiveAgents(rig)) { var na = pa.GetComponent<NavMeshAgent>(); if (na != null) na.enabled = false; }
        if (abl.Contains("nolog")) foreach (var pa in ActiveAgents(rig)) { var mt = pa.GetComponent(MoveType); if (mt != null) Set(mt, "enableDebugLogs", false); var ub = pa.GetComponent(T("UncertainBehavior")); if (ub != null) Set(ub, "enableLogging", false); }
        if (abl.Contains("reiss")) foreach (var pa in ActiveAgents(rig)) { var mt = pa.GetComponent(MoveType); if (mt != null) Set(mt, "targetUpdateInterval", 1e9f); }
        if (abl.Contains("ped")) foreach (var pa in ActiveAgents(rig)) { var b = pa as Behaviour; if (b != null) b.enabled = false; }
        if (abl.Length > 0) { t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < 2f) yield return null; }
        if (Environment.GetEnvironmentVariable("NPCBENCH_PROFILER") == "1") { UnityEngine.Profiling.Profiler.enabled = true; yield return null; }
        var recs = new Dictionary<string, ProfilerRecorder>(); var perMarkerInit = new Dictionary<string, List<double>>();
        var handles = new List<ProfilerRecorderHandle>(); ProfilerRecorderHandle.GetAvailable(handles);
        var want = new HashSet<string>(BenchMarkers) { "Animators.Update", "Animation.Update", "Physics.Simulate", "Physics.SyncColliderTransform", "Physics.SyncRigidbodyTransform", "Physics.UpdateBodies", "Physics.FetchResults", "Physics.RaycastNonAlloc", "Physics.Raycast", "Components.NavMeshAgent.Transform", "Components.NavMeshAgent.State", "NavMeshManager", "Animators.ProcessAnimationsJob", "Animators.WriteJob", "Animators.PrepareFirstPass" };
        foreach (var h in handles) { var d = ProfilerRecorderHandle.GetDescription(h); string nm = d.Name; if ((d.Category.Name == "PlayerLoop" || want.Contains(nm)) && !recs.ContainsKey(nm)) recs[nm] = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately); }
        res["n_recorders"] = recs.Count;
        foreach (var nm in recs.Keys) { if (!perMarkerInit.ContainsKey(nm)) perMarkerInit[nm] = new List<double>(); }
        var gc = new ProfilerRecorder(); var gcc = new ProfilerRecorder();
        var dts = new List<double>(); var gcBytes = new List<double>(); var gcCount = new List<double>(); var fixedPerFrame = new List<int>();
        var perMarker = perMarkerInit; long validCount = 0; var mx = new Dictionary<string, double>(); long memPrev = GC.GetTotalMemory(false); double allocSum = 0;
        int collections0 = GC.CollectionCount(0), collections2 = GC.CollectionCount(2);
        DiagCall("Reset"); FixedCounter.Count = 0;
        var agentsList = new List<NavMeshAgent>();
        foreach (var pa in ActiveAgents(rig)) { var a = pa.GetComponent<NavMeshAgent>(); if (a != null) agentsList.Add(a); }
        long pending = 0, pendSamples = 0, onMesh = 0;
        float tm0 = Time.realtimeSinceStartup; int frame = 0; int lastFixed = 0;
        yield return null;
        while (Time.realtimeSinceStartup - tm0 < meas || dts.Count < 30)
        {
            yield return null;
            dts.Add(Time.unscaledDeltaTime * 1000.0);
            long memNow = GC.GetTotalMemory(false); if (memNow > memPrev) allocSum += memNow - memPrev; memPrev = memNow;
            foreach (var kv in recs) if (kv.Value.Valid && kv.Value.LastValue > 0) perMarker[kv.Key].Add(kv.Value.LastValue / 1e6);
            fixedPerFrame.Add(FixedCounter.Count - lastFixed); lastFixed = FixedCounter.Count;
            if ((frame++ % 10) == 0) { foreach (var a in agentsList) { if (a == null) continue; pendSamples++; if (a.pathPending) pending++; if (a.isOnNavMesh) onMesh++; } }
            if (Time.realtimeSinceStartup - tm0 > meas * 6) break;
        }
        res["n_valid_samples"] = validCount; res["mk_dbg_top"] = string.Join(";", mx.OrderByDescending(x => x.Value).Take(12).Select(x => x.Key + "=" + x.Value.ToString("F0")));
        Physics.autoSimulation = physWas;
        foreach (var kv in recs) kv.Value.Dispose();
        dts.Sort();
        double P(List<double> s, double q) { return s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)(q * (s.Count - 1) + 0.5))]; }
        res["frames"] = dts.Count; res["mean_ms"] = dts.Average(); res["median_ms"] = P(dts, 0.5); res["p95_ms"] = P(dts, 0.95); res["p99_ms"] = P(dts, 0.99); res["max_ms"] = dts.Max();
        res["fps_mean"] = 1000.0 / dts.Average();
        res["alloc_bytes_per_frame_approx"] = allocSum / dts.Count;
        res["gc_collect_gen0"] = GC.CollectionCount(0) - collections0; res["gc_collect_gen2"] = GC.CollectionCount(2) - collections2;
        res["fixed_steps_per_frame_mean"] = fixedPerFrame.Average(); res["fixed_steps_per_frame_max"] = fixedPerFrame.Max();
        foreach (var kv in perMarker) if (kv.Value.Count > dts.Count / 4) { kv.Value.Sort(); double md = P(kv.Value, 0.5); if (md >= 0.02) res["mk_" + kv.Key + "_median_ms"] = md; }
        res["live_agents"] = Live(rig); res["agents_tracked"] = agentsList.Count;
        res["pathPending_fraction"] = pendSamples > 0 ? (double)pending / pendSamples : double.NaN; res["onNavMesh_fraction"] = pendSamples > 0 ? (double)onMesh / pendSamples : double.NaN;
        long snaps = DG("Snapshots"); res["target_reissues"] = DG("TargetReissues"); res["target_reissues_same_dest"] = DG("TargetReissuesSameDest"); res["target_reissues_per_s"] = DG("TargetReissues") / (dts.Sum() / 1000.0);
        res["snapshots"] = snaps; res["snapshots_per_frame"] = (double)snaps / dts.Count; res["rows"] = DG("Rows"); res["raycasts"] = DG("Raycasts"); res["bytes"] = DG("Bytes");
        if (snaps > 0)
        {
            res["snap_total_ms"] = Ms(DG("TotalTicks")) / snaps; res["snap_read_ms"] = Ms(DG("ReadTicks")) / snaps; res["snap_eye_ms"] = Ms(DG("EyeTicks")) / snaps;
            res["snap_los_ms"] = Ms(DG("LosTicks")) / snaps; res["snap_format_ms"] = Ms(DG("FormatTicks")) / snaps; res["snap_event_ms"] = Ms(DG("EventTicks")) / snaps;
            res["snap_write_ms"] = Ms(DG("WriteTicks")) / snaps; res["snap_batchbuild_ms"] = Ms(DG("BatchBuildTicks")) / snaps; res["snap_append_ms"] = Ms(DG("AppendTicks")) / snaps;
            res["rows_per_snapshot"] = (double)DG("Rows") / snaps; res["bytes_per_snapshot"] = (double)DG("Bytes") / snaps; res["bytes_per_row"] = DG("Rows") > 0 ? (double)DG("Bytes") / DG("Rows") : 0.0;
            res["raycasts_per_snapshot"] = (double)DG("Raycasts") / snaps;
            double secs = dts.Sum() / 1000.0; res["bytes_per_second"] = DG("Bytes") / secs; res["snapshot_ms_per_frame"] = Ms(DG("TotalTicks")) / dts.Count;
        }
    }

    static readonly Type DiagT = T("NpcPerfDiag");
    static long DG(string f) { return (long)DiagT.GetField(f).GetValue(null); }
    static void DiagCall(string m) { DiagT.GetMethod(m).Invoke(null, null); }
    static double Ms(long ticks) { return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
    static GameObject LoadNpcPrefab()
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Npc.prefab");
#else
        return null;
#endif
    }

    static string MiniJson(Dictionary<string, object> d)
    {
        var sb = new System.Text.StringBuilder("{");
        bool first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append('"').Append(kv.Key).Append("\":");
            if (kv.Value is string s) sb.Append('"').Append(s).Append('"');
            else if (kv.Value is double dv) sb.Append(double.IsNaN(dv) || double.IsInfinity(dv) ? "null" : dv.ToString("R", CultureInfo.InvariantCulture));
            else if (kv.Value is float fv) sb.Append(fv.ToString("R", CultureInfo.InvariantCulture));
            else sb.Append(Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }
}
