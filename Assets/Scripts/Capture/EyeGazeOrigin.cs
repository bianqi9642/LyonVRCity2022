// EyeGazeOrigin.cs
// World-space origin of an eye-gaze ray: validation of the provider origin against the rig's own eye position, and the explicit model of the SDK conversion used to explain / test it.
// Pure math (no scene access), so it is unit-testable with synthetic transforms.
//
// WHY THIS EXISTS (root cause of the +1.361 m origin offset seen in the Windows Editor dataset 20261103T131517Z_a1a6fbd81fc6)
//   OVREyeGaze (TrackingMode = WorldSpace) computes   world = Camera.main.localToWorld * ( headPose^-1 * eyePose ).
//     eyePose  = OVRPlugin.GetEyeGazesState(...).Pose            (OVRPlugin tracking space)
//     headPose = OVRPlugin.GetNodePose(Head) or the Unity XR head node (OVRCommon.ToHeadSpacePose), i.e. the SAME tracking origin the OVRCameraRig uses for its anchors.
//   The shipped OVRCameraRig prefab is EyeLevel (OVRManager._trackingOriginType = 0): the head node is ~0 m high in that space and the camera sits at the rig origin
//   (hmd_local_pos_y = -0.08..-0.20 m in trajectory.csv). The eye-gaze pose evidently was NOT expressed in that same origin: the recorded origin minus the camera was
//   (0, 1.361, 0) on every row, i.e. a pure VERTICAL translation between two tracking origins (floor-referenced eye pose vs eye-level head pose). Because the camera
//   rotation equals rig * headRotation, the head rotation cancels in  cameraRot * headRot^-1 * (eyePos - headPos)  and the residual stays a constant WORLD-vertical vector,
//   which is exactly why it did not follow head yaw or pitch. Rotation (gaze DIRECTION) is unaffected by a translation between origins, which is why directions were sane.
//   This is derived from the SDK source and the dataset; it has NOT been reproduced on Android / Quest Pro hardware (see docs/gaze_origin_verification.md).
//
// CORRECTION (schema v13, scene audit): the "two tracking origins" hypothesis below was NOT the cause. The +1.36144 m is the XR Origin's Camera Floor Offset (XROrigin.CameraYOffset): OVREyeGaze converts
//   with Camera.main = the XRI Main Camera (child of that offset), the OVR anchors are not - see ViewFrame.cs. The anchors are now re-applied at the rendered view before validation and fallback.
//   The tracking-origin probes added with v12 stay as hardware evidence.
// FOLLOW-UP (schema v12, G1 verification; SUPERSEDED, kept for the record): a more specific candidate mechanism was found in the installed SDK (OVRManager.trackingOriginType setter, OVRManager.cs ~1850-1895). With the
//   OpenXR loader (OVRPlugin.UnityOpenXR.Enabled) the setter maps EyeLevel -> Unity XRInputSubsystem.TrackingOriginMode.Device and RETURNS without calling OVRPlugin.SetTrackingOriginType.
//   Unity-side poses (head / eye anchors, the camera) therefore live in an eye-level (Device) origin, while OVRPlugin - the source of OVRPlugin.GetEyeGazesState - keeps its OWN tracking
//   origin, which this project never changes. If that origin is floor-referenced, the offset between the two is the floor-to-eye-level height at recenter: a constant world-vertical vector,
//   exactly the observed signature. This is a code-level hypothesis: the native OVRPlugin binary is not inspectable, so which base space the eye-gaze pose uses is NOT proven. The recorder
//   now records the facts that confirm or refute it on hardware (TrackingSpaceStats / manifest eye_tracking.tracking_space, offset statistics in world, rig and head frames).
//   The eye-anchor fallback is unchanged and the raw provider origin is NOT switched to.
//
// WHAT THE RECORDER DOES (no hard-coded height, no SDK edit)
//   A provider origin is accepted only if it lies within a physical bound of the rig's own eye anchor for that eye (LeftEyeAnchor / RightEyeAnchor world position). A real
//   pupil position and the HMD's eye node differ by centimetres. When the provider origin is farther than that bound (a tracking-origin mismatch, NaN, ...), the eye's origin
//   is taken from the rig eye anchor, which is the actual per-eye position the rig renders from, and the row says so (origin source column + manifest counters).
//   The bound is a plausibility gate; it is not added to or subtracted from any coordinate.
// All comments in English per project convention.

