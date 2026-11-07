# NavMesh source configuration: nine intentionally disabled NavMeshModifiers (2026-11-07)

**Intended production state (do not "fix"):** nine `NavMeshModifier` components are saved with `m_Enabled: 0`:
`Grid Area/TIN_Textured`, `Organic Area/TIN_Textured`, `Organic Area/TIN_Textured/SouthRiver_TIN_GapPatch`, and the six `Practice Area/gml_*` tile roots (all Ignore From Build, Apply To Children).
A disabled modifier is ignored by the baker, so the **visual TIN renderers AND the repaired `NavMeshGroundProxy` meshes are both NavMesh sources**. Nothing in the project enables them:
`GroundNavFix.SetModifier` no longer writes `enabled` (it only adds a missing modifier and sets its ignore / area fields), and `GroundNavRegressionTests` guards this (file scan + the nine paths must stay disabled).
Eight other modifiers are enabled by design: seven building roots (Override Area = Not Walkable, roof exclusion) and `XR Interaction Setup` (Ignore From Build).

## Verified with the production NavMeshSurface and the modifiers exactly as saved (Editor, in memory; report: `Assets/Editor/NavMeshAudit/GroundNavAuditSourceReport.cs`)
| Region | visual TIN sources (tris) | repaired proxy sources (tris) | bridge sources | building sources (walkable) | polygons: saved asset -> fresh bake |
|---|---:|---:|---:|---:|---:|
| PONT | 0 | 0 | 7 (68) | 0 | 122 -> 122 |
| Practice | 6 (35 151) | 6 (35 010) | 0 | 574 (0) | 15 403 -> 16 657 |
| Organic | 6 (156 337) | 6 (153 773) | 0 | 629 (0) | 30 230 -> 30 423 |
| Grid | 8 (47 041) | 6 (41 768) | 0 | 825 (0) | 18 825 -> 18 825 |

* Both TIN sets overlap on the same surface, so polygon counts rise slightly (Practice +8 %, fragmentation) while the **NavMesh area is unchanged** (Practice 777 344 -> 777 170 m2, Organic 975 280 -> 975 323, Grid and PONT identical).
* Sampled coverage (2 m grid) saved vs fresh: Grid 98.81 / 98.81 %, Organic 97.75 / 97.75 %, Practice 98.91 / 98.89 %, PONT decks 96.87 / 96.84 %. Physical ground missing: 0 / 0.
* Rooftops: 0 building sources are Walkable. The fresh bake has 11 roof-like polygons (2.8 m2, Organic, y = 189.8, ~21.8 m above ground). They do NOT come from a building: the source is the `Visuals` cylinder of
  `TeleportationArea/OrganicTeleportPoint/Unexplored/Teleport Anchor Variant` lying on a roof (576 teleport-anchor visuals are collected as Walkable sources in the current scene; only this one sits above ground).
  Not changed (decision for the owner: e.g. Ignore From Build on `TeleportationArea`).

## Repeating the bake
1. Make sure the `NavMesh` object (NavMeshSurface) and the area groups you want baked are active. Do not touch the nine modifiers.
2. `Tools > NavMesh Ground Audit > Run source collection report (modifiers as saved)` first: it lists every modifier with its enabled state and bakes in memory only.
3. To bake into the project: either the NavMeshSurface Inspector "Bake" button, or `Tools > NavMesh Ground Audit > Apply ground fix` (idempotent; keeps modifier enabled states; writes `NavMesh-NavMesh_GroundOnly.asset`).
4. Re-run the EditMode tests (`-runTests -testPlatform EditMode -assemblyNames GazeRecorder.EditModeTests`).
Editor verification only; no Meta Quest Pro runtime verification.
