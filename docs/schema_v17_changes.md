# Schema v17 and the four fixes that came with it

This document records one work package (audit follow-up of session 20261104T141934Z_424f001ded51): (A) one frozen audio time mapping, (B) terrain colliders on the Ground layer, (C) face tracking started before the first gaze row, (D) a smaller gaze.csv plus gaze_diagnostics.csv.
Everything below was verified by automated tests with synthetic data or by an offline replay of recorded positions against the project's terrain colliders; **nothing was verified on a Quest Pro** (see "Still requires a hardware session").

## A. Audio: one frozen clock map

See `docs/part3_audio.md`, section "Frozen time mapping (schema v17)". Summary: index timestamps are no longer stamped with a moving map; the index is written once from `AudioClockMap.Freeze()`; adjacent rows share their boundary time exactly; every anchor (including anchor 0) is persisted;
`AudioIndexCheck` classifies discrepancies and reproduces the times from the anchors file. The audio stop boundary (`EndTrial()` / `phase_end`) is unchanged.

## B. Terrain collider layers

Cause: 14 TIN tile colliders of "Grid Area" / "Organic Area" and the `SouthRiver_TIN_GapPatch` collider were on Default while the Practice Area tiles and all PONT bridges were already on Ground (layer 3). `ViewFrameProbe.TryGroundY` (mask = `AutoGroundAlign.groundLayer` = Ground) therefore found a floor only on the one bridge near the start,
and `AoiClassifier` (layer based) called the other terrain hits OTHER.

Identification (not by name prefix): a collider is terrain when its MeshCollider mesh is an asset under `Assets/Models/TIN` (tiles, gap patch) or `Assets/Models/PONT` (bridge decks) - the same source-path rule `ExplorationBoundaryEditor.Classify` already uses ("independent of layers and activation").
`gml_` / `ID_` name prefixes were not used. Not touched: 1453 building MeshColliders (layer 0; 9 candidate colliders on layer 6), 159 exploration-boundary BoxColliders (layer 2, Ignore Raycast), 7 attractor / start-point SphereColliders (layer 0), the inactive scratch `Plane` (layer 0), NPC colliders.

Scene change (Assets/Scenes/LyonVRVity35.unity, +57 / -1 lines): for the 14 prefab instances a `m_Layer = 3` modification on the root GameObject (the one that carries the added MeshCollider; same form the Practice Area instances already use) and `m_Layer: 3` on the GapPatch GameObject.
No prefab asset, mesh, collider setting, renderer, material, transform or activation state was changed. In the saved scene both `TIN_Textured` groups are INACTIVE (`m_IsActive: 0`); the audited session evidently ran with them active (its rays hit `gml_*` colliders). Activation was deliberately left alone.

