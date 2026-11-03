// EyeTrackingTests.cs
// PlayMode/EditMode-style tests for schema_version 9: the eye-tracking ACQUISITION lifecycle (EyeTrackingLifecycle state machine + permission manager), writer-health vs
// acquisition-health reporting, provider-gated validity, the deadline-grid sampling scheduler and the rate diagnosis. The production classes are driven through reflection with
// scripted fakes (delegates) - there is no Quest in the test run, so these are lifecycle/logic tests, NOT a hardware verification.
// Synthetic data is written only below the per-test temporary folder, never into a real output directory.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Reflect;

public class EyeTrackingLifecycleTests
{
    // A scripted world standing in for OVRPlugin / the Android permission system / the two OVREyeGaze components.
    sealed class World
    {
        public bool xrReady, supported = true, trackingEnabled, permissionGranted, worldFrame = true;
        public bool[] present = { true, true };
        public bool[] enabled = { false, false };
        public bool[] startOk = { true, true };      // false = OVREyeGaze.OnEnable fails and the component disables itself again
        public int enableCalls;                       // SetComponentEnabled(i, true) calls = OVREyeGaze.OnEnable runs = StartEyeTracking calls
        public int permissionRequests;
        public Action<int> pendingCallback;
        public int mask = 1 | 2 | 4;                  // EyeProviderBits: a live provider with both eyes valid by default
        public double providerTime = 1;
        public bool autoAdvance = true;               // every provider query returns a NEW sample (timestamp advances) unless a test pins it
    }

    static object Make(World w, bool androidPermission, string path, out object platform, Action<object> options = null)
    {
        platform = Activator.CreateInstance(T("EyeTrackingPlatform"));
        Set(platform, "RuntimePath", path);
        Set(platform, "PermissionApplicable", androidPermission);
        Set(platform, "XrReady", (Func<bool>)(() => w.xrReady));
        Set(platform, "Supported", (Func<bool>)(() => w.supported));
        Set(platform, "TrackingEnabled", (Func<bool>)(() => w.trackingEnabled));
        Set(platform, "PermissionGranted", (Func<bool>)(() => w.permissionGranted));
        Set(platform, "WorldFrameReady", (Func<bool>)(() => w.worldFrame));
        Set(platform, "RequestPermission", (Action<Action<int>>)(cb => { w.permissionRequests++; w.pendingCallback = cb; }));
        Set(platform, "ComponentPresent", (Func<int, bool>)(i => w.present[i]));
        Set(platform, "ComponentEnabled", (Func<int, bool>)(i => w.enabled[i]));
        Set(platform, "SetComponentEnabled", (Action<int, bool>)((i, on) =>
        {
            if (!on) { w.enabled[i] = false; return; }
            w.enableCalls++;
            bool ok = w.startOk[i] && (!androidPermission || w.permissionGranted);
            w.enabled[i] = ok;
            if (ok) w.trackingEnabled = true;
        }));
        Set(platform, "ReadProvider", (Func<int>)(() => { if (w.autoAdvance) w.providerTime += 0.01; return w.mask; }));
        Set(platform, "ProviderTime", (Func<double>)(() => w.providerTime));
        object opt = Activator.CreateInstance(T("EyeTrackingLifecycleOptions"));
        options?.Invoke(opt);
        return Activator.CreateInstance(T("EyeTrackingLifecycle"), platform, opt);
    }

    static string State(object lc) => Get<object>(lc, "State").ToString();
    static string Perm(object lc) => Get<object>(lc, "Permission").ToString();
    static string Cap(object lc) => Get<object>(lc, "Capability").ToString();
    static string Status(object lc) => Get<string>(lc, "AcquisitionStatus");
    static string Reason(object lc) => Get<string>(lc, "LastReason");
    static int Int(object lc, string n) => Get<int>(lc, n);
    static void Tick(object lc, double t) => Call(lc, "Tick", t);

    // Ticks once per 0.1 s from t0 to t1 (inclusive); returns the last time.
    static double Run(object lc, double t0, double t1) { double t = t0; for (; t <= t1 + 1e-9; t += 0.1) Tick(lc, t); return t - 0.1; }

    const int Both = 1 | 2 | 4;   // EyeProviderBits.QueryOk | LeftValid | RightValid

    // ============================================================ the two execution paths

