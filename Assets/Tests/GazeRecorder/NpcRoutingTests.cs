// NpcRoutingTests.cs
// PlayMode tests for the event-stream separation (schema_version 4): events.csv = participant + experiment/session workflow events ONLY; NPC-subject
// events live in npc_events.csv (and the other NPC streams). They drive the PRODUCTION routing (CaptureConstants.IsNpcSubjectEvent, the WriteEventRow guard,
// the NpcLifecycleBus gate) through reflection and read the files that are really written. Editor tests - not a Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type ConstType = T("CaptureConstants");
    static bool IsNpcSubject(string type) => (bool)CallStatic(ConstType, "IsNpcSubjectEvent", type);

    static void AssertEventsCsvIsParticipantOnly(Csv evs)
    {
        AssertWidths(evs, "events.csv");
        foreach (var r in evs.Rows)
        {
            string type = evs.Get(r, "event_type");
            Assert.IsFalse(IsNpcSubject(type), "NPC-subject event in events.csv: " + type);
            Assert.AreEqual("", evs.Get(r, "npc_id"), "npc_id column is always blank in schema v4 (" + type + ")");
            Assert.AreEqual("", evs.Get(r, "npc_spawn_instance_id"), "npc_spawn_instance_id column is always blank in schema v4 (" + type + ")");
        }
    }

    object NewExperimentEvent(string type, Action<object> fill = null)
    {
        object e = Activator.CreateInstance(T("ExperimentEventInfo"));
        e.GetType().GetField("type").SetValue(e, type);
        fill?.Invoke(e);
        return e;
    }

    static void SetField(object boxed, string name, object value) => boxed.GetType().GetField(name).SetValue(boxed, value);

    static int CountType(Csv c, string type) => new List<List<string>>(c.Where("event_type", type)).Count;

    // Every participant-facing event type the recorder writes, produced through the production callbacks.
    void EmitRepresentativeParticipantEvents(Rig rig)
    {
        RaiseExperimentEvent(rig, "panel_shown", "Part3");
        RaiseExperimentEvent(rig, "panel_confirmed", "Part3");
        RaiseExperimentEvent(rig, "destination_shown", "Part3");
        Call(rig.em, "RaiseEvt", "phase_start", "Part3", "d1_search", "", null, false, null, Vector3.zero, Vector3.zero);
        Call(rig.em, "RaiseEvt", "teleport", "Part3", "river_bank", "dest", null, false, null, new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        Call(rig.em, "BeginTrial");
        var selGo = Make("BAT_TEST"); selGo.AddComponent<BoxCollider>(); var sel = selGo.AddComponent(T("TargetSelectable"));
        Set(sel, "targetId", "T_ROUTE");
        Call(rig.dcm, "OnTargetSelectableClicked", sel);
        Call(rig.dcm, "OnExperimentEvent", NewExperimentEvent("part3_log", e => { SetField(e, "part3Event", "d1_click"); SetField(e, "boolValue", true); SetField(e, "stringValue", "T_ROUTE"); SetField(e, "numValue", 4.5); SetField(e, "hasNum", true); SetField(e, "notes", "ok"); }));
        Call(rig.dcm, "OnExperimentEvent", NewExperimentEvent("part3_log", e => { SetField(e, "part3Event", "d2_click"); SetField(e, "boolValue", false); SetField(e, "stringValue", "T_OTHER"); SetField(e, "numValue", 6.5); SetField(e, "hasNum", true); SetField(e, "notes", "wrong"); }));
        Call(rig.dcm, "OnRiverCrossing", null, null, new Vector3(7f, 0f, 0f), new Vector3(8f, 0f, 0f));
        Call(rig.dcm, "OnApplicationPause", true);
        Call(rig.dcm, "OnApplicationPause", false);
        Call(rig.dcm, "OnApplicationFocus", false);
        Call(rig.dcm, "OnApplicationFocus", true);
        Call(rig.em, "EndTrial");
    }

    // ====================================================================================== classification table

    [Test]
    public void NpcRouting_EveryNpcEventConstantIsClassified_AndNoWorkflowEventIs()
    {
        var npc = new List<string>(); var other = new List<string>();
        foreach (FieldInfo f in ConstType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
            string n = f.Name, v = (string)f.GetRawConstantValue();
            bool isEvt = n.StartsWith("Evt") || n.StartsWith("NpcEvt");
            if (!isEvt) continue;
            bool npcByName = n.StartsWith("NpcEvt") || n == "EvtNpcSpawn" || n == "EvtNpcDespawn";   // the NPC-subject constants
            bool warmup = n == "EvtNpcWarmupStart" || n == "EvtNpcWarmupEnd";                       // experiment-workflow markers (see docs)
            if (npcByName) { Assert.IsTrue(IsNpcSubject(v), "NPC event constant not classified: " + n + "=" + v); npc.Add(v); }
            else { Assert.IsFalse(IsNpcSubject(v), "workflow event wrongly classified as NPC: " + n + "=" + v); other.Add(v); if (warmup) Assert.IsFalse(IsNpcSubject(v)); }
        }
        Assert.GreaterOrEqual(npc.Count, 18);
        CollectionAssert.Contains(other, "building_click"); CollectionAssert.Contains(other, "teleport"); CollectionAssert.Contains(other, "npc_warmup_start");
        CollectionAssert.Contains(npc, "prewarm_created"); CollectionAssert.Contains(npc, "npc_uncertain_state"); CollectionAssert.Contains(npc, "npc_leg_start");
        Assert.AreEqual("Npc", CallStatic(ConstType, "RouteOf", "npc_dest_change").ToString());
        Assert.AreEqual("ParticipantExperiment", CallStatic(ConstType, "RouteOf", "building_click").ToString());
        Assert.AreEqual("ParticipantExperiment", CallStatic(ConstType, "RouteOf", "some_future_workflow_event").ToString(), "unknown types default to the experiment stream; NPC types must be listed explicitly");
    }

    // ====================================================================================== Test A: participant events retained

    [UnityTest]
    public IEnumerator Routing_ParticipantAndWorkflowEvents_AreStillWrittenToEventsCsv_WithTheirFields()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        EmitRepresentativeParticipantEvents(rig);
        yield return null;

        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        foreach (string t in new[] { "session_start", "part_start", "panel_shown", "panel_confirmed", "destination_shown", "phase_start", "teleport", "trial_start",
                                     "building_click", "correct_selection", "incorrect_selection", "river_crossing", "app_pause", "app_focus", "trial_end" })
            Assert.GreaterOrEqual(CountType(evs, t), 1, "participant/workflow event missing from events.csv: " + t);
        Assert.AreEqual(2, CountType(evs, "app_pause"), "pause and resume"); Assert.AreEqual(2, CountType(evs, "app_focus"));

        var click = new List<List<string>>(evs.Where("event_type", "building_click"))[0];
        Assert.AreEqual("BAT_TEST", evs.Get(click, "building_id")); Assert.AreEqual("T_ROUTE", evs.Get(click, "target_id"));
        var correct = new List<List<string>>(evs.Where("event_type", "correct_selection"))[0];
        Assert.AreEqual("D1", evs.Get(correct, "label")); Assert.AreEqual("T_ROUTE", evs.Get(correct, "target_id")); Assert.AreEqual("4.5000", evs.Get(correct, "value_num")); Assert.AreEqual("ok", evs.Get(correct, "value_str"));
        var wrong = new List<List<string>>(evs.Where("event_type", "incorrect_selection"))[0];
        Assert.AreEqual("D2", evs.Get(wrong, "label")); Assert.AreEqual("T_OTHER", evs.Get(wrong, "target_id"));
        var tp = new List<List<string>>(evs.Where("event_type", "teleport"))[0];
        Assert.AreEqual("1.0000", evs.Get(tp, "from_pos_x")); Assert.AreEqual("6.0000", evs.Get(tp, "to_pos_z"));
        var rc = new List<List<string>>(evs.Where("event_type", "river_crossing"))[0];
        Assert.AreEqual("7.0000", evs.Get(rc, "from_pos_x"));
        foreach (var r in evs.Rows) Assert.AreEqual(Get<string>(rig.dcm, "SessionId"), evs.Get(r, "session_id"));
    }

    // ====================================================================================== Test B: NPC events excluded, present in npc_events

    [UnityTest]
    public IEnumerator Routing_FullNpcLifecycle_NeverReachesEventsCsv_AndIsCompleteInNpcEvents()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = true, compact = true, attractor = true, prewarm = 1, maxAgents = 1, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        Component pa = ActiveAgents(rig)[0];
        string s1 = Lid(pa);
        yield return new WaitForSecondsRealtime(0.8f);
        Call(pa.GetComponent(T("UncertainBehavior")), "TriggerPauseNow", 0.4f);   // uncertain transition walking->pause (and back by its timer)
        EmitRepresentativeParticipantEvents(rig);
        yield return WaitUntil(() => File.Exists(rig.File("npc_events.csv")) && CountType(NpcEvents(rig), "npc_pool_return") > 0, 40f, "first lifetime to finish");
        yield return WaitUntil(() => Live(rig) == 1, 10f, "respawn so an open lifetime exists at the End panel");
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rig.em, "HandleEndPanelDisplayed", "Part3", true);
        yield return null; yield return null;

        var nev = NpcEvents(rig);
        AssertWidths(nev, "npc_events.csv");
        foreach (string t in new[] { "prewarm_created", "pool_acquired", "npc_spawn", "npc_movement_start", "npc_target_set", "npc_arrived", "npc_dwell_start", "npc_dwell_end",
                                     "npc_dest_change", "npc_uncertain_state", "npc_itinerary_complete", "npc_despawn", "npc_pool_return", "npc_lifetime_open_at_end" })
            Assert.GreaterOrEqual(CountType(nev, t), 1, "NPC event missing from npc_events.csv: " + t);
        Assert.AreEqual(0, CountType(nev, "npc_leg_start"), "retired event");

        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        foreach (string t in new[] { "prewarm_created", "pool_acquired", "npc_spawn", "npc_despawn", "npc_pool_return", "npc_target_set", "npc_dest_change", "npc_leg_start",
                                     "npc_movement_start", "npc_entrance_teleport", "npc_arrived", "npc_dwell_start", "npc_dwell_end", "npc_itinerary_complete", "npc_uncertain_state",
                                     "npc_lifetime_open_at_end" })
            Assert.AreEqual(0, CountType(evs, t), t + " must not be in events.csv");

        // lifetime IDs agree across the NPC streams
        var lt = Lifetimes(rig); var st = NpcState(rig);
        var ids = new HashSet<string>(); foreach (var r in lt.Rows) ids.Add(lt.Get(r, "npc_spawn_instance_id"));
        Assert.IsTrue(ids.Contains(s1));
        foreach (var r in nev.Rows) { string id = nev.Get(r, "npc_spawn_instance_id"); if (id != "") Assert.IsTrue(ids.Contains(id), "npc_events lifetime not in npc_lifetimes: " + id); }
        foreach (var r in st.Rows) Assert.IsTrue(ids.Contains(st.Get(r, "npc_spawn_instance_id")));
        AssertWidths(lt, "npc_lifetimes.csv"); AssertWidths(st, "npc_state.csv");

        string manifest = File.ReadAllText(rig.File("session_manifest.json"));
        Call(rig.dcm, "WriteManifestEnd", "test");
        manifest = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"schema_version\": 7", manifest);
        StringAssert.Contains("\"events_csv_npc_rows_rejected\": 0", manifest);
        StringAssert.Contains("events.csv holds participant actions", manifest);
    }

    // ====================================================================================== guard paths

    [UnityTest]
    public IEnumerator Routing_NpcEventsOnSecondaryPaths_AreRefusedBeforeEventsCsv()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        int before = Events(rig).Rows.Count;

        // (1) a generic experiment callback that carries an NPC-subject type
        RaiseExperimentEvent(rig, "npc_arrived", "Part3");
        RaiseExperimentEvent(rig, "npc_spawn", "Part3");
        // (2) a direct call of the events.csv writer with an NPC type, and with a participant type that smuggles NPC identity
        object[] args(string type, string npcId, string lifetime) => new object[] { type, "Part3", "d1_search", "", "", "", npcId, lifetime, null, "x", Vector3.zero, Vector3.zero, double.NaN, 0L, "" };
        Call(rig.dcm, "WriteEventRow", args("npc_despawn", "", ""));
        Call(rig.dcm, "WriteEventRow", args("building_click", "NPC-000001", ""));
        Call(rig.dcm, "WriteEventRow", args("building_click", "", "NPC-000001-S1"));
        // (3) a genuine participant event in the same breath is still written
        Call(rig.dcm, "WriteEventRow", args("building_click", "", ""));
        yield return null;

        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        Assert.AreEqual(before + 1, evs.Rows.Count, "only the clean participant row was added");
        Call(rig.dcm, "WriteManifestEnd", "test");
        StringAssert.Contains("\"events_csv_npc_rows_rejected\": 5", File.ReadAllText(rig.File("session_manifest.json")), "all five NPC-carrying attempts were refused and counted");
    }

    // ====================================================================================== Test C: master switch off

    [UnityTest]
    public IEnumerator Routing_MasterSwitchOff_ParticipantEventsAndGenericGazeContinue_NpcStreamsStayEmpty()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, npcCaptureEnabled = false, selectedPartFallback = true, withUncertain = true, uncertainEnabled = true, prewarm = 2, maxAgents = 2, npcSpeed = 2f }, r => rig = r);
        RaiseExperimentEvent(rig, "npc_warmup_start", "Part2");
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "Part 2 NPCs");
        Component npc = ActiveAgents(rig)[0];
        Call(npc.GetComponent(T("UncertainBehavior")), "TriggerPauseNow", 0.3f);   // an NPC behaviour transition that must not be recorded anywhere
        EmitRepresentativeParticipantEvents(rig);   // also begins a trial (generic gaze requires one)

        var head = Make("Head"); head.transform.position = new Vector3(0f, 0.9f, -3f);
        var nav = npc.GetComponent<NavMeshAgent>(); Call(npc.GetComponent(MoveType), "Stop");
        var rec = Make("Gaze").AddComponent(RecorderType);
        Set(rec, "headTransform", head.transform); Set(rec, "sampleRate", 60f); Set(rec, "enableDebugLogs", false); Set(rec, "experimentManager", rig.em);
        Call(rig.em, "BeginTrial");
        yield return null;
        Call(rec, "StartRecording");
        float until = Time.realtimeSinceStartup + 0.5f;
        while (Time.realtimeSinceStartup < until) { nav.Warp(new Vector3(0f, 0.1f, 0f)); yield return null; }
        Call(rec, "StopRecording");
        RaiseExperimentEvent(rig, "npc_warmup_end", "Part2");
        yield return null;

        foreach (string f in NpcFiles) Assert.IsFalse(File.Exists(rig.File(f)), f + " must not exist");
        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        foreach (string t in new[] { "building_click", "correct_selection", "teleport", "trial_start", "trial_end", "app_pause", "panel_shown" })
            Assert.GreaterOrEqual(CountType(evs, t), 1, "participant event must not depend on npcCaptureEnabled: " + t);
        Assert.AreEqual(1, CountType(evs, "npc_warmup_start"), "the warm-up window marker is an experiment-workflow event: independent of the NPC switch");
        Assert.AreEqual(1, CountType(evs, "npc_warmup_end"));
        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        Assert.Greater(gaze.Rows.Count, 0, "generic gaze keeps recording");
        foreach (var r in gaze.Rows) { Assert.AreEqual("", gaze.Get(r, "hit_npc_id")); Assert.AreEqual("", gaze.Get(r, "hit_npc_spawn_instance_id")); }
        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"));
        Call(rig.dcm, "WriteManifestEnd", "test");
        string m = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"events_csv_npc_rows_rejected\": 0", m);   // stopped at the gate, never reached the events.csv guard
        Assert.Greater(Get<int>(rig.dcm, "NpcEventsSuppressedByMasterSwitch"), 0);
        foreach (var pa in ActiveAgents(rig)) Assert.IsTrue(pa != null);   // simulation alive
    }

    // ====================================================================================== Test D: same frame

    [UnityTest]
    public IEnumerator Routing_ParticipantAndNpcEventsInTheSameFrame_AreEachKeptOnceInTheirOwnStream()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { withUncertain = true, uncertainEnabled = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.5f);
        Component pa = ActiveAgents(rig)[0];
        string id = Lid(pa);
        var selGo = Make("BAT_SAME"); selGo.AddComponent<BoxCollider>(); var sel = selGo.AddComponent(T("TargetSelectable")); Set(sel, "targetId", "T_SAME");
        int frame0 = Time.frameCount;
        double t0 = Time.realtimeSinceStartupAsDouble;

        // one frame: participant click + NPC state change + NPC lifetime end
        Call(rig.dcm, "OnTargetSelectableClicked", sel);
        Call(pa.GetComponent(T("UncertainBehavior")), "TriggerPauseNow", 30f);
        Call(pa, "ForceFinish");
        Assert.AreEqual(frame0, Time.frameCount);
        yield return null; yield return null;

        var evs = Events(rig); var nev = NpcEvents(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        var clicks = new List<List<string>>(evs.Where("event_type", "building_click"));
        Assert.AreEqual(1, clicks.Count, "the participant click is neither lost nor duplicated");
        Assert.AreEqual("T_SAME", evs.Get(clicks[0], "target_id"));
        var life = LifeEvents(nev, id);
        foreach (string t in new[] { "npc_uncertain_state", "npc_itinerary_complete", "npc_despawn", "npc_pool_return" })
            Assert.AreEqual(1, life.FindAll(r => nev.Get(r, "event_type") == t && D(nev.Get(r, "t_abs")) >= t0 - 0.001).Count, t + " exactly once in npc_events (from this frame on)");
        double tc = D(evs.Get(clicks[0], "t_abs"));
        foreach (var r in life) if (nev.Get(r, "event_type") == "npc_uncertain_state" && D(nev.Get(r, "t_abs")) >= t0 - 0.001) Assert.Less(Math.Abs(D(nev.Get(r, "t_abs")) - tc), 0.05, "same frame -> timestamps within a few ms");
        Assert.GreaterOrEqual(tc, t0 - 0.001);
    }

    // ====================================================================================== Test E: buffering and replay

    [UnityTest]
    public IEnumerator Routing_EventsBufferedBeforeTheRecorder_ReplayToNpcEventsOnly_WhenEnabled()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, createDcm = false }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs to spawn with no recorder");
        Assert.Greater(GetStatic<int>(BusType, "PendingCount"), 0);
        double createdAt = Get<double>(ActiveAgents(rig)[0], "ObjectCreatedTAbs");
        var dcmGo = Make("LateDcm"); rig.dcm = dcmGo.AddComponent(DcmType);
        Set(rig.dcm, "outputRootOverride", _root); Set(rig.dcm, "writeFullBatiRegistry", false); Set(rig.dcm, "enableDebugLogs", false);
        yield return null; yield return new WaitForSecondsRealtime(0.2f);

        var evs = Events(rig); var nev = NpcEvents(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        Assert.AreEqual(0, CountType(evs, "prewarm_created")); Assert.AreEqual(0, CountType(evs, "npc_spawn"));
        Assert.AreEqual(3, CountType(nev, "prewarm_created"));
        Assert.IsTrue(new List<List<string>>(nev.Where("t_abs", F4(createdAt))).Count >= 1, "the replayed prewarm row keeps its original creation time");
        Assert.Greater(CountType(nev, "npc_spawn"), 0);
    }

    [UnityTest]
    public IEnumerator Routing_EventsBufferedBeforeTheRecorder_AreDiscarded_WhenCaptureIsDisabled_AndNothingLeaks()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 3, maxAgents = 3, createDcm = false, part2 = true, npcCaptureEnabled = false }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs to spawn with no recorder");
        Assert.Greater(GetStatic<int>(BusType, "PendingCount"), 0, "no recorder and no gate yet: queued");
        var dcmGo = Make("LateDcm"); rig.dcm = dcmGo.AddComponent(DcmType);
        Set(rig.dcm, "outputRootOverride", _root); Set(rig.dcm, "writeFullBatiRegistry", false); Set(rig.dcm, "enableDebugLogs", false);
        yield return null; yield return new WaitForSecondsRealtime(0.3f);

        Assert.AreEqual(0, GetStatic<int>(BusType, "PendingCount"), "the closed gate dropped what was queued");
        foreach (string f in NpcFiles) Assert.IsFalse(File.Exists(rig.File(f)), f + " must not exist");
        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        Assert.AreEqual(1, CountType(evs, "session_start"), "workflow events are unaffected");
    }
}
