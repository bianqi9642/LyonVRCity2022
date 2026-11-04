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

## 7. Follow-up (schema v12): verification of the coordinate relationships, and what remains open

Scope: static analysis of the installed SDK (`com.meta.xr.sdk.core@201.0.0`), the project's rig, and the two Windows Editor datasets `20261103T171815Z_645a9da83306` (Part 3) and `20261103T172020Z_11db1ebeabbf` (Part 2). **No code path that produces a recorded gaze origin or direction was changed. The eye-anchor fallback is kept; the raw provider origin is not used. G1 is NOT fully fixed.**

### 7.1 Two sources, kept separate
* **Source A - the rig eye anchors** (`OVRCameraRig.leftEyeAnchor/rightEyeAnchor`). `OVRCameraRig.UpdateAnchors` sets their local poses from `OVRNodeStateProperties` (`Node.LeftEye/RightEye`, i.e. Unity XR `XRNode.LeftEye/RightEye` under the OpenXR loader) in the **tracking space of the rig**; the camera is the head node mapped through the same tracking space. They are the per-eye *view* poses the runtime reports for rendering (IPD-based), not tracked pupils. Internally consistent with the head / camera by construction.
* **Source B - the provider (OVREyeGaze)**: `OVRPlugin.GetEyeGazesState` pose -> `ToWorldSpacePose(Camera.main)` = `camera.localToWorld * (headRef^-1 * eyePose)` (`OVRCommon.cs`); `headRef` = Unity XR `XRNode.Head` (OpenXR) in Unity's tracking origin. The camera is `trackingToWorld * headNode`, so `world = trackingToWorld * eyePose`: the result is right **only if `eyePose` is in the same tracking origin as Unity XR / the rig**.

### 7.2 Candidate mechanism found in the SDK source (SUPERSEDED by section 8: the offset is the XR Origin's Camera Floor Offset, not an OVRPlugin-vs-Unity origin mismatch)
`OVRManager.trackingOriginType` setter (`OVRManager.cs` ~1850-1895): with the OpenXR loader (`OVRPlugin.UnityOpenXR.Enabled`) `EyeLevel` is applied as Unity `XRInputSubsystem.TrackingOriginMode.Device` and the setter **returns without calling `OVRPlugin.SetTrackingOriginType`**; the getter reads `OVRPlugin.GetTrackingOriginType()` when an HMD is present, so the "requested != actual" check in `OVRManager.Update` stays true. Unity-side poses (head, eye anchors, camera) are therefore eye-level (Device) while OVRPlugin keeps its own tracking origin, which this project never sets. If that origin is floor-referenced, provider-minus-anchor is the floor-to-eye-level height at recenter: a constant vector along world up - exactly what was recorded (+1.3614 m, x/z ~ 0). **Not proven:** which base space the native eye-gaze query uses cannot be read from the managed SDK, and Windows Editor (`OpenXR Display`) is not Android Standalone.

### 7.3 What the recorded data say (calculated, Windows Editor, OpenXR Display)
| Quantity | Value | Frame | Reading |
|---|---:|---|---|
| Left / right anchor vs head (Part 3 1257 rows, Part 2 1228 rows) | x = -33.68 / +33.69 mm (sd 0.35), y = 0.00 mm (sd 0.06), z = 0.01 mm (sd 0.2) | head | The runtime's head pose is the view-space origin between the eyes, so eyes at +-IPD/2 with ~0 vertical is what the definition predicts - plausible, **not** evidence of alignment with the physical eyes |
| Anchor separation (IPD) | 67.37 mm (sd 0.3) | world / head | The runtime's IPD setting; plausible |
| Provider minus anchor, mean | (~0, +1.3614, ~0) m, max deviation ~1.41 m | world | pure vertical translation; no horizontal component |
| Offset direction (from the v11 manifest counters) | mean deviation 1.361441 m vs mean-offset magnitude 1.361441 m (difference ~6e-7 m, Part 2 and Part 3); mean x / z = 0.04 / 0.01 mm | world | the mean of the vector's length equals the length of its mean, so its DIRECTION is fixed in world axes while the head turned through 360 deg: a world-fixed (tracking-origin) offset, not a head-fixed one (a head-fixed 1.36 m vector would average out to ~0). The 1.41 m maximum is a few transient frames |
| Offset std per frame (v12) | recorded as `origin_offset_{world,rig,head}_std_m` from v12 on | world / rig / head | constant in world/rig axes but varying in head axes = tracking-origin offset (the two analysed sessions pre-date these fields) |
| Combined gaze direction in head axes | yaw mean -2.0 deg (sd 6.8-6.9), pitch mean -5.7 to -6.5 deg; range +-30 deg | head | eyes-in-head within a physical range; head-frame yaw stays near 0 in every 30-deg bucket of rig yaw (0-360), so the direction is **not** rotated by the rig's yaw |
| Direction normalisation | unit length on every row | world | confirmed |
| Left-right direction angle | ~3.08 deg, almost constant | world | little vergence signal on this runtime |

