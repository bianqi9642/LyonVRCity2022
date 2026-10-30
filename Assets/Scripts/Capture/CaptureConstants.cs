// CaptureConstants.cs
// CSV header strings, event-type identifiers and small enum->string maps for the data-capture layer
// (Architecture C, Revision 2). gaze.csv keeps its own header inside GazeRecorder (existing columns
// must not be reordered), so it is not defined here.
// All comments in English per project convention.

using System.Collections.Generic;

public static class CaptureConstants
{
    // ---------------- CSV headers (streams owned by DataCaptureManager) ----------------

    public const string EventsHeader =
        "t_abs,t_unix_ms,event_type,part,phase,label," +
        "building_id,target_id,npc_id,npc_spawn_instance_id," +
        "from_pos_x,from_pos_y,from_pos_z,to_pos_x,to_pos_y,to_pos_z," +
        "value_num,value_str," +
        // Appended (schema v2): same session key as every other stream.
        "session_id," +
        // Appended (schema v6): click <-> outcome link (see docs: events.csv interaction_id).
        "interaction_id";

    public const string TrajectoryHeader =
        "t_abs,t_unix_ms,frame_index," +
        "rig_pos_x,rig_pos_y,rig_pos_z,teleport_epoch," +
        "rig_rot_y,rig_forward_x,rig_forward_z," +
        "hmd_pos_x,hmd_pos_y,hmd_pos_z," +
        "hmd_rot_x,hmd_rot_y,hmd_rot_z,hmd_rot_w," +
        "hmd_local_pos_x,hmd_local_pos_y,hmd_local_pos_z," +
        "ctrl_l_pos_x,ctrl_l_pos_y,ctrl_l_pos_z,ctrl_l_rot_x,ctrl_l_rot_y,ctrl_l_rot_z,ctrl_l_rot_w,ctrl_l_tracked," +
        "ctrl_r_pos_x,ctrl_r_pos_y,ctrl_r_pos_z,ctrl_r_rot_x,ctrl_r_rot_y,ctrl_r_rot_z,ctrl_r_rot_w,ctrl_r_tracked," +
        // Appended (schema v5): Part and experimental phase AT THE SAMPLE TIME (see docs: phase labels).
        "part,phase";

    public const string NpcStateHeader =
        "t_abs,t_unix_ms,frame_index," +
        "npc_id,npc_spawn_instance_id," +
        "pos_x,pos_y,pos_z,yaw," +
        "vel_x,vel_y,vel_z," +
        "nav_dest_x,nav_dest_y,nav_dest_z," +
        "state,speed,agent_desired_vel_x,agent_desired_vel_z," +
        "mt_target_x,mt_target_y,mt_target_z," +
        "nav_has_path,nav_path_pending,los_clear_to_participant," +
        // Appended (schema v2). npc_spawn_instance_id is now the composite lifetime ID "<AgentID>-S<n>" (it was a bare "S<n>").
        "session_id,part,phase,capture_period,sample_seq,lifetime_index," +
        "rot_x,rot_y,rot_z,rot_w," +
        "nav_agent_speed,itinerary_index,itinerary_count,target_id,nav_on_mesh," +
        // Appended (schema v3): UncertainBehavior. uncertain_enabled = configured ON for this lifetime; uncertain_state / uncertain_look_phase = runtime.
        "uncertain_enabled,uncertain_state,uncertain_look_phase," +
        // Appended (schema v7): why los_clear_to_participant is 1 / 0 / blank (clear | blocked | missing_participant_target | missing_npc_origin | invalid_geometry | hit_buffer_overflow | query_error).
        "los_status";

    public const string NpcEventsHeader =
        "t_abs,t_unix_ms,npc_id,npc_spawn_instance_id,event_type," +
        "pos_x,pos_y,pos_z,yaw,dest_x,dest_y,dest_z," +
        "speed_setting,path_corners,value_str," +
        // Appended (schema v2). For prewarm / pool events npc_spawn_instance_id and lifetime_index are blank (no active lifetime).
        "session_id,part,phase,lifetime_index,itinerary_index,target_id";

