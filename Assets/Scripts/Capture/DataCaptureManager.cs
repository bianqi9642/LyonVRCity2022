// DataCaptureManager.cs
// Central data-capture / observer layer for the Lyon VR wayfinding experiment (Architecture C, Revision 2).
//
// RESPONSIBILITIES
//   - Own the session output folder and the manifest (3 write passes).
//   - Own events.csv, trajectory.csv, npc_state.csv, npc_events.csv, building_registry.csv.
//   - Hold the single capture-scope state machine (TrajectoryCaptureActive / GazeCaptureActive /
//     NpcCaptureActive), each derived from ExperimentManager.CurrentPartConfig (the single authoritative
//     per-Part configuration - see Requirement 7) plus the shared trial/warm-up/End-Panel lifecycle flags.
//   - Own the teleport epoch counter (single-increment debounce shared by scripted + river teleports).
//   - Own the NPC spawn-instance id map (PedestrianAgent.AgentID is only a pool-slot identity).
//   - Provide read context to GazeRecorder + AoiClassifier.
//
// PURE OBSERVER: never calls into experiment control flow; never moves a transform, NavMeshAgent,
// animator, or timing value. Only subscribes to existing events / reads public state.
//
// SCOPE (per-Part configurable, Revision 2.3 - previously a frozen Part1/Part2/Part3 rule table):
//   Each Part's ExperimentManager.PartCaptureConfig (part1Config/part2Config/part3Config) independently
//   declares npcEnabled / npcCaptureEnabled / gazeCaptureEnabled / trajectoryCaptureEnabled. The shipped
//   defaults exactly reproduce the previous frozen rules: Part1 = everything off; Part2 = NPCs on, NPC
//   capture off, gaze+trajectory on; Part3 = everything on. building_registry.csv generation and the
//   Part1-suppression of misc. participant events (building_click, river_crossing, candidate_interactable_*,
//   teleport rows) are UNCHANGED and still hardcoded to "not Part1" - they are not among the four
//   configurable settings and were left untouched per the minimal-change instruction.
//
// NPC WARM-UP (additive, Revision 2.1; Requirement 3 in Revision 2.3 made it conditional on npcEnabled):
//   ExperimentManager now runs a pre-trial NPC warm-up window (see ExperimentManager.RunNpcWarmup) during
//   which SpawnerManager spawns/moves NPCs completely normally, but the participant has not yet started
//   their trial. NpcCaptureActive requires that no warm-up window is open (_npcWarmupActive, driven by the
//   npc_warmup_start/npc_warmup_end experiment events, which are now raised ONLY for a Part whose
//   PartCaptureConfig.npcEnabled is true) - otherwise warm-up NPC telemetry would silently enter
//   npc_state.csv / npc_events.csv and contaminate the participant's NPC record. Any NPC that was already
//   alive when warm-up ends is picked up automatically by the EXISTING "late-bound" path in SampleNpcs() (an
//   active agent with no prior spawn record gets one assigned the first time it is sampled) - so carried-over
//   NPCs start appearing in npc_state.csv from trial start onward, with a one-time npc_latebind warning, and
//   WITHOUT a synthetic npc_spawn event inside the trial window (which would misrepresent when they actually
//   spawned). CSV schemas (column order/names) are unchanged.
//
// PARTICIPANT CAPTURE SCOPE (additive, Revision 2.2, generalised to gaze+NPC in Revision 2.3):
//   trajectory.csv (and, as of Revision 2.3, gaze.csv and NPC telemetry too) previously recorded for the
//   participant's entire Part2/Part3 lifetime, which begins the instant part_start fires - well before
//   OnTrialStart. That meant capture could include the teleport, the NPC warm-up window, and the Start panel
//   wait, none of which is the participant's actual experimental behaviour. TrajectoryCaptureActive /
//   GazeCaptureActive / NpcCaptureActive now ALL require _trialActive (a Part-agnostic flag set true in
//   OnTrialStart() and false in OnTrialEnd()) AND !_endPanelActive (set true when ExperimentManager raises
//   OnEndPanelShown, immediately before a Part's End Panel is shown - strictly before part_end). So every
//   continuous capture stream now runs ONLY between OnTrialStart and the End-Panel-shown moment, for every
//   Part where its respective *CaptureEnabled flag is on. Part 3's internal D1/D2 sub-phases do not raise
//   their own OnTrialStart/OnTrialEnd or OnEndPanelShown (ExperimentManager fires exactly one of each per
//   Part, including Part 3), so this does not fragment capture across Part 3's sub-phases - it only trims the
//   pre-trial and post-trial/End-Panel edges. GazeRecorder's own per-sample check now reads
//   dataCapture.GazeCaptureActive instead of the old ParticipantCaptureActive (see GazeRecorder.cs). CSV
//   schemas (trajectory.csv / gaze.csv / npc_state.csv / npc_events.csv columns) are unchanged; no new
//   events.csv event type was added for the End-Panel boundary - the existing "panel_shown" row (label
//   "partX_end") already timestamps that exact moment (see Requirement 8 in the audit response).
//
// [DefaultExecutionOrder(-100)] so Start() (and the OnExperimentEvent subscription) runs before
// ExperimentManager.Start() -> StartSelectedPart() -> RunPartX(), so the very first part_start is caught.
// All comments in English per project convention.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[DefaultExecutionOrder(-100)]
public class DataCaptureManager : MonoBehaviour
{
    [Header("Sampling rates (Hz)")]
    [Tooltip("Participant trajectory sampling rate. Frozen at 20 Hz by the approved plan.")]
    public float trajectoryHz = 20f;
    [Tooltip("NPC state sampling rate (Part 3 only). Frozen at 10 Hz by the approved plan.")]
    public float npcHz = 10f;

    [Header("Building registry")]
    [Tooltip("If true, also scan the whole scene for BATI buildings (SHOULD). Never affects gameplay.")]
    public bool writeFullBatiRegistry = true;
    [Tooltip("Yield every N objects while scanning the scene for buildings.")]
    public int registryScanChunk = 500;

    [Header("Debug")]
    [Tooltip("Gates informational logs. Stream-failure errors are always shown.")]
    public bool enableDebugLogs = true;

    // ---------------- public read context ----------------

    public string SessionId     { get; private set; } = "";
    public string SessionFolder  { get; private set; } = "";
    public string CurrentPart    { get; private set; } = "Unknown";
    public string CurrentPhase   { get; private set; } = "idle";

    /// <summary>
    /// The current Part's PartCaptureConfig, read directly from ExperimentManager - the single authoritative
    /// source for per-Part NPC/capture configuration (Requirement 7). Null when no Part is running or no
    /// ExperimentManager is present, in which case all three *CaptureActive properties below are false.
    /// </summary>
    ExperimentManager.PartCaptureConfig CurrentConfig => _experimentManager != null ? _experimentManager.CurrentPartConfig : null;

    /// <summary>
    /// Trajectory is captured only when the current Part's config enables it (trajectoryCaptureEnabled), a
    /// trial is currently open (_trialActive, OnTrialStart..OnTrialEnd), and the End-Panel hard stop has not
    /// fired (_endPanelActive). Replaces the old CurrentPart==Part2||Part3 rule, which could not express
    /// per-Part configurability.
    /// </summary>
    public bool TrajectoryCaptureActive => CurrentConfig != null && CurrentConfig.trajectoryCaptureEnabled && _trialActive && !_endPanelActive;

    /// <summary>Gaze is captured only when the current Part's config enables it, a trial is open, and the End-Panel hard stop has not fired.</summary>
    public bool GazeCaptureActive => CurrentConfig != null && CurrentConfig.gazeCaptureEnabled && _trialActive && !_endPanelActive;