### 7.4 What can and cannot be concluded
* **Internal consistency of the rig transforms:** established (anchors and camera share one tracking space; consistent-origin synthetic tests give provider == anchor under head translation / rotation, and IPD is preserved).
* **Geometric plausibility of the anchors:** established (+-33.7 mm, ~0 vertical, unit-consistent).
* **Alignment of the anchors with the participant's physical eyes:** NOT established by any available data; the anchors are the runtime's view poses. A plausible IPD does not prove it.
* **Accuracy of the raw provider origin:** NOT established; it is offset by the tracking-origin difference on this runtime and is not used.
* **Gaze direction:** consistent with the head in one world frame (above); whether it lands on what the participant looked at requires a fixation test on hardware.
* **Adequacy for raycasting / AOI:** the ray starts at the rig's eye position (cm-level plausible), direction is in the right frame; the absolute accuracy of the direction is the tracker's and is not validated here.

### 7.5 Evidence the recorder now collects (manifest `eye_tracking`, schema v12)
`origin_offset_{world,rig,head}_{mean,std}_m`, and `tracking_space`: `ovrplugin_tracking_origin` (`OVRPlugin.GetTrackingOriginType`), `unity_xr_tracking_origin_mode` (`XRInputSubsystem.GetTrackingOriginMode`), `tracking_origins_relation`, `loaded_xr_device`, `native_xr_api`, and `head_height_delta_*` = OVRPlugin head y minus Unity XR head y (about every 2 s). **Decision rule for the headset session:** if `tracking_origins_relation = differ` and `head_height_delta_mean_m` ~ `origin_offset_world_mean_m[1]` (both ~1.36 m), the cause is two tracking origins (a generic property of the stack, not a gaze-specific fault) and the next step is a deliberate design decision on how to align them (e.g. make OVRPlugin's origin match Unity's, or the reverse) - not a constant. If `same_kind` and the delta is ~0 while the offset persists, the eye-gaze pose uses a different base space than the head and the anchor fallback must stay. Probes are guarded and report `unavailable` where OVRPlugin is not initialised (Editor without a headset).

### 7.6 Hardware tests still required (Quest Pro, Android Standalone and Quest Link)
1. A recording with v12 and check 7.5's decision rule.
2. Fixation targets at known building corners, head still, eyes moving, then head moving: ray hit-point error.
3. Eye-level vs floor-level behaviour after a recenter (the offset should change by exactly the recentre height change if the mechanism above is right).
4. Confirm the Android manifest eye-tracking permission and that provider confidence varies (constant 0.5 was seen on the Editor runtime).

## 8. Scene audit (schema v13): what 1.36144 m is, and the corrected coordinate frames

This section supersedes the explanation in 7.2. Evidence: a read-only dump of the serialized scene `LyonVRVity35.unity` (opened in a CLONE of the project, nothing in the repository was touched), the installed packages and the two recorded sessions.

### 8.1 What the value is
**1.36144 m is Unity's stock `XROrigin.CameraYOffset` default**, stored as the local Y of the XR Origin's **Camera Floor Offset** ("Camera Offset") in the scene's `XR Interaction Setup/XR Origin (XR Rig)` (also the stock `CharacterController.m_Height` of that prefab, `XRRig.prefab`, `CameraOffset.k_DefaultCameraYOffset`). It is a configured scene constant, **not** a measurement of any participant: it is neither a tracked HMD pose, nor a provider coordinate, nor a calibration value. The audited offset (provider origin minus OVR eye anchor) was 1.361441 m (Part 2) / 1.361522 m (Part 3), i.e. the constant to 1e-6 .. 1e-4 m.

