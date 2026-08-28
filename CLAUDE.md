# CLAUDE.md

Guidance for working in this repository.

This is a **VR wayfinding / eye-gaze research experiment** set in a georeferenced 3D model
of Lyon. Participants wear a Meta Quest (Pro, for eye tracking), optionally walk on an
Omnifinity Omnideck treadmill, navigate the city, select target buildings, and cross the
river via paired teleport points. Head trajectory and binocular gaze are logged to CSV per
trial for offline analysis.

---

## Environment

| Thing | Value |
|---|---|
| Unity | **2022.3.53f1** (LTS) — see `ProjectSettings/ProjectVersion.txt` |
| Render pipeline | **URP 14.0.11** (`com.unity.render-pipelines.universal`). Configs in `Assets/Settings/Project Configuration/`. Meta XR Foveation on; eye-tracked foveation off. |
| XR framework | **OpenXR 1.14.1** is the **active** loader for Standalone **and** Android (`Assets/XR/XRGeneralSettingsPerBuildTarget.asset`). XR Plugin Management 4.5.1. The Oculus/legacy XR loader exists but is **inactive** — do not switch to it. |
| XR Interaction Toolkit | 2.6.5 (present; the XRI rig in the scene is a **disabled leftover** — see rig notes) |
| Meta XR SDK | **All-in-One 201.0.0** (`com.meta.xr.sdk.all` / `.core` / `.interaction` / `.interaction.ovr` / `.platform` / `.haptics` / MR Utility Kit). The **Meta XR OpenXR feature set** (`com.meta.openxr.featureset.metaxr`) is enabled for Standalone + Android. |
| Navigation | `com.unity.ai.navigation` 1.1.7 (NavMeshSurface-based) |
| Other | `com.unity.animation.rigging` 1.2.1, `com.unity.cloud.gltfast` + `draco` (city import), Omnifinity Omnideck API (git package) |

### Known configuration inconsistencies (verify before a live session — do not "fix" silently)
- `Assets/Oculus/OculusProjectConfig.asset` still has `eyeTrackingSupport: 0` (and hand/body/face = 0). Eye tracking is wired via the **OpenXR/Meta feature-set path**, not the legacy OVRManager path. Confirm the Android `com.oculus.permission.EYE_TRACKING` permission and `oculus.software.eye_tracking` feature actually land in the built manifest.
- The OpenXR "Eye Gaze Interaction Profile" is enabled inconsistently across platform entries in `Assets/XR/Settings/Open XR Package Settings.asset`.

---

## Project architecture

Three loosely-coupled runtime subsystems, joined by the `ExperimentManager` trial events:

### 1. Experiment orchestration
```
ExternalSessionController        (participant ID, selectedPart, group index; optional session_config.json)
        │
        ▼
ExperimentManager                (Part1 / Part2 / Part3 as coroutines:
        │                          teleport player → show panel → wait → spawn NPCs →
        │                          fire OnTrialStart → run duration → fire OnTrialEnd → end panel)
        ├── UIManager / PanelController   (HMD-anchored world-space confirmation panels, full + shrunk-corner)
        └── SpawnerManager               (spawns pedestrian crowd for the trial)
```
- `ExperimentManager.OnTrialStart` / `OnTrialEnd` are the **integration backbone**. Recorders and other per-trial systems subscribe to these; they are the canonical trial boundary.
- `ExperimentManager.playerRig` is the single source of truth for the player transform. `TeleportPlayerTo()` directly sets `playerRig.position/rotation`.

### 2. Data recording (per trial, CSV → `Application.persistentDataPath`)
- `TrajectoryRecorder` — head/rig pose + movement metadata.
- `GazeRecorder` — binocular `OVREyeGaze` samples + combined gaze ray + ROI raycast hit.
- Both subscribe to `OnTrialStart`/`OnTrialEnd`, share the `{subjectId}_{trialId}_..._{yyyyMMddTHHmmssZ}.csv` naming, use `Time.realtimeSinceStartupAsDouble` as the clock, and use an **open/write/close-per-row** crash-safety pattern. Keep these three properties identical across recorders so files stay mergeable offline.

