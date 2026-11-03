// CaptureReliabilityTests.cs
// Tests for the data-capture reliability work (schema_version 11): G2 raw-confidence recording, G3 blink capability / detection, M1 canonical eye-tracking state, M2 the central session capture
// lifecycle, M3 evidence-based sampling diagnosis, N2 initial destination assignment. They drive the PRODUCTION classes through reflection with scripted / synthetic inputs and read the files that are
// really written. Editor tests: they establish logic, NOT Quest Pro behaviour (the real confidence stream, the face-tracking signal and the unworn / suspended headset need hardware).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using static Reflect;

// ================================================================================== pure logic

public class CaptureReliabilityPureTests
{
    static readonly Type Conf = T("GazeConfidence");

    // ---------------------------------------------------------------- G2

    [Test]
    public void G2_Format_PreservesRealValues_AGenuineZeroIsAValue_AndMissingIsBlank()
    {
        Assert.AreEqual("0.620", (string)CallStatic(Conf, "Format", 0.62f, true));
        Assert.AreEqual("0.000", (string)CallStatic(Conf, "Format", 0f, true), "a genuine zero confidence is recorded as 0.000, not replaced by a default");
        Assert.AreEqual("", (string)CallStatic(Conf, "Format", 0f, false), "no measurement is blank - never 0.000");
        Assert.AreEqual("", (string)CallStatic(Conf, "Format", 0.5f, false), "and never the 0.5 threshold / default");
        Assert.IsFalse((bool)CallStatic(Conf, "Usable", float.NaN)); Assert.IsFalse((bool)CallStatic(Conf, "Usable", float.PositiveInfinity)); Assert.IsTrue((bool)CallStatic(Conf, "Usable", 0f));
    }

    [Test]
    public void G2_Combined_FollowsTheDocumentedDerivation_AndNeverAveragesAMissingEye()
    {
        object[] a;
        float C(bool lv, bool lh, float l, bool rv, bool rh, float r, out bool ok) { a = new object[] { lv, lh, l, rv, rh, r, 0f }; ok = (bool)CallStatic(Conf, "TryCombine", a); return (float)a[6]; }
        float c = C(true, true, 0.6f, true, true, 0.9f, out bool ok);   Assert.IsTrue(ok); Assert.AreEqual(0.75f, c, 1e-6f, "both valid: mean of the two raw values");
        c = C(true, true, 0.6f, true, false, 0f, out ok);               Assert.IsFalse(ok, "a contributing eye without a value: combined is unavailable, not a half average");
        c = C(true, true, 0.8f, false, true, 0.1f, out ok);             Assert.IsTrue(ok); Assert.AreEqual(0.8f, c, 1e-6f, "one valid eye: that eye's value; the invalid eye does not contribute");
        c = C(false, true, 0.8f, true, true, 0.3f, out ok);             Assert.IsTrue(ok); Assert.AreEqual(0.3f, c, 1e-6f);
        c = C(false, true, 0.8f, false, true, 0.3f, out ok);            Assert.IsFalse(ok, "head-fallback ray: no combined confidence");
        c = C(true, true, 0f, true, true, 0f, out ok);                  Assert.IsTrue(ok); Assert.AreEqual(0f, c, "genuine zeros combine to a genuine zero");
    }

    [Test]
    public void G2_MissingConfidence_NeverInvalidatesAProviderValidEye_ButTheThresholdStillRejectsAMeasuredLowValue()
    {
        bool V(float conf, bool avail, bool known, bool valid) => (bool)CallStatic(Conf, "IsValid", conf, avail, 0.5f, known, valid);
        Assert.IsTrue(V(0f, false, true, true), "provider valid, nothing measured: the threshold cannot reject it");
        Assert.IsFalse(V(0f, false, true, false), "provider says invalid");
        Assert.IsFalse(V(0f, false, false, true), "no provider probe and no confidence: nothing establishes validity");
        Assert.IsFalse(V(0.49f, true, true, true), "a measured value below the threshold is rejected");
        Assert.IsTrue(V(0.5f, true, true, true), "threshold is >=");
        Assert.IsTrue(V(0.9f, true, false, false), "legacy path without a probe: the confidence rule decides");
        Assert.IsFalse((bool)CallStatic(Conf, "AvailableAfterProvider", true, true, false), "stale SDK confidence of a provider-invalid eye is not an observation");
        Assert.IsTrue((bool)CallStatic(Conf, "AvailableAfterProvider", true, false, false), "unknown probe: the value is kept");
        Assert.IsFalse((bool)CallStatic(Conf, "AvailableAfterProvider", false, true, true));
    }

    [Test]
    public void G2_ConfidenceStream_FlagsAConstantProvider_AndRecognisesAVaryingOne()
    {
        var constant = Activator.CreateInstance(T("ConfidenceManifest"));
        var cl = Get<object>(constant, "left");
        for (int i = 0; i < 60; i++) Call(cl, "Observe", 0.5f);
        Call(constant, "Refresh");
        Assert.AreEqual("constant", Get<string>(cl, "behaviour"));
        Assert.AreEqual("constant_on_this_runtime", Get<string>(constant, "semantics"), "60 identical raw values: preserved, but flagged as carrying no quality information");
        Assert.AreEqual(1, Get<int>(cl, "distinct_values"));

        var varying = Activator.CreateInstance(T("ConfidenceManifest"));
        var vl = Get<object>(varying, "left");
        var rnd = new System.Random(7);
        for (int i = 0; i < 60; i++) Call(vl, "Observe", (float)(0.5 + rnd.NextDouble() * 0.5));
        Call(varying, "Refresh");
        Assert.AreEqual("varying_provider_confidence", Get<string>(varying, "semantics"));
        Assert.Greater(Get<int>(vl, "distinct_values"), 10);

        var few = Activator.CreateInstance(T("ConfidenceManifest")); Call(Get<object>(few, "left"), "Observe", 0.5f); Call(few, "Refresh");
        Assert.AreEqual("insufficient_samples", Get<string>(few, "semantics"), "one value is not enough to call a stream constant");
        var none = Activator.CreateInstance(T("ConfidenceManifest")); Call(none, "Refresh");
        Assert.AreEqual("no_samples", Get<string>(none, "semantics"));
        Assert.AreEqual(-1.0, Get<double>(Get<object>(none, "left"), "min"), "no observation is -1, not 0.5");
        StringAssert.Contains("never written into an observation column", Get<string>(none, "threshold_role"));
    }

