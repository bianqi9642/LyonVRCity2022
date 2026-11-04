// FaceTrackingTests.cs
// Tests for the Quest Pro eyelid signal (schema v16): classification of the face-tracking layers, the SDK eye-following correction, blank-vs-zero CSV semantics, interruption bookkeeping, the optional
// diagnostic row format, and the production GazeRecorder + DataCaptureManager path driven by SYNTHETIC readings. Everything below is synthetic: it establishes logic and serialization, NOT what a real
// Quest Pro delivers (real EyesClosedL/R values, Quest Link face forwarding and the controlled closure series need the headset - see docs/face_tracking_diagnostic.md).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class FaceTrackingPureTests
{
    static object Reading(bool queried, bool valid, bool eyeFollowing, float cl, float cr, float ldl = 0f, float ldr = 0f, int frame = 7)
    {
        var r = Activator.CreateInstance(T("FaceReading"));
        Set(r, "Queried", queried); Set(r, "ExpressionsValid", valid); Set(r, "EyeFollowingValid", eyeFollowing);
        Set(r, "ClosedL", cl); Set(r, "ClosedR", cr); Set(r, "LookDownL", ldl); Set(r, "LookDownR", ldr); Set(r, "Frame", frame);
        return r;
    }

    static string Classify(bool init, bool sup, bool started, string ds, object r) => (string)CallStatic(T("FaceClosure"), "Classify", init, sup, started, ds, r);
    static string[] Cols(string state, bool known, bool started, string ds, object r) => (string[])CallStatic(T("FaceColumns"), "Values", state, known, started, ds, r);
    static string[] Names => (string[])T("FaceColumns").GetField("Names").GetValue(null);
    static string Col(string[] row, string name) => row[Array.IndexOf(Names, name)];

    [Test]
    public void Classify_SeparatesEveryLayer_AndOnlyAValidVisualFrameIsCurrent()
    {
        object ok = Reading(true, true, false, 0.2f, 0.8f);
        Assert.AreEqual("no_runtime", Classify(false, true, true, "visual", ok), "OVRPlugin not initialised (Editor without runtime)");
        Assert.AreEqual("unsupported", Classify(true, false, true, "visual", ok));
        Assert.AreEqual("no_component", Classify(true, true, true, "visual", Reading(false, false, false, 0, 0)), "FaceTrackingEnabled in the project does not create a component");
        Assert.AreEqual("not_started", Classify(true, true, false, "", Reading(true, false, false, 0, 0)), "component exists, session not started (permission pending / StartFaceTracking2 failed)");
        Assert.AreEqual("invalid_frame", Classify(true, true, true, "visual", Reading(true, false, false, 0, 0)), "started, but GetFaceState2 failed or Status.IsValid = false");
        Assert.AreEqual("audio_only_source", Classify(true, true, true, "audio", ok), "valid but audio-only: not an eyelid measurement");
        Assert.AreEqual("current", Classify(true, true, true, "visual", ok));
        Assert.AreEqual("current", Classify(true, true, true, "", ok), "data source not probed yet: the SDK's validity is not second-guessed");
    }

    [Test]
    public void Recover_FollowsTheDocumentedFormula_AndNeverClamps()
    {
        object[] a = { Reading(true, true, true, 0.2f, 0.8f, 0.10f, 0.30f), 0f, 0f };
        CallStatic(T("FaceClosure"), "Recover", a);
        Assert.AreEqual(0.30f, (float)a[1], 1e-6f, "EyeFollowing valid: EyesClosedL + min(LookDownL, LookDownR) = 0.2 + 0.1");
        Assert.AreEqual(0.90f, (float)a[2], 1e-6f);
        a = new object[] { Reading(true, true, false, 0.2f, 0.8f, 0.10f, 0.30f), 0f, 0f };
        CallStatic(T("FaceClosure"), "Recover", a);
        Assert.AreEqual(0.2f, (float)a[1], 1e-6f, "EyeFollowing invalid: the raw weight already is the lid state, look-down is zero by contract and ignored");
        Assert.AreEqual(0.8f, (float)a[2], 1e-6f);
        a = new object[] { Reading(true, true, true, 0.9f, 0.9f, 0.4f, 0.4f), 0f, 0f };
        CallStatic(T("FaceClosure"), "Recover", a);
        Assert.AreEqual(1.3f, (float)a[1], 1e-6f, "reported as measured, not clamped (the blink detector treats it as out-of-range)");
    }

    [Test]
    public void Columns_LeftRightAreNotSwapped_AndBlankIsNotZero()
    {
        // synthetic Left 0.2 / Right 0.8
        var c = Cols("current", true, true, "visual", Reading(true, true, false, 0.2f, 0.8f));
        Assert.AreEqual("0.2000", Col(c, "eyes_closed_left_recovered")); Assert.AreEqual("0.8000", Col(c, "eyes_closed_right_recovered"));
        Assert.AreEqual("1", Col(c, "face_expressions_valid")); Assert.AreEqual("0", Col(c, "eye_following_valid")); Assert.AreEqual("", Col(c, "eyes_look_down_left"), "look-down is not valid without eye following");
        Assert.AreEqual("7", Col(c, "face_sample_frame")); Assert.AreEqual("visual", Col(c, "face_data_source"));
        // synthetic Left 0.9 / Right 0.1
        c = Cols("current", true, true, "visual", Reading(true, true, false, 0.9f, 0.1f));
        Assert.AreEqual("0.9000", Col(c, "eyes_closed_left_recovered")); Assert.AreEqual("0.1000", Col(c, "eyes_closed_right_recovered"));
        // a VALID zero is 0.0000, never blank
        c = Cols("current", true, true, "visual", Reading(true, true, false, 0f, 0f));
        Assert.AreEqual("0.0000", Col(c, "eyes_closed_left_recovered")); Assert.AreEqual("0.0000", Col(c, "eyes_closed_right_recovered"));
        // eye following: look-down recorded, recovered = raw + min(look-down)
        c = Cols("current", true, true, "visual", Reading(true, true, true, 0.4f, 0.4f, 0.1f, 0.3f));
        Assert.AreEqual("0.1000", Col(c, "eyes_look_down_left")); Assert.AreEqual("0.3000", Col(c, "eyes_look_down_right"));
        Assert.AreEqual("0.5000", Col(c, "eyes_closed_left_recovered")); Assert.AreEqual("1", Col(c, "eye_following_valid"));
        // every non-current state: closure blank (not 0), validity 0 only where it was actually observed
        foreach (var st in new[] { "invalid_frame", "audio_only_source", "not_started", "no_component", "unsupported", "no_runtime" })
        {
            c = Cols(st, true, st != "not_started", st == "audio_only_source" ? "audio" : "", Reading(st != "no_component", st == "audio_only_source", false, 0.5f, 0.5f));
            foreach (var n in new[] { "eyes_look_down_left", "eyes_look_down_right", "eyes_closed_left_recovered", "eyes_closed_right_recovered", "face_sample_frame" })
                Assert.AreEqual("", Col(c, n), st + ": " + n + " must be blank, never 0 and never a previous value");
            Assert.AreEqual(st, Col(c, "face_state"));
        }
        c = Cols("no_runtime", false, false, "", Reading(false, false, false, 0, 0));
        Assert.AreEqual("", Col(c, "face_tracking_started"), "facts not known: blank, not a guessed 0");
        Assert.AreEqual(Names.Length, c.Length);
    }

    [Test]
    public void Continuity_CountsInterruptionsRecoveriesAndTheLongestGap_WithoutCarryingValues()
    {
        var fc = Activator.CreateInstance(T("FaceContinuity"));
        void Obs(bool cur, double t) => Call(fc, "Observe", cur, t);
        Obs(false, 0.0); Obs(false, 0.1); Obs(true, 0.5); Obs(true, 0.6); Obs(false, 0.7); Obs(false, 0.8); Obs(false, 1.2); Obs(true, 1.6);
        Assert.AreEqual(5L, Get<long>(fc, "rows_not_current")); Assert.AreEqual(3L, Get<long>(fc, "rows_current"));
        Assert.AreEqual(1L, Get<long>(fc, "interruptions"), "current -> not current once after the first valid frame");
        Assert.AreEqual(2L, Get<long>(fc, "recoveries"), "the first appearance and the recovery");
        Assert.AreEqual(0.9, Get<double>(fc, "longest_gap_s"), 1e-9, "0.7 -> 1.6");
    }

    [Test]
    public void DiagnosticRow_HasOneFieldPerColumn_BlankIsNotZero_AndRecoveredFollowsTheSdkRule()
    {
        Type tool = T("FaceTrackingDiagnostic"), snapT = tool.GetNestedType("Snapshot");
        var cols = (string[])tool.GetField("Columns").GetValue(null);
        object s = Activator.CreateInstance(snapT);
        Set(s, "TAbs", 12.5); Set(s, "Frame", 100); Set(s, "Segment", "left_closed"); Set(s, "SegmentT", 1.25);
        foreach (var n in new[] { "OvrInit", "Supported", "PermissionGranted", "ComponentEnabled", "SessionStarted" }) Set(s, n, true);
        Set(s, "ApiOk", (bool?)true); Set(s, "StatusValid", (bool?)true); Set(s, "EyeFollowing", (bool?)true); Set(s, "WrapperValid", (bool?)true);
        Set(s, "DataSource", "visual"); Set(s, "ProviderTime", (double?)3.5);
        Set(s, "ApiClosedL", (float?)0.2f); Set(s, "ApiClosedR", (float?)0.8f); Set(s, "WrapClosedL", (float?)0.2f); Set(s, "WrapClosedR", (float?)0.8f);
        Set(s, "WrapLookDownL", (float?)0.1f); Set(s, "WrapLookDownR", (float?)0.3f); Set(s, "GazeRowFrame", (int?)99);
        string[] f = ((string)CallStatic(tool, "FormatRow", s)).Split(',');
        Assert.AreEqual(cols.Length, f.Length, "one field per column");
        string F(string n) => f[Array.IndexOf(cols, n)];
        Assert.AreEqual("0.2000", F("api_closed_l")); Assert.AreEqual("0.8000", F("api_closed_r")); Assert.AreEqual("left_closed", F("segment"));
        Assert.AreEqual("0.3000", F("recovered_l")); Assert.AreEqual("0.9000", F("recovered_r"), "0.2 + min(0.1, 0.3), 0.8 + min(0.1, 0.3)");
        Assert.AreEqual("1", F("gaze_row_frame_lag"));
        // wrapper invalid: wrapper values and recovered stay blank (no previous value, no zero); API fields untouched
        Set(s, "WrapperValid", (bool?)false); Set(s, "WrapClosedL", (float?)null); Set(s, "WrapClosedR", (float?)null);
        f = ((string)CallStatic(tool, "FormatRow", s)).Split(',');
        Assert.AreEqual("", F("wrap_closed_l")); Assert.AreEqual("", F("recovered_l")); Assert.AreEqual("0", F("wrapper_valid")); Assert.AreEqual("0.2000", F("api_closed_l"));
        // API not called at all: blank, not 0
        Set(s, "ApiOk", (bool?)null); Set(s, "ApiClosedL", (float?)null); Set(s, "ApiClosedR", (float?)null); Set(s, "StatusValid", (bool?)null);
        f = ((string)CallStatic(tool, "FormatRow", s)).Split(',');
        Assert.AreEqual("", F("get_face_state_ok")); Assert.AreEqual("", F("api_closed_l"));
    }
}