| GameObject (scene object name) | Scene path (parents) | Collider | Mesh asset | Previous layer | New layer | Changed |
|---|---|---|---|---|---|---|
| `ID_218c03d1-4956-47f9-b29b-b8d52544a429` (PONT bridge) | PONT/4 | MeshCollider (on the root GameObject of the prefab instance) | ID_218c03d1-4956-47f9-b29b-b8d52544a429.gltf | 3 | 3 | no (already Ground) |
| `ID_48bd793d-807e-4a39-a514-76f7e4a57c54` (PONT bridge) | PONT/2 | MeshCollider (on the root GameObject of the prefab instance) | ID_48bd793d-807e-4a39-a514-76f7e4a57c54.gltf | 3 | 3 | no (already Ground) |
| `ID_5e242b2c-9b71-4521-acfd-f14cd3413d16` (PONT bridge) | PONT/3 | MeshCollider (on the root GameObject of the prefab instance) | ID_5e242b2c-9b71-4521-acfd-f14cd3413d16.gltf | 3 | 3 | no (already Ground) |
| `ID_60e8440f-6f17-4a98-8e3a-4e10cc9cb02b` (PONT bridge) | PONT/1 | MeshCollider (on the root GameObject of the prefab instance) | ID_60e8440f-6f17-4a98-8e3a-4e10cc9cb02b.gltf | 3 | 3 | no (already Ground) |
| `ID_625564ab-b907-4cf7-8567-c0de60f7cb56` (PONT bridge) | PONT/3 | MeshCollider (on the root GameObject of the prefab instance) | ID_625564ab-b907-4cf7-8567-c0de60f7cb56.gltf | 3 | 3 | no (already Ground) |
| `ID_7409666e-9428-44cf-9097-21927640989e` (PONT bridge) | PONT/2 | MeshCollider (on the root GameObject of the prefab instance) | ID_7409666e-9428-44cf-9097-21927640989e.gltf | 3 | 3 | no (already Ground) |
| `ID_7519768a-12aa-435a-bbd7-f487b99cc80f` (PONT bridge) | PONT/4 | MeshCollider (on the root GameObject of the prefab instance) | ID_7519768a-12aa-435a-bbd7-f487b99cc80f.gltf | 3 | 3 | no (already Ground) |
| `ID_7784513b-9607-48a8-8fe1-6f9df4951b92` (PONT bridge) | PONT/1 | MeshCollider (on the root GameObject of the prefab instance) | ID_7784513b-9607-48a8-8fe1-6f9df4951b92.gltf | 3 | 3 | no (already Ground) |
| `ID_7cc46bf9-9b9d-41ff-9204-e44c0ed906d0` (PONT bridge) | PONT/4 | MeshCollider (on the root GameObject of the prefab instance) | ID_7cc46bf9-9b9d-41ff-9204-e44c0ed906d0.gltf | 3 | 3 | no (already Ground) |
| `ID_80d44e22-3fae-485d-b167-997b0083681f` (PONT bridge) | PONT/4 | MeshCollider (on the root GameObject of the prefab instance) | ID_80d44e22-3fae-485d-b167-997b0083681f.gltf | 3 | 3 | no (already Ground) |
| `ID_84d75247-e134-422c-a753-d51caf0a5c65` (PONT bridge) | PONT/1 | MeshCollider (on the root GameObject of the prefab instance) | ID_84d75247-e134-422c-a753-d51caf0a5c65.gltf | 3 | 3 | no (already Ground) |
| `ID_96e22e81-dce7-4ef7-8e6f-0beebc409c9b` (PONT bridge) | PONT/4 | MeshCollider (on the root GameObject of the prefab instance) | ID_96e22e81-dce7-4ef7-8e6f-0beebc409c9b.gltf | 3 | 3 | no (already Ground) |
| `ID_cc5f432f-2ae9-434d-82b7-ff34a9cc4d8c` (PONT bridge) | PONT/1 | MeshCollider (on the root GameObject of the prefab instance) | ID_cc5f432f-2ae9-434d-82b7-ff34a9cc4d8c.gltf | 3 | 3 | no (already Ground) |
| `ID_d6dd4191-253d-4fb3-ad23-f6c7e9f699e9` (PONT bridge) | PONT/3 | MeshCollider (on the root GameObject of the prefab instance) | ID_d6dd4191-253d-4fb3-ad23-f6c7e9f699e9.gltf | 3 | 3 | no (already Ground) |
| `ID_f541bd61-3632-44c1-b583-16c591116b42` (PONT bridge) | PONT/5 | MeshCollider (on the root GameObject of the prefab instance) | ID_f541bd61-3632-44c1-b583-16c591116b42.gltf | 3 | 3 | no (already Ground) |
| `ID_ffcb0caf-e3db-46b0-843d-8962c8433652` (PONT bridge) | PONT/5 | MeshCollider (on the root GameObject of the prefab instance) | ID_ffcb0caf-e3db-46b0-843d-8962c8433652.gltf | 3 | 3 | no (already Ground) |
| `SouthRiver_TIN_GapPatch` (TIN gap patch) | Organic Area/TIN_Textured/SouthRiver_TIN_GapPatch | MeshCollider (on the root GameObject of the prefab instance) | SouthRiver_TIN_GapPatch.asset | 0 | 3 | YES |
| `gml_198275d7-1842-4ff1-abc3-123f14b587d4` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_198275d7-1842-4ff1-abc3-123f14b587d4.gltf | 3 | 3 | no (already Ground) |
| `gml_38179d6d-9886-48a9-9634-4e5eb1016a06` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_38179d6d-9886-48a9-9634-4e5eb1016a06.gltf | 3 | 3 | no (already Ground) |
| `gml_8bf87428-0e6e-485c-894d-eebcb42a5e76` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_8bf87428-0e6e-485c-894d-eebcb42a5e76.gltf | 3 | 3 | no (already Ground) |
| `gml_9fb11684-5d01-442f-8e2a-781b88d720b7` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_9fb11684-5d01-442f-8e2a-781b88d720b7.gltf | 3 | 3 | no (already Ground) |
| `gml_ca89eda0-46ef-4c3b-8e69-8b65157d50ea` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_ca89eda0-46ef-4c3b-8e69-8b65157d50ea.gltf | 3 | 3 | no (already Ground) |
| `gml_dd4fd483-1c11-4e3b-9b11-02d15babedd8` (TIN tile) | Practice Area | MeshCollider (on the root GameObject of the prefab instance) | gml_dd4fd483-1c11-4e3b-9b11-02d15babedd8.gltf | 3 | 3 | no (already Ground) |
| `gml_28445915-ef07-4e22-987c-610c478cc370` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_28445915-ef07-4e22-987c-610c478cc370.gltf | 0 | 3 | YES |
| `gml_29317c3e-50a8-4689-8e26-0ebd3b66324c` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_29317c3e-50a8-4689-8e26-0ebd3b66324c.gltf | 0 | 3 | YES |
| `gml_29317c3e-50a8-4689-8e26-0ebd3b66324c` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_29317c3e-50a8-4689-8e26-0ebd3b66324c.gltf | 0 | 3 | YES |
| `gml_2c9c7a15-afcf-44d3-a4fa-e57b6234043d` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_2c9c7a15-afcf-44d3-a4fa-e57b6234043d.gltf | 0 | 3 | YES |
| `gml_4811c913-de34-43c1-ae8a-51304bcd7a24` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_4811c913-de34-43c1-ae8a-51304bcd7a24.gltf | 0 | 3 | YES |
| `gml_4d5046a5-1bf0-4bd7-b6f9-ba977d2bfe55` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_4d5046a5-1bf0-4bd7-b6f9-ba977d2bfe55.gltf | 0 | 3 | YES |
| `gml_786faa4c-221d-4254-b8b2-aca3b8f8fea1` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_786faa4c-221d-4254-b8b2-aca3b8f8fea1.gltf | 0 | 3 | YES |
| `gml_87623043-f996-4e2c-83c8-16f139a63d7c` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_87623043-f996-4e2c-83c8-16f139a63d7c.gltf | 0 | 3 | YES |
| `gml_a4635653-22bf-43bf-9894-1f652cf03371` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_a4635653-22bf-43bf-9894-1f652cf03371.gltf | 0 | 3 | YES |
| `gml_af20ae56-d662-4581-b17f-e7ca01101e3e` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_af20ae56-d662-4581-b17f-e7ca01101e3e.gltf | 0 | 3 | YES |
| `gml_babe4fa3-ecc6-4cd9-8a77-78814f30b57c` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_babe4fa3-ecc6-4cd9-8a77-78814f30b57c.gltf | 0 | 3 | YES |
| `gml_c9b1830c-4fe8-4fea-bfd7-87af196e6721` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_c9b1830c-4fe8-4fea-bfd7-87af196e6721.gltf | 0 | 3 | YES |
| `gml_c9b1830c-4fe8-4fea-bfd7-87af196e6721` (TIN tile (Textured)) | Grid Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_c9b1830c-4fe8-4fea-bfd7-87af196e6721.gltf | 0 | 3 | YES |
| `gml_e3cdfa42-929d-4954-904e-fa4807d0a2bd` (TIN tile (Textured)) | Organic Area/TIN_Textured | MeshCollider (on the root GameObject of the prefab instance) | gml_e3cdfa42-929d-4954-904e-fa4807d0a2bd.gltf | 0 | 3 | YES |

