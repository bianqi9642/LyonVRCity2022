# Eye-tracking acquisition: ownership, state machine and Quest Pro verification procedures

Status: **implemented and covered by Editor/logic tests only. Not verified on a Quest Pro.** Nothing below may be reported as "eye tracking works" until the criteria in §5 are met on the real runtime.

## 1. Ownership (one owner per concern)

| Concern | Owner | Notes |
|---|---|---|
| When to start, the single permission request, retries/backoff, pause/resume, shutdown, observable state | `EyeTrackingLifecycle` (hosted by `EyeTrackingLifecycleController`, auto-created by `GazeRecorder.Awake` if the scene has none) | Pure state machine, `Assets/Scripts/Capture/EyeTrackingLifecycle.cs` |
| `OVRPlugin.StartEyeTracking()` | the two `OVREyeGaze` components (`OnEnable`) | The lifecycle never calls it; it only enables / re-enables the components, so the SDK has one start path. The components start **disabled** in the scene (`m_Enabled: 0`). |
| Gaze pose/confidence reading, `gaze.csv` | `GazeRecorder` | Still the only reader of gaze data. The lifecycle reads only the provider's per-eye `IsValid` flag (`OVRPlugin.GetEyeGazesState`, public API). |
| Permission request | lifecycle (`UnityEngine.Android.Permission.RequestUserPermission`, public API) | `OVRManager.requestEyeTrackingPermissionOnStartup` is left **off** and `GazeRecorder` no longer requests - a second owner would show the dialog twice. `OVRPermissionsRequester` is used only for the permission id and `IsPermissionGranted`. |

## 2. State machine

`Idle -> WaitingForXR -> CheckingCapability -> (RequestingPermission -> WaitingForPermission) -> Starting -> Running <-> TrackingUnavailable`; failures: `StartFailed` (bounded backoff 1,2,4,8,15,30 s, at most 8 attempts per episode, at most 5 recovery episodes), `PermissionDenied`, `Unsupported` (after 6 capability checks), `NotApplicable` (no components / disabled), `Stopped` (terminal).
Pause/resume: while paused nothing is decided; on resume `Running` is re-verified, `StartFailed`/`Unsupported` start a fresh episode, `PermissionDenied` only re-checks whether the permission was granted in the system settings (never re-prompts), `WaitingForPermission` keeps waiting (the system dialog is what pauses the app).
A late permission callback after shutdown or after a newer request is counted (`ignored_late_callbacks`) and ignored.

## 3. Two execution paths - do not mix them

| | A) Windows Editor / PC OpenXR / Quest Link | B) Android Standalone (Quest Pro build) |
|---|---|---|
| `runtime_path` | `windows_editor_openxr` | `android_standalone` |
| Android permission | does not exist: `permission_state = not_applicable`, no dialog | `com.oculus.permission.EYE_TRACKING`, requested once by the lifecycle |
| "unsupported" means | the **PC runtime** does not expose eye tracking (e.g. `XR_FB_eye_tracking_social` not enabled by the Link runtime) - says nothing about a standalone build | the device/runtime reports no eye-tracking capability |
| What to check | `capability_state`, `tracking_enabled_state`, `start_*` | additionally `permission_*` and the manifest permission entry of the built APK |

## 4. Scene change (and why)

`OVREyeGaze` runs at execution order -80, `OVRCameraRig` at -50, both in `Update`. With the components **on** the Left/RightEyeAnchor objects (as before), the rig rewrites that very transform right after `OVREyeGaze` wrote the gaze pose, so `GazeRecorder` read the eye-camera pose (head-like), labelled valid whenever the (stale) confidence was high.
Also `OVREyeGaze.Update` returns early on an invalid sample **without** updating `Confidence`, so the last confidence stays and was read as valid. Both are now handled: the components were moved (same component fileIDs, so `GazeRecorder` references are unchanged) onto `EyeGazeProviders/EyeGaze_Left|Right` (identity rotation, scene root), `TrackingMode = WorldSpace` (pose converted with `Camera.main`, so origin and direction are world coordinates; the previous `HeadSpace` mode wrote head-relative values into a world position), `Eye`/`ConfidenceThreshold`/`ApplyPosition`/`ApplyRotation` unchanged, and a per-eye sample also needs the provider's `IsValid`.
`GazeRecorder.blockEyeSamplesOnHostConflict` (default on) records eye samples as invalid if an `OVREyeGaze` is ever found directly on a rig eye anchor again (`gaze_host_conflict` in the manifest).
**Not verified without a headset:** that `WorldSpace` + this hierarchy yields the correct gaze direction on the device. Check §5 step 6.