    // One row per ACTIVE NPC LIFETIME, written at its spawn boundary. Termination is recorded in npc_events.csv (npc_despawn).
    public const string NpcLifetimesHeader =
        "session_id,npc_spawn_instance_id,npc_id,lifetime_index,part,phase,capture_period_at_spawn," +
        "prefab_name,object_name,object_created_t_abs,object_created_kind,pool_acquired_t_abs," +
        "spawn_t_abs,spawn_t_unix_ms,registered_t_abs,registration,spawn_source," +
        "spawn_x,spawn_y,spawn_z," +
        "movement_mode,itinerary_count,entrance_id,first_target_id,final_target_id,itinerary_ids," +
        "configured_speed,profile_name,profile_base_speed,speed_multiplier,uncertain_behavior_enabled,gender,dwell_time_s," +
        "wander_look_ahead_m,nav_radius,nav_height,nav_acceleration,nav_angular_speed,nav_avoidance_priority," +
        // Appended (schema v3): the profile content in force and the REALISED appearance (see NpcAppliedProfile.cs).
        "profile_content_hash,profile_female_probability,profile_force_uncertain,profile_uncertain_probability,uncertain_config," +
        "appearance_hash,appearance_applied_t_abs," +
        "app_body_option,app_body_mesh,app_body_materials,app_hair_option,app_hair_mesh,app_hair_materials," +
        "app_top_option,app_top_mesh,app_top_materials,app_bottom_option,app_bottom_mesh,app_bottom_materials," +
        "app_shoes_option,app_shoes_mesh,app_shoes_materials,app_accessories_option,app_accessories_mesh,app_accessories_materials";

    // Diagnostic channel (NOT a participant or NPC event stream): one row per recording failure / recovery transition of any stream or of the manifest.
    public const string CaptureErrorsHeader =
        "session_id,t_abs,t_utc,stream_name,operation,error_type,error_message,rows_written_before_failure,rows_dropped,status,recovery_attempt";

    // One row per distinct AgentProfile CONTENT (profile_content_hash) seen in a session: the full description needed to reconstruct what a hash means,
    // so a profile asset edited later cannot make old lifetimes ambiguous.
    public const string NpcProfilesHeader =
        "session_id,profile_content_hash,profile_name,first_seen_t_abs,profile_json";

    public const string BuildingRegistryHeader =
        "building_id,gameobject_name,gameobject_instance_id,hierarchy_path," +
        "pos_x,pos_y,pos_z," +
        "bounds_center_x,bounds_center_y,bounds_center_z," +
        "bounds_size_x,bounds_size_y,bounds_size_z," +
        "layer,tag,has_collider,collider_type,is_target_selectable,target_id," +
        "experiment_role,part3_zone,part3_candidate_slot,part1_target_ordinal";

    // ---------------- events.csv event_type values ----------------

    public const string EvtSessionStart         = "session_start";
    public const string EvtSessionEnd           = "session_end";
    public const string EvtAppPause             = "app_pause";
    public const string EvtAppFocus             = "app_focus";
    public const string EvtPartStart            = "part_start";
    public const string EvtPartEnd              = "part_end";
    public const string EvtTrialStart           = "trial_start";
    public const string EvtTrialEnd             = "trial_end";
    public const string EvtPanelShown           = "panel_shown";
    public const string EvtPanelConfirmed       = "panel_confirmed";
    public const string EvtPhaseStart           = "phase_start";
    public const string EvtDestinationShown     = "destination_shown";
    public const string EvtDestinationConfirmed = "destination_confirmed";
    public const string EvtTargetAssigned       = "target_assigned";
    public const string EvtCandidateInteractOn  = "candidate_interactable_on";
    public const string EvtCandidateInteractOff = "candidate_interactable_off";
    public const string EvtBuildingClick        = "building_click";
    public const string EvtCorrectSelection     = "correct_selection";
    public const string EvtIncorrectSelection   = "incorrect_selection";
    public const string EvtPhaseResult          = "phase_result";
    public const string EvtPhaseTimeout         = "phase_timeout";
    public const string EvtPhaseEnd             = "phase_end";
    public const string EvtTeleport             = "teleport";
    public const string EvtRiverCrossing        = "river_crossing";
    public const string EvtRecordingMarker      = "recording_marker";
    public const string EvtNpcSpawn             = "npc_spawn";
    public const string EvtNpcDespawn           = "npc_despawn";