### 3. Crowd simulation (NavMesh pedestrians — independent of the player)
```
SpawnerManager → AgentFactory → agent prefab
   (AgentProfile drives appearance + speed + behaviour)
        agent prefab = PedestrianAgent + MoveToTarget + UncertainBehavior
                       + AppearanceController + NavMeshAgent
AttractorArea            (samples NavMesh points inside a collider — pedestrian destinations)
```
- `MoveToTarget` is **NavMeshAgent-only**; the agent is the authoritative steering controller. `MoveToTarget` does **not** write `NavMeshAgent.speed` unless `autoSetAgentSpeed` is set — external systems (`AgentFactory`) own speed.

### Interaction convention
Everything selectable exposes the same triad so it works from an XR interactor, a UI pointer, or a mouse in the Editor:
```csharp
public void Select() { ... }                      // XR Interactor / code entry point
public void OnPointerClick(PointerEventData e)     // IPointerClickHandler
private void OnMouseDown()                         // Editor mouse testing (needs a Collider)
```
`TargetSelectable` (buildings) and `RiverTeleportPoint` (river crossing) both follow this.

---

## Important scenes

| Scene | Purpose |
|---|---|
| **`Assets/Scenes/LyonVRVity35.unity`** | **The experiment scene.** All work happens here. Contains the city model, `OVRCameraRig`, `ExperimentManager` / `SessionController` / `UIManager` / `SpawnerManager`, `GazeRecorder` + `DataRecorder`, teleport points, target buildings, NavMesh. |
| `Assets/Scenes/SampleScene.unity`, `BasicScene.unity` | Scratch / template scenes — not the experiment. |
| `Assets/Samples/**`, `Assets/EasyNpcs/Scenes/**`, `Assets/npc_casual_set_00/Scenes/**` | Third-party package sample scenes. Reference only; do not build on them. |

---

## Important prefabs

| Prefab / group | Notes |
|---|---|
| **`OVRCameraRig`** (in-scene instance, `guid 126d619cf4daa52469682f85c1378b4a`) | The **active** player rig. `disableEyeAnchorCameras = 1`. Scene-added components: **two `OVREyeGaze`** (Eye = Left / Right, `ConfidenceThreshold 0.5`, ApplyPosition/ApplyRotation = 1), a `UniversalAdditionalCameraData`, a `NavMeshObstacle`, and OVR hand/controller anchors. `AutoGroundAlign` keeps it on the terrain. |
| `Complete XR Origin Set Up Variant`, `XR Origin (XR Rig)` (in-scene, `m_IsActive: 0`) | **Disabled** leftover XRI rigs. Do not re-enable without an explicit request. |
| `Assets/Prefabs/BATI/<arrondissement>/<block>/*.prefab` | The city buildings (thousands), converted from glTF. Some are wired as experiment targets via `TargetSelectable`. |
| `Assets/Prefabs/TIN/gml_*.prefab` | Terrain (triangulated irregular network). Carries the ground colliders + NavMesh source geometry. |
| Pedestrian agent prefab (referenced by `AgentFactory.agentPrefab`) | `PedestrianAgent` + `MoveToTarget` + `UncertainBehavior` + `AppearanceController` + `NavMeshAgent`. |
| NPC art prefabs | `Assets/EasyNpcs/`, `Assets/npc_casual_set_00/`. |
| Panel templates | Referenced by `UIManager` explicit slots (Part1/2/3 start / continue / end / destination panels). |

City prefabs are generated by editor tools in `Assets/Editor/`: `CityKMLImporter`, `GeoJsonImporter`, `ConvertGltfToPrefabs`, `CityBakeTransforms`, `NavMeshVerticalProjector`, `BATIMaterialTool` + batch material scripts. Re-running these is a deliberate, destructive re-import — never do it as a side effect.

---

## Important scripts (`Assets/Scripts/`)