### 8.2 Why the provider origin differs from the anchors
The scene has two tracked rigs under ONE XR Origin base (`ExperimentManager.playerRig`, put on the terrain by `AutoGroundAlign`, heightOffset 0):
* **XRI rig:** `Camera Offset` (local Y = 1.36144, written/kept by `XROrigin` in Device mode) -> `Main Camera` (enabled, tag MainCamera, `TrackedPoseDriver`). It is the **only enabled camera tagged MainCamera** and the one rendered; `UIManager.hmdCamera`, `HeadLockedPanel` and `OVREyeGaze` (`TrackingMode = WorldSpace` -> `Camera.main`) use it.
* **OVRCameraRig** (`disableEyeAnchorCameras = true`, local position 0 under the base): CenterEye / LeftEye / RightEye anchors, controllers. No Camera Offset. `GazeRecorder.headTransform`, the trajectory `hmd_*` columns, the eye anchors and `NpcLineOfSight`'s participant target all read this tree.

`OVREyeGaze` computes `world = Camera.main.localToWorld * (headRef^-1 * eyePose)`, so the provider origin lies in the XRI frame (+ Camera Offset); the OVR anchors do not. **provider - anchor = Camera Offset Y**, and `Camera.main` (not an OVRPlugin tracking-origin mismatch) is the cause. Corroboration from the data: in both sessions the OVR centre eye sat 5.9 - 6.0 cm BELOW the XR Origin base (`hmd_local_pos_y` mean -0.059 / -0.060, sd 5 - 7 mm), so the rendered eye is at base + 1.36144 - 0.06 = **1.30 m above the ground for this (static) head**, while the recorded `hmd_pos` / `head_pos` / eye anchors were at ground level (slightly under it). The provider-minus-anchor spread was ~1 mm (perpendicular) and its mean x / z 0.04 / 0.01 mm: a rigid world-up vector, as a fixed Camera Offset predicts. The sub-millimetre agreement of the provider with the anchors (once the offset is removed) says these Windows Editor samples carry a head-anchored eye pose (no real pupil measurement); real Quest Pro pupils will differ from the view anchors by millimetres to centimetres.

### 8.3 Consequences for data recorded before v13 (v10 - v12)
* `combined_origin` / `ray_origin` with `gaze_origin_source = eye_anchor`, the head-fallback ray, `head_pos_*`, trajectory `hmd_*` and the controller poses are in the OVR frame, **about one Camera Floor Offset (1.36144 m) below the viewpoint the participant sees from** (ground level instead of eye level). Directions are unaffected (a translation does not rotate them). Hit points of rays cast from these origins and `los_clear_to_participant` (aimed at the OVR centre eye, i.e. at ground level) are therefore biased: the very low LOS-clear fractions (0.15 - 0.30 %) of the audited sessions are not interpretable as eye-level visibility. They are not rewritten; re-analysis can shift the origins by the recorded view offset only where `view_frame` evidence exists (v13+).
* Whether the headset session itself was mis-heighted for participants: yes in the scene as configured - see 8.5.

### 8.4 Coordinate hierarchy (serialized scene values; runtime head pose added on top)
| Object | Local Y (scene) | World Y (scene, base at the serialized 168.449) | Parent | Meaning |
|---|---:|---:|---|---|
| XR Origin base (`XR Origin (XR Rig)`, = `playerRig`) | 0 (world 168.449 at the serialized position; `AutoGroundAlign` and teleports set it at runtime) | 168.449 | `XR Interaction Setup` | maps tracking space into the city; sits ON the ground |
| Camera Offset (Camera Floor Offset) | **1.36144** | 169.810 | XR Origin base | the configured constant; `XROrigin` keeps it in Device mode, sets 0 in Floor mode |
| Main Camera (XRI, `Camera.main`) | 0 + `TrackedPoseDriver` pose (recorded OVR pose: -0.06) | 169.810 + pose | Camera Offset | what the participant sees; HMD / centre-eye reference |
| OVRCameraRig | 0 | 168.449 | XR Origin base | tracked tree WITHOUT the offset |
| CenterEyeAnchor / Left / Right EyeAnchor | pose set at runtime (recorded: head -0.06, eyes +-33.7 mm lateral, 0 vertical, 0 forward vs the head) | base + pose | TrackingSpace | head frame of the anchors |
Tracking origin: `OVRManager._trackingOriginType = Eye Level` (OpenXR: Unity input mode **Device**); `XROrigin.RequestedTrackingOriginMode = NotSpecified` (it follows whatever is active); `XROrigin.CameraYOffset = 1.36144`; the XR Origin components of the two other XRI instances in the scene are inactive. (A hierarchy dump showed `[missing script]` entries on the XR Origin root in the batch-mode clone; the project code (`HeadLockedPanel`, `GuardedTeleportationProvider`) and the recorded `xri_participant` teleports show the XR Origin component works at runtime, so this is treated as an artefact of the offline dump, not verified.)