    // NPC warm-up window markers: EXPERIMENT WORKFLOW events (raised by ExperimentManager.RunNpcWarmup while the participant waits before the trial),
    // written to events.csv exactly like part_start/part_end, independent of npcCaptureEnabled. They carry no NPC identity and describe no NPC;
    // they only delimit the pre-trial window (the NPC files label the same window with capture_period = warmup). They never gate participant capture.
    public const string EvtNpcWarmupStart       = "npc_warmup_start";
    public const string EvtNpcWarmupEnd         = "npc_warmup_end";

    // npc_events.csv event_type values
    public const string NpcEvtSpawn      = "npc_spawn";
    public const string NpcEvtDespawn    = "npc_despawn";
    public const string NpcEvtDwellStart = "npc_dwell_start";
    public const string NpcEvtDwellEnd   = "npc_dwell_end";
    public const string NpcEvtArrived    = "npc_arrived";
    public const string NpcEvtDestChange = "npc_dest_change";
    public const string NpcEvtLegStart   = "npc_leg_start";   // RETIRED in schema v3: identical to npc_dest_change by construction; no longer emitted

    // Added with the lifetime identity work (schema v2). Every one has a real code trigger.
    public const string NpcEvtPrewarmCreated     = "prewarm_created";       // pool object instantiated at prewarm; INACTIVE, not a live NPC
    public const string NpcEvtPoolObjectCreated  = "pool_object_created";   // pool object instantiated on demand because the pool was empty
    public const string NpcEvtPoolAcquired       = "pool_acquired";         // object taken from the pool for a new lifetime
    public const string NpcEvtMovementStart      = "npc_movement_start";    // first leg handed to MoveToTarget
    public const string NpcEvtEntranceTeleport   = "npc_entrance_teleport"; // placement at the itinerary entrance (pos = before, dest = after)
    public const string NpcEvtTargetSet          = "npc_target_set";        // itinerary leg issued (itinerary-level target change)
    public const string NpcEvtItineraryComplete  = "npc_itinerary_complete";// itinerary finished / force-finished (lifetime ends)
    public const string NpcEvtPoolReturn         = "npc_pool_return";       // inactive object is back in the pool
    public const string NpcEvtUncertainState    = "npc_uncertain_state";   // UncertainBehavior label changed (value_str from=..;to=..;planned_s=..)
    public const string NpcEvtOpenAtEnd          = "npc_lifetime_open_at_end"; // lifetime still open when the NPC simulation was frozen (End panel)
    public const string NpcEvtSuperseded         = "npc_lifetime_superseded";  // diagnostic: a lifetime was replaced without a despawn

    // ---------------- event routing: which stream owns an event ----------------

    public enum EventStream { ParticipantExperiment, Npc }