using UnityEngine;

// EyeAnchor = the OVR rig anchor itself (rig frame, no Camera Offset); EyeAnchorView = the anchor re-applied at the RENDERED view (schema v13, see ViewFrame.cs).
public enum EyeOriginSource { Provider = 0, EyeAnchor = 1, ProviderUnchecked = 2, EyeAnchorView = 3 }

public static class EyeGazeOrigin
{
    /// <summary>Default plausibility bound: a gaze origin farther than this from the rig's eye position cannot be that eye (head-hardware scale: IPD/2 is ~0.032 m, pupil vs eye-node offsets are centimetres).</summary>
    public const float DefaultMaxDeviationMeters = 0.12f;

    public static string Name(EyeOriginSource s)
    {
        switch (s)
        {
            case EyeOriginSource.Provider: return "provider";
            case EyeOriginSource.EyeAnchor: return "eye_anchor";
            case EyeOriginSource.EyeAnchorView: return "eye_anchor_view";
            default: return "provider_unchecked";
        }
    }

    /// <summary>
    /// Chooses the world-space origin of ONE eye. providerWorld = the OVREyeGaze transform position; anchorWorld = the rig's eye anchor position for the same eye (null = no anchor
    /// available, the provider origin is passed through unchecked). deviation = |provider - anchor| (NaN when unchecked, +Infinity when the provider origin is not finite).
    /// </summary>
    public static EyeOriginSource Resolve(Vector3 providerWorld, Vector3? anchorWorld, float maxDeviationMeters, out Vector3 origin, out float deviation)
    {
        if (!anchorWorld.HasValue || !IsFinite(anchorWorld.Value))
        {
            origin = providerWorld; deviation = float.NaN;
            return EyeOriginSource.ProviderUnchecked;
        }
        Vector3 anchor = anchorWorld.Value;
        if (!IsFinite(providerWorld))
        {
            origin = anchor; deviation = float.PositiveInfinity;
            return EyeOriginSource.EyeAnchor;
        }
        deviation = (providerWorld - anchor).magnitude;
        if (deviation <= maxDeviationMeters) { origin = providerWorld; return EyeOriginSource.Provider; }
        origin = anchor;
        return EyeOriginSource.EyeAnchor;
    }

    /// <summary>The combined row's origin source: the common source of the contributing eyes, or "mixed" when two valid eyes disagree.</summary>
    public static string CombinedName(bool leftValid, EyeOriginSource left, bool rightValid, EyeOriginSource right)
    {
        if (leftValid && rightValid) return left == right ? Name(left) : "mixed";
        if (leftValid) return Name(left);
        if (rightValid) return Name(right);
        return "";   // head fallback ray: no eye origin involved
    }

    public static bool IsFinite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

    /// <summary>
    /// The SDK's WorldSpace conversion (OVRCommon.ToWorldSpacePose): world = cameraToWorld * (headRef^-1 * eyePose), with eyePose and headRef both given in "tracking space".
    /// Used by the tests to reproduce the offset from synthetic transforms; production code never calls it.
    /// </summary>
    public static Vector3 SdkWorldSpaceOrigin(Matrix4x4 cameraLocalToWorld, Vector3 headRefPos, Quaternion headRefRot, Vector3 eyePosTracking)
    {
        Vector3 inHead = Quaternion.Inverse(headRefRot) * (eyePosTracking - headRefPos);
        return cameraLocalToWorld.MultiplyPoint3x4(inHead);
    }

