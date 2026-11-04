// ViewFrame.cs
// The coordinate frame the PARTICIPANT SEES FROM, its relation to the OVRCameraRig anchor tree, and ground-relative heights. Pure math + a small scene probe, unit-testable with synthetic transforms.
//
// WHY THIS EXISTS (root cause of the +1.36144 m "provider origin" offset, traced in the scene, not assumed)
//   The experiment scene holds TWO tracked rigs under one XR Origin base ("XR Interaction Setup/XR Origin (XR Rig)"; ExperimentManager.playerRig, moved by AutoGroundAlign so the base sits on the ground):
//     * the XRI rig: "Camera Offset" (Camera Floor Offset, local Y = XROrigin.CameraYOffset = 1.36144 = Unity's stock default) -> "Main Camera" (the ONLY enabled camera tagged MainCamera, driven by a
//       TrackedPoseDriver). This is what the participant sees; UIManager, HeadLockedPanel and OVREyeGaze (Camera.main, TrackingMode = WorldSpace) all use it.
//     * the OVRCameraRig (disableEyeAnchorCameras = true), a CHILD of the base with local position 0: its CenterEye / LeftEye / RightEye anchors, the controllers and GazeRecorder.headTransform /
//       the trajectory hmd_* columns follow the same tracked head but WITHOUT the Camera Offset.
//   OVREyeGaze converts the provider pose with Camera.main, so it lands in the XRI frame (+CameraYOffset); the OVR anchors do not. provider - anchor = exactly the Camera Offset's local Y (measured
//   1.361441 m in both audited sessions, vs 1.36144 in the prefab). 1.36144 is a SCENE CONSTANT, not a participant measurement: while OVRManager keeps the tracking origin at Eye Level (Unity XR "Device"
//   mode) XROrigin writes it into the Camera Offset, so every participant's rendered eye height is that constant plus the (small) pose relative to the recentre point.
//
// WHAT THIS FILE DOES (nothing here contains an eye-height number)
//   * defines the view frame = the rendered camera (Camera.main) and expresses the eyes in it:  eyeWorld = view.pos + view.rot * headLocal,  headLocal = inverse(centreEye.rot) * (anchor.pos - centreEye.pos)
//     (head-relative eye positions are taken from the OVR anchors, where they are well defined, and re-applied at the rendered head pose);
//   * ground-relative height = world Y - Y of the walkable ground surface straight below (raycast on the AutoGroundAlign ground layer, NPC / participant colliders ignored); unavailable when no ground is found;
//   * decides, from the live XR facts only, WHERE the eye height comes from (EyeHeightSource): a fixed Camera Y offset (not participant-specific), a runtime floor reference, or undetermined.
// All comments in English per project convention.

using System;
using UnityEngine;

/// <summary>One reading of the rendered view and the eyes in it.</summary>
public struct ViewFrameSample
{
    public bool valid;                    // view camera, OVR centre eye and both anchors resolved
    public Vector3 hmdPos; public Quaternion hmdRot;   // the rendered camera (centre-eye / HMD reference the participant sees from)
    public Vector3 centreEyePos;          // OVR CenterEyeAnchor (same tracked head, without the Camera Offset)
    public Vector3 leftLocal, rightLocal; // eye anchors in the OVR centre-eye (head) frame
    public Vector3 leftWorld, rightWorld; // the eyes in the rendered view frame
    public Vector3 leftRigWorld, rightRigWorld;   // the raw OVR anchor world positions (rig frame)
    public Vector3 viewMinusCentre;       // view - OVR centre eye, world (= Camera Floor Offset in world axes when the heads coincide)
    public bool groundOk; public float groundY;
}

public static class ViewFrameMath
{
    /// <summary>A world point expressed in a head frame (position + rotation of the head).</summary>
    public static Vector3 HeadLocal(Vector3 headPos, Quaternion headRot, Vector3 worldPoint) => Quaternion.Inverse(headRot) * (worldPoint - headPos);

    /// <summary>A head-local point applied at another head pose (the rendered view).</summary>
    public static Vector3 EyeInView(Vector3 viewPos, Quaternion viewRot, Vector3 headLocal) => viewPos + viewRot * headLocal;

    /// <summary>Height above the ground surface (NaN-free: callers pass groundOk).</summary>
    public static float HeightAbove(float worldY, float groundY) => worldY - groundY;

    public static bool IsFinite(Vector3 v) => EyeGazeOrigin.IsFinite(v);
}

/// <summary>Facts about the XR Origin and tracking origin, read from the live scene (or injected by a test).</summary>
public struct XrOriginFacts
{
    public bool OriginPresent;
    public string RequestedMode, CurrentMode;     // XROrigin.RequestedTrackingOriginMode / CurrentTrackingOriginMode (strings; "" = unknown)
    public float CameraYOffsetProperty;           // XROrigin.CameraYOffset (the serialized, configured number)
    public bool HasOffsetObject;
    public float OffsetObjectLocalY;              // the Camera Floor Offset object's ACTUAL local Y at this moment
    public string OffsetObjectPath, ViewCameraPath, CentreEyePath;
}

