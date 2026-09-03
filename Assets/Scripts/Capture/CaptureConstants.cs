// CaptureConstants.cs
// CSV header strings, event-type identifiers and small enum->string maps for the data-capture layer
// (Architecture C, Revision 2). gaze.csv keeps its own header inside GazeRecorder (existing columns
// must not be reordered), so it is not defined here.
// All comments in English per project convention.

public static class CaptureConstants
{
    // ---------------- CSV headers (streams owned by DataCaptureManager) ----------------

    public const string EventsHeader =
        "t_abs,t_unix_ms,event_type,part,phase,label," +
        "building_id,target_id,npc_id,npc_spawn_instance_id," +
        "from_pos_x,from_pos_y,from_pos_z,to_pos_x,to_pos_y,to_pos_z," +
        "value_num,value_str";

    public const string TrajectoryHeader =
        "t_abs,t_unix_ms,frame_index," +
        "rig_pos_x,rig_pos_y,rig_pos_z,teleport_epoch," +
        "rig_rot_y,rig_forward_x,rig_forward_z," +
        "hmd_pos_x,hmd_pos_y,hmd_pos_z," +
        "hmd_rot_x,hmd_rot_y,hmd_rot_z,hmd_rot_w," +
        "hmd_local_pos_x,hmd_local_pos_y,hmd_local_pos_z," +
        "ctrl_l_pos_x,ctrl_l_pos_y,ctrl_l_pos_z,ctrl_l_rot_x,ctrl_l_rot_y,ctrl_l_rot_z,ctrl_l_rot_w,ctrl_l_tracked," +
        "ctrl_r_pos_x,ctrl_r_pos_y,ctrl_r_pos_z,ctrl_r_rot_x,ctrl_r_rot_y,ctrl_r_rot_z,ctrl_r_rot_w,ctrl_r_tracked";

    public const string NpcStateHeader =
        "t_abs,t_unix_ms,frame_index," +
        "npc_id,npc_spawn_instance_id," +
        "pos_x,pos_y,pos_z,yaw," +
        "vel_x,vel_y,vel_z," +
        "nav_dest_x,nav_dest_y,nav_dest_z," +
        "state,speed,agent_desired_vel_x,agent_desired_vel_z," +
        "mt_target_x,mt_target_y,mt_target_z," +
        "nav_has_path,nav_path_pending,los_clear_to_participant";

    public const string NpcEventsHeader =
        "t_abs,t_unix_ms,npc_id,npc_spawn_instance_id,event_type," +
        "pos_x,pos_y,pos_z,yaw,dest_x,dest_y,dest_z," +
        "speed_setting,path_corners,value_str";

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

    // npc_events.csv event_type values
    public const string NpcEvtSpawn      = "npc_spawn";
    public const string NpcEvtDespawn    = "npc_despawn";
    public const string NpcEvtDwellStart = "npc_dwell_start";
    public const string NpcEvtDwellEnd   = "npc_dwell_end";
    public const string NpcEvtArrived    = "npc_arrived";
    public const string NpcEvtDestChange = "npc_dest_change";
    public const string NpcEvtLegStart   = "npc_leg_start";

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