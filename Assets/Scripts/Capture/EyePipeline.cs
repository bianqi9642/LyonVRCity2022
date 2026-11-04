// EyePipeline.cs
// The per-eye measurement pipeline of a Quest Pro session, as data: what the provider reports, how it relates to the stereo view anchors and the rendered camera, and the evidence that tells a reader
// whether those frames are coherent. Pure math + manifest types (unit-testable without a headset).
//
// ARCHITECTURE (audited in the installed Meta XR SDK Core 201.0.0 + the scene; see docs/gaze_origin_verification.md section 9)
//   XRI "Main Camera" (under the XR Origin's Camera Offset) RENDERS. OVRCameraRig (cameras disabled: disableEyeAnchorCameras only toggles Camera.enabled, UpdateAnchors is independent of it) keeps
//   supplying the OVR tracking-space anchors, and two OVREyeGaze components (Eye = Left / Right, TrackingMode = WorldSpace) supply the genuine per-eye gaze poses from OVRPlugin.GetEyeGazesState.
//   OVRPlugin is initialised on Quest / Quest Link / the Meta simulator, so OVRManager.loadedXRDevice == Oculus and BOTH the anchors and the head reference of OVREyeGaze's head-space conversion come from
//   OVRPlugin.GetNodePose, i.e. the OVRPlugin tracking space - not from Unity XR (OVRCommon.OVRNodeStateProperties). The XRI camera, in contrast, is driven by Unity XR (TrackedPoseDriver, Unity's tracking origin).
//
// WHY THE RECORDED GAZE RAY IS THE ONE-STEP "HEAD-RELATIVE, THEN PLACED AT THE RENDERED CAMERA" COMPOSITION (OVREyeGaze WorldSpace)
//   OVREyeGaze: world = Camera.main.localToWorld * ( headRef^-1 * eyePose ), with eyePose and headRef BOTH from OVRPlugin (same tracking space). The eye pose relative to the head (headRef^-1 * eyePose) therefore
//   cancels any translation between OVRPlugin's tracking origin and Unity's (Floor vs Device, a Camera Offset...), and is then placed at the pose of the camera the participant sees. A raw "tracking space
//   pose * some tracking-to-world matrix" route would instead inherit every origin mismatch. Execution order inside one Update: TrackedPoseDriver (-30000) -> OVRManager (-100) -> OVREyeGaze (-80) ->
//   OVRCameraRig (-50) -> project scripts / GazeRecorder's coroutine, so the camera pose OVREyeGaze used is the same frame's. Each row is therefore one coherent set (camera, anchors, provider state of the
//   same Update pass); the evidence below (provider_time_s, anchors_updated_frame, composed-vs-WorldSpace deviation) lets a reader check that on hardware instead of assuming it.
//
// WHAT IS A GAZE ORIGIN, A VIEW ANCHOR, AN IPD (no anatomical claim is made anywhere)
//   * raw provider pose  : OVRPlugin EyeGazeState.Pose in OVRPlugin tracking space: position = the eye position the eye-tracking system reports, forward (-Z in Unity axes after the SDK conversion) = gaze direction.
//                          The SDK documents no pupil, cornea or eye-centre definition: it is "the provider's eye position", not a measured pupil.
//   * view anchor        : OVRCameraRig Left/RightEyeAnchor = the per-eye VIEW (rendering) pose from the runtime (affected by the headset IPD setting when OVRManager.useIPDInPositionTracking is true).
//   * runtime user IPD   : OVRPlugin.ipd (ovrp_GetUserIPD) = the IPD the runtime holds for the user (headset setting); a number, not a pupil measurement.
//   These three are recorded as separate quantities; none is derived from another and no IPD constant exists in this project.
// All comments in English per project convention.

using System;
using UnityEngine;

/// <summary>One eye of a raw provider reading (OVRPlugin tracking space, Unity axes).</summary>
public struct RawGazeEye
{
    public bool valid;                    // the provider's own IsValid
    public float confidence;
    public Vector3 rawPos, rawDir;        // tracking space (position, forward direction)
}

/// <summary>One raw provider reading of both eyes.</summary>
public struct RawGazeState
{
    public bool available;                // OVRPlugin.GetEyeGazesState succeeded
    public double time;                   // EyeGazesState.Time (provider timestamp)
    public RawGazeEye left, right;
}

public static class EyePipelineMath
{
    /// <summary>A head-space point placed at the rendered camera: view.pos + view.rot * headPos.</summary>
    public static Vector3 ComposeOrigin(Vector3 viewPos, Quaternion viewRot, Vector3 headPos) => viewPos + viewRot * headPos;