    /// <summary>Running mean / standard deviation of a 3-vector (Welford; bounded memory).</summary>
    public sealed class Welford3
    {
        public long N;
        double _mx, _my, _mz, _m2x, _m2y, _m2z;
        public void Add(Vector3 v)
        {
            N++;
            double dx = v.x - _mx; _mx += dx / N; _m2x += dx * (v.x - _mx);
            double dy = v.y - _my; _my += dy / N; _m2y += dy * (v.y - _my);
            double dz = v.z - _mz; _mz += dz / N; _m2z += dz * (v.z - _mz);
        }
        public Vector3 Mean => N > 0 ? new Vector3((float)_mx, (float)_my, (float)_mz) : Vector3.zero;
        public Vector3 Std => N > 1 ? new Vector3((float)System.Math.Sqrt(_m2x / (N - 1)), (float)System.Math.Sqrt(_m2y / (N - 1)), (float)System.Math.Sqrt(_m2z / (N - 1))) : Vector3.zero;
    }

    /// <summary>
    /// Agreement of the two tracking origins that matter here: the one OVRPlugin reports (OVRPlugin.GetTrackingOriginType: EyeLevel / FloorLevel / Stage / Stationary) and Unity's XR input
    /// subsystem mode (Device / Floor / TrackingReference / Unknown). EyeLevel and Stationary correspond to Device; FloorLevel and Stage to Floor. "unknown" when either is not available.
    /// </summary>
    public static string TrackingOriginsRelation(string ovrPluginOrigin, string unityXrMode)
    {
        if (string.IsNullOrEmpty(ovrPluginOrigin) || string.IsNullOrEmpty(unityXrMode) || ovrPluginOrigin == "unavailable" || unityXrMode == "unavailable" || unityXrMode == "Unknown") return "unknown";
        bool ovrpDevice = ovrPluginOrigin == "EyeLevel" || ovrPluginOrigin == "Stationary";
        bool ovrpFloor = ovrPluginOrigin == "FloorLevel" || ovrPluginOrigin == "Stage";
        bool unityDevice = unityXrMode == "Device", unityFloor = unityXrMode == "Floor";
        if ((ovrpDevice && unityDevice) || (ovrpFloor && unityFloor)) return "same_kind";
        if ((ovrpDevice && unityFloor) || (ovrpFloor && unityDevice)) return "differ";
        return "unknown";
    }

    /// <summary>One low-rate reading of the facts TrackingSpaceStats records (separated from the Unity / OVR API so tests can supply synthetic readings).</summary>
    public struct TrackingSpaceFacts
    {
        public bool OvrPluginInitialized, OpenXrActive;
        public string OvrPluginOrigin, UnityXrMode, LoadedXrDevice, NativeXrApi;
        public double? OvrPluginHeadY, UnityHeadY;
    }

    /// <summary>
    /// Evidence about the two tracking origins, gathered at a low rate (not per sample): the HEAD node height reported by OVRPlugin (its tracking origin) minus the same head node as Unity XR
    /// reports it (the origin the rig / camera / eye anchors use). The head is a different signal from the eye-gaze one, so a delta that equals the provider-minus-anchor offset shows the
    /// cause is the two origins, independent of the eye-gaze binary. Pure bookkeeping; the facts are read by GazeRecorder.
    /// </summary>
    public sealed class TrackingSpaceStats
    {
        public long Probes, ProbesWithBothHeads;
        public string OvrPluginOrigin = "", UnityXrMode = "", LoadedXrDevice = "", NativeXrApi = "";
        public bool OvrPluginInitialized, OpenXrActive;
        public readonly Welford3 HeadDelta = new Welford3();   // x = unused (0), y = ovrplugin head y - unity xr head y, z = unused (0): only the vertical delta is meaningful across the handedness conversion
        public double LastOvrPluginHeadY = double.NaN, LastUnityHeadY = double.NaN;
        public string Relation => TrackingOriginsRelation(OvrPluginOrigin, UnityXrMode);

