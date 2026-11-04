// FaceExpressionSupport.cs
// Pure (no scene access) pieces of the Quest Pro eyelid / blink signal: how one face reading is classified, how the SDK's eye-following correction is undone, how the gaze.csv face columns are formatted,
// and how interruptions of the signal are counted. Unit-testable without a headset. The runtime plumbing lives in GazeRecorder; the optional controlled-closure tool in FaceTrackingDiagnostic.
//
// WHY THIS EXISTS (diagnosed, see docs/face_tracking_diagnostic.md)
//   * OculusProjectConfig.faceTrackingSupport only controls manifest / permission generation. Face tracking is STARTED by an enabled OVRFaceExpressions component (OnEnable -> OVRPlugin.StartFaceTracking2).
//     OVRManager never starts it. No scene contained such a component and nothing created one, so OVRPlugin.faceTracking2Enabled stayed false, BlinkCapability said not_configured, and eyes_closed_* stayed blank.
//   * The runtime settings request BOTH data sources (Visual + Audio). The documented meaning of the Audio source is "estimate face expressions using audio data only": it cannot observe eyelids. A frame
//     that is "valid" but audio-sourced is therefore not an eyelid measurement and is recorded as such (face_state = audio_only_source, closure blank) instead of as a flat, plausible-looking 0.
//   * OVRFaceExpressions documents that, when EyeFollowingBlendshapesValid is true, EyesClosedX is reduced so that EyesClosedX + EyesLookDownX is in 0..1; the true lid state is EyesClosedX + min(LookDownL, LookDownR).
//     The raw weights stay in eyes_closed_left / eyes_closed_right (unchanged meaning); the recovered values are separate columns and drive the blink detector.
// Nothing here fabricates a value: a missing / invalid / audio-sourced reading yields blank, never 0 and never the previous value.
// All comments in English per project convention.

using System;
using System.Globalization;

/// <summary>One live read of the face-expression state in a single Unity frame (OVRFaceExpressions semantics).</summary>
public struct FaceReading
{
    public bool Queried;                 // a face-expression source was read at all (false = no component / no seam)
    public bool Started;                 // schema v17: OVRPlugin.faceTracking2Enabled read in THIS frame (not a <=1 s old cache). Seams that leave it false are treated as started.
    public bool ApiOk;                   // schema v17: GetFaceState2 returned a state in this frame (says nothing about Status.IsValid)
    public string DataSource;            // schema v17: "visual" | "audio" | "" of THIS frame's state (never of an older probe); null/"" = unknown
    public double ProviderTime;          // schema v17: FaceState.Time of this frame's state (OVRPlugin clock, NOT CaptureClock.TAbs); 0 = unknown
    public bool ExpressionsValid;        // GetFaceState2 succeeded AND Status.IsValid (the OVRFaceExpressions.ValidExpressions rule, evaluated on the same state the weights come from)
    public bool EyeFollowingValid;       // OVRFaceExpressions.EyeFollowingBlendshapesValid
    public float ClosedL, ClosedR;       // raw FaceExpression.EyesClosedL / R as delivered by the SDK
    public float LookDownL, LookDownR;   // FaceExpression.EyesLookDownL / R (meaningful only when EyeFollowingValid)
    public int Frame;                    // Unity Time.frameCount of the read
}

public static class FaceStateNames
{
    public const string NoRuntime = "no_runtime";            // OVRPlugin not initialised: the runtime cannot be queried (Editor without a headset)
    public const string Unsupported = "unsupported";         // the runtime reports no face-tracking support
    public const string NoComponent = "no_component";        // no running OVRFaceExpressions
    public const string NotStarted = "not_started";          // component exists but face tracking is not started (permission pending / StartFaceTracking2 failed)
    public const string InvalidFrame = "invalid_frame";      // started, but the current frame is not valid (GetFaceState2 failed or Status.IsValid = false)
    public const string AudioOnly = "audio_only_source";     // valid frame whose data source is Audio only: not an eyelid measurement
    public const string Current = "current";                 // a valid, visually sourced frame read in this Unity frame
}