public static class EyeHeightSource
{
    public const string FixedCameraOffset = "fixed_camera_y_offset_not_participant_specific";
    public const string RuntimeFloor = "runtime_floor_referenced";
    public const string RigPoseOnly = "rig_pose_only_no_xr_origin";
    public const string Indeterminate = "indeterminate";

    /// <summary>
    /// Where the rendered eye height above the XR Origin base comes from, decided ONLY from live facts:
    /// Device (eye-level) tracking origin + a non-zero Camera Floor Offset = a configured constant height for every participant; Floor tracking origin + zero offset = the runtime's floor-relative pose
    /// (participant-specific); no XR Origin = the rig pose alone.
    /// </summary>
    public static string Decide(XrOriginFacts f, float tolerance = 1e-3f)
    {
        if (!f.OriginPresent) return RigPoseOnly;
        bool offsetZero = !f.HasOffsetObject || Mathf.Abs(f.OffsetObjectLocalY) <= tolerance;
        if (f.CurrentMode == "Floor" && offsetZero) return RuntimeFloor;
        if (f.CurrentMode == "Device" && f.HasOffsetObject && !offsetZero) return FixedCameraOffset;
        if (string.IsNullOrEmpty(f.CurrentMode) && f.HasOffsetObject && !offsetZero) return FixedCameraOffset + "_mode_unreported";
        return Indeterminate;
    }
}

/// <summary>Resolves the transforms behind <see cref="ViewFrameSample"/> and reads them. Low cost: a few transform reads and one downward raycast per sample.</summary>
public sealed class ViewFrameProbe
{
    public Transform View, CentreEye, LeftEye, RightEye;
    public int GroundMask;                 // 0 = ground unavailable
    public float GroundRayMeters = 12f;
    int _searches; float _nextSearchT;

    public bool Resolved => View != null && CentreEye != null && LeftEye != null && RightEye != null;

    /// <summary>Fills the references that are still missing (explicit slots win). Bounded retries: the rig builds its anchors in its own Awake / Update.</summary>
    public void Resolve(Transform viewOverride, Transform centreOverride, Transform leftOverride, Transform rightOverride, LayerMask groundOverride, Transform headFallback)
    {
        if (Resolved && GroundMask != 0 && (View != null && View.gameObject.activeInHierarchy)) return;
        // Scene searches are throttled (once per second): this runs every gaze sample and FindObjectOfType is not free.
        if (Time.unscaledTime < _nextSearchT) return;
        _nextSearchT = Time.unscaledTime + 1.0f; _searches++;
        if (View == null || !View.gameObject.activeInHierarchy) View = viewOverride != null ? viewOverride : (Camera.main != null ? Camera.main.transform : null);
        OVRCameraRig rig = null;
        if (CentreEye == null || LeftEye == null || RightEye == null) rig = UnityEngine.Object.FindObjectOfType<OVRCameraRig>();
        if (CentreEye == null) CentreEye = centreOverride != null ? centreOverride : (rig != null && rig.centerEyeAnchor != null ? rig.centerEyeAnchor : headFallback);
        if (LeftEye == null) LeftEye = leftOverride != null ? leftOverride : (rig != null ? rig.leftEyeAnchor : null);
        if (RightEye == null) RightEye = rightOverride != null ? rightOverride : (rig != null ? rig.rightEyeAnchor : null);
        if (GroundMask == 0)
        {
            if (groundOverride.value != 0) GroundMask = groundOverride.value;
            else
            {
                AutoGroundAlign ag = UnityEngine.Object.FindObjectOfType<AutoGroundAlign>();   // the ground layer the project already uses to put the XR Origin on the terrain
                if (ag != null) GroundMask = ag.groundLayer.value;
            }
        }
    }

    static readonly RaycastHit[] s_hits = new RaycastHit[16];

    /// <summary>The walkable ground surface straight below a world point; NPC and participant colliders are never ground. False when none is found.</summary>
    public bool TryGroundY(Vector3 from, out float y)
    {
        y = 0f;
        if (GroundMask == 0 || !ViewFrameMath.IsFinite(from)) return false;
        int n = Physics.RaycastNonAlloc(from, Vector3.down, s_hits, GroundRayMeters, GroundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider c = s_hits[i].collider;
            if (c == null) continue;
            if (c.GetComponentInParent<PedestrianAgent>() != null || c.gameObject.tag == "Npc") continue;   // an NPC standing below is not the ground
            if (View != null && c.transform.IsChildOf(View.root)) continue;                              // the participant's own capsule / body is not the ground
            if (s_hits[i].distance < best) { best = s_hits[i].distance; y = s_hits[i].point.y; found = true; }
        }
        return found;
    }