### 8.5 Floor vs Device: decision
* **In Device (eye-level) mode the runtime pose is relative to the recentre point**, so participants of any height report ~0 and the rendered eye height is the Camera Offset constant: **the current scene gives every participant the same ~1.30 m eye height above the ground** (a standing 1.85 m participant and a seated 1.20 m one alike). That is the problem the audit suspected.
* **Floor mode (preferred, NOT applied):** `OVRManager` -> Tracking Origin Type = **Floor Level** makes the Unity input subsystem report Floor (`OVRManager.cs` ~1867-1880, `OpenXRSettings.SetAllowRecentering(true)`); `XROrigin` (NotSpecified) then adopts Floor and sets the Camera Offset to **0**, so the rendered height is the runtime's floor-relative HMD height (participant-specific) and the OVR anchors and the XRI camera coincide (offset 0). Nothing is added by the scene; no `OVRPlugin.SetTrackingOriginType` call is needed on the OpenXR path (the OVRManager setter routes through the Unity input subsystem). **It was not applied because it changes the stimulus for every participant and cannot be validated here:** no headset, the Windows Editor / Link runtime is not Android Standalone, and the floor reference on the Omnideck treadmill (platform height vs the guardian floor), `GuardedTeleportationProvider`'s camera-height maths and the recentre behaviour need a hardware check. This is a one-field scene change once the hardware test passes.
* **Option B (Device + participant calibration) is not implemented.** If Floor cannot be validated, the only defensible alternatives are an explicit measured HMD height per participant (recorded in the manifest with method, time and what it measures) or a controller-on-floor measurement (needs the controller model's tip offset). Neither was invented. `view_frame.participant_height_calibration = "none"` says so in every session.

### 8.6 Equations used by the v13 recorder (no height constant)
* rendered HMD / centre-eye: `hmd = Camera.main` world pose (`hmd_world_*`).
* head-local eye: `eyeLocal = inverse(centreEye.rot) * (anchor.pos - centreEye.pos)` (OVR anchors; `*_eye_head_local_*`; Y ~ 0, independent of any world height).
* eye in the view frame: `eyeWorld = hmd.pos + hmd.rot * eyeLocal` (`*_eye_world_*`); `provider ~ eyeWorld` by construction of `OVREyeGaze`.
* ground: `ground_y` = Y of the nearest non-NPC non-participant collider straight below the rendered camera on the `AutoGroundAlign` ground layer (blank when none within 12 m); `*_height_above_ground = world Y - ground_y`.
* provider validation/fallback now compares with and falls back to `eyeWorld` (`gaze_origin_source = eye_anchor_view`; the raw-anchor `eye_anchor` is used only when no view frame exists). The head-fallback ray and `los_*` use the rendered view.
* Manifest `eye_tracking.view_frame` (XR Origin requested/current mode, Camera Offset property and live local Y, OVRManager tracking origin, measured view-minus-OVR-centre-eye offset, `eye_height_source`, height statistics) and `origin_vs_rig_anchor_*` (provider minus the raw anchor: should read the Camera Offset in Device mode and ~0 in Floor mode).

### 8.7 Hardware verification (Quest Pro; Quest Link and Android Standalone separately)
1. Record with v13. Read `view_frame`: `xr_origin_current_mode` (Device now; Floor after the switch), `camera_floor_offset_local_y_m` (1.36144 now; 0 after), `eye_height_source`, `ovr_manager_tracking_origin_type`, `xr_origin_requested_mode`.
2. **Before the switch**, expect `view_minus_ovr_centre_eye_mean_m[1]` = the Camera Offset and a `hmd_height_above_ground` that is ~1.3 m for participants of different heights.
3. **Switch OVRManager to Floor Level**, restart. Expect `current_mode = Floor`, offset 0, `view_minus_ovr_centre_eye ~ 0`, `eye_height_source = runtime_floor_referenced`, `eye_height_is_participant_specific = true`, and `hmd_height_above_ground` at natural standing height that **differs between participants** (e.g. 1.5 / 1.7 / 1.85 m) and tracks the physical eye height measured with a tape (the relation to the physical floor/Omnideck platform must be checked: height above the platform, not the floor, if the guardian floor is the room floor).
4. Head movement: bend (height decreases by the physical amount), look up/down (height ~constant, rotation changes), turn (height constant); recentre (the height must NOT jump by the recentre offset in Floor mode).
5. Eye anchors: `*_eye_head_local_y` ~ 0, +-IPD/2 laterally (IPD per the headset setting); `*_eye_world_y ~ hmd_world_y`.
6. Provider: `origin_vs_rig_anchor_*` ~ 0 and `gaze_origin_source = provider` in Floor mode; if not, the provider and the rendered camera disagree on hardware and the anchor fallback is doing its job.
7. Known-target fixation: fixate building corners at known distances from several positions; the recorded ray must pass through them (hit point error).
8. Teleport / XRI teleport: heights above ground stay at the participant's height on terrain of different elevation.

## 9. Quest Pro eye-tracking architecture audit (schema v14)

Sources: the installed `com.meta.xr.sdk.core@201.0.0` (`OVREyeGaze.cs`, `OVRCameraRig.cs`, `OVRCommon.cs`, `OVRManager.cs`, `OVRPlugin.cs`, the `.meta` execution orders), `com.unity.xr.legacyinputhelpers@2.1.11` (`TrackedPoseDriver`), `com.unity.xr.core-utils@2.3.0` (`XROrigin`), the serialized scene (read-only clone) and the project scripts. Nothing below was verified on a headset.

### 9.1 Runtime data path (who does what)
| Object | Role | Pose source / space | Camera enabled | Used by |
|---|---|---|---|---|
| XRI `Main Camera` (under `Camera Offset`) | **renders**; `Camera.main` | Unity XR via `TrackedPoseDriver` (legacy, `UpdateAndBeforeRender`, order -30000); Unity's tracking origin + Camera Offset | yes | rendering, UI/panels, `OVREyeGaze` WorldSpace, v13 view frame, LOS target, head-fallback ray |
| `XROrigin` / base | maps tracking space to the city | scene transform; `AutoGroundAlign`, `TeleportPlayerTo`, XRI teleport | n/a | locomotion |
| `OVRManager` (on the rig) | initialises OVRPlugin, permissions, tracking origin | - | n/a | everything Meta; **required** for `OVREyeGaze` |
| `OVRCameraRig` | tracked anchors | `OVRNodeStateProperties`: **`OVRPlugin.GetNodePose` whenever `OVRPlugin.initialized`** (`loadedXRDevice = Oculus`, `OVRManager.SetCurrentXRDevice`), else Unity XR nodes; updated in `Update` (-50) and in `Application.onBeforeRender` | **no** (`disableEyeAnchorCameras`) | OVR tracking-space anchors, `UpdatedAnchors` |
| Center / Left / Right EyeAnchor | tracking-space head and per-eye **view** poses | as above | (cameras off) | trajectory `hmd_*`, `head_pos_*`, v13 head-local eyes |
| `OVREyeGaze` x2 (Left / Right, `EyeGazeProviders/*`, WorldSpace, parent identity) | **genuine per-eye gaze poses** | `OVRPlugin.GetEyeGazesState(Step.Render)`, order -80, confidence gate 0.5 | n/a | `GazeRecorder` |
| `GazeRecorder` | the single recorder | reads the two `OVREyeGaze` transforms, the anchors, the view camera; since v14 also `GetEyeGazesState` for the raw columns | n/a | `gaze.csv` |

**`disableEyeAnchorCameras` only switches `Camera.enabled`** (`OVRCameraRig.EnsureGameObjectIntegrity`, lines ~650-670); `UpdateAnchors` does not read it and keeps writing the anchors every `Update` (and in `onBeforeRender`). A test scans the installed source for this.

### 9.2 Is OVRCameraRig still necessary? Yes
It hosts `OVRManager` (OVRPlugin initialisation, eye-tracking permission/feature, the tracking-origin request) which `OVREyeGaze` needs, it supplies the tracking-space Center/Left/Right anchors that the recorders and the v13 view frame use, and the controller anchors. The XRI `Main Camera` alone cannot supply any of that, and `XRNode` / OpenXR "eye gaze interaction" gives one combined gaze pose, not Quest Pro's independent left/right poses. Nothing is removed. The OVR eye-anchor cameras are correctly off (the XRI camera renders); they are not a duplicate renderer.

### 9.3 What each quantity is (not interchangeable)
| Quantity | Source (SDK 201) | Meaning | Used for |
|---|---|---|---|
| XRI Main Camera | Unity XR TrackedPoseDriver + Camera Offset | rendered centre-eye / HMD reference | what the participant sees; placement of everything |
| CenterEyeAnchor | `OVRPlugin.GetNodePose(EyeCenter)` | OVR tracking-space centre-eye | head frame of the anchors |
| Left/RightEyeAnchor | `OVRPlugin.GetNodePose(EyeLeft/EyeRight)` | per-eye **view** pose of the runtime (IPD-dependent when `useIPDInPositionTracking`, true in the scene) | stereo geometry reference, fallback origin |
| Raw provider pose | `OVRPlugin.EyeGazeState.Pose` (`GetEyeGazesState`) | eye position + gaze direction (forward) in **OVRPlugin tracking space**, Unity axes after `ToOVRPose()` | the measurement |
| `OVREyeGaze` transform | provider pose through `TrackingMode` | WorldSpace: world position/rotation | recorded ray |
| Gaze origin / direction | the pose's position / forward | provider's eye position and gaze direction | gaze ray |
| Runtime user IPD | `OVRPlugin.ipd` (`ovrp_GetUserIPD`) | the IPD the runtime holds | context only |
| Physical pupil | - | **not measured**: the SDK documents no pupil / cornea / eye-centre definition for the pose | never claimed |

`EyeGazeState` = `Pose`, `Confidence`, `IsValid`; `EyeGazesState` adds `Time` (provider timestamp). Independent left and right poses: yes (two entries; origins differ per eye). Head-relative: no (tracking space). Calibration state: **not exposed** by OVRPlugin 201 (the Quest Pro system performs it); the application sees only validity and confidence.

### 9.4 Which `TrackingMode`: WorldSpace, one transformation
`OVREyeGaze.Update` (verified in source): `TrackingSpace` = raw pose; `HeadSpace` = `pose.ToHeadSpacePose()` = `headNode^-1 * pose`; `WorldSpace` = `pose.ToWorldSpacePose(Camera.main)` = `Camera.main.localToWorld * (headNode^-1 * pose)`. With OVRPlugin initialised, the head node and the eye pose are **both OVRPlugin tracking-space poses**, so `headNode^-1 * pose` (the eye relative to the head) is independent of the tracking origin, the Camera Offset and the world; it is then placed at the pose of the camera the participant sees. That is exactly "head-relative, then placed once at the rendered view". A manual route (`raw tracking pose` x some tracking-to-world matrix) would instead inherit any difference between OVRPlugin's tracking origin and Unity's (the OVRManager OpenXR setter only changes Unity's input mode, never OVRPlugin's), as the synthetic test shows (error = the mismatch). **Decision: keep `WorldSpace` as the single recorded path; do not mix it with raw / manual transforms.** Camera.main is the XRI camera, already updated for the frame when `OVREyeGaze` runs (TrackedPoseDriver -30000 < OVRManager -100 < OVREyeGaze -80 < OVRCameraRig -50 < `GazeRecorder`'s coroutine), so camera, anchors and provider sample come from one `Update` pass. `HeadSpace` was rejected as the recording mode: the SDK assigns head-space numbers to `transform.position`, which only reads as head-local under an identity parent (fragile).
The Camera Offset is applied once (inside the camera pose). The v13 view-frame fallback anchor is built the same way (head-local anchor placed at the rendered camera), so provider and fallback live in one frame.

### 9.5 Synchronisation evidence (schema v14, no extra sampling cost beyond one OVRPlugin read per row)
`provider_time_s` (`EyeGazesState.Time`) and `provider_time_changed` (1 = newer provider sample than the previous row, 0 = reused: the provider rate relative to the row rate), `anchors_updated_frame` (frame of the last `OVRCameraRig.UpdatedAnchors`; the manifest keeps the maximum lag), `frame_index` (existing). The coherence check `*_worldspace_vs_composed_*` compares the `OVREyeGaze` result with `rendered camera * head-space eye` computed from the raw read at sampling time: ~0 within one frame; a systematic difference means the camera and the provider sample are not from the same pass.

### 9.6 IPD and per-eye positions: what the numbers are
* **Anchors (`Left/RightEyeAnchor`):** runtime-supplied per-eye view poses, not static prefab values (`OVRCameraRig.UpdateAnchors` overwrites their local poses each update from `GetNodePose(EyeLeft/EyeRight)`); with `useIPDInPositionTracking` true (scene) they reflect the headset IPD setting. In the audited Windows Editor sessions their separation was **67.37 mm (sd 0.3 mm)**: a **runtime-reported stereo view separation** (simulator / Link runtime), **not** a measured pupil distance and not a gaze-origin separation.
* **Provider positions:** the eye positions of the eye-tracking system; the SDK does not define them as pupil centres. In the Editor sessions they agreed with the anchors to sub-millimetre, i.e. the simulator reports head-anchored eyes; on Quest Pro they are expected to differ. Recorded separately (`*_gaze_head_pos_*`, `gaze_head_separation_m`).
* **`OVRPlugin.ipd`:** the runtime's user IPD (`runtime_user_ipd_m`); a plausibility gate (0.02 - 0.12 m) only decides whether the number is usable, it never corrects anything.
* No IPD, eye offset or eye height constant exists in production code (tests scan for it). Whether the runtime exposes participant-specific values on Quest Pro is **a hardware question**: the three separations above are recorded so it can be answered.

### 9.7 Architecture decision
| Criterion | A: XRI renders, OVRCameraRig + OVREyeGaze measure (**selected**) | B: OVRCameraRig primary | C: XRI camera only |
|---|---|---|---|
| Quest Pro eye-tracking API | yes (`OVREyeGaze`, `GetEyeGazesState`) | yes | no independent L/R poses |
| Per-eye gaze origin / direction | provider poses | provider poses | derived from camera + assumed IPD (rejected) |
| Runtime-derived IPD | anchors, `OVRPlugin.ipd` | same | none |
| Correct world transform | one: head-relative placed at the rendered camera | native (rig is the camera) | n/a |
| XRI compatibility / teleport / controllers | unchanged | needs replacing `XROrigin` camera pairing, `TrackedPoseDriver`, `GuardedTeleportationProvider` camera-height maths, UI placement, turn providers | unchanged |
| Update synchronisation | one Update pass, evidence recorded | single rig | single rig |
| Risk | low (additive recording) | high (rework of the experiment's interaction layer, hardware-only validation) | unacceptable data loss |
Selected: **A**, with `ViewFrame` retained as the non-invasive alignment of the two trees. Moving `OVRCameraRig` under `Camera Offset` was considered and **not** done: the anchors would then inherit Unity's offset but stay in OVRPlugin's tracking space, hiding a possible origin mismatch; head-relative composition does not depend on either.

Hierarchy (unchanged):
```
XR Interaction Setup
  XR Origin (XR Rig)            = XROrigin base (ground; AutoGroundAlign, teleports)
    Camera Offset               -> Main Camera (XRI, TrackedPoseDriver)   RENDERS, Camera.main, UI, LOS target, view frame
    OVRCameraRig (OVRManager)   -> TrackingSpace -> Center/Left/Right EyeAnchor, controller anchors (cameras off, anchors live)
  EyeGazeProviders              -> EyeGaze_Left, EyeGaze_Right (OVREyeGaze, WorldSpace)   per-eye gaze poses
```

### 9.8 Data semantics (v14)
* **View anchors:** `left_eye_head_local_*` / `right_eye_head_local_*` (head frame), `left_eye_world_*` / `right_eye_world_*` (the anchors re-applied at the rendered camera). These are **stereo view anchors, not gaze origins** (names kept to avoid breaking v13; this document and `eye_pipeline.note` are the definition).
* **Actual gaze:** `left_` / `right_` / `combined_origin_*` and `*_dir_*` (OVREyeGaze WorldSpace, validity/confidence columns unchanged), `gaze_ray_source` = both | left | right | head_fallback, `gaze_origin_source` = provider | eye_anchor_view | eye_anchor | provider_unchecked | mixed, `origin_fallback_reason`. Combined = mean origin and normalised mean direction of the valid eyes; one valid eye = that eye; none = head-forward fallback (marked, with `ray_is_eye_gaze = 0`).
* **Raw / head space (v14):** `*_raw_tracking_pos_*`, `*_raw_tracking_dir_*`, `*_gaze_head_pos_*`, `*_gaze_head_dir_*`, `provider_time_s`, `provider_time_changed`; comparisons and separations as listed in `npc_data_schema.md` section 29.
* No existing column changes meaning in v14; nothing is deprecated. The previous fallback (anchor) is kept and distinguished (`eye_anchor_view`).

### 9.9 Hardware validation (nothing here is done)
Quest Link and Android Standalone separately; record each with v14 and compare the manifest `eye_pipeline`.
1. **Calibration / availability:** run the Quest Pro eye-tracking calibration in system settings; check permission, `eye_tracking` lifecycle state, `*_provider_valid`, confidence **varies** (the Editor runtime reported constant 0.5).
2. **Per-eye pose:** `left_gaze_head_pos_x < 0 < right_gaze_head_pos_x`; `gaze_head_separation_m` vs `view_anchor_separation_m` vs `runtime_user_ipd_m` (three values; report their relations, do not assume equality).
3. **IPD slider:** step the slider across its range with the same participant; the anchor separation and `runtime_user_ipd_m` must follow; note whether `gaze_head_separation_m` follows (it will only if the provider's eye model follows the setting). Repeat with several participants and after re-seating the headset.
4. **Head movement:** translate / rotate / bend: `*_gaze_head_pos_*` stay constant for fixed eyes-in-head; world positions follow the camera; `left_worldspace_vs_composed_*` ~ 0; `anchors_updated_frame` lag 0; `provider_time_changed` mostly 1 at the provider's rate.
5. **Recentre / teleport:** head-space values do not jump; world values follow the rig; heights above ground as in section 8.7.
6. **Floor vs Device** (section 8.5) before any accuracy claim.
7. **Known-target fixation:** targets at several depths and horizontal / vertical angles in the city (and after recentre / fit change / per participant). For each: cast the recorded left, right and combined rays, compare the hit with the known surface; report angular error (ray vs eye-to-target direction) and spatial error separately, plus the monocular divergence. This measures tracker accuracy + the transformation chain together; the head-space columns and the composed-vs-WorldSpace check isolate the transformation chain from the tracker.
8. **Fallback:** cover one eye / lose tracking: `origin_fallback_reason`, `gaze_ray_source`.

### 9.10 Limits
No claim of research-grade accuracy, participant-specific IPD support, or physical pupil position is made from Editor tests. The simulator's eye pose is head-anchored; confidence was constant. The OVRPlugin-origin vs Unity-origin question (Floor/Device) does not affect the recorded gaze ray (head-relative composition) but does affect any comparison of tracking-space anchors with Unity-space positions (trajectory `hmd_*`, controllers); the v12 `tracking_space` probe stays for that.

