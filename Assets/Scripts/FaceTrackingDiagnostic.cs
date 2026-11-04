// FaceTrackingDiagnostic.cs
// OPTIONAL, stand-alone diagnostic for the Quest Pro eyelid signal (EyesClosedL / EyesClosedR). It is NOT part of the experiment: it is not in any experiment scene and the experiment never needs it.
// Add it to an EMPTY scene (an XR-capable scene such as the Link smoke-test scene is enough; no OVRCameraRig, no gaze) and press Play on a Quest Pro over Quest Link, or run it on the headset.
//
// WHAT IT DOES (rate-limited to 5-10 Hz, never per frame, never to the Unity Console per sample)
//   * Makes sure an OVRFaceExpressions component runs (same rule as GazeRecorder.autoCreateFaceExpressions) and records, per tick, the layers that must all hold before an eyelid value is real:
//       runtime initialised -> face tracking supported -> permission -> component enabled -> OVRPlugin.faceTracking2Enabled (session started) -> GetFaceState2 call succeeded -> Status.IsValid ->
//       EyeFollowingBlendshapesValid -> data source (visual / audio) -> the weights.
//   * Reads the weights TWICE, independently: through OVRFaceExpressions (what the recorder uses) and through a direct OVRPlugin.GetFaceState2 call (the provider's own answer and timestamp), so a disagreement
//     between the two exposes a wrapper problem. Neither is ever replaced by a default: unavailable values are blank, a valid zero is 0.0000.
//   * Optionally steps through the controlled closure sequence (default: open 5 s, left closed 3 s, open 2 s, right closed 3 s, open 2 s, both closed 3 s, open 5 s) and writes the segment label next to every
//     row, so the trace can be plotted against CaptureClock.TAbs (docs/tools/plot_face_closure.py). The sequence is a protocol for the person wearing the headset, not a detection threshold.
//   * If a GazeRecorder is in the scene, also records the frame of its latest gaze row (gaze_row_frame) to show that face reads and gaze rows share frames.
// Output: <persistentDataPath>/face_diag_<yyyyMMddTHHmmssZ>.csv, appended row by row (crash safe). Nothing is written to any experiment session folder.
// All comments in English per project convention.