### Experiment core
| Script | Role |
|---|---|
| `ExperimentManager.cs` | Trial state machine (Part 1/2/3), `OnTrialStart`/`OnTrialEnd` events, `playerRig`, `TeleportPlayerTo()`, target enable/disable per part. |
| `ExternalSessionController.cs` | Session config (participant ID, part, group index); optional load/save of `session_config.json` in `persistentDataPath`. |
| `UIManager.cs` / `PanelController.cs` | World-space VR panels anchored to the HMD camera. `PanelController.Setup()` preserves inspector template text when passed null/empty. |
| `TrajectoryRecordingManager.cs` | Coordinates trajectory recorders. |

### VR interaction
| Script | Role |
|---|---|
| `RiverTeleportPoint.cs` *(untracked / new)* | Paired riverbank teleport points; 1:1 `pairedPoint`; moves `playerRig` transform on `Select()`; cooldown; `OnPlayerTeleported` event. |
| `TargetSelectable.cs` | Building selection → `ExperimentManager.RegisterTargetSelection(targetId)`. |
| `AutoGroundAlign.cs` | Downward raycast each frame to keep the rig on uneven terrain. |

### Eye tracking
| Script | Role |
|---|---|
| `GazeRecorder.cs` *(untracked / new)* | **Primary eye-tracking script.** Reads two `OVREyeGaze` components, thresholds on `Confidence` (no boolean validity flag exists in the SDK), builds a combined binocular ray (average / single-eye fallback / head-forward fallback), optional ROI raycast, CSV per sample, trial-event driven, Android `requestEyeTrackingPermission()`. |

### Crowd simulation
| Script | Role |
|---|---|
| `SpawnerManager.cs` | Spawns/despawns the pedestrian crowd; holds `defaultProfile`. |
| `AgentFactory.cs` | Instantiates agent prefab, applies `AgentProfile` (appearance + speed multipliers + navmesh settings). |
| `MoveToTarget.cs` | NavMeshAgent-only mover; owns rotation smoothing; animator driven from measured speed; external pause API. |
| `PedestrianAgent.cs`, `UncertainBehavior.cs` | Per-agent pedestrian behaviour / hesitation. |
| `AgentProfile.cs` (ScriptableObject), `AppearanceController.cs` | Agent config + skinned-mesh visual application. |
| `AttractorArea.cs` | `SamplePointOnNavMesh()` inside a collider. |

### Editor tools (`Assets/Editor/`)
City import / bake pipeline — see prefabs section. Destructive; run only on explicit request.

---

## Coding conventions

- **Language / comments:** C#, **all comments in English**. Existing scripts carry a top-of-file block comment explaining the script's role, its integration points, and design rationale — match that style for new scripts.
- **MonoBehaviour patterns already established — follow them:**
  - `[Header(...)]` / `[Tooltip(...)]` on every serialized field; `[Range]` where bounded.
  - `[DisallowMultipleComponent]` on singleton-ish components; `[RequireComponent(...)]` for hard dependencies.
  - Resolve scene references in this order: explicit inspector slot → `FindObjectOfType<T>()` in `Start()` → warn via `Debug.LogWarning` if still null. Never assume a reference is non-null.
  - Gate diagnostic logging behind a public `bool enableDebugLogs` field (see `SpawnerManager`, `MoveToTarget`, `GazeRecorder`). Never log per-frame / per-sample data.
  - Prefix logs with `[ScriptName]`.
- **Events over polling** for cross-system signals (`Action` events like `OnTrialStart`, `OnPlayerTeleported`). Always unsubscribe in `OnDestroy`.
- **Time:** use `Time.realtimeSinceStartupAsDouble` for any recorded timestamp (timeScale-independent, matches existing recorders). Use `Time.deltaTime` for gameplay motion.
- **File I/O:** write to `Application.persistentDataPath`. For anything that must survive a crash/headset-removal, use the open/write/close-per-row pattern and wrap in try/catch.
- **CSV:** lightweight comma-join, no quoting; sanitize fields by replacing `, \n \r` with spaces (see `GazeRecorder.SafeCsvField`).
- **Naming:** `PascalCase` types/methods/public fields, `camelCase` locals/privates. (This matches the existing code, including public fields in PascalCase-ish inspector style — stay consistent within a file.)
- Prefer editing an existing script over adding a parallel one. New scripts need a matching `.meta` (Unity generates it — commit it).