    /// <summary>NPC telemetry is captured only when the current Part's config enables it, a trial is open, no NPC warm-up window is open, and the End-Panel hard stop has not fired.</summary>
    public bool NpcCaptureActive => CurrentConfig != null && CurrentConfig.npcCaptureEnabled && _trialActive && !_npcWarmupActive && !_endPanelActive;

    public GameObject AssignedBuildingD1 { get; private set; }
    public GameObject AssignedBuildingD2 { get; private set; }

    public int TeleportEpoch => _teleportEpoch;

    // ---------------- internal state ----------------

    ExperimentManager        _experimentManager;
    ExternalSessionController _sessionController;
    SpawnerManager           _spawner;
    GazeRecorder             _gazeRecorder;

    Transform _playerRig, _centerEye, _ctrlL, _ctrlR;

    CaptureCsvWriter _events, _traj, _npcState, _npcEvents, _registry;

    int    _teleportEpoch = 0;
    double _lastEpochBumpT = -10.0;
    int    _spawnInstanceCounter = 0;

    // NEW (NPC warm-up): true while ExperimentManager's pre-trial NPC warm-up window is open (between the
    // npc_warmup_start and npc_warmup_end experiment events for the current Part). Used only to gate
    // NpcCaptureActive; never affects trajectory/gaze capture or any other stream.
    bool _npcWarmupActive = false;

    // NEW (participant capture scope, renamed from _trajectoryCaptureActive): true only between
    // ExperimentManager.OnTrialStart and OnTrialEnd for the Part currently running - set/cleared in the
    // OnTrialStart()/OnTrialEnd() handlers below, which already exist and already subscribe to those exact
    // events. Deliberately Part-agnostic (set to true even during a Part1 trial), mirroring
    // SpawnerManager.trialActive: it only tracks "is a trial currently open", not "which Part" or "which
    // stream". Per-Part enable/disable and the End-Panel hard stop are layered on separately by each
    // *CaptureActive property above, which is what actually decides whether a given stream records anything.
    bool _trialActive = false;

    // NEW (End Panel hard stop): true from the moment ExperimentManager.OnEndPanelShown fires for the
    // current Part until the next Part's part_start event is handled below (HandleExperimentEvent). Gates
    // all three *CaptureActive properties above. In the CURRENT code this is provably redundant with
    // _trialActive (OnTrialEnd always fires synchronously, in the same frame, strictly before
    // OnEndPanelShown - see ExperimentManager.RunPartX()), but it is kept as an explicit, independent
    // condition so the invariant ("no capture while the End Panel is visible") remains correct even if that
    // synchronous relationship is ever changed (e.g. a fade-out before the panel appears), and so it reads
    // as self-documenting intent rather than an implicit assumption about coroutine timing.
    bool _endPanelActive = false;

    sealed class NpcRec
    {
        public PedestrianAgent pa;
        public Transform tr;
        public NavMeshAgent agent;
        public MoveToTarget mt;
        public string instanceId;
        public string lastState = "";
        public bool hasLastState;
        public Vector3 lastDest;
        public bool hasLastDest;
    }
    readonly Dictionary<PedestrianAgent, NpcRec> _npcByAgent = new Dictionary<PedestrianAgent, NpcRec>();
    readonly Dictionary<GameObject, NpcRec>      _npcByGo    = new Dictionary<GameObject, NpcRec>();

    readonly HashSet<string> _buildingNames = new HashSet<string>();
    bool _registryStarted;

    int _losMask;

    readonly List<string> _rf = new List<string>(48);       // reusable row buffer
    readonly List<string> _npcLines = new List<string>(64); // reusable batch buffer

    RiverTeleportPoint[] _riverPoints;

    bool _endWritten;
    bool _npcProduced;
    readonly List<string> _fileList = new List<string>();
    readonly HashSet<string> _warned = new HashSet<string>();

    Manifest _manifest;

    // ---------------- lifecycle ----------------

    void Start()
    {
        _experimentManager = FindObjectOfType<ExperimentManager>();
        _sessionController  = FindObjectOfType<ExternalSessionController>();
        _spawner            = FindObjectOfType<SpawnerManager>();
        _gazeRecorder       = FindObjectOfType<GazeRecorder>();

        if (_experimentManager != null) _playerRig = _experimentManager.playerRig;
        _centerEye = FindDeep(_playerRig, "CenterEyeAnchor") ?? FindAnywhere("CenterEyeAnchor");
        _ctrlL = FindDeep(_playerRig, "LeftHandAnchor")  ?? FindDeep(_playerRig, "LeftControllerAnchor")  ?? FindAnywhere("LeftHandAnchor");
        _ctrlR = FindDeep(_playerRig, "RightHandAnchor") ?? FindDeep(_playerRig, "RightControllerAnchor") ?? FindAnywhere("RightHandAnchor");

        _losMask = (1 << 0); // "Default"
        int gl = LayerMask.NameToLayer("Ground");
        if (gl >= 0) _losMask |= (1 << gl);

        string participantId = (_sessionController != null && !string.IsNullOrEmpty(_sessionController.participantId))
            ? _sessionController.participantId : "UNKNOWN";
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        SessionId = SanitizeFileToken(participantId) + "_" + stamp;
        SessionFolder = Path.Combine(Application.persistentDataPath, SessionId);
        try { Directory.CreateDirectory(SessionFolder); }
        catch (Exception e) { Debug.LogError("[DataCapture] Could not create session folder: " + e.Message); }

        _events    = new CaptureCsvWriter(Path.Combine(SessionFolder, "events.csv"),            CaptureConstants.EventsHeader);
        _traj      = new CaptureCsvWriter(Path.Combine(SessionFolder, "trajectory.csv"),         CaptureConstants.TrajectoryHeader);
        _npcState  = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_state.csv"),          CaptureConstants.NpcStateHeader);
        _npcEvents = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_events.csv"),         CaptureConstants.NpcEventsHeader);
        _registry  = new CaptureCsvWriter(Path.Combine(SessionFolder, "building_registry.csv"), CaptureConstants.BuildingRegistryHeader);

        CurrentPart = (_sessionController != null) ? _sessionController.selectedPart.ToString() : "Unknown";
        CurrentPhase = "idle";

        if (_experimentManager != null)
        {
            _experimentManager.OnTrialStart += OnTrialStart;
            _experimentManager.OnTrialEnd   += OnTrialEnd;
            _experimentManager.OnExperimentEvent += OnExperimentEvent;
            _experimentManager.OnEndPanelShown += OnEndPanelShown;
        }
        else Debug.LogWarning("[DataCapture] No ExperimentManager found; event capture / scope tracking limited.");

        if (_spawner != null)
        {
            _spawner.AgentSpawned   += OnAgentSpawned;
            _spawner.AgentDespawned += OnAgentDespawned;
        }
        else if (enableDebugLogs) Debug.LogWarning("[DataCapture] No SpawnerManager found; NPC capture disabled.");

        _riverPoints = FindObjectsOfType<RiverTeleportPoint>();
        for (int i = 0; i < _riverPoints.Length; i++)
            if (_riverPoints[i] != null) _riverPoints[i].OnPlayerTeleported += OnRiverCrossing;

        TargetSelectable.Clicked += OnTargetSelectableClicked;

        BuildManifestPass1();
        WriteManifest();
        WriteEventRow(CaptureConstants.EvtSessionStart, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);

        MaybeBuildRegistry();

        StartCoroutine(TrajectoryLoop());
        StartCoroutine(NpcLoop());

        if (enableDebugLogs)
            Debug.Log($"[DataCapture] Session '{SessionId}' -> {SessionFolder} (configured part: {CurrentPart})");
    }