    [Test]
    public void EditorPath_XrNotInitialised_WaitsWithAnExplicitReason_AndNeverTouchesTheComponents()
    {
        var w = new World();
        var lc = Make(w, false, "windows_editor_openxr", out _);
        Run(lc, 0, 5);
        Assert.AreEqual("WaitingForXR", State(lc)); Assert.AreEqual("initializing", Status(lc));
        Assert.AreEqual("xr_not_initialized", Reason(lc));
        Assert.AreEqual(0, w.enableCalls, "no start attempt before XR is ready");
        Assert.AreEqual(0, w.permissionRequests);
        Assert.AreEqual("NotApplicable", Perm(lc), "no Android permission exists on the PC/Link path");

        w.xrReady = true;
        Run(lc, 5.1, 9);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual("running", Status(lc));
        Assert.AreEqual("Supported", Cap(lc)); Assert.AreEqual("NotApplicable", Perm(lc));
        Assert.AreEqual(0, w.permissionRequests, "the permission dialog is never requested off-device");
        Assert.AreEqual(1, Int(lc, "StartupSuccesses")); Assert.AreEqual(0, Int(lc, "StartupFailures"));
    }

    [Test]
    public void EditorPath_UnsupportedExtension_IsReportedAsUnsupportedOnThatRuntime_NotAsAQuestVerdict()
    {
        var w = new World { xrReady = true, supported = false };
        var lc = Make(w, false, "windows_editor_openxr", out _);
        Run(lc, 0, 120);
        Assert.AreEqual("Unsupported", State(lc)); Assert.AreEqual("unsupported", Status(lc)); Assert.AreEqual("Unsupported", Cap(lc));
        StringAssert.Contains("windows_editor_openxr", Reason(lc), "the reason names the runtime path so an Editor result cannot be mistaken for a standalone result");
        Assert.AreEqual(6, Int(lc, "CapabilityChecks"), "bounded: capabilityMaxChecks, then stop");
        Assert.AreEqual(0, w.enableCalls); Assert.AreEqual(0, w.permissionRequests);
        Assert.AreEqual("NotApplicable", Perm(lc));
    }

    [Test]
    public void CapabilityThatAppearsLate_IsPickedUp_BeforeTheVerdictIsUnsupported()
    {
        var w = new World { xrReady = true, supported = false };
        var lc = Make(w, false, "android_standalone", out _);
        Run(lc, 0, 4);
        Assert.AreEqual("CheckingCapability", State(lc)); Assert.AreEqual("capability_not_yet_reported", Reason(lc));
        w.supported = true;
        Run(lc, 4.1, 12);
        Assert.AreEqual("Running", State(lc));
    }

    [Test]
    public void NoEyeGazeComponents_IsNotApplicable()
    {
        var w = new World { xrReady = true }; w.present[0] = w.present[1] = false;
        var lc = Make(w, false, "android_standalone", out _);
        Run(lc, 0, 2);
        Assert.AreEqual("NotApplicable", State(lc)); Assert.AreEqual("not_applicable", Status(lc)); Assert.AreEqual(0, w.enableCalls);
    }