Masks and physics audited (unchanged): gaze ROI mask 123 (Default, TransparentFX, Ground, Water, UI, TargetPreview) still hits Ground; NPC line-of-sight mask = Default | Ground (DataCaptureManager.Start) still contains both; SpawnerManager.groundLayers = Everything; ExplorationBoundary walls use "Ignore Raycast";
the physics collision matrix has layers Default (0) and Ground (3) colliding with every layer, so a Default -> Ground swap changes no collision. NPC colliders are on Default and are additionally excluded by `PedestrianAgent` / tag `Npc` in the ground query (`ViewFrameProbe.TryGroundY`).

Measured by replay (scratch project with the real scene, TIN and PONT assets; the 14 Textured tiles imported; buildings absent, which cannot matter for Ground-only queries). Positions recorded in the audited session were replayed:
| | before | after |
|---|---|---|
| gaze rows with `ground_y` (ray from `hmd_world`, 12 m, Ground mask) | 53 / 1266 (4.2 %) - exactly the audited value, all on bridge `ID_625564ab` | **1266 / 1266** (tile gml_4811c913: 912, gml_4d5046a5: 301, bridge: 53) |
| ROI ray replay: recorded terrain / bridge hits reproduced by the same collider | 295 / 321 (the other 26 hit the identical duplicate bridge object) | 295 / 321, hit layer now 3 |
| `hmd_height_above_ground` | 1.332 - 1.384 m (53 rows) | 1.182 - 1.458 m (mean 1.343, sd 0.093) |
| teleport / river destinations with Ground within 12 m | 1 / 21 | 18 / 21 (the other 3 destinations lie at or just below the surface, so a downward ray from them hits nothing) |