public static class FaceClosure
{
    /// <summary>Classifies one row. facts come from the 1 Hz runtime probe, the reading is live.</summary>
    public static string Classify(bool ovrInit, bool supported, bool started, string dataSource, in FaceReading r)
    {
        if (!ovrInit) return FaceStateNames.NoRuntime;
        if (!supported) return FaceStateNames.Unsupported;
        if (!r.Queried) return FaceStateNames.NoComponent;
        if (!started) return FaceStateNames.NotStarted;
        if (!r.ExpressionsValid) return FaceStateNames.InvalidFrame;
        if (dataSource == "audio") return FaceStateNames.AudioOnly;
        return FaceStateNames.Current;
    }

    public static bool IsCurrent(string state) => state == FaceStateNames.Current;

    /// <summary>
    /// The true eyelid closure per the SDK documentation. EyeFollowingValid: EyesClosedX + min(EyesLookDownL, EyesLookDownR); otherwise the raw EyesClosedX already is the lid state.
    /// No clamping: a result outside 0..1 is reported as measured (the blink detector treats it as out-of-range, never as a clamped value).
    /// </summary>
    public static void Recover(in FaceReading r, out float left, out float right)
    {
        float add = r.EyeFollowingValid ? Math.Min(r.LookDownL, r.LookDownR) : 0f;
        left = r.ClosedL + add; right = r.ClosedR + add;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------- startup readiness (schema v17)

/// <summary>What must be true before the first formal gaze row can carry a visual eyelid measurement. Every flag is read live (no cached probe).</summary>
public struct FaceReadinessFacts
{
    public bool OvrInit, Supported, PermissionGranted, ComponentCreated, ComponentEnabled, Started, ApiOk, StatusValid, EyeClosureAvailable;
    public string DataSource;   // "visual" | "audio" | "" (unknown)
}

public enum FaceReadinessStage
{
    NoRuntime, Unsupported, PermissionNotGranted, ComponentMissing, ComponentDisabled, NotStarted, ApiNotResponding, FrameInvalid, AudioOnlySource, DataSourceUnknown, EyeClosureUnavailable, Ready
}

public static class FaceReadiness
{
    /// <summary>The FIRST unmet requirement, in the order a start-up goes through them. "A component exists" is the weakest stage, never sufficient: Ready needs a valid Visual frame with eyelid weights.</summary>
    public static FaceReadinessStage Evaluate(in FaceReadinessFacts f)
    {
        if (!f.OvrInit) return FaceReadinessStage.NoRuntime;
        if (!f.Supported) return FaceReadinessStage.Unsupported;
        if (!f.PermissionGranted) return FaceReadinessStage.PermissionNotGranted;
        if (!f.ComponentCreated) return FaceReadinessStage.ComponentMissing;
        if (!f.ComponentEnabled) return FaceReadinessStage.ComponentDisabled;
        if (!f.Started) return FaceReadinessStage.NotStarted;
        if (!f.ApiOk) return FaceReadinessStage.ApiNotResponding;
        if (!f.StatusValid) return FaceReadinessStage.FrameInvalid;
        if (f.DataSource == "audio") return FaceReadinessStage.AudioOnlySource;
        if (f.DataSource != "visual") return FaceReadinessStage.DataSourceUnknown;
        if (!f.EyeClosureAvailable) return FaceReadinessStage.EyeClosureUnavailable;
        return FaceReadinessStage.Ready;
    }

    public static string Name(FaceReadinessStage s)
    {
        switch (s)
        {
            case FaceReadinessStage.NoRuntime: return "no_runtime";
            case FaceReadinessStage.Unsupported: return "unsupported";
            case FaceReadinessStage.PermissionNotGranted: return "permission_not_granted";
            case FaceReadinessStage.ComponentMissing: return "component_missing";
            case FaceReadinessStage.ComponentDisabled: return "component_disabled";
            case FaceReadinessStage.NotStarted: return "not_started";
            case FaceReadinessStage.ApiNotResponding: return "api_not_responding";
            case FaceReadinessStage.FrameInvalid: return "frame_invalid";
            case FaceReadinessStage.AudioOnlySource: return "audio_only_source";
            case FaceReadinessStage.DataSourceUnknown: return "data_source_unknown";
            case FaceReadinessStage.EyeClosureUnavailable: return "eye_closure_unavailable";
            default: return "ready";
        }
    }

    /// <summary>Stages that waiting cannot change (the runtime is absent or reports no support): the gate gives up at once instead of burning its timeout.</summary>
    public static bool IsPermanent(FaceReadinessStage s) => s == FaceReadinessStage.NoRuntime || s == FaceReadinessStage.Unsupported;
}

/// <summary>Outcome of the bounded start-up wait (manifest eye_tracking.blink.startup_*). Nothing here is a gaze or eyelid value.</summary>
[Serializable]
public sealed class FaceStartupInfo
{
    public bool attempted;               // the start-up gate ran for this session
    public bool ready;                   // a valid Visual frame with eyelid weights was observed before the first formal gaze row
    public string stage = "not_attempted";   // the stage reached (FaceReadiness.Name): "ready" or the first unmet requirement when the wait ended
    public string failure_reason = "";   // "" when ready; else the unmet requirement and why the wait ended (timeout | permanent)
    public double requested_t_abs = -1, ready_t_abs = -1, wait_s = -1, max_wait_s = -1;
    public long frames_polled;
    public string data_source_at_ready = "";
    public string start_point = "";      // preflight | after_start_click
    public bool component_created_by_gate;
}

/// <summary>The v16 gaze.csv face columns, appended after origin_fallback_reason. Blank = not measured; 0.0000 = a valid zero.</summary>
public static class FaceColumns
{
    public static readonly string[] Names =
    {
        "face_state", "face_tracking_started", "face_expressions_valid", "eye_following_valid",
        "eyes_look_down_left", "eyes_look_down_right", "eyes_closed_left_recovered", "eyes_closed_right_recovered",
        "face_sample_frame", "face_data_source"
    };

    static string F4(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <param name="factsKnown">the 1 Hz runtime probe has produced facts (false = started / validity columns blank, never a guessed 0).</param>
    public static string[] Values(string state, bool factsKnown, bool started, string dataSource, in FaceReading r)
    {
        var c = new string[Names.Length];
        c[0] = state;
        c[1] = factsKnown ? (started ? "1" : "0") : "";
        bool live = r.Queried && started;
        c[2] = live ? (r.ExpressionsValid ? "1" : "0") : "";
        bool current = FaceClosure.IsCurrent(state);
        c[3] = (live && r.ExpressionsValid) ? (r.EyeFollowingValid ? "1" : "0") : "";
        for (int i = 4; i <= 7; i++) c[i] = "";
        if (current)
        {
            if (r.EyeFollowingValid) { c[4] = F4(r.LookDownL); c[5] = F4(r.LookDownR); }
            FaceClosure.Recover(in r, out float l, out float rr);
            c[6] = F4(l); c[7] = F4(rr);
            c[8] = r.Frame.ToString(CultureInfo.InvariantCulture);
        }
        else c[8] = "";
        c[9] = dataSource ?? "";
        return c;
    }
}

/// <summary>Counts how often and for how long the signal was not current (an interruption never carries the previous value forward; this is only bookkeeping).</summary>
[Serializable]
public sealed class FaceContinuity
{
    public long rows_current, rows_not_current, interruptions, recoveries;
    public double longest_gap_s = 0;
    bool _have, _wasCurrent; double _gapStart = double.NaN;

    public void Observe(bool current, double t)
    {
        if (current) rows_current++; else rows_not_current++;
        if (_have)
        {
            if (_wasCurrent && !current) { interruptions++; _gapStart = t; }
            else if (!_wasCurrent && current)
            {
                recoveries++;
                if (!double.IsNaN(_gapStart)) longest_gap_s = Math.Max(longest_gap_s, t - _gapStart);
                _gapStart = double.NaN;
            }
        }
        else if (!current) _gapStart = t;   // the signal was never current yet: the first wait is also a gap
        _have = true; _wasCurrent = current;
    }
}