    [Test]
    public void DisabledByConfiguration_ReportsNotEnabled()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, false, "android_standalone", out _);
        Call(lc, "DisableByConfiguration", 0.0);
        Tick(lc, 0.1);
        Assert.AreEqual("not_enabled", Status(lc)); Assert.AreEqual(0, w.enableCalls);
    }

    // ============================================================ permission lifecycle (Android Standalone path)

    [Test]
    public void Android_PermissionMissing_IsRequestedExactlyOnce_AndWaitedFor_ThenGrantedStartsTracking()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 6);
        Assert.AreEqual("WaitingForPermission", State(lc)); Assert.AreEqual("permission_required", Status(lc));
        Assert.AreEqual(1, w.permissionRequests); Assert.AreEqual("Requested", Perm(lc));
        Assert.AreEqual(0, w.enableCalls, "OVREyeGaze is not enabled (and so cannot fail) before the permission exists");

        w.permissionGranted = true; w.pendingCallback(0);   // user taps Allow
        Run(lc, 6.1, 9);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual("Granted", Perm(lc));
        Assert.AreEqual(1, w.permissionRequests, "still exactly one request");
        Assert.AreEqual(2, w.enableCalls, "one start per eye, no duplicates");
    }

    [Test]
    public void Android_PermissionAlreadyGranted_NeverShowsADialog()
    {
        var w = new World { xrReady = true, permissionGranted = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 4);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(0, w.permissionRequests); Assert.AreEqual("Granted", Perm(lc));
    }

    [Test]
    public void Android_PermissionDenied_DoesNotLoopTheDialog_AndRecoversWhenGrantedInSettings()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 3);
        w.pendingCallback(1);   // denied
        double t = Run(lc, 3.1, 60);
        Assert.AreEqual("PermissionDenied", State(lc)); Assert.AreEqual("permission_denied", Status(lc)); Assert.AreEqual("Denied", Perm(lc));
        Assert.AreEqual(1, w.permissionRequests, "a denial never re-requests by itself");
        Assert.AreEqual(0, w.enableCalls);

        Call(lc, "OnPause", t); Call(lc, "OnResume", t + 5);
        Run(lc, t + 5.1, t + 10);
        Assert.AreEqual("PermissionDenied", State(lc), "resume alone does not re-prompt");
        Assert.AreEqual(1, w.permissionRequests);

        w.permissionGranted = true;   // the operator enabled it in the system settings
        Call(lc, "OnPause", t + 11); Call(lc, "OnResume", t + 20);
        Run(lc, t + 20.1, t + 24);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, w.permissionRequests);
    }

    [Test]
    public void Android_DeniedPermanently_NeverPromptsAgain_UntilAnExplicitOperatorRetry()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 3); w.pendingCallback(2);
        double t = Run(lc, 3.1, 8);
        Assert.AreEqual("PermissionDenied", State(lc)); StringAssert.Contains("dont_ask_again", Reason(lc));
        Call(lc, "RequestRetry", t, false);
        Run(lc, t + 0.1, t + 5);
        Assert.AreEqual("PermissionDenied", State(lc), "a plain retry never re-prompts a permanent denial"); Assert.AreEqual(1, w.permissionRequests);
        Call(lc, "RequestRetry", t + 6, true);
        Run(lc, t + 6.1, t + 10);
        Assert.AreEqual(2, w.permissionRequests, "an explicit operator retry with allowPrompt may ask once more");
        Assert.AreEqual("WaitingForPermission", State(lc));
    }

    [Test]
    public void Android_PausedBehindTheSystemDialog_KeepsWaiting_WithoutASecondRequest()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 2);
        Call(lc, "OnPause", 2.0);   // the permission dialog pauses the app
        Tick(lc, 2.5); Tick(lc, 3.0);
        Assert.AreEqual("WaitingForPermission", State(lc));
        w.permissionGranted = true;   // the callback never arrives; the granted state itself is authoritative
        Call(lc, "OnResume", 8.0);
        Run(lc, 8.1, 11);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, w.permissionRequests);
    }

    [Test]
    public void Shutdown_WhileWaitingForPermission_IgnoresTheLateCallback_AndNeverStartsTracking()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        Run(lc, 0, 3);
        Call(lc, "Shutdown", 3.0);
        w.permissionGranted = true; w.pendingCallback(0);
        Run(lc, 3.1, 8);
        Assert.AreEqual("Stopped", State(lc)); Assert.AreEqual(0, w.enableCalls); Assert.AreEqual(1, Int(lc, "IgnoredLateCallbacks"));
    }

    // ============================================================ start failures, bounded recovery

    [Test]
    public void StartFailure_IsRetriedWithBackoff_AndRecovers_WithoutTogglingTheHealthyEye()
    {
        var w = new World { xrReady = true };
        w.startOk[1] = false;   // the right OVREyeGaze disables itself on every start until "fixed"
        var lc = Make(w, false, "android_standalone", out _);
        Run(lc, 0, 1.2);
        Assert.AreEqual("StartFailed", State(lc)); Assert.AreEqual("initialization_failed", Status(lc));
        StringAssert.Contains("component_self_disabled:right", Reason(lc));
        Assert.AreEqual(1, Int(lc, "StartupAttempts"), "no retry before the 1 s backoff elapsed");
        Run(lc, 1.3, 1.6);
        Assert.AreEqual(2, Int(lc, "StartupAttempts"), "retry after the first backoff");
        Assert.AreEqual(1 + 2, w.enableCalls, "left started once; only the failed right eye was re-enabled");
        Assert.AreEqual("StartFailed", State(lc));

        w.startOk[1] = true;
        Run(lc, 3.3, 12);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, Int(lc, "StartupSuccesses")); Assert.AreEqual(2, Int(lc, "StartupFailures"));
        Assert.AreEqual("success", Get<string>(lc, "LastResult"));
        int calls = w.enableCalls; Run(lc, 12.1, 60);
        Assert.AreEqual(calls, w.enableCalls, "a running lifecycle never re-enables or restarts anything (no per-frame re-enable)");
    }

    [Test]
    public void StartFailure_RetriesAreBounded_AndAResumeIsALegitimateNewEpisode()
    {
        var w = new World { xrReady = true }; w.startOk[0] = w.startOk[1] = false;
        var lc = Make(w, false, "android_standalone", out _);
        double t = Run(lc, 0, 400);
        Assert.AreEqual("StartFailed", State(lc));
        Assert.AreEqual(8, Int(lc, "StartupAttempts"), "maxStartAttempts then stop");
        int calls = w.enableCalls; t = Run(lc, t + 0.1, t + 200);
        Assert.AreEqual(calls, w.enableCalls, "exhausted: no further attempts however long we wait");
        Call(lc, "OnPause", t); Call(lc, "OnResume", t + 1);
        w.startOk[0] = w.startOk[1] = true;
        Run(lc, t + 1.1, t + 8);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, Int(lc, "ResumeRetries"));
    }

    [Test]
    public void ComponentThatSelfDisablesWhileRunning_IsDetectedAndRecovered_Boundedly()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, false, "android_standalone", out _);
        double t = Run(lc, 0, 3);
        Assert.AreEqual("Running", State(lc));
        w.enabled[0] = false;   // the SDK turned the left eye off
        t = Run(lc, t + 0.1, t + 1);
        Assert.AreEqual("StartFailed", State(lc)); StringAssert.Contains("component_disabled_while_running:left", Reason(lc));
        Run(lc, t + 0.1, t + 4);
        Assert.AreEqual("Running", State(lc)); Assert.AreEqual(1, Int(lc, "RecoveryEpisodes"));

        // flapping forever is bounded
        for (int i = 0; i < 12; i++) { w.enabled[0] = false; t = Run(lc, t + 0.1, t + 6); }
        Assert.Greater(Int(lc, "RecoveryEpisodes"), 5); Assert.AreEqual("StartFailed", State(lc), "recovery episodes are capped (maxRecoveryEpisodes)");
    }

    [Test]
    public void WorldFrameMissing_WaitsInStarting_WithoutCountingAnAttempt()
    {
        var w = new World { xrReady = true, worldFrame = false };
        var lc = Make(w, false, "android_standalone", out _);
        Run(lc, 0, 5);
        Assert.AreEqual("Starting", State(lc)); Assert.AreEqual("world_frame_unavailable", Reason(lc)); Assert.AreEqual(0, Int(lc, "StartupAttempts"));
        w.worldFrame = true; Run(lc, 5.1, 8);
        Assert.AreEqual("Running", State(lc));
    }

    // ============================================================ provider samples, tracking unavailable, pause/resume

    [Test]
    public void ProviderSamples_AreCountedOncePerNewSample_AndTrackingUnavailableIsDetectedAndLeft()
    {
        var w = new World { xrReady = true, autoAdvance = false, mask = 0 };
        var lc = Make(w, false, "android_standalone", out _);
        double t = Run(lc, 0, 3);
        Assert.AreEqual(0L, Get<long>(lc, "ProviderAnyValid"));

        w.mask = Both; w.providerTime = 100;
        for (int i = 0; i < 5; i++) { t += 0.1; Tick(lc, t); }   // the SAME provider sample polled five times
        Assert.AreEqual(1L, Get<long>(lc, "ProviderAnyValid"), "one provider sample = one count, however often it is polled");
        w.providerTime = 100.01; t += 0.1; Tick(lc, t);
        Assert.AreEqual(2L, Get<long>(lc, "ProviderAnyValid")); Assert.AreEqual(2L, Get<long>(lc, "ProviderLeftValid")); Assert.AreEqual(2L, Get<long>(lc, "ProviderRightValid"));
        Assert.IsTrue(Get<bool>(lc, "LeftProviderValid") && Get<bool>(lc, "RightProviderValid") && Get<bool>(lc, "ProviderProbeAvailable"));

        int firsts = 0; foreach (var tr in Get<System.Collections.IEnumerable>(lc, "Transitions")) if (Get<string>(tr, "event_name") == "first_valid_provider_sample") firsts++;
        Assert.AreEqual(1, firsts, "first_valid_provider_sample is recorded exactly once");

        w.mask = 1;   // query ok, no valid eye (eyes closed / tracking lost)
        for (int i = 0; i < 40; i++) { t += 0.1; w.providerTime += 0.1; Tick(lc, t); }
        Assert.AreEqual("TrackingUnavailable", State(lc)); Assert.AreEqual("tracking_unavailable", Status(lc));
        Assert.AreEqual(0, w.permissionRequests); Assert.AreEqual(2, w.enableCalls, "no restart for an unavailable-but-running tracker");

        w.mask = 1 | 2; w.providerTime += 0.1; t += 0.1; Tick(lc, t);
        Assert.AreEqual("Running", State(lc)); Assert.IsTrue(Get<bool>(lc, "LeftProviderValid")); Assert.IsFalse(Get<bool>(lc, "RightProviderValid"));
        Assert.AreEqual(0, Int(lc, "RecoveryEpisodes"));
    }

    [Test]
    public void FailedProviderQuery_IsNotAValidEye_AndNotACount()
    {
        var w = new World { xrReady = true, autoAdvance = false, mask = 0 };
        var lc = Make(w, false, "android_standalone", out _);
        double t = Run(lc, 0, 3);
        w.mask = 0; w.providerTime = 5; t += 0.1; Tick(lc, t);
        Assert.IsFalse(Get<bool>(lc, "ProviderProbeAvailable")); Assert.AreEqual(0L, Get<long>(lc, "ProviderSamples"));
    }

    [Test]
    public void PauseResume_WhileRunning_DoesNotLookLikeLostTracking_AndNothingRunsWhilePaused()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, false, "android_standalone", out _);
        double t = Run(lc, 0, 3);
        w.mask = Both; t += 0.1; Tick(lc, t);
        Call(lc, "OnPause", t);
        w.mask = 0; w.enabled[0] = false;   // whatever happens during the pause is not evaluated until the resume
        Tick(lc, t + 30); Tick(lc, t + 31);
        Assert.AreEqual("Running", State(lc), "a paused lifecycle takes no decisions");
        Call(lc, "OnResume", t + 40);
        w.enabled[0] = true; w.mask = 1;   // provider queries fine (new samples keep arriving), no valid eye yet
        Tick(lc, t + 40.1); Tick(lc, t + 41);
        Assert.AreEqual("Running", State(lc), "the 40 s pause is not 40 s without valid samples");
        for (int i = 0; i < 40; i++) Tick(lc, t + 41.1 + i * 0.1);
        Assert.AreEqual("TrackingUnavailable", State(lc), "but a real 3 s+ stretch after the resume is");
    }

    [Test]
    public void Transitions_CarryThePermissionCapabilityAndTrackingStateAtThatMoment()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, true, "android_standalone", out _);
        var seen = new List<string>();
        Run(lc, 0, 2); w.permissionGranted = true; w.pendingCallback(0); Run(lc, 2.1, 5);
        object startOk = null;
        foreach (var tr in Get<System.Collections.IEnumerable>(lc, "Transitions")) { seen.Add(Get<string>(tr, "event_name")); if (Get<string>(tr, "event_name") == "start_success") startOk = tr; }
        CollectionAssert.IsSubsetOf(new[] { "begin", "xr_ready", "permission_request", "permission_result", "start_success" }, seen);
        Assert.IsNotNull(startOk);
        Assert.AreEqual("granted", Get<string>(startOk, "permission_state")); Assert.AreEqual("supported", Get<string>(startOk, "capability_state"));
        Assert.AreEqual("true", Get<string>(startOk, "tracking_enabled")); Assert.AreEqual("running", Get<string>(startOk, "to"));
    }

    [Test]
    public void TransitionLog_IsBounded_AndKeepsTheStartOfTheSession()
    {
        var w = new World { xrReady = true };
        var lc = Make(w, false, "android_standalone", out _, o => Set(o, "maxTransitions", 6));
        double t = Run(lc, 0, 3);
        for (int i = 0; i < 40; i++) { w.enabled[0] = false; t = Run(lc, t + 0.1, t + 3); }
        var list = new List<object>(); foreach (var tr in Get<System.Collections.IEnumerable>(lc, "Transitions")) list.Add(tr);
        Assert.LessOrEqual(list.Count, 6); Assert.AreEqual("begin", Get<string>(list[0], "event_name")); Assert.Greater(Int(lc, "DroppedTransitions"), 0);
    }

    // ============================================================ scheduler, frame timing, rate diagnosis (pure)

    [Test]
    public void Scheduler_KeepsTheNominalRate_WhereWaitAfterEachSampleDrifted()
    {
        const double frame = 1.0 / 90.0, interval = 0.05;
        // old behaviour: wait `interval` after every sample, resume at the first frame boundary at/after it
        double t = 0, n = 0, tOld = 0;
        for (int i = 0; i < 2000; i++) { tOld = Math.Ceiling((tOld + interval) / frame - 1e-9) * frame; n++; }
        double oldHz = n / tOld;
        // new behaviour: deadline grid
        var sch = Activator.CreateInstance(T("SampleScheduler"), interval);
        t = 0; n = 0;
        for (int i = 0; i < 2000; i++)
        {
            double wait = (double)Call(sch, "WaitAfterSample", t);
            t = Math.Ceiling((t + wait) / frame - 1e-9) * frame; n++;
        }
        double newHz = n / t;
        Assert.Less(oldHz, 19.0, "the old scheduling lost rate to frame overshoot (20 Hz nominal at 90 fps ~ 18 Hz)");
        Assert.AreEqual(20.0, newHz, 0.2, "the deadline grid converges to the nominal rate");
        Assert.AreEqual(0L, Get<long>(sch, "SkippedTicks"));
    }

    [Test]
    public void Scheduler_BelowNominalFrameRate_NeverBursts_AndCountsMissedTicks()
    {
        const double frame = 0.135, interval = 0.05;   // the observed ~7.4 fps Editor session
        var sch = Activator.CreateInstance(T("SampleScheduler"), interval);
        double t = 0; int samples = 0; var gaps = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            double wait = (double)Call(sch, "WaitAfterSample", t);
            Assert.GreaterOrEqual(wait, 0.001); Assert.LessOrEqual(wait, interval + 1e-9);
            double next = Math.Ceiling((t + wait) / frame - 1e-9) * frame;
            gaps.Add(next - t); t = next; samples++;
        }
        foreach (double g in gaps) Assert.GreaterOrEqual(g, frame - 1e-9, "at most one sample per frame - no catch-up burst");
        Assert.AreEqual(1.0 / frame, samples / t, 0.2, "frame-rate limited, exactly");
        Assert.Greater(Get<long>(sch, "SkippedTicks"), 0L);
    }

    [Test]
    public void Scheduler_ClockJumpsBackwards_NeverStallsALoop()
    {
        var sch = Activator.CreateInstance(T("SampleScheduler"), 0.05);
        Call(sch, "WaitAfterSample", 1000.0);
        double w = (double)Call(sch, "WaitAfterSample", 1.0);   // the (test) clock went backwards by ~1000 s
        Assert.LessOrEqual(w, 0.05 + 1e-9); Assert.GreaterOrEqual(w, 0.001);
    }

    [Test]
    public void FrameTiming_AndRateDiagnosis_AttributeTheObservedRate()
    {
        var fs = Activator.CreateInstance(T("FrameTimingStats"));
        Assert.IsFalse(Get<bool>(fs, "Available"));
        for (int i = 0; i < 100; i++) Call(fs, "OnFrame", i % 10 == 0 ? 0.25 : 0.12);
        Assert.IsTrue(Get<bool>(fs, "Available")); Assert.AreEqual(100L, Get<long>(fs, "Frames")); Assert.AreEqual(100L, Get<long>(fs, "Over100ms"));
        Assert.AreEqual(0.25, Get<double>(fs, "MaxDt"), 1e-9); Assert.AreEqual(1.0 / ((0.25 * 10 + 0.12 * 90) / 100), Get<double>(fs, "MeanHz"), 1e-6);
        var rd = T("RateDiagnosis");
        string C(double nominal, double observed, double frameHz) => (string)CallStatic(rd, "Classify", nominal, observed, frameHz);
        Assert.AreEqual("nominal_achieved", C(20, 19.5, 90));
        Assert.AreEqual("frame_rate_limited", C(20, 7.39, 7.4), "observed ~ frame rate < nominal: the sampler cannot do better");
        Assert.AreEqual("scheduling_or_io_limited", C(20, 7.39, 60), "frame rate is fine but samples are missing: blocked work / scheduling");
        Assert.AreEqual("insufficient_samples", C(20, double.NaN, 60));
    }

    [Test]
    public void SamplingStats_AttributesGapsToLongFrames_WithoutWeakeningGapDetection()
    {
        var st = Activator.CreateInstance(T("SamplingStats"), 20.0);
        double t = 0;
        for (int i = 0; i < 20; i++) { Call(st, "OnSampleWithFrame", t, 0.016); t += 0.05; }
        Call(st, "OnSampleWithFrame", t + 0.4, 0.4); t += 0.4;      // one 0.45 s interval, the frame itself lasted 0.4 s
        Call(st, "OnSampleWithFrame", t + 0.45, 0.01);              // another 0.45 s gap that no frame explains (blocked I/O)
        Assert.AreEqual(2L, Get<long>(st, "UnexpectedGaps"), "both are still unexpected gaps (threshold unchanged)");
        Assert.AreEqual(1L, Get<long>(st, "GapsFrameLimited"), "only the one explained by a long frame is attributed to frame time");
        Assert.AreEqual(0.15, Get<double>(st, "GapThreshold"), 1e-9);
    }
}

