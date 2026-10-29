// NpcMasterSwitchTests.cs
// PlayMode tests for (1) the NPC MASTER SWITCH (PartCaptureConfig.npcCaptureEnabled is the only gate of NPC-specific research data) and
// (2) the Inspector-adjustable npc_state sampling interval and MB/minute data budget. Like NpcLifecycleTests they drive the PRODUCTION classes
// through reflection on a runtime NavMesh and read the files they write (unique temp folder). Editor tests - not a Meta Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type RateType = T("NpcStateRateController");

    static readonly HashSet<string> NpcEventTypesInEventsCsv = new HashSet<string> { "npc_spawn", "npc_despawn", "npc_warmup_start", "npc_warmup_end" };

    void RaiseExperimentEvent(Rig rig, string type, string part)
        => Call(rig.em, "RaiseEvt", type, part, "", "", 0.0, false, null, Vector3.zero, Vector3.zero);

    static string[] NpcFiles = { "npc_state.csv", "npc_events.csv", "npc_lifetimes.csv", "npc_profiles.csv" };

    // Snapshot = all npc_state rows sharing one t_abs. Returns (time -> row count), ordered by time.
    static List<KeyValuePair<double, int>> Snapshots(Csv st)
    {
        var map = new SortedDictionary<double, int>();
        foreach (var r in st.Rows)
        {
            double t = double.Parse(st.Get(r, "t_abs"), CultureInfo.InvariantCulture);
            map[t] = map.ContainsKey(t) ? map[t] + 1 : 1;
        }
        return new List<KeyValuePair<double, int>>(map);
    }

    static double MeanGap(List<KeyValuePair<double, int>> snaps, int skip, out double minGap)
    {
        minGap = double.MaxValue; double sum = 0; int n = 0;
        for (int i = skip + 1; i < snaps.Count; i++)
        {
            double g = snaps[i].Key - snaps[i - 1].Key;
            sum += g; n++; if (g < minGap) minGap = g;
        }
        return n > 0 ? sum / n : double.NaN;
    }

    // ====================================================================================== Test A: master switch OFF

    [UnityTest]
    public IEnumerator MasterSwitchOff_Part2_RecordsNoNpcData_ButSimulationAndGenericGazeRun()
    {
        LogAssert.ignoreFailingMessages = false;
        Rig rig = null;
        // Part 2 as shipped (npcCaptureEnabled=false). The Part's config is only visible through the SELECTED Part until the first frame has passed,
        // so the prewarm events are raised before CurrentPartConfig exists - the gate must already be closed for them.
        yield return BuildRig(new RigOptions { part2 = true, npcCaptureEnabled = false, selectedPartFallback = true, prewarm = 3, maxAgents = 3, npcSpeed = 2f }, r => rig = r);
        RaiseExperimentEvent(rig, "npc_warmup_start", "Part2");
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "Part 2 NPCs (simulation keeps running)");
        Assert.IsFalse(Get<bool>(rig.dcm, "NpcRecordingEnabled"));

        var agents = ActiveAgents(rig);
        var firstIds = new List<string>(); var startPos = new List<Vector3>();
        foreach (var pa in agents) { firstIds.Add(Lid(pa)); startPos.Add(pa.transform.position); }
        yield return new WaitForSecondsRealtime(0.5f);
        bool moved = false;
        for (int i = 0; i < agents.Count; i++) if (agents[i] != null && (agents[i].transform.position - startPos[i]).sqrMagnitude > 0.01f) moved = true;
        Assert.IsTrue(moved, "NPC behaviour is unaffected: the NPCs walk although nothing is recorded");

        // Trial/census/pool-reuse callbacks must not circumvent the gate.
        RaiseExperimentEvent(rig, "npc_warmup_end", "Part2");
        Call(rig.em, "BeginTrial");
        Call(rig.dcm, "CensusActiveNpcs");
        ForceFinishAll(rig);                                  // lifetimes end, objects return to the pool ...
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "pool reuse");
        foreach (var pa in ActiveAgents(rig)) StringAssert.EndsWith("-S2", Lid(pa), "the simulation still gives reused objects a new lifetime ID");
        Call(rig.dcm, "SampleNpcs");                          // even a direct sampler call writes nothing

        // Generic gaze keeps working; NPC identity in gaze.csv stays blank. Put an NPC in front of the head.
        Component npc = ActiveAgents(rig)[0];
        var nav = npc.GetComponent<NavMeshAgent>();
        Call(npc.GetComponent(MoveType), "Stop");
        var head = Make("Head");
        head.transform.position = new Vector3(0f, 0.9f, -3f);
        var recGo = Make("Gaze");
        var rec = recGo.AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform); Set(rec, "sampleRate", 60f); Set(rec, "enableDebugLogs", false); Set(rec, "experimentManager", rig.em);
        yield return null;
        Call(rec, "StartRecording");
        float until = Time.realtimeSinceStartup + 0.6f;
        while (Time.realtimeSinceStartup < until) { nav.Warp(new Vector3(0f, 0.1f, 0f)); yield return null; }
        Call(rec, "StopRecording");

        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        Assert.Greater(gaze.Rows.Count, 0, "generic gaze sampling stays active when NPC capture is off");
        int npcClass = 0;
        foreach (var r in gaze.Rows)
        {
            Assert.AreEqual("", gaze.Get(r, "hit_npc_spawn_instance_id"), "no NPC lifetime ID while NPC capture is off");
            Assert.AreEqual("", gaze.Get(r, "hit_npc_id"), "no NPC identity of any kind while NPC capture is off");
            if (gaze.Get(r, "hit_semantic_class") == "NPC") npcClass++;
        }
        Assert.Greater(npcClass, 0, "precondition: the gaze ray did hit the NPC collider (class stays NPC; only the identity is withheld)");
        Assert.AreEqual(0, GetStatic<int>(AoiType, "UnresolvedNpcHits"), "a disabled recorder does not count 'unresolved' NPC hits");

        Call(rig.em, "EndTrial");
        Call(rig.em, "HandleEndPanelDisplayed", "Part2", true);
        Call(rig.em, "TerminateSession", "Part2");
        yield return new WaitForSecondsRealtime(0.3f);

        // No NPC-specific record anywhere.
        foreach (string f in NpcFiles) Assert.IsFalse(File.Exists(rig.File(f)), f + " must not exist when NPC capture is disabled");
        var evs = Events(rig);
        AssertWidths(evs, "events.csv");
        bool sawPartStart = false;
        foreach (var r in evs.Rows)
        {
            string type = evs.Get(r, "event_type");
            Assert.IsFalse(NpcEventTypesInEventsCsv.Contains(type), "NPC-specific event in events.csv: " + type);
            Assert.AreEqual("", evs.Get(r, "npc_id")); Assert.AreEqual("", evs.Get(r, "npc_spawn_instance_id"));
            if (type == "part_start") sawPartStart = true;
        }
        Assert.IsTrue(sawPartStart, "non-NPC events are still recorded");

        // Nothing retained for a later replay.
        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"));
        Assert.Greater(GetStatic<int>(BusType, "SuppressedByGate"), 0, "events were discarded at the gate (counter only, no content)");
        string manifest = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"npc_capture_effective_enabled\": false", manifest);
        StringAssert.Contains("\"npc_lifetimes_registered\": 0", manifest);
        StringAssert.Contains("\"npc_state_snapshots\": 0", manifest);
    }

    [Test]
    public void MasterSwitchOff_DiscardedEvents_AreNeverQueuedOrReplayed()
    {
        CallStatic(BusType, "ResetForTests");
        object NewEvent() { var e = Activator.CreateInstance(EvtType); EvtType.GetField("type").SetValue(e, "prewarm_created"); return e; }
        bool enabled = false;
        var sinkType = typeof(Action<>).MakeGenericType(EvtType);
        s_delivered = 0;

        // 1) events queued BEFORE a gate exists are discarded when a closed gate is installed
        for (int i = 0; i < 5; i++) CallStatic(BusType, "Raise", NewEvent());
        Assert.AreEqual(5, GetStatic<int>(BusType, "PendingCount"), "no recorder and no gate yet: queued for the recorder");
        CallStatic(BusType, "SetGate", (Func<bool>)(() => enabled));
        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"), "installing a closed gate drops what was retained");
        Assert.AreEqual(5, GetStatic<int>(BusType, "SuppressedByGate"));

        // 2) while the gate is closed nothing is queued
        for (int i = 0; i < 7; i++) CallStatic(BusType, "Raise", NewEvent());
        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"));
        Assert.AreEqual(12, GetStatic<int>(BusType, "SuppressedByGate"));

        // 3) a recorder that attaches later - even with the gate now open - receives none of the discarded events
        var d = Delegate.CreateDelegate(sinkType, typeof(NpcLifecycleTests).GetMethod(nameof(CountDeliveredObj), All).MakeGenericMethod(EvtType));
        CallStatic(BusType, "Attach", d);
        enabled = true;
        Assert.AreEqual(0, s_delivered, "disabled-period events are not replayed");
        CallStatic(BusType, "Raise", NewEvent());
        Assert.AreEqual(1, s_delivered, "with the gate open, events flow");
        CallStatic(BusType, "ResetForTests");
    }

    static int s_delivered;
    static void CountDeliveredObj<TEvt>(TEvt e) { s_delivered++; }

    // ====================================================================================== Test B: master switch ON

    [UnityTest]
    public IEnumerator MasterSwitchOn_Part3_RecordsPrewarmPreTrialSpawnsAndReuse_FromTheSelectedPartConfig()
    {
        Rig rig = null;
        // CurrentPartConfig is still null when the prewarm events are raised: the selected Part's config (enabled) must be honoured, and the
        // events must be kept with their original timestamps.
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, selectedPartFallback = true, npcSpeed = 3f }, r => rig = r);
        RaiseExperimentEvent(rig, "npc_warmup_start", "Part3");
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "three live NPCs before any trial");
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.IsTrue(Get<bool>(rig.dcm, "NpcRecordingEnabled"));
        RaiseExperimentEvent(rig, "npc_warmup_end", "Part3");

        var nev = NpcEvents(rig); var lt = Lifetimes(rig); var st = NpcState(rig); var evs = Events(rig);
        AssertWidths(nev, "npc_events.csv"); AssertWidths(lt, "npc_lifetimes.csv"); AssertWidths(st, "npc_state.csv"); AssertWidths(evs, "events.csv");
        Assert.AreEqual(3, new List<List<string>>(nev.Where("event_type", "prewarm_created")).Count, "prewarm events recorded");
        Assert.AreEqual(3, lt.Rows.Count, "lifetimes registered before the trial (Start-panel wait / pre-trial)");
        Assert.Greater(st.Rows.Count, 0, "positions recorded before the trial");
        foreach (var r in st.Rows) CollectionAssert.Contains(new[] { "warmup", "pre_trial" }, st.Get(r, "capture_period"), "recorded outside any trial");
        Assert.GreaterOrEqual(new List<List<string>>(evs.Where("event_type", "npc_warmup_start")).Count, 1);

        ForceFinishAll(rig);
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "pool reuse");
        yield return new WaitForSecondsRealtime(0.4f);
        lt = Lifetimes(rig);
        Assert.AreEqual(6, lt.Rows.Count, "reuse = three more lifetimes, new IDs");
        foreach (var pa in ActiveAgents(rig)) StringAssert.EndsWith("-S2", Lid(pa));
    }

    [UnityTest]
    public IEnumerator MasterSwitchOn_Part2_RecordsEverything_WhenItsFlagIsEnabled()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, npcCaptureEnabled = true, prewarm = 2, maxAgents = 2 }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "Part 2 NPCs");
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.GreaterOrEqual(Lifetimes(rig).Rows.Count, 2);
        Assert.Greater(NpcState(rig).Rows.Count, 0);
        var ltp = Lifetimes(rig);
        foreach (var r in ltp.Rows) { Assert.AreEqual("", ltp.Get(r, "configured_speed")); Assert.AreEqual("", ltp.Get(r, "profile_content_hash")); Assert.AreEqual("", ltp.Get(r, "appearance_hash")); }
        Assert.Greater(NpcEvents(rig).Rows.Count, 0);
        foreach (var r in NpcState(rig).Rows) Assert.AreEqual("Part2", NpcState(rig).Get(r, "part"));
    }

    [Test]
    public void ShippedPartDefaults_KeepPart2Disabled_AndPart3Enabled()
    {
        var go = Make("EmDefaults");
        var em = go.AddComponent(EmType);
        Assert.IsFalse(Get<bool>(Get<object>(em, "part2Config"), "npcCaptureEnabled"), "Part 2 default stays disabled");
        Assert.IsFalse(Get<bool>(Get<object>(em, "part1Config"), "npcCaptureEnabled"));
        Assert.IsTrue(Get<bool>(Get<object>(em, "part3Config"), "npcCaptureEnabled"));
        Assert.IsTrue(Get<bool>(Get<object>(em, "part2Config"), "npcEnabled"), "Part 2 NPCs still exist");
    }

    // ====================================================================================== Test C: sampling interval

    IEnumerator RunIntervalCase(float interval, Action<double, double, int> done)
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, npcSpeed = 1f, samplingInterval = interval, spawnInterval = 0.02f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "NPCs");
        float t0 = Time.realtimeSinceStartup;
        yield return new WaitForSecondsRealtime(Mathf.Max(1.6f, interval * 8f));
        var st = NpcState(rig);
        AssertWidths(st, "npc_state.csv");
        var snaps = Snapshots(st);
        Assert.GreaterOrEqual(snaps.Count, 4, "enough snapshots at interval " + interval);
        // all active NPCs are in every complete snapshot once they exist (3), and never more than the population
        int full = 0;
        for (int i = 0; i < snaps.Count; i++) { Assert.LessOrEqual(snaps[i].Value, 3); if (snaps[i].Value == 3) full++; }
        Assert.GreaterOrEqual(full, snaps.Count - 2, "complete population snapshots (spawning happens in the first ticks)");
        double mean = MeanGap(snaps, 2, out double min);
        Assert.AreEqual(interval, Get<float>(rig.dcm, "npcStateSamplingIntervalSeconds"), 1e-6);
        Assert.AreEqual(interval, Get<float>(rig.dcm, "NpcStateEffectiveIntervalSeconds"), 1e-6, "no budget: effective == base");
        done(mean, min, snaps.Count);
        yield return null;
    }

    [UnityTest]
    public IEnumerator SamplingInterval_Inspector_Value_Controls_TheSnapshotSpacing()
    {
        double m05 = 0, m10 = 0, m20 = 0, n05 = 0, n10 = 0, n20 = 0; int c05 = 0, c10 = 0, c20 = 0;
        yield return RunIntervalCase(0.05f, (m, n, c) => { m05 = m; n05 = n; c05 = c; });
        TearDownRigObjects(); yield return null; yield return null;
        yield return RunIntervalCase(0.1f, (m, n, c) => { m10 = m; n10 = n; c10 = c; });
        TearDownRigObjects(); yield return null; yield return null;
        yield return RunIntervalCase(0.2f, (m, n, c) => { m20 = m; n20 = n; c20 = c; });
        UnityEngine.Debug.Log($"[NpcMasterSwitchTests][interval] 0.05 -> mean gap {m05:F3}s (min {n05:F3}, {c05} snapshots); 0.10 -> {m10:F3}s ({n10:F3}, {c10}); 0.20 -> {m20:F3}s ({n20:F3}, {c20})");

        // Frame-quantised runtime sampling: the gap can be longer than requested, never (materially) shorter, and it grows with the setting.
        Assert.GreaterOrEqual(n05, 0.05 * 0.9); Assert.GreaterOrEqual(n10, 0.1 * 0.9); Assert.GreaterOrEqual(n20, 0.2 * 0.9);
        Assert.Less(m05, m10); Assert.Less(m10, m20);
        Assert.Greater(n05, 0.0, "no zero-gap / duplicate-loop snapshots");
        Assert.Less(m20, 0.2 * 2.0); Assert.Less(m05, 0.05 * 3.0);
        Assert.Greater(c05, c10); Assert.Greater(c10, c20);
    }

    void TearDownRigObjects()
    {
        foreach (var o in _spawned) if (o != null) UnityEngine.Object.Destroy(o);
        _spawned.Clear();
        if (_navInstance.valid) NavMesh.RemoveNavMeshData(_navInstance);
        CallStatic(BusType, "ResetForTests");
    }

    // ====================================================================================== Test D / E: data budget

    [UnityTest]
    public IEnumerator Budget_Zero_NeverThrottles()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 20, maxAgents = 20, npcSpeed = 1f, spawnInterval = 0f, samplingInterval = 0.1f, budgetMB = 0f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 20, 20f, "20 NPCs");
        yield return new WaitForSecondsRealtime(1.5f);
        var rate = Get<object>(rig.dcm, "NpcStateRate");
        Assert.AreEqual(0.1f, Get<float>(rate, "EffectiveInterval"), 1e-6, "effective interval stays at the base interval");
        Assert.AreEqual(0L, Get<long>(rate, "ThrottledSnapshots"));
        Assert.AreEqual("unlimited", Get<string>(rig.dcm, "NpcStateBudgetStatus"));
        Assert.Greater(Get<long>(rate, "Snapshots"), 5);
    }

    [UnityTest]
    public IEnumerator Budget_Positive_StretchesTheInterval_KeepsEveryNpc_AndRelaxesWhenThereIsHeadroom()
    {
        const int N = 20;
        Rig rig = null;
        // ~290 B/row x 20 NPCs ~ 5.8 kB per snapshot; 0.5 MB/min allows one snapshot per ~0.7 s instead of one per 0.1 s.
        yield return BuildRig(new RigOptions { prewarm = N, maxAgents = N, npcSpeed = 1f, spawnInterval = 0f, samplingInterval = 0.1f, budgetMB = 0.5f, maxInterval = 5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= N, 20f, N + " NPCs");
        var rate = Get<object>(rig.dcm, "NpcStateRate");
        yield return WaitUntil(() => Get<long>(rate, "Snapshots") >= 4, 20f, "throttled snapshots");
        float eff = Get<float>(rig.dcm, "NpcStateEffectiveIntervalSeconds");
        UnityEngine.Debug.Log($"[NpcMasterSwitchTests][budget] N={N} budget=0.5MB/min -> effective interval {eff:F3}s, observed {Get<double>(rig.dcm, "NpcStateObservedMBPerMinute"):F3} MB/min, status {Get<string>(rig.dcm, "NpcStateBudgetStatus")}");
        Assert.Greater(eff, 0.4f, "interval stretched well above the 0.1 s base when the output exceeds the budget");
        Assert.LessOrEqual(eff, 5f);
        Assert.Greater(Get<long>(rate, "ThrottledSnapshots"), 0L);

        // Let the NPC population exist for a while at the stretched interval, then check completeness.
        yield return new WaitForSecondsRealtime(2.5f);
        var st = NpcState(rig);
        AssertWidths(st, "npc_state.csv");
        var snaps = Snapshots(st);
        for (int i = 1; i < snaps.Count; i++)   // the very first snapshot may predate the last spawn
            Assert.AreEqual(N, snaps[i].Value, "every snapshot after the population is complete contains ALL " + N + " NPCs");
        Assert.AreEqual(N, Lifetimes(rig).Rows.Count, "per-lifetime attribute records are never throttled");
        Assert.AreEqual(N, new List<List<string>>(Events(rig).Where("event_type", "npc_spawn")).Count, "spawn events are never throttled");
        Assert.AreEqual(N, new List<List<string>>(NpcEvents(rig).Where("event_type", "npc_spawn")).Count);
        double mean = MeanGap(snaps, 1, out double min);
        Assert.Greater(mean, 0.4, "recorded snapshot spacing reflects the stretched interval");

        // Headroom: a much larger budget lets the interval fall back to the configured base.
        Set(rig.dcm, "npcStateDataBudgetMBPerMinute", 500f);
        yield return WaitUntil(() => Get<float>(rig.dcm, "NpcStateEffectiveIntervalSeconds") <= 0.1001f, 20f, "interval to return to the base");
        Assert.AreEqual(0.1f, Get<float>(rig.dcm, "NpcStateEffectiveIntervalSeconds"), 1e-4);

        // Manifest carries the settings, the statistics and no false claim.
        Call(rig.dcm, "WriteManifestEnd", "test");
        string m = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"npc_state_base_interval_seconds\": 0.1", m);
        StringAssert.Contains("\"npc_state_max_interval_seconds\": 5", m);
        StringAssert.Contains("\"npc_state_budget_scope\"", m);
        StringAssert.Contains("\"npc_state_max_active_npcs\": " + N, m);
        Assert.IsTrue(Regex.IsMatch(m, "\"npc_state_throttled_snapshots\": [1-9]"), "throttling is recorded");
        Assert.IsTrue(Regex.IsMatch(m, "\"npc_state_snapshots\": [1-9]"));
        Assert.IsTrue(Regex.IsMatch(m, "\"npc_state_bytes_written\": [1-9]"));
    }

    [UnityTest]
    public IEnumerator Budget_ImpossiblyLow_WarnsHonestly_AndDoesNotClaimItIsMet()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 10, maxAgents = 10, npcSpeed = 1f, spawnInterval = 0f, samplingInterval = 0.1f, budgetMB = 0.001f, maxInterval = 0.5f }, r => rig = r);
        LogAssert.Expect(LogType.Warning, new Regex("cannot be met"));
        yield return WaitUntil(() => Live(rig) >= 10, 20f, "NPCs");
        var rate = Get<object>(rig.dcm, "NpcStateRate");
        yield return WaitUntil(() => Get<long>(rate, "Snapshots") >= 3, 20f, "snapshots at the maximum interval");
        Assert.AreEqual(0.5f, Get<float>(rig.dcm, "NpcStateEffectiveIntervalSeconds"), 1e-4, "pinned at the maximum interval");
        Assert.IsTrue(Get<bool>(rate, "Unachievable"));
        Assert.AreEqual("exceeded_unachievable", Get<string>(rig.dcm, "NpcStateBudgetStatus"));
        Assert.Greater(Get<double>(rig.dcm, "NpcStateObservedMBPerMinute"), 0.001, "the real rate is reported, above the target");
        Call(rig.dcm, "WriteManifestEnd", "test");
        string m = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"npc_state_budget_unachievable_seen\": true", m);
        StringAssert.Contains("\"npc_state_budget_status\": \"exceeded_unachievable\"", m);
    }

    [UnityTest]
    public IEnumerator InvalidInspectorValues_AreSanitised_NeverLoopTightly()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 3, 10f, "NPCs");
        foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Set(rig.dcm, "npcStateSamplingIntervalSeconds", bad);
            Set(rig.dcm, "npcStateDataBudgetMBPerMinute", bad);
            yield return new WaitForSecondsRealtime(0.45f);
            var rate = Get<object>(rig.dcm, "NpcStateRate");
            float bi = Get<float>(rate, "BaseInterval");
            Assert.IsTrue(!float.IsNaN(bi) && !float.IsInfinity(bi) && bi >= 0.01f, "base interval is finite and positive for input " + bad);
            Assert.AreEqual(0.1f, bi, 1e-6, "invalid interval falls back to the default 0.1 s");
            Assert.AreEqual(0.0, Get<double>(rate, "BudgetMBPerMinute"), "invalid budget = unlimited");
            Assert.AreEqual("unlimited", Get<string>(rig.dcm, "NpcStateBudgetStatus"));
        }
        // a positive but tiny interval is raised to the 0.01 s floor, and the loop still yields between snapshots (no tight loop)
        Set(rig.dcm, "npcStateSamplingIntervalSeconds", 0.0001f);
        Set(rig.dcm, "npcStateDataBudgetMBPerMinute", 0f);
        long before = Get<long>(Get<object>(rig.dcm, "NpcStateRate"), "Snapshots");
        yield return new WaitForSecondsRealtime(1f);
        long made = Get<long>(Get<object>(rig.dcm, "NpcStateRate"), "Snapshots") - before;
        Assert.AreEqual(0.01f, Get<float>(Get<object>(rig.dcm, "NpcStateRate"), "BaseInterval"), 1e-6);
        Assert.LessOrEqual(made, 100, "at most ~100 snapshots per second at the 0.01 s floor");

        // static sanitisers
        object[] a = { float.NaN, 0.1f, false };
        float r1 = (float)RateType.GetMethod("SanitizeBaseInterval", All).Invoke(null, a);
        Assert.AreEqual(0.1f, r1); Assert.IsTrue((bool)a[2]);
        object[] b = { -3f, false };
        Assert.AreEqual(0f, (float)RateType.GetMethod("SanitizeBudget", All).Invoke(null, b)); Assert.IsTrue((bool)b[1]);
        object[] c = { 0.05f, 0.1f, 2f, false };
        Assert.AreEqual(0.1f, (float)RateType.GetMethod("SanitizeMaxInterval", All).Invoke(null, c), 1e-6, "max below base is raised to base"); Assert.IsTrue((bool)c[3]);
    }

    // ====================================================================================== controller (pure logic, simulated time)

    [Test]
    public void RateController_StretchesRelaxesWithoutOscillation_AndNeverDropsRows()
    {
        object NewController(float baseI, float maxI, double budget)
        {
            var c = Activator.CreateInstance(RateType);
            Call(c, "Configure", baseI, maxI, budget);
            return c;
        }
        // simulates the sampler: 100 NPCs, 290 B each; returns the interval trace
        List<float> Run(object c, ref double t, int steps, int npcs, double bytesPerRow = 290)
        {
            var trace = new List<float>();
            float interval = Get<float>(c, "EffectiveInterval");
            for (int i = 0; i < steps; i++)
            {
                t += interval;
                interval = (float)Call(c, "OnSnapshot", t, npcs, (long)(npcs * bytesPerRow), npcs);
                trace.Add(interval);
            }
            return trace;
        }

        // unlimited: always the base interval
        double t0 = 0;
        var u = NewController(0.1f, 2f, 0.0);
        foreach (var x in Run(u, ref t0, 50, 100)) Assert.AreEqual(0.1f, x, 1e-6f);

        // 1 MB/min with 100 NPCs x 290 B = 29 kB/snapshot -> needs >= 1.74 s between snapshots
        double t = 0;
        var c1 = NewController(0.1f, 5f, 1.0);
        var up = Run(c1, ref t, 60, 100);
        Assert.AreEqual(29000.0 * 60 / 1e6 * 1.05, up[up.Count - 1], 0.05, "settles just above the required interval");
        Assert.IsTrue(Get<double>(c1, "ObservedMBPerMinute") <= 1.05, "measured rate within the budget once settled: " + Get<double>(c1, "ObservedMBPerMinute"));
        int turns = 0; for (int i = 2; i < up.Count; i++) if (Math.Sign(up[i] - up[i - 1]) * Math.Sign(up[i - 1] - up[i - 2]) < 0) turns++;
        Assert.LessOrEqual(turns, 1, "no oscillation in steady state");

        // population drops to 10 -> interval relaxes towards base (but never below the required one)
        var down = Run(c1, ref t, 40, 10);
        float last = down[down.Count - 1];
        Assert.Less(last, 0.25f); Assert.GreaterOrEqual(last, 0.1f);
        for (int i = 1; i < down.Count; i++) Assert.LessOrEqual(down[i], down[i - 1] + 1e-6f, "monotonic relaxation, no bounce");

        // generous budget -> exactly the base interval again
        var c2 = NewController(0.1f, 2f, 100.0);
        foreach (var x in Run(c2, ref t, 20, 100)) Assert.AreEqual(0.1f, x, 1e-6f);

        // impossible budget -> pinned at the max, flagged, rate reported honestly
        var c3 = NewController(0.1f, 2f, 0.01);
        Run(c3, ref t, 30, 100);
        Assert.AreEqual(2f, Get<float>(c3, "EffectiveInterval"), 1e-6f);
        Assert.IsTrue(Get<bool>(c3, "Unachievable"));
        Assert.Greater(Get<double>(c3, "ObservedMBPerMinute"), 0.01);
        Assert.AreEqual(100, Get<int>(c3, "LastActiveNpcs"), "the controller reports rows; it has no way to drop NPCs");
    }
}
