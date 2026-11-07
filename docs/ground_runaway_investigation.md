# Participant "fall" in sessions 20261106T140500Z and 20261106T142958Z - investigation (2026-11-07)

Scope: runtime evidence from the two session folders (CSV only), the project code, and offline replays in a scratch clone of the project. **No headset session was run by me; nothing is committed.**
Chinese report with the full evidence trail: `docs/ground_runaway_investigation_report_zh.md`.

## Result in one paragraph
Both sessions were **not** a collision failure. In each, the participant stood within ~1 m of the edge of the Part 2 "Exploration Area"; `ExplorationBoundary.EnforceContainmentByShift()` then called `TryRecoverShift()`,
which computed `shift.z = target.z - q.y` (q.y = head HEIGHT) instead of `target.z - q.z`. The rig was moved by `z - height` (~640-660 m) in one frame, then by ~980-1016 m **every frame** (the head stays outside the area, the closest
area point stays at the far edge z ~ 1185, and the height is constant), so the rig left the map and then free-fell (y reached -7997 m / -31 m before the apps were quit). Ground colliders were present under the participant right up to the onset
(HMD height above ground 1.22-1.67 m in 6 500 pre-onset samples, no missing ground sample before the onset). `GroundFallRecovery` could not help: a >2.5 m per-frame move was classed as a teleport and the safe position was reset every frame.
The bug is in `ExplorationBoundary.cs` since commit fb0bf256 (2026-10-26); the TIN / collider repair was unrelated to it.

## Evidence (all re-computable)
| Item | 14:05 session | 14:29 session |
|---|---|---|
| Archive | `.zip` truncated (1.79 GB of a 5.2 GB `npc_state.csv`); 7 files recoverable; **no `trajectory.csv`, no manifest** | folder, complete, all 12 files |
| Platform | not recorded (manifest missing) | `WindowsEditor`, `is_editor: True`, `windows_editor_openxr` |
| Frame rate | ~3.3 Hz gaze rows (5 000-NPC pool, 600 s warm-up) | ~17.7 Hz (1 000 NPCs) |
| Last grounded sample | gaze row t_abs 1284.92: HMD (2114.43, 168.864, 809.08), ground_y 167.336, height 1.528 | trajectory t_abs 368.486: rig (1966.97, 205.560, 865.79), view height 1.508 |
| Onset | next sample t_abs 1285.32: z 809 -> 1450.77 (+641.7), ground_y NaN | next sample t_abs 368.558: z 865.79 -> 1525.94 (+660.15) |
| Then | +1016 .. 1020 m per frame (grows slowly as y falls: shift.z = 1185 - y) | +979.53 m per frame = 1185.1 - 205.56 |
| Phase | `p2_task2` (Part 2) | `p2_continue_pending` (Part 2) |
| Registered teleport near onset | none | none (`teleport_epoch` stays 19) |
| Replay of the production containment loop at the logged head position | - | shifts (0.30, 660.42), (78.83, 980.89), (0.0, 980.89) ... vs logged (0.18, 660.15), (78.65, 979.53), (-0.03, 979.52) |

Code: `Assets/Scripts/ExplorationBoundary.cs` (before the fix) line 375 `shift = new Vector3(target.x - q.x, 0f, target.y - q.y);` (target is a Vector2 of x,z), called from `EnforceContainmentByShift()` (line 349/359), enabled by `enforceContainment: 1`, `containHeadPosition: 1`, `safetyRecoveryMode: 0` in the scene.
`Assets/Scripts/GroundFallRecovery.cs` (before) lines 97-99: jump > `teleportJumpMeters` -> `hasSafe = false`, cooldown reset, no recovery path.

## What was changed
* `ExplorationBoundary.cs`: `q.y` -> `q.z`; new serialized `maxContainmentShift` (30 m): a larger correction is refused with one warning (a geometry error must never throw the rig out of the map).
* `GroundFallRecovery.cs`: new recovery path B - a jump whose DESTINATION has no Ground support after `voidDisplacementSeconds` (0.5 s) is undone (rig returns to the last supported position before the jump); real teleports land on ground and are never undone.
  Optional event-driven CSV `ground_recovery_<UTC>.csv` in `Application.persistentDataPath` (inventory at start, jump / support lost / recovery rows, 1 Hz while unsupported). No recorder schema is touched.
* `GroundNavFix.cs`: (superseded 2026-11-07) an `enabled = true` write was added here and then REMOVED again: the nine disabled modifiers are the intended configuration, see `docs/navmesh_modifier_config.md`.
* Tests: `Assets/Tests/EditMode/GroundRunawayRegressionTests.cs` (synthetic, 9 cases), one real-scene replay in `GroundNavRegressionTests.cs`. Against the pre-fix scripts the boundary and runaway tests fail (shift -200 m at height 200, 660.42 m at the logged position); with the fix 15/17 pass.
* Backups: `Backups/GroundRunaway_20261107/`.

## Working-copy findings (not changed by me)
* The uncommitted `LyonVRVity35.unity` (saved 2026-11-07 12:57) has `Grid Area`, `Organic Area`, `Practice Area`, `NavMesh`, `NavMeshGroundProxy` all inactive, and **9 NavMeshModifier components saved with `m_Enabled: 0`** (the 8 TIN "ignore from build" modifiers and the gap-patch one; HEAD has 1 disabled).
  With them disabled a re-bake would take the 20 visual TIN renderers (flipped triangles included) as NavMesh sources again (checked: 20 visual TIN sources vs 0 with them enabled). The already baked NavMeshData asset is unaffected. (Update 2026-11-07: this disabled state is intentional and must be preserved - see `docs/navmesh_modifier_config.md`; the 20 visual TIN sources are accepted.)
* Ground colliders are active only if the area groups are active at run time; the sessions' `ground_y` values show they were, under the participant, in both runs. Which objects were toggled is not recorded.
* Two failing tests remain, both caused by that working-copy state: `NavMeshSurface_CollectsNoWalkableBuilding_AndOnlyRepairedProxiesAsTin` (disabled modifiers, true positive) and the older `EveryWiredStartAnchor_...` (it only activates TIN groups, not `Grid/Organic Area`).

## Not verified
Quest Pro standalone behaviour; the 14:05 trajectory (file lost in the truncated zip); whether `GroundFallRecovery` was present in the tested build (it writes no CSV row in the old revision); Omnideck vs thumbstick input at the onset.
