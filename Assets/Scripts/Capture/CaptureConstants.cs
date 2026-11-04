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
        "part,phase," +
        // Appended (schema v13): the RENDERED camera (what the participant sees from; the hmd_* columns above are the OVRCameraRig CenterEyeAnchor, which lacks the XR Origin's Camera Floor Offset) and its height
        // above the walkable ground below it (blank when no ground is found - never an assumed eye height).
        "view_pos_x,view_pos_y,view_pos_z,view_height_above_ground";

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
    // Blink events (only ever written when the face-tracking eye-closure signal is available): value_num = blink duration (seconds) on blink_end, blank on blink_start.
    public const string EvtBlinkStart           = "blink_start";
    public const string EvtBlinkEnd             = "blink_end";
    // Part 3 think-aloud audio lifecycle (schema v15). One row per transition - never per audio buffer (the per-chunk timing lives in audio/audio_index.csv).
    public const string EvtAudioPermissionChecked = "audio_permission_checked", EvtAudioPermissionGranted = "audio_permission_granted", EvtAudioRecordingRequested = "audio_recording_requested",
                        EvtAudioRecordingStarted = "audio_recording_started", EvtAudioRecordingInterrupted = "audio_recording_interrupted", EvtAudioRecordingResumed = "audio_recording_resumed",
                        EvtAudioBufferOverrun = "audio_buffer_overrun", EvtAudioStopRequested = "audio_recording_stop_requested", EvtAudioStopped = "audio_recording_stopped",
                        EvtAudioFileFinalized = "audio_file_finalized", EvtAudioFailed = "audio_recording_failed";
    public const string EvtNpcWarmupStart       = "npc_warmup_start";
    public const string EvtNpcWarmupEnd         = "npc_warmup_end";

    // npc_events.csv event_type values
    public const string NpcEvtSpawn      = "npc_spawn";
    public const string NpcEvtDespawn    = "npc_despawn";
    public const string NpcEvtDwellStart = "npc_dwell_start";
    public const string NpcEvtDwellEnd   = "npc_dwell_end";
    public const string NpcEvtArrived    = "npc_arrived";
    public const string NpcEvtDestChange = "npc_dest_change";
    // value_str of npc_dest_change: "cause=<cause>;..." - cause is initial_assignment (the lifetime's first valid destination; prev_dest=none, no jump_m), detour, itinerary_target, detour_return (v12) or other
    // (a genuine change whose reason is not identified). Genuine changes also carry jump_m and prev_dest=<x>|<z>.
    public const string DestCauseInitialAssignment = "initial_assignment";
    public const string DestCauseOther = "other";
    public const string DestCauseDetourReturn = "detour_return";   // schema v12: a verified return to the pre-detour destination (see DestinationCause.cs for the exact evidence rule)
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
    // 8: Group Number == Group Index everywhere (zero-based: Part 2 0..1, Part 3 0..11); part3_group_number / events.csv 'group=' are zero-based, part3_group_number = -1 when
    //    not applicable; manifest adds the frozen effective settings with provenance (setting_entries) and session_config.used.json. Earlier schemas are not interpreted.
    // 9: eye-tracking acquisition. gaze.csv appends eye_tracking_state, left_provider_valid, right_provider_valid; the manifest adds the eye_tracking block (lifecycle state,
    //    permission / capability / runtime path, startup counters, provider-level valid-sample counters, bounded transition log with the states at each transition),
    //    gaze_writer_status vs gaze_acquisition_status (+ reason), frame_timing and per-stream limit diagnosis (rate_gaze / rate_trajectory gain gaps_frame_limited,
    //    limit_diagnosis, frame_mean_hz). Existing fields keep their names, types and meaning.
    // 10: gaze origin + NPC preparation. gaze.csv appends gaze_origin_source (provider | eye_anchor | provider_unchecked | mixed; blank for the head-fallback ray); the manifest eye_tracking
    //    block adds origin_* validation counters / deviation summary. The manifest adds the npc_preparation block (time-based preparation: pool preparation vs NPC generation period vs
    //    Start-gated pause / resume - configured vs actual durations, counts, timestamps, state trail) and npc_random_sources. events.csv gains the workflow events npc_pool_preparation_started / _completed / _failed, npc_behaviour_paused / npc_behaviour_resumed.
    //    No existing column or field changes name, order or meaning.
    // 11: data-capture reliability. gaze.csv: left/right/combined_confidence are now the RAW provider value or BLANK when unavailable (previously "0.000" for a missing measurement) and combined is defined
    //    only from contributing valid eyes that have one (semantic change); appends eyes_closed_left, eyes_closed_right, blink_state (blank unless a face-tracking eye-closure signal is available).
    //    Manifest: eye_tracking gains the canonical capability / permission / configured / runtime_available / tracking_active / last_state_change_t_abs / provider / sdk_version plus confidence and blink
    //    blocks; the top-level eye_tracking_* fields are deprecated ALIASES derived from it; capture_lifecycle_state + late_* counters; rate_gaze / rate_trajectory use the evidence-based diagnosis
    //    (limit_diagnosis values change to healthy | frame_limited | scheduler_limited | io_limited | mixed | unknown, gaps_* attribution counters, writer_* cost) - the old value
    //    scheduling_or_io_limited is retired. events.csv: session_end is the final row (events after it are refused), blink_start / blink_end only with an available signal. npc_events.csv: the first
    //    destination of a lifetime is npc_dest_change cause=initial_assignment (prev_dest=none, no jump_m); genuine changes keep their cause and gain prev_dest. Older sessions are not reinterpreted.
    // 12: follow-up to v11. npc_events.csv: npc_dest_change gains the cause value detour_return - an identity-verified return to the main destination followed BEFORE a completed UncertainBehavior
    //    detour, for every movement mode (Part 2 wandering points and Part 3 itinerary / attractor targets alike; such rows were cause=other in v11 Part 3 and cause=itinerary_target in v11 Part 2) -
    //    value_str of that row also carries detour_end=arrived|timer|retries_exhausted. Manifest: capture_lifecycle_state is now persisted as Closed with a
    //    real capture_closed_t_abs once every stream closed cleanly (v11 files said Closing / -1); capture_streams_closed / capture_streams_failed / capture_finalization_error added. gaze.csv columns
    //    unchanged. Older sessions are not reinterpreted.
    // 13: view frame. The scene audit found the +1.36144 m "provider origin offset" is the XR Origin's Camera Floor Offset (XROrigin.CameraYOffset, a scene constant): the rendered camera
    //    (Camera.main = XRI Main Camera) sits that far above the OVRCameraRig anchors that gaze.csv head_*, trajectory hmd_* and the NPC line of sight used. gaze.csv appends 23 view-frame columns (hmd_world_*,
    //    eye head-local / world positions, ground_y, heights above ground); trajectory.csv appends view_pos_* and view_height_above_ground; gaze_origin_source gains eye_anchor_view (the anchor re-applied at
    //    the rendered view); the head-fallback ray and npc_state los_* now start from / aim at the rendered view; manifest eye_tracking gains view_frame and origin_vs_rig_anchor_*.
    //    Existing columns keep their names, positions and meaning (head_* and hmd_* remain the OVR anchor tree). v10-v12 recordings are NOT reinterpreted: their eye_anchor origins, head-fallback rays and los_*
    //    sit about one Camera Floor Offset below the viewpoint.
    // 14: per-eye provider pipeline (audit of the Quest Pro eye-tracking architecture). gaze.csv appends 37 columns: provider_time_s / provider_time_changed, the raw OVRPlugin eye poses in tracking space,
    //    the same poses in the head frame (SDK head-space conversion), provider-vs-view-anchor and WorldSpace-vs-composed coherence measures, view-anchor / provider / runtime-IPD separations,
    //    anchors_updated_frame and origin_fallback_reason. Manifest eye_tracking gains eye_pipeline. The recorded gaze ray (left_/right_/combined_ origin and dir) is unchanged: still the OVREyeGaze WorldSpace
    //    result, with the v13 view-frame anchor fallback. v13 columns left_eye_world_* / right_eye_world_* are VIEW ANCHORS (stereo view poses re-applied at the rendered camera), not gaze origins: documented, not renamed.
    // 15: Part 3 think-aloud audio. New directory audio/ (part3_audio.wav PCM16, audio_index.csv, audio_anchors.csv, audio_metadata.json), new events audio_* (events.csv), new manifest block audio_capture and
    //    file_list entries. No existing column, field or file changes. Sessions of other Parts / older schemas simply have no audio/ directory and audio_capture.status = not_enabled / not_started.
    // 16: face-tracking eyelid signal (diagnosis of empty eyes_closed_*). gaze.csv appends 10 columns after origin_fallback_reason: face_state, face_tracking_started, face_expressions_valid, eye_following_valid,
    //    eyes_look_down_left/right, eyes_closed_left/right_recovered, face_sample_frame, face_data_source. eyes_closed_left/right/blink_state keep their meaning (raw SDK weights / detector state); blink detection now
    //    uses the recovered closure and the signal is read live per row. GazeRecorder creates OVRFaceExpressions at run time when missing (autoCreateFaceExpressions). Manifest eye_tracking.blink gains the
    //    fields listed in BlinkManifest (schema v16). No existing column, field or file changes.
    // 17: gaze.csv is reduced to a 61-column analysis table and a NEW stream gaze_diagnostics.csv (85 columns, one row per gaze row, keys session_id + frame_index + t_abs) carries the QA / reproduction columns.
    //    Removed from gaze.csv (moved to gaze_diagnostics.csv unless noted): unix_time_ms, left/right/combined_confidence, left/right_valid, left/right_provider_valid, head_pos_*, head_rot_*, left/right_eye_head_local_*,
    //    left/right_eye_world_*, all v14 provider-pipeline columns, origin_fallback_reason, face_state, face_tracking_started, face_data_source, face_expressions_valid, eye_following_valid, eyes_look_down_*,
    //    raw eyes_closed_left/right, face_sample_frame, blink_state; removed outright (derivable, see docs/schema_v17_changes.md): ray_origin_*, ray_dir_*, ray_is_eye_gaze, roi_hit_name.
    //    New in gaze.csv: face_closure_valid (and the explicit eyes_closed_left/right_recovered kept from v16). New in gaze_diagnostics.csv: face_provider_time_s, fallback_ray_origin_* / fallback_ray_dir_* (the cast ray of head-fallback rows). No column keeps a name with a different meaning.
    //    Audio: audio_index.csv is written once at finalisation from ONE frozen clock map (t_abs columns identical in meaning, no longer provisional); audio_anchors.csv holds every anchor including the first.
    //    Manifest: eye_tracking.blink gains startup_* and the blink-detection honesty fields; audio_capture gains time_mapping*, index_* classification fields. Readers of v11-v16 files are unchanged.
    public const int    SchemaVersion   = 17;
    public const string GazeDiagnosticsFile = "gaze_diagnostics.csv";
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
