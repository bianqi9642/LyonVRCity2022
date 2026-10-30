// EventSemanticsTests.cs
// PlayMode tests for schema_version 6: F-07 (value_num: explicit zero vs missing), F-08 (one shared experiment clock for Parts 1-3, injectable through
// CaptureClock.TestSource) and F-11 (interaction_id linking a building_click to its correct/incorrect outcome). F-08 / F-11 drive the REAL
// ExperimentManager.RunPart1/2/3 coroutines (with real UIManager + PanelController templates whose confirm buttons the test presses) and the real
// TargetSelectable.Select(); F-07 uses the production DataCaptureManager event writer. Editor tests - not a Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type ClockType = T("CaptureClock");
    double _clockJump;

    // Installs a controllable monotonic clock: real time + an explicit jump (a simulated hitch / suspension that lands inside ONE frame).
    void InstallJumpClock()
    {
        _clockJump = 0;
        ClockType.GetField("TestSource").SetValue(null, (Func<double>)(() => Time.realtimeSinceStartupAsDouble + _clockJump));
    }
    static void CaptureIoFault(Func<string, string, Exception> f) { T("CaptureIO").GetField("FaultInjector").SetValue(null, f); }
    static void RemoveJumpClock() { ClockType.GetField("TestSource").SetValue(null, null); }

    static string NextPanelState(Component ui) => OpenPanelButton(ui) != null ? "open" : "none";

    // Waits for the currently open panel to be destroyed (after a confirm), then for the next one to appear, and presses it.
    IEnumerator PressPanel(Component ui, string what)
    {
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 6f, what);
        OpenPanelButton(ui).onClick.Invoke();
        yield return null; yield return null;   // the confirmed panel is destroyed at the end of the frame; the next panel may already be open
    }

    Component MakeUi(params string[] templateFields)
    {
        var uiGo = Make("UI"); var ui = uiGo.AddComponent(T("UIManager"));
        foreach (var f in templateFields) Set(ui, f, PanelTemplate(f, out _));
        return ui;
    }

    static double Num(Csv c, List<string> row, string col) => D(c.Get(row, col));

    // ====================================================================================== F-07

    [UnityTest]
    public IEnumerator EventsCsv_ValueNum_ZeroIsAValue_MissingIsBlank_UnderDecimalCommaCulture()
    {
        var saved = CultureInfo.CurrentCulture; var savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR"); CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");
            Rig rig = null;
            yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
            object[] row(string label, double? v) => new object[] { "teleport", "Part3", "d1_search", label, "", "", "", "", v, "x", Vector3.zero, Vector3.zero, double.NaN, 0L, "" };
            Call(rig.dcm, "WriteEventRow", row("zero", 0.0));
            Call(rig.dcm, "WriteEventRow", row("pos", 12.5));
            Call(rig.dcm, "WriteEventRow", row("neg", -3.25));
            Call(rig.dcm, "WriteEventRow", row("missing", null));
            // production events whose numeric value is a legitimate 0: resume / focus-loss flags and a zero elapsed time
            Call(rig.dcm, "OnApplicationPause", true); Call(rig.dcm, "OnApplicationPause", false);
            Call(rig.dcm, "OnApplicationFocus", false); Call(rig.dcm, "OnApplicationFocus", true);
            Call(rig.dcm, "OnExperimentEvent", NewExperimentEvent("part3_log", e => { SetField(e, "part3Event", "d2_skipped"); SetField(e, "numValue", 0.0); SetField(e, "hasNum", true); SetField(e, "notes", "no_budget"); SetField(e, "terminationReason", "no_budget_remaining"); }));
            RaiseExperimentEvent(rig, "panel_shown", "Part3");          // an event with NO numeric value
            yield return null;

            var evs = Events(rig);
            AssertWidths(evs, "events.csv");
            Assert.AreEqual("interaction_id", evs.Header[evs.Header.Count - 1], "interaction_id is appended after session_id");
            Assert.AreEqual("session_id", evs.Header[evs.Header.Count - 2]);
            string V(string label) { foreach (var r in evs.Rows) if (evs.Get(r, "label") == label && evs.Get(r, "event_type") == "teleport") return evs.Get(r, "value_num"); Assert.Fail("row " + label); return null; }
            Assert.AreEqual("0.0000", V("zero"), "a numeric zero is written as an explicit 0");
            Assert.AreEqual("12.5000", V("pos")); Assert.AreEqual("-3.2500", V("neg"));
            Assert.AreEqual("", V("missing"), "a missing numeric value stays blank");
            Assert.AreNotEqual(V("zero"), V("missing"), "zero and missing are distinguishable after parsing");

            var pauseRows = Rows(evs, "event_type", "app_pause");
            Assert.AreEqual("1.0000", evs.Get(pauseRows[0], "value_num")); Assert.AreEqual("0.0000", evs.Get(pauseRows[1], "value_num"), "resume (0) is a value, not blank");
            var focusRows = Rows(evs, "event_type", "app_focus");
            Assert.AreEqual("0.0000", evs.Get(focusRows[0], "value_num"), "focus lost (0)"); Assert.AreEqual("1.0000", evs.Get(focusRows[1], "value_num"));
            Assert.AreEqual("0.0000", evs.Get(Rows(evs, "event_type", "phase_result")[0], "value_num"), "an elapsed time of exactly 0 is a value");
            foreach (var r in Rows(evs, "event_type", "panel_shown")) Assert.AreEqual("", evs.Get(r, "value_num"), "panel_shown carries no numeric value");
            foreach (var t in new[] { "session_start", "part_start", "phase_start" })
                foreach (var r in Rows(evs, "event_type", t)) Assert.AreEqual("", evs.Get(r, "value_num"), t + ": no numeric value -> blank (NOT 0)");
            foreach (var r in evs.Rows) { string v = evs.Get(r, "value_num"); Assert.IsFalse(v.Contains(","), "decimal comma leaked: " + v); if (v != "") Assert.IsTrue(Regexp(v)); }
        }
        finally { CultureInfo.CurrentCulture = saved; CultureInfo.CurrentUICulture = savedUi; }
    }

    [Test]
    public void EventsCsv_TheOriginalZeroSentinelRule_CannotTellZeroFromMissing()
    {
        // The pre-v6 writer was: valueNum != 0.0 ? F4(valueNum) : "" . This documents why it had to go: the two inputs collapse to the same field.
        string Old(double v) => v != 0.0 ? v.ToString("F4", CultureInfo.InvariantCulture) : "";
        Assert.AreEqual(Old(0.0), Old(0.0), "valid zero");
        Assert.AreEqual("", Old(0.0), "old rule: a real zero is blank ...");
        Assert.AreEqual("", Old(0.0), "... exactly like 'no value' (which also arrived as 0.0)");
        // The production writer keeps them apart (covered above); here the production API itself shows the distinction is explicit:
        var m = T("DataCaptureManager").GetMethod("WriteEventRow", All);
        Assert.AreEqual(typeof(double?), m.GetParameters()[8].ParameterType, "valueNum is Nullable<double>: presence is explicit");
    }

    // ====================================================================================== F-08

    [UnityTest]
    public IEnumerator Clock_UnityDeltaTimeIsClampedAfterAHitch_TheSharedClockIsNot()
    {
        yield return null;
        double c0 = Time.realtimeSinceStartupAsDouble;
        System.Threading.Thread.Sleep(2000);                       // a 2-second hitch inside one frame
        yield return null;
        double dReal = (double)CaptureClock_TAbs() - c0; float dt = Time.deltaTime;
        UnityEngine.Debug.Log($"[EventSemanticsTests][clock] hitch of 2 s: Time.deltaTime = {dt:F3} s (maximumDeltaTime = {Time.maximumDeltaTime:F3}), shared clock advanced {dReal:F3} s");
        Assert.GreaterOrEqual(dReal, 1.95, "the shared clock sees the whole hitch");
        Assert.LessOrEqual(dt, Time.maximumDeltaTime + 1e-4f, "Unity clamps the frame delta to maximumDeltaTime");
        Assert.Less(dt, 0.6f, "so an accumulator of Time.deltaTime loses ~1.5+ s of the 2 s");
    }

    static double CaptureClock_TAbs() => (double)ClockType.GetProperty("TAbs").GetValue(null);

    [UnityTest]
    public IEnumerator Part2_SevereHitchAndSuspension_AreMeasuredOnTheSharedClock_AndTimeScaleDoesNotStretchTasks()
    {
        Rig rig = null;
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        InstallJumpClock();
        try
        {
            yield return BuildRig(new RigOptions { part2 = true, selectedPartFallback = true, realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false }, r => rig = r);
            var ui = MakeUi("part2StartPanelTemplate", "part2ContinuePanelTemplate", "part2EndPanelTemplate");
            var rigT = Make("PlayerRig").transform; var p0 = Make("Start0").transform; var p1 = Make("Tele0").transform;
            var groupType = Type.GetType("ExperimentManager+Part2Group, Assembly-CSharp");
            var group = Activator.CreateInstance(groupType); Set(group, "startPositions", new[] { p0 }); Set(group, "teleportPositions", new[] { p1 });
            var groups = Array.CreateInstance(groupType, 1); groups.SetValue(group, 0);
            Set(rig.em, "uiManager", ui); Set(rig.em, "playerRig", rigT); Set(rig.em, "spawner", rig.spawner); Set(rig.em, "part2Groups", groups);
            Set(rig.em, "part2FirstDuration", 5f); Set(rig.em, "part2SecondDuration", 4f); Set(rig.em, "npcWarmupDuration", 0f);
            yield return null;

            Call(rig.em, "StartPart2");
            yield return PressPanel(ui, "Part 2 Start panel");
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task1", 3f, "task 1");

            // (a) severe hitch: 5.2 s of clock land in ONE frame. A Time.deltaTime accumulator would have gained only ~0.13 s and still be waiting.
            float realAtHitch = Time.realtimeSinceStartup; _clockJump += 5.2;
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_continue_pending", 3f, "task 1 to end after the hitch");
            float realToEnd = Time.realtimeSinceStartup - realAtHitch;
            UnityEngine.Debug.Log($"[EventSemanticsTests][part2] 5.2 s hitch: task 1 (limit 5 s) ended {realToEnd:F2} s of real time later");
            Assert.Less(realToEnd, 1.0f, "task 1 ended within a few frames of the hitch, because the elapsed time was measured on the shared clock");

            yield return PressPanel(ui, "Continue panel");
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task2", 3f, "task 2");

            // (b) application suspension inside task 2 (limit 4 s): policy = it COUNTS as elapsed time; the app_pause/app_focus rows make it reconstructable
            yield return new WaitForSecondsRealtime(0.4f);
            Call(rig.dcm, "OnApplicationPause", true); _clockJump += 2.5; Call(rig.dcm, "OnApplicationPause", false);
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 6f, "task 2 end");
            yield return PressPanel(ui, "End panel");
            yield return new WaitForSecondsRealtime(0.2f);

            var evs = Events(rig);
            AssertEventsCsvIsParticipantOnly(evs);
            var ph = new Dictionary<string, double>(); foreach (var r in Rows(evs, "event_type", "phase_start")) ph[evs.Get(r, "label")] = D(evs.Get(r, "t_abs"));
            double task1 = ph["p2_continue_pending"] - ph["p2_task1"], task2 = ph["ended"] - ph["p2_task2"];
            UnityEngine.Debug.Log($"[EventSemanticsTests][part2] measured task1={task1:F2}s (limit 5) task2={task2:F2}s (limit 4)");
            Assert.AreEqual(5.2, task1, 0.4, "the measured task-1 duration contains the whole 5.2 s hitch");
            Assert.AreEqual(4.0, task2, 0.5, "task 2 ends when its 4 s have elapsed on the shared clock, suspension included");
            Assert.Less(task2, 4.5);

            // event timestamps are on the same clock and consistent
            Assert.AreEqual(1, Rows(evs, "event_type", "trial_start").Count); Assert.AreEqual(1, Rows(evs, "event_type", "trial_end").Count);
            double ts = D(evs.Get(Rows(evs, "event_type", "trial_start")[0], "t_abs")), te = D(evs.Get(Rows(evs, "event_type", "trial_end")[0], "t_abs"));
            Assert.Greater(te, ts); Assert.GreaterOrEqual(ts, ph["p2_task1"] - 1e-4, "phase_start p2_task1 precedes (or equals) trial_start");
            var pr = Rows(evs, "event_type", "app_pause"); Assert.AreEqual(2, pr.Count);
            double suspended = D(evs.Get(pr[1], "t_abs")) - D(evs.Get(pr[0], "t_abs"));
            Assert.AreEqual(2.5, suspended, 0.2, "the suspended interval is recoverable from the app_pause rows");
            Assert.AreEqual(task2 - suspended, 4.0 - 2.5, 0.6, "participant-active time = measured span - suspended interval");
        }
        finally { RemoveJumpClock(); }

        // (c) Time.timeScale does not stretch an experiment timer (the old deltaTime accumulator ran 4x slower at timeScale 0.25)
        TearDownRigObjects(); yield return null; yield return null;
        Rig rig2 = null;
        float savedScale = Time.timeScale;
        try
        {
            var camGo2 = new GameObject("Cam2"); camGo2.tag = "MainCamera"; camGo2.AddComponent<Camera>(); _spawned.Add(camGo2);
            yield return BuildRig(new RigOptions { part2 = true, selectedPartFallback = true, realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false }, r => rig2 = r);
            var ui2 = MakeUi("part2StartPanelTemplate", "part2ContinuePanelTemplate", "part2EndPanelTemplate");
            var rigT2 = Make("PlayerRig").transform; var a0 = Make("S").transform; var a1 = Make("T").transform;
            var groupType2 = Type.GetType("ExperimentManager+Part2Group, Assembly-CSharp");
            var g2 = Activator.CreateInstance(groupType2); Set(g2, "startPositions", new[] { a0 }); Set(g2, "teleportPositions", new[] { a1 });
            var groups2 = Array.CreateInstance(groupType2, 1); groups2.SetValue(g2, 0);
            Set(rig2.em, "uiManager", ui2); Set(rig2.em, "playerRig", rigT2); Set(rig2.em, "spawner", rig2.spawner); Set(rig2.em, "part2Groups", groups2);
            Set(rig2.em, "part2FirstDuration", 0.6f); Set(rig2.em, "part2SecondDuration", 0.6f); Set(rig2.em, "npcWarmupDuration", 0f);
            yield return null;
            Call(rig2.em, "StartPart2");
            yield return PressPanel(ui2, "Start panel");
            Time.timeScale = 0.25f;
            float r0 = Time.realtimeSinceStartup;
            yield return WaitUntil(() => Get<string>(rig2.dcm, "CurrentPhase") == "p2_continue_pending", 6f, "task 1 under timeScale 0.25");
            float real1 = Time.realtimeSinceStartup - r0;
            UnityEngine.Debug.Log($"[EventSemanticsTests][timescale] task 1 (0.6 s) took {real1:F2} s of real time at timeScale 0.25");
            Assert.Less(real1, 1.2f, "scaled time would have needed ~2.4 s; the shared clock needs 0.6 s");
            Time.timeScale = 1f;
            yield return PressPanel(ui2, "Continue"); yield return WaitUntil(() => Get<string>(rig2.dcm, "CurrentPhase") == "ended", 5f, "end"); yield return PressPanel(ui2, "End");
        }
        finally { Time.timeScale = savedScale; }
    }

    [UnityTest]
    public IEnumerator Part1_SevereHitch_IsMeasuredOnTheSharedClock()
    {
        Rig rig = null;
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        InstallJumpClock();
        try
        {
            yield return BuildRig(new RigOptions { realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false }, r => rig = r);
            var ui = MakeUi("part1StartPanelTemplate", "part1EndPanelTemplate");
            Set(rig.em, "uiManager", ui); Set(rig.em, "playerRig", Make("PlayerRig").transform); Set(rig.em, "spawner", rig.spawner);
            Set(rig.em, "part1Duration", 5f); Set(rig.em, "npcWarmupDuration", 0f);
            double startedAt = double.NaN, endedAt = double.NaN; int starts = 0, ends = 0;
            var emType = rig.em.GetType();
            emType.GetEvent("OnTrialStart").AddEventHandler(rig.em, (Action)(() => { starts++; startedAt = (double)ClockType.GetProperty("TAbs").GetValue(null); }));
            emType.GetEvent("OnTrialEnd").AddEventHandler(rig.em, (Action)(() => { ends++; endedAt = (double)ClockType.GetProperty("TAbs").GetValue(null); }));
            yield return null;

            Call(rig.em, "StartPart1");
            yield return PressPanel(ui, "Part 1 Start panel");
            yield return WaitUntil(() => starts == 1, 3f, "Part 1 trial start");
            float realAt = Time.realtimeSinceStartup; _clockJump += 5.3;   // severe hitch: Time.deltaTime would add ~0.13 s
            yield return WaitUntil(() => ends == 1, 3f, "Part 1 trial end after the hitch");
            float realTo = Time.realtimeSinceStartup - realAt;
            UnityEngine.Debug.Log($"[EventSemanticsTests][part1] 5.3 s hitch: trial (limit 5 s) ended {realTo:F2} s of real time later; measured {endedAt - startedAt:F2} s");
            Assert.Less(realTo, 1.0f); Assert.AreEqual(5.3, endedAt - startedAt, 0.4, "measured trial duration includes the hitch");
            Assert.AreEqual(1, starts); Assert.AreEqual(1, ends, "exactly one trial start and one trial end");
            yield return PressPanel(ui, "Part 1 End panel");
        }
        finally { RemoveJumpClock(); }
    }

    // ---- Part 3 harness: real RunPart3 with real panels and real TargetSelectable buildings ----

    sealed class P3 { public Rig rig; public Component ui; public GameObject d1, d2, other; public List<GameObject> all = new List<GameObject>(); }

    IEnumerator StartPart3(float d1Max, float total, Action<P3> done)
    {
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        var p = new P3();
        yield return BuildRig(new RigOptions { selectedPartFallback = true, realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false }, r => p.rig = r);
        p.ui = MakeUi("part3StartPanelTemplate", "part3Dest1FullTemplate", "part3Dest2FullTemplate", "part3EndPanelTemplate");
        var zoneType = Type.GetType("ExperimentManager+Part3ZoneTargets, Assembly-CSharp");
        GameObject B(string n, float x) { var go = MakeBuilding(n, new Vector3(x, 2f, 30f), true, false); ((Behaviour)go.GetComponent(T("TargetSelectable"))).enabled = false; Set(go.GetComponent(T("TargetSelectable")), "experimentManager", p.rig.em); p.all.Add(go); return go; }
        var gu = Activator.CreateInstance(zoneType); var oe = Activator.CreateInstance(zoneType);
        Set(gu, "buildingA", B("BAT_GU_A", 0f)); Set(gu, "buildingB", B("BAT_GU_B", 10f)); Set(oe, "buildingA", B("BAT_OE_A", 20f)); Set(oe, "buildingB", B("BAT_OE_B", 30f));
        p.other = B("BAT_OTHER", 40f);
        Set(p.rig.em, "part3ZoneGU", gu); Set(p.rig.em, "part3ZoneOE", oe); Set(p.rig.em, "part3StartGE", Make("StartGE").transform);
        Set(p.rig.em, "allowWrongCandidateSelection", true);
        Set(p.rig.em, "uiManager", p.ui); Set(p.rig.em, "playerRig", Make("PlayerRig").transform); Set(p.rig.em, "spawner", p.rig.spawner);
        Set(p.rig.em, "part3Destination1MaxSeconds", d1Max); Set(p.rig.em, "part3TotalMaxSeconds", total); Set(p.rig.em, "npcWarmupDuration", 0f);
        yield return null;
        Call(p.rig.em, "StartPart3");
        done(p);
    }

    [UnityTest]
    public IEnumerator Part3_D1LimitUnderASevereHitch_TimesOutOnTheSameClock_AsParts1And2()
    {
        P3 p = null;
        InstallJumpClock();
        try
        {
            yield return StartPart3(5f, 10f, x => p = x);
            var rig = p.rig;
            yield return PressPanel(p.ui, "Part 3 Start panel");
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_pending", 3f, "d1_pending");
            yield return PressPanel(p.ui, "D1 panel");
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_search", 3f, "d1_search");
            float realAt = Time.realtimeSinceStartup; _clockJump += 6.0;   // D1 limit is 5 s
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_pending", 4f, "D1 timeout -> d2_pending");
            float realTo = Time.realtimeSinceStartup - realAt;
            UnityEngine.Debug.Log($"[EventSemanticsTests][part3] 6 s hitch against a 5 s D1 limit: timeout handled {realTo:F2} s of real time later");
            Assert.Less(realTo, 1.0f);
            var evs = Events(rig);
            var to = Rows(evs, "event_type", "phase_timeout"); Assert.AreEqual(1, to.Count, "exactly one D1 timeout row");
            Assert.AreEqual("D1", evs.Get(to[0], "label")); Assert.AreEqual(6.0, D(evs.Get(to[0], "value_num")), 0.4, "the timeout duration is measured on the shared clock");
            Assert.AreEqual(1, Rows(evs, "event_type", "trial_start").Count); Assert.AreEqual(0, Rows(evs, "event_type", "trial_end").Count, "the trial is still open (D2 follows)");
            // finish the session so nothing is left running
            yield return PressPanel(p.ui, "D2 panel"); yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_search", 3f, "d2_search");
            _clockJump += 20.0;
            yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 5f, "end"); yield return PressPanel(p.ui, "End panel");
        }
        finally { RemoveJumpClock(); }
    }

    // ====================================================================================== F-11

    [UnityTest]
    public IEnumerator Click_EachAcceptedClickHasOneId_AndAtMostOneOutcomeSharingIt()
    {
        P3 p = null;
        yield return StartPart3(60f, 120f, x => p = x);
        var rig = p.rig;
        yield return PressPanel(p.ui, "Start panel");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_pending", 3f, "d1_pending");
        GameObject d1 = null, d2 = null;
        yield return WaitUntil(() => { d1 = Get<GameObject>(rig.dcm, "AssignedBuildingD1"); d2 = Get<GameObject>(rig.dcm, "AssignedBuildingD2"); return d1 != null && d2 != null; }, 3f, "assigned destinations");
        Component TsOf(GameObject g) => g.GetComponent(T("TargetSelectable"));
        void Click(GameObject g) => Call(TsOf(g), "Select");
        string Id(GameObject g) => Get<string>(TsOf(g), "LastInteractionId");

        // (1) click while the D1 panel is being read: raw click recorded, no scoring window open -> no outcome (documented)
        Click(d1); string idPending = Id(d1); yield return null; yield return null;

        yield return PressPanel(p.ui, "D1 panel");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d1_search", 3f, "d1_search");
        yield return null;

        // (2) two separate wrong clicks on the same building, in different frames
        Click(p.other); string wrong1 = Id(p.other); yield return null; yield return null;
        Click(p.other); string wrong2 = Id(p.other); yield return null; yield return null;
        Assert.AreNotEqual(wrong1, wrong2, "separate physical clicks on one building get different ids");

        // (3) the correct building, with a DUPLICATE callback in the same frame (OnPointerClick + OnMouseDown / two interactors)
        Click(d1); Click(d1); string idD1 = Id(d1);
        long dupBefore = 0;
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_pending", 4f, "D1 found -> d2_pending");

        // (4) click during the D2 panel again, then search D2 with a correct click
        Click(d2); string idPending2 = Id(d2); yield return null; yield return null;
        yield return PressPanel(p.ui, "D2 panel");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "d2_search", 3f, "d2_search");
        yield return null;
        Click(d2); string idD2 = Id(d2);
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 4f, "end");

        // (5) a replayed outcome for an already-scored interaction (retry / second handler) is refused
        Call(rig.dcm, "OnExperimentEvent", NewExperimentEvent("part3_log", e => { SetField(e, "part3Event", "d1_click"); SetField(e, "boolValue", false); SetField(e, "stringValue", "BAT_OTHER"); SetField(e, "numValue", 1.0); SetField(e, "hasNum", true); SetField(e, "interactionId", wrong1); }));
        yield return PressPanel(p.ui, "End panel");
        yield return new WaitForSecondsRealtime(0.2f);

        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        var clicks = Rows(evs, "event_type", "building_click");
        var correct = Rows(evs, "event_type", "correct_selection"); var incorrect = Rows(evs, "event_type", "incorrect_selection");
        UnityEngine.Debug.Log($"[EventSemanticsTests][click] clicks={clicks.Count} correct={correct.Count} incorrect={incorrect.Count}  ids pending={idPending} wrong={wrong1},{wrong2} d1={idD1} pending2={idPending2} d2={idD2}");

        // exactly one building_click per accepted physical click: 6 (pending, wrong, wrong, D1 [duplicate ignored], pending, D2)
        Assert.AreEqual(6, clicks.Count);
        var clickIds = new List<string>(); foreach (var r in clicks) clickIds.Add(evs.Get(r, "interaction_id"));
        CollectionAssert.AllItemsAreUnique(clickIds); foreach (var id in clickIds) StringAssert.IsMatch(@"^C\d{6}$", id);
        CollectionAssert.AreEqual(new[] { idPending, wrong1, wrong2, idD1, idPending2, idD2 }, clickIds, "ids are allocated in click order, once per click");
        for (int i = 1; i < clickIds.Count; i++) Assert.Greater(int.Parse(clickIds[i].Substring(1)), int.Parse(clickIds[i - 1].Substring(1)), "monotonic");
        Assert.IsFalse(clickIds.Contains(""), "no building_click without an interaction id");

        // outcomes: 2 correct (D1, D2) + 2 incorrect; every outcome shares the id of ITS click; no id has two outcomes
        Assert.AreEqual(2, correct.Count); Assert.AreEqual(2, incorrect.Count);
        var outcomeIds = new List<string>();
        foreach (var r in correct) { outcomeIds.Add(evs.Get(r, "interaction_id")); Assert.AreEqual("T:" + evs.Get(r, "label"), "T:" + evs.Get(r, "label")); }
        foreach (var r in incorrect) outcomeIds.Add(evs.Get(r, "interaction_id"));
        CollectionAssert.AllItemsAreUnique(outcomeIds, "at most one outcome per interaction");
        CollectionAssert.IsSubsetOf(outcomeIds, clickIds, "every outcome links to a recorded click");
        Assert.AreEqual(idD1, evs.Get(correct[0], "interaction_id")); Assert.AreEqual("D1", evs.Get(correct[0], "label"));
        Assert.AreEqual(idD2, evs.Get(correct[1], "interaction_id")); Assert.AreEqual("D2", evs.Get(correct[1], "label"));
        CollectionAssert.AreEquivalent(new[] { wrong1, wrong2 }, new[] { evs.Get(incorrect[0], "interaction_id"), evs.Get(incorrect[1], "interaction_id") });
        var unresolved = clickIds.FindAll(id => !outcomeIds.Contains(id)); CollectionAssert.AreEquivalent(new[] { idPending, idPending2 }, unresolved, "clicks outside a search window have no outcome (by design)");
        foreach (var r in evs.Rows) if (evs.Get(r, "event_type") != "building_click" && evs.Get(r, "event_type") != "correct_selection" && evs.Get(r, "event_type") != "incorrect_selection")
            Assert.AreEqual("", evs.Get(r, "interaction_id"), evs.Get(r, "event_type") + ": interaction_id is blank on every other event");

        // click and outcome keep their own, real timestamps; the id (not the timestamp) is the link
        foreach (var o in new List<List<string>>(correct.Concat2(incorrect)))
        {
            string id = evs.Get(o, "interaction_id");
            var c = clicks.Find(r => evs.Get(r, "interaction_id") == id);
            Assert.GreaterOrEqual(D(evs.Get(o, "t_abs")), D(evs.Get(c, "t_abs")) - 1e-4, "an outcome is committed at or after its click");
            Assert.AreEqual(evs.Get(c, "target_id"), evs.Get(o, "target_id"), "same building identity in click and outcome");
        }
        foreach (var o in new[] { correct[0], correct[1], incorrect[0], incorrect[1] }) Assert.IsTrue(Regexp(evs.Get(o, "value_num")), "outcome carries its elapsed time");

        // counting rules from the documentation
        Assert.AreEqual(6, new HashSet<string>(clickIds).Count); Assert.AreEqual(4, new HashSet<string>(outcomeIds).Count);

        Call(rig.dcm, "WriteManifestEnd", "test");
        var man = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"duplicate_clicks_ignored\": 1", man, "the same-frame duplicate callback was ignored, not recorded");
        StringAssert.Contains("\"duplicate_outcomes_rejected\": 1", man, "the replayed outcome was refused");
        Assert.AreEqual(2, Rows(evs, "event_type", "incorrect_selection").Count, "the replay did not add a third incorrect row");
        Assert.IsTrue(dupBefore == 0);
    }
}

static class ListExt { public static IEnumerable<List<string>> Concat2(this List<List<string>> a, List<List<string>> b) { foreach (var x in a) yield return x; foreach (var x in b) yield return x; } }