// ================================================================================== production recorder path (synthetic readings)

public partial class NpcLifecycleTests
{
    static void SetFaceSeam(Component rec, Func<object> make)
    {
        var fi = rec.GetType().GetField("testFaceReading");
        var body = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(make)), fi.FieldType.GetGenericArguments()[0]);
        fi.SetValue(rec, System.Linq.Expressions.Expression.Lambda(fi.FieldType, body).Compile());
    }

    static object FaceReading(bool valid, bool ef, float cl, float cr, float ldl = 0f, float ldr = 0f)
    {
        var r = Activator.CreateInstance(T("FaceReading"));
        Set(r, "ExpressionsValid", valid); Set(r, "EyeFollowingValid", ef); Set(r, "ClosedL", cl); Set(r, "ClosedR", cr); Set(r, "LookDownL", ldl); Set(r, "LookDownR", ldr); Set(r, "Frame", Time.frameCount);
        return r;
    }

    static object FactsAllOn(string dataSource = "")
    {
        var f = Activator.CreateInstance(T("BlinkFacts"));
        foreach (var n in new[] { "OvrPluginInitialized", "FaceTrackingSupported", "FaceTrackingEnabled", "ComponentPresent", "PermissionGranted", "SignalValid" }) Set(f, n, true);
        Set(f, "DataSource", dataSource);
        return f;
    }

    [UnityTest]
    public IEnumerator Face_SyntheticLeftRightValues_FlowToTheRightGazeColumns_BlankIsNotZero_AndNothingIsCarriedAcrossAnInterruption()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        float t0 = Time.realtimeSinceStartup;
        // 0.0-0.3 L0.2/R0.8 | 0.3-0.6 L0.9/R0.1 | 0.6-0.9 INVALID | 0.9-1.2 valid zero | then L0.2/R0.8 again
        SetFaceSeam(rec, () =>
        {
            float t = Time.realtimeSinceStartup - t0;
            if (t < 0.3f) return FaceReading(true, false, 0.2f, 0.8f);
            if (t < 0.6f) return FaceReading(true, false, 0.9f, 0.1f);
            if (t < 0.9f) return FaceReading(false, false, 0.55f, 0.55f);   // stale-looking numbers that MUST NOT be recorded
            if (t < 1.2f) return FaceReading(true, false, 0f, 0f);
            return FaceReading(true, false, 0.2f, 0.8f);
        });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(1.6f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        Assert.Greater(gaze.Rows.Count, 20);
        var seen = new HashSet<string>();
        gaze.Rows.RemoveAll(rw => gaze.Get(rw, "face_state") == "no_runtime");   // rows written before the seams were installed (the sampler runs from BeginTrial)
        double prevT = double.NegativeInfinity;
        foreach (var row in gaze.Rows)
        {
            string l = gaze.Get(row, "eyes_closed_left"), r = gaze.Get(row, "eyes_closed_right"), st = gaze.Get(row, "face_state");
            double tAbs = double.Parse(gaze.Get(row, "time_since_start_s"), System.Globalization.CultureInfo.InvariantCulture); Assert.GreaterOrEqual(tAbs, prevT, "timestamps are monotonic"); prevT = tAbs;
            if (st == "current")
            {
                seen.Add(l + "/" + r);
                CollectionAssert.Contains(new[] { "0.2000/0.8000", "0.9000/0.1000", "0.0000/0.0000" }, l + "/" + r, "left and right are never swapped or mixed across samples");
                Assert.AreEqual(l, gaze.Get(row, "eyes_closed_left_recovered")); Assert.AreEqual(r, gaze.Get(row, "eyes_closed_right_recovered"));
                Assert.AreEqual("1", gaze.Get(row, "face_expressions_valid")); Assert.AreEqual("visual", gaze.Get(row, "face_data_source"));
                Assert.AreNotEqual("", gaze.Get(row, "face_sample_frame"));
            }
            else
            {
                Assert.AreEqual("invalid_frame", st); Assert.AreEqual("", l, "invalid frame: blank, never 0 and never the previous value"); Assert.AreEqual("", r);
                Assert.AreEqual("", gaze.Get(row, "eyes_closed_left_recovered")); Assert.AreEqual("0", gaze.Get(row, "face_expressions_valid")); Assert.AreEqual("", gaze.Get(row, "blink_state"));
            }
            // REGRESSION: the gaze columns the face work must not touch are still produced for every row
            Assert.AreEqual("1", gaze.Get(row, "left_valid")); Assert.AreEqual("1", gaze.Get(row, "right_valid"));
        }
        CollectionAssert.IsSubsetOf(new[] { "0.2000/0.8000", "0.9000/0.1000", "0.0000/0.0000" }, seen);
        Assert.IsTrue(gaze.Rows.Any(row => gaze.Get(row, "face_state") == "invalid_frame"), "the interruption is visible in the data");
        var bm = ManifestOf(rig)["eye_tracking"]["blink"];
        Assert.GreaterOrEqual((long)bm["interruptions"], 1L); Assert.GreaterOrEqual((long)bm["recoveries"], 1L); Assert.Greater((long)bm["rows_face_current"], 0L); Assert.Greater((long)bm["rows_face_not_current"], 0L);
        Assert.AreEqual("current", (string)bm["face_state_last"]);
    }

    [UnityTest]
    public IEnumerator Face_EyeFollowing_RawStaysRaw_RecoveredDrivesTheBlink_AndAudioOnlyIsNotAnEyelidMeasurement()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("visual")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        float t0 = Time.realtimeSinceStartup;
        // raw 0.40 is BELOW the 0.7 blink threshold, but with look-down 0.40 the true lid closure is 0.80 -> a blink from 0.4 s to 0.8 s
        SetFaceSeam(rec, () =>
        {
            float t = Time.realtimeSinceStartup - t0;
            bool closed = t >= 0.4f && t < 0.8f;
            return closed ? FaceReading(true, true, 0.40f, 0.40f, 0.40f, 0.40f) : FaceReading(true, true, 0.02f, 0.02f, 0.10f, 0.10f);
        });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(1.3f);
        Call(rec, "StopRecording");
        var evs = Events(rig);
        Assert.AreEqual(1, Rows(evs, "event_type", "blink_start").Count, "the recovered closure (0.80) crosses the threshold although the raw weight (0.40) does not");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        var closedRow = gaze.Rows.First(r => gaze.Get(r, "blink_state") == "1");
        Assert.AreEqual("0.4000", gaze.Get(closedRow, "eyes_closed_left"), "eyes_closed_left keeps its meaning: the raw SDK weight");
        Assert.AreEqual("0.8000", gaze.Get(closedRow, "eyes_closed_left_recovered")); Assert.AreEqual("0.4000", gaze.Get(closedRow, "eyes_look_down_left")); Assert.AreEqual("1", gaze.Get(closedRow, "eye_following_valid"));
        Assert.Greater((long)ManifestOf(rig)["eye_tracking"]["blink"]["rows_eye_following_valid"], 0L);
    }

    [UnityTest]
    public IEnumerator Face_AudioOnlySource_IsRecordedAsSuch_WithBlankClosure_AndNoBlink()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        SetConstDelegate(rec, "testBlinkFacts", FactsAllOn("audio")); Set(rec, "_nextBlinkCapabilityT", 0.0);
        SetFaceSeam(rec, () => FaceReading(true, false, 0.95f, 0.95f));   // would be a blink if it were an eyelid measurement
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.6f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        gaze.Rows.RemoveAll(rw => gaze.Get(rw, "face_state") == "no_runtime");   // rows before the facts were injected
        Assert.Greater(gaze.Rows.Count, 3);
        foreach (var row in gaze.Rows)
        {
            Assert.AreEqual("audio_only_source", gaze.Get(row, "face_state")); Assert.AreEqual("", gaze.Get(row, "eyes_closed_left")); Assert.AreEqual("", gaze.Get(row, "eyes_closed_left_recovered"));
            Assert.AreEqual("", gaze.Get(row, "blink_state")); Assert.AreEqual("audio", gaze.Get(row, "face_data_source"));
        }
        Assert.AreEqual(0, Rows(Events(rig), "event_type", "blink_start").Count);
        Assert.Greater((long)ManifestOf(rig)["eye_tracking"]["blink"]["rows_audio_only_source"], 0L);
    }

    [UnityTest]
    public IEnumerator Face_NoComponentAndUnsupported_AreSeparateStates_NothingIsFabricated()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        var f = Activator.CreateInstance(T("BlinkFacts")); Set(f, "OvrPluginInitialized", true); Set(f, "FaceTrackingSupported", true); Set(f, "PermissionGranted", true);   // supported, no component, no seam
        SetConstDelegate(rec, "testBlinkFacts", f); Set(rec, "_nextBlinkCapabilityT", 0.0);
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.5f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        gaze.Rows.RemoveAll(rw => gaze.Get(rw, "face_state") == "no_runtime");   // rows before the facts were injected
        Assert.Greater(gaze.Rows.Count, 3);
        foreach (var row in gaze.Rows)
        {
            Assert.AreEqual("no_component", gaze.Get(row, "face_state"), "FaceTrackingEnabled in the project settings does not start face tracking: no OVRFaceExpressions means no signal");
            Assert.AreEqual("", gaze.Get(row, "eyes_closed_left")); Assert.AreEqual("", gaze.Get(row, "eyes_closed_left_recovered")); Assert.AreEqual("", gaze.Get(row, "face_sample_frame"));
            Assert.AreEqual("0", gaze.Get(row, "face_tracking_started"), "the runtime session is not started: observed 0, not blank");
        }
        var bm = ManifestOf(rig)["eye_tracking"]["blink"];
        Assert.AreEqual("not_configured", (string)bm["status"]); Assert.IsTrue((bool)bm["auto_create_enabled"]); Assert.IsFalse((bool)bm["component_auto_created"]);
    }

    [UnityTest]
    public IEnumerator Face_AutoCreate_IsOffInTheEditorWithoutARuntime_AndNeverCreatesAComponentThere()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Set(rec, "_nextBlinkCapabilityT", 0.0);   // real runtime probe (OVRPlugin is not initialised in the test Editor)
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rec, "StopRecording");
        Assert.IsFalse(rec.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "OVRFaceExpressions"), "without a runtime nothing is created (and nothing would work anyway)");
        Assert.IsFalse(Get<bool>(rec, "FaceAutoCreated"));
        var gaze = Csv.Load(rig.File("gaze.csv"));
        foreach (var row in gaze.Rows) { Assert.AreEqual("no_runtime", gaze.Get(row, "face_state")); Assert.AreEqual("", gaze.Get(row, "eyes_closed_left")); }
    }
}