    // EXPLICIT list of every event whose SUBJECT is an NPC (creation, pooling, lifecycle, movement, behaviour). Membership is deliberate, not a name
    // prefix rule: a new NPC event type must be added here (the test NpcRouting_EveryNpcEventConstantIsClassified fails for any Npc* / NpcEvt*
    // constant that is missing). Everything raised through ExperimentManager.OnExperimentEvent (session / Part / trial / phase / panel / selection /
    // teleport / warm-up window ...) is ParticipantExperiment.
    static readonly HashSet<string> s_npcSubjectEvents = new HashSet<string>
    {
        EvtNpcSpawn, EvtNpcDespawn,   // legacy events.csv mirror types (schema <= 3); same strings as NpcEvtSpawn / NpcEvtDespawn
        NpcEvtSpawn, NpcEvtDespawn, NpcEvtDwellStart, NpcEvtDwellEnd, NpcEvtArrived, NpcEvtDestChange, NpcEvtLegStart,
        NpcEvtPrewarmCreated, NpcEvtPoolObjectCreated, NpcEvtPoolAcquired, NpcEvtMovementStart, NpcEvtEntranceTeleport, NpcEvtTargetSet,
        NpcEvtItineraryComplete, NpcEvtPoolReturn, NpcEvtUncertainState, NpcEvtOpenAtEnd, NpcEvtSuperseded
    };

    /// <summary>True when the event describes an NPC itself. Such events are written ONLY to npc_events.csv (and the other NPC streams), never to events.csv.</summary>
    public static bool IsNpcSubjectEvent(string type) => type != null && s_npcSubjectEvents.Contains(type);

    public static EventStream RouteOf(string type) => IsNpcSubjectEvent(type) ? EventStream.Npc : EventStream.ParticipantExperiment;

    public const string EventsCsvScopeText =
        "events.csv holds participant actions and experiment / session workflow events only (session, Part, trial, phase, panel, target selection, " +
        "teleport, application pause/focus, NPC warm-up window markers). NPC lifecycle, movement and behaviour events are written only to npc_events.csv; " +
        "the npc_id / npc_spawn_instance_id columns of events.csv are kept for compatibility and are always blank from schema_version 4";

    // Identity / schema metadata written to session_manifest.json.
    public const int    SchemaVersion   = 7;
    public const string NpcIdScheme     = "agent-lifetime-v1: npc_id=AgentID (stable pooled GameObject); npc_spawn_instance_id=<AgentID>-S<n> (n = per-AgentID lifetime index, from 1)";
    public const string NpcCaptureModeText =
        "master_switch: npc_state.csv holds every active NPC lifetime of a Part whose npcCaptureEnabled is true, from the moment NPCs exist (warm-up, " +
        "Start-panel wait, pre-trial, trial) until the End-panel freeze; there is no trial-state gate";
    public const string NpcMasterSwitchText =
        "PartCaptureConfig.npcCaptureEnabled is the only NPC recording switch. When false for the running Part: no npc_state / npc_events / npc_lifetimes " +
        "rows, no NPC identity (hit_npc_id, hit_npc_spawn_instance_id) in gaze.csv, nothing queued " +
        "or replayed; the NPC simulation, gaze and trajectory recording are unaffected";
    public const string NpcBudgetScopeText =
        "npcStateDataBudgetMBPerMinute (decimal MB/min, 0 = unlimited) limits ONLY the frequency of complete npc_state.csv snapshots, from measured bytes; " +
        "npc_events.csv, npc_lifetimes.csv, events.csv, headers and the manifest are never throttled and may exceed it";
    public const string SessionKeyScheme = "logical cross-file key = (session_id, npc_spawn_instance_id); session_id is the session folder name and gaze.csv session_id";

    // Part labels used by the scope machine
    public const string Part1    = "Part1";
    public const string Part2    = "Part2";
    public const string Part3    = "Part3";
    public const string PartIdle = "Idle";

    public const string Part3Log = "part3_log"; // internal ExperimentEventInfo.type for AppendPart3Log

    /// <summary>Maps PedestrianAgent.State to the raw CSV token.</summary>
    public static string NpcStateToString(PedestrianAgent.State s)
    {
        switch (s)
        {
            case PedestrianAgent.State.Moving:             return "moving";
            case PedestrianAgent.State.WaitingAtAttractor: return "dwelling";
            case PedestrianAgent.State.Finished:           return "finished";
            default:                                       return "unknown";
        }
    }
}
