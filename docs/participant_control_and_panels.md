# Participant control and the Part 3 panels (what is locked when)

Reconstructed from the code and from session `20261105T170113Z_67e1fc797de5` (see the audit `panel_lock_comparison.md` for the evidence). Two independent mechanisms:

| Mechanism | Gates | State | Code |
|---|---|---|---|
| **Participant-control gate** | continuous move, turn (continuous / snap), climb, grab (every XRI `LocomotionProvider` except teleportation, via `ParticipantControlLock`); every generic XRI teleport (`GuardedTeleportationProvider`, reason `participant_locked`); river-point teleports; building selection | `ExperimentManager.ParticipantControlEnabled`, written only by `SetParticipantControl` | `ExperimentManager.cs`, `ParticipantControlLock.cs`, `GuardedTeleportationProvider.cs`, `RiverTeleportPoint.cs`, `TargetSelectable.cs` |
| **Open-panel teleport guard** | generic XRI teleports only (`panel_open`) | `UIManager.HasOpenFullPanel` | `UIManager.cs`, `GuardedTeleportationProvider.cs` |

Neither touches NPC spawning / movement (`NpcSimulationGate`), audio, gaze / trajectory capture, head tracking, controller rays, UI input or `Time.timeScale`.

## Sequence in Part 3

| Stage | Gate | XRI teleport | River teleport | Released by |
|---|---|---|---|---|
| Part start .. Start panel | locked | `participant_locked` | blocked | `BeginTrial()` after the Start click **and** the audio start gate (first microphone sample; ~1.2 s in the audited session) |
| Destination 1 panel | enabled | `panel_open` | allowed | (never locked) XRI teleports are released when the panel is destroyed by the Continue click |
| D1 search | enabled | policy | allowed | - |
| **Destination 2 panel** | **locked** (`lockParticipantWhileDestination2Panel`, default true) | `participant_locked` | blocked | `ReleaseParticipantCheckpointLock` in the D2 Continue callback (idempotent safety release after the wait) |
| D2 search | enabled | policy | allowed | - |
| End panel / session end | locked | blocked | blocked | terminal |

So movement is released by the **Start** click (through `BeginTrial`), **not** by the Destination 1 Continue click. The Destination 2 lock was added because that panel is a mandatory checkpoint, appears at an arbitrary place / time (end of the D1 search), is positioned once in world space and was left behind by a participant who kept walking (303 s without a click). The lock takes `ParticipantControlEnabled` false only if it is currently true and neither the End panel nor the session end owns it; the release never re-opens a terminal state. Events: `participant_control` (label `D2`, value `locked;reason=d2_panel_shown` / `unlocked;reason=d2_continue_confirmed`).

NPC behaviour while the Start panel is read: with `npcWarmupDuration > 0` and `holdNpcsUntilStart` the NPCs are paused after the generation period and resumed by `BeginTrial`; with `npcWarmupDuration = 0` (as in the audited session) no pause exists and NPCs keep walking (`docs/npc_preparation.md`).