### B2. AutoGroundAlign after the layer correction (follow-up audit)

`AutoGroundAlign` (host: the XR Origin base = `ExperimentManager.playerRig`; scene values `raycastDistance 3`, `groundLayer = Ground`, `smoothSpeed 10`, `heightOffset 0`) runs every frame: `Physics.Raycast` from `base + 0.1 m` straight down, mask Ground, closest hit, `base.y = Lerp(base.y, hit.y, dt * 10)`.
It only writes the base's Y (never XZ, never the Camera Offset), has no NPC filter (NPC colliders are kept off the layer, see below), and does nothing when no Ground surface lies in its 3 m window. Teleports (`TeleportPlayerTo`, `RiverTeleportPoint`, the guarded XRI teleport) write the rig transform and the component then converges from the landing height;
`ExplorationBoundary` and the guarded teleport test ignore Y; the Start / panel logic already waits for the head height to be steady (`UIManager.settledFramesRequired = 8`, `settledMaxHeightDelta = 3 mm`), which covers this lerp.

Replay of the audited session's recorded rig X/Z path against the real scene colliders with the component's own loop (Y restarts from the recorded landing height after each of the 20 teleports; tooling in `docs/tools/terrain_replay`):

| | before the layer change | after (TIN groups active) | after, TIN groups INACTIVE as saved |
|---|---|---|---|
| rig samples with a Ground surface in the ray window | 53 / 1264 | 965 / 1264 | 53 / 1264 |
| surface selected | bridge `ID_d6dd4191` only | Organic Area tile `gml_4811c913` (912) + bridge (53); all Ground layer, 0 trigger hits | bridge only |
| steady state (target - rig Y) | 0 | median 0.000 m, mean -0.003 m | 0 |
| at the 20 teleport landings | 1 landing aligned (-0.072 m) | gap -0.138 .. +0.036 m, median -0.076 m; converges (< 2 mm) in 5-6 samples of 50 ms | as before |
| oscillation (consecutive corrections of opposite sign > 1 mm) | none | none (0) | none |
| change of the target height between consecutive walking samples | 0 | max 8 mm for 0.12 m of travel; 0 collider switches along the route | 0 |