    void OnDestroy()
    {
        if (_experimentManager != null)
        {
            _experimentManager.OnTrialStart -= OnTrialStart;
            _experimentManager.OnTrialEnd   -= OnTrialEnd;
            _experimentManager.OnExperimentEvent -= OnExperimentEvent;
            _experimentManager.OnEndPanelShown -= OnEndPanelShown;
        }
        if (_spawner != null)
        {
            _spawner.AgentSpawned   -= OnAgentSpawned;
            _spawner.AgentDespawned -= OnAgentDespawned;
        }
        if (_riverPoints != null)
            for (int i = 0; i < _riverPoints.Length; i++)
                if (_riverPoints[i] != null) _riverPoints[i].OnPlayerTeleported -= OnRiverCrossing;

        TargetSelectable.Clicked -= OnTargetSelectableClicked;

        if (!_endWritten)
        {
            WriteEventRow(CaptureConstants.EvtSessionEnd, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "on_destroy", Vector3.zero, Vector3.zero);
            WriteManifestEnd("on_destroy");
            _endWritten = true;
        }
    }

    void OnApplicationFocus(bool hasFocus)
    {
        WriteEventRow(CaptureConstants.EvtAppFocus, CurrentPart, CurrentPhase, "", "", "", "", "",
            hasFocus ? 1.0 : 0.0, "", Vector3.zero, Vector3.zero);
    }

    void OnApplicationPause(bool pause)
    {
        WriteEventRow(CaptureConstants.EvtAppPause, CurrentPart, CurrentPhase, "", "", "", "", "",
            pause ? 1.0 : 0.0, "", Vector3.zero, Vector3.zero);
        // Defensive snapshot; does NOT mark the session finalised (it may resume).
        if (pause) WriteManifestEnd("app_pause");
    }

    void OnApplicationQuit()
    {
        if (_endWritten) return;
        WriteEventRow(CaptureConstants.EvtSessionEnd, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "app_quit", Vector3.zero, Vector3.zero);
        WriteManifestEnd("app_quit");
        _endWritten = true;
    }

    // ---------------- experiment event stream ----------------

    void OnExperimentEvent(ExperimentEventInfo e)
    {
        try { HandleExperimentEvent(e); }
        catch (Exception ex) { WarnOnce("exp_event", "OnExperimentEvent handler failed: " + ex.Message); }
    }