---

## Rules for modifying XR-related code

1. **Do not change the active XR loader or plugin stack.** OpenXR (not Oculus/legacy) is active for both Standalone and Android. Do not enable the Oculus loader, and do not touch `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` or `Packages/manifest.json` XR entries without an explicit request.
2. **The active rig is the `OVRCameraRig`.** The XRI `XR Origin` rigs in the scene are intentionally disabled. Do not re-enable, delete, or "clean up" either rig. Do not migrate the rig between OVR and XRI.
3. **Locomotion is OVR / Omnideck + scripted teleports**, not XRI `LocomotionSystem`/`ContinuousMoveProvider`. Player repositioning goes through `ExperimentManager.playerRig` (`TeleportPlayerTo`) or a `RiverTeleportPoint`. Do not add an XRI locomotion provider to move the player.
4. **Preserve the `Select()` + `OnPointerClick` + `OnMouseDown` interaction triad** on any selectable. The `OnMouseDown` path is what makes Editor testing without a headset possible — never remove it.
5. Teleport / rig-move code must not fight `AutoGroundAlign` (it sets rig Y every frame) and must not disturb `NavMeshAgent` state — only set the rig transform, mirroring the existing `TeleportPlayerTo` / `RiverTeleportPoint.TeleportPlayerTo` style.
6. Keep the OVR anchor hierarchy (`CenterEyeAnchor`, controller/hand anchors) intact — `GazeRecorder` and `UIManager` reference the HMD camera through it.
7. Meta XR SDK API surface has been renamed repeatedly across versions. When touching OVR/Meta API calls, verify names against `com.meta.xr.sdk.core@201.0.0` in `Library/PackageCache` before relying on them; let the compiler point at breakages rather than guessing.
8. XR/OpenXR `.asset` settings files are already dirty in the working tree. Do not add unrelated changes to them.

## Rules for modifying eye-tracking code

1. **`GazeRecorder.cs` is the single eye-tracking entry point.** Route new gaze features through it; do not add a second component that reads `OVREyeGaze` in parallel.
2. **Gaze source is two `OVREyeGaze` components** (Eye = Left / Right) on the `OVRCameraRig`. Keep both. If you add a combined/CenterEye gaze source, keep per-eye recording too — the analysis pipeline expects `left_*` / `right_*` / `combined_*` columns.
3. **Validity is derived from `OVREyeGaze.Confidence >= minConfidenceForValid`.** The SDK exposes no boolean validity flag — do not invent one or assume one appeared.
4. **Do not break the CSV schema.** `GetCsvHeader()` and the row in `SamplingLoop()` must stay in lockstep (same columns, same order). Append new columns at the **end** only; never reorder or rename existing ones. Announce any schema change in the commit message.
5. Keep gaze recording **trial-event driven** (`OnTrialStart`/`OnTrialEnd`) and keep the clock basis (`Time.realtimeSinceStartupAsDouble`) and filename convention identical to `TrajectoryRecorder` so gaze and trajectory rows remain alignable.
6. Preserve the crash-safety guarantees: header-once, open/write/close per row, `OnApplicationPause`/`OnApplicationQuit` stop hooks, `requestEyeTrackingPermission()` on Android.
7. Eye tracking only produces real data on Quest Pro hardware (or the Meta XR Simulator if supported). Code must degrade gracefully to "all-invalid samples" everywhere else — never assume a valid gaze sample.
8. If you enable eye tracking in project settings, do it consistently across **all** of: `OculusProjectConfig` (`eyeTrackingSupport`), the OpenXR Meta feature set, and the Eye Gaze Interaction Profile per platform — and call it out for the user to verify on-device.

## Rules for preserving existing functionality