    // ---------------------------------------------------------------- G3

    static string Decide(bool init, bool sup, bool en, bool comp, bool perm, bool valid, out string reason)
    {
        var f = Activator.CreateInstance(T("BlinkFacts"));
        Set(f, "OvrPluginInitialized", init); Set(f, "FaceTrackingSupported", sup); Set(f, "FaceTrackingEnabled", en); Set(f, "ComponentPresent", comp); Set(f, "PermissionGranted", perm); Set(f, "SignalValid", valid);
        var args = new object[] { f, null };
        string s = (string)CallStatic(T("BlinkCapability"), "Decide", args);
        reason = (string)args[1];
        return s;
    }

    [Test]
    public void G3_Capability_IsDecidedFromRuntimeFacts_NeverFromTheAbsenceOfBlinks()
    {
        Assert.AreEqual("unavailable", Decide(false, false, false, false, false, false, out _), "runtime cannot be queried: unavailable, not unsupported");
        Assert.AreEqual("unsupported", Decide(true, false, false, false, true, false, out string r1)); StringAssert.Contains("runtime_reports_no_face_tracking_support", r1);
        Assert.AreEqual("not_configured", Decide(true, true, false, false, true, false, out string r2)); StringAssert.Contains("no_OVRFaceExpressions_component", r2);
        Assert.AreEqual("not_configured", Decide(true, true, false, true, false, false, out string r3)); StringAssert.Contains("permission", r3);
        Assert.AreEqual("not_configured", Decide(true, true, false, true, true, false, out _), "supported, component and permission present but face tracking not started");
        Assert.AreEqual("unavailable", Decide(true, true, true, true, true, false, out _), "enabled but the signal is not valid right now");
        Assert.AreEqual("available", Decide(true, true, true, true, true, true, out _));
    }

    static object Detector(float close = 0.7f, float open = 0.5f) => Activator.CreateInstance(T("BlinkDetector"), close, open);
    static (bool started, bool ended, bool truncated, double start, double end) Feed(object d, double t, float? l, float? r)
    {
        var res = Call(d, "Update", t, l, r);
        return (Get<bool>(res, "Started"), Get<bool>(res, "Ended"), Get<bool>(res, "Truncated"), Get<double>(res, "StartT"), Get<double>(res, "EndT"));
    }

    [Test]
    public void G3_Detector_OpenEyes_NeverBlink_SustainedAndBriefClosuresAreEachOneBlink_WithHysteresis()
    {
        var d = Detector();
        for (int i = 0; i < 20; i++) Assert.IsFalse(Feed(d, i * 0.05, 0.05f, 0.04f).started, "normal open-eye measurements");
        Assert.AreEqual(0L, Get<long>(d, "Blinks"));
        // brief closure: 2 samples
        Assert.IsTrue(Feed(d, 1.00, 0.9f, 0.85f).started);
        Assert.IsFalse(Feed(d, 1.05, 0.95f, 0.9f).ended);
        var e = Feed(d, 1.10, 0.1f, 0.1f); Assert.IsTrue(e.ended); Assert.AreEqual(1.00, e.start, 1e-9); Assert.AreEqual(1.10, e.end, 1e-9);
        // sustained closure: 1.5 s, then reopening
        Assert.IsTrue(Feed(d, 2.0, 0.9f, 0.9f).started);
        for (int i = 1; i < 30; i++) { var s = Feed(d, 2.0 + i * 0.05, 0.95f, 0.95f); Assert.IsFalse(s.started || s.ended); }
        var e2 = Feed(d, 3.5, 0.2f, 0.2f); Assert.IsTrue(e2.ended); Assert.AreEqual(1.5, e2.end - e2.start, 1e-9);
        Assert.AreEqual(2L, Get<long>(d, "Blinks")); Assert.AreEqual(1.5, Get<double>(d, "MaxDuration"), 1e-9);
        // jitter between the two thresholds: no repeated events
        var j = Detector();
        int events = 0;
        foreach (float v in new[] { 0.9f, 0.6f, 0.72f, 0.55f, 0.69f, 0.51f, 0.8f, 0.2f }) { var s = Feed(j, events * 0.01 + 5, v, v); if (s.started || s.ended) events++; }
        Assert.AreEqual(2, events, "exactly one start and one end across noisy values between 0.5 and 0.7 (hysteresis)");
        Assert.AreEqual(1L, Get<long>(j, "Blinks"), "duplicate blink events are prevented");
    }

