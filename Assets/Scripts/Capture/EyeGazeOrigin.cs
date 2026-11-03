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
// WHAT THE RECORDER DOES (no hard-coded height, no SDK edit)
//   A provider origin is accepted only if it lies within a physical bound of the rig's own eye anchor for that eye (LeftEyeAnchor / RightEyeAnchor world position). A real
//   pupil position and the HMD's eye node differ by centimetres. When the provider origin is farther than that bound (a tracking-origin mismatch, NaN, ...), the eye's origin
//   is taken from the rig eye anchor, which is the actual per-eye position the rig renders from, and the row says so (origin source column + manifest counters).
//   The bound is a plausibility gate; it is not added to or subtracted from any coordinate.
// All comments in English per project convention.

using UnityEngine;

public enum EyeOriginSource { Provider = 0, EyeAnchor = 1, ProviderUnchecked = 2 }

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

    /// <summary>Running summary of the provider-vs-anchor deviation, written to the manifest (bounded: a handful of numbers, no per-sample logging).</summary>
    public sealed class DeviationStats
    {
        public long Checked, Provider, AnchorFallback, Unchecked;
        public double MaxDeviation, SumDeviation;
        public double SumDx, SumDy, SumDz;   // provider - anchor, world axes, over rows where the deviation was finite
        public long FiniteCount;

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
