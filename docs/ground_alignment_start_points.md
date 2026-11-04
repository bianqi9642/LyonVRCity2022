# Ground alignment of the experiment start points (audit and correction, 2026-11-04)

Scope: every position at which the experiment initialises or repositions the participant (Part 1 start, Part 2 start and cross-bank transition, Part 3 starts GE / GU / OE / OU).
Not in scope and untouched: AutoGroundAlign, layers and masks, the 15 terrain layer assignments, terrain activation state, NPCs, ExplorationBoundary, teleport rules, river teleports, UI, audio, gaze / face / v17 data capture.
Nothing was committed. Offline (Editor / batch-mode) measurements only; no Quest Pro or headset session was run.

## 1. Definition: the ground-contact reference

* The reference is the **XR Origin root** (`XR Interaction Setup/XR Origin (XR Rig)`), which is also `ExperimentManager.playerRig` and the host of `AutoGroundAlign` (all one Transform).
  Its world Y is the height of the participant's floor (tracking origin: `Camera Offset` is `CameraYOffset` 1.36144 m above it; Main Camera local Y 0). It is **not** the HMD / camera height.
  The rig's `CharacterController` (centre y 0.7607, height 1.36144, skin 0.08) bottoms out 0.08 m above this point; it is on layer 2 (Ignore Raycast) and is not what is aligned.
* `ExperimentManager.TeleportPlayerTo(dest)` assigns `playerRig.position = dest.position` and `playerRig.rotation = dest.rotation`: **no vertical offset**. A start anchor's world Y is therefore the rig's ground-contact height, and
  the correct anchor Y is the height of the walkable surface under its X / Z.
* `AutoGroundAlign` (`raycastDistance 3`, mask Ground = layer 3, `smoothSpeed 10`, `heightOffset 0`) casts from `base + 0.1 m` straight down every frame and lerps base Y to the closest hit. It only hits the one-sided terrain mesh when
  the base is **less than 0.1 m below** the surface; a base further below it is never corrected. It is a smoothing / terrain-following aid, not a repair for wrong start heights, and it was not changed.
* ΔY below is `rig base Y - ground Y` (negative = below the ground). Tolerance 0.02 m.

## 2. Start-point inventory

Start anchors are plain scene GameObjects (identity rotation, scale 10, parents translation-only) under `EnvironmentConfig/Participant/{PracticePhase|ExplorationPhase|ExperimentPhase}`; world = local + parents.
`EnvironmentConfig` sits at local (2788.3801, 0.6802473, 704.7568), so **world Y = local Y + 0.6802473**.

| Part / phase | Anchor (ExperimentManager field) | World X, Z | Ground collider (Ground layer, MeshCollider) | Role |
|---|---|---|---|---|
| Part 1 start | `PracticePhase/StartPoint` (`part1StartPosition`) | 2588.880, 2629.957 | `gml_9fb11684…` | start |
| Part 2 group 0 start / group 1 transition | `ExplorationPhase/StartPointO` (`part2Groups[0].startPositions[0]`, `part2Groups[1].teleportPositions[0]`) | 2228.180, 810.957 | `gml_4811c913…` | start + cross-bank transition (same Transform) |
| Part 2 group 1 start / group 0 transition | `ExplorationPhase/StartPointG` (`part2Groups[1].startPositions[0]`, `part2Groups[0].teleportPositions[0]`) | 2393.180, 807.357 | `gml_4d5046a5…` | start + cross-bank transition (same Transform) |
| Part 3 GE | `ExperimentPhase/StartPointGE` (`part3StartGE`) | 2393.180, 807.357 | `gml_4d5046a5…` | start |
| Part 3 GU | `ExperimentPhase/StartPointGU` (`part3StartGU`) | 2332.780, 402.457 | `gml_2c9c7a15…` | start |
| Part 3 OE | `ExperimentPhase/StartPointOE` (`part3StartOE`) | 2228.180, 810.957 | `gml_4811c913…` | start |
| Part 3 OU | `ExperimentPhase/StartPointOU` (`part3StartOU`) | 2106.880, 418.757 | `gml_e3cdfa42…` | start |