    public bool Read(out ViewFrameSample s)
    {
        s = default;
        if (!Resolved) return false;
        Vector3 vp = View.position, cp = CentreEye.position, lp = LeftEye.position, rp = RightEye.position;
        if (!ViewFrameMath.IsFinite(vp) || !ViewFrameMath.IsFinite(cp) || !ViewFrameMath.IsFinite(lp) || !ViewFrameMath.IsFinite(rp)) return false;
        Quaternion vr = View.rotation, cr = CentreEye.rotation;
        s.valid = true;
        s.hmdPos = vp; s.hmdRot = vr; s.centreEyePos = cp;
        s.leftRigWorld = lp; s.rightRigWorld = rp;
        s.leftLocal = ViewFrameMath.HeadLocal(cp, cr, lp); s.rightLocal = ViewFrameMath.HeadLocal(cp, cr, rp);
        s.leftWorld = ViewFrameMath.EyeInView(vp, vr, s.leftLocal); s.rightWorld = ViewFrameMath.EyeInView(vp, vr, s.rightLocal);
        s.viewMinusCentre = vp - cp;
        s.groundOk = TryGroundY(vp, out s.groundY);
        return true;
    }

    /// <summary>Reads the live XR Origin facts (guarded: any failure leaves the field unknown).</summary>
    public XrOriginFacts ReadXrFacts()
    {
        var f = new XrOriginFacts { RequestedMode = "", CurrentMode = "", OffsetObjectPath = "", ViewCameraPath = PathOf(View), CentreEyePath = PathOf(CentreEye) };
        try
        {
            Unity.XR.CoreUtils.XROrigin xo = View != null ? View.GetComponentInParent<Unity.XR.CoreUtils.XROrigin>() : null;
            if (xo == null) xo = UnityEngine.Object.FindObjectOfType<Unity.XR.CoreUtils.XROrigin>();
            if (xo != null)
            {
                f.OriginPresent = true;
                f.RequestedMode = xo.RequestedTrackingOriginMode.ToString();
                f.CurrentMode = xo.CurrentTrackingOriginMode.ToString();
                f.CameraYOffsetProperty = xo.CameraYOffset;
                if (xo.CameraFloorOffsetObject != null) { f.HasOffsetObject = true; f.OffsetObjectLocalY = xo.CameraFloorOffsetObject.transform.localPosition.y; f.OffsetObjectPath = PathOf(xo.CameraFloorOffsetObject.transform); }
            }
        }
        catch (Exception) { /* unknown on this runtime */ }
        return f;
    }

    static string PathOf(Transform t)
    {
        if (t == null) return "";
        string p = t.name; for (Transform q = t.parent; q != null; q = q.parent) p = q.name + "/" + p;
        return p;
    }
}

/// <summary>Manifest block (eye_tracking.view_frame): the live coordinate facts a reader needs to interpret every height in the data. Evidence only; nothing here is an assumed participant measurement.</summary>
[Serializable]
public class ViewFrameManifest
{
    public bool captured;
    public long samples, ground_samples, ground_unavailable_samples;
    public string view_camera = "", ovr_centre_eye = "";
    public bool xr_origin_present;
    public string xr_origin_requested_mode = "", xr_origin_current_mode = "";
    public float xr_origin_camera_y_offset_property_m;       // the configured number (XROrigin.CameraYOffset)
    public bool camera_floor_offset_object_present;
    public float camera_floor_offset_local_y_m;              // what the Camera Floor Offset object actually holds right now
    public string camera_floor_offset_object = "";
    public string ovr_manager_tracking_origin_type = "";     // OVRManager.trackingOriginType as the running OVRPlugin reports it ("" = unavailable)
    public float[] view_minus_ovr_centre_eye_mean_m = new float[0], view_minus_ovr_centre_eye_std_m = new float[0];   // measured world offset between the rendered camera and the OVR centre eye
    public string eye_height_source = "unknown";             // see EyeHeightSource
    public bool eye_height_is_participant_specific;          // true only for runtime_floor_referenced
    public double hmd_height_above_ground_first_m = -1, hmd_height_above_ground_min_m = -1, hmd_height_above_ground_max_m = -1, hmd_height_above_ground_mean_m = -1;
    public string ground_reference = "AutoGroundAlign ground layer, nearest non-NPC non-participant surface straight below the rendered camera";
    public string participant_height_calibration = "none";   // no participant-specific calibration is implemented; see docs/gaze_origin_verification.md section 8
    public string note = "Heights are world Y minus ground Y. A fixed_camera_y_offset source means every participant is rendered at the same configured offset above the XR Origin base (plus the pose relative to the recentre point); it is a scene constant, not a measurement.";
}
