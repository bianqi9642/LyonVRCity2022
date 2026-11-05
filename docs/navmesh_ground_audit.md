# NavMesh / ground-collision audit and repair (2026-11-05)

Scope: roof-top polygons in the NavMesh, "holes" in the TIN ground (navigation and physics), participants falling through the ground, and the tooling that proves the fixes.
Everything below was measured in the Editor / batch mode on a scratch clone of the project. **No Quest Pro / headset session was run.** Nothing is committed.
Full Chinese report: `docs/navmesh_ground_audit_report_zh.md`. Raw before / after reports (CSV + summary): `Diagnostics/NavMeshGround/{before,after,seams}/` (outside `Assets`).
Backups of the files that were replaced: `Backups/NavMeshGroundFix_20261105/` (scene, the three NavMesh assets, `NavMeshAreas.asset`, `TagManager.asset`, with `SHA1SUMS.txt`).

## 1. How the NavMesh is built (verified, not assumed)

* One `NavMeshSurface` (GameObject `NavMesh`, scene root of `Assets/Scenes/LyonVRVity35.unity`): Collect Objects = All Game Objects, Use Geometry = Render Meshes, Include Layers = Everything, Default Area = Walkable,
  Override Voxel / Tile Size off, Min Region Area 2. The agent is Agent Type 0 from `ProjectSettings/NavMeshAreas.asset`: radius 0.3, height 2, max slope 60, step height 0.75, voxel 0.1 (= radius / 3), tile 256.
  (The scene's own legacy `NavMeshSettings` block, 0.5 / 2 / 45 / 0.4, is not used by the surface.) Data: `NavMesh-NavMesh 2.asset` (the two other NavMesh assets in that folder are not referenced).
* TIN render meshes are on layer 0 (only their `MeshCollider` objects are on layer 3 "Ground"); building render meshes are on layer 0 as well. Layers therefore cannot separate ground from roofs.
* BATI buildings are one glTF mesh each (`geometry0` = walls + roof) plus a child `geometry0_1` (a flat street-level footprint plate).
* The scene saves the terrain groups (`Organic Area/TIN_Textured`, `Grid Area/TIN_Textured`, `Practice Area`) **inactive**; the audit tools switch them on in memory only.
* The participant rig is `XR Interaction Setup/XR Origin (XR Rig)`: `CharacterController` (layer 2) + `AutoGroundAlign` + XRI continuous move (`OmnideckMove`, gravity on, mode AttemptingMove). `OVRCameraRig` is its child.

## 2. Findings

| Problem | Root cause | Evidence (before -> after) |
|---|---|---|
| Roof-tops walkable | The surface collects every MeshRenderer on every layer and no building carries a NavMeshModifier; all 1454 building sources were Walkable | NavMesh polygons floating > 0.5 m above the ground: 43 095 (309 953 m2, 1 712 roof islands) -> 0 |
| "Holes" in the TIN | The TIN has **no large accidental holes** (hair-line cracks only). It has 573 triangles (2 127 m2) with **flipped winding** (normal pointing down), 3 025 degenerate and 5 108 duplicate triangles | Physical ground missing at 492 samples -> 0 |
| NavMesh gaps | Mostly legitimate: slopes > 60 deg (3 321 samples), erosion beside steep slopes / buildings, the SouthRiver gap patch (deliberately `ignoreFromBuild`). Only the flipped triangles (468 samples) were defects | Candidate-hole samples 5 424 -> 4 860 (flipped-triangle causes 468 -> 3) |
| Falling | Down-facing triangles are invisible to one-sided mesh colliders: the CharacterController (rig parameters) fell through 54 of 128 probed flipped triangles, the AutoGroundAlign ray missed 62; AutoGroundAlign only looks 0.1 m above the base so it never recovers a sunken rig | 74/128 landed, ray hits 66/128 -> 128/128 and 128/128 |

Bake settings are not the cause: agent radius 0.3 / voxel 0.1 only erode a thin band beside walls; no setting was changed.

## 3. What was changed

1. `Assets/Models/TIN/Repaired/*.asset` (new, 18 meshes): derived copies of every TIN tile, local space, welded 1 mm, degenerate + duplicate triangles dropped, down-facing triangles re-wound. Positions are unchanged (height / slope preserved, nothing is filled). The imported glTF files are untouched.
2. The 20 Ground-layer TIN `MeshCollider`s use these meshes (`sharedMesh`); bridges and the gap patch are unchanged.
3. New scene root `NavMeshGroundProxy` (18 invisible, material-less MeshRenderers at the tile transforms) is the NavMesh ground source; `TIN_Textured` groups and the Practice tiles carry `NavMeshModifier(ignoreFromBuild)` so the visual terrain is not a source.
4. 7 building roots (maximal pure-BATI hierarchy nodes) carry `NavMeshModifier(overrideArea, Not Walkable, applyToChildren)`: buildings stay obstacles, roofs can never be walkable. (Ignore From Build would open the ground inside buildings to the street.)
5. `XR Interaction Setup` carries `NavMeshModifier(ignoreFromBuild)` (the rig's own poke / vignette meshes were NavMesh sources).
6. `NavMeshSurface.layerMask` = Default + Ground + TargetPreview (was Everything). Nothing else on the surface changed.
7. New NavMeshData `Assets/Scenes/LyonVRVity35/NavMesh-NavMesh_GroundOnly.asset`, assigned to the surface. `NavMesh-NavMesh 2.asset` stays on disk (rollback = re-assign it).
8. `Assets/Scripts/GroundFallRecovery.cs` on the XR Origin: secondary safety net (see its header for the activation conditions).
9. Tooling: `Assets/Editor/NavMeshAudit/*` (menu `Tools > NavMesh Ground Audit`), tests `Assets/Tests/EditMode/GroundNavRegressionTests.cs`.

Re-running `Tools > NavMesh Ground Audit > Apply ground fix` is idempotent (same scene, same numbers) and rebuilds the repaired meshes, proxies, modifiers and the NavMeshData from the current TIN.

## 4. Verification run

* Diagnostics D1-D6 before / after (see the report files), Editor-mode connectivity (`NavMesh.CalculatePath`), NPC route points (987 points, 944 have NavMesh within 0.5 m both before and after).
* EditMode tests (`-runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests`): 7 pass on the repaired scene; on the original scene the 5 new geometry tests fail (mutation check) and the existing start-point test passes in both.

## 5. Known limits

* Not repaired, deliberately (needs a decision, not a script): (a) one 160 m2 internal opening in the Practice TIN at (2929.5, 160.6, 2465.3) at river-bed elevation - probably water, not filled; (b) tile overlaps
  (`Diagnostics/NavMeshGround/seams/D2b_tile_overlaps.csv`): 29 324 sampled cells (117 296 m2) are covered by two tiles; the two duplicated Grid tiles agree exactly, but Grid / Organic overlaps disagree vertically
  (mean 0.31 m, up to 8.2 m, 2 559 cells > 0.25 m). Both surfaces carry colliders and NavMesh; AutoGroundAlign uses the closest hit.
* Ground inside building footprints (TIN under a roof) is still NavMesh in ~76 700 sampled cells; those polygons are isolated interior islands (a small edge band, ~376 polygons, touches the street network). Unchanged from before.
* The scene is saved with the terrain groups inactive. Without active terrain there is no ground collider at all. Verify the activation state you actually run / build with.
* Offline geometry only. Quest / Omnideck runtime behaviour (tracking height, gravity feel, recovery trigger comfort) must be tested on the device.