Nine wiring entries, seven distinct Transforms. Reported but **not start points and not changed**:
* The rig's own saved pose (1991.30, 168.449, 551.00): the idle position before any Part. Every Part teleports away from it before the participant does anything, so it is never a Part start. The terrain there is 168.657 (ΔY -0.208, AutoGroundAlign ray starts under the surface);
  it matters only if a participant ever stands at the idle pose with the terrain active - whether that matters was not decided here (ambiguous, left as is).
* Ordinary teleport destinations (`RiverTeleportPoint` pairs, the guarded XRI teleport) and the NPC spawner's own `StartPoint` object: different mechanisms, excluded by the task.

## 3. Audit table: before and after (world heights, real colliders)

Surface = closest Ground-layer MeshCollider hit by a ray from 30 m above; the collider and the visible renderer bounds agree within 8-16 mm, each point has a single Ground surface, and the local slope is <= 1 cm within 0.1 m.
"AGA" = the AutoGroundAlign ray from base + 0.1 m; "settle" = what its lerp loop would still do to the base after placement (simulation, 400 steps).

| Anchor | Local Y before -> after | World Y before | Ground Y | ΔY before | AGA before (ray origin vs surface; hit; settle) | World Y after | ΔY after | AGA after |
|---|---|---|---|---|---|---|---|---|
| StartPoint (Part 1) | 166.6 -> unchanged | 167.2803 | 167.2891 | **-0.0088** (within tolerance) | +0.091 above; hit; +0.0088 | 167.2803 | -0.0088 | unchanged: hit, settle +0.0088 |
| StartPointO (Part 2 / transition) | 168.5 -> 168.76285 | 169.1803 | 169.4431 | **-0.2628** | 0.163 *below*; **no hit**; 0 (stays under the terrain) | 169.4431 | 0.0000 | +0.100 above; hit; 0 |
| StartPointG (Part 2 / transition) | 167.35 -> 167.47295 | 168.0303 | 168.1532 | **-0.1229** | 0.023 *below*; **no hit**; 0 | 168.1532 | 0.0000 | hit; 0 |
| StartPointGE (Part 3) | 167.35 -> 167.47295 | 168.0303 | 168.1532 | **-0.1229** | 0.023 below; **no hit**; 0 | 168.1532 | 0.0000 | hit; 0 |
| StartPointGU (Part 3) | 168.46 -> 168.51935 | 169.1403 | 169.1996 | **-0.0593** | 0.041 above; hit; +0.0593 | 169.1996 | 0.0000 | hit; 0 |
| StartPointOE (Part 3) | 168.5 -> 168.76285 | 169.1803 | 169.4431 | **-0.2628** | 0.163 below; **no hit**; 0 | 169.4431 | 0.0000 | hit; 0 |
| StartPointOU (Part 3) | 168.73 -> 168.68825 | 169.4102 | 169.3685 | **+0.0417** (above) | 0.142 above; hit; -0.0417 | 169.3685 | 0.0000 | hit; 0 |

Raw probe output: `docs/tools/terrain_replay/starts_before_correction.csv` / `starts_after_correction.csv` (probe: `TerrainProbe.RunStarts`).
The 0.131 m figure of the earlier audit for StartPointGE was measured against the surface under the *recorded* rig X / Z (168.16); at the anchor's exact X / Z the surface is 168.1532, i.e. 0.123 m.

## 4. Root cause

Cause class (of the A-F list): **A, a stale serialized Y**, on six scene objects. The other classes were excluded with measurements:
* B (parent / coordinate space): the parent chain is translation only (the 0.68 m offset of `EnvironmentConfig` is accounted for and was identical in the initial commit); no rotation or scale on any ancestor.
* C (rig-placement offset): `TeleportPlayerTo` adds nothing; rig base Y equals anchor Y.
* D (collider mismatch): collider and renderer bounds agree to <= 16 mm; one Ground surface per point.
* E (no runtime correction): AutoGroundAlign exists, but its 0.1 m start offset and the one-sided mesh make it blind to a base lower than 0.1 m below the surface (StartPointO / OE / G / GE). The two anchors inside the window (GU, OU) would have been silently re-aligned in the first frames, which hides the error.
* F (overlapping colliders): none under any start point.

