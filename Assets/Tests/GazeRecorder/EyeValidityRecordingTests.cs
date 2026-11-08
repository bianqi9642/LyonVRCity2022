// EyeValidityRecordingTests.cs
// Tests for the eye-tracking validity recording fixes made after the S3 audit (session 20261106T145652Z_0980aa930c32):
//   (1) EyeTrackingLifecycle: a provider that becomes enabled AFTER the bounded start retries were exhausted is reported as Running ("late_start_success") instead of staying start_failed; the earlier failures stay
//       counted / recorded; a component that is still disabled, or a provider that is still disabled, stays start_failed.
//   (2) GazeRecorder: per-row provider validity comes from the same-row raw provider read (ReadRawGaze, testRawGaze seam) when the lifecycle probe is not available, so the left/right_provider_valid columns and the
//       validity of an eye no longer depend on the lifecycle having reached Running; an unknown provider stays BLANK (never 0 / 1).
//   (3) GazeConfidence.ProviderEstablished: an unverified real-provider sample is not accepted as an eye sample.
// Everything runs in the Unity Editor without a headset (scripted fakes); NONE of this is Quest validation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class EyeValidityLifecycleTests
{
    sealed class W { public bool xrReady = true, supported = true, trackingEnabled, enableOk = true, makesTrackingEnabled; public bool[] enabled = { false, false }; public int enableCalls; }

    static object Make(W w, Action<object> opt = null)
    {
        var p = Activator.CreateInstance(T("EyeTrackingPlatform"));
        Set(p, "RuntimePath", "windows_editor_openxr"); Set(p, "PermissionApplicable", false);
        Set(p, "XrReady", (Func<bool>)(() => w.xrReady)); Set(p, "Supported", (Func<bool>)(() => w.supported)); Set(p, "TrackingEnabled", (Func<bool>)(() => w.trackingEnabled)); Set(p, "PermissionGranted", (Func<bool>)(() => true)); Set(p, "WorldFrameReady", (Func<bool>)(() => true));
        Set(p, "ComponentPresent", (Func<int, bool>)(i => true)); Set(p, "ComponentEnabled", (Func<int, bool>)(i => w.enabled[i]));
        Set(p, "SetComponentEnabled", (Action<int, bool>)((i, on) => { if (!on) { w.enabled[i] = false; return; } w.enableCalls++; w.enabled[i] = w.enableOk; if (w.enableOk && w.makesTrackingEnabled) w.trackingEnabled = true; }));
        double ts = 1; Set(p, "ReadProvider", (Func<int>)(() => 7)); Set(p, "ProviderTime", (Func<double>)(() => ts += 0.01));
        var o = Activator.CreateInstance(T("EyeTrackingLifecycleOptions")); opt?.Invoke(o);
        return Activator.CreateInstance(T("EyeTrackingLifecycle"), p, o);
    }
    static string State(object lc) => Get<object>(lc, "State").ToString();
    static void Run(object lc, double t0, double t1) { for (double t = t0; t <= t1 + 1e-9; t += 0.1) Call(lc, "Tick", t); }
    static List<string> Reasons(object lc, string ev) { var l = new List<string>(); foreach (var t in (System.Collections.IEnumerable)Get<object>(lc, "Transitions")) if ((string)t.GetType().GetField("event_name").GetValue(t) == ev) l.Add((string)t.GetType().GetField("reason").GetValue(t)); return l; }
    static List<string> Events(object lc) { var l = new List<string>(); foreach (var t in (System.Collections.IEnumerable)Get<object>(lc, "Transitions")) l.Add((string)t.GetType().GetField("event_name").GetValue(t)); return l; }

    [Test]
    public void InitSuccess_IsRunning_NoLateEvent()
    {
        var w = new W { makesTrackingEnabled = true }; var lc = Make(w); Run(lc, 0, 10);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, Get<int>(lc, "StartupSuccesses")); CollectionAssert.DoesNotContain(Events(lc), "late_start_success");
    }

    [Test]
    public void ProviderNotEnabled_RetriesAreBounded_ThenStaysStartFailed_AndTheFailureIsVisible()
    {
        var w = new W(); var lc = Make(w); Run(lc, 0, 400);
        Assert.AreEqual("StartFailed", State(lc)); Assert.AreEqual("initialization_failed", Get<string>(lc, "AcquisitionStatus")); Assert.AreEqual(8, Get<int>(lc, "StartupAttempts"), "bounded: maxStartAttempts");
        Assert.AreEqual(8, Get<int>(lc, "StartupFailures")); StringAssert.Contains("bounded_retries_exhausted", Get<string>(lc, "LastReason")); Assert.IsTrue(Reasons(lc, "start_failure").All(r => r == "tracking_not_enabled_after_start"), "every failure keeps its reason in the transition log"); Assert.AreEqual(w.enableCalls, 2, "components are enabled once (a healthy component is never toggled again), no further start requests after exhaustion");
    }

    [Test]
    public void ProviderEnabledLate_AfterRetriesExhausted_IsReportedRunning_AndFailuresRemainRecorded()
    {
        var w = new W(); var lc = Make(w); Run(lc, 0, 400); Assert.AreEqual("StartFailed", State(lc));
        int attemptsBefore = Get<int>(lc, "StartupAttempts"); w.trackingEnabled = true;   // the runtime enables eye tracking late (headset worn / focus)
        Run(lc, 400.1, 405);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual("running", Get<string>(lc, "AcquisitionStatus")); Assert.AreEqual("success", Get<string>(lc, "LastResult"));
        Assert.AreEqual(attemptsBefore, Get<int>(lc, "StartupAttempts"), "no extra start request: the late check only observes"); Assert.AreEqual(8, Get<int>(lc, "StartupFailures"), "the earlier failures stay counted"); Assert.AreEqual(1, Get<int>(lc, "StartupSuccesses"));
        var ev = Events(lc); CollectionAssert.Contains(ev, "start_failure"); CollectionAssert.Contains(ev, "late_start_success"); StringAssert.Contains("after_8_failures", Reasons(lc, "late_start_success").Single());
        Assert.IsTrue(Get<bool>(lc, "ProviderProbeAvailable"), "the provider probe starts answering once Running");
    }

    [Test]
    public void ComponentStillDisabled_IsNotALateStart_EvenIfTheProviderIsEnabled()
    {
        var w = new W { enableOk = false }; var lc = Make(w); Run(lc, 0, 400); w.trackingEnabled = true; Run(lc, 400.1, 410);
        Assert.AreEqual("StartFailed", State(lc)); Assert.IsTrue(Reasons(lc, "start_failure").All(r => r.StartsWith("component_self_disabled"))); CollectionAssert.DoesNotContain(Events(lc), "late_start_success");
    }

    [Test]
    public void LateCheckCanBeSwitchedOff()
    {
        var w = new W(); var lc = Make(w, o => Set(o, "exhaustedRecheckInterval", 0.0)); Run(lc, 0, 400); w.trackingEnabled = true; Run(lc, 400.1, 410);
        Assert.AreEqual("StartFailed", State(lc), "interval 0 = the previous behaviour");
    }

    [Test]
    public void ProviderValidityChangesDuringRunning_IsFollowedPerEye_AndLossIsReported()
    {
        var w = new W { makesTrackingEnabled = true }; int mask = 7; var lc = Make(w); Set(Get<object>(lc, "Platform"), "ReadProvider", (Func<int>)(() => mask)); double ts = 1; Set(Get<object>(lc, "Platform"), "ProviderTime", (Func<double>)(() => ts += 0.01));
        Run(lc, 0, 10); Assert.IsTrue(Get<bool>(lc, "LeftProviderValid") && Get<bool>(lc, "RightProviderValid"));
        mask = 1 | 2; Run(lc, 10.1, 11); Assert.IsTrue(Get<bool>(lc, "LeftProviderValid")); Assert.IsFalse(Get<bool>(lc, "RightProviderValid"));
        mask = 1; Run(lc, 11.1, 20); Assert.AreEqual("TrackingUnavailable", State(lc), "no valid eye for > 3 s");
        mask = 7; Run(lc, 20.1, 21); Assert.AreEqual("Running", State(lc));
    }

    [Test]
    public void ProviderUnavailable_QueryFailed_IsNotAValidFlag()
    {
        var w = new W { makesTrackingEnabled = true }; var lc = Make(w); Set(Get<object>(lc, "Platform"), "ReadProvider", (Func<int>)(() => 0)); Run(lc, 0, 10);
        Assert.IsFalse(Get<bool>(lc, "ProviderProbeAvailable")); Assert.IsFalse(Get<bool>(lc, "LeftProviderValid")); Assert.AreEqual(0L, Get<long>(lc, "ProviderSamples"));
    }

    [Test]
    public void GazeConfidence_ProviderEstablished_Rule()
    {
        var t = T("GazeConfidence");
        Assert.IsTrue((bool)CallStatic(t, "ProviderEstablished", true, true)); Assert.IsFalse((bool)CallStatic(t, "ProviderEstablished", true, false), "required but unknown = not established");
        Assert.IsTrue((bool)CallStatic(t, "ProviderEstablished", false, false), "validity not required: unchanged legacy behaviour");
    }
}