public partial class NpcLifecycleTests
{
    // ======================================================================= recorder / manifest integration (Editor, no Quest)

    [UnityTest]
    public IEnumerator EyeAcquisition_Gaze_WriterOkButNoEyesAcquired_IsReportedAsTwoDifferentFacts()
    {
        Rig rig = null;
        yield return BuildRig(new RigOptions { prewarm = 1, maxAgents = 1, npcSpeed = 0.3f }, r => rig = r);
        var head = Make("Head").transform; head.position = new Vector3(0f, 1f, -5f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); _spawned.Add(wall); wall.transform.position = new Vector3(0f, 1f, 0f); wall.transform.localScale = new Vector3(40f, 40f, 0.2f);
        var rec = MakeGaze(rig, head, 30f);
        yield return null;
        Call(rig.em, "BeginTrial");
        yield return new WaitForSecondsRealtime(1.0f);
        Call(rig.dcm, "WriteManifestEnd", "test");

        var csv = Csv.Load(rig.File("gaze.csv"));
        Assert.Greater(csv.Rows.Count, 5);
        Assert.AreEqual(69, csv.Header.Count);
        CollectionAssert.AreEqual(new[] { "eye_tracking_state", "left_provider_valid", "right_provider_valid" }, csv.Header.GetRange(66, 3), "schema v9 columns are appended at the END");
        foreach (var r in csv.Rows)
        {
            Assert.AreEqual("head_fallback", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("0", csv.Get(r, "ray_is_eye_gaze"));
            Assert.AreEqual("not_applicable", csv.Get(r, "eye_tracking_state"), "no OVREyeGaze assigned in this rig");
            Assert.AreEqual("", csv.Get(r, "left_provider_valid"), "unknown is blank, never a guessed 0"); Assert.AreEqual("", csv.Get(r, "right_provider_valid"));
        }

        var m = JObject.Parse(File.ReadAllText(rig.File("session_manifest.json")));
        Assert.AreEqual(9, (int)m["schema_version"]);
        Assert.AreEqual("ok", (string)m["gaze_writer_status"], "the CSV WRITER is healthy ...");
        Assert.AreEqual("ok", (string)m["health_gaze"]["status"], "(unchanged legacy field)");
        Assert.AreEqual("not_applicable", (string)m["gaze_acquisition_status"], "... which says nothing about whether eyes were tracked");
        var et = m["eye_tracking"];
        Assert.AreEqual(0L, (long)et["rows_valid_combined"]); Assert.Greater((long)et["rows_fallback"], 5L); Assert.AreEqual((long)et["rows_fallback"], (long)et["rows_fallback_not_acquiring"]);
        Assert.AreEqual(0L, (long)et["provider_any_valid_samples"]); Assert.AreEqual(-1.0, (double)et["first_valid_row_t_abs"], 1e-9);
        Assert.AreEqual(true, (bool)et["lifecycle_present"]); Assert.AreEqual("not_applicable", (string)et["lifecycle_state"]);
        Assert.IsNotNull(et["transitions"]); Assert.Greater(et["transitions"].Count(), 0);
        Assert.IsTrue(m["frame_timing"] != null && m["rate_gaze"]["limit_diagnosis"] != null);
    }

    [UnityTest]
    public IEnumerator EyeAcquisition_StaleConfidence_IsRejectedByTheProviderGate_AndRecordedAsInvalid()
    {
        var rec = MakeStandaloneGaze("gate.csv", out Transform head);
        var lc = Get<object>(rec, "Lifecycle"); Assert.IsNotNull(lc, "GazeRecorder creates the lifecycle controller itself in Awake (no scene component, no ordering)");
        // the fakes are installed synchronously, before the controller's first Update (the real platform has no OVREyeGaze components in this rig and would go NotApplicable)
        var plat = Get<object>(lc, "Platform");
        bool fakeRight = true; double ts = 1000;
        Set(plat, "PermissionApplicable", false);
        Set(plat, "XrReady", (Func<bool>)(() => true)); Set(plat, "Supported", (Func<bool>)(() => true)); Set(plat, "TrackingEnabled", (Func<bool>)(() => true));
        Set(plat, "WorldFrameReady", (Func<bool>)(() => true));
        Set(plat, "ComponentPresent", (Func<int, bool>)(i => true)); Set(plat, "ComponentEnabled", (Func<int, bool>)(i => true));
        Set(plat, "SetComponentEnabled", (Action<int, bool>)((i, on) => { }));
        Set(plat, "ReadProvider", (Func<int>)(() => 1 | 2 | (fakeRight ? 4 : 0)));
        Set(plat, "ProviderTime", (Func<double>)(() => ts += 0.01));
        // the lifecycle of the (already started) controller picks the fakes up on its next ticks
        for (int i = 0; i < 40 && Get<object>(lc, "State").ToString() != "Running"; i++) yield return null;
        Assert.AreEqual("Running", Get<object>(lc, "State").ToString());

        string path = Get<string>(rec, "outputPathOverride");
        Call(rec, "StartRecording");
        var eyeL = Inj(0.9f, new Vector3(0.5f, 1f, -5f), Vector3.forward); Set(eyeL, "applyProviderGate", true);
        var eyeR = Inj(0.9f, new Vector3(-0.5f, 1f, -5f), Vector3.forward); Set(eyeR, "applyProviderGate", true);   // confidence passes, but the provider will say "invalid"
        Set(rec, "testLeftEye", eyeL); Set(rec, "testRightEye", eyeR);
        yield return new WaitForSecondsRealtime(0.4f);
        fakeRight = false;
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rec, "StopRecording");

        var csv = Csv.Load(path);
        var both = new List<List<string>>(csv.Where("gaze_ray_source", "both")); var leftOnly = new List<List<string>>(csv.Where("gaze_ray_source", "left"));
        Assert.Greater(both.Count, 3); Assert.Greater(leftOnly.Count, 3, "once the provider flags the right eye invalid, its (stale) confidence no longer counts");
        foreach (var r in both) { Assert.AreEqual("1", csv.Get(r, "left_provider_valid")); Assert.AreEqual("1", csv.Get(r, "right_provider_valid")); Assert.AreEqual("running", csv.Get(r, "eye_tracking_state")); }
        foreach (var r in leftOnly) { Assert.AreEqual("1", csv.Get(r, "left_valid")); Assert.AreEqual("0", csv.Get(r, "right_valid")); Assert.AreEqual("0", csv.Get(r, "right_provider_valid")); Assert.AreEqual("", csv.Get(r, "right_dir_x")); }
        Assert.Greater(Get<long>(rec, "RowsStaleConfidenceRejected"), 0L);
        Assert.AreEqual(Get<long>(rec, "RowsWritten"), (long)csv.Rows.Count);
    }