using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public class FaceTrackingDiagnostic : MonoBehaviour
{
    [Serializable]
    public class Segment
    {
        [Tooltip("Instruction label written to the CSV (and logged once when the segment starts).")] public string label = "open";
        [Tooltip("Seconds this segment lasts.")] [Min(0.1f)] public float seconds = 2f;
    }

    [Header("Sampling")]
    [Tooltip("Diagnostic rows per second. Clamped to 5..10 so the diagnostic can never become a per-frame logger.")]
    [Range(5f, 10f)] public float sampleHz = 10f;
    [Tooltip("Create an OVRFaceExpressions on this GameObject when none exists and the runtime supports face tracking.")]
    public bool autoCreateFaceExpressions = true;
    [Tooltip("Optional explicit output file (tests). Empty = persistentDataPath/face_diag_<timestamp>.csv.")]
    public string outputPathOverride = "";

    [Header("Controlled closure sequence")]
    [Tooltip("Run the closure protocol below from Start. The label column is 'idle' when this is off.")]
    public bool runSequence = true;
    public Segment[] sequence =
    {
        new Segment { label = "open_both", seconds = 5f }, new Segment { label = "left_closed", seconds = 3f }, new Segment { label = "open_both", seconds = 2f },
        new Segment { label = "right_closed", seconds = 3f }, new Segment { label = "open_both", seconds = 2f }, new Segment { label = "both_closed", seconds = 3f },
        new Segment { label = "open_both", seconds = 5f },
    };

    [Header("Diagnostics")]
    public bool enableDebugLogs = true;
    public OVRFaceExpressions faceExpressions;
    public GazeRecorder gazeRecorder;

    public string OutputPath { get; private set; } = "";
    public long RowsWritten { get; private set; }
    OVRPlugin.FaceState _state;
    Coroutine _loop;
    double _seqStart = double.NaN;

    /// <summary>One diagnostic tick, every field either measured or blank (nullable / empty).</summary>
    public struct Snapshot
    {
        public double TAbs; public int Frame; public string Segment; public double SegmentT;
        public bool OvrInit, Supported, PermissionGranted, ComponentEnabled, SessionStarted;
        public bool? ApiOk, StatusValid, EyeFollowing, WrapperValid;
        public string DataSource; public double? ProviderTime;
        public float? ApiClosedL, ApiClosedR, ApiLookDownL, ApiLookDownR;          // direct OVRPlugin.GetFaceState2 weights (only while ApiOk && StatusValid)
        public float? WrapClosedL, WrapClosedR, WrapLookDownL, WrapLookDownR;      // OVRFaceExpressions weights (only while WrapperValid)
        public int? GazeRowFrame;
    }

    public static readonly string[] Columns =
    {
        "t_abs", "frame", "segment", "segment_t_s",
        "ovr_initialized", "face_tracking_supported", "permission_granted", "component_enabled", "session_started",
        "get_face_state_ok", "status_valid", "eye_following_valid", "wrapper_valid", "data_source", "provider_time",
        "api_closed_l", "api_closed_r", "api_look_down_l", "api_look_down_r",
        "wrap_closed_l", "wrap_closed_r", "wrap_look_down_l", "wrap_look_down_r",
        "recovered_l", "recovered_r", "gaze_row_frame", "gaze_row_frame_lag"
    };

    static string B(bool v) => v ? "1" : "0";
    static string B(bool? v) => v.HasValue ? (v.Value ? "1" : "0") : "";
    static string N(float? v) => v.HasValue ? v.Value.ToString("F4", CultureInfo.InvariantCulture) : "";

    /// <summary>Formats a snapshot (pure; unit-tested). Recovered closure follows the SDK rule and is blank unless the wrapper's frame is valid.</summary>
    public static string FormatRow(in Snapshot s)
    {
        string rl = "", rr = "";
        if (s.WrapperValid == true && s.WrapClosedL.HasValue && s.WrapClosedR.HasValue)
        {
            var r = new FaceReading { Queried = true, ExpressionsValid = true, EyeFollowingValid = s.EyeFollowing == true, ClosedL = s.WrapClosedL.Value, ClosedR = s.WrapClosedR.Value,
                                      LookDownL = s.WrapLookDownL ?? 0f, LookDownR = s.WrapLookDownR ?? 0f };
            FaceClosure.Recover(in r, out float l, out float rgt); rl = N(l); rr = N(rgt);
        }
        // gaze_row_frame_lag: frames between this tick and the latest gaze row (0 = same frame); blank without a recorder
        string lag = s.GazeRowFrame.HasValue ? (s.Frame - s.GazeRowFrame.Value).ToString(CultureInfo.InvariantCulture) : "";
        var c = new[]
        {
            s.TAbs.ToString("F4", CultureInfo.InvariantCulture), s.Frame.ToString(CultureInfo.InvariantCulture), s.Segment ?? "", s.SegmentT.ToString("F3", CultureInfo.InvariantCulture),
            B(s.OvrInit), B(s.Supported), B(s.PermissionGranted), B(s.ComponentEnabled), B(s.SessionStarted),
            B(s.ApiOk), B(s.StatusValid), B(s.EyeFollowing), B(s.WrapperValid), s.DataSource ?? "", s.ProviderTime.HasValue ? s.ProviderTime.Value.ToString("F6", CultureInfo.InvariantCulture) : "",
            N(s.ApiClosedL), N(s.ApiClosedR), N(s.ApiLookDownL), N(s.ApiLookDownR),
            N(s.WrapClosedL), N(s.WrapClosedR), N(s.WrapLookDownL), N(s.WrapLookDownR),
            rl, rr, s.GazeRowFrame.HasValue ? s.GazeRowFrame.Value.ToString(CultureInfo.InvariantCulture) : "", lag
        };
        return string.Join(",", c);
    }

    void OnEnable()
    {
        if (gazeRecorder == null) gazeRecorder = FindObjectOfType<GazeRecorder>();
        string path = !string.IsNullOrEmpty(outputPathOverride) ? outputPathOverride
            : Path.Combine(Application.persistentDataPath, "face_diag_" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".csv");
        OutputPath = path;
        try { File.WriteAllText(path, string.Join(",", Columns) + "\n"); }
        catch (Exception e) { Debug.LogWarning("[FaceTrackingDiagnostic] cannot create " + path + ": " + e.Message); OutputPath = ""; return; }
        if (enableDebugLogs) Debug.Log("[FaceTrackingDiagnostic] writing " + path + " at " + Mathf.Clamp(sampleHz, 5f, 10f) + " Hz");
        _loop = StartCoroutine(Loop());
    }

    void OnDisable() { if (_loop != null) StopCoroutine(_loop); _loop = null; }

    IEnumerator Loop()
    {
        double interval = 1.0 / Mathf.Clamp(sampleHz, 5f, 10f);
        _seqStart = CaptureClock.TAbs;
        string lastSegment = null;
        while (true)
        {
            double now = CaptureClock.TAbs;
            Snapshot s = Collect(now, ref lastSegment);
            try { File.AppendAllText(OutputPath, FormatRow(in s) + "\n"); RowsWritten++; }
            catch (Exception e) { Debug.LogWarning("[FaceTrackingDiagnostic] write failed: " + e.Message); yield break; }
            yield return new WaitForSecondsRealtime((float)interval);
        }
    }

    Snapshot Collect(double now, ref string lastSegment)
    {
        var s = new Snapshot { TAbs = now, Frame = Time.frameCount };
        // protocol segment
        s.Segment = "idle";
        if (runSequence && sequence != null)
        {
            double t = now - _seqStart, acc = 0; s.Segment = "finished"; s.SegmentT = t - SequenceLength();
            foreach (var seg in sequence)
            {
                if (t < acc + seg.seconds) { s.Segment = seg.label; s.SegmentT = t - acc; break; }
                acc += seg.seconds;
            }
            if (s.Segment != lastSegment) { lastSegment = s.Segment; if (enableDebugLogs) Debug.Log("[FaceTrackingDiagnostic] cue: " + s.Segment + " (t=" + t.ToString("F1", CultureInfo.InvariantCulture) + " s)"); }
        }
        try
        {
            s.OvrInit = OVRPlugin.initialized;
            if (s.OvrInit) { s.Supported = OVRPlugin.faceTracking2Supported; s.SessionStarted = OVRPlugin.faceTracking2Enabled; }
            s.PermissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.FaceTracking);
        }
        catch (Exception) { }
        if (faceExpressions == null) faceExpressions = FindObjectOfType<OVRFaceExpressions>();
        if (faceExpressions == null && autoCreateFaceExpressions && s.OvrInit && s.Supported)
        {
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.FaceTracking });
            faceExpressions = gameObject.AddComponent<OVRFaceExpressions>();
        }
        s.ComponentEnabled = faceExpressions != null && faceExpressions.isActiveAndEnabled;
        if (s.ComponentEnabled)
        {
            s.WrapperValid = faceExpressions.ValidExpressions;
            if (s.WrapperValid == true)
            {
                s.EyeFollowing = faceExpressions.EyeFollowingBlendshapesValid;
                s.WrapClosedL = Weight(OVRFaceExpressions.FaceExpression.EyesClosedL); s.WrapClosedR = Weight(OVRFaceExpressions.FaceExpression.EyesClosedR);
                if (s.EyeFollowing == true) { s.WrapLookDownL = Weight(OVRFaceExpressions.FaceExpression.EyesLookDownL); s.WrapLookDownR = Weight(OVRFaceExpressions.FaceExpression.EyesLookDownR); }
            }
        }
        if (s.OvrInit && s.SessionStarted)
        {
            try
            {
                bool ok = OVRPlugin.GetFaceState2(OVRPlugin.Step.Render, -1, ref _state);
                s.ApiOk = ok;
                if (ok)
                {
                    s.StatusValid = _state.Status.IsValid; s.ProviderTime = _state.Time;
                    if (_state.Status.IsValid)
                    {
                        s.DataSource = _state.DataSource == OVRPlugin.FaceTrackingDataSource.Audio ? "audio" : "visual";
                        if (s.EyeFollowing == null) s.EyeFollowing = _state.Status.IsEyeFollowingBlendshapesValid;   // wrapper not valid: still show the provider's own flag
                        s.ApiClosedL = _state.ExpressionWeights[(int)OVRPlugin.FaceExpression2.Eyes_Closed_L]; s.ApiClosedR = _state.ExpressionWeights[(int)OVRPlugin.FaceExpression2.Eyes_Closed_R];
                        if (_state.Status.IsEyeFollowingBlendshapesValid)
                        {
                            s.ApiLookDownL = _state.ExpressionWeights[(int)OVRPlugin.FaceExpression2.Eyes_Look_Down_L]; s.ApiLookDownR = _state.ExpressionWeights[(int)OVRPlugin.FaceExpression2.Eyes_Look_Down_R];
                        }
                    }
                }
            }
            catch (Exception) { s.ApiOk = null; }
        }
        if (gazeRecorder != null && gazeRecorder.LastRowFrame > 0) s.GazeRowFrame = gazeRecorder.LastRowFrame;
        return s;
    }

    float? Weight(OVRFaceExpressions.FaceExpression e) => faceExpressions.TryGetFaceExpressionWeight(e, out float w) ? w : (float?)null;

    double SequenceLength() { double l = 0; if (sequence != null) foreach (var seg in sequence) l += seg.seconds; return l; }
}