public partial class NpcLifecycleTests
{
    // Recorder level: the provider flags come from the same-row raw provider read while the lifecycle is NOT running (no OVREyeGaze components in this rig => NotApplicable).
    [UnityTest]
    public IEnumerator EyeValidity_ProviderFlagsComeFromTheSameRowRawRead_WhenTheLifecycleProbeIsUnavailable_AndFollowAChangeDuringTheSession()
    {
        var rec = MakeStandaloneGaze("rawvalid.csv", out Transform head); yield return null;
        var rawT = T("RawGazeState"); var eyeT = T("RawGazeEye");
        bool rightValid = true, available = true; double ts = 100;
        Func<object> make = () =>
        {
            object r = Activator.CreateInstance(rawT); ts += 0.01;
            if (!available) { Set(r, "available", false); return r; }
            object L = Activator.CreateInstance(eyeT), Rt = Activator.CreateInstance(eyeT);
            Set(L, "valid", true); Set(L, "confidence", 0.9f); Set(Rt, "valid", rightValid); Set(Rt, "confidence", 0.9f);
            Set(r, "available", true); Set(r, "time", ts); Set(r, "left", L); Set(r, "right", Rt); return r;
        };
        var del = Delegate.CreateDelegate(rec.GetType().GetField("testRawGaze", All).FieldType, new RawBox(make), typeof(RawBox).GetMethod("Get").MakeGenericMethod(rawT));
        rec.GetType().GetField("testRawGaze", All).SetValue(rec, del);
        string path = Get<string>(rec, "outputPathOverride");
        var eyeL = Inj(0.9f, new Vector3(0.5f, 1f, -5f), Vector3.forward); Set(eyeL, "applyProviderGate", true);
        var eyeR = Inj(0.9f, new Vector3(-0.5f, 1f, -5f), Vector3.forward); Set(eyeR, "applyProviderGate", true);
        Set(rec, "testLeftEye", eyeL); Set(rec, "testRightEye", eyeR);
        Call(rec, "StartRecording"); yield return new WaitForSecondsRealtime(0.4f);
        rightValid = false; yield return new WaitForSecondsRealtime(0.4f);
        available = false; yield return new WaitForSecondsRealtime(0.4f);
        Call(rec, "StopRecording");
        var csv = Csv.Load(path); Assert.Greater(csv.Rows.Count, 9);
        var withFlags = csv.Rows.Where(r => csv.Get(r, "left_provider_valid") != "").ToList(); var blank = csv.Rows.Where(r => csv.Get(r, "left_provider_valid") == "").ToList();
        Assert.Greater(withFlags.Count, 5, "the flags are written although the lifecycle is not running"); Assert.Greater(blank.Count, 3, "provider read unavailable: unknown stays blank, never a guessed 0/1");
        foreach (var r in withFlags) Assert.AreEqual("not_applicable", csv.Get(r, "eye_tracking_state"), "the lifecycle state is unchanged and stays visible");
        var both = withFlags.Where(r => csv.Get(r, "right_provider_valid") == "1").ToList(); var leftOnly = withFlags.Where(r => csv.Get(r, "right_provider_valid") == "0").ToList();
        Assert.Greater(both.Count, 2); Assert.Greater(leftOnly.Count, 2, "provider validity changed during the session");
        foreach (var r in both) { Assert.AreEqual("1", csv.Get(r, "left_provider_valid")); Assert.AreEqual("both", csv.Get(r, "gaze_ray_source")); }
        foreach (var r in leftOnly) { Assert.AreEqual("left", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("0", csv.Get(r, "right_valid")); Assert.AreEqual("", csv.Get(r, "right_dir_x")); Assert.AreEqual("", csv.Get(r, "right_confidence"), "no confidence for an eye the provider flagged invalid"); }
    }

    sealed class RawBox { readonly Func<object> _f; public RawBox(Func<object> f) { _f = f; } public T Get<T>() => (T)_f(); }
}
