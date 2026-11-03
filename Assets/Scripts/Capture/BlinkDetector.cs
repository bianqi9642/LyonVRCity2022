// BlinkDetector.cs
// Blink detection from RAW per-eye eye-closure measurements, and the honest capability status of that signal. Pure C# (no Unity types) so it is unit-testable.
//
// WHAT THE QUEST PRO / INSTALLED SDK (Meta XR SDK Core 201.0.0) EXPOSES - verified by reading the installed package source, not assumed
//   * EYE TRACKING (this project's gaze path): OVRPlugin.GetEyeGazesState -> OVRPlugin.EyeGazeState { Pose, Confidence, IsValid } per eye. That is gaze pose + validity + confidence ONLY.
//     There is no eye-openness, eyelid or blink field in it. Eye-gaze validity / confidence dropping is NOT a blink signal (it also drops for tracking loss, glare, off-axis gaze, an
//     unworn headset ...), so gaze validity is never turned into a blink.
//   * FACE TRACKING (a separate feature, separate permission com.oculus.permission.FACE_TRACKING, OVRPlugin.faceTracking2Supported / StartFaceTracking2, OVRFaceExpressions): 63 blendshape
//     weights including FaceExpression.EyesClosedL / EyesClosedR (0..1). That IS a usable eye-closure signal - but only when face tracking is enabled in the project
//     (OculusProjectConfig.faceTrackingSupport), the permission is granted and an OVRFaceExpressions component runs. This project has none of those configured (faceTrackingSupport = 0, no
//     OVRFaceExpressions in the scene), so the capability here is "not_configured", NOT "unsupported". Enabling it also records facial data about the participant (a data-protection
//     decision for the study, not a code detail), and it needs Quest Pro hardware to validate.
//
// Status vocabulary: unsupported (the runtime reports it cannot provide the signal) | unavailable (could be provided, not accessible right now) | not_configured (the capability / permission /
// component is not enabled in this project) | available (the raw signal is being delivered). The status is NEVER derived from "no blinks were seen".
//
// Detection: both eyes' closure >= CloseThreshold starts a blink; both <= OpenThreshold ends it (hysteresis, so jitter around one threshold cannot create repeated events). One eye closed with
// the other open is a unilateral closure (counted, not a blink). A sample with missing / non-finite / out-of-range data neither starts nor continues a blink: an open blink is closed at its
// last valid closed sample and marked truncated. Resolution is the sampling interval of the caller. The default thresholds are UNCALIBRATED defaults for 0..1 blendshape weights: they must be
// calibrated on a Quest Pro before blink durations are analysed (manifest: blink.thresholds_calibrated = false).
// All comments in English per project convention.

using System;

public struct BlinkFacts
{
    public bool OvrPluginInitialized;
    public bool FaceTrackingSupported;      // OVRPlugin.faceTracking2Supported (the runtime's own report)
    public bool FaceTrackingEnabled;        // OVRPlugin.faceTracking2Enabled (face tracking has been started)
    public bool ComponentPresent;           // an OVRFaceExpressions component exists in the scene / is assigned
    public bool PermissionGranted;          // com.oculus.permission.FACE_TRACKING (or the platform does not require it)
    public bool SignalValid;                // OVRFaceExpressions.ValidExpressions right now
}

public static class BlinkCapability
{
    public const string Unsupported = "unsupported", Unavailable = "unavailable", NotConfigured = "not_configured", Available = "available";

