// SchemaV17Tests.cs
// Tests for the schema-v17 work package: face-tracking start-up before the first gaze row (readiness gate, bounded wait, failure states, live per-row data source), the 61-column gaze.csv + 79-column
// gaze_diagnostics.csv split, raw-vs-recovered eyelid semantics across the two files, and reader compatibility with historical (v11-v16) files.
// All synthetic: they establish logic and serialisation, NOT what a Quest Pro delivers (first-row readiness on hardware, real wait time, independent left / right response: see docs/schema_v17_changes.md).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class FaceReadinessPureTests
{
    static object Facts(Action<object> set) { var f = Activator.CreateInstance(T("FaceReadinessFacts")); Set(f, "DataSource", ""); set(f); return f; }
    static string Stage(object facts) => (string)CallStatic(T("FaceReadiness"), "Name", CallStatic(T("FaceReadiness"), "Evaluate", facts));
    static object AllOk() => Facts(f => { foreach (var n in new[] { "OvrInit", "Supported", "PermissionGranted", "ComponentCreated", "ComponentEnabled", "Started", "ApiOk", "StatusValid", "EyeClosureAvailable" }) Set(f, n, true); Set(f, "DataSource", "visual"); });

    [Test]
    public void Evaluate_ReportsTheFirstUnmetRequirement_AComponentThatMerelyExistsIsNeverReady()
    {
        Assert.AreEqual("ready", Stage(AllOk()));
        string[] order = { "OvrInit", "Supported", "PermissionGranted", "ComponentCreated", "ComponentEnabled", "Started", "ApiOk", "StatusValid" };
        string[] expect = { "no_runtime", "unsupported", "permission_not_granted", "component_missing", "component_disabled", "not_started", "api_not_responding", "frame_invalid" };
        for (int i = 0; i < order.Length; i++)
        {
            var f = AllOk(); Set(f, order[i], false);
            Assert.AreEqual(expect[i], Stage(f), "missing " + order[i]);
        }
        var audio = AllOk(); Set(audio, "DataSource", "audio"); Assert.AreEqual("audio_only_source", Stage(audio), "a valid Audio frame is not an eyelid measurement");
        var unknown = AllOk(); Set(unknown, "DataSource", ""); Assert.AreEqual("data_source_unknown", Stage(unknown));
        var noWeights = AllOk(); Set(noWeights, "EyeClosureAvailable", false); Assert.AreEqual("eye_closure_unavailable", Stage(noWeights));
        // "created + enabled + started" is still not ready without a valid Visual frame
        var started = Facts(f => { foreach (var n in new[] { "OvrInit", "Supported", "PermissionGranted", "ComponentCreated", "ComponentEnabled", "Started" }) Set(f, n, true); });
        Assert.AreEqual("api_not_responding", Stage(started));
    }

    [Test]
    public void Permanent_OnlyForNoRuntimeAndUnsupported_EverythingElseMayStillResolve()
    {
        Type rs = T("FaceReadinessStage");
        foreach (var n in Enum.GetNames(rs))
        {
            bool perm = (bool)CallStatic(T("FaceReadiness"), "IsPermanent", Enum.Parse(rs, n));
            Assert.AreEqual(n == "NoRuntime" || n == "Unsupported", perm, n);
        }
    }
}

public partial class NpcLifecycleTests
{
    // ================================================================ face-tracking start-up (readiness gate)
    object ReadinessFacts(Action<object> set)
    {
        var f = Activator.CreateInstance(T("FaceReadinessFacts")); Set(f, "DataSource", "");
        foreach (var n in new[] { "OvrInit", "Supported", "PermissionGranted", "ComponentCreated", "ComponentEnabled", "Started", "ApiOk", "StatusValid", "EyeClosureAvailable" }) Set(f, n, true);
        Set(f, "DataSource", "visual"); set?.Invoke(f); return f;
    }