    /// <summary>A head-space direction rotated by the rendered camera (unit length).</summary>
    public static Vector3 ComposeDirection(Quaternion viewRot, Vector3 headDir) { Vector3 d = viewRot * headDir; return d.sqrMagnitude > 1e-12f ? d.normalized : d; }

    /// <summary>The same head-space conversion the SDK performs (OVRCommon.ToHeadSpacePose): headRef^-1 * pose.</summary>
    public static void ToHeadSpace(Vector3 headRefPos, Quaternion headRefRot, Vector3 pos, Quaternion rot, out Vector3 headPos, out Quaternion headRot)
    {
        Quaternion inv = Quaternion.Inverse(headRefRot);
        headPos = inv * (pos - headRefPos); headRot = inv * rot;
    }

    public static float AngleDeg(Vector3 a, Vector3 b)
    {
        if (a.sqrMagnitude < 1e-12f || b.sqrMagnitude < 1e-12f) return float.NaN;
        return Vector3.Angle(a, b);
    }

    /// <summary>Is a reported IPD a usable number? A very wide sanity gate (a unit / garbage check), NOT an assumed IPD and not used to correct anything.</summary>
    public static bool IsUsableIpd(float meters) => !float.IsNaN(meters) && !float.IsInfinity(meters) && meters > 0.02f && meters < 0.12f;
}

/// <summary>Bounded running statistics (count, min, max, mean).</summary>
[Serializable]
public sealed class RunningStat
{
    public long count; public double min = -1, max = -1, mean = -1;
    double _sum;
    public void Add(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return;
        count++; _sum += v; mean = _sum / count;
        if (count == 1 || v < min) min = v;
        if (count == 1 || v > max) max = v;
    }
}

/// <summary>Manifest block eye_tracking.eye_pipeline: the architecture facts and the coherence / geometry evidence of the per-eye pipeline. Evidence, not a verdict.</summary>
[Serializable]
public class EyePipelineManifest
{
    public string architecture = "A: XRI Main Camera renders; OVRCameraRig (cameras disabled, anchors still updating) + two OVREyeGaze components supply tracking and per-eye gaze";
    public string primary_gaze_path = "OVREyeGaze TrackingMode=WorldSpace: world = Camera.main.localToWorld * (OVRPlugin head node^-1 * OVRPlugin eye pose); origin and direction are recorded as left_/right_/combined_ origin and dir";
    public string left_provider_tracking_mode = "", right_provider_tracking_mode = "";   // OVREyeGaze.TrackingMode as configured on the running components ("" = no component)
    public string anchor_source = "";                  // OVRPlugin.GetNodePose (OVRPlugin tracking space) | Unity XR nodes | unknown - from OVRManager.loadedXRDevice
    public bool raw_provider_read_available;           // OVRPlugin.GetEyeGazesState answered for the raw columns
    public long rows_with_raw_provider, rows_provider_time_repeated, rows_provider_time_advanced;
    public string provider_timestamp_note = "provider_time_s = OVRPlugin EyeGazesState.Time; a repeated value means the row reuses the previous provider sample (provider rate below the row rate)";
    public long anchors_updated_events;                // OVRCameraRig.UpdatedAnchors raised while recording
    public long anchors_frame_lag_max;                 // largest (frame_index - anchors_updated_frame) seen; 0 = anchors refreshed in the same frame as every row
    public bool runtime_user_ipd_available;
    public RunningStat runtime_user_ipd_m = new RunningStat();                 // OVRPlugin.ipd
    public RunningStat view_anchor_separation_m = new RunningStat();           // |left view anchor - right view anchor| (stereo VIEW geometry)
    public RunningStat gaze_head_separation_m = new RunningStat();             // |left - right provider eye position| in head space (provider geometry, both eyes valid)
    public RunningStat left_gaze_vs_view_anchor_head_m = new RunningStat(), right_gaze_vs_view_anchor_head_m = new RunningStat();   // provider eye position minus view anchor, head space
    public RunningStat left_worldspace_vs_composed_origin_m = new RunningStat(), right_worldspace_vs_composed_origin_m = new RunningStat();   // OVREyeGaze world origin vs (rendered camera * head-space eye)
    public RunningStat left_worldspace_vs_composed_dir_deg = new RunningStat(), right_worldspace_vs_composed_dir_deg = new RunningStat();
    public string eye_tracking_calibration = "not_exposed_by_OVRPlugin_201: the Quest Pro system calibrates eye tracking; the application sees only provider validity and confidence";
    public string physical_pupil_position = "not_measured: the provider reports an eye position and direction; no pupil / cornea definition is documented";
    public string note = "Differences above are measurements, not errors by definition: provider eye position vs view anchor is expected to differ (different definitions); worldspace_vs_composed should be ~0 when the camera and the provider sample share a frame";
}