    /// <summary>Decides the status of the blink signal from observed facts only (never from the absence of blinks).</summary>
    public static string Decide(BlinkFacts f, out string reason)
    {
        if (!f.OvrPluginInitialized) { reason = "ovrplugin_not_initialized_so_the_runtime_cannot_be_queried"; return Unavailable; }
        if (!f.FaceTrackingSupported) { reason = "runtime_reports_no_face_tracking_support_eye_gaze_api_has_no_openness_signal"; return Unsupported; }
        if (!f.ComponentPresent) { reason = "face_tracking_supported_by_runtime_but_no_OVRFaceExpressions_component_and_project_faceTrackingSupport_off"; return NotConfigured; }
        if (!f.PermissionGranted) { reason = "face_tracking_permission_not_granted"; return NotConfigured; }
        if (!f.FaceTrackingEnabled) { reason = "face_tracking_not_started_by_the_application"; return NotConfigured; }
        if (!f.SignalValid) { reason = "face_expressions_not_valid_right_now"; return Unavailable; }
        reason = "eyes_closed_blendshapes_delivered"; return Available;
    }
}

public sealed class BlinkDetector
{
    public float CloseThreshold { get; }
    public float OpenThreshold { get; }

    public bool InBlink { get; private set; }
    public double CurrentStartT { get; private set; } = double.NaN;
    public long Blinks { get; private set; }
    public long TruncatedBlinks { get; private set; }
    public long UnilateralClosureSamples { get; private set; }
    public long InvalidSamples { get; private set; }
    public long OutOfRangeSamples { get; private set; }
    public double SumDuration { get; private set; }
    public double MaxDuration { get; private set; }
    public double MeanDuration => Blinks > 0 ? SumDuration / Blinks : double.NaN;

    double _lastClosedT = double.NaN;

    public BlinkDetector(float closeThreshold = 0.7f, float openThreshold = 0.5f)
    {
        if (!(closeThreshold > openThreshold)) throw new ArgumentException("closeThreshold must exceed openThreshold (hysteresis)");
        CloseThreshold = closeThreshold; OpenThreshold = openThreshold;
    }

    public struct Result
    {
        public bool Started, Ended, Truncated;
        public double StartT, EndT;
        public double Duration => EndT - StartT;
    }

    /// <summary>Feeds one pair of closure weights (null / NaN = no measurement). Returns whether a blink started or ended at this sample.</summary>
    public Result Update(double t, float? left, float? right)
    {
        var r = new Result();
        bool valid = Valid(left, out bool lOut) & Valid(right, out bool rOut);
        if (lOut || rOut) OutOfRangeSamples++;
        if (!valid)
        {
            InvalidSamples++;
            if (InBlink) { r.Ended = true; r.Truncated = true; r.StartT = CurrentStartT; r.EndT = double.IsNaN(_lastClosedT) ? CurrentStartT : _lastClosedT; Close(r); }
            return r;
        }
        float l = left.Value, rr = right.Value;
        bool lClosed = l >= CloseThreshold, rClosed = rr >= CloseThreshold;
        if (!InBlink)
        {
            if (lClosed && rClosed) { InBlink = true; CurrentStartT = t; _lastClosedT = t; r.Started = true; r.StartT = t; }
            else if (lClosed != rClosed) UnilateralClosureSamples++;
        }
        else
        {
            if (l >= CloseThreshold || rr >= CloseThreshold) _lastClosedT = t;
            if (l <= OpenThreshold && rr <= OpenThreshold) { r.Ended = true; r.StartT = CurrentStartT; r.EndT = t; Close(r); }
        }
        return r;
    }

    void Close(Result r)
    {
        InBlink = false; CurrentStartT = double.NaN; _lastClosedT = double.NaN;
        Blinks++;
        if (r.Truncated) TruncatedBlinks++;
        double d = r.Duration; SumDuration += d; if (d > MaxDuration) MaxDuration = d;
    }

    // a measurement is usable only when present, finite and inside the blendshape range 0..1; anything else is "no measurement", never a clamped value
    static bool Valid(float? v, out bool outOfRange)
    {
        outOfRange = false;
        if (!v.HasValue || float.IsNaN(v.Value) || float.IsInfinity(v.Value)) return false;
        if (v.Value < 0f || v.Value > 1f) { outOfRange = true; return false; }
        return true;
    }
}