    [Test]
    public void G3_Detector_TrackingLossIsNotABlink_MissingDataTruncates_InvalidAndUnilateralAreNotBlinks()
    {
        var d = Detector();
        for (int i = 0; i < 10; i++) Feed(d, i * 0.05, null, null);                    // tracking lost, eyes never reported closed
        for (int i = 0; i < 10; i++) Feed(d, 1 + i * 0.05, float.NaN, 0.9f);           // one eye missing
        for (int i = 0; i < 5; i++) Feed(d, 2 + i * 0.05, 1.5f, 0.9f);                 // out of range
        Assert.AreEqual(0L, Get<long>(d, "Blinks"), "missing / invalid data never creates a blink");
        Assert.AreEqual(25L, Get<long>(d, "InvalidSamples")); Assert.AreEqual(5L, Get<long>(d, "OutOfRangeSamples"));
        for (int i = 0; i < 6; i++) Feed(d, 3 + i * 0.05, 0.95f, 0.1f);                // one eye closed, the other open
        Assert.AreEqual(0L, Get<long>(d, "Blinks")); Assert.AreEqual(6L, Get<long>(d, "UnilateralClosureSamples"), "a unilateral closure is counted, not a blink");
        // data lost in the middle of a closure: closed at the last valid closed sample and marked truncated
        Assert.IsTrue(Feed(d, 4.0, 0.9f, 0.9f).started);
        Feed(d, 4.05, 0.95f, 0.95f);
        var t = Feed(d, 4.10, null, null);
        Assert.IsTrue(t.ended); Assert.IsTrue(t.truncated); Assert.AreEqual(4.05, t.end, 1e-9);
        Assert.AreEqual(1L, Get<long>(d, "TruncatedBlinks"));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => Detector(0.4f, 0.5f), "thresholds must have hysteresis");
    }

    // ---------------------------------------------------------------- M3

    // 20 Hz nominal; every 20th sample is preceded by a long gap whose evidence is supplied by the scenario.
    static object RunScenario(double gap, double frameMax, int frames, double prevWrite, int gapEvery = 10, int cycles = 12, bool noEvidence = false)
    {
        var st = Activator.CreateInstance(T("SamplingStats"), 20.0);
        double t = 0;
        for (int c = 0; c < cycles; c++)
            for (int i = 0; i < gapEvery; i++)
            {
                bool isGap = i == 0 && c > 0;
                double dt = isGap ? gap : 0.05;
                t += dt;
                if (noEvidence && isGap) Call(st, "OnSampleWithEvidence", t, double.NaN, 0, 0.001);
                else if (isGap) Call(st, "OnSampleWithEvidence", t, frameMax, frames, 0.001);
                else Call(st, "OnSampleWithEvidence", t, 0.016, 3, 0.001);
                if (isGap) { /* the previous row's write is the one that caused the gap */ }
            }
        return st;
    }

    // Builds the stream with the previous-row write time controlled: the write that precedes each gap takes prevWrite seconds.
    static object RunScenarioWithWrites(double gap, double frameMax, int frames, double slowWrite, int cycles = 12)
    {
        var st = Activator.CreateInstance(T("SamplingStats"), 20.0);
        double t = 0;
        for (int c = 0; c < cycles; c++)
        {
            for (int i = 0; i < 9; i++) { t += 0.05; Call(st, "OnSampleWithEvidence", t, 0.016, 3, (i == 8) ? slowWrite : 0.001); }   // the 9th row's write is the slow one
            t += gap; Call(st, "OnSampleWithEvidence", t, frameMax, frames, 0.001);                                                    // the next sample follows the long gap
        }
        return st;
    }

    static string Diagnose(object st, double frameMeanHz, long dropped, out string reason)
    {
        var args = new object[] { Get<double>(st, "NominalHz"), Get<double>(st, "ObservedHz"), frameMeanHz,
            Get<long>(st, "GapsFrameLimited"), Get<long>(st, "GapsSchedulerLimited"), Get<long>(st, "GapsIoLimited"), Get<long>(st, "GapsMixed"), Get<long>(st, "GapsUnknown"), dropped, null };
        string s = (string)CallStatic(T("RateDiagnosis"), "ClassifyStream", args);
        reason = (string)args[9];
        return s;
    }

    [Test]
    public void M3_ScenarioA_NormalOperation_IsHealthy()
    {
        var st = Activator.CreateInstance(T("SamplingStats"), 20.0);
        for (int i = 0; i < 200; i++) Call(st, "OnSampleWithEvidence", i * 0.05, 0.016, 3, 0.001);
        Assert.AreEqual("healthy", Diagnose(st, 60, 0, out _)); Assert.AreEqual(0L, Get<long>(st, "UnexpectedGaps"));
    }

    [Test]
    public void M3_ScenarioB_FrameLimited_LongFramesWithoutWriterBacklog()
    {
        var st = RunScenario(gap: 0.35, frameMax: 0.33, frames: 1, prevWrite: 0.001);
        Assert.Greater(Get<long>(st, "UnexpectedGaps"), 5); Assert.AreEqual(Get<long>(st, "UnexpectedGaps"), Get<long>(st, "GapsFrameLimited"));
        string d = Diagnose(st, 24, 0, out string why);
        Assert.AreEqual("frame_limited", d, why); Assert.AreNotEqual("scheduling_or_io_limited", d, "the old catch-all label is gone: a frame-hitch dominated session is frame_limited");
    }

    [Test]
    public void M3_ScenarioC_SchedulerLimited_FramesKeepArriving_ButDispatchIsLate()
    {
        var st = RunScenario(gap: 0.35, frameMax: 0.02, frames: 17, prevWrite: 0.001);
        Assert.AreEqual(Get<long>(st, "UnexpectedGaps"), Get<long>(st, "GapsSchedulerLimited"), "frames ran normally, writes were quick, the sample was still late");
        Assert.AreEqual("scheduler_limited", Diagnose(st, 60, 0, out _));
    }

    [Test]
    public void M3_ScenarioD_IoLimited_DispatchedOnTime_ButTheWriteConsumedTheGap()
    {
        var st = RunScenarioWithWrites(gap: 0.30, frameMax: 0.29, frames: 1, slowWrite: 0.28);   // the slow write also made the frame long; it is subtracted from the frame evidence
        Assert.AreEqual(Get<long>(st, "UnexpectedGaps"), Get<long>(st, "GapsIoLimited"), "a slow write is I/O, not a frame hitch");
        Assert.AreEqual(0L, Get<long>(st, "GapsFrameLimited"));
        Assert.AreEqual("io_limited", Diagnose(st, 60, 0, out _));
    }

    [Test]
    public void M3_ScenarioE_Mixed_FrameTimeAndWriterBothMaterial()
    {
        var st = RunScenarioWithWrites(gap: 0.30, frameMax: 0.29, frames: 1, slowWrite: 0.10);   // write 0.10 s + 0.19 s of frame time not explained by it
        Assert.AreEqual(Get<long>(st, "UnexpectedGaps"), Get<long>(st, "GapsMixed"));
        Assert.AreEqual("mixed", Diagnose(st, 60, 0, out _));
        // and at stream level: half the gaps frame-explained, half io-explained
        var a = RunScenario(0.35, 0.33, 1, 0.001, cycles: 6); var b = RunScenarioWithWrites(0.30, 0.29, 1, 0.28, cycles: 6);
        long f = Get<long>(a, "GapsFrameLimited"), io = Get<long>(b, "GapsIoLimited");
        var args = new object[] { 20.0, 12.0, 60.0, f, 0L, io, 0L, 0L, 0L, null };
        Assert.AreEqual("mixed", (string)CallStatic(T("RateDiagnosis"), "ClassifyStream", args), (string)args[9]);
    }

    [Test]
    public void M3_ScenarioF_Unknown_WhenTheEvidenceCannotDistinguishTheCause()
    {
        var st = RunScenario(gap: 0.35, frameMax: 0, frames: 0, prevWrite: 0.001, noEvidence: true);
        Assert.AreEqual(Get<long>(st, "UnexpectedGaps"), Get<long>(st, "GapsUnknown"));
        string d = Diagnose(st, 60, 0, out string why);
        Assert.AreEqual("unknown", d, why);
        var args = new object[] { 20.0, 8.0, 60.0, 0L, 0L, 0L, 0L, 0L, 0L, null };
        Assert.AreEqual("unknown", (string)CallStatic(T("RateDiagnosis"), "ClassifyStream", args), "a low rate with no attributable gaps is not guessed at");
        args = new object[] { 20.0, double.NaN, 60.0, 0L, 0L, 0L, 0L, 0L, 0L, null };
        Assert.AreEqual("unknown", (string)CallStatic(T("RateDiagnosis"), "ClassifyStream", args)); Assert.AreEqual("insufficient_samples", (string)args[9]);
        args = new object[] { 20.0, 8.0, 60.0, 0L, 0L, 0L, 0L, 0L, 12L, null };
        Assert.AreEqual("io_limited", (string)CallStatic(T("RateDiagnosis"), "ClassifyStream", args), "dropped records are I/O evidence in their own right");
    }

    [Test]
    public void M3_FrameTimingWindows_AndWriterCommitTiming_AreMeasured()
    {
        var fs = Activator.CreateInstance(T("FrameTimingStats"));
        for (int i = 0; i < 5; i++) Call(fs, "OnFrame", 0.02);
        Call(fs, "OnFrame", 0.4); Call(fs, "OnStallFrame", 3.0);
        var args = new object[] { 0, 0.0, 0 }; // TakeWindow(int, out double, out int)
        var m = T("FrameTimingStats").GetMethod("TakeWindow"); var a = new object[] { 0, 0.0, 0 }; m.Invoke(fs, a);
        Assert.AreEqual(3.0, (double)a[1], 1e-9, "the stall frame is evidence for the gap it caused"); Assert.AreEqual(7, (int)a[2]);
        a = new object[] { 0, 0.0, 0 }; m.Invoke(fs, a); Assert.IsTrue(double.IsNaN((double)a[1]), "the window was consumed"); Assert.AreEqual(0, (int)a[2]);
        a = new object[] { 1, 0.0, 0 }; m.Invoke(fs, a); Assert.AreEqual(7, (int)a[2], "each stream has its own window");

        string dir = Path.Combine(Path.GetTempPath(), "CaptureReliability_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var w = Activator.CreateInstance(T("CaptureCsvWriter"), Path.Combine(dir, "x.csv"), "a,b", null, false);
            Call(w, "WriteLine", "1,2");
            Assert.AreEqual(1L, Get<long>(w, "Commits")); Assert.GreaterOrEqual(Get<double>(w, "LastCommitSeconds"), 0.0); Assert.GreaterOrEqual(Get<double>(w, "MaxCommitSeconds"), Get<double>(w, "LastCommitSeconds"));
            Call(w, "Close"); Call(w, "Close");
            Call(w, "WriteLine", "3,4"); Call(w, "WriteLines", new List<string> { "5,6", "7,8" });
            Assert.IsTrue(Get<bool>(w, "IsClosed")); Assert.AreEqual(3L, Get<long>(w, "RowsRejectedAfterClose"));
            Assert.AreEqual(1L, Get<long>(w, "RowsWritten"), "nothing was appended after Close()"); Assert.AreEqual(1L, Get<long>(w, "RowsAttempted"), "refused records are not counted as attempts");
            var lines = File.ReadAllLines(Path.Combine(dir, "x.csv")).Where(l => l.Length > 0).ToArray();
            Assert.AreEqual("1,2", lines[lines.Length - 1], "the last row on disk is the one written before Close()"); Assert.IsFalse(lines.Contains("3,4") || lines.Contains("5,6"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- M1 (canonical state)

    sealed class World { public bool xrReady = true, supported = true, trackingEnabled, permissionGranted = true; public bool[] present = { true, true }, enabled = { false, false }; public Action<int> pending; public int mask = 7; public double provT = 1; }

    static object MakeLc(World w, bool android, string path)
    {
        var p = Activator.CreateInstance(T("EyeTrackingPlatform"));
        Set(p, "RuntimePath", path); Set(p, "PermissionApplicable", android);
        Set(p, "XrReady", (Func<bool>)(() => w.xrReady)); Set(p, "Supported", (Func<bool>)(() => w.supported)); Set(p, "TrackingEnabled", (Func<bool>)(() => w.trackingEnabled));
        Set(p, "PermissionGranted", (Func<bool>)(() => w.permissionGranted)); Set(p, "WorldFrameReady", (Func<bool>)(() => true));
        Set(p, "RequestPermission", (Action<Action<int>>)(cb => w.pending = cb));
        Set(p, "ComponentPresent", (Func<int, bool>)(i => w.present[i])); Set(p, "ComponentEnabled", (Func<int, bool>)(i => w.enabled[i]));
        Set(p, "SetComponentEnabled", (Action<int, bool>)((i, on) => { if (!on) { w.enabled[i] = false; return; } bool ok = !android || w.permissionGranted; w.enabled[i] = ok; if (ok) w.trackingEnabled = true; }));
        Set(p, "ReadProvider", (Func<int>)(() => { w.provT += 0.01; return w.mask; })); Set(p, "ProviderTime", (Func<double>)(() => w.provT));
        return Activator.CreateInstance(T("EyeTrackingLifecycle"), p, Activator.CreateInstance(T("EyeTrackingLifecycleOptions")));
    }

    static double Run(object lc, double t0, double t1) { double t = t0; for (; t <= t1 + 1e-9; t += 0.1) Call(lc, "Tick", t); return t - 0.1; }

    static (string cap, string perm, bool configured, bool runtime, bool active) Canon(object lc, double now)
    {
        var m = Activator.CreateInstance(T("EyeTrackingManifest"));
        Call(m, "FillLifecycle", lc, now, "");
        return (Get<string>(m, "capability"), Get<string>(m, "permission"), Get<bool>(m, "configured"), Get<bool>(m, "runtime_available"), Get<bool>(m, "tracking_active"));
    }

    [Test]
    public void M1_Canonical_SupportedGrantedActive_ThenTrackingLostIsNotARevocation_ThenRecovered()
    {
        var w = new World { permissionGranted = false }; var lc = MakeLc(w, true, "android_standalone");
        double t = Run(lc, 0, 1);
        Assert.IsNotNull(w.pending, "the permission request is pending (not granted yet)"); w.permissionGranted = true; w.pending((int)T("EyePermissionOutcome").GetField("Granted").GetRawConstantValue());
        t = Run(lc, t + 0.1, t + 8);
        var c = Canon(lc, t);
        Assert.AreEqual(("supported", "granted", true, true, true), c, "supported, permission granted, tracking active");
        w.mask = 1;   // provider answers but no eye valid: tracking lost
        t = Run(lc, t + 0.1, t + 8);
        c = Canon(lc, t);
        Assert.AreEqual(("supported", "granted", true, true, false), c, "tracking loss changes tracking_active ONLY - capability, permission and configured are untouched");
        w.mask = 7; t = Run(lc, t + 0.1, t + 4);
        Assert.AreEqual(("supported", "granted", true, true, true), Canon(lc, t), "recovered without a new permission request");
    }

    [Test]
    public void M1_Canonical_PermissionDenied_Unsupported_ProviderUnavailable_PauseResume()
    {
        var denied = new World { permissionGranted = false }; var lcD = MakeLc(denied, true, "android_standalone");
        double t = Run(lcD, 0, 1); denied.pending((int)T("EyePermissionOutcome").GetField("Denied").GetRawConstantValue()); t = Run(lcD, t + 0.1, t + 3);
        var c = Canon(lcD, t);
        Assert.AreEqual("denied", c.perm); Assert.AreEqual("supported", c.cap, "denied permission does not make the capability unsupported"); Assert.IsFalse(c.active);

        var unsup = new World { supported = false }; var lcU = MakeLc(unsup, false, "windows_editor_openxr");
        t = Run(lcU, 0, 120); c = Canon(lcU, t);
        Assert.AreEqual("unsupported", c.cap); Assert.AreEqual("not_applicable", c.perm); Assert.IsFalse(c.active); Assert.IsFalse(c.runtime);

        var noxr = new World { xrReady = false }; var lcX = MakeLc(noxr, false, "windows_editor_openxr");
        t = Run(lcX, 0, 3); c = Canon(lcX, t);
        Assert.AreEqual("unknown", c.cap, "provider not initialised: capability not yet known"); Assert.IsFalse(c.runtime); Assert.IsFalse(c.active);

        var ok = new World(); var lcO = MakeLc(ok, false, "windows_editor_openxr"); t = Run(lcO, 0, 4);
        Assert.IsTrue(Canon(lcO, t).active);
        Call(lcO, "OnPause", t + 0.1);
        var paused = Canon(lcO, t + 0.1);
        Assert.AreEqual("supported", paused.cap); Assert.AreEqual("not_applicable", paused.perm);
        Call(lcO, "OnResume", t + 5); t = Run(lcO, t + 5.1, t + 9);
        Assert.IsTrue(Canon(lcO, t).active, "resumed and verified again");
    }
}

// ================================================================================== integration (production components)

public partial class NpcLifecycleTests
{
    static void AssertManifestAliasesMatchCanonical(JObject m)
    {
        var et = m["eye_tracking"];
        string cap = (string)et["capability"], perm = (string)et["permission"];
        Assert.AreEqual(cap == "supported", (bool)m["eye_tracking_supported"], "alias eye_tracking_supported == (capability == supported)");
        Assert.AreEqual((bool)et["runtime_available"], (bool)m["eye_tracking_enabled"], "alias eye_tracking_enabled == runtime_available");
        Assert.AreEqual(perm == "granted" || perm == "not_applicable", (bool)m["eye_tracking_permission_granted"], "alias eye_tracking_permission_granted == the permission requirement is satisfied");
        Assert.AreEqual((string)et["tracking_enabled_state"], (string)m["eye_tracking_enabled_state"]);
        string expectedSupported = cap == "supported" ? "true" : (cap == "unsupported" ? "false" : "unknown");
        Assert.AreEqual(expectedSupported, (string)m["eye_tracking_supported_state"]);
        string expectedPerm = perm == "granted" ? "true" : (perm == "denied" || perm == "denied_permanently" ? "false" : (perm == "not_applicable" ? "not_applicable" : "unknown"));
        Assert.AreEqual(expectedPerm, (string)m["eye_tracking_permission_state"]);
        Assert.AreEqual((double)et["queried_t_abs"], (double)m["eye_tracking_queried_t_abs"], 1e-6);
    }

    JObject ManifestOf(Rig rig, string reason = "test") { Call(rig.dcm, "WriteManifestEnd", reason); return JObject.Parse(File.ReadAllText(rig.File("session_manifest.json"))); }

    // ====================================================================== G2 / G3 through the real GazeRecorder + DataCaptureManager

    IEnumerator OpenTrialWithGaze(RigOptions o, Action<Rig, Component, Transform> done)
    {
        Rig rig = null;
        yield return BuildRig(o, r => rig = r);
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(50f, 50f, 0.2f);
        var rec = MakeGaze(rig, head, 40f);
        yield return null;
        Call(rig.em, "BeginTrial");
        done(rig, rec, head);
    }

    [UnityTest]
    public IEnumerator G2_GazeRows_CarryTheRawProviderConfidence_BlankWhenMissing_AndNeverTheThreshold()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "minConfidenceForValid", 0.77f);   // a threshold that must never show up as an observation
        float[] seq = { 0.31f, 0.0f, 0.93f, 0.81f, float.NaN };
        int step = 0;
        Set(rec, "testLeftEye", Inj(seq[0], head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        for (; step < seq.Length; step++)
        {
            Set(rec, "testLeftEye", Inj(seq[step], head.position, Vector3.forward));
            yield return new WaitForSecondsRealtime(0.12f);
        }
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        AssertWidths(gaze, "gaze.csv");
        var left = new List<string>(); var combined = new List<string>(); var lv = new List<string>();
        foreach (var row in gaze.Rows) { left.Add(gaze.Get(row, "left_confidence")); combined.Add(gaze.Get(row, "combined_confidence")); lv.Add(gaze.Get(row, "left_valid")); }
        CollectionAssert.Contains(left, "0.310", "a varying provider confidence is preserved (below the threshold it is still the measurement)");
        CollectionAssert.Contains(left, "0.000", "a genuine zero is not replaced by a default");
        CollectionAssert.Contains(left, "0.930"); CollectionAssert.Contains(left, "0.810");
        CollectionAssert.Contains(left, "", "a missing (NaN) provider value is blank");
        Assert.IsFalse(left.Contains("0.770") || combined.Contains("0.770"), "the project threshold (0.77) is never written as an observation");
        Assert.IsFalse(left.Contains("0.500"), "no hard-coded 0.5 is written");
        Assert.Greater(left.Distinct().Count(), 3, "REGRESSION GUARD: the three confidence columns do not silently collapse to one constant");
        Assert.IsTrue(left.Zip(lv, (c, v) => (c, v)).Any(p => p.c == "0.310" && p.v == "0"), "0.31 < 0.77: measured and rejected by the threshold");
        Assert.IsTrue(left.Zip(lv, (c, v) => (c, v)).Any(p => p.c == "0.930" && p.v == "1"));
        // combined = mean of the contributing valid eyes' raw values, only when both have one: (0.93 + 0.90) / 2 = 0.915
        Assert.IsTrue(combined.Contains("0.915") || combined.Contains("0.920") || combined.Contains("0.910"), "combined follows the documented derivation: " + string.Join(",", combined.Distinct()));
        // manifest: the behaviour of the stream and the role of the threshold are separate facts
        var m = ManifestOf(rig);
        var conf = m["eye_tracking"]["confidence"];
        Assert.AreEqual(0.77, (double)conf["project_acceptance_threshold"], 1e-6);
        Assert.AreEqual("insufficient_samples", (string)conf["semantics"], "a handful of rows cannot call the stream constant");
        Assert.Greater((long)conf["rows_confidence_unavailable_left"], 0);
    }

    [UnityTest]
    public IEnumerator G3_NoFaceTrackingConfigured_ReportsNotConfigured_RecordsNoBlink_AndBlinkColumnsStayBlank()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        var f = Activator.CreateInstance(T("BlinkFacts")); Set(f, "OvrPluginInitialized", true); Set(f, "FaceTrackingSupported", true); Set(f, "PermissionGranted", true);   // runtime supports it, nothing configured
        SetConstDelegate(rec, "testBlinkFacts", f); Set(rec, "_nextBlinkCapabilityT", 0.0);   // sampling already started at BeginTrial: drop the cached real-runtime capability so the injected facts are read
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.6f);
        Call(rec, "StopRecording");
        var gaze = Csv.Load(rig.File("gaze.csv"));
        Assert.Greater(gaze.Rows.Count, 3, "the recorder produced rows");
        foreach (var row in gaze.Rows)
        {
            Assert.AreEqual("", gaze.Get(row, "eyes_closed_left")); Assert.AreEqual("", gaze.Get(row, "eyes_closed_right"));
            Assert.AreEqual("", gaze.Get(row, "blink_state"), "blank = not measured; never 'eyes open'");
        }
        Assert.AreEqual(0, Rows(Events(rig), "event_type", "blink_start").Count); Assert.AreEqual(0, Rows(Events(rig), "event_type", "blink_end").Count);
        var m = ManifestOf(rig);
        var bm = m["eye_tracking"]["blink"];
        Assert.AreEqual("not_configured", (string)bm["status"]); StringAssert.Contains("OVRFaceExpressions", (string)bm["reason"]);
        Assert.IsFalse((bool)bm["eye_gaze_api_exposes_openness"]);
        Assert.IsFalse((bool)bm["absence_of_blink_events_is_evidence_of_no_blinks"], "no blink events here are NOT evidence that the participant did not blink");
        Assert.AreEqual(0L, (long)bm["blinks_detected"]);
    }

    // Sets a Func<T> test-seam field to a delegate that returns the given constant (T may be a production struct reached by reflection).
    static void SetConstDelegate(Component target, string field, object value)
    {
        var fi = target.GetType().GetField(field);
        var body = System.Linq.Expressions.Expression.Constant(value, value.GetType());
        fi.SetValue(target, System.Linq.Expressions.Expression.Lambda(fi.FieldType, body).Compile());
    }

    [UnityTest]
    public IEnumerator G3_WithAnAvailableClosureSignal_BlinkEventsAreRecorded_AtTheirOwnTimes_NeverFromGazeLoss()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        var f = Activator.CreateInstance(T("BlinkFacts"));
        foreach (var n in new[] { "OvrPluginInitialized", "FaceTrackingSupported", "FaceTrackingEnabled", "ComponentPresent", "PermissionGranted", "SignalValid" }) Set(f, n, true);
        SetConstDelegate(rec, "testBlinkFacts", f); Set(rec, "_nextBlinkCapabilityT", 0.0);   // sampling already started at BeginTrial: drop the cached real-runtime capability so the injected facts are read
        float t0 = Time.realtimeSinceStartup;
        var closureType = rec.GetType().GetField("testEyeClosure").FieldType;   // Func<(float?, float?)>
        var holder = new ClosureHolder { t0 = t0 };
        rec.GetType().GetField("testEyeClosure").SetValue(rec, Delegate.CreateDelegate(closureType, holder, typeof(ClosureHolder).GetMethod("Get")));
        // gaze validity DROPS during the closure window on purpose: the blink must come from the closure signal alone
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(1.4f);
        Call(rec, "StopRecording");
        var evs = Events(rig);
        var starts = Rows(evs, "event_type", "blink_start"); var ends = Rows(evs, "event_type", "blink_end");
        Assert.AreEqual(1, starts.Count, "one blink start"); Assert.AreEqual(1, ends.Count, "one blink end (no duplicates)");
        double dur = D(evs.Get(ends[0], "value_num"));
        Assert.AreEqual(0.4, dur, 0.15, "duration from the closure samples (resolution = the gaze sampling interval)");
        Assert.AreEqual(D(evs.Get(ends[0], "t_abs")) - D(evs.Get(starts[0], "t_abs")), dur, 1e-3);
        var gaze = Csv.Load(rig.File("gaze.csv"));
        bool sawBlinkState = gaze.Rows.Any(r => gaze.Get(r, "blink_state") == "1"), sawOpen = gaze.Rows.Any(r => gaze.Get(r, "blink_state") == "0");
        Assert.IsTrue(sawBlinkState && sawOpen); Assert.IsTrue(gaze.Rows.Any(r => gaze.Get(r, "eyes_closed_left") != ""), "raw closure weights are recorded");
        var m = ManifestOf(rig);
        Assert.AreEqual("available", (string)m["eye_tracking"]["blink"]["status"]); Assert.AreEqual(1L, (long)m["eye_tracking"]["blink"]["blinks_detected"]);
        Assert.IsTrue((bool)m["eye_tracking"]["blink"]["absence_of_blink_events_is_evidence_of_no_blinks"]);
    }

    public sealed class ClosureHolder
    {
        public float t0;
        public (float? left, float? right) Get()
        {
            float t = Time.realtimeSinceStartup - t0;
            bool closed = t >= 0.5f && t < 0.9f;   // a 0.4 s closure in the middle of the recording
            float v = closed ? 0.95f : 0.05f;
            return (v, v);
        }
    }

    // ====================================================================== M1 on the real manifest

    [UnityTest]
    public IEnumerator M1_Manifest_LegacyEyeTrackingFields_AreAliasesOfTheCanonicalBlock_AtEveryCheckpoint()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, (r, c, h) => { rig = r; rec = c; head = h; });
        yield return new WaitForSecondsRealtime(0.3f);
        var m1 = ManifestOf(rig, "checkpoint_1");
        AssertManifestAliasesMatchCanonical(m1);
        foreach (var k in new[] { "capability", "permission", "configured", "runtime_available", "tracking_active", "last_state_change_t_abs", "provider", "sdk_version" })
            Assert.IsNotNull(m1["eye_tracking"][k], "canonical field " + k);
        // the state changes at runtime (the lifecycle controller disables itself without components, or is moved by the test) - the aliases must follow, never retain an earlier value
        var lc = Get<object>(Get<object>(rec, "lifecycleController"), "Lifecycle");
        Call(lc, "Shutdown", CaptureClockNow());
        var m2 = ManifestOf(rig, "checkpoint_2");
        AssertManifestAliasesMatchCanonical(m2);
        Assert.IsFalse((bool)m2["eye_tracking"]["tracking_active"], "a shut-down lifecycle is not active");
    }

    static double CaptureClockNow() => GetStatic<double>(T("CaptureClock"), "TAbs");

    // ====================================================================== M2 session lifecycle

    string[] EventTypes(Rig rig) { var e = Events(rig); return e.Rows.Select(r => e.Get(r, "event_type")).ToArray(); }

    static void WriteEvt(Component dcm, string type, double t)
    {
        var m = dcm.GetType().GetMethod("WriteEventRow", All);
        m.Invoke(dcm, new object[] { type, "Part3", "d1_search", "", "", "", "", "", (double?)1.0, "", Vector3.zero, Vector3.zero, t, 1700000000000L, "" });
    }

    [UnityTest]
    public IEnumerator M2_SessionEnd_IsTheFinalEventsRow_ExactlyOnce_AndLateCallbacksAreRefused_EverywhereAndRepeatedly()
    {
        Rig rig = null; Component rec = null; Transform head = null;
        yield return OpenTrialWithGaze(new RigOptions { prewarm = 2, maxAgents = 2, npcCaptureEnabled = true, npcSpeed = 1f }, (r, c, h) => { rig = r; rec = c; head = h; });
        Set(rec, "testLeftEye", Inj(0.9f, head.position, Vector3.forward)); Set(rec, "testRightEye", Inj(0.9f, head.position, Vector3.forward));
        Call(rec, "StartRecording");
        yield return WaitUntil(() => Live(rig) >= 1, 10f, "an NPC");
        yield return new WaitForSecondsRealtime(0.4f);

        // a normal event before the end is written
        Call(rig.dcm, "OnApplicationFocus", true);
        // an NPC record accepted just before the end, still queued (not yet flushed to npc_events.csv)
        double tq = CaptureClockNow();
        Call(rig.dcm, "AppendNpcEventLine", "npc_spawn", tq, 1700000000000L, "NPC-QUEUED", "NPC-QUEUED-S1", 1, Vector3.zero, 0f, false, Vector3.zero, "", "", "queued_before_end", -1, "");
        Assert.AreEqual("Open", Get<object>(rig.dcm, "CaptureState").ToString());

        Call(rig.dcm, "OnSessionEnded");      // the End panel confirmed
        Assert.AreEqual("Closed", Get<object>(rig.dcm, "CaptureState").ToString());
        var types = EventTypes(rig);
        Assert.AreEqual("session_end", types[types.Length - 1], "session_end is the final events.csv row");
        Assert.AreEqual(1, types.Count(t => t == "session_end"));
        CollectionAssert.Contains(types, "app_focus", "an event accepted before the end is there");
        var nev1 = NpcEvents(rig);
        Assert.IsTrue(nev1.Rows.Any(r => nev1.Get(r, "npc_id") == "NPC-QUEUED"), "a record queued before termination was flushed, not lost");

        // snapshot every session stream, then hammer every late entry point
        var files = new[] { "events.csv", "gaze.csv", "trajectory.csv", "npc_state.csv", "npc_events.csv", "npc_lifetimes.csv", "npc_profiles.csv" };
        var before = files.ToDictionary(f => f, f => File.Exists(rig.File(f)) ? File.ReadAllBytes(rig.File(f)).Length : -1);
        string manifestBefore = File.ReadAllText(rig.File("session_manifest.json"));

        Call(rig.dcm, "OnApplicationFocus", true);                    // the previously observed regression: app_focus after session_end
        Call(rig.dcm, "OnApplicationFocus", false);
        Call(rig.dcm, "OnApplicationPause", true);
        Call(rig.dcm, "OnApplicationPause", false);
        Call(rig.dcm, "OnSessionEnded");                              // duplicate termination requests
        Call(rig.dcm, "OnApplicationQuit");
        Call(rig.em, "RaiseEvt", "panel_shown", "Part3", "late", "", null, false, null, Vector3.zero, Vector3.zero);   // a delayed experiment callback
        WriteEvt(rig.dcm, "teleport", CaptureClockNow());
        Call(rig.dcm, "RecordBlinkEvent", "blink_start", CaptureClockNow(), (double?)null, "");
        Call(rig.dcm, "AppendNpcEventLine", "npc_spawn", CaptureClockNow(), 1700000000000L, "NPC-LATE", "NPC-LATE-S1", 1, Vector3.zero, 0f, false, Vector3.zero, "", "", "late", -1, "");
        Call(rig.dcm, "FlushNpcEvents");
        Call(rec, "StartRecording");                                  // a late gaze start is refused
        yield return new WaitForSecondsRealtime(0.5f);                // delayed coroutines / samplers get frames to misbehave
        Assert.IsFalse(Get<bool>(rec, "IsSampling"));
        Assert.Greater(Get<long>(rec, "LateRowsRejected"), 0L);

        types = EventTypes(rig);
        Assert.AreEqual("session_end", types[types.Length - 1], "still the final row after every late callback");
        Assert.AreEqual(1, types.Count(t => t == "session_end"), "exactly one terminal event");
        foreach (var f in files) Assert.AreEqual(before[f], File.Exists(rig.File(f)) ? File.ReadAllBytes(rig.File(f)).Length : -1, f + " accepted nothing after the session closed");
        Assert.AreEqual(manifestBefore, File.ReadAllText(rig.File("session_manifest.json")), "the end snapshot is not rewritten after the close");
        Assert.Greater(Get<long>(rig.dcm, "LateEventsRejected"), 3L); Assert.Greater(Get<long>(rig.dcm, "LateNpcRecordsRejected"), 0L);
        foreach (var w in new[] { "_events", "_traj", "_npcState", "_npcEvents", "_npcLifetimes", "_npcProfiles" })
            Assert.IsTrue(Get<bool>(Get<object>(rig.dcm, w), "IsClosed"), w + " is closed: no writer remains open for the finished session");
    }

    [UnityTest]
    public IEnumerator M2_ConcurrentSubmissionDuringFinalization_CannotWriteAfterSessionEnd()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcCaptureEnabled = false }, r => rig = r);
        yield return null;
        var dcm = rig.dcm; long submitted = 0; bool stop = false;
        var threads = new List<Thread>();
        for (int k = 0; k < 3; k++)
        {
            var th = new Thread(() => { double t = 5000; while (!Volatile.Read(ref stop)) { try { WriteEvt(dcm, "worker_event", t += 0.001); Interlocked.Increment(ref submitted); } catch { } } });
            th.IsBackground = true; th.Start(); threads.Add(th);
        }
        yield return new WaitForSecondsRealtime(0.15f);
        Call(dcm, "OnSessionEnded");                       // finalization on the main thread while the workers keep submitting
        yield return new WaitForSecondsRealtime(0.15f);
        Volatile.Write(ref stop, true);
        foreach (var th in threads) th.Join(2000);
        var types = EventTypes(rig);
        Assert.Greater(types.Count(t => t == "worker_event"), 0, "events accepted before the close were written");
        Assert.AreEqual("session_end", types[types.Length - 1], "no concurrently submitted row landed after session_end");
        Assert.AreEqual(1, types.Count(t => t == "session_end"));
        Assert.Greater(Get<long>(dcm, "LateEventsRejected"), 0L, "the workers kept submitting after the close and were refused");
    }

    // ====================================================================== N2 destination semantics

    List<List<string>> DestRows(Rig rig, string lifetimeId)
    {
        var nev = NpcEvents(rig);
        return nev.Rows.Where(r => nev.Get(r, "event_type") == "npc_dest_change" && nev.Get(r, "npc_spawn_instance_id") == lifetimeId).ToList();
    }

    static string Cause(string v) { foreach (var kv in v.Split(';')) { int i = kv.IndexOf('='); if (i > 0 && kv.Substring(0, i) == "cause") return kv.Substring(i + 1); } return ""; }

    [UnityTest]
    public IEnumerator N2_FirstDestination_IsAnInitialAssignment_LaterChangesAreChanges_SameDestinationIsNoEvent_PoolReuseResetsHistory()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { part2 = true, prewarm = 1, maxAgents = 1, npcSpeed = 0.05f, npcCaptureEnabled = true }, r => rig = r);   // Part 2 (wandering) - an NPC too slow to reach anything
        yield return WaitUntil(() => Live(rig) == 1, 10f, "an NPC");
        var pa = ActiveAgents(rig)[0]; string life1 = Lid(pa); string agentId = Aid(pa);
        yield return WaitUntil(() => DestRows(rig, life1).Count >= 1, 10f, "the first destination row");
        var nev = NpcEvents(rig);
        var first = DestRows(rig, life1)[0]; string v = nev.Get(first, "value_str");
        Assert.AreEqual("initial_assignment", Cause(v), v); StringAssert.Contains("prev_dest=none", v); StringAssert.DoesNotContain("jump_m", v, "no previous destination, no jump");
        Assert.AreEqual(1, DestRows(rig, life1).Count, "the initial assignment is a single event");
        Assert.AreEqual(0, DestRows(rig, life1).Count(r => Cause(nev.Get(r, "value_str")) == "other"), "the first assignment is not labelled other");

        // the same destination again (re-issued): no event
        var nav = pa.GetComponent<NavMeshAgent>();
        Vector3 d0 = nav.destination;
        nav.SetDestination(d0); yield return new WaitForSecondsRealtime(0.4f);
        nav.SetDestination(d0 + new Vector3(0.1f, 0f, 0f)); yield return new WaitForSecondsRealtime(0.4f);
        Assert.AreEqual(1, DestRows(rig, life1).Count, "a repeated / sub-0.5 m assignment is not a destination change");

        // a genuine change whose reason cannot be identified keeps an explicit 'other' (never relabelled)
        MoveType.GetMethod("MoveTo", new[] { typeof(Vector3) }).Invoke(pa.GetComponent(MoveType), new object[] { d0 + new Vector3(12f, 0f, 9f) });   // the mover's own entry point, so nothing re-issues the old target
        yield return new WaitForSecondsRealtime(0.5f);
        nev = NpcEvents(rig); var rows = DestRows(rig, life1);
        Assert.AreEqual(2, rows.Count, "initial assignment + one genuine change");
        string v2 = nev.Get(rows[1], "value_str");
        Assert.AreEqual("other", Cause(v2), v2); StringAssert.Contains("jump_m=", v2); StringAssert.Contains("prev_dest=", v2); StringAssert.DoesNotContain("prev_dest=none", v2);

        // pooled object reused: a NEW lifetime id and a fresh destination history
        Call(pa, "ForceFinish");
        yield return WaitUntil(() => ActiveAgents(rig).Exists(a => Aid(a) == agentId && Lid(a) != life1 && Lid(a) != ""), 10f, "the pooled object to start a new lifetime");
        var pa2 = ActiveAgents(rig).Find(a => Aid(a) == agentId); string life2 = Lid(pa2);
        Assert.AreNotEqual(life1, life2);
        yield return WaitUntil(() => DestRows(rig, life2).Count >= 1, 10f, "the second lifetime's first destination row");
        nev = NpcEvents(rig); var r2 = DestRows(rig, life2);
        Assert.AreEqual("initial_assignment", Cause(nev.Get(r2[0], "value_str")), "history was reset: the new lifetime's first destination is again an initial assignment");
        Assert.AreEqual(1, r2.Count);
        Assert.AreEqual(2, DestRows(rig, life1).Count, "the previous lifetime's rows are untouched");
    }
}