    static void SetReadiness(Component rec, Func<object> make)
    {
        var fi = rec.GetType().GetField("testFaceReadiness");
        var body = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(make)), fi.FieldType.GetGenericArguments()[0]);
        fi.SetValue(rec, System.Linq.Expressions.Expression.Lambda(fi.FieldType, body).Compile());
    }

    IEnumerator RigWithRecorder(Action<Rig, Component, Transform> done)
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, r => rig = r);
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(50f, 50f, 0.2f);
        var rec = MakeGaze(rig, head, 40f);
        yield return null;
        done(rig, rec, head);   // NO trial has begun: the gaze file does not exist yet
    }

    static object Startup(Component rec) => Get<object>(rec, "FaceStartup");

    [UnityTest]
    public IEnumerator FaceStartup_Ready_BeforeTheFirstRow_TheFirstFormalRowCarriesValidVisualClosure_AndNothingExistsBeforeStart()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        double t0 = CaptureClockNow();
        SetReadiness(rec, () => ReadinessFacts(f => { if (CaptureClockNow() - t0 < 0.15) { Set(f, "StatusValid", false); } }));   // the first valid Visual frame arrives after ~0.15 s
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        SetFaceSeam(rec, () => { var r = FaceReading(true, false, 0.2f, 0.8f); Set(r, "DataSource", "visual"); Set(r, "Started", true); return r; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));

        yield return (IEnumerator)Call(rec, "AwaitFaceTrackingReady", 3f, "preflight");
        var su = Startup(rec);
        Assert.IsTrue(Get<bool>(su, "attempted")); Assert.IsTrue(Get<bool>(su, "ready"), Get<string>(su, "failure_reason")); Assert.AreEqual("ready", Get<string>(su, "stage")); Assert.AreEqual("visual", Get<string>(su, "data_source_at_ready"));
        Assert.AreEqual(0.15, Get<double>(su, "wait_s"), 0.12, "the wait lasted until the first valid Visual frame, and no longer"); Assert.Greater(Get<long>(su, "frames_polled"), 1L);
        // PRIVACY / semantics: the gate recorded nothing - neither gaze rows nor face samples exist before the participant's Start
        Assert.IsFalse(File.Exists(rig.File("gaze.csv")), "no gaze file before the Start click"); Assert.IsFalse(File.Exists(rig.File("gaze_diagnostics.csv")), "no diagnostics file before the Start click");

        Call(rig.em, "BeginTrial");   // the Start click: the first gaze row is written synchronously
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        Assert.Greater(gaze.Rows.Count, 5);
        var first = gaze.Rows[0];
        Assert.AreEqual("current", gaze.Get(first, "face_state"), "the FIRST formal row already has a valid Visual face state"); Assert.AreEqual("1", gaze.Get(first, "face_closure_valid"));
        Assert.AreEqual("0.2000", gaze.Get(first, "eyes_closed_left_recovered")); Assert.AreEqual("0.8000", gaze.Get(first, "eyes_closed_right_recovered")); Assert.AreEqual("visual", gaze.Get(first, "face_data_source"));
        Assert.IsFalse(gaze.Rows.Any(r => gaze.Get(r, "face_state") == "not_started"), "no initial not_started gap");
        Assert.IsTrue(gaze.Rows.All(r => gaze.Get(r, "face_closure_valid") == "1"));
        var bm = ManifestOf(rig)["eye_tracking"]["blink"];
        Assert.IsTrue((bool)bm["startup_attempted"]); Assert.IsTrue((bool)bm["startup_ready"]); Assert.AreEqual("ready", (string)bm["startup_stage"]); Assert.AreEqual("preflight", (string)bm["startup_point"]); Assert.AreEqual("", (string)bm["startup_failure_reason"]);
        Assert.IsFalse((bool)bm["absence_of_blink_events_is_evidence_of_no_blinks"], "zero detected blinks never proves there were none");
    }

    [UnityTest]
    public IEnumerator FaceStartup_FailureStates_AreNamedPrecisely_TheWaitIsBounded_AndNothingIsClaimedReady([Values("FrameInvalid", "PermissionNotGranted", "AudioOnly", "NotStarted", "ApiNotResponding")] string failure)
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        SetReadiness(rec, () => ReadinessFacts(f =>
        {
            switch (failure)
            {
                case "FrameInvalid": Set(f, "StatusValid", false); break;
                case "PermissionNotGranted": Set(f, "PermissionGranted", false); break;
                case "AudioOnly": Set(f, "DataSource", "audio"); break;
                case "NotStarted": Set(f, "Started", false); break;
                case "ApiNotResponding": Set(f, "ApiOk", false); break;
            }
        }));
        LogAssert.ignoreFailingMessages = true;   // the not-ready warning is logged on purpose
        double t0 = CaptureClockNow();
        yield return (IEnumerator)Call(rec, "AwaitFaceTrackingReady", 0.4f, "preflight");
        double waited = CaptureClockNow() - t0; LogAssert.ignoreFailingMessages = false;
        var su = Startup(rec);
        Assert.IsFalse(Get<bool>(su, "ready")); Assert.GreaterOrEqual(waited, 0.35, "it waited for the whole bound"); Assert.Less(waited, 1.5, "and not longer: the wait is bounded");
        string expect = new Dictionary<string, string> { { "FrameInvalid", "frame_invalid" }, { "PermissionNotGranted", "permission_not_granted" }, { "AudioOnly", "audio_only_source" }, { "NotStarted", "not_started" }, { "ApiNotResponding", "api_not_responding" } }[failure];
        Assert.AreEqual(expect, Get<string>(su, "stage")); StringAssert.StartsWith(expect + ":timeout_after_", Get<string>(su, "failure_reason"));
        Assert.AreEqual(-1.0, Get<double>(su, "ready_t_abs"), "no ready time is claimed");
        Assert.IsFalse(File.Exists(rig.File("gaze.csv")), "a failed start-up still records nothing");
    }

    [UnityTest]
    public IEnumerator FaceStartup_NoRuntime_And_UnsupportedRuntime_EndTheWaitAtOnce_AsPermanent([Values("NoRuntime", "Unsupported")] string which)
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        SetReadiness(rec, () => ReadinessFacts(f => { if (which == "NoRuntime") Set(f, "OvrInit", false); else Set(f, "Supported", false); }));
        LogAssert.ignoreFailingMessages = true;
        double t0 = CaptureClockNow();
        yield return (IEnumerator)Call(rec, "AwaitFaceTrackingReady", 5f, "preflight");
        double waited = CaptureClockNow() - t0; LogAssert.ignoreFailingMessages = false;
        Assert.Less(waited, 0.5, "waiting cannot make a missing runtime appear: the gate does not burn its 5 s bound");
        Assert.IsFalse(Get<bool>(Startup(rec), "ready")); Assert.AreEqual(which == "NoRuntime" ? "no_runtime:permanent" : "unsupported:permanent", Get<string>(Startup(rec), "failure_reason"));
    }

    [UnityTest]
    public IEnumerator FaceStartup_WithoutARuntime_LeavesTheEyelidFieldsBlank_NeverZero_AndTheFirstRowSaysWhy()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        LogAssert.ignoreFailingMessages = true;
        yield return (IEnumerator)Call(rec, "AwaitFaceTrackingReady", 2f, "preflight");   // the real readiness path: OVRPlugin is not initialised in the Editor test run
        LogAssert.ignoreFailingMessages = false;
        Assert.AreEqual("no_runtime:permanent", Get<string>(Startup(rec), "failure_reason"));
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rig.em, "BeginTrial"); yield return new WaitForSecondsRealtime(0.3f); Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        foreach (var r in gaze.Rows)
        {
            Assert.AreEqual("no_runtime", gaze.Get(r, "face_state")); Assert.AreEqual("0", gaze.Get(r, "face_closure_valid"));
            Assert.AreEqual("", gaze.Get(r, "eyes_closed_left_recovered")); Assert.AreEqual("", gaze.Get(r, "eyes_closed_right_recovered"));
            Assert.AreEqual("", gaze.Get(r, "eyes_closed_left"), "raw weights are blank too");
        }
    }

    [UnityTest]
    public IEnumerator FaceRows_TheDataSourceIsLivePerRow_ASourceSwitchIsSeenOnTheRowItHappens_AndAudioOnlyRowsCarryNoEyelidValues()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);   // the 1 Hz probe says "visual" for the whole run
        double t0 = CaptureClockNow();
        SetFaceSeam(rec, () =>
        {
            double t = CaptureClockNow() - t0; var r = FaceReading(true, false, 0.3f, 0.3f);
            Set(r, "DataSource", (t >= 0.3 && t < 0.6) ? "audio" : "visual"); Set(r, "Started", true);   // the LIVE source of this very frame
            return r;
        });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(1.0f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        gaze.Rows.RemoveAll(r => gaze.Get(r, "face_state") == "no_runtime");
        int audio = 0, visual = 0;
        foreach (var r in gaze.Rows)
        {
            string src = gaze.Get(r, "face_data_source"), st = gaze.Get(r, "face_state");
            if (src == "audio") { audio++; Assert.AreEqual("audio_only_source", st); Assert.AreEqual("0", gaze.Get(r, "face_closure_valid")); Assert.AreEqual("", gaze.Get(r, "eyes_closed_left_recovered")); Assert.AreEqual("", gaze.Get(r, "eyes_closed_left")); }
            else { visual++; Assert.AreEqual("current", st); Assert.AreEqual("0.3000", gaze.Get(r, "eyes_closed_left_recovered")); Assert.AreEqual("1", gaze.Get(r, "face_closure_valid")); }
        }
        Assert.Greater(audio, 3, "audio-source rows exist although the cached probe said visual all the time"); Assert.Greater(visual, 3);
    }

    // ================================================================ gate wiring in ExperimentManager
    [UnityTest]
    public IEnumerator FaceGate_InExperimentManager_RunsOnlyWhereGazeIsCaptured_HonoursItsSwitches_AndAFailureDoesNotBlockByDefault()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        SetReadiness(rec, () => ReadinessFacts(null));
        // the rig's running Part has gaze capture on: the preflight gate runs and records its outcome
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "preflight");
        Assert.IsTrue(Get<bool>(Startup(rec), "attempted")); Assert.IsTrue(Get<bool>(Startup(rec), "ready")); Assert.AreEqual("preflight", Get<string>(Startup(rec), "start_point"));
        // the after-start-click gate is a no-op in the default configuration
        var su = Startup(rec); Set(su, "attempted", false); Set(su, "ready", false); Set(su, "start_point", "");
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "after_start_click");
        Assert.IsFalse(Get<bool>(Startup(rec), "attempted"), "the after-click point is off unless faceTrackingStartAfterStartClick");
        // switches
        Set(rig.em, "faceTrackingStartAfterStartClick", true);
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "preflight"); Assert.IsFalse(Get<bool>(Startup(rec), "attempted"), "preflight point off in after-click mode");
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "after_start_click"); Assert.IsTrue(Get<bool>(Startup(rec), "ready")); Assert.AreEqual("after_start_click", Get<string>(Startup(rec), "start_point"));
        Set(rig.em, "faceTrackingStartAfterStartClick", false); Set(rig.em, "faceTrackingPreflight", false);
        su = Startup(rec); Set(su, "attempted", false); Set(su, "ready", false);
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "preflight"); Assert.IsFalse(Get<bool>(Startup(rec), "attempted"), "faceTrackingPreflight = false: the legacy lazy path");
        // a failure with faceTrackingRequired = false lets the Part continue (the coroutine ends; nothing blocks)
        Set(rig.em, "faceTrackingPreflight", true); Set(rig.em, "faceTrackingMaxWaitSeconds", 0.2f); Set(rig.em, "faceTrackingRequired", false);
        SetReadiness(rec, () => ReadinessFacts(f => Set(f, "StatusValid", false)));
        LogAssert.ignoreFailingMessages = true;
        yield return (IEnumerator)Call(rig.em, "FaceTrackingReadyGate", "preflight");
        LogAssert.ignoreFailingMessages = false;
        Assert.IsFalse(Get<bool>(Startup(rec), "ready")); StringAssert.StartsWith("frame_invalid:timeout", Get<string>(Startup(rec), "failure_reason"));
    }

    // ================================================================ schema v17 files
    static readonly string[] RemovedFromGazeCsv = { "unix_time_ms", "left_confidence", "left_valid", "right_confidence", "right_valid", "combined_confidence", "head_pos_x", "head_pos_y", "head_pos_z", "head_rot_x", "head_rot_y", "head_rot_z", "head_rot_w", "left_provider_valid", "right_provider_valid", "eyes_closed_left", "eyes_closed_right", "blink_state", "left_eye_head_local_x", "left_eye_head_local_y", "left_eye_head_local_z", "right_eye_head_local_x", "right_eye_head_local_y", "right_eye_head_local_z", "left_eye_world_x", "left_eye_world_y", "left_eye_world_z", "right_eye_world_x", "right_eye_world_y", "right_eye_world_z", "provider_time_s", "provider_time_changed", "left_raw_tracking_pos_x", "left_raw_tracking_pos_y", "left_raw_tracking_pos_z", "left_raw_tracking_dir_x", "left_raw_tracking_dir_y", "left_raw_tracking_dir_z", "right_raw_tracking_pos_x", "right_raw_tracking_pos_y", "right_raw_tracking_pos_z", "right_raw_tracking_dir_x", "right_raw_tracking_dir_y", "right_raw_tracking_dir_z", "left_gaze_head_pos_x", "left_gaze_head_pos_y", "left_gaze_head_pos_z", "left_gaze_head_dir_x", "left_gaze_head_dir_y", "left_gaze_head_dir_z", "right_gaze_head_pos_x", "right_gaze_head_pos_y", "right_gaze_head_pos_z", "right_gaze_head_dir_x", "right_gaze_head_dir_y", "right_gaze_head_dir_z", "left_gaze_vs_view_anchor_head_m", "right_gaze_vs_view_anchor_head_m", "left_worldspace_vs_composed_origin_m", "right_worldspace_vs_composed_origin_m", "left_worldspace_vs_composed_dir_deg", "right_worldspace_vs_composed_dir_deg", "view_anchor_separation_m", "gaze_head_separation_m", "runtime_user_ipd_m", "anchors_updated_frame", "origin_fallback_reason", "face_state", "face_tracking_started", "face_expressions_valid", "eye_following_valid", "eyes_look_down_left", "eyes_look_down_right", "face_sample_frame", "face_data_source", "roi_hit_name", "ray_is_eye_gaze", "ray_origin_x", "ray_origin_y", "ray_origin_z", "ray_dir_x", "ray_dir_y", "ray_dir_z" };

    [UnityTest]
    public IEnumerator V17_TwoFiles_ExactHeaders_OneRowPerRow_RemovedColumnsAreAbsent_AndTheManifestListsBoth()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        SetFaceSeam(rec, () => { var r = FaceReading(true, true, 0.4f, 0.5f, 0.1f, 0.3f); Set(r, "DataSource", "visual"); Set(r, "Started", true); return r; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.5f); Call(rec, "StopRecording");

        var raw = Csv.LoadRaw(rig.File("gaze.csv")); var diag = Csv.LoadRaw(rig.File("gaze_diagnostics.csv"));
        Assert.IsTrue(raw.Exists && diag.Exists);
        Assert.AreEqual((string)Call(rec, "GetCsvHeader"), File.ReadLines(rig.File("gaze.csv")).First(), "the first line of gaze.csv is exactly what the writer's header builder produces");
        Assert.AreEqual((string)Call(rec, "GetDiagnosticsHeader"), File.ReadLines(rig.File("gaze_diagnostics.csv")).First(), "the first line of gaze_diagnostics.csv is exactly what the writer's header builder produces");
        CollectionAssert.AreEqual((string[])T("GazeRecorder").GetField("PrimaryColumns").GetValue(null), raw.Header); CollectionAssert.AreEqual((string[])T("GazeRecorder").GetField("DiagnosticColumns").GetValue(null), diag.Header);
        Assert.AreEqual(61, raw.Header.Count); Assert.AreEqual(85, diag.Header.Count); Assert.AreEqual(raw.Rows.Count, diag.Rows.Count, "one diagnostics row per gaze row");
        foreach (var r in raw.Rows) Assert.AreEqual(61, r.Count, "every gaze row has exactly 61 fields"); foreach (var r in diag.Rows) Assert.AreEqual(85, r.Count, "every diagnostics row has exactly 85 fields");
        foreach (var n in RemovedFromGazeCsv) CollectionAssert.DoesNotContain(raw.Header, n, n + " is not a gaze.csv column in v17");
        // the removed columns that still carry QA value are in the diagnostics (all but the 8 derivable ones)
        string[] derivable = { "ray_origin_x", "ray_origin_y", "ray_origin_z", "ray_dir_x", "ray_dir_y", "ray_dir_z", "ray_is_eye_gaze", "roi_hit_name" };
        foreach (var n in RemovedFromGazeCsv.Except(derivable)) CollectionAssert.Contains(diag.Header, n, n + " moved to gaze_diagnostics.csv");
        foreach (var n in derivable) { CollectionAssert.DoesNotContain(raw.Header, n); CollectionAssert.DoesNotContain(diag.Header, n); }
        // join keys agree row by row
        for (int i = 0; i < raw.Rows.Count; i++)
            foreach (var k in new[] { "session_id", "frame_index", "t_abs" }) Assert.AreEqual(raw.Rows[i][raw.Col(k)], diag.Rows[i][diag.Col(k)], k + " row " + i);
        // raw vs recovered across the two files: gaze.csv has ONLY the recovered weights, the diagnostics the raw weights and the correction inputs
        int fi0 = diag.Rows.FindIndex(r => diag.Get(r, "face_state") == "current"); Assert.GreaterOrEqual(fi0, 0, "rows before the seams were installed are no_runtime; a current row exists"); var row0 = raw.Rows[fi0]; var d0 = diag.Rows[fi0];
        Assert.AreEqual("0.5000", raw.Get(row0, "eyes_closed_left_recovered"), "0.4 + min(0.1, 0.3)"); Assert.AreEqual("0.6000", raw.Get(row0, "eyes_closed_right_recovered"), "0.5 + min(0.1, 0.3)");
        Assert.AreEqual("0.4000", diag.Get(d0, "eyes_closed_left")); Assert.AreEqual("0.5000", diag.Get(d0, "eyes_closed_right")); Assert.AreEqual("0.1000", diag.Get(d0, "eyes_look_down_left")); Assert.AreEqual("0.3000", diag.Get(d0, "eyes_look_down_right"));
        Assert.AreEqual("1", diag.Get(d0, "eye_following_valid")); Assert.AreEqual("current", diag.Get(d0, "face_state")); Assert.AreEqual("1", raw.Get(row0, "face_closure_valid"));
        // the manifest tells the same story
        Call(rig.dcm, "OnSessionEnded");
        var m = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(17, (int)m["schema_version"]);
        var fl = ((Newtonsoft.Json.Linq.JArray)m["file_list"]).Select(t => (string)t).ToList(); CollectionAssert.Contains(fl, "gaze.csv"); CollectionAssert.Contains(fl, "gaze_diagnostics.csv");
        CollectionAssert.Contains(((Newtonsoft.Json.Linq.JArray)m["capture_streams_closed"]).Select(t => (string)t).ToList(), "gaze_diagnostics");
        Assert.AreEqual("Closed", (string)m["capture_lifecycle_state"]);
    }

    [UnityTest]
    public IEnumerator V17_ValidZeroIsNotBlank_InvalidIsNotZero_InBothFiles()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        double t0 = CaptureClockNow();
        SetFaceSeam(rec, () =>
        {
            double t = CaptureClockNow() - t0; bool valid = t < 0.25;
            var r = FaceReading(valid, false, 0f, 0f); Set(r, "DataSource", valid ? "visual" : ""); Set(r, "Started", true); return r;
        });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.7f); Call(rec, "StopRecording");
        var g = Csv.Load(rig.File("gaze.csv")); g.Rows.RemoveAll(r => g.Get(r, "face_state") == "no_runtime");
        Assert.IsTrue(g.Rows.Any(r => g.Get(r, "face_closure_valid") == "1" && g.Get(r, "eyes_closed_left_recovered") == "0.0000" && g.Get(r, "eyes_closed_left") == "0.0000"), "a measured zero is written as 0.0000 in gaze.csv AND as the raw weight in the diagnostics");
        var bad = g.Rows.Where(r => g.Get(r, "face_closure_valid") == "0").ToList(); Assert.IsNotEmpty(bad);
        foreach (var r in bad) { Assert.AreEqual("invalid_frame", g.Get(r, "face_state")); Assert.AreEqual("", g.Get(r, "eyes_closed_left_recovered")); Assert.AreEqual("", g.Get(r, "eyes_closed_right_recovered")); Assert.AreEqual("", g.Get(r, "eyes_closed_left")); }
    }

    [UnityTest]
    public IEnumerator V17_RecordDiagnosticsOff_WritesTheMainGazeFileAlone_Unchanged()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return RigWithRecorder((r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "recordDiagnostics", false);
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rig.em, "BeginTrial"); yield return new WaitForSecondsRealtime(0.3f); Call(rec, "StopRecording");
        Assert.IsTrue(File.Exists(rig.File("gaze.csv"))); Assert.IsFalse(File.Exists(rig.File("gaze_diagnostics.csv")), "recordDiagnostics = false: no second file");
        var raw = Csv.LoadRaw(rig.File("gaze.csv")); Assert.AreEqual(61, raw.Header.Count); Assert.Greater(raw.Rows.Count, 3);
    }

    [UnityTest]
    public IEnumerator V17_FallbackRayColumns_AreBlankOnEyeGazeRows_FilledOnHeadFallbackRows_AndTheHitLiesOnThatRay()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        // eyes valid for 0.35 s, then invalid (confidence 0): the combined ray falls back to the head-forward ray
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.35f);
        Set(rec, "testLeftEye", Inj(0.0f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.0f, head.position, Vector3.forward));
        yield return new WaitForSecondsRealtime(0.35f);
        Call(rec, "StopRecording");
        var raw = Csv.LoadRaw(rig.File("gaze.csv")); var diag = Csv.LoadRaw(rig.File("gaze_diagnostics.csv")); var merged = Csv.Load(rig.File("gaze.csv"));
        string[] fb = { "fallback_ray_origin_x", "fallback_ray_origin_y", "fallback_ray_origin_z", "fallback_ray_dir_x", "fallback_ray_dir_y", "fallback_ray_dir_z" };
        CollectionAssert.AreEqual(fb, diag.Header.GetRange(diag.Header.Count - 6, 6), "the six fallback-ray columns are the last six diagnostics columns");
        int eye = 0, fallback = 0;
        for (int i = 0; i < raw.Rows.Count; i++)
        {
            bool valid = raw.Get(raw.Rows[i], "combined_valid") == "1";
            if (valid)
            {
                eye++;
                foreach (var n in fb) Assert.AreEqual("", diag.Get(diag.Rows[i], n), "eye-gaze row: " + n + " is blank (the cast ray is combined_origin / combined_dir)");
                Assert.AreEqual(raw.Get(raw.Rows[i], "combined_origin_x"), merged.Get(merged.Rows[i], "ray_origin_x")); Assert.AreEqual(raw.Get(raw.Rows[i], "combined_dir_z"), merged.Get(merged.Rows[i], "ray_dir_z"));
            }
            else
            {
                fallback++;
                Assert.AreEqual("head_fallback", raw.Get(raw.Rows[i], "gaze_ray_source"));
                foreach (var n in fb) Assert.IsTrue(Regexp(diag.Get(diag.Rows[i], n)), "head-fallback row: " + n + " = '" + diag.Get(diag.Rows[i], n) + "' is a number");
                foreach (var n in new[] { "combined_origin_x", "combined_dir_x" }) Assert.AreEqual("", raw.Get(raw.Rows[i], n), "the combined ray stays blank on a fallback row");
                // the recorded hit lies on the recorded fallback ray (origin + t * dir, t > 0)
                if (raw.Get(raw.Rows[i], "ray_did_hit") == "1")
                {
                    float F(Csv c, List<string> r, string n) => float.Parse(c.Get(r, n), System.Globalization.CultureInfo.InvariantCulture);
                    var o = new Vector3(F(diag, diag.Rows[i], fb[0]), F(diag, diag.Rows[i], fb[1]), F(diag, diag.Rows[i], fb[2])); var d = new Vector3(F(diag, diag.Rows[i], fb[3]), F(diag, diag.Rows[i], fb[4]), F(diag, diag.Rows[i], fb[5]));
                    var h = new Vector3(F(raw, raw.Rows[i], "hit_point_x"), F(raw, raw.Rows[i], "hit_point_y"), F(raw, raw.Rows[i], "hit_point_z"));
                    Assert.AreEqual(1f, d.magnitude, 2e-3f); Assert.Greater(Vector3.Dot(h - o, d), 0f, "the hit is in front of the fallback origin");
                    Assert.Less(Vector3.Cross(h - o, d).magnitude, 2e-3f * (h - o).magnitude + 2e-3f, "the hit lies on the fallback ray");
                }
                Assert.AreEqual(diag.Get(diag.Rows[i], fb[0]), merged.Get(merged.Rows[i], "ray_origin_x"), "the reader-side ray_* of a fallback row is the recorded fallback ray");
            }
        }
        Assert.Greater(eye, 3); Assert.Greater(fallback, 3);
        Assert.AreEqual(61, raw.Header.Count);
    }

    [Test]
    public void DictionaryDocument_ListsExactlyTheColumnsTheWriterEmits_InOrder_IncludingTheSixFallbackFields()
    {
        string doc = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "docs", "gaze_schema_v17_dictionary.md");
        if (!File.Exists(doc)) Assert.Ignore("docs/gaze_schema_v17_dictionary.md is not part of the project being tested");
        string text = File.ReadAllText(doc);
        int s1 = text.IndexOf("## 1. gaze.csv"), s2 = text.IndexOf("## 2. gaze_diagnostics.csv"), s3 = text.IndexOf("## 3. Disposition");
        Assert.IsTrue(s1 >= 0 && s2 > s1 && s3 > s2);
        List<string> Names(string t) => System.Text.RegularExpressions.Regex.Matches(t, @"^\| \d+ \| `([a-z_0-9]+)` \|", System.Text.RegularExpressions.RegexOptions.Multiline).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToList();
        CollectionAssert.AreEqual((string[])T("GazeRecorder").GetField("PrimaryColumns").GetValue(null), Names(text.Substring(s1, s2 - s1)), "dictionary section 1 == gaze.csv header");
        var diagCols = (string[])T("GazeRecorder").GetField("DiagnosticColumns").GetValue(null);
        CollectionAssert.AreEqual(diagCols, Names(text.Substring(s2, s3 - s2)), "dictionary section 2 == gaze_diagnostics.csv header");
        Assert.AreEqual(85, diagCols.Length);
        foreach (var n in new[] { "fallback_ray_origin_x", "fallback_ray_origin_y", "fallback_ray_origin_z", "fallback_ray_dir_x", "fallback_ray_dir_y", "fallback_ray_dir_z" })
            Assert.IsTrue(text.Substring(s2, s3 - s2).Split('\n').Any(l => l.Contains("| `" + n + "` |") && l.Contains("reconstructible")), n + " is explained in the dictionary");
    }

    [Test]
    public void Reader_LeavesHistoricalV16Files_Untouched_AndOnlyMergesV17Pairs()
    {
        string dir = Path.Combine(Application.temporaryCachePath, "v16_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "gaze.csv");
            File.WriteAllText(path, "time_since_start_s,left_confidence,face_state,blink_state\n0.1,0.998,current,0\n0.2,0.998,current,0\n");
            File.WriteAllText(Path.Combine(dir, "gaze_diagnostics.csv"), "t_abs,x\n1,2\n");   // an unrelated file beside a v16 file must not be joined
            var c = Csv.Load(path);
            Assert.IsFalse(c.MergedV17, "no face_closure_valid column: this is a v11-v16 file"); CollectionAssert.AreEqual(new[] { "time_since_start_s", "left_confidence", "face_state", "blink_state" }, c.Header); Assert.AreEqual(2, c.Rows.Count);
            Assert.AreEqual("0.998", c.Get(c.Rows[0], "left_confidence"), "historical columns read exactly as written");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