    [UnityTest]
    public IEnumerator EyeAcquisition_HostConflict_BlocksEyeSamples_ButStaysIdentifiable()
    {
        var rec = MakeStandaloneGaze("host.csv", out Transform head);
        yield return null;
        var ctl = Get<object>(rec, "lifecycleController");
        Set(ctl, "HostConflict", "left_on_rig_eye_anchor;");   // auto-property backing: set through the setter
        string path = Get<string>(rec, "outputPathOverride");
        var eye = Inj(0.9f, head.position, Vector3.forward); Set(eye, "applyProviderGate", true);
        Set(rec, "testLeftEye", eye); Set(rec, "testRightEye", eye);
        Call(rec, "StartRecording");
        yield return new WaitForSecondsRealtime(0.4f);
        Call(rec, "StopRecording");
        var csv = Csv.Load(path);
        Assert.Greater(csv.Rows.Count, 3);
        foreach (var r in csv.Rows) { Assert.AreEqual("0", csv.Get(r, "combined_valid")); Assert.AreEqual("head_fallback", csv.Get(r, "gaze_ray_source")); Assert.AreEqual("0", csv.Get(r, "ray_is_eye_gaze")); }
        Assert.Greater(Get<long>(rec, "RowsHostConflictBlocked"), 0L);
    }

    [UnityTest]
    public IEnumerator EyeAcquisition_NoDuplicateLifecycleControllers_AndShutdownOnDestroy()
    {
        var rec = MakeStandaloneGaze("dup.csv", out Transform head);
        yield return null;
        var ctls = UnityEngine.Object.FindObjectsOfType(T("EyeTrackingLifecycleController"));
        Assert.AreEqual(1, ctls.Length, "one owner");
        var lc = Get<object>(rec, "Lifecycle");
        UnityEngine.Object.Destroy(((Component)rec).gameObject);
        yield return null; yield return null;
        Assert.AreEqual("Stopped", Get<object>(lc, "State").ToString(), "destroying the host stops the lifecycle (no enabling after shutdown)");
    }
}