## 5. Verification procedures (expected result in *italics*)

Open `session_manifest.json` (`eye_tracking`, `gaze_acquisition_status`, `gaze_acquisition_reason`) and `gaze.csv` after each run.

### A) Windows Editor + Quest Link (Quest Pro, eye tracking enabled in the Meta Quest Link / Horizon Link settings)
1. Enter Play mode with the headset on. Console: `[EyeTrackingLifecycle] begin`, `xr_ready`, then either `permission_request`-free `Starting -> Running` or `Unsupported`.
2. *`runtime_path = windows_editor_openxr`, `permission_state = not_applicable`, `permission_requests = 0`.*
3. If `capability_state = unsupported` (`gaze_acquisition_reason = capability_unsupported_on_windows_editor_openxr`): the Link runtime does not expose eye tracking. **This does not decide the standalone case.** Check the Link app's eye-tracking toggle and the OpenXR runtime in use; then do B.
4. If `Running`: look at the headset, run a trial. *`provider_any_valid_samples > 0`, `first_valid_provider_t_abs` set, `left/right_component_state = enabled`, `tracking_enabled_state = true`.*
5. *In `gaze.csv`: rows with `left_valid`/`right_valid = 1`, `left_provider_valid`/`right_provider_valid = 1`, confidence >= 0.5, non-empty `left_dir_*`/`right_dir_*`, `gaze_ray_source` in `both|left|right` (not only `head_fallback`), `ray_is_eye_gaze = 1`, `eye_tracking_state = running`.*
6. Direction check: fixate a known building corner; the ray (`ray_dir_*`) must differ from the head forward (`head_rot_*`) when the eyes move without moving the head. *If `ray_dir_*` simply equals the head forward on every row, the pose is still head-driven - report it.*
7. Close your eyes for > 3 s: *`tracking_unavailable` then `running` again; no restart (`startup_attempts` unchanged).*

### B) Android Standalone (build and install on the Quest Pro)
1. Confirm in the built manifest (`AndroidManifest.xml` of the APK): `com.oculus.permission.EYE_TRACKING` and `oculus.software.eye_tracking` (the project has `removeGradleManifest: 1` and the OpenXR path; this was **not** verified in this work). Eye tracking must be enabled in the headset's Settings > Movement tracking.
2. First launch: *the system permission dialog appears once; log `permission_request`, state `waiting_for_permission` (`gaze_acquisition_status = permission_required`)*. Allow. *`permission_result: permission_granted` -> `starting` -> `running`; `permission_requests = 1`.*
3. Relaunch: *no dialog (`permission_already_granted`).* Deny test: deny once -> *`permission_denied`, no repeated dialog (also after pause/resume)*; re-enable in settings, resume -> *`running`*.
4. Take the headset off and on during a trial: *`application_paused`/`application_resumed` transitions, state returns to `running`, `startup_attempts` unchanged or +1 with `start_success`; gaze.csv resumes (`PauseCount/ResumeCount`).*
5. Success criteria for the whole feature (all required): nonzero `rows_valid_combined`; `left`/`right` valid rows nonzero; confidence >= 0.5; non-empty eye vectors; `gaze_ray_source` not exclusively `head_fallback`; direction check as A6; the same values persisted in `gaze.csv` and the manifest.

## 6. What is NOT established by this work

Real Quest Pro acquisition; whether the Link runtime exposes eye tracking; the built Android manifest contents; that `WorldSpace` gives correct gaze directions on device; behaviour of `OVRPlugin.StartEyeTracking` being called by two `OVREyeGaze` components in sequence (SDK design, unchanged); the actual frame rate on the Windows machine (the new `frame_timing` / `limit_diagnosis` fields will show it).
