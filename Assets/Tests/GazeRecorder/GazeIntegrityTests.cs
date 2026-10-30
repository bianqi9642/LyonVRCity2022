// GazeIntegrityTests.cs
// PlayMode tests for schema_version 5: F-05 (AOI current target), F-06 (phase labels incl. the REAL Part 2 flow), F-09 (manifest = evidence), F-16 (invalid gaze vectors),
// F-18 (achieved sampling rates). They drive the production GazeRecorder / AoiClassifier / DataCaptureManager / ExperimentManager / SamplingStats through reflection and
// read the files that are really written. SamplingStats is also tested with deterministic synthetic timestamps. Editor tests - not a Quest test.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using static Reflect;

public partial class NpcLifecycleTests
{
    static readonly Type StatsType = T("SamplingStats");
    static readonly Type InjType = Type.GetType("GazeRecorder+EyeInjection, Assembly-CSharp");

    static object Inj(float conf, Vector3 origin, Vector3 dir)
    {
        var x = Activator.CreateInstance(InjType);
        Set(x, "confidence", conf); Set(x, "origin", origin); Set(x, "direction", dir);
        return x;
    }

    // Recorder wired to the rig's ExperimentManager / DataCaptureManager (rows go to the session folder).
    Component MakeGaze(Rig rig, Transform head, float hz = 30f)
    {
        var rec = Make("Gaze").AddComponent(RecorderType);
        Set(rec, "headTransform", head); Set(rec, "sampleRate", hz); Set(rec, "enableDebugLogs", false); Set(rec, "experimentManager", rig.em);
        return rec;
    }

    // Standalone recorder (no DCM) in front of a wall, for the F-16 vector tests.
    Component MakeStandaloneGaze(string file, out Transform head)
    {
        head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = "FallbackWall"; _spawned.Add(wall);
        wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(50f, 50f, 0.2f);
        var rec = Make("StandaloneGaze").AddComponent(RecorderType);
        Set(rec, "headTransform", head); Set(rec, "sampleRate", 100f); Set(rec, "enableDebugLogs", false);
        Set(rec, "subjectId", "S"); Set(rec, "trialId", "T"); Set(rec, "outputPathOverride", Path.Combine(_root, file));
        return rec;
    }

    static object HitOf(object info, string field) => info.GetType().GetField(field).GetValue(info);

    // ====================================================================================== F-16: invalid gaze vectors

