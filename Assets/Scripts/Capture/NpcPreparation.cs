// NpcPreparation.cs
// Explicit NPC preparation lifecycle of a Part and the evidence the manifest records about it. The design is TIME-BASED, not population-based. The control variable is elapsed NPC generation time.
// Sequence of one Part (no step waits for the participant, the headset, gaze, controllers or the Start panel):
//   1. POOL PREPARATION     - starts automatically with the Part (the spawner creates the pool at scene start). SpawnerManager creates poolPrewarm INACTIVE, invisible pooled objects (not NPCs). There
//                             is NO time limit: the stage ends when the pool is complete, when creation reports a failure, or when the Part is cancelled. A pool that is stuck is not "failed" - it stays
//                             PoolPreparing and the live creation progress is in the manifest and the log. A pool with created != poolPrewarm, a missing spawner or a creation error is Failed: no
//                             generation, no Start panel, no trial.
//   2. NPC GENERATION       - starts the moment the pool is verified complete and lasts ExperimentManager.npcWarmupDuration seconds of EXECUTED time: the sum of per-frame unscaled deltas, each capped at
//                             Time.maximumDeltaTime - the same cap the NPC simulation itself obeys. The timer therefore advances exactly as far as the simulation did: an unworn headset or lost focus while
//                             Unity keeps executing does not slow it, and an interval in which Unity was not executing (genuine OS suspension) adds at most one capped step, never wall-clock time.
//                             NPCs spawn at SpawnerManager.spawnInterval and behave normally; the duration - not a number of NPCs - is the stopping condition.
//   3. START-GATED PAUSE    - when the duration is over, spawning stops and every active NPC is held (visible, raycastable, still sampled) automatically, worn headset or not. The Start panel is
//                             placed in front of the participant's head only once the headset is worn (UI placement is a one-shot, see ExperimentManager.WaitForHeadsetBeforeStartPanel); the Start
//                             click resumes the NPCs in the same transition that opens the trial (ExperimentManager.BeginTrial).
// States: NotStarted -> PoolPreparing -> PoolReady -> NpcGenerationRunning -> NpcGenerationCompleted -> PausedAwaitingParticipantStart -> Running -> Ended
//         (Failed: pool creation error / incomplete pool / no spawner; Cancelled: the Part / session ended before the participant started).
// A fixed generation duration standardises the PREPARATION PERIOD; it does not make NPC trajectories identical (see npc_random_sources in the manifest).
// All comments in English per project convention.

using System;

public enum NpcPreparationState
{
    NotStarted, PoolPreparing, PoolReady, NpcGenerationRunning, NpcGenerationCompleted, PausedAwaitingParticipantStart, Running, Ended, Failed, Cancelled
}

/// <summary>Runtime status + manifest block ("npc_preparation"). Public fields are serialised as-is; times are CaptureClock t_abs seconds (-1 = did not happen).</summary>
[Serializable]
public class NpcPreparationInfo
{
    public string part = "";
    public string state = "NotStarted";
    public string state_trail = "";              // bounded history of the states entered (a state can last a single frame)
    public bool applicable;                      // the Part has NPCs enabled
    public bool hold_until_start;                // frozen effective setting at Part start (ExperimentManager.holdNpcsUntilStart)

    // ---- pool preparation (inactive pooled objects - NOT the NPC population) ----
    public int pool_preparation_target_count;
    public int pool_preparation_created_count;
    public double pool_preparation_started_t_abs = -1, pool_preparation_completed_t_abs = -1, pool_preparation_duration_seconds = -1;
    public double pool_preparation_last_progress_t_abs = -1;   // when the most recent pooled object was created (diagnostic for a stuck pool; never a deadline)
    public string pool_preparation_status = "not_started";   // not_started | preparing | ready | failed_error | failed_incomplete | failed_no_spawner | cancelled
    public string pool_preparation_error = "";               // the creation error, when failed_error

    // ---- NPC generation (elapsed behaviour time after pool readiness; the configured duration is the only stopping condition) ----
    public float npc_generation_configured_duration_seconds;   // frozen effective npc_warmup_duration_seconds at Part start
    public double npc_generation_started_t_abs = -1, npc_generation_completed_t_abs = -1;
    public double npc_generation_actual_duration_seconds = -1;  // EXECUTED generation time (sum of capped per-frame deltas): what the NPC simulation really advanced
    public double npc_generation_wall_clock_seconds = -1;       // completed - started on the capture clock
    public double npc_generation_non_executing_seconds;         // wall - actual: intervals in which Unity was not executing (suspension / stalls beyond the step cap)
    public double npc_generation_max_frame_gap_seconds;         // longest gap between two frames inside the window
    public int npc_generation_unreported_stalls;                // gaps beyond the step cap with NO application-pause callback (a suspension the callbacks did not report)
    public int npc_generation_app_pause_count;                  // OnApplicationPause(true) callbacks inside the window (diagnostic: they do not change the timer)
    public double npc_generation_app_pause_seconds;             // time between those callbacks and the matching resume
    public double npc_generation_focus_lost_seconds;            // OnApplicationFocus(false) time inside the window (Unity kept executing unless it is also a pause)
    public int npc_generation_hmd_unmount_count;                // OVRManager.HMDUnmounted events inside the window
    public double npc_generation_hmd_unmounted_seconds;         // headset-unworn time inside the window (generation continues while unworn and executing)
    public int npc_generation_spawn_count = -1;                 // NPCs spawned during the generation window
    public int active_npcs_at_generation_end = -1;              // visible population when the duration ended (recorded, never a condition)

    // ---- pause / resume ----
    public double npc_behaviour_paused_t_abs = -1, participant_start_t_abs = -1, npc_behaviour_resumed_t_abs = -1;
    public int active_npcs_at_pause = -1;        // NPCs actually held
    public int active_npcs_at_resume = -1;       // NPCs released
    public string pause_reason = "", resume_reason = "";
    public int pause_count, resume_count, early_start_attempts;
    public double start_panel_headset_wait_seconds;   // time the Start panel was withheld until the headset was worn (does not delay pool preparation or generation)

    public void ResetForPart(string partLabel)
    {
        part = partLabel ?? ""; state = "NotStarted"; state_trail = "NotStarted"; applicable = false; hold_until_start = false;
        pool_preparation_target_count = 0; pool_preparation_created_count = 0;
        pool_preparation_started_t_abs = pool_preparation_completed_t_abs = pool_preparation_duration_seconds = -1; pool_preparation_last_progress_t_abs = -1; pool_preparation_status = "not_started"; pool_preparation_error = "";
        npc_generation_configured_duration_seconds = 0; npc_generation_started_t_abs = npc_generation_completed_t_abs = -1; npc_generation_actual_duration_seconds = -1;
        npc_generation_wall_clock_seconds = -1; npc_generation_non_executing_seconds = 0; npc_generation_max_frame_gap_seconds = 0; npc_generation_unreported_stalls = 0;
        npc_generation_app_pause_count = 0; npc_generation_app_pause_seconds = 0; npc_generation_focus_lost_seconds = 0; npc_generation_hmd_unmount_count = 0; npc_generation_hmd_unmounted_seconds = 0;
        npc_generation_spawn_count = -1; active_npcs_at_generation_end = -1;
        npc_behaviour_paused_t_abs = participant_start_t_abs = npc_behaviour_resumed_t_abs = -1;
        active_npcs_at_pause = active_npcs_at_resume = -1; pause_reason = resume_reason = ""; pause_count = resume_count = early_start_attempts = 0; start_panel_headset_wait_seconds = 0;
    }
}
