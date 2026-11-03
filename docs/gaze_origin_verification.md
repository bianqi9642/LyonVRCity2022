# Gaze origin: root cause, the project-side fix, and Quest Pro verification

Status: **fix implemented and covered by synthetic-transform tests only. Not verified on a Quest Pro (or any headset).** The cause below is derived from the installed SDK source and the
Windows Editor dataset `20261103T131517Z_a1a6fbd81fc6`; it has not been reproduced on hardware.

## 1. What was observed

In every one of the 982 gaze rows: `combined_origin = head_pos + (0, +1.361, 0)` in world axes (23 distinct 3-mm variants), constant over 90 s, not following head yaw (up to 180 deg) or pitch
(up to 25 deg). Directions were plausible (eye-in-head angular speed looked like real saccades). `trajectory.csv` shows `hmd_local_pos_y = -0.08 .. -0.20 m` (the rig runs **EyeLevel**:
`OVRCameraRig.prefab` -> `OVRManager._trackingOriginType: 0`).

## 2. Pipeline (every stage)

| Stage | Space | Source |
|---|---|---|
| `OVRPlugin.GetEyeGazesState(...).EyeGazes[eye].Pose` | OVRPlugin tracking space (the eye pose; its origin type is **not documented** in the SDK) | OVRPlugin |
| head reference used by the SDK conversion | the rig's tracking space: `OVRPlugin.GetNodePose(Head)` when `OVRManager.loadedXRDevice == Oculus`, otherwise the Unity XR head node (`OVRCommon.cs:830-935`) | OVRPlugin / Unity XR |
| `pose.ToWorldSpacePose(Camera.main)` (`OVREyeGaze.cs:172-186`, `OVRCommon.cs:119-134`) | `world = camera.localToWorld * (headRef^-1 * eyePose)` | SDK |
| `OVREyeGaze.transform.position / rotation` | world | SDK writes it in `Update` (order -80) |
| `GazeRecorder.ReadEye` | world origin = that transform, direction = its forward | project |
| `Physics.Raycast(origin, direction)` -> AOI -> `gaze.csv` | world | project |

## 3. Root cause (derived)

`camera.localToWorld = trackingToWorld * headNode` (OVRCameraRig maps the head node through the tracking space). Substituting into the SDK formula gives
`world = trackingToWorld * eyePose` - the head node cancels exactly. So the world eye position is correct **only if `eyePose` is in the same tracking origin as the rig**. If the eye pose is
floor-referenced while the rig is EyeLevel, the result is `trackingToWorld * (trueEye + (0, H, 0))`: a pure vertical translation `H` between the two origins, rotated only by the rig's yaw -
hence a constant **world-vertical** offset that ignores head yaw and pitch (the head rotation cancels). Rotation (gaze direction) is unaffected by a translation, which is why directions
were sane. `H = 1.361 m` is consistent with a head height above the floor. Reproduced mathematically in `EyeGazeOriginMathTests.SdkConversion_WithTwoTrackingOrigins_...`.

What is **not** established: that the eye pose is floor-referenced on Android Standalone (the dataset is Windows Editor / `OpenXR Display`); which OVRPlugin/OpenXR path chooses that origin.

## 4. The fix (project side; SDK and packages untouched; no height constant)

`EyeGazeOrigin` (Assets/Scripts/Capture/EyeGazeOrigin.cs) + `GazeRecorder.ReadEye`:

* each eye's world origin is **validated against the rig's own eye anchor** (`OVRCameraRig.leftEyeAnchor` / `rightEyeAnchor`, auto-resolved, overridable in the Inspector);
* if `|provider - anchor| <= maxProviderOriginDeviationMeters` (default 0.12 m, a plausibility gate - real pupil vs eye-node differences are centimetres) the provider origin is used **unchanged**;
* otherwise (tracking-origin mismatch, NaN) that eye's origin is the rig anchor's world position - the actual per-eye position the rig renders from - not the provider origin shifted by a constant;
* left / right origins stay independent; combined origin is the mean of the valid eyes' (resolved) origins, as before; direction, per-eye validity, confidence and head fallback are unchanged;
* `gaze.csv` appends `gaze_origin_source` (`provider | eye_anchor | provider_unchecked | mixed`, blank for the head-fallback ray); the manifest `eye_tracking` block adds `origin_*`
  (counts per source, max / mean deviation, mean provider-minus-anchor offset in world axes). A constant non-zero `origin_mean_offset_y_m` is the signature of this mismatch.

Why not fix the SDK: `Library/PackageCache` must not be edited. Why not change `OVRManager.trackingOriginType` to FloorLevel: it would move the participant's eye height in the scene (a
change of the experimental stimulus) and cannot be validated without hardware; it is the alternative to evaluate in step 5 below.

## 5. Quest Pro verification procedure (required before use)

Run once in **Android Standalone** and once through **Quest Link**, eye tracking enabled in the headset settings, with a debug build; no participant data.

1. Sit upright, look straight ahead at a known building corner for 10 s, then move the head only (yaw +45 / -45, pitch up / down), then move only the eyes.
2. Open `session_manifest.json` -> `eye_tracking`: `origin_samples_checked > 0`. **Expected after the fix, if the provider origin is consistent:** `origin_samples_provider` ~ all,
   `origin_deviation_max_m < 0.05`, `origin_mean_offset_*` ~ 0. **If the mismatch exists:** `origin_samples_eye_anchor_fallback` ~ all and `origin_mean_offset_y_m` ~ +1.36 (this tells you the provider origin is NOT in the rig frame on that runtime).
3. In `gaze.csv`: `|combined_origin - head_pos|` must be about 0.0-0.08 m on every row and must rotate with the head (check with yaw +-45); `gaze_origin_source` values as above.
4. Direction check: fixate a building corner; the ray should reach it. With the eyes moving and the head still, `ray_dir_*` must change while `head_rot_*` does not.
5. Record `OVRManager.trackingOriginType` and `OVRPlugin.GetTrackingOriginType()` in the same session. If the fallback is active, test (separately, as an explicit design decision) whether FloorLevel makes the provider origin consistent.
6. Do not claim the physical gaze origin is corrected until steps 2-4 pass on the headset.