    [UnityTest]
    public IEnumerator Gaze_InvalidEyeVectors_AreBlank_FlagsStayAuthoritative_AndFallbackIsMarked()
    {
        var rec = MakeStandaloneGaze("f16.csv", out Transform head);
        yield return null;
        string path = Get<string>(rec, "outputPathOverride");
        Call(rec, "StartRecording");

        var good = Inj(0.9f, new Vector3(0.5f, 1f, -5f), Vector3.forward);
        var goodR = Inj(0.8f, new Vector3(-0.5f, 1f, -5f), Vector3.forward);
        var bad = Inj(0.1f, new Vector3(7f, 7f, 7f), new Vector3(0.1f, 0.2f, 0.9f));   // plausible-looking numbers, but below the confidence threshold
        var zeroOrigin = Inj(0.9f, Vector3.zero, Vector3.forward);                       // VALID vector whose origin happens to be (0,0,0)
        var zeroDir = Inj(0.9f, new Vector3(0f, 1f, -5f), new Vector3(1f, 0f, 0f));      // VALID, direction has zero components

        var scenarios = new List<(string name, object l, object r)>
        {
            ("both_valid", good, goodR), ("left_invalid", bad, goodR), ("right_invalid", good, bad),
            ("both_unavailable", null, null), ("both_low_confidence", bad, bad), ("zero_origin_valid", zeroOrigin, null), ("zero_dir_valid", zeroDir, zeroDir)
        };
        var ranges = new Dictionary<string, (int from, int to)>();
        foreach (var sc in scenarios)
        {
            int from = Csv.Load(path).Rows.Count;
            Set(rec, "testLeftEye", sc.l); Set(rec, "testRightEye", sc.r);
            yield return new WaitForSecondsRealtime(0.12f);
            ranges[sc.name] = (from, Csv.Load(path).Rows.Count);
            Assert.Greater(ranges[sc.name].to, ranges[sc.name].from, sc.name + ": rows were written");
        }
        Call(rec, "StopRecording");

        var csv = Csv.Load(path);
        AssertWidths(csv, "gaze.csv");
        Assert.AreEqual(66, csv.Header.Count);
        string[] lv = { "left_origin_x", "left_origin_y", "left_origin_z", "left_dir_x", "left_dir_y", "left_dir_z" };
        string[] rv = { "right_origin_x", "right_origin_y", "right_origin_z", "right_dir_x", "right_dir_y", "right_dir_z" };
        string[] cv = { "combined_origin_x", "combined_origin_y", "combined_origin_z", "combined_dir_x", "combined_dir_y", "combined_dir_z" };
        void AllBlank(List<string> r, string[] cols, string why) { foreach (var c in cols) Assert.AreEqual("", csv.Get(r, c), why + ": " + c + " must be blank"); }
        void AllFilled(List<string> r, string[] cols, string why) { foreach (var c in cols) Assert.IsTrue(Regexp(csv.Get(r, c)), why + ": " + c + " = '" + csv.Get(r, c) + "'"); }

        List<List<string>> Rows(string name) => csv.Rows.GetRange(ranges[name].from, ranges[name].to - ranges[name].from);

        foreach (var r in Rows("both_valid"))
        {
            Assert.AreEqual("1", csv.Get(r, "left_valid")); Assert.AreEqual("1", csv.Get(r, "right_valid")); Assert.AreEqual("1", csv.Get(r, "combined_valid"));
            AllFilled(r, lv, "valid left"); AllFilled(r, rv, "valid right"); AllFilled(r, cv, "valid combined");
            Assert.AreEqual("both", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("1", csv.Get(r, "ray_is_eye_gaze"));
            Assert.AreEqual("0.0000", csv.Get(r, "combined_origin_x"), "combined origin = mean of (0.5, -0.5)");
        }
        foreach (var r in Rows("left_invalid"))
        {
            Assert.AreEqual("0", csv.Get(r, "left_valid")); Assert.AreEqual("0.100", csv.Get(r, "left_confidence"), "the confidence value stays recorded");
            AllBlank(r, lv, "invalid left"); AllFilled(r, rv, "valid right"); Assert.AreEqual("right", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("1", csv.Get(r, "combined_valid"));
            Assert.AreEqual("-0.5000", csv.Get(r, "combined_origin_x"), "combined = the valid right eye only");
        }
        foreach (var r in Rows("right_invalid"))
        {
            Assert.AreEqual("0", csv.Get(r, "right_valid")); AllBlank(r, rv, "invalid right"); AllFilled(r, lv, "valid left");
            Assert.AreEqual("left", csv.Get(r, "gaze_ray_source"));
        }
        foreach (string name in new[] { "both_unavailable", "both_low_confidence" })
            foreach (var r in Rows(name))
            {
                Assert.AreEqual("0", csv.Get(r, "left_valid")); Assert.AreEqual("0", csv.Get(r, "right_valid")); Assert.AreEqual("0", csv.Get(r, "combined_valid"));
                AllBlank(r, lv, name); AllBlank(r, rv, name); AllBlank(r, cv, name);
                Assert.AreEqual("head_fallback", csv.Get(r, "gaze_ray_source"));
                Assert.AreEqual("0", csv.Get(r, "ray_is_eye_gaze"), "a head-fallback ray is never an eye-gaze ray");
                // the fallback ray survives as an explicitly named diagnostic: it starts at the head and points along it
                Assert.AreEqual("-5.0000", csv.Get(r, "ray_origin_z")); Assert.AreEqual("1.0000", csv.Get(r, "ray_dir_z"));
                // AOI of a raycast made from the fallback ray: the hit is recorded, flagged by the ray columns; legacy roi_* stay blank for invalid gaze
                Assert.AreEqual("1", csv.Get(r, "ray_did_hit")); Assert.AreEqual("FallbackWall", csv.Get(r, "hit_object_name"));
                Assert.AreEqual("", csv.Get(r, "roi_hit_name"), "legacy ROI columns are filled only for valid eye gaze");
            }
        foreach (var r in Rows("zero_origin_valid"))
        {
            Assert.AreEqual("1", csv.Get(r, "left_valid"));
            Assert.AreEqual("0.0000", csv.Get(r, "left_origin_x"), "a VALID zero coordinate is written as 0.0000, not blanked");
            Assert.AreEqual("0.0000", csv.Get(r, "left_origin_y")); Assert.AreEqual("0.0000", csv.Get(r, "left_origin_z"));
            Assert.AreEqual("0", csv.Get(r, "right_valid")); AllBlank(r, rv, "missing right next to a valid zero left");
            Assert.AreEqual("0.0000", csv.Get(r, "combined_origin_x"));
        }
        foreach (var r in Rows("zero_dir_valid"))
        {
            Assert.AreEqual("0.0000", csv.Get(r, "left_dir_y")); Assert.AreEqual("0.0000", csv.Get(r, "left_dir_z")); Assert.AreEqual("1.0000", csv.Get(r, "left_dir_x"));
            AllFilled(r, cv, "valid zero-component combined");
        }

        // An invalid vector cannot be mistaken for valid by its coordinates: the numeric content of "bad" never reaches the file.
        foreach (var r in csv.Rows) foreach (var c in lv) if (csv.Get(r, "left_valid") == "0") Assert.AreNotEqual("7.0000", csv.Get(r, c));

        // production counters agree with the file
        int validRows = 0, fallbackRows = 0, leftValid = 0, rightValid = 0;
        foreach (var r in csv.Rows) { if (csv.Get(r, "combined_valid") == "1") validRows++; else fallbackRows++; if (csv.Get(r, "left_valid") == "1") leftValid++; if (csv.Get(r, "right_valid") == "1") rightValid++; }
        Assert.AreEqual(csv.Rows.Count, Get<long>(rec, "RowsWritten"));
        Assert.AreEqual(validRows, Get<long>(rec, "RowsValidEye")); Assert.AreEqual(fallbackRows, Get<long>(rec, "RowsFallback"));
        Assert.AreEqual(leftValid, Get<long>(rec, "RowsLeftValid")); Assert.AreEqual(rightValid, Get<long>(rec, "RowsRightValid"));
        Assert.AreEqual(Get<long>(rec, "RowsWritten"), Get<long>(rec, "RowsValidEye") + Get<long>(rec, "RowsFallback"), "every written row is exactly one of valid / fallback");
    }

    static bool Regexp(string v) => System.Text.RegularExpressions.Regex.IsMatch(v, @"^-?\d+\.\d{4}$");

    // ====================================================================================== F-05: current target

    GameObject MakeBuilding(string name, Vector3 pos, bool withSelectable, bool selectableEnabled)
    {
        var go = Make(name); go.transform.position = pos;
        var col = go.AddComponent<BoxCollider>(); col.size = new Vector3(4f, 4f, 4f);
        if (withSelectable) { var ts = go.AddComponent(T("TargetSelectable")); ((Behaviour)ts).enabled = selectableEnabled; Set(ts, "targetId", name + "_id"); }
        return go;
    }

    void AssignDestinations(Rig rig, GameObject d1, GameObject d2)
        => Call(rig.dcm, "OnExperimentEvent", NewExperimentEvent("part3_log", e => { SetField(e, "part3Event", "assigned"); SetField(e, "d1Building", d1); SetField(e, "d2Building", d2); SetField(e, "d1TargetId", "d1_id"); SetField(e, "d2TargetId", "d2_id"); }));

    void SetPhaseEvt(Rig rig, string phase) => Call(rig.em, "RaiseEvt", "phase_start", "Part3", phase, "", null, false, null, Vector3.zero, Vector3.zero);

    object ClassifyHit(Rig rig, GameObject go) => CallStatic(AoiType, "Classify", true, go.GetComponent<Collider>(), rig.em, rig.dcm);

    [UnityTest]
    public IEnumerator Aoi_TargetBuilding_MeansTheCurrentTarget_NotEveryAssignedDestination()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");

        var d1 = MakeBuilding("BAT_D1", new Vector3(0f, 2f, 10f), true, false);
        var d2 = MakeBuilding("BAT_D2", new Vector3(10f, 2f, 10f), true, false);
        var other = MakeBuilding("BAT_OTHER", new Vector3(20f, 2f, 10f), true, false);        // selectable, never assigned
        var plain = MakeBuilding("PLAIN", new Vector3(30f, 2f, 10f), false, false);            // no selectable, never assigned
        // zone candidates, so non-current destinations keep the existing CANDIDATE_BUILDING class
        var zone = Activator.CreateInstance(T("ExperimentManager").GetNestedType("Part3ZoneTargets"));
        Set(zone, "buildingA", d1); Set(zone, "buildingB", d2); Set(rig.em, "part3ZoneGE", zone);
        AssignDestinations(rig, d1, d2);

        string Cls(GameObject g) => (string)HitOf(ClassifyHit(rig, g), "semanticClass");
        string Cur(GameObject g) => (string)HitOf(ClassifyHit(rig, g), "isCurrentTarget");
        string Role(GameObject g) => (string)HitOf(ClassifyHit(rig, g), "assignedDestination");

        // D1 panel being read: both destinations are assigned, neither is being searched
        SetPhaseEvt(rig, "d1_pending");
        Assert.AreNotEqual("TARGET_BUILDING", Cls(d1), "assigned but not yet searched"); Assert.AreEqual("0", Cur(d1)); Assert.AreEqual("D1", Role(d1));

        // D1 search: only D1 is current (its selectable is enabled by the experiment)
        SetPhaseEvt(rig, "d1_search"); ((Behaviour)d1.GetComponent(T("TargetSelectable"))).enabled = true;
        Assert.AreEqual("TARGET_BUILDING", Cls(d1)); Assert.AreEqual("1", Cur(d1)); Assert.AreEqual("D1", Role(d1));
        Assert.AreEqual("CANDIDATE_BUILDING", Cls(d2), "D2 is assigned for later: not the current target"); Assert.AreEqual("0", Cur(d2)); Assert.AreEqual("D2", Role(d2));
        Assert.AreEqual("BUILDING", Cls(other)); Assert.AreEqual("0", Cur(other)); Assert.AreEqual("", Role(other));
        Assert.AreNotEqual("TARGET_BUILDING", Cls(plain)); Assert.AreEqual("0", Cur(plain)); Assert.AreEqual("", Role(plain));
        // cached collider: asking again for D1 and then for D2 never leaks status between them
        Assert.AreEqual("TARGET_BUILDING", Cls(d1)); Assert.AreEqual("CANDIDATE_BUILDING", Cls(d2)); Assert.AreEqual("TARGET_BUILDING", Cls(d1));

        // D1 ends (selectable disabled again by the experiment) while the phase label is still d1_search: not current any more
        ((Behaviour)d1.GetComponent(T("TargetSelectable"))).enabled = false;
        Assert.AreEqual("0", Cur(d1), "target disabled -> no longer current, immediately");
        Assert.AreEqual("CANDIDATE_BUILDING", Cls(d1));

        // D2 search: the SAME colliders flip, the cache must not keep obsolete status
        string classBeforeFlip = Cls(d1);   // cache now holds d1
        SetPhaseEvt(rig, "d2_pending"); SetPhaseEvt(rig, "d2_search"); ((Behaviour)d2.GetComponent(T("TargetSelectable"))).enabled = true;
        Assert.AreEqual("TARGET_BUILDING", Cls(d2)); Assert.AreEqual("1", Cur(d2));
        Assert.AreEqual("CANDIDATE_BUILDING", Cls(d1), "D1 was a target earlier: it must not keep current-target status"); Assert.AreEqual("0", Cur(d1));
        Assert.AreEqual(classBeforeFlip, Cls(d1));

        // enable/disable transition of D2 mid-search
        ((Behaviour)d2.GetComponent(T("TargetSelectable"))).enabled = false; Assert.AreEqual("0", Cur(d2));
        ((Behaviour)d2.GetComponent(T("TargetSelectable"))).enabled = true; Assert.AreEqual("1", Cur(d2));

        // reassignment: new destinations replace the old ones
        var n1 = MakeBuilding("BAT_N1", new Vector3(0f, 2f, 20f), true, true); var n2 = MakeBuilding("BAT_N2", new Vector3(10f, 2f, 20f), true, false);
        AssignDestinations(rig, n1, n2); SetPhaseEvt(rig, "d1_search");
        Assert.AreEqual("1", Cur(n1)); Assert.AreEqual("0", Cur(d2), "the old D2 is no longer assigned or current"); Assert.AreEqual("", Role(d2)); Assert.AreEqual("0", Cur(n2));

        // phases that are not searches
        SetPhaseEvt(rig, "ended"); Assert.AreEqual("0", Cur(n1));

        // undeterminable: no DataCaptureManager -> blank, never a guess
        Assert.AreEqual("", (string)HitOf(CallStatic(AoiType, "Classify", true, n1.GetComponent<Collider>(), rig.em, null), "isCurrentTarget"));
        // nothing hit
        Assert.AreEqual("", (string)HitOf(CallStatic(AoiType, "Classify", false, null, rig.em, rig.dcm), "isCurrentTarget"));

        // NPC classification and lifetime linkage are intact
        Component pa = ActiveAgents(rig)[0];
        var npcInfo = CallStatic(AoiType, "Classify", true, pa.GetComponent<CapsuleCollider>(), rig.em, rig.dcm);
        Assert.AreEqual("NPC", (string)HitOf(npcInfo, "semanticClass")); Assert.AreEqual("0", (string)HitOf(npcInfo, "isCurrentTarget"));
        Assert.AreEqual(Lid(pa), (string)HitOf(npcInfo, "npcSpawnInstanceId")); Assert.AreEqual(Aid(pa), (string)HitOf(npcInfo, "npcId"));
    }

    // ====================================================================================== F-05 + F-06 + F-16 integration: gaze rows carry the right target status and phase

    [UnityTest]
    public IEnumerator Gaze_RowsCarryPhaseAndCurrentTarget_EvaluatedAtTheSampleTimestamp()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        var d1 = MakeBuilding("BAT_G1", new Vector3(0f, 2f, 10f), true, false);
        var d2 = MakeBuilding("BAT_G2", new Vector3(12f, 2f, 10f), true, false);
        AssignDestinations(rig, d1, d2);
        var head = Make("Head").transform; head.position = new Vector3(0f, 2f, 0f);
        var rec = MakeGaze(rig, head, 60f);
        yield return null;

        SetPhaseEvt(rig, "d1_pending");   // production order: phase first, then the trial opens
        Call(rig.em, "BeginTrial");
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));   // looking at D1
        yield return new WaitForSecondsRealtime(0.25f);                                   // panel reading: D1 is assigned, not searched
        SetPhaseEvt(rig, "d1_search"); ((Behaviour)d1.GetComponent(T("TargetSelectable"))).enabled = true;
        yield return new WaitForSecondsRealtime(0.25f);                                   // searching D1 while looking at D1
        var toD2 = (d2.transform.position - head.position).normalized;
        Set(rec, "testLeftEye", Inj(0.9f, head.position, toD2)); Set(rec, "testRightEye", Inj(0.9f, head.position, toD2));
        yield return new WaitForSecondsRealtime(0.25f);                                   // searching D1 while looking at D2
        ((Behaviour)d1.GetComponent(T("TargetSelectable"))).enabled = false;
        SetPhaseEvt(rig, "d2_pending"); SetPhaseEvt(rig, "d2_search"); ((Behaviour)d2.GetComponent(T("TargetSelectable"))).enabled = true;
        yield return new WaitForSecondsRealtime(0.25f);                                   // searching D2 while looking at D2
        Call(rig.em, "EndTrial");
        yield return null;

        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        var evs = Events(rig);
        // reconstruct the phase from the phase_start events by timestamp and compare with the label the recorder wrote
        var changes = new List<(double t, string phase)>();
        foreach (var r in Rows(evs, "event_type", "phase_start")) changes.Add((D(evs.Get(r, "t_abs")), evs.Get(r, "label")));
        var seen = new Dictionary<string, int>(); int hitsD1Current = 0, hitsD1Search_D2NotCurrent = 0, hitsD2Current = 0;
        foreach (var r in gaze.Rows)
        {
            double t = D(gaze.Get(r, "t_abs")); string expectPhase = "idle";
            foreach (var c in changes) if (c.t <= t + 1e-4) expectPhase = c.phase;
            string phase = gaze.Get(r, "phase");
            Assert.AreEqual(expectPhase, phase, "row @" + gaze.Get(r, "t_abs") + ": the label written at record time equals the label reconstructed from phase events");
            Assert.AreEqual("Part3", gaze.Get(r, "part"));
            seen[phase] = seen.ContainsKey(phase) ? seen[phase] + 1 : 1;
            string cls = gaze.Get(r, "hit_semantic_class"), cur = gaze.Get(r, "hit_is_current_target"), building = gaze.Get(r, "hit_building_id"), role = gaze.Get(r, "hit_assigned_destination");
            if (phase == "d1_pending" && building == "BAT_G1") { Assert.AreEqual("0", cur); Assert.AreNotEqual("TARGET_BUILDING", cls); }
            if (phase == "d1_search" && building == "BAT_G1" && cur == "1") { Assert.AreEqual("TARGET_BUILDING", cls); Assert.AreEqual("D1", role); hitsD1Current++; }
            if (phase == "d1_search" && building == "BAT_G2") { Assert.AreEqual("0", cur); Assert.AreNotEqual("TARGET_BUILDING", cls); Assert.AreEqual("D2", role); hitsD1Search_D2NotCurrent++; }
            if (phase == "d2_search" && building == "BAT_G2") { Assert.AreEqual("1", cur); Assert.AreEqual("TARGET_BUILDING", cls); hitsD2Current++; }
        }
        Assert.Greater(seen["d1_pending"], 0, "panel-reading samples carry their own label"); Assert.Greater(seen["d1_search"], 0); Assert.Greater(seen["d2_search"], 0);
        Assert.Greater(hitsD1Current, 0); Assert.Greater(hitsD1Search_D2NotCurrent, 0); Assert.Greater(hitsD2Current, 0);
        foreach (var r in gaze.Rows) Assert.AreNotEqual("idle", gaze.Get(r, "phase"), "no active-trial sample may stay 'idle'");
    }

    // ====================================================================================== F-06: the REAL Part 2 flow

    GameObject PanelTemplate(string name, out Button confirm)
    {
        var t = Make(name, active: false);
        var pc = t.AddComponent(T("PanelController"));
        confirm = t.AddComponent<Button>();
        Set(pc, "confirmButton", confirm);
        return t;
    }

    static Button OpenPanelButton(Component ui)
    {
        foreach (var pc in ui.GetComponentsInChildren(T("PanelController")))
        {
            var b = Get<Button>(pc, "confirmButton");
            if (b != null) return b;
        }
        return null;
    }

    [UnityTest]
    public IEnumerator Phase_Part2_ProductionFlow_SeparatesPanelReadingFromTaskTime_AndLabelsEverySample()
    {
        var camGo = new GameObject("Cam"); camGo.tag = "MainCamera"; camGo.AddComponent<Camera>(); _spawned.Add(camGo);
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, selectedPartFallback = true, realPart = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.3f, npcCaptureEnabled = false }, r => rig = r);

        // UIManager with real PanelController templates: the confirm button is "pressed" by the test
        var uiGo = Make("UI"); var ui = uiGo.AddComponent(T("UIManager"));
        Set(ui, "part2StartPanelTemplate", PanelTemplate("StartTpl", out _)); Set(ui, "part2ContinuePanelTemplate", PanelTemplate("ContinueTpl", out _)); Set(ui, "part2EndPanelTemplate", PanelTemplate("EndTpl", out _));
        var rigT = Make("PlayerRig").transform; var p0 = Make("Start0").transform; var p1 = Make("Tele0").transform; p1.position = new Vector3(3f, 0f, 3f);
        var groupType = Type.GetType("ExperimentManager+Part2Group, Assembly-CSharp");
        var group = Activator.CreateInstance(groupType); Set(group, "startPositions", new[] { p0 }); Set(group, "teleportPositions", new[] { p1 });
        var groups = Array.CreateInstance(groupType, 1); groups.SetValue(group, 0);
        Set(rig.em, "uiManager", ui); Set(rig.em, "playerRig", rigT); Set(rig.em, "spawner", rig.spawner); Set(rig.em, "part2Groups", groups);
        Set(rig.em, "part2FirstDuration", 0.6f); Set(rig.em, "part2SecondDuration", 0.6f); Set(rig.em, "npcWarmupDuration", 0f);
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(40f, 40f, 0.2f);
        var rec = MakeGaze(rig, head, 30f);
        yield return null;

        Call(rig.em, "StartPart2");
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 5f, "the Part 2 Start panel");
        Assert.AreEqual("p2_start_pending", Get<string>(rig.dcm, "CurrentPhase"), "Start panel being read");
        yield return new WaitForSecondsRealtime(0.3f);
        OpenPanelButton(ui).onClick.Invoke();                                                     // participant confirms -> trial + task 1
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task1", 2f, "task 1");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_continue_pending", 5f, "the Continue panel");
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 5f, "the Continue panel object");
        yield return new WaitForSecondsRealtime(0.6f);                                            // the participant reads the Continue panel
        OpenPanelButton(ui).onClick.Invoke();
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "p2_task2", 2f, "task 2");
        yield return WaitUntil(() => Get<string>(rig.dcm, "CurrentPhase") == "ended", 5f, "task end");
        yield return WaitUntil(() => OpenPanelButton(ui) != null, 5f, "the End panel");
        yield return new WaitForSecondsRealtime(0.2f);
        OpenPanelButton(ui).onClick.Invoke();
        yield return new WaitForSecondsRealtime(0.3f);

        var evs = Events(rig);
        AssertEventsCsvIsParticipantOnly(evs);
        var ph = new List<(double t, string phase)>(); foreach (var r in Rows(evs, "event_type", "phase_start")) ph.Add((D(evs.Get(r, "t_abs")), evs.Get(r, "label")));
        CollectionAssert.AreEqual(new[] { "p2_start_pending", "p2_task1", "p2_continue_pending", "p2_task2", "ended" }, ph.ConvertAll(x => x.phase));
        double PhT(string phase) => ph.Find(x => x.phase == phase).t;
        double tTrialStart = D(evs.Get(Rows(evs, "event_type", "trial_start")[0], "t_abs")), tTrialEnd = D(evs.Get(Rows(evs, "event_type", "trial_end")[0], "t_abs"));

        // task durations come from the phase boundaries and exclude the reading time
        double task1 = PhT("p2_continue_pending") - PhT("p2_task1"), reading = PhT("p2_task2") - PhT("p2_continue_pending"), task2 = PhT("ended") - PhT("p2_task2");
        UnityEngine.Debug.Log($"[GazeIntegrityTests][part2] task1={task1:F3}s reading={reading:F3}s task2={task2:F3}s trial window={tTrialEnd - tTrialStart:F3}s");
        Assert.AreEqual(0.6, task1, 0.15); Assert.AreEqual(0.6, task2, 0.15);
        Assert.Greater(reading, 0.55, "reading time is its own interval");
        Assert.AreEqual(task1 + reading + task2, tTrialEnd - tTrialStart, 0.2, "the whole trial window = task 1 + panel reading + task 2: nothing is lost");
        Assert.Greater((tTrialEnd - tTrialStart) - (task1 + task2), 0.5, "a naive trial-window duration would overstate task time by the reading time");

        // panel events bracket the pending phases
        double PanelT(string type, string label) { foreach (var r in Rows(evs, "event_type", type)) if (evs.Get(r, "label") == label) return D(evs.Get(r, "t_abs")); return double.NaN; }
        Assert.LessOrEqual(Math.Abs(PanelT("panel_shown", "part2_continue") - PhT("p2_continue_pending")), 0.05);
        Assert.LessOrEqual(Math.Abs(PanelT("panel_confirmed", "part2_continue") - PhT("p2_task2")), 0.05);

        // every high-frequency sample carries the phase in force at its own timestamp
        string PhaseAt(double t) { string p = "idle"; foreach (var c in ph) if (c.t <= t + 1e-4) p = c.phase; return p; }
        var gaze = Csv.Load(rig.File("gaze.csv")); var traj = Csv.Load(rig.File("trajectory.csv"));
        AssertWidths(gaze, "gaze.csv"); AssertWidths(traj, "trajectory.csv");
        var counts = new Dictionary<string, int>();
        foreach (var r in gaze.Rows)
        {
            string p = gaze.Get(r, "phase"); Assert.AreEqual(PhaseAt(D(gaze.Get(r, "t_abs"))), p); Assert.AreEqual("Part2", gaze.Get(r, "part")); Assert.AreNotEqual("idle", p);
            counts[p] = counts.ContainsKey(p) ? counts[p] + 1 : 1;
        }
        foreach (var r in traj.Rows) { Assert.AreEqual(PhaseAt(D(traj.Get(r, "t_abs"))), traj.Get(r, "phase")); Assert.AreEqual("Part2", traj.Get(r, "part")); }
        Assert.Greater(counts["p2_task1"], 0); Assert.Greater(counts["p2_continue_pending"], 5, "panel-reading samples exist and are labelled separately"); Assert.Greater(counts["p2_task2"], 0);
        Assert.IsFalse(counts.ContainsKey("p2_start_pending"), "no trial is open while the Start panel is read: intentionally no gaze rows (an intentional gap, not a sensor failure)");

        // the intentional gap shows up as epochs / breaks, not as an unexpected-gap warning
        Call(rig.dcm, "WriteManifestEnd", "test");
        string m = File.ReadAllText(rig.File("session_manifest.json"));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(m, "\"rate_gaze\": \\{[^}]*\"unexpected_gaps\": [1-9]"), "no unexpected gaps in a clean run");
    }

    // ====================================================================================== F-18: SamplingStats (deterministic)

    object NewStats(double hz) => Activator.CreateInstance(StatsType, hz);

    [Test]
    public void SamplingStats_NominalJitterMissingPauseAndSmallSamples_AreMeasuredFromTimestamps()
    {
        // nominal 30 Hz, exact
        var s = NewStats(30.0); double t = 100.0;
        for (int i = 0; i < 61; i++) { Call(s, "OnSample", t); t += 1.0 / 30.0; }
        Assert.AreEqual(61L, Get<long>(s, "Samples")); Assert.AreEqual(60L, Get<long>(s, "Intervals"));
        Assert.AreEqual(30.0, Get<double>(s, "ObservedHz"), 1e-6); Assert.AreEqual(1.0 / 30.0, Get<double>(s, "MedianInterval"), 1e-9);
        Assert.AreEqual(0L, Get<long>(s, "UnexpectedGaps")); Assert.AreEqual(1, Get<int>(s, "Epochs"));

        // jitter: alternating 25 / 41.67 ms around the 33.3 ms target: mean exact, median/p95 from the buffer, not the nominal
        var j = NewStats(30.0); t = 0;
        for (int i = 0; i < 101; i++) { Call(j, "OnSample", t); t += (i % 2 == 0) ? 0.025 : 0.0416667; }
        Assert.AreEqual(1.0 / ((0.025 + 0.0416667) / 2), Get<double>(j, "ObservedHz"), 0.01);
        Assert.AreEqual(0.0416667, Get<double>(j, "P95Interval"), 1e-4); Assert.AreEqual(0L, Get<long>(j, "UnexpectedGaps"));

        // a delayed sample inside one epoch: a real, unexpected gap (kept in the statistics, counted separately)
        var g = NewStats(30.0); t = 0;
        for (int i = 0; i < 30; i++) { Call(g, "OnSample", t); t += 1.0 / 30.0; }
        t += 0.5; for (int i = 0; i < 30; i++) { Call(g, "OnSample", t); t += 1.0 / 30.0; }
        Assert.AreEqual(1L, Get<long>(g, "UnexpectedGaps")); Assert.AreEqual(0.5 + 1.0 / 30.0, Get<double>(g, "MaxInterval"), 1e-9);
        Assert.Less(Get<double>(g, "ObservedHz"), 30.0, "a stall lowers the achieved rate");

        // a LONG application pause between two runs: break the epoch -> not a low rate, not an unexpected gap
        var p = NewStats(20.0); t = 0;
        for (int i = 0; i < 40; i++) { Call(p, "OnSample", t); t += 0.05; }
        Call(p, "BreakEpoch"); Call(p, "BreakEpoch");   // idempotent
        t += 600.0;                                      // ten minutes paused
        for (int i = 0; i < 40; i++) { Call(p, "OnSample", t); t += 0.05; }
        Assert.AreEqual(2, Get<int>(p, "Epochs")); Assert.AreEqual(1L, Get<long>(p, "IntentionalBreaks")); Assert.AreEqual(0L, Get<long>(p, "UnexpectedGaps"));
        Assert.AreEqual(20.0, Get<double>(p, "ObservedHz"), 1e-6, "pause excluded from the rate"); Assert.AreEqual(78L, Get<long>(p, "Intervals"), "no interval across the break");
        // the same data WITHOUT the break would have been misread
        var naive = NewStats(20.0); t = 0;
        for (int i = 0; i < 40; i++) { Call(naive, "OnSample", t); t += 0.05; }
        t += 600.0; for (int i = 0; i < 40; i++) { Call(naive, "OnSample", t); t += 0.05; }
        Assert.Less(Get<double>(naive, "ObservedHz"), 1.0); Assert.AreEqual(1L, Get<long>(naive, "UnexpectedGaps"));

        // too few samples: unknown, never 0
        var few = NewStats(30.0); t = 0;
        for (int i = 0; i < 5; i++) { Call(few, "OnSample", t); t += 1.0 / 30.0; }
        Assert.IsFalse(Get<bool>(few, "EstimateAvailable")); Assert.IsTrue(double.IsNaN(Get<double>(few, "ObservedHz"))); Assert.IsTrue(double.IsNaN(Get<double>(few, "MedianInterval")));
        var none = NewStats(30.0); Assert.IsTrue(double.IsNaN(Get<double>(none, "FirstT"))); Assert.AreEqual(0L, Get<long>(none, "Samples"));

        // duplicate / out-of-order timestamps are counted, never averaged in
        var bad = NewStats(30.0); t = 10;
        for (int i = 0; i < 12; i++) { Call(bad, "OnSample", t); t += 1.0 / 30.0; }
        Call(bad, "OnSample", t - 1.0 / 30.0); Call(bad, "OnSample", t - 1.0 / 30.0);   // repeats the last timestamp, then goes backwards in relation to nothing new
        Assert.GreaterOrEqual(Get<long>(bad, "NonMonotonic"), 1L);
        Assert.AreEqual(30.0, Get<double>(bad, "ObservedHz"), 1e-3);
    }

    // ====================================================================================== F-18 + F-09: runtime evidence in the manifest

    [UnityTest]
    public IEnumerator Manifest_IsEvidence_RecorderWithoutRowsIsNotOk_ThenCountsMatchTheFiles_PauseResumeAndLaterFailureAreKept()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, npcSpeed = 0.5f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) == 2, 10f, "NPCs");
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(40f, 40f, 0.2f);
        var rec = MakeGaze(rig, head, 30f);
        yield return null;

        // (1) the recorder EXISTS but has written nothing: not a success
        Call(rig.dcm, "WriteManifestCheckpoint", "test");
        string m0 = File.ReadAllText(rig.File("session_manifest.json"));
        StringAssert.Contains("\"gaze_recorder_present\": true", m0);
        var man0 = JsonUtility.FromJson<ManifestProbe>(m0);
        Assert.AreEqual("configured_no_rows", man0.health_gaze.status, "a present recorder with zero rows is not 'ok'");
        Assert.AreEqual(0L, man0.health_gaze.rows_written); Assert.IsFalse(man0.gaze_ok, "legacy gaze_ok no longer means 'recorder component exists'");
        Assert.AreEqual("no_samples", man0.rate_gaze.status); Assert.IsFalse(man0.rate_gaze.available);
        Assert.AreEqual(-1.0, man0.rate_gaze.observed_hz, "unknown is -1 with available=false, never 0");

        // (2) a trial with valid, then fallback-only gaze
        Call(rig.em, "BeginTrial");
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        yield return new WaitForSecondsRealtime(0.8f);
        Set(rec, "testLeftEye", null); Set(rec, "testRightEye", null);
        yield return new WaitForSecondsRealtime(0.5f);

        // (3) pause + resume (epoch break), then another stretch
        Call(rec, "OnApplicationPause", true); Call(rig.dcm, "OnApplicationPause", true);
        yield return new WaitForSecondsRealtime(0.6f);
        Call(rig.dcm, "OnApplicationPause", false); Call(rec, "OnApplicationPause", false);
        yield return new WaitForSecondsRealtime(0.5f);

        Call(rig.dcm, "WriteManifestCheckpoint", "test");
        var gazeCsv = Csv.Load(rig.File("gaze.csv")); var trajCsv = Csv.Load(rig.File("trajectory.csv"));
        var m1 = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("ok", m1.health_gaze.status); Assert.IsTrue(m1.gaze_ok);
        Assert.AreEqual(gazeCsv.Rows.Count, (int)m1.health_gaze.rows_written, "manifest rows == rows in gaze.csv");
        int valid = 0, fb = 0; double minT = double.MaxValue, maxT = double.MinValue;
        foreach (var r in gazeCsv.Rows) { if (gazeCsv.Get(r, "combined_valid") == "1") valid++; else fb++; double tt = D(gazeCsv.Get(r, "t_abs")); minT = Math.Min(minT, tt); maxT = Math.Max(maxT, tt); }
        Assert.AreEqual(valid, (int)m1.gaze_rows_valid_eye); Assert.AreEqual(fb, (int)m1.gaze_rows_fallback); Assert.Greater(valid, 5); Assert.Greater(fb, 5);
        Assert.AreEqual(minT, m1.health_gaze.first_row_t_abs, 2e-3); Assert.AreEqual(maxT, m1.health_gaze.last_row_t_abs, 2e-3);
        Assert.AreEqual((float)valid / gazeCsv.Rows.Count, m1.gaze_valid_eye_fraction, 1e-4);
        Assert.AreEqual(1, m1.gaze_pause_count); Assert.AreEqual(1, m1.gaze_resume_count); Assert.AreEqual(1, m1.app_pause_count); Assert.AreEqual(1, m1.app_resume_count);
        Assert.AreEqual("", m1.end_reason, "after a resume the manifest does not claim the session ended");
        Assert.AreEqual(trajCsv.Rows.Count, (int)m1.health_trajectory.rows_written); Assert.AreEqual("ok", m1.health_trajectory.status);
        Assert.AreEqual(Events(rig).Rows.Count, (int)m1.health_events.rows_written);
        Assert.AreEqual(NpcState(rig).Rows.Count, (int)m1.health_npc_state.rows_written); Assert.AreEqual("ok", m1.health_npc_state.status);
        Assert.AreEqual(NpcEvents(rig).Rows.Count, (int)m1.health_npc_events.rows_written); Assert.AreEqual(Lifetimes(rig).Rows.Count, (int)m1.health_npc_lifetimes.rows_written);

        // rates: the pause is an INTENTIONAL break, not a slow sensor
        Assert.IsTrue(m1.rate_gaze.available); Assert.GreaterOrEqual(m1.rate_gaze.intentional_breaks, 1); Assert.GreaterOrEqual(m1.rate_gaze.epochs, 2);
        Assert.AreEqual(0, (int)m1.rate_gaze.unexpected_gaps, "the pause did not register as a sampling failure");
        Assert.AreEqual(gazeCsv.Rows.Count, (int)m1.rate_gaze.samples);
        Assert.AreEqual(30.0, m1.rate_gaze.nominal_hz);
        Assert.Greater(m1.rate_gaze.observed_hz, 10.0); Assert.Less(m1.rate_gaze.observed_hz, 40.0);
        Assert.AreEqual(m1.rate_gaze.observed_hz * m1.gaze_valid_eye_fraction, m1.gaze_valid_eye_rate_hz, 1e-3, "valid-eye rate = observed row rate x valid fraction");
        Assert.Greater(m1.rate_trajectory.observed_hz, 5.0); Assert.AreEqual(20.0, m1.rate_trajectory.nominal_hz);
        Assert.AreEqual(trajCsv.Rows.Count, (int)m1.rate_trajectory.samples);
        UnityEngine.Debug.Log($"[GazeIntegrityTests][rates] gaze observed {m1.rate_gaze.observed_hz:F1} Hz (nominal 30, median {m1.rate_gaze.median_interval_s * 1000:F1} ms, p95 {m1.rate_gaze.p95_interval_s * 1000:F1} ms, max {m1.rate_gaze.max_interval_s * 1000:F1} ms, unexpected gaps {m1.rate_gaze.unexpected_gaps}); " +
                              $"trajectory observed {m1.rate_trajectory.observed_hz:F1} Hz (nominal 20, median {m1.rate_trajectory.median_interval_s * 1000:F1} ms); valid-eye fraction {m1.gaze_valid_eye_fraction:F2}");
        // computed independently from the file timestamps
        var dts = new List<double>(); for (int i = 1; i < gazeCsv.Rows.Count; i++) { double d = D(gazeCsv.Get(gazeCsv.Rows[i], "t_abs")) - D(gazeCsv.Get(gazeCsv.Rows[i - 1], "t_abs")); if (d < 0.25) dts.Add(d); }
        double mean = 0; foreach (var d in dts) mean += d; mean /= dts.Count;
        Assert.AreEqual(mean, m1.rate_gaze.mean_interval_s, 0.003, "manifest mean interval == mean computed from gaze.csv timestamps (excluding the pause)");

        // eye-tracking capability is tri-state; the Android permission cannot be queried here -> unknown, not false
        Assert.AreEqual("unknown", m1.eye_tracking_permission_state);
        CollectionAssert.Contains(new[] { "true", "unknown" }, m1.eye_tracking_supported_state, "never a disguised false in the Editor");

        // (4) a later write failure keeps the earlier history
        long rowsBefore = m1.health_gaze.rows_written;
        CaptureIoFault((p, op) => p.EndsWith("gaze.csv") && op == "append" ? new IOException("injected disk full") : null);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("CSV stream disabled after I/O error \\(gaze.csv\\)"));
        yield return new WaitForSecondsRealtime(0.4f);
        yield return null;
        Call(rig.dcm, "WriteManifestEnd", "test");
        var m2 = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("write_error", m2.health_gaze.status); Assert.IsFalse(m2.gaze_ok);
        Assert.GreaterOrEqual(m2.health_gaze.rows_written, rowsBefore, "rows written before the failure are kept"); Assert.Greater(m2.health_gaze.write_errors, 0L);
        StringAssert.Contains("IOException", m2.health_gaze.last_error);
        Assert.IsTrue(m2.trajectory_ok && m2.events_ok, "other streams are unaffected");
        Assert.AreEqual(gazeCsv.Rows.Count, (int)rowsBefore);
        Assert.Greater(m2.health_gaze.rows_dropped_after_failure, 0L, "later rows are counted as dropped, not silently lost");
        CaptureIoFault(null);
    }

    // minimal mirror of the manifest used for typed parsing in tests (JsonUtility ignores unknown fields)
    [Serializable] public class ProbeHealth { public long rows_attempted, rows_failed, rows_dropped_after_failure, recovery_attempts; public string writer_state, last_error_operation, last_error_type; public bool partial_output_possible; public double first_error_t_abs, last_error_t_abs; public string status; public bool configured_now, ever_configured, writer_initialised; public long rows_written, write_errors; public double first_row_t_abs, last_row_t_abs; public string last_error; }
    [Serializable] public class ProbeRate { public bool available; public string status; public double nominal_hz, observed_hz, mean_interval_s, median_interval_s, p95_interval_s, max_interval_s; public long samples, intervals, epochs, intentional_breaks, unexpected_gaps; }
    [Serializable]
    public class ManifestProbe
    {
        public string end_reason; public bool gaze_ok, trajectory_ok, events_ok, npc_state_ok, npc_events_ok, npc_lifetimes_ok;
        public ProbeHealth health_gaze, health_trajectory, health_events, health_npc_state, health_npc_events, health_npc_lifetimes;
        public ProbeRate rate_gaze, rate_trajectory;
        public long gaze_rows_valid_eye, gaze_rows_fallback; public float gaze_valid_eye_fraction, gaze_valid_eye_rate_hz;
        public int gaze_pause_count, gaze_resume_count, app_pause_count, app_resume_count;
        public string eye_tracking_supported_state, eye_tracking_permission_state;
    }

    [UnityTest]
    public IEnumerator Manifest_NpcCaptureDisabled_IsDistinguishableFromANpcStreamFailure()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, npcCaptureEnabled = false, selectedPartFallback = true, prewarm = 2, maxAgents = 2, npcSpeed = 1f }, r => rig = r);
        yield return WaitUntil(() => Live(rig) >= 2, 10f, "NPCs");
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rig.dcm, "WriteManifestEnd", "test");
        var m = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual("disabled_by_master_switch", m.health_npc_state.status); Assert.AreEqual("disabled_by_master_switch", m.health_npc_events.status);
        Assert.IsTrue(m.npc_state_ok && m.npc_events_ok && m.npc_lifetimes_ok, "disabled capture is expected absence, not a failure");
        Assert.AreEqual(0L, m.health_npc_state.rows_written); Assert.AreEqual(0L, m.health_npc_state.write_errors);
        Assert.AreEqual("ok", m.health_events.status, "participant events are recorded regardless");

        // a real failure of an ENABLED stream looks different
        TearDownRigObjects(); yield return null; yield return null;
        Rig rig2 = null;
        yield return BuildRig(new RigOptions { prewarm = 2, maxAgents = 2, npcSpeed = 1f }, r => rig2 = r);
        yield return WaitUntil(() => Live(rig2) >= 2, 10f, "NPCs");
        yield return new WaitForSecondsRealtime(0.4f);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("CSV stream disabled after I/O error \\(npc_state.csv\\)"));
        var npcState = Get<object>(rig2.dcm, "_npcState");
        File.Delete(rig2.File("npc_state.csv")); Directory.CreateDirectory(rig2.File("npc_state.csv"));   // a directory where the file was: appends now throw
        yield return new WaitForSecondsRealtime(0.4f);
        yield return null;
        Call(rig2.dcm, "WriteManifestEnd", "test");
        var m2 = JsonUtility.FromJson<ManifestProbe>(File.ReadAllText(rig2.File("session_manifest.json")));
        Assert.AreEqual("write_error", m2.health_npc_state.status); Assert.IsFalse(m2.npc_state_ok);
        Assert.Greater(m2.health_npc_state.rows_written, 0L, "history before the failure is kept"); Assert.IsNotNull(npcState);
        Assert.AreEqual("ok", m2.health_npc_events.status);
    }
}
