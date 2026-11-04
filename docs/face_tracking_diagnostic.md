# Face tracking / eyelid signal (`EyesClosedL/R`) — diagnosis and verification

Scope: why `eyes_closed_left`, `eyes_closed_right` and `blink_state` stayed blank after `faceTrackingSupport` was enabled, what was changed, and what still has to be verified on a real Quest Pro. Schema v16.

## 1. Root cause (code- and SDK-level evidence)

`OculusProjectConfig.faceTrackingSupport` (the "FaceTrackingEnabled" project setting) only controls Android manifest / permission generation. Face tracking is **started by an enabled `OVRFaceExpressions` component** (`OnEnable` → `OVRPlugin.StartFaceTracking2`). `OVRManager` only (optionally) requests the permission (`requestFaceTrackingPermissionOnStartup`, false in the scene); it never starts tracking. No scene or prefab contained `OVRFaceExpressions` and `GazeRecorder` only searched for one — so `OVRPlugin.faceTracking2Enabled` stayed false, `BlinkCapability` returned `not_configured`, and the columns stayed blank.

Layers that must all hold before an eyelid value is real (each is now recorded separately): runtime initialised → face tracking supported → permission → component enabled → session started (`faceTracking2Enabled`) → `GetFaceState2` succeeded → `Status.IsValid` → `EyeFollowingBlendshapesValid` (corrects interpretation) → data source is **visual**.

Secondary findings (all fixed): (a) `EyesClosed` was recorded without the SDK's eye-following recovery (`+ min(LookDownL, LookDownR)`); (b) status was cached for 1 s, so up to 1 s of valid data after the signal appeared was blank, and a value could be read after the cached status was stale; (c) `OculusRuntimeSettings` requests **both** Visual and Audio data sources; the SDK documents Audio as "estimate face expressions using audio data only", which cannot observe eyelids — a *valid* frame from that source would look like flat, unresponsive data. It is now recorded as `audio_only_source` with blank closure. **The runtime settings were not changed**; consider disabling the Audio source for this study (data-protection and data-quality).

## 2. What changed

* `GazeRecorder`: `autoCreateFaceExpressions` (default true) creates `OVRFaceExpressions` on the recorder's GameObject when the runtime reports support and none exists (requests the Android permission once; bounded re-start: ≤12 attempts, every 5 s, only for the auto-created component). Per-row live read; recovered closure drives the blink detector; 10 appended columns; 1 Hz direct `GetFaceState2` probe (API result, `Status.IsValid`, data source, provider time).
* `FaceExpressionSupport.cs`: pure classification / recovery / column formatting / interruption bookkeeping.
* `FaceTrackingDiagnostic.cs` (optional, not in any experiment scene) and `docs/tools/plot_face_closure.py`.
* Manifest `eye_tracking.blink` additions; schema 16.
* Not touched: XR Origin, Camera Offset, XRI camera, OVRCameraRig, gaze pose/transforms, ViewFrame, raycasting, sampling frequency, NPC gaze-hit processing, audio, Part 1/2 behaviour, any scene.

## 3. Hardware verification procedure (NOT done here — no headset was available)

Prerequisites (Quest Link): Quest Pro with eye tracking **and** Natural Facial Expressions enabled in the headset settings; Meta Quest Link app (PC) → Settings → Beta/Developer: enable eye tracking and face tracking over Link; eye-tracking calibration done.

1. Create an empty scene, add `FaceTrackingDiagnostic` to an empty GameObject, enter Play over Link. A CSV `face_diag_<time>.csv` is written to `persistentDataPath` (path logged once).
2. Follow the cues logged at segment changes (open 5 s, left closed 3 s, open 2 s, right closed 3 s, open 2 s, both closed 3 s, open 5 s).
3. `python3 docs/tools/plot_face_closure.py face_diag_<time>.csv` → trace of left / right vs `CaptureClock.TAbs` with the layer flags.
4. Read the layers in order. First `0` is the failure point: `session_started=0` (component/permission/StartFaceTracking2), `get_face_state_ok=0` (runtime/link not forwarding face data), `status_valid=0` (no valid frame), `data_source=audio` (visual unavailable → audio fallback, no eyelid data).
5. Acceptance: left-only closure raises `api_closed_l`/`recovered_l` but not `_r` (and vice versa), both-closed raises both, `wrapper_valid=1`, `data_source=visual`, and in a real session gaze rows show `face_state=current` with the same pattern.

## 4. Limitations

* No claim is made about real Quest Pro readings; all automated tests use synthetic readings.
* Provider timestamp (`FaceState.Time`) is only available from the 1 Hz probe / diagnostic, not per row (`OVRFaceExpressions` keeps it private). `face_sample_frame` is the Unity frame of the read.
* No blink threshold was calibrated (`blinkCloseThreshold` 0.7 / `blinkOpenThreshold` 0.5 remain uncalibrated defaults).
* Enabling face tracking makes the runtime process facial data — a data-protection decision for the study.


## Update (schema v17): start-up before the first gaze row, live per-row source

The 1.02 s `not_started` gap of the first real v16 session came from creating `OVRFaceExpressions` lazily in the first row and from `face_tracking_started` / `face_data_source` being <= 1 s old probes. v17 creates the component at the start of Part 2 / 3 and waits (bounded, 5 s) for a valid Visual frame before the Start panel;
each row reads started / valid / source / provider time / weights from ONE live `GetFaceState2` state. See `docs/schema_v17_changes.md` (section C). Raw weights and all face diagnostics are in `gaze_diagnostics.csv`; `gaze.csv` carries `eyes_closed_left/right_recovered` and `face_closure_valid`.
Open question from the audit (unchanged by this work): in ~20 % of rows `EyesClosedR` equalled `EyesLookDownR` exactly and the left eye's closure stayed far below the right's; whether that is the provider or the headset can only be decided with the controlled closure sequence on hardware.