The errors are -0.263, -0.123, -0.059 and +0.042 m: **not constant**, so no global offset exists or was applied. The six Y values were authored between January and August 2026 and the Textured TIN terrain set was added on 2026-11-01;
that is consistent with the heights having been set against an earlier terrain surface, but the earlier terrain is not in the project, so this was not proven (ambiguous). Part 1 (`StartPoint`) happens to agree with the current surface to 9 mm.

## 5. Correction (scene data only)

Only the vertical `m_LocalPosition` of six Transforms in `Assets/Scenes/LyonVRVity35.unity` changed (X and Z identical to the byte; rotation and scale untouched):

| Transform | Local Y before | Local Y after |
|---|---|---|
| StartPointO | 168.5 | 168.76285 |
| StartPointOE | 168.5 | 168.76285 |
| StartPointG | 167.35 | 167.47295 |
| StartPointGE | 167.35 | 167.47295 |
| StartPointGU | 168.46 | 168.51935 |
| StartPointOU | 168.73 | 168.68825 |

**No runtime C# was changed.** Each new value = measured ground height at the anchor's X / Z - 0.6802473 (parent offset). The Part 1 `StartPoint` was left alone (|ΔY| 0.009 m < 0.02 m).
Residual after the correction: 0.0000 m for the six corrected anchors (|ΔY| <= 0.0001 m), -0.0088 m for Part 1; AutoGroundAlign hits all nine, and its settle simulation moves the base 0 mm (Part 1: 8.8 mm, a smooth lerp inside the existing steady-head gate).
The scene file also still contains the earlier terrain layer correction (the `m_Layer: 3` property overrides); that is a separate, earlier change.

## 6. How the tests protect it

* `Assets/Tests/GazeRecorder/StartPointGroundTests.cs` (PlayMode assembly, runs everywhere):
  * scene-file tests - the anchors `ExperimentManager` is wired to are exactly the audited set; their world X / Z / yaw equal the frozen values; their world Y is within 0.02 m of the frozen terrain height; the AutoGroundAlign ray origin lies above the surface.
    The terrain heights are a **frozen measurement** of the real colliders (table in the test) - they fail if an anchor is moved, an un-audited start is added, or a serialized offset comes back; they cannot notice that the terrain itself changed.
  * synthetic tests with the **real** `ExperimentManager.TeleportPlayerTo` and **real** `AutoGroundAlign` on a flat Ground collider - placement is exact (no vertical offset, only the base moves), an on-surface anchor is not moved, a base > 0.1 m under the surface is never recovered (characterisation of the defect).
    These use synthetic geometry, not the city.
* `Assets/Tests/EditMode/StartPointGroundRealSceneTests.cs` (new Editor-only assembly `GazeRecorder.EditModeTests`, category `RealScene`): opens the real scene additively, reads the wired anchors through `SerializedObject`, raycasts the **real** terrain colliders with the AutoGroundAlign parameters read from the scene,
  and asserts |ΔY| <= 0.02, a ray hit and ~0 settle movement for every wiring entry (and unchanged X / Z / rotation). It sets the terrain groups active in memory only (nothing saved) and is Ignored if the scene is already open in the Editor, the file is absent, or no terrain mesh is imported.
  Run: `Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests -testResults <xml>` (close the scene in the Editor first when running interactively).
* Mutation check: against the pre-correction scene copy both layers fail (the PlayMode scene tests list the six offending anchors with their ΔY; the EditMode test fails on the same anchors); against the corrected scene they pass.

## 7. Limits

* Offline only: geometry from the Editor / batch mode. The HMD tracking-origin height, floor calibration and the felt standing height on the Quest Pro and on the Omnideck were not measured. Re-check on the device that the participant's feet are on the visible ground at each start.
* The frozen terrain heights describe the current terrain colliders. If the TIN terrain is re-imported or edited, re-run `TerrainProbe.RunStarts` and update the table (the EditMode test then catches the discrepancy by itself).
* The measurements and the EditMode test need the terrain groups active; the saved scene keeps them as it had them. Terrain activation was not investigated or changed.
* The Editor session of the user must reload `LyonVRVity35.unity` from disk (do not save a stale in-memory copy over this change).