    void HandleExperimentEvent(ExperimentEventInfo e)
    {
        if (e.type == CaptureConstants.EvtPartStart)
        {
            CurrentPart = string.IsNullOrEmpty(e.part) ? CurrentPart : e.part;
            CurrentPhase = "idle";

            // NEW (End Panel hard stop): clear any stale hard-stop from whichever Part ran previously, so
            // the new Part's own OnTrialStart/OnTrialEnd window can capture normally. ExperimentManager has
            // already set CurrentPartConfig to the new Part's config by the time this event is raised (it is
            // the very first statement in each RunPartX()), so CurrentConfig below already reflects the new
            // Part.
            _endPanelActive = false;

            // NEW (per-Part config, Requirement 1A): one-time diagnostic for a suspicious-but-allowed
            // configuration - npcCaptureEnabled=true with npcEnabled=false produces an empty (not missing)
            // npc_state.csv/npc_events.csv for this Part, since no NPC can ever exist to capture.
            var cfg = CurrentConfig;
            if (cfg != null && cfg.npcCaptureEnabled && !cfg.npcEnabled)
            {
                WarnOnce("npc_capture_without_npc_" + CurrentPart,
                    $"{CurrentPart}: npcCaptureEnabled=true but npcEnabled=false - npc_state.csv/npc_events.csv " +
                    "will contain zero rows for this Part (no NPCs will exist to capture).");
            }

            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", e.numValue, e.stringValue, Vector3.zero, Vector3.zero);
            MaybeBuildRegistry();
            if (CurrentPart != CaptureConstants.Part1)
                WriteEventRow(CaptureConstants.EvtRecordingMarker, CurrentPart, CurrentPhase, e.label, "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);
            return;
        }

        if (e.type == CaptureConstants.EvtPartEnd)
        {
            WriteEventRow(e.type, string.IsNullOrEmpty(e.part) ? CurrentPart : e.part, CurrentPhase, e.label, "", "", "", "", e.numValue, e.stringValue, Vector3.zero, Vector3.zero);
            CurrentPart = CaptureConstants.PartIdle;
            return;
        }

        if (e.type == CaptureConstants.EvtPhaseStart)
        {
            CurrentPhase = string.IsNullOrEmpty(e.label) ? CurrentPhase : e.label;
            if (CurrentPart == CaptureConstants.Part1) return;
            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);
            return;
        }

        // NEW (NPC warm-up): structural markers, handled the same way as part_start/part_end - always
        // written to events.csv (even for Part 1, for audit-trail completeness, though they are never
        // actually raised for a Part whose npcEnabled is false) and NEVER dependent on CurrentPart.
        // _npcWarmupActive is the ONLY thing these two events change; they never touch
        // CurrentPart/CurrentPhase/TrajectoryCaptureActive/GazeCaptureActive.
        if (e.type == CaptureConstants.EvtNpcWarmupStart)
        {
            _npcWarmupActive = true;
            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", e.numValue, "", Vector3.zero, Vector3.zero);
            return;
        }

        if (e.type == CaptureConstants.EvtNpcWarmupEnd)
        {
            _npcWarmupActive = false;
            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", e.numValue, "", Vector3.zero, Vector3.zero);
            return;
        }

        if (e.type == CaptureConstants.EvtTeleport)
        {
            BumpTeleportEpoch();
            if (CurrentPart == CaptureConstants.Part1) return;
            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", 0.0, S(e.stringValue), e.fromPos, e.toPos);
            return;
        }

        if (e.type == CaptureConstants.Part3Log) { HandlePart3Log(e); return; }

        // research event -> dropped entirely during Part 1
        if (CurrentPart == CaptureConstants.Part1) return;

        switch (e.type)
        {
            case CaptureConstants.EvtPanelShown:
            case CaptureConstants.EvtPanelConfirmed:
            case CaptureConstants.EvtDestinationShown:
            case CaptureConstants.EvtDestinationConfirmed:
                WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", 0.0, S(e.stringValue), Vector3.zero, Vector3.zero);
                return;

            case CaptureConstants.EvtCandidateInteractOn:
            case CaptureConstants.EvtCandidateInteractOff:
                WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label,
                    e.building != null ? S(e.building.name) : "", ResolveTargetId(e.building), "", "", 0.0, "", Vector3.zero, Vector3.zero);
                return;

            default:
                WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", e.numValue, S(e.stringValue), e.fromPos, e.toPos);
                return;
        }
    }

    void HandlePart3Log(ExperimentEventInfo e)
    {
        switch (e.part3Event)
        {
            case "assigned":
            {
                AssignedBuildingD1 = e.d1Building;
                AssignedBuildingD2 = e.d2Building;

                string bid = Name(e.d1Building) + "|" + Name(e.d2Building);
                string tid = S(e.d1TargetId) + "|" + S(e.d2TargetId);
                string vs  = $"group={e.groupNumber};start_zone={e.startZone};d1_zone={e.d1Zone};d2_zone={e.d2Zone};start_point={S(e.startPointName)}";
                WriteEventRow(CaptureConstants.EvtTargetAssigned, CaptureConstants.Part3, CurrentPhase, "", bid, tid, "", "", 0.0, vs, Vector3.zero, Vector3.zero);
                UpdateManifestAssigned(e);
                return;
            }
            case "d1_click":
            case "d2_click":
            {
                string label = e.part3Event == "d1_click" ? "D1" : "D2";
                string type  = e.boolValue ? CaptureConstants.EvtCorrectSelection : CaptureConstants.EvtIncorrectSelection;
                WriteEventRow(type, CaptureConstants.Part3, CurrentPhase, label, "", S(e.stringValue), "", "", e.numValue, S(e.notes), Vector3.zero, Vector3.zero);
                return;
            }
            case "d1_result":
            case "d2_result":
            case "d2_skipped":
            {
                string label = e.part3Event == "d1_result" ? "D1" : "D2";
                WriteEventRow(CaptureConstants.EvtPhaseResult, CaptureConstants.Part3, CurrentPhase, label, "", "", "", "", e.numValue, S(e.notes), Vector3.zero, Vector3.zero);
                if (!string.IsNullOrEmpty(e.notes) &&
                    (e.notes.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     e.notes.IndexOf("time_reached", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    WriteEventRow(CaptureConstants.EvtPhaseTimeout, CaptureConstants.Part3, CurrentPhase, label, "", "", "", "", e.numValue, S(e.notes), Vector3.zero, Vector3.zero);
                }
                return;
            }
            case "phase_end":
                WriteEventRow(CaptureConstants.EvtPhaseEnd, CaptureConstants.Part3, CurrentPhase, "", "", "", "", "", e.numValue, S(e.notes), Vector3.zero, Vector3.zero);
                return;
        }
    }

    // ---------------- trial + interaction + teleport subscribers ----------------

    void OnTrialStart()
    {
        // NEW (participant capture scope): flips BEFORE the Part1 early-return below, so it always reflects
        // "a trial is currently open" regardless of Part. Harmless for Part1 - TrajectoryCaptureActive /
        // GazeCaptureActive / NpcCaptureActive also require CurrentConfig's respective *CaptureEnabled flag
        // (false by default for Part1), so this never causes Part1 rows to be written.
        _trialActive = true;

        if (CurrentPart == CaptureConstants.Part1) return;
        WriteEventRow(CaptureConstants.EvtTrialStart, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);
    }

    void OnTrialEnd()
    {
        // NEW (participant capture scope): see OnTrialStart() above.
        _trialActive = false;

        if (CurrentPart == CaptureConstants.Part1) return;
        WriteEventRow(CaptureConstants.EvtTrialEnd, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);
    }

    /// <summary>
    /// NEW (End Panel hard stop, Requirement 2/2A). Called when ExperimentManager raises OnEndPanelShown for
    /// the Part currently running - immediately before that Part's End Panel is shown to the participant,
    /// strictly before part_end. Sets _endPanelActive, which TrajectoryCaptureActive / GazeCaptureActive /
    /// NpcCaptureActive all require to be false - so TrajectoryLoop()/NpcLoop() stop producing rows on their
    /// very next tick, and GazeRecorder's own per-sample check (dataCapture.GazeCaptureActive) does the same
    /// for gaze.csv. No event row is written here beyond this in-memory flag flip: the moment is already
    /// fully timestamped in events.csv by the existing "panel_shown" row (label "partX_end"), raised by
    /// ExperimentManager immediately after this handler runs - a dedicated new event type would only
    /// duplicate that timestamp (see Requirement 8).
    /// </summary>
    void OnEndPanelShown()
    {
        _endPanelActive = true;
    }

    void OnTargetSelectableClicked(TargetSelectable ts)
    {
        try
        {
            if (ts == null || CurrentPart == CaptureConstants.Part1) return;
            WriteEventRow(CaptureConstants.EvtBuildingClick, CurrentPart, CurrentPhase, "",
                S(ts.gameObject.name), S(ts.targetId), "", "", 0.0, "", Vector3.zero, Vector3.zero);
        }
        catch (Exception ex) { WarnOnce("target_click", "building_click capture failed: " + ex.Message); }
    }

    void OnRiverCrossing(RiverTeleportPoint from, RiverTeleportPoint to, Vector3 fromPos, Vector3 toPos)
    {
        try
        {
            BumpTeleportEpoch();
            if (CurrentPart == CaptureConstants.Part1) return;
            string label = (from != null ? from.name : "?") + "->" + (to != null ? to.name : "?");
            WriteEventRow(CaptureConstants.EvtRiverCrossing, CurrentPart, CurrentPhase, S(label), "", "", "", "", 0.0, "", fromPos, toPos);
        }
        catch (Exception ex) { WarnOnce("river", "river_crossing capture failed: " + ex.Message); }
    }

    void BumpTeleportEpoch()
    {
        double now = CaptureClock.TAbs;
        if (now - _lastEpochBumpT < 0.10) return; // 100 ms debounce
        _teleportEpoch++;
        _lastEpochBumpT = now;
    }

    // ---------------- NPC lifecycle (Part 3 only, and never during NPC warm-up) ----------------

    void OnAgentSpawned(PedestrianAgent pa)
    {
        try
        {
            // NpcCaptureActive is false during warm-up, so a warm-up spawn simply is not recorded here -
            // no spawn record is created, and the agent will be "late-bound" by SampleNpcs() the first time
            // it is sampled after NpcCaptureActive becomes true (see SampleNpcs()).
            if (!NpcCaptureActive || pa == null) return;

            NpcRec rec = new NpcRec
            {
                pa = pa,
                tr = pa.transform,
                agent = pa.GetComponent<NavMeshAgent>() ?? pa.GetComponentInChildren<NavMeshAgent>(true),
                mt    = pa.GetComponent<MoveToTarget>() ?? pa.GetComponentInChildren<MoveToTarget>(true),
                instanceId = "S" + (++_spawnInstanceCounter)
            };
            _npcByAgent[pa] = rec;
            _npcByGo[pa.gameObject] = rec;

            Vector3 pos = rec.tr.position;
            float yaw = rec.tr.eulerAngles.y;
            bool agentReady = rec.agent != null && rec.agent.isActiveAndEnabled && rec.agent.isOnNavMesh;
            Vector3 dest = agentReady ? rec.agent.destination : pos;
            string speedSetting = rec.agent != null ? F(rec.agent.speed) : "";
            string corners = agentReady ? CornersPacked(SafePathCorners(rec.agent)) : "";

            rec.lastState = SafeState(rec.pa); rec.hasLastState = true;
            rec.lastDest = dest;               rec.hasLastDest = true;

            WriteNpcEventRow(CaptureConstants.NpcEvtSpawn, rec, pos, yaw, dest, speedSetting, corners, "");
            WriteEventRow(CaptureConstants.EvtNpcSpawn, CurrentPart, CurrentPhase, "", "", "",
                S(rec.pa.AgentID), rec.instanceId, 0.0, "", Vector3.zero, Vector3.zero);
        }
        catch (Exception ex) { WarnOnce("npc_spawn", "npc_spawn capture failed: " + ex.Message); }
    }

    void OnAgentDespawned(PedestrianAgent pa)
    {
        try
        {
            if (pa == null) return;
            if (!_npcByAgent.TryGetValue(pa, out NpcRec rec)) return;

            Vector3 pos = rec.tr != null ? rec.tr.position : Vector3.zero;
            float yaw   = rec.tr != null ? rec.tr.eulerAngles.y : 0f;
            Vector3 dest = (rec.agent != null && rec.agent.isActiveAndEnabled && rec.agent.isOnNavMesh) ? rec.agent.destination : pos;

            WriteNpcEventRow(CaptureConstants.NpcEvtDespawn, rec, pos, yaw, dest, "", "", "despawn");
            WriteEventRow(CaptureConstants.EvtNpcDespawn, CurrentPart, CurrentPhase, "", "", "",
                S(rec.pa != null ? rec.pa.AgentID : ""), rec.instanceId, 0.0, "despawn", Vector3.zero, Vector3.zero);

            _npcByAgent.Remove(pa);
            if (rec.pa != null) _npcByGo.Remove(rec.pa.gameObject);
        }
        catch (Exception ex) { WarnOnce("npc_despawn", "npc_despawn capture failed: " + ex.Message); }
    }

    public string GetSpawnInstanceId(PedestrianAgent pa)
        => (pa != null && _npcByAgent.TryGetValue(pa, out NpcRec rec)) ? rec.instanceId : "";

    public bool IsRegisteredBuildingName(string name)
        => !string.IsNullOrEmpty(name) && _buildingNames.Contains(name);

    // ---------------- trajectory loop (per-Part configurable, 20 Hz), ONLY between OnTrialStart and OnTrialEnd, and never after the End Panel is shown ----------------

    IEnumerator TrajectoryLoop()
    {
        while (true)
        {
            float interval = 1f / Mathf.Max(1f, trajectoryHz);
            // NEW (per-Part config + End Panel hard stop): TrajectoryCaptureActive folds together the
            // current Part's trajectoryCaptureEnabled flag, the open-trial flag (_trialActive, set by
            // OnTrialStart/OnTrialEnd), and the End-Panel hard stop (_endPanelActive). Previously a hardcoded
            // CurrentPart==Part2||Part3 check gated this loop; that could not express per-Part configurability
            // and had no notion of the End-Panel boundary. Part 3's internal D1/D2 sub-phases raise no
            // additional OnTrialStart/OnTrialEnd (see ExperimentManager.RunPart3: exactly one pair per Part),
            // so this does not fragment trajectory capture within Part 3 - it only trims the pre-trial and
            // post-trial/End-Panel edges.
            if (!TrajectoryCaptureActive) { yield return new WaitForSecondsRealtime(interval); continue; }
            try { SampleTrajectory(); }
            catch (Exception e) { WarnOnce("traj_tick", "trajectory sample failed: " + e.Message); }
            yield return new WaitForSecondsRealtime(interval);
        }
    }

    void SampleTrajectory()
    {
        if (_traj.Failed) return;
        if (_playerRig == null) WarnOnce("traj_no_rig", "playerRig is null; trajectory.csv rig columns will be blank.");

        _rf.Clear();
        _rf.Add(F(CaptureClock.TAbs));
        _rf.Add(CaptureClock.TUnixMs.ToString(CultureInfo.InvariantCulture));
        _rf.Add(Time.frameCount.ToString(CultureInfo.InvariantCulture));

        if (_playerRig != null) { Vector3 rp = _playerRig.position; _rf.Add(F(rp.x)); _rf.Add(F(rp.y)); _rf.Add(F(rp.z)); }
        else { _rf.Add(""); _rf.Add(""); _rf.Add(""); }

        _rf.Add(_teleportEpoch.ToString(CultureInfo.InvariantCulture));

        if (_playerRig != null) { _rf.Add(F(_playerRig.eulerAngles.y)); Vector3 fw = _playerRig.forward; _rf.Add(F(fw.x)); _rf.Add(F(fw.z)); }
        else { _rf.Add(""); _rf.Add(""); _rf.Add(""); }

        if (_centerEye != null)
        {
            Vector3 hp = _centerEye.position; Quaternion hr = _centerEye.rotation;
            _rf.Add(F(hp.x)); _rf.Add(F(hp.y)); _rf.Add(F(hp.z));
            _rf.Add(F(hr.x)); _rf.Add(F(hr.y)); _rf.Add(F(hr.z)); _rf.Add(F(hr.w));
            if (_playerRig != null) { Vector3 lp = _playerRig.InverseTransformPoint(hp); _rf.Add(F(lp.x)); _rf.Add(F(lp.y)); _rf.Add(F(lp.z)); }
            else { _rf.Add(""); _rf.Add(""); _rf.Add(""); }
        }
        else { for (int i = 0; i < 10; i++) _rf.Add(""); }

        AppendControllerFields(_ctrlL, OVRControllerTracked(OVRInput.Controller.LTouch));
        AppendControllerFields(_ctrlR, OVRControllerTracked(OVRInput.Controller.RTouch));

        _traj.WriteLine(_traj.Join(_rf));
    }

    void AppendControllerFields(Transform ct, int trackedFlag)
    {
        if (ct != null)
        {
            Vector3 p = ct.position; Quaternion r = ct.rotation;
            _rf.Add(F(p.x)); _rf.Add(F(p.y)); _rf.Add(F(p.z));
            _rf.Add(F(r.x)); _rf.Add(F(r.y)); _rf.Add(F(r.z)); _rf.Add(F(r.w));
            _rf.Add(trackedFlag < 0 ? "" : trackedFlag.ToString(CultureInfo.InvariantCulture));
        }
        else { for (int i = 0; i < 8; i++) _rf.Add(""); }
    }

    static int OVRControllerTracked(OVRInput.Controller c)
    {
        try { return OVRInput.GetControllerOrientationTracked(c) ? 1 : 0; }
        catch { return -1; }
    }

    // ---------------- NPC state loop (per-Part configurable, 10 Hz), never during NPC warm-up, never after the End Panel is shown ----------------

    IEnumerator NpcLoop()
    {
        while (true)
        {
            float interval = 1f / Mathf.Max(1f, npcHz);
            // FIRST operation every tick: bail out entirely unless the current Part's npcCaptureEnabled is
            // true, a trial is open, no warm-up window is open, and the End Panel has not been shown.
            if (!NpcCaptureActive) { yield return new WaitForSecondsRealtime(interval); continue; }
            try { SampleNpcs(); }
            catch (Exception e) { WarnOnce("npc_tick", "npc sample failed: " + e.Message); }
            yield return new WaitForSecondsRealtime(interval);
        }
    }

    void SampleNpcs()
    {
        if (_spawner == null || _npcState.Failed) return;

        IReadOnlyList<GameObject> active = _spawner.ActiveAgents;
        if (active == null || active.Count == 0) return;

        double tAbs = CaptureClock.TAbs;
        long tUnix = CaptureClock.TUnixMs;
        int frame = Time.frameCount;
        Vector3 headPos = _centerEye != null ? _centerEye.position : Vector3.zero;

        _npcLines.Clear();

        for (int i = 0; i < active.Count; i++)
        {
            GameObject go = active[i];
            if (go == null) continue;

            if (!_npcByGo.TryGetValue(go, out NpcRec rec))
            {
                // NEW (NPC warm-up): this is also the normal path for an NPC that spawned during warm-up
                // (when NpcCaptureActive was false, so OnAgentSpawned never created a record for it) and is
                // still alive now that the trial has started. It gets a fresh instance id here, at the
                // moment it FIRST becomes part of the participant's captured trial - not at its true
                // (warm-up) spawn time.
                PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
                if (pa == null) continue;
                rec = new NpcRec
                {
                    pa = pa, tr = pa.transform,
                    agent = pa.GetComponent<NavMeshAgent>() ?? pa.GetComponentInChildren<NavMeshAgent>(true),
                    mt    = pa.GetComponent<MoveToTarget>() ?? pa.GetComponentInChildren<MoveToTarget>(true),
                    instanceId = "S" + (++_spawnInstanceCounter)
                };
                _npcByAgent[pa] = rec; _npcByGo[go] = rec;
                WarnOnce("npc_latebind", "an active NPC had no spawn record; late-bound " + rec.instanceId);
            }

            if (rec.tr == null || rec.pa == null) continue;

            Vector3 pos = rec.tr.position;
            float yaw = rec.tr.eulerAngles.y;

            Vector3 vel = Vector3.zero, desired = Vector3.zero, navDest = pos;
            bool hasPath = false, pathPending = false;
            if (rec.agent != null && rec.agent.isActiveAndEnabled && rec.agent.isOnNavMesh)
            {
                vel = rec.agent.velocity;
                desired = rec.agent.desiredVelocity;
                navDest = rec.agent.destination;
                hasPath = rec.agent.hasPath;
                pathPending = rec.agent.pathPending;
            }

            string state = SafeState(rec.pa);
            float speed = vel.magnitude;

            Vector3 mtTarget = pos;
            if (rec.mt != null)
                mtTarget = rec.mt.targetTransform != null ? rec.mt.targetTransform.position : rec.mt.targetPosition;

            string los = "";
            if (_centerEye != null)
            {
                bool blocked = Physics.Linecast(headPos, pos + Vector3.up * 1.6f, _losMask, QueryTriggerInteraction.Ignore);
                los = blocked ? "0" : "1";
            }

            _rf.Clear();
            _rf.Add(F(tAbs));
            _rf.Add(tUnix.ToString(CultureInfo.InvariantCulture));
            _rf.Add(frame.ToString(CultureInfo.InvariantCulture));
            _rf.Add(S(rec.pa.AgentID));
            _rf.Add(rec.instanceId);
            _rf.Add(F(pos.x)); _rf.Add(F(pos.y)); _rf.Add(F(pos.z));
            _rf.Add(F(yaw));
            _rf.Add(F(vel.x)); _rf.Add(F(vel.y)); _rf.Add(F(vel.z));
            _rf.Add(F(navDest.x)); _rf.Add(F(navDest.y)); _rf.Add(F(navDest.z));
            _rf.Add(state);
            _rf.Add(F(speed));
            _rf.Add(F(desired.x)); _rf.Add(F(desired.z));
            _rf.Add(F(mtTarget.x)); _rf.Add(F(mtTarget.y)); _rf.Add(F(mtTarget.z));
            _rf.Add(hasPath ? "1" : "0");
            _rf.Add(pathPending ? "1" : "0");
            _rf.Add(los);
            _npcLines.Add(_npcState.Join(_rf));

            // edge detection -> npc_events
            if (!rec.hasLastState) { rec.lastState = state; rec.hasLastState = true; }
            else if (rec.lastState != state)
            {
                if (rec.lastState == "moving" && state == "dwelling")
                {
                    WriteNpcEventRow(CaptureConstants.NpcEvtArrived, rec, pos, yaw, navDest, "", "", "");
                    WriteNpcEventRow(CaptureConstants.NpcEvtDwellStart, rec, pos, yaw, navDest, "", "", "");
                }
                else if (rec.lastState == "dwelling" && state == "moving")
                {
                    WriteNpcEventRow(CaptureConstants.NpcEvtDwellEnd, rec, pos, yaw, navDest, "", "", "");
                }
                rec.lastState = state;
            }

            if (!rec.hasLastDest) { rec.lastDest = navDest; rec.hasLastDest = true; }
            else if ((navDest - rec.lastDest).sqrMagnitude > 0.25f) // > ~0.5 m
            {
                WriteNpcEventRow(CaptureConstants.NpcEvtDestChange, rec, pos, yaw, navDest, "", "", "");
                WriteNpcEventRow(CaptureConstants.NpcEvtLegStart, rec, pos, yaw, navDest, "", "", "");
                rec.lastDest = navDest;
            }
        }

        if (_npcLines.Count > 0) _npcProduced = true;
        _npcState.WriteLines(_npcLines);
    }

    void WriteNpcEventRow(string type, NpcRec rec, Vector3 pos, float yaw, Vector3 dest,
        string speedSetting, string cornersPacked, string valueStr)
    {
        if (_npcEvents.Failed) return;
        _rf.Clear();
        _rf.Add(F(CaptureClock.TAbs));
        _rf.Add(CaptureClock.TUnixMs.ToString(CultureInfo.InvariantCulture));
        _rf.Add(S(rec.pa != null ? rec.pa.AgentID : ""));
        _rf.Add(rec.instanceId);
        _rf.Add(type);
        _rf.Add(F(pos.x)); _rf.Add(F(pos.y)); _rf.Add(F(pos.z));
        _rf.Add(F(yaw));
        _rf.Add(F(dest.x)); _rf.Add(F(dest.y)); _rf.Add(F(dest.z));
        _rf.Add(speedSetting ?? "");
        _rf.Add(cornersPacked ?? "");
        _rf.Add(S(valueStr));
        _npcEvents.WriteLine(_npcEvents.Join(_rf));
    }

    // ---------------- events.csv writer ----------------

    void WriteEventRow(string type, string part, string phase, string label,
        string buildingId, string targetId, string npcId, string npcSpawnInstanceId,
        double valueNum, string valueStr, Vector3 fromPos, Vector3 toPos)
    {
        try
        {
            if (_events.Failed) return;
            _rf.Clear();
            _rf.Add(F(CaptureClock.TAbs));
            _rf.Add(CaptureClock.TUnixMs.ToString(CultureInfo.InvariantCulture));
            _rf.Add(S(type)); _rf.Add(S(part)); _rf.Add(S(phase)); _rf.Add(S(label));
            _rf.Add(S(buildingId)); _rf.Add(S(targetId)); _rf.Add(S(npcId)); _rf.Add(S(npcSpawnInstanceId));
            bool hasPos = fromPos != Vector3.zero || toPos != Vector3.zero;
            _rf.Add(hasPos ? F(fromPos.x) : ""); _rf.Add(hasPos ? F(fromPos.y) : ""); _rf.Add(hasPos ? F(fromPos.z) : "");
            _rf.Add(hasPos ? F(toPos.x)   : ""); _rf.Add(hasPos ? F(toPos.y)   : ""); _rf.Add(hasPos ? F(toPos.z)   : "");
            _rf.Add(valueNum != 0.0 ? F(valueNum) : "");
            _rf.Add(S(valueStr));
            _events.WriteLine(_events.Join(_rf));
        }
        catch (Exception ex) { WarnOnce("evt_write", "events.csv write failed: " + ex.Message); }
    }

    // ---------------- building registry (Part 2 + Part 3 only) ----------------

    void MaybeBuildRegistry()
    {
        if (_registryStarted) return;
        if (CurrentPart == CaptureConstants.Part1) return; // never for a Part 1 session
        _registryStarted = true;
        StartCoroutine(BuildRegistryCoroutine());
    }

    IEnumerator BuildRegistryCoroutine()
    {
        var seen = new HashSet<string>();

        if (_experimentManager != null)
        {
            AddExperimentBuilding(_experimentManager.part3ZoneGE?.buildingA, "part3_candidate", "GE", "A", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneGE?.buildingB, "part3_candidate", "GE", "B", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneGU?.buildingA, "part3_candidate", "GU", "A", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneGU?.buildingB, "part3_candidate", "GU", "B", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneOE?.buildingA, "part3_candidate", "OE", "A", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneOE?.buildingB, "part3_candidate", "OE", "B", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneOU?.buildingA, "part3_candidate", "OU", "A", -1, seen);
            AddExperimentBuilding(_experimentManager.part3ZoneOU?.buildingB, "part3_candidate", "OU", "B", -1, seen);

            if (_experimentManager.part1TargetBuildings != null)
                for (int i = 0; i < _experimentManager.part1TargetBuildings.Length; i++)
                    AddExperimentBuilding(_experimentManager.part1TargetBuildings[i], "part1_target", "", "", i, seen);
        }
        yield return null;

        if (writeFullBatiRegistry)
        {
            MeshRenderer[] renderers = FindObjectsOfType<MeshRenderer>();
            int chunk = Mathf.Max(50, registryScanChunk);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer r = renderers[i];
                if (r != null)
                {
                    Transform bt = ResolveBuildingTransform(r.transform);
                    if (bt != null && LooksLikeBati(bt.name) && seen.Add(bt.name))
                    {
                        WriteRegistryRow(bt.gameObject, "none", "", "", -1);
                        _buildingNames.Add(bt.name);
                    }
                }
                if ((i % chunk) == 0) yield return null;
            }
        }

        if (enableDebugLogs)
            Debug.Log($"[DataCapture] building_registry.csv written ({_buildingNames.Count} building names).");
    }

    void AddExperimentBuilding(GameObject go, string role, string zone, string slot, int p1ord, HashSet<string> seen)
    {
        if (go == null) return;
        seen.Add(go.name); // block the later BATI scan from writing a duplicate 'none' row
        WriteRegistryRow(go, role, zone, slot, p1ord);
        _buildingNames.Add(go.name);
    }

    void WriteRegistryRow(GameObject go, string role, string zone, string slot, int p1ord)
    {
        if (_registry.Failed || go == null) return;

        Transform t = go.transform;
        Vector3 pos = t.position;

        Bounds b = new Bounds(pos, Vector3.zero);
        Renderer rr = go.GetComponentInChildren<Renderer>();
        if (rr != null) b = rr.bounds;

        Collider col = go.GetComponentInChildren<Collider>();
        string colType = col != null ? col.GetType().Name : "";
        TargetSelectable ts = go.GetComponent<TargetSelectable>() ?? go.GetComponentInChildren<TargetSelectable>(true);

        _rf.Clear();
        _rf.Add(S(go.name));                                   // building_id
        _rf.Add(S(go.name));                                   // gameobject_name
        _rf.Add(go.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        _rf.Add(S(HierarchyPath(t)));
        _rf.Add(F(pos.x)); _rf.Add(F(pos.y)); _rf.Add(F(pos.z));
        _rf.Add(F(b.center.x)); _rf.Add(F(b.center.y)); _rf.Add(F(b.center.z));
        _rf.Add(F(b.size.x));   _rf.Add(F(b.size.y));   _rf.Add(F(b.size.z));
        _rf.Add(go.layer.ToString(CultureInfo.InvariantCulture));
        _rf.Add(S(go.tag));
        _rf.Add(col != null ? "1" : "0");
        _rf.Add(S(colType));
        _rf.Add(ts != null ? "1" : "0");
        _rf.Add(ts != null ? S(ts.targetId) : "");
        _rf.Add(S(role)); _rf.Add(S(zone)); _rf.Add(S(slot));
        _rf.Add(p1ord >= 0 ? p1ord.ToString(CultureInfo.InvariantCulture) : "");
        _registry.WriteLine(_registry.Join(_rf));
    }

    static Transform ResolveBuildingTransform(Transform t)
    {
        Transform cur = t;
        for (int i = 0; i < 6 && cur != null; i++) { if (LooksLikeBati(cur.name)) return cur; cur = cur.parent; }
        return t;
    }

    static readonly System.Text.RegularExpressions.Regex s_bati =
        new System.Text.RegularExpressions.Regex(@"^\d{5}[A-Z]{2}\d+$");
    static bool LooksLikeBati(string n) => !string.IsNullOrEmpty(n) && s_bati.IsMatch(n);

    string ResolveTargetId(GameObject building)
    {
        if (building == null) return "";
        TargetSelectable ts = building.GetComponent<TargetSelectable>() ?? building.GetComponentInChildren<TargetSelectable>(true);
        return ts != null ? S(ts.targetId) : "";
    }

    // ---------------- manifest ----------------

    /// <summary>
    /// NEW (per-Part config, OPTIONAL, Requirement 9): a compact snapshot of one Part's
    /// ExperimentManager.PartCaptureConfig, recorded in session_manifest.json so later analysis can tell,
    /// without opening the project, whether a given Part had NPCs/NPC capture/gaze/trajectory enabled.
    /// Purely informational - never read back by any capture logic.
    /// </summary>
    [Serializable]
    public class PartConfigSnapshot
    {
        public bool npc_enabled;
        public bool npc_capture_enabled;
        public bool gaze_capture_enabled;
        public bool trajectory_capture_enabled;

        public PartConfigSnapshot() { }
        public PartConfigSnapshot(ExperimentManager.PartCaptureConfig cfg)
        {
            if (cfg == null) return;
            npc_enabled = cfg.npcEnabled;
            npc_capture_enabled = cfg.npcCaptureEnabled;
            gaze_capture_enabled = cfg.gazeCaptureEnabled;
            trajectory_capture_enabled = cfg.trajectoryCaptureEnabled;
        }
    }

    [Serializable]
    public class Manifest
    {
        public string session_id, participant_id, started_utc, ended_utc = "", t_abs_at_start, t_abs_at_end = "", end_reason = "";
        public string selected_part;
        public int part2_group_index, part3_group_index;
        public int part3_group_number;
        public string part3_start_zone = "", part3_start_point_name = "", part3_d1_zone = "", part3_d2_zone = "";
        public string part3_d1_assigned_building_id = "pending", part3_d2_assigned_building_id = "pending";
        public string part3_d1_assigned_target_id   = "pending", part3_d2_assigned_target_id   = "pending";
        public string unity_version, app_version, build_guid, platform;
        public bool is_editor;
        public string device_model, device_name, graphics_device, scene_name, xr_device;
        public int target_frame_rate;
        public bool eye_tracking_supported, eye_tracking_enabled, eye_tracking_permission_granted;
        public bool gaze_left_present, gaze_right_present;
        public float gaze_confidence_threshold;
        public int roi_layer_mask;
        public float sample_rate_gaze_hz, sample_rate_trajectory_hz, sample_rate_npc_hz;
        public int max_agents;
        public float spawn_interval, dwell_time_min, dwell_time_max;
        // NEW (NPC warm-up, OPTIONAL): reproducibility record of the warm-up duration configured on
        // ExperimentManager at session start. Purely informational - never read back by any capture logic.
        public float npc_warmup_duration_seconds;
        // NEW (per-Part config, OPTIONAL, Requirement 9): snapshot of all three Parts' PartCaptureConfig,
        // regardless of which Part this session actually runs - see PartConfigSnapshot doc comment.
        public PartConfigSnapshot part1_config, part2_config, part3_config;
        public bool npc_capture, trajectory_ok = true, gaze_ok = true, events_ok = true;
        public string randomization_note =
            "UnityEngine.Random not seeded; AttractorDwell uses TickCount-seeded System.Random; NPC trajectories are not replayable from initial conditions.";
        public string[] file_list = new string[0];
    }

    void BuildManifestPass1()
    {
        _manifest = new Manifest
        {
            session_id      = SessionId,
            participant_id  = _sessionController != null ? _sessionController.participantId : "UNKNOWN",
            started_utc     = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            t_abs_at_start  = F(CaptureClock.TAbs),
            selected_part   = _sessionController != null ? _sessionController.selectedPart.ToString() : "Unknown",
            part2_group_index = _sessionController != null ? _sessionController.part2GroupIndex : 0,
            part3_group_index = _sessionController != null ? _sessionController.part3GroupIndex : 0,

            unity_version = Application.unityVersion,
            app_version   = Application.version,
            build_guid    = Application.buildGUID,
            platform      = Application.platform.ToString(),
            is_editor     = Application.isEditor,
            device_model  = SystemInfo.deviceModel,
            device_name   = SystemInfo.deviceName,
            graphics_device = SystemInfo.graphicsDeviceName,
            target_frame_rate = Application.targetFrameRate,
            scene_name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            xr_device  = SafeXrDevice(),

            eye_tracking_supported = SafeEyeSupported(),
            eye_tracking_enabled   = SafeEyeEnabled(),
            eye_tracking_permission_granted = SafeEyePermission(),
            gaze_left_present  = _gazeRecorder != null && _gazeRecorder.leftEyeGaze  != null,
            gaze_right_present = _gazeRecorder != null && _gazeRecorder.rightEyeGaze != null,
            gaze_confidence_threshold = _gazeRecorder != null ? _gazeRecorder.minConfidenceForValid : 0f,

            roi_layer_mask = _gazeRecorder != null ? _gazeRecorder.roiLayerMask.value : 0,
            sample_rate_gaze_hz = _gazeRecorder != null ? _gazeRecorder.sampleRate : 0f,
            sample_rate_trajectory_hz = trajectoryHz,
            sample_rate_npc_hz = npcHz,

            npc_capture = (_sessionController != null && _sessionController.selectedPart == ExternalSessionController.ExperimentPart.Part3),

            // NEW (NPC warm-up, OPTIONAL): record the configured warm-up duration for reproducibility.
            npc_warmup_duration_seconds = _experimentManager != null ? _experimentManager.npcWarmupDuration : 0f,

            // NEW (per-Part config, OPTIONAL): snapshot all three Parts' configuration up front - these are
            // static Inspector values, readable regardless of which Part this session will actually run.
            part1_config = new PartConfigSnapshot(_experimentManager?.part1Config),
            part2_config = new PartConfigSnapshot(_experimentManager?.part2Config),
            part3_config = new PartConfigSnapshot(_experimentManager?.part3Config),
        };

        _manifest.part3_group_number = Mathf.Clamp(_manifest.part3_group_index + 1, 1, 12);

        if (_spawner != null)
        {
            _manifest.max_agents      = _spawner.maxAgents;
            _manifest.spawn_interval  = _spawner.spawnInterval;
            _manifest.dwell_time_min  = _spawner.dwellTimeSec.x;
            _manifest.dwell_time_max  = _spawner.dwellTimeSec.y;
        }
    }

    void UpdateManifestAssigned(ExperimentEventInfo e)
    {
        if (_manifest == null) return;
        _manifest.part3_group_number = e.groupNumber;
        _manifest.part3_start_zone   = e.startZone ?? "";
        _manifest.part3_start_point_name = e.startPointName ?? "";
        _manifest.part3_d1_zone = e.d1Zone ?? "";
        _manifest.part3_d2_zone = e.d2Zone ?? "";
        _manifest.part3_d1_assigned_building_id = Name(e.d1Building);
        _manifest.part3_d2_assigned_building_id = Name(e.d2Building);
        _manifest.part3_d1_assigned_target_id = string.IsNullOrEmpty(e.d1TargetId) ? "" : e.d1TargetId;
        _manifest.part3_d2_assigned_target_id = string.IsNullOrEmpty(e.d2TargetId) ? "" : e.d2TargetId;
        WriteManifest();
    }

    void WriteManifest()
    {
        if (_manifest == null) return;
        try { File.WriteAllText(Path.Combine(SessionFolder, "session_manifest.json"), JsonUtility.ToJson(_manifest, true)); }
        catch (Exception e) { Debug.LogError("[DataCapture] Failed to write session_manifest.json: " + e.Message); }
    }

    void WriteManifestEnd(string reason)
    {
        if (_manifest == null) return;
        _manifest.ended_utc     = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        _manifest.t_abs_at_end  = F(CaptureClock.TAbs);
        _manifest.end_reason    = reason;
        _manifest.npc_capture   = _npcState != null && !_npcState.Failed && (_npcProduced || _spawnInstanceCounter > 0);
        _manifest.trajectory_ok = _traj != null && !_traj.Failed;
        _manifest.gaze_ok       = _gazeRecorder != null;
        _manifest.events_ok     = _events != null && !_events.Failed;

        _fileList.Clear();
        AddIfExists("session_manifest.json");
        AddIfExists("events.csv");
        AddIfExists("gaze.csv");
        AddIfExists("trajectory.csv");
        AddIfExists("building_registry.csv");
        AddIfExists("npc_state.csv");
        AddIfExists("npc_events.csv");
        _manifest.file_list = _fileList.ToArray();

        WriteManifest();
    }

    void AddIfExists(string name)
    {
        try { if (File.Exists(Path.Combine(SessionFolder, name))) _fileList.Add(name); }
        catch { /* ignore */ }
    }

    // ---------------- helpers ----------------

    static string F(float v)  => v.ToString("F4", CultureInfo.InvariantCulture);
    static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    static string S(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
    }

    static string Name(GameObject go) => go != null ? S(go.name) : "";

    string SafeState(PedestrianAgent pa)
    {
        try { return CaptureConstants.NpcStateToString(pa.CurrentState); }
        catch { return "unknown"; }
    }

    static Vector3[] SafePathCorners(NavMeshAgent a)
    {
        try { if (a != null && a.hasPath && a.path != null) return a.path.corners; }
        catch { }
        return null;
    }

    static string CornersPacked(Vector3[] c)
    {
        if (c == null || c.Length == 0) return "";
        var sb = new StringBuilder(64);
        for (int i = 0; i < c.Length; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(F(c[i].x)).Append(';').Append(F(c[i].y)).Append(';').Append(F(c[i].z));
        }
        return sb.ToString();
    }

    static string SanitizeFileToken(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        return s.Replace(' ', '_');
    }

    static string HierarchyPath(Transform t)
    {
        if (t == null) return "";
        var stack = new Stack<string>();
        Transform cur = t;
        int guard = 0;
        while (cur != null && guard++ < 64) { stack.Push(cur.name); cur = cur.parent; }
        var sb = new StringBuilder(128);
        bool first = true;
        while (stack.Count > 0) { if (!first) sb.Append('/'); sb.Append(stack.Pop()); first = false; }
        return sb.ToString();
    }

    Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform r = FindDeep(root.GetChild(i), name);
            if (r != null) return r;
        }
        return null;
    }

    Transform FindAnywhere(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null) return go.transform;
        Transform[] all = FindObjectsOfType<Transform>();
        for (int i = 0; i < all.Length; i++)
            if (all[i] != null && all[i].name == name) return all[i];
        return null;
    }

    static string SafeXrDevice()
    {
        try { return UnityEngine.XR.XRSettings.loadedDeviceName; } catch { return ""; }
    }

    static bool SafeEyeSupported() { try { return OVRPlugin.eyeTrackingSupported; } catch { return false; } }
    static bool SafeEyeEnabled()   { try { return OVRPlugin.eyeTrackingEnabled;   } catch { return false; } }

    static bool SafeEyePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { return UnityEngine.Android.Permission.HasUserAuthorizedPermission("com.oculus.permission.EYE_TRACKING"); }
        catch { return false; }
#else
        return false;
#endif
    }

    void WarnOnce(string key, string message)
    {
        if (_warned.Contains(key)) return;
        _warned.Add(key);
        Debug.LogWarning("[DataCapture] " + message);
    }
}
