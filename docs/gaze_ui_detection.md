# Canvas UI gaze detection (gaze_ui_hits.csv, schema v1)

Status: implemented, verified **synthetically** (PlayMode), against the **real experiment panels offline** (EditMode, no headset) and in the **offline Python reader**. **Not** verified on Quest Link or a Quest Pro - see
[Verification limits](#verification-limits).

Gaze v17 (`gaze.csv` 61 columns, `gaze_diagnostics.csv` 85 columns), the physical gaze raycast, the sampling rate, the validity threshold, the gaze origin / direction conventions and the head-fallback behaviour are **unchanged**.

## 1. What the original gaze ray detects (audit, before this work)

`GazeRecorder.SamplingLoop` builds ONE ray per sample (combined binocular ray, or the head-forward fallback when no eye is valid) and calls
`Physics.Raycast(origin, direction, out hit, roiMaxDistance, roiLayerMask)` - a **single closest hit**, no `RaycastAll`, no `QueryTriggerInteraction` argument.

| Parameter | Actual value | Consequence |
|---|---|---|
| query | `Physics.Raycast` (closest hit) | one collider per sample; anything behind the first collider is invisible to the ray |
| `QueryTriggerInteraction` | default = `UseGlobal`; `ProjectSettings/DynamicsManager.asset` `m_QueriesHitTriggers: 1` | **trigger colliders are hit** (they can shadow what is behind them) |
| `QueriesHitBackfaces` | 0 | back faces of mesh colliders are not hit |
| `roiLayerMask` | scene value `m_Bits: 123` = layers 0 Default, 1 TransparentFX, 3 Ground, 4 Water, 5 UI, 6 TargetPreview (code default `~0`) | layer 2 *Ignore Raycast* and layers 7+ are never hit |
| `roiMaxDistance` | 50 m | |
| sample rate (scene) | 20 Hz (`sampleRate: 20`), frame-limited | |
| inactive objects | colliders of inactive GameObjects do not exist for physics | never hit |
| requirement | a **Collider** | objects without one are invisible to the ray |

Classification (`AoiClassifier.Classify`, first match): `PedestrianAgent` in parents -> `NPC`; `TargetSelectable` in parents -> `CANDIDATE_BUILDING` / `BUILDING` (`TARGET_BUILDING` when it is the currently active target);
MeshCollider + Default layer + BATI name pattern / registry -> `BUILDING`; layer Ground -> `TERRAIN`; layer Water -> `WATER`; layer UI **or a Canvas in the parents** -> `UI`; anything else -> `OTHER`; no hit -> `NONE`.
The class `UI` therefore only ever occurred for a **collider** on the UI layer / under a Canvas - which no experiment panel has.

### Inventory (before / after)

| Object type | Detected before? | Required components / conditions | Reason | After this work |
|---|---|---|---|---|
| Buildings (BATI) | **Yes** | enabled Collider (MeshCollider added by `CityKMLImporter`; 1497 MeshColliders in the scene), layer in mask | `Physics.Raycast` closest hit; `BUILDING` / `CANDIDATE_BUILDING` / `TARGET_BUILDING` | unchanged |
| NPCs | **Yes** | capsule / sphere colliders of the Npc prefab + `PedestrianAgent`; NPC capture on for the id | `NPC` | unchanged |
| Terrain / roads (TIN) | **Yes** | Ground-layer collider | `TERRAIN` | unchanged |
| Water | Yes (if a Water-layer collider exists) | layer 4 collider | `WATER` | unchanged |
| Signs | Only as a generic collider (`OTHER` / `BUILDING` by name) | collider | no dedicated sign objects exist in the experiment scene | unchanged |
| **World-space Canvas panels** (all 12 experiment panels) | **No** | none of the 12 templates has a Collider | a Canvas is not physical; the ray passes through the panel and reports what is behind it (building / terrain / `NONE`) | **Yes** (UI path) |
| Canvas Button / Image / RawImage / Text / TMP | **No** | idem | idem | **Yes**: visible, `raycastTarget` Graphics (Text / TMP rect-level) |
| Screen Space - Camera Canvas | No | | | **Yes** when a camera is assigned (none exists in the project) |
| Screen Space - Overlay Canvas | No | | not composited into the headset image, no world position | **Unsupported by design** (skipped and counted) |
| Other (teleport points, XRI interactables, boundary walls, triggers) | Yes if collider and in mask | | | unchanged |

## 2. Canvas audit of the real experiment scene (`Assets/Scenes/LyonVRVity35.unity`)

The scene file contains **no Canvas of its own** except one **inactive** leftover (`UI Sample` -> `Canvas`, `m_IsActive: 0`, XRI demo). Every experiment panel is a **template**: a scene instance (inactive) of
`Assets/Samples/XR Interaction Toolkit/2.6.5/Starter Assets/DemoSceneAssets/Prefabs/UI/ModalSingleButton.prefab` that `UIManager` clones at run time (`Instantiate` + `SetActive(true)`).
Machine-verified by `GazeUiRealSceneTests.AllTwelvePanels_...` (it prints this table):

| Template (name in scene) | UIManager slot | Phase | Render mode | Event camera | GraphicRaycaster | Sorting | Nested canvas / CanvasGroup / Collider |
|---|---|---|---|---|---|---|---|
| PracticePhaseStart / Continue / End | part1Start / Continue / End | Part 1 (practice) | World Space, 750x450 px at scale 0.01 (7.5 x 4.5 m) | none assigned (`worldCamera` null -> `Camera.main`) | `GraphicRaycaster` (ignoreReversed = on); the prefab also lists an XRI `TrackedDeviceGraphicRaycaster`, which the batch-mode Editor reported as a **missing script** on the root (see note below) | 0 / no override | none / none / none |
| ExplorationPhaseStart / Continue / End | part2Start / Continue / End | Part 2 | same | same | same | same | same |
| ExperimentPhaseStart / ExperimentPhaseEnd | part3Start / part3End | Part 3 | same | same | same | same | same |
| ExperimentPhaseDestination1 / 2 | part3Dest1Full / Dest2Full | Part 3 destination phases (target preview + Continue) | same (400 x 300 inner) | same | same | same | same |
| ExperimentPhaseDestination1Shrunk / 2Shrunk | part3Dest1Shrunk / Dest2Shrunk | Part 3 head-locked corner HUD (scale 0.01 -> `HeadLockedPanel`) | same | same | same | same | same |

Children per template (from the prefab + scene overrides):

| Graphic | Type | raycastTarget | Gaze role | Detected after this work |
|---|---|---|---|---|
| root `Image` on the Canvas GameObject | Image (panel background) | **true** | the panel | yes - resolves to the panel marker |
| `Header Text`, `Modal Text` (message / destination text) | legacy `Text` | **false** | text labels | **no** (raycastTarget = false is ignored) |
| `TextButton` (confirm / Continue / Start / End) | `Image` (**color alpha 0** = transparent hit area) + `Button` | **true** | part of the panel | yes, resolved to the **panel** identity (not a Button ROI) - accepted although the Image itself is transparent, because its non-raycast children (outline, label) make the control visible (`ControlLooksVisible`) |
| `TextButton/Image`, `TextButton/Text`, `TextButton/Outline` | Image / Text | false | button decoration | no |
| `BuildingImage` (target-building preview; full + shrunk Destination panels) | `RawImage` | **false** | part of the panel | not a target itself; the area is covered by the root background, so it resolves to the **panel** (section 6) |

Observation (not changed, not part of this task): in the batch-mode test run the root of every template lists `Canvas, GraphicRaycaster, Image, VerticalLayoutGroup, <MISSING_SCRIPT>, PanelController` - the prefab's `TrackedDeviceGraphicRaycaster` entry did not resolve to a script there. Check the live Editor console for "missing script" warnings on the panel prefab; the gaze path does not depend on it (it uses the standard `GraphicRaycaster` and the Graphic registry).

Phases: panels exist only while shown (Start -> confirmed; Continue; End); `UIManager` hides the root Canvas (`Canvas.enabled = false`) while a first panel waits for a settled head pose - the UI path treats that as not hittable.
There are no questionnaire or building-info canvases in the project today. The `EventSystem` GameObject in the scene is inactive; none is created by this work.

## 3. Feasibility per Canvas type

| Canvas type | Supported | Why |
|---|---|---|
| World Space | **Yes (required)** | the Canvas has a real transform; ray vs Graphic rectangle is exact geometry; event camera = `worldCamera` else `Camera.main` (what `GraphicRaycaster` itself uses) |
| Screen Space - Camera | **Yes, when a camera is assigned** | the engine places the Canvas in the world at `planeDistance` in front of that camera while it renders; the same geometry applies. A camera-less one behaves as Overlay and is skipped. Not present in the project; verified synthetically only. |
| Screen Space - Overlay | **No** | no world position; the project's OpenXR rendering does not composite it into the headset image, so a gaze hit would not correspond to anything the participant sees. Skipped and counted (`canvases_skipped_overlay_unsupported`). |

## 4. Mechanism (separate UI path; the Physics raycast is untouched)

`GazeUiRaycaster.Raycast(origin, direction, options)` - same combined gaze ray, same maximum distance as the physical ray. **Why not `GraphicRaycaster.Raycast(PointerEventData)` literally:** a `PointerEventData` carries a screen position and
the raycaster derives its ray from the event camera; a gaze ray starts at the eye (a few cm off the rendered camera) and cannot be expressed that way, and re-assigning `Canvas.worldCamera` per sample would mutate the scene's UI. The class therefore
uses the **same primitives `GraphicRaycaster` uses** on the gaze ray - the pattern of XRI's own `TrackedDeviceGraphicRaycaster`:

1. Canvases come from `GazeUiRegistry` (no scene scan per sample). UIManager registers each panel it instantiates (two one-line additive calls); a `GazeUIElement` registers its Canvas in `OnEnable`; the recorder performs **one** scene scan when a recording starts and the registry flags one after a scene load. Destroyed canvases are pruned; active / enabled state is evaluated live every sample (no events needed).
2. Per Canvas: skip inactive / disabled (also a disabled *root*), Overlay (counted), missing GraphicRaycaster (`uiRequireGraphicRaycaster`), no usable event camera / viewport, layers the event camera does not render.
3. Per Graphic of `GraphicRegistry.GetGraphicsForCanvas`: `raycastTarget` (false ignored unless an opt-in `GazeUIElement.hitWhenNotRaycastTarget`), `canvasRenderer.cull`, `Graphic.depth == -1` (not drawn), **visibility** (`Graphic.color.a` x CanvasRenderer colour / alpha x inherited `CanvasGroup` alpha >= `uiMinVisibleAlpha`; empty Text / TMP label; the one exception is the transparent hit-area Image of a control that has another visible Graphic under the same `Selectable`, e.g. every experiment panel's `TextButton` - found by the real-scene test, where treating that Image as invisible made gaze on the button resolve to the panel background), back face (`GraphicRaycaster.ignoreReversedGraphics`, from the gaze origin), ray vs the Graphic's plane, `maxDistance`, then the hit point is projected through the event camera and tested with `RectTransformUtility.RectangleContainsScreenPoint` (incl. `raycastPadding`) and `Graphic.Raycast` (every `ICanvasRaycastFilter`: `CanvasGroup.blocksRaycasts` / `ignoreParentGroups`, Mask, RectMask2D, Image alpha hit test).
4. Ordering (EventSystem `RaycastComparer`): sorting layer, sorting order, then `Graphic.depth` inside one Canvas hierarchy / distance between Canvases. `GazeUIElement.recordAsTarget = false` removes a Graphic from the candidates (the ray continues beneath it).
5. A hit is always on a **specific Graphic's rectangle**; a ray that crosses a Canvas plane without touching a Graphic is "no UI hit". Text / TMP hit at rectangle level, not glyph level.

Runs only for rows with a real eye-gaze ray (`combined_valid = 1`) unless `uiRaycastOnHeadFallback`; head-fallback rows are still written with `ui_ray_evaluated = 0`.

## 5. Hit resolution / priority (`GazeTargetResolver`)

Both raw results are recorded independently. The primary target (`primary_target_class` = `NoHit` | `WorldObject` | `CanvasUI` | `Uncertain`, with `primary_target_basis`):

| Situation | class | basis |
|---|---|---|
| UI path not evaluated (head-fallback ray / off) | `WorldObject` if the physical ray hit, else `NoHit` | `ui_not_evaluated` |
| neither hits | `NoHit` | `no_hit` |
| UI only | `CanvasUI` | `ui_only` |
| physical only | `WorldObject` | `world_only` |
| both, UI at or in front of the physical hit (<= 1 mm behind counts as coplanar) | `CanvasUI` | `ui_in_front` |
| both, physical hit in front, and it is an **established occluder** (non-trigger collider with an enabled, active, opaque, non-shadow-only Renderer, or a TerrainCollider) | `WorldObject` | `world_occludes_ui` |
| both, physical hit in front, occluder **not** established (trigger volume, collider-only object, transparent material) | `Uncertain` | `occlusion_not_established` |

A UI hit is never created from an inactive / invisible / blocked Graphic, so those can never be primary. Limitation: an object with no collider in front of a UI element is seen by neither ray; the UI then wins as `ui_only`.

## 6. ROI classification (extension of `AoiClassifier`, not a second system)

`AoiClassifier` gains `UiSemanticClass = "UI"` (the class it already used for colliders under a Canvas), `UiRoiLabel(category, explicitLabel)` (`UI_<CATEGORY>` default, e.g. `UI_BUTTON`) and `PrimaryRoi(...)`. The physical classification is untouched.
Automatic categories (panels without a marker): Graphic on the Canvas GameObject -> `panel`; Graphic under a `Selectable` -> `button`; `Text` / TMP -> `text`; other Image / RawImage -> `image`.

`GazeUIElement` fields: `stableId`, `displayName`, `category` (Auto, Panel, Button, Text, Image, QuestionnaireOption, DestinationPanel, TargetPreview, ContinueControl, Other), `roiLabel`, `recordAsTarget`, `applyToChildren`, `hitWhenNotRaycastTarget`.

### Panel-level configuration (current state of the experiment scene)

The study only needs to know WHICH PANEL is looked at, not which element inside it. Each of the 12 experiment panel templates (UIManager slots, scene instances of `ModalSingleButton`) carries **exactly one** `GazeUIElement`, on the template **root** GameObject
(the one that also carries the Canvas and `PanelController`), configured as `category = Panel`, `applyToChildren = true`, `recordAsTarget = true`, `hitWhenNotRaycastTarget = false`. No child Graphic has a marker. The marker is a scene-instance override (an "added component"
on the prefab instance), so the XRI package prefab is untouched and `UIManager`'s runtime clones inherit it.

| UIManager slot | scene template | `stableId` = `roiLabel` | displayName |
|---|---|---|---|
| part1StartPanelTemplate | PracticePhaseStart | `PART1_START_PANEL` | Part 1 Start Panel |
| part1ContinuePanelTemplate | PracticePhaseContinue | `PART1_CONTINUE_PANEL` | Part 1 Continue Panel |
| part1EndPanelTemplate | PracticePhaseEnd | `PART1_END_PANEL` | Part 1 End Panel |
| part2StartPanelTemplate | ExplorationPhaseStart | `PART2_START_PANEL` | Part 2 Start Panel |
| part2ContinuePanelTemplate | ExplorationPhaseContinue | `PART2_CONTINUE_PANEL` | Part 2 Continue Panel |
| part2EndPanelTemplate | ExplorationPhaseEnd | `PART2_END_PANEL` | Part 2 End Panel |
| part3StartPanelTemplate | ExperimentPhaseStart | `PART3_START_PANEL` | Part 3 Start Panel |
| part3EndPanelTemplate | ExperimentPhaseEnd | `PART3_END_PANEL` | Part 3 End Panel |
| part3Dest1FullTemplate | ExperimentPhaseDestination1 | `DESTINATION1_PANEL` | Destination 1 Panel |
| part3Dest2FullTemplate | ExperimentPhaseDestination2 | `DESTINATION2_PANEL` | Destination 2 Panel |
| part3Dest1ShrunkTemplate | ExperimentPhaseDestination1Shrunk | `DESTINATION1_SHRUNK_PANEL` | Destination 1 Shrunk Panel |
| part3Dest2ShrunkTemplate | ExperimentPhaseDestination2Shrunk | `DESTINATION2_SHRUNK_PANEL` | Destination 2 Shrunk Panel |

There is **no separate Part 3 Continue panel template** in the scene: the Destination 1 / 2 panels carry the Continue button.

How a hit resolves: the topmost eligible Graphic under the ray is found as before (background Image, or the transparent `TextButton` hit area); `FindElement` then walks up from that Graphic and returns the nearest `GazeUIElement` - the panel root marker.
Recorded: `ui_element_id` = `ui_roi_label` = the panel id, `ui_element_category` = `panel`, `ui_element_has_gaze_component` = 1, `ui_interactable` blank (a control crossed inside a panel must not leak into a panel-level record), `primary_target_id` / `primary_target_roi` = the panel id.
Child names (`TextButton`, `Header Text`, ...) are never recorded. Header / Modal Text and `BuildingImage` stay `raycastTarget = false` (unchanged): the root background Image, which is a raycast target, covers the **whole** panel rectangle (verified for all 12 templates; the
only Graphic rectangle that overhangs the root is the shrunk HUD's heading rect, 12 of 50 units at the bottom, whose glyphs are top-aligned inside the root), so every visible area resolves to the panel. No layout, raycastTarget or render-mode change was needed.
Because categories use the existing vocabulary, the CSV column `ui_element_category` reads `panel` (the `UI_PANEL` form is the default ROI label of unmarked panels); the panel-specific ROI label is `ui_roi_label`.

## Data: gaze_ui_hits.csv

Optional companion file (schema **v1**, own version `GazeRecorder.GazeUiHitsSchemaVersion`; the session `schema_version` stays 17). One row per `gaze.csv` row, written only after that row was written; created lazily with the first row; keys `t_abs` + `frame_index` + `session_id` are the same values as in `gaze.csv` / `gaze_diagnostics.csv`. Blank = not applicable; `ui_hit` is blank when the UI path was **not evaluated**.

| # | Column | Meaning |
|---|---|---|
| 1 | `t_abs` | key, same as gaze.csv |
| 2 | `frame_index` | key |
| 3 | `session_id` | key |
| 4 | `ui_ray_evaluated` | 1 = the UI path ran for this row, 0 = skipped |
| 5 | `ui_ray_skip_reason` | `head_fallback_ray` when skipped |
| 6 | `ui_hit` | 1 / 0 when evaluated; blank when not |
| 7 | `ui_canvas_id` | root Canvas name without `(Clone)` |
| 8 | `ui_canvas_name` | root Canvas name |
| 9 | `ui_canvas_render_mode` | `WorldSpace` or `ScreenSpaceCamera` |
| 10 | `ui_canvas_sorting_order` | effective sorting order |
| 11 | `ui_element_id` | stable id (`GazeUIElement.stableId` or `<canvas>/<path>`) |
| 12 | `ui_element_name` | human-readable name |
| 13 | `ui_element_category` | panel / button / text / image / questionnaire_option / destination_panel / target_preview / continue_control / other |
| 14 | `ui_roi_label` | ROI label (`UI_<CATEGORY>` or explicit) |
| 15 | `ui_element_has_gaze_component` | 1 if a `GazeUIElement` supplied the identity |
| 16 | `ui_interactable` | `Selectable.IsInteractable()` of the control above the Graphic; blank if none |
| 17 | `ui_hit_point_x` | world hit point (x) |
| 18 | `ui_hit_point_y` | world hit point (y) |
| 19 | `ui_hit_point_z` | world hit point (z) |
| 20 | `ui_hit_distance` | metres from the ray origin along the ray |
| 21 | `ui_graphics_under_ray` | eligible Graphics under the ray (the winner included) |
| 22 | `world_hit` | 1 / 0: the physical raycast hit (always filled, also for head-fallback rows) |
| 23 | `world_hit_distance` | physical hit distance (always, unlike gaze.csv `roi_hit_distance`, which is blank for fallback rays) |
| 24 | `world_hit_instance_id` | same as gaze.csv `hit_instance_id` |
| 25 | `world_hit_semantic_class` | same as gaze.csv `hit_semantic_class` |
| 26 | `world_occluder_established` | 1 / 0 only when both channels hit and the world hit lies in front of the UI; else blank |
| 27 | `primary_target_class` | NoHit / WorldObject / CanvasUI / Uncertain |
| 28 | `primary_target_basis` | see section 5 |
| 29 | `primary_target_id` | UI element id, or the building id / NPC id / object name |
| 30 | `primary_target_roi` | UI ROI label, or the physical semantic class; blank for NoHit / Uncertain |

Manifest (`session_manifest.json`): `file_list` includes `gaze_ui_hits.csv`; `capture_streams_closed` includes `gaze_ui_hits` (a close failure appears in `capture_streams_failed`); block `health_gaze_ui_hits` (same writer machinery / vocabulary as every stream) and block `gaze_ui_hits`
(schema version, enabled, status, columns, policy texts, row / hit / not-evaluated / primary-class counters, registered canvases, scene scans, tested / skipped canvas counters). Writer transitions land in `capture_errors.csv` like every other stream.
Historical sessions (v11 - v17 without the file) are unchanged; the reader (`docs/tools/gaze_reader.py`: `read_gaze_ui_hits`, `read_gaze_with_ui`) reports `ui_missing` instead of failing.

## 8. Performance

No change to sampling rate, validity threshold, origin / direction conventions or fallback ray. The physical raycast is not touched. UI cost per evaluated sample: O(registered canvases x their Graphics), plane maths only, **no allocation after warm-up**
(identity strings cached per Graphic, bounded; reused buffers; no LINQ; no `FindObject` per sample; no collider added; no EventSystem created). One scene scan per recording start / scene load; per-row string building follows the existing recorder pattern
(one `string.Join` per row). A UI failure is caught and can never stop gaze recording. Measured numbers are **not** claimed: no frame-time measurement was taken on a headset.

## Verification limits

| Level | What was verified | What was not |
|---|---|---|
| Synthetic PlayMode (`GazeUiTests.cs`, `GazeUiRecordingTests.cs`) | geometry, filters, sorting, resolver policy, CSV / manifest / finalisation, physical channel unchanged | rendering, XR, real eyes |
| Real scene, EditMode offline (`GazeUiRealSceneTests.cs`) | the 12 real panel templates: render mode, raycasters, no colliders, exactly which Graphics are hit | panel placement by `UIManager` at run time, head-locked follow, the real camera |
| Python reader tests | schema, join, history | - |
| Windows Editor with the scene running / Quest Link | **not run** | everything interactive |
| Real Quest Pro | **not run** | eye data, comfort, per-eye parallax, `Camera.main` / event camera behaviour on device, frame cost, whether the panel depth marker (`Graphic.depth`) is assigned the same way under the XR render path |

Run on a headset before study use: look at a panel button and the panel background while recording; confirm `ui_hit = 1`, the expected `ui_element_id`, `primary_target_class = CanvasUI`, and that `gaze.csv` hit columns are unchanged.