* The figures reported earlier ("mean -0.051 m over 965 rows") compared the target with the rig height *as recorded*, i.e. before any alignment; the dynamic replay shows the rig converging within about a third of a second after each landing and then staying on the surface.
  Real data agree: after the first landing on the bridge the recorded rig Y went 168.7562 -> 168.6336 within 0.3 s and stayed constant for 2.4 s.
* **Where the alignment cannot act (unchanged by the layer work):** the Part 3 start point (`StartPointGE`) sits 0.131 m *below* the terrain (rig Y 168.0303, surface 168.16). The ray starts only 0.1 m above the base, i.e. 3 cm under the one-sided mesh, so nothing is hit and the rig base stays under the surface for as long as the participant remains there
  (299 rows, the first 15 s of the audited trial; camera 1.19 m above the surface instead of ~1.33 m). The component lifts a rig that is at most 0.1 m below the surface and lowers one that is up to 2.9 m above it. Raising `StartPointGE` by >= 0.14 m (or the ray offset) would remove this; it was not changed because it alters experimental positions.
  **Update (ground-alignment audit):** this was later fixed in the scene data - `StartPointGE` and the five other mis-heighted start anchors were raised / lowered onto the terrain surface (vertical only, no C# change); see `docs/ground_alignment_start_points.md`. The schema itself is unaffected.
* **Overlapping Ground colliders** (scan of 33 000 random points over the terrain colliders, closest-hit selection): same-name duplicate tiles are coincident (dy = 0); the duplicate bridge decks differ by <= 3.8 mm; neighbouring TIN tiles overlap in two strips (`gml_29317c3e` / `gml_4811c913` at x 2242-2309, z 805-930; `gml_4811c913` / `gml_4d5046a5` at x 2209-2309, z 488-806)
  with a median vertical offset of ~12 cm (up to 2.5-2.8 m at steep banks), and bridges lie 0.1-3 m above terrain. Where the participant walks across such a strip the closest surface (and so the target) can step by that offset and the lerp then moves the base over ~0.3 s; the audited route produced no such switch.
* **Runtime activation:** both `TIN_Textured` groups (and `Practice Area`) are saved INACTIVE (`m_IsActive: 0`) and have been in every committed scene version since `e7ebbfac` (they were active in `d11a9f9c`). The audited session's gaze rays hit `gml_*` colliders, so it ran with them active; no script in the project activates them.
  With the committed scene state the terrain does not exist for physics at all, and the layer correction (and the terrain part of AutoGroundAlign) only matter once the groups are active. Activation was left untouched; the intended state should be recorded in the scene.
* **NPC colliders:** the NPC prefab root (tag Npc, 2 m capsule) is serialised on layer 3 (Ground) but `AI_Stats.Awake` assigns the layer named "Npc"; none exists, so it falls back to Default (0) (all 21 recorded NPC gaze hits had `hit_layer 0`). `TerrainLayerTests` pins this chain. AutoGroundAlign itself has no NPC filter; it relies on this.
* Conclusion: no implementation defect found in AutoGroundAlign (stable, monotone, closest-surface, Y only); the displacement is the component's designed behaviour. The component, its mask and the 15 layer assignments were not changed.

## C. Face tracking started before the first gaze row

Previous: `GazeRecorder` created `OVRFaceExpressions` inside the first row's `RefreshBlinkCapability`; `face_tracking_started` came from a probe refreshed at most once per second, so the first 19 rows (1.02 s) were `not_started`.
Now: `ExperimentManager` (Parts 2 and 3, i.e. the Parts with gaze capture enabled) calls `GazeRecorder.BeginFaceTrackingStartup` right after the participant is placed at the Part's start (the component is created and the runtime starts tracking during the NPC warm-up),
and after `WaitForHeadsetBeforeStartPanel` (headset mounted) waits (bounded, `faceTrackingReadyTimeoutSeconds` = 5 s) in `AwaitFaceTrackingReady` until a **valid Visual frame with eyelid weights** has been observed. Readiness is evaluated from live facts, not from the 1 Hz probe (`FaceReadiness.Evaluate`):
runtime initialised -> supported -> permission -> component created -> enabled -> started (`faceTracking2Enabled`) -> `GetFaceState2` responding -> `Status.IsValid` -> data source Visual -> eyelid weights present. No runtime / unsupported ends the wait at once; every other unmet stage waits until the timeout.
The gate **records nothing**: the gaze file opens only at the Start click (`OnTrialStart`), exactly as before; no pre-Start expression sample exists in any file. Result: manifest `eye_tracking.blink.startup_*` (attempted, ready, stage reached, failure reason with `timeout_after_<s>` / `permanent`, wait seconds, frames polled, data source at ready, start point).
Failure policy: `faceTrackingRequired = false` (default) continues and the eyelid columns are blank with the reason in `face_state`; `true` shows the retry panel (same mechanism as the microphone gate). `faceTrackingStartAfterStartClick = true` moves the wait to after the Start click and before `BeginTrial` (if starting the face camera before the click is not acceptable for the study).
Each row now also reads `started`, the data source and the provider time **live** from the same `GetFaceState2` state as its weights, so a source switch is seen on the row it happens. Lazy creation inside the data path survives only when no start-up gate ran (standalone use).
Face tracking runs (it does not record) from the start of Part 2 / 3 until the session ends; disable `faceTrackingPreflight` or enable `faceTrackingStartAfterStartClick` if that is not acceptable.

## D. Schema v17

* `gaze.csv`: 61 columns (v16: 143). `gaze_diagnostics.csv`: 85 columns, one row per gaze row, keys `session_id` + `frame_index` + `t_abs`; written by `GazeRecorder` with the same crash-safe writer, closed with the session, listed in `file_list`, reported in `capture_streams_closed`.
* Every v16 column is accounted for in `docs/gaze_schema_v17_dictionary.md` (section 3): 60 stay, 75 move, 8 are removed because they are exact, derivable duplicates (`ray_origin_*`, `ray_dir_*`, `ray_is_eye_gaze`, `roi_hit_name`). Added: `face_closure_valid` (gaze.csv); `face_provider_time_s` and `fallback_ray_origin_*` / `fallback_ray_dir_*` (diagnostics; the last six record the ray that was cast on a head-fallback row, so every hit stays reconstructible: the cast ray is either `combined_origin/dir` or `fallback_ray_*`). No name is reused with a different meaning.
* The eyelid pair in gaze.csv is the **recovered** closure (`eyes_closed_left_recovered`, `eyes_closed_right_recovered`); the **raw** SDK weights (`eyes_closed_left/right`, same meaning as in v11-v16) and the correction inputs are in the diagnostics. `blink_state` (uncalibrated, false negatives in the audited session) is diagnostics-only; manifest `absence_of_blink_events_is_evidence_of_no_blinks` is now always `false`.
* Reading: `docs/tools/gaze_reader.py` reads v11-v16 unchanged and v17 as one joined table with the v16 names restored where derivable; the PlayMode test helper `Csv.Load` does the same. Historical session files are never rewritten.
* Per-eye `left_/right_origin_*` / `left_/right_dir_*` stay in gaze.csv (the analysis pipeline expects per-eye and combined columns); per-eye validity and confidence are diagnostics.

## Still requires a hardware session (Quest Pro over Quest Link)

Audio index finalisation and the `Closed` session state with a real microphone; first-row Visual face tracking readiness and the real wait time; independent left / right eyelid response (controlled closure sequence); terrain classification and ground-height coverage on the real route (and the AutoGroundAlign effect above); the v17 files from a real session; a full session reaching `Closed`.