- **Additive by default.** Prefer new optional serialized fields (defaulting to current behaviour) and new methods over changing existing signatures, defaults, or control flow.
- Do not change public field names/types on scripts that are wired in `LyonVRVity35.unity` — it silently drops inspector references. If a rename is unavoidable, say so explicitly and note that the scene/prefab references must be re-wired.
- Keep the trial lifecycle contract intact: `OnTrialStart` fires once after the start panel is confirmed and NPCs are configured; `OnTrialEnd` fires once when the timed phase ends. Recorders depend on exactly-once semantics.
- Don't alter `NavMesh` assets, baked data, or the city/terrain prefabs as a side effect. `Assets/Scenes/LyonVRVity35/NavMesh-NavMesh 2.asset` is already modified in the tree — leave further changes out unless asked.
- `MoveToTarget` must remain NavMeshAgent-authoritative and must not write `agent.speed` unless `autoSetAgentSpeed` is true.
- Leftover disabled objects, sample scenes, and package samples are not dead code to prune — leave them.
- When behaviour must change, describe the before/after in the response and the commit message.

---

## Git workflow

- Repo is on `git` (tracking `origin/main`) **and** carries `.plastic/` metadata (Unity/Plastic VCS). Only `git` is used from here; leave `.plastic/*` changes alone.
- **Git LFS** tracks `*.png *.jpg *.psd *.fbx *.wav *.mp4 *.unitypackage`. Ensure `git lfs` is installed before committing binary assets.
- `.gitattributes` sets `*.cs`/text to `eol=lf` and Unity YAML assets to `merge=unityyamlmerge`. Do not commit CRLF in `.cs`. Do not hand-merge `.unity`/`.prefab`/`.asset` conflicts without the Unity smart-merge tool.
- **Commit `.meta` files** alongside the assets they describe (new scripts, new folders).
- **Do not commit or push unless the user asks.** When they do:
  - Branch off `main` first (don't commit directly to `main`).
  - Keep commits scoped; don't sweep in the pre-existing unrelated working-tree changes (scene, NavMesh, `MoveToTarget`, `PedestrianAgent`, `SpawnerManager`, XR settings, `AudioManager`, `.plastic/*`) unless they're part of the task.
  - End commit messages with:
    `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`
- Untracked new work in progress: `Assets/Scripts/GazeRecorder.cs`, `Assets/Scripts/RiverTeleportPoint.cs`, `Assets/Oculus/`, `Assets/Resources/` (generated Meta XR settings), `Assets/StreamingAssets.meta`.

---

## Testing requirements

There is **no automated test suite** and no Unity Test Framework assemblies in this project. Verification is manual:

1. **Compile check first.** After any script change, confirm the project compiles with no errors/warnings in Unity (or `Library/` scripting logs). C# changes here are only "done" once Unity has reimported cleanly.
2. **Editor play-mode smoke test (no headset):**
   - Enter play mode in `LyonVRVity35.unity`.
   - Selectables: click target buildings and `RiverTeleportPoint`s with the mouse (`OnMouseDown` path) — verify selection registers / player teleports / cooldown works.
   - Run a Part via `ExternalSessionController` (`autoStart` or `StartSelectedPart()`); confirm panels appear, `OnTrialStart`/`OnTrialEnd` fire once, NPCs spawn.
3. **Recorder verification:** after a trial, check `Application.persistentDataPath` for the expected `{subject}_{trial}_..._{timestamp}.csv`; confirm header matches row width, timestamps are monotonic, and (for gaze) `*_valid` columns behave (all-`0` in Editor without a Quest Pro is expected and correct).
4. **NavMesh check** after touching movement/agents: pedestrians path without stalling/warping; no "failed to place agent on NavMesh" errors.
5. **On-device test (Quest / Quest Pro)** is required before any study use for: XR rig tracking, locomotion/Omnideck, teleport comfort, eye-tracking permission prompt + non-zero `*_valid` gaze samples, and panel readability. State clearly in your response when a change is Editor-verified only and still needs a device pass.
6. If you cannot run Unity, say so — describe exactly what manual steps the user must run to verify, and do not claim the change is tested.