        public void Note(TrackingSpaceFacts f) { Note(f.OvrPluginInitialized, f.OvrPluginOrigin, f.UnityXrMode, f.OpenXrActive, f.LoadedXrDevice, f.NativeXrApi, f.OvrPluginHeadY, f.UnityHeadY); }

        public void Note(bool ovrPluginInitialized, string ovrPluginOrigin, string unityXrMode, bool openXrActive, string loadedXrDevice, string nativeXrApi, double? ovrPluginHeadY, double? unityHeadY)
        {
            Probes++;
            OvrPluginInitialized = ovrPluginInitialized; OvrPluginOrigin = ovrPluginOrigin ?? ""; UnityXrMode = unityXrMode ?? ""; OpenXrActive = openXrActive;
            LoadedXrDevice = loadedXrDevice ?? ""; NativeXrApi = nativeXrApi ?? "";
            if (ovrPluginHeadY.HasValue) LastOvrPluginHeadY = ovrPluginHeadY.Value;
            if (unityHeadY.HasValue) LastUnityHeadY = unityHeadY.Value;
            if (ovrPluginHeadY.HasValue && unityHeadY.HasValue && !double.IsNaN(ovrPluginHeadY.Value) && !double.IsNaN(unityHeadY.Value))
            {
                ProbesWithBothHeads++;
                HeadDelta.Add(new Vector3(0f, (float)(ovrPluginHeadY.Value - unityHeadY.Value), 0f));
            }
        }
    }

    /// <summary>Running summary of the provider-vs-anchor deviation, written to the manifest (bounded: a handful of numbers, no per-sample logging).</summary>
    public sealed class DeviationStats
    {
        public long Checked, Provider, AnchorFallback, Unchecked;
        public double MaxDeviation, SumDeviation;
        public double SumDx, SumDy, SumDz;   // provider - anchor, world axes, over rows where the deviation was finite
        public long FiniteCount;
        // The same provider-minus-anchor vector expressed in three frames (mean / std). A constant vector in WORLD (or rig) axes that does not follow the head is a tracking-origin offset; one that is
        // constant in HEAD axes is a head-model offset. Only filled when the frames are supplied (NoteInFrames).
        public readonly Welford3 OffsetWorld = new Welford3(), OffsetRig = new Welford3(), OffsetHead = new Welford3();
        public bool FramesRecorded => OffsetHead.N > 0;

        /// <summary>Adds the frame-resolved offset for one valid sample (provider - anchor, finite only). headRotation / rigRotation are world rotations of the head (camera) and of the rig tracking space.</summary>
        public void NoteInFrames(Vector3 providerWorld, Vector3 anchorWorld, Quaternion headRotation, Quaternion rigRotation)
        {
            if (!IsFinite(providerWorld) || !IsFinite(anchorWorld)) return;
            Vector3 d = providerWorld - anchorWorld;
            OffsetWorld.Add(d);
            OffsetRig.Add(Quaternion.Inverse(rigRotation) * d);
            OffsetHead.Add(Quaternion.Inverse(headRotation) * d);
        }

        public void Note(EyeOriginSource source, Vector3 providerWorld, Vector3? anchorWorld, float deviation)
        {
            if (source == EyeOriginSource.ProviderUnchecked) { Unchecked++; return; }
            Checked++;
            if (source == EyeOriginSource.Provider) Provider++; else AnchorFallback++;
            if (!float.IsInfinity(deviation) && !float.IsNaN(deviation) && anchorWorld.HasValue)
            {
                FiniteCount++;
                SumDeviation += deviation;
                if (deviation > MaxDeviation) MaxDeviation = deviation;
                Vector3 d = providerWorld - anchorWorld.Value;
                SumDx += d.x; SumDy += d.y; SumDz += d.z;
            }
        }

        public double MeanDeviation => FiniteCount > 0 ? SumDeviation / FiniteCount : -1.0;
        public Vector3 MeanOffset => FiniteCount > 0 ? new Vector3((float)(SumDx / FiniteCount), (float)(SumDy / FiniteCount), (float)(SumDz / FiniteCount)) : Vector3.zero;
    }
}
