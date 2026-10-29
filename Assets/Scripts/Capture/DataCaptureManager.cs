// DataCaptureManager.cs
// Central data-capture / observer layer for the Lyon VR wayfinding experiment (Architecture C, Revision 2).
//
// RESPONSIBILITIES
//   - Own the session output folder and the manifest (3 write passes).
//   - Own events.csv, trajectory.csv, npc_state.csv, npc_events.csv, building_registry.csv.
//   - Hold the single capture-scope state machine (TrajectoryCaptureActive / GazeCaptureActive /
//     NpcStateSamplingActive), each derived from ExperimentManager.CurrentPartConfig (the single authoritative
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
//   their trial. The window is only a LABEL now (capture_period = warmup) - NPC telemetry of a Part whose npcCaptureEnabled is true is
//   recorded during warm-up too (see NPC MASTER SWITCH below); the old 'no NPC rows during warm-up / before the trial' rule no longer exists.
//   _npcWarmupActive is driven by the npc_warmup_start/npc_warmup_end experiment events (raised ONLY for a Part whose npcEnabled is true).
//
// PARTICIPANT CAPTURE SCOPE (additive, Revision 2.2, generalised to gaze+NPC in Revision 2.3):
//   trajectory.csv (and, as of Revision 2.3, gaze.csv and NPC telemetry too) previously recorded for the
//   participant's entire Part2/Part3 lifetime, which begins the instant part_start fires - well before
//   OnTrialStart. That meant capture could include the teleport, the NPC warm-up window, and the Start panel
//   wait, none of which is the participant's actual experimental behaviour. TrajectoryCaptureActive /
//   GazeCaptureActive now both require _trialActive (NPC capture no longer does - see NPC MASTER SWITCH) (a Part-agnostic flag set true in
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
// NPC LIFETIME IDENTITY (schema v2, see CaptureConstants.SchemaVersion):
//   PedestrianAgent.AgentID = stable pooled GameObject. npc_spawn_instance_id = "<AgentID>-S<n>", one per active lifetime, allocated by
//   PedestrianAgent.Initialize() BEFORE the NPC joins the live population. Lifecycle events reach this class through NpcLifecycleBus
//   with the timestamps of the moment they happened. Inactive prewarmed pool objects only get a 'prewarm_created' event; they never get
//   a lifetime or position rows.
//
// NPC MASTER SWITCH: the per-Part PartCaptureConfig.npcCaptureEnabled (ExperimentManager) is the ONLY switch for NPC-specific research
//   data. NpcRecordingEnabled resolves it (current Part, or the selected Part before the first Part has started) and is installed as the
//   NpcLifecycleBus gate in Awake, i.e. before any prewarm event can exist. When it is false nothing NPC-specific is persisted or retained
//   for later replay: no lifecycle/pool events, no npc_events / npc_lifetimes / npc_state rows, no NPC rows in events.csv, no NPC identity
//   in gaze.csv. Generic gaze / trajectory capture and the NPC simulation itself are unaffected. When it is true, lifetime registration and
//   position sampling are NOT additionally gated by trial state (only the End-panel freeze ends position sampling).
//
// NPC STATE SAMPLING: the interval between complete population snapshots is npcStateSamplingIntervalSeconds (default 0.1 s); an optional
//   data budget (npcStateDataBudgetMBPerMinute, 0 = off) stretches that interval through NpcStateRateController. See docs/npc_data_schema.md.
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
    [HideInInspector, Tooltip("DEPRECATED - replaced by npcStateSamplingIntervalSeconds (1 / interval). Kept only so existing scene data still deserialises; no code reads it.")]
    public float npcHz = 10f;

    [Header("Building registry")]
    [Tooltip("If true, also scan the whole scene for BATI buildings (SHOULD). Never affects gameplay.")]
    public bool writeFullBatiRegistry = true;
    [Tooltip("Yield every N objects while scanning the scene for buildings.")]
    public int registryScanChunk = 500;

    [Header("NPC state sampling (npc_state.csv) - recorded only while the Part's 'npcCaptureEnabled' is true")]
    [Tooltip("Seconds between two COMPLETE NPC population snapshots in npc_state.csv (every active NPC is sampled together). This is not the render " +
             "frame rate. Frequency = 1 / interval: 0.05 = 20 Hz, 0.1 = 10 Hz (default), 0.2 = 5 Hz. Must be finite and > 0; NaN / infinite / <= 0 " +
             "fall back to 0.1, values below 0.01 are raised to 0.01. The real frequency is quantised by the frame rate; use the t_abs column for " +
             "the true spacing. Does not affect gaze or trajectory sampling.")]
    public float npcStateSamplingIntervalSeconds = 0.1f;

    [Tooltip("Target volume of the high-frequency npc_state.csv stream in decimal MB per minute. 0 (default) = unlimited: the interval above is used " +
             "unchanged. > 0: the interval between complete snapshots is stretched when the MEASURED bytes written to npc_state.csv (average row size x " +
             "active NPCs) would exceed the target, and relaxes back towards the base interval when there is headroom. NPCs are never dropped from a " +
             "snapshot. Lifecycle events, per-lifetime attributes (npc_lifetimes.csv), npc_events.csv, headers and session metadata are NOT limited and " +
             "may exceed it. NaN / infinite / negative values are treated as 0 (unlimited) with a warning.")]
    public float npcStateDataBudgetMBPerMinute = 0f;

    [Tooltip("Longest interval (seconds) the data budget may stretch the sampling to. Safe default 2 s (0.5 Hz). If even this cannot meet the budget " +
             "the sampler stays at this interval, logs a warning and the manifest records the real observed rate (budget status 'exceeded_unachievable'). " +
             "Values below the base interval are raised to it (no throttling room).")]
    public float npcStateMaxIntervalSeconds = 2f;

    [Header("NPC state sampling - live readout (written by the recorder, read-only)")]
    [SerializeField, Tooltip("Interval currently used between complete snapshots (equals the base interval unless the budget is throttling).")]
    float readoutEffectiveIntervalSeconds;
    [SerializeField, Tooltip("Rolling (10 s window) npc_state.csv data rate in decimal MB per minute.")]
    float readoutObservedMBPerMinute;
    [SerializeField, Tooltip("Active NPCs in the last complete snapshot.")]
    int readoutActiveNpcs;
    [SerializeField, Tooltip("unlimited | met | approached | exceeded | exceeded_unachievable")]
    string readoutBudgetStatus = "unlimited";

    [Tooltip("Test hook: when non-empty, the session folder is created under this root instead of Application.persistentDataPath.")]
    [NonSerialized] public string outputRootOverride = "";

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

    /// <summary>
    /// THE NPC MASTER SWITCH. True when the effective PartCaptureConfig.npcCaptureEnabled is true: the current Part's config once a Part has started,
    /// or - before the first Part starts (e.g. pool prewarm) - the config of the Part selected in ExternalSessionController. False when it cannot be
    /// determined (no ExperimentManager): unknown never means "record". Nothing NPC-specific is persisted, queued or replayed while this is false.
    /// </summary>
    public bool NpcRecordingEnabled
    {
        get
        {
            EnsureConfigSources();
            ExperimentManager.PartCaptureConfig cfg = CurrentConfig ?? SelectedPartConfig();
            return cfg != null && cfg.npcCaptureEnabled;
        }
    }

    /// <summary>
    /// Whether npc_state.csv rows are sampled right now: the master switch is on, the Part has NPCs, and the End-Panel freeze has not happened.
    /// There is no trial-state gate: warm-up, the Start-panel wait and pre-trial NPC activity are recorded too.
    /// </summary>
    public bool NpcStateSamplingActive
    {
        get
        {
            if (_endPanelActive || !NpcRecordingEnabled) return false;
            ExperimentManager.PartCaptureConfig cfg = CurrentConfig ?? SelectedPartConfig();
            return cfg != null && cfg.npcEnabled;
        }
    }

    /// <summary>Alias of NpcStateSamplingActive (the former trial-only second gate no longer exists).</summary>
    public bool NpcCaptureActive => NpcStateSamplingActive;

    public GameObject AssignedBuildingD1 { get; private set; }
    public GameObject AssignedBuildingD2 { get; private set; }

    public int TeleportEpoch => _teleportEpoch;

    // ---------------- NPC state sampling statistics (public, read by tests / readout) ----------------
    public NpcStateRateController NpcStateRate => _rate;
    public float NpcStateEffectiveIntervalSeconds => _rate.EffectiveInterval;
    public double NpcStateObservedMBPerMinute => _rate.ObservedMBPerMinute;
    public string NpcStateBudgetStatus => NpcStateRateController.StatusName(_rate.BudgetStatus);
    public int NpcEventsSuppressedByMasterSwitch => NpcLifecycleBus.SuppressedByGate + _npcSuppressedAtSink;

    // ---------------- internal state ----------------

    ExperimentManager        _experimentManager;
    ExternalSessionController _sessionController;
    SpawnerManager           _spawner;
    GazeRecorder             _gazeRecorder;

    Transform _playerRig, _centerEye, _ctrlL, _ctrlR;

    CaptureCsvWriter _events, _traj, _npcState, _npcEvents, _npcLifetimes, _npcProfiles, _registry;
    readonly HashSet<string> _profilesWritten = new HashSet<string>();

    int    _teleportEpoch = 0;
    double _lastEpochBumpT = -10.0;
    int    _lifetimesRegistered = 0;
    int    _npcSuppressedAtSink = 0;
    readonly NpcStateRateController _rate = new NpcStateRateController();
    bool   _rateWarnedUnachievable;
    double _rateLastWarnT = -1e9;
    int    _unresolvedNpcHits = 0;
    bool   _openLifetimesEmitted = false;
    string _runGuid = "";
    readonly List<string> _npcEventBuf = new List<string>(256);   // npc_events.csv lines waiting for the per-frame flush
    const int NpcEventBufMax = 4096;

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

    // Part 3 phase_timeout idempotency: one terminal timeout row per destination per session, even if a result event were repeated.
    bool _d1TimeoutLogged = false;
    bool _d2TimeoutLogged = false;

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
        public string agentId;
        public string lifetimeId;       // "<AgentID>-S<n>": the canonical npc_spawn_instance_id
        public int lifetimeIndex;
        public int sampleSeq;           // npc_state rows written for this lifetime
        public string lastState = "";
        public bool hasLastState;
        public Vector3 lastDest;
        public bool hasLastDest;
    }
    // Registry of OPEN lifetimes (one entry per pooled object that is currently an active NPC).
    readonly Dictionary<PedestrianAgent, NpcRec> _npcByAgent = new Dictionary<PedestrianAgent, NpcRec>();

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

    // Resolves the configuration sources and installs the NPC master-switch gate before any other component's Start() can raise a prewarm
    // event (Awake of every object runs before any Start). No file is created here and nothing is retained when the switch is off.
    void Awake()
    {
        EnsureConfigSources();
        _npcGate = () => NpcRecordingEnabled;
        NpcLifecycleBus.SetGate(_npcGate);
    }

    Func<bool> _npcGate;

    int _nextConfigLookupFrame;
    void EnsureConfigSources()
    {
        if (_experimentManager != null && _sessionController != null) return;
        if (Time.frameCount < _nextConfigLookupFrame) return;          // a missing source is re-searched at most once per second-ish, not per event
        _nextConfigLookupFrame = Time.frameCount + 60;
        if (_experimentManager == null) _experimentManager = FindObjectOfType<ExperimentManager>();
        if (_sessionController == null) _sessionController = FindObjectOfType<ExternalSessionController>();
    }

    // The configuration of the Part selected in ExternalSessionController, used only until the first Part has set CurrentPartConfig.
    ExperimentManager.PartCaptureConfig SelectedPartConfig()
    {
        if (_experimentManager == null || _sessionController == null) return null;
        switch (_sessionController.selectedPart)
        {
            case ExternalSessionController.ExperimentPart.Part1: return _experimentManager.part1Config;
            case ExternalSessionController.ExperimentPart.Part2: return _experimentManager.part2Config;
            case ExternalSessionController.ExperimentPart.Part3: return _experimentManager.part3Config;
            default: return null;
        }
    }

    void OnValidate()
    {
        npcStateSamplingIntervalSeconds = NpcStateRateController.SanitizeBaseInterval(npcStateSamplingIntervalSeconds, 0.1f, out _);
        npcStateDataBudgetMBPerMinute = NpcStateRateController.SanitizeBudget(npcStateDataBudgetMBPerMinute, out _);
        npcStateMaxIntervalSeconds = NpcStateRateController.SanitizeMaxInterval(npcStateMaxIntervalSeconds, npcStateSamplingIntervalSeconds, 2f, out _);
    }

    // Reads and sanitises the three Inspector values (every tick, so a change during play takes effect), warning once per kind of problem.
    void ApplyNpcSamplingSettings()
    {
        bool c1, c2, c3;
        float b = NpcStateRateController.SanitizeBaseInterval(npcStateSamplingIntervalSeconds, 0.1f, out c1);
        float budget = NpcStateRateController.SanitizeBudget(npcStateDataBudgetMBPerMinute, out c2);
        float max = NpcStateRateController.SanitizeMaxInterval(npcStateMaxIntervalSeconds, b, 2f, out c3);
        if (c1) WarnOnce("npc_interval_invalid", "npcStateSamplingIntervalSeconds=" + F(npcStateSamplingIntervalSeconds) + " is invalid (must be finite and > 0, minimum 0.01); using " + F(b) + " s.");
        if (c2) WarnOnce("npc_budget_invalid", "npcStateDataBudgetMBPerMinute=" + F(npcStateDataBudgetMBPerMinute) + " is invalid (must be finite and >= 0); treated as 0 (unlimited).");
        if (c3) WarnOnce("npc_max_interval_invalid", "npcStateMaxIntervalSeconds=" + F(npcStateMaxIntervalSeconds) + " is invalid or below the base interval; using " + F(max) + " s.");
        if (b != _rate.BaseInterval || max != _rate.MaxInterval || budget != (float)_rate.BudgetMBPerMinute) _rate.Configure(b, max, budget);
    }

    void Start()
    {
        EnsureConfigSources();
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
        string root = string.IsNullOrEmpty(outputRootOverride) ? Application.persistentDataPath : outputRootOverride;
        // Never join an existing session folder: two sessions of the same participant started within one second would otherwise share
        // files and session_id. A suffix keeps session_id (the cross-file key) unique.
        string baseId = SessionId;
        for (int n = 2; Directory.Exists(Path.Combine(root, SessionId)) && n < 1000; n++) SessionId = baseId + "-r" + n;
        _runGuid = Guid.NewGuid().ToString("N");
        SessionFolder = Path.Combine(root, SessionId);
        try { Directory.CreateDirectory(SessionFolder); }
        catch (Exception e) { Debug.LogError("[DataCapture] Could not create session folder: " + e.Message); }

        _events    = new CaptureCsvWriter(Path.Combine(SessionFolder, "events.csv"),            CaptureConstants.EventsHeader);
        _traj      = new CaptureCsvWriter(Path.Combine(SessionFolder, "trajectory.csv"),         CaptureConstants.TrajectoryHeader);
        _npcState  = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_state.csv"),          CaptureConstants.NpcStateHeader);
        _npcEvents = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_events.csv"),         CaptureConstants.NpcEventsHeader);
        _npcLifetimes = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_lifetimes.csv"),   CaptureConstants.NpcLifetimesHeader);
        _npcProfiles = new CaptureCsvWriter(Path.Combine(SessionFolder, "npc_profiles.csv"),    CaptureConstants.NpcProfilesHeader);
        _registry  = new CaptureCsvWriter(Path.Combine(SessionFolder, "building_registry.csv"), CaptureConstants.BuildingRegistryHeader);

        CurrentPart = (_sessionController != null) ? _sessionController.selectedPart.ToString() : "Unknown";
        CurrentPhase = "idle";
        _d1TimeoutLogged = false;
        _d2TimeoutLogged = false;

        if (_experimentManager != null)
        {
            _experimentManager.OnTrialStart += OnTrialStart;
            _experimentManager.OnTrialEnd   += OnTrialEnd;
            _experimentManager.OnExperimentEvent += OnExperimentEvent;
            _experimentManager.OnEndPanelShown += OnEndPanelShown;
            _experimentManager.OnSessionEnded += OnSessionEnded;
        }
        else Debug.LogWarning("[DataCapture] No ExperimentManager found; event capture / scope tracking limited.");

        if (_spawner == null && enableDebugLogs) Debug.LogWarning("[DataCapture] No SpawnerManager found; NPC census safety net disabled.");

        _riverPoints = FindObjectsOfType<RiverTeleportPoint>(true);   // includeInactive: river points are inactive outside Part 3, their component events must still be subscribed
        for (int i = 0; i < _riverPoints.Length; i++)
            if (_riverPoints[i] != null) _riverPoints[i].OnPlayerTeleported += OnRiverCrossing;

        TargetSelectable.Clicked += OnTargetSelectableClicked;

        ApplyNpcSamplingSettings();
        BuildManifestPass1();
        WriteManifest();
        WriteEventRow(CaptureConstants.EvtSessionStart, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "", Vector3.zero, Vector3.zero);

        // The session now exists: attach as the NPC lifecycle sink. Everything raised earlier (e.g. prewarm_created, if SpawnerManager started
        // first) is replayed in order with its original timestamps.
        NpcLifecycleBus.Attach(OnNpcLifecycle);

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
            _experimentManager.OnSessionEnded -= OnSessionEnded;
        }
        if (_riverPoints != null)
            for (int i = 0; i < _riverPoints.Length; i++)
                if (_riverPoints[i] != null) _riverPoints[i].OnPlayerTeleported -= OnRiverCrossing;

        TargetSelectable.Clicked -= OnTargetSelectableClicked;

        NpcLifecycleBus.Detach(OnNpcLifecycle);
        NpcLifecycleBus.ClearGate(_npcGate);
        EmitOpenLifetimes(_endWritten ? "destroyed_after_end" : "on_destroy");   // idempotent; also flushes npc_events

        if (!_endWritten)
        {
            WriteEventRow(CaptureConstants.EvtSessionEnd, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "on_destroy", Vector3.zero, Vector3.zero);
            WriteManifestEnd("on_destroy");
            _endWritten = true;
        }
    }

    // The participant confirmed the End panel: part_end was already written (ExperimentManager raises it first). Every row is
    // appended and closed immediately, so nothing is buffered; this writes the same session_end row and manifest end that
    // OnApplicationQuit/OnDestroy would, now, with the reason 'end_confirmed'. Idempotent via _endWritten.
    void OnSessionEnded()
    {
        EmitOpenLifetimes("session_end");
        if (_endWritten) return;
        WriteEventRow(CaptureConstants.EvtSessionEnd, CurrentPart, CurrentPhase, "", "", "", "", "", 0.0, "end_confirmed", Vector3.zero, Vector3.zero);
        WriteManifestEnd("end_confirmed");
        _endWritten = true;
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
        FlushNpcEvents();
        // Defensive snapshot; does NOT mark the session finalised (it may resume).
        if (pause) WriteManifestEnd("app_pause");
    }

    void OnApplicationQuit()
    {
        EmitOpenLifetimes("app_quit");
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
            _openLifetimesEmitted = false;   // a new Part freezes / ends its own lifetimes again at its End panel

            // Master switch: a Part with NPC capture disabled must not inherit lifetimes registered under an earlier, enabled Part.
            if (!NpcRecordingEnabled) { _npcByAgent.Clear(); _npcEventBuf.Clear(); }

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
            if (NpcRecordingEnabled)   // NPC-specific marker: not written while NPC capture is disabled
            WriteEventRow(e.type, CurrentPart, CurrentPhase, e.label, "", "", "", "", e.numValue, "", Vector3.zero, Vector3.zero);
            return;
        }

        if (e.type == CaptureConstants.EvtNpcWarmupEnd)
        {
            _npcWarmupActive = false;
            if (NpcRecordingEnabled)
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

                // phase_timeout comes from the STRUCTURED termination reason (Part3TerminationReason), never from the free-text notes: only
                // a search that actually ran and hit its limit qualifies. A found target, a skipped D2 (no_budget_remaining, never started)
                // and an unknown / empty reason do not. value_str keeps the legacy note and appends the reason so a D1 cap and an
                // exhausted total / remaining budget stay distinguishable. A skipped D2 is told apart from a D2 timeout by the absence of
                // this row (and of part3_d2_search_end).
                if (Part3TerminationReason.ShouldEmitTimeout(e.part3Event, e.terminationReason))
                {
                    bool already = (label == "D1") ? _d1TimeoutLogged : _d2TimeoutLogged;
                    if (!already)
                    {
                        if (label == "D1") _d1TimeoutLogged = true; else _d2TimeoutLogged = true;
                        WriteEventRow(CaptureConstants.EvtPhaseTimeout, CaptureConstants.Part3, CurrentPhase, label, "", "", "", "", e.numValue,
                            S(e.notes) + ";reason=" + S(e.terminationReason), Vector3.zero, Vector3.zero);
                    }
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

        // Safety net: any already-active NPC lifetime that is somehow not registered is registered NOW, synchronously, keeping its original
        // lifetime ID and spawn time (no new lifetime, no second npc_spawn for an already registered one).
        CensusActiveNpcs();

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
    /// the Part currently running - at the moment that Part's End Panel becomes visible to the participant,
    /// strictly before part_end. Sets _endPanelActive, which TrajectoryCaptureActive / GazeCaptureActive /
    /// NpcCaptureActive all require to be false - so TrajectoryLoop()/NpcLoop() stop producing rows on their
    /// very next tick, and GazeRecorder's own per-sample check (dataCapture.GazeCaptureActive) does the same
    /// for gaze.csv. No event row is written here beyond this in-memory flag flip: ExperimentManager writes the
    /// visible-moment row itself ("end_panel_visible", label "partX_end") right after this handler returns; the
    /// existing "panel_shown" row (same label) is raised just before the panel is requested and is unchanged.
    /// Event rows are not gated by _endPanelActive, so those rows and the Part 3 valid-interval rows are still written.
    /// </summary>
    void OnEndPanelShown()
    {
        _endPanelActive = true;
        // SpawnerManager freezes every NPC at this moment (no despawn follows): make every still-open lifetime explicit.
        EmitOpenLifetimes("end_panel_shown");
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

    // ---------------- NPC lifetimes (identity, spawn / despawn, attributes) ----------------
    // Registration is NOT gated by the trial / Part capture flags: every active lifetime gets its spawn rows and its attribute row, at the
    // moment it really began, even during warm-up, the Start-panel wait or Part 2. Only npc_state sampling has its own gate.

    /// <summary>Canonical lifetime ID of an NPC ("" when it has no open lifetime). Same value for npc_state, npc_events, events and gaze.</summary>
    public string GetSpawnInstanceId(PedestrianAgent pa) => pa != null ? pa.LifetimeId : "";

    /// <summary>Called by AoiClassifier when a gaze ray hit an NPC that has no open lifetime (the hit is recorded as unresolved, never with an invented ID).</summary>
    public void NoteUnresolvedNpcHit(PedestrianAgent pa)
    {
        _unresolvedNpcHits++;
        WarnOnce("npc_unresolved_gaze_hit", "a gaze ray hit an NPC with no open lifetime (" + (pa != null ? pa.AgentID : "?") +
            "); npc_spawn_instance_id left blank. Counted in the manifest (unresolved_npc_gaze_hits).");
    }

    public bool IsRegisteredBuildingName(string name)
        => !string.IsNullOrEmpty(name) && _buildingNames.Contains(name);

    void OnNpcLifecycle(NpcLifecycleEvent e)
    {
        // Second, independent enforcement of the master switch (the bus already discards events at raise time): events delivered by a replay
        // or raised in the instant the switch changed are dropped here, never recorded.
        if (!NpcRecordingEnabled) { _npcSuppressedAtSink++; return; }
        try { HandleNpcLifecycle(e); }
        catch (Exception ex) { WarnOnce("npc_lifecycle", "NPC lifecycle handler failed for '" + e.type + "': " + ex.Message); }
    }

    void HandleNpcLifecycle(NpcLifecycleEvent e)
    {
        switch (e.type)
        {
            case CaptureConstants.NpcEvtSpawn:
                RegisterLifetime(e.agent, false, false);
                return;

            case CaptureConstants.NpcEvtDespawn:
                HandleNpcDespawn(e);
                return;
        }

        // An event of an open lifetime that was never registered (should not happen): register it first, keeping its true spawn time.
        if (e.lifetimeId != null && e.lifetimeId.Length > 0 && e.agent != null && e.agent.HasOpenLifetime
            && e.type != CaptureConstants.NpcEvtPoolReturn && !_npcByAgent.ContainsKey(e.agent))
            RegisterLifetime(e.agent, true, false);

        AppendNpcEventLine(e.type, e.tAbs, e.tUnixMs, e.agentId, e.lifetimeId, e.lifetimeIndex, e.pos, e.yaw, e.hasDest, e.dest,
            "", "", e.value, e.itineraryIndex, e.targetId);

        // Lifecycle boundaries are flushed at once (a crash must not lose them); frequent events wait for the per-frame flush.
        if (e.type == CaptureConstants.NpcEvtItineraryComplete || e.type == CaptureConstants.NpcEvtPoolReturn) FlushNpcEvents();
    }

    // Registers one lifetime (idempotent). 'late' = it is being registered after its spawn event should have been processed. The spawn
    // time written everywhere is the TRUE spawn time of the lifetime (PedestrianAgent.LifetimeSpawnTAbs); the registration time is separate.
    NpcRec RegisterLifetime(PedestrianAgent pa, bool late, bool allowClosed)
    {
        if (pa == null || !NpcRecordingEnabled) return null;
        string lifetimeId = allowClosed ? pa.LastLifetimeId : pa.LifetimeId;
        if (string.IsNullOrEmpty(lifetimeId)) return null;

        if (_npcByAgent.TryGetValue(pa, out NpcRec existing))
        {
            if (existing.lifetimeId == lifetimeId) return existing;          // same lifetime: never a second spawn
            // A previous lifetime of this object was never closed here: close it explicitly instead of silently overwriting it.
            AppendNpcEventLine(CaptureConstants.NpcEvtSuperseded, CaptureClock.TAbs, CaptureClock.TUnixMs, existing.agentId, existing.lifetimeId,
                existing.lifetimeIndex, existing.tr != null ? existing.tr.position : Vector3.zero, 0f, false, default, "", "",
                "replaced_by=" + lifetimeId, -1, "");
            _npcByAgent.Remove(pa);
        }

        NpcRec rec = new NpcRec
        {
            pa = pa,
            tr = pa.transform,
            agent = pa.GetComponent<NavMeshAgent>() ?? pa.GetComponentInChildren<NavMeshAgent>(true),
            mt    = pa.GetComponent<MoveToTarget>() ?? pa.GetComponentInChildren<MoveToTarget>(true),
            agentId = pa.AgentID,
            lifetimeId = lifetimeId,
            lifetimeIndex = pa.LifetimeIndex
        };
        _npcByAgent[pa] = rec;
        _lifetimesRegistered++;

        double nowAbs = CaptureClock.TAbs;
        double spawnAbs = double.IsNaN(pa.LifetimeSpawnTAbs) ? nowAbs : pa.LifetimeSpawnTAbs;
        long spawnUnix = pa.LifetimeSpawnTUnixMs != 0 ? pa.LifetimeSpawnTUnixMs : CaptureClock.TUnixMs;

        Vector3 pos = rec.tr.position;
        float yaw = rec.tr.eulerAngles.y;
        bool agentReady = rec.agent != null && rec.agent.isActiveAndEnabled && rec.agent.isOnNavMesh;
        Vector3 dest = agentReady ? rec.agent.destination : pos;
        string speedSetting = rec.agent != null ? F(rec.agent.speed) : "";
        string corners = agentReady ? CornersPacked(SafePathCorners(rec.agent)) : "";

        rec.lastState = SafeState(pa); rec.hasLastState = true;
        rec.lastDest = dest;           rec.hasLastDest = true;

        string registration = late ? "late" : "spawn";
        string value = "lifetime_index=" + rec.lifetimeIndex + ";source=" + S(pa.SpawnSource) + ";registration=" + registration +
                       ";registered_t_abs=" + F(nowAbs);

        // events.csv row (immediate). t_abs is the true spawn time, not the registration time.
        WriteEventRow(CaptureConstants.EvtNpcSpawn, CurrentPart, CurrentPhase, "", "", "", rec.agentId, rec.lifetimeId, 0.0, value,
            Vector3.zero, Vector3.zero, spawnAbs, spawnUnix);
        AppendNpcEventLine(CaptureConstants.NpcEvtSpawn, spawnAbs, spawnUnix, rec.agentId, rec.lifetimeId, rec.lifetimeIndex, pos, yaw, true, dest,
            speedSetting, corners, value, pa.CurrentItineraryIndex, pa.CurrentTargetName);
        WriteLifetimeRow(rec, spawnAbs, spawnUnix, nowAbs, registration);
        FlushNpcEvents();

        if (late) WarnOnce("npc_registered_late", "an active NPC lifetime was registered after its spawn (" + rec.lifetimeId + "); original spawn time kept.");
        return rec;
    }

    void HandleNpcDespawn(NpcLifecycleEvent e)
    {
        if (!NpcRecordingEnabled) return;
        PedestrianAgent pa = e.agent;
        NpcRec rec = null;
        if (pa != null && !_npcByAgent.TryGetValue(pa, out rec))
            rec = RegisterLifetime(pa, true, true);   // never registered: write its (late) spawn rows before closing it

        string value = "lifetime_index=" + e.lifetimeIndex + ";" + e.value;
        string lifetimeId = rec != null ? rec.lifetimeId : e.lifetimeId;
        string agentId = rec != null ? rec.agentId : e.agentId;
        int index = rec != null ? rec.lifetimeIndex : e.lifetimeIndex;

        WriteEventRow(CaptureConstants.EvtNpcDespawn, CurrentPart, CurrentPhase, "", "", "", agentId, lifetimeId, 0.0, value,
            Vector3.zero, Vector3.zero, e.tAbs, e.tUnixMs);
        AppendNpcEventLine(CaptureConstants.NpcEvtDespawn, e.tAbs, e.tUnixMs, agentId, lifetimeId, index, e.pos, e.yaw, false, default, "", "", value, -1, "");
        FlushNpcEvents();

        if (pa != null) _npcByAgent.Remove(pa);
    }

    // Registers every active NPC lifetime that is not registered yet (see OnTrialStart). Idempotent: registered lifetimes are untouched.
    void CensusActiveNpcs()
    {
        if (_spawner == null || !NpcRecordingEnabled) return;
        IReadOnlyList<GameObject> active = _spawner.ActiveAgents;
        if (active == null) return;
        for (int i = 0; i < active.Count; i++)
        {
            GameObject go = active[i];
            if (go == null) continue;
            PedestrianAgent pa = go.GetComponent<PedestrianAgent>();
            if (pa == null || !pa.HasOpenLifetime) continue;
            if (_npcByAgent.TryGetValue(pa, out NpcRec r) && r.lifetimeId == pa.LifetimeId) continue;
            RegisterLifetime(pa, true, false);
        }
    }

    // Writes a 'still open' marker for every open lifetime once the NPC simulation is frozen / the session ends (no despawn follows then).
    void EmitOpenLifetimes(string reason)
    {
        if (!NpcRecordingEnabled) return;
        if (_openLifetimesEmitted) { FlushNpcEvents(); return; }
        _openLifetimesEmitted = true;
        double t = CaptureClock.TAbs; long tu = CaptureClock.TUnixMs;
        foreach (var kv in _npcByAgent)
        {
            NpcRec r = kv.Value;
            Vector3 pos = r.tr != null ? r.tr.position : Vector3.zero;
            AppendNpcEventLine(CaptureConstants.NpcEvtOpenAtEnd, t, tu, r.agentId, r.lifetimeId, r.lifetimeIndex, pos,
                r.tr != null ? r.tr.eulerAngles.y : 0f, false, default, "", "", "reason=" + reason, -1, "");
        }
        FlushNpcEvents();
    }

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

    // ---------------- NPC state loop (per-lifetime positions, nominal 10 Hz) ----------------

    IEnumerator NpcLoop()
    {
        bool wasSampling = false;
        while (true)
        {
            ApplyNpcSamplingSettings();
            // FIRST operation every tick: bail out unless NPC positions are being sampled (master switch + NPCs exist + not frozen).
            if (!NpcStateSamplingActive)
            {
                if (wasSampling) { _rate.ResetState(); wasSampling = false; }
                yield return new WaitForSecondsRealtime(_rate.BaseInterval);
                continue;
            }
            wasSampling = true;
            try { SampleNpcs(); }
            catch (Exception e) { WarnOnce("npc_tick", "npc sample failed: " + e.Message); }
            // The interval is re-read after every complete snapshot: base interval, stretched by the data budget when one is configured.
            yield return new WaitForSecondsRealtime(_rate.EffectiveInterval);
        }
    }

    string CapturePeriod() => _npcWarmupActive ? "warmup" : (_trialActive ? "trial" : "pre_trial");

    void SampleNpcs()
    {
        if (_npcState.Failed || !NpcStateSamplingActive) return;

        // Cheap safety net: the registry should always hold exactly the spawner's live population.
        if (_spawner != null && _spawner.ActiveAgents != null && _spawner.ActiveAgents.Count != _npcByAgent.Count) CensusActiveNpcs();

        if (_npcByAgent.Count == 0) return;

        double tAbs = CaptureClock.TAbs;
        long tUnix = CaptureClock.TUnixMs;
        int frame = Time.frameCount;
        Vector3 headPos = _centerEye != null ? _centerEye.position : Vector3.zero;
        string tAbsS = F(tAbs), tUnixS = tUnix.ToString(CultureInfo.InvariantCulture), frameS = frame.ToString(CultureInfo.InvariantCulture);
        string sessionS = S(SessionId), partS = S(CurrentPart), phaseS = S(CurrentPhase), periodS = CapturePeriod();

        _npcLines.Clear();

        foreach (var kv in _npcByAgent)
        {
            NpcRec rec = kv.Value;
            if (rec.tr == null || rec.pa == null) continue;
            if (!rec.pa.HasOpenLifetime || rec.pa.LifetimeId != rec.lifetimeId) continue;   // ended / replaced: never sampled under an old ID

            Vector3 pos = rec.tr.position;
            float yaw = rec.tr.eulerAngles.y;
            Quaternion rot = rec.tr.rotation;

            Vector3 vel = Vector3.zero, desired = Vector3.zero, navDest = pos;
            bool hasPath = false, pathPending = false, onMesh = false;
            float agentSpeed = 0f;
            if (rec.agent != null && rec.agent.isActiveAndEnabled && rec.agent.isOnNavMesh)
            {
                onMesh = true;
                vel = rec.agent.velocity;
                desired = rec.agent.desiredVelocity;
                navDest = rec.agent.destination;
                hasPath = rec.agent.hasPath;
                pathPending = rec.agent.pathPending;
                agentSpeed = rec.agent.speed;
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

            rec.sampleSeq++;

            _rf.Clear();
            _rf.Add(tAbsS);
            _rf.Add(tUnixS);
            _rf.Add(frameS);
            _rf.Add(S(rec.agentId));
            _rf.Add(rec.lifetimeId);
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
            // ---- appended (schema v2) ----
            _rf.Add(sessionS); _rf.Add(partS); _rf.Add(phaseS); _rf.Add(periodS);
            _rf.Add(rec.sampleSeq.ToString(CultureInfo.InvariantCulture));
            _rf.Add(rec.lifetimeIndex.ToString(CultureInfo.InvariantCulture));
            _rf.Add(F(rot.x)); _rf.Add(F(rot.y)); _rf.Add(F(rot.z)); _rf.Add(F(rot.w));
            _rf.Add(onMesh ? F(agentSpeed) : "");
            _rf.Add(rec.pa.CurrentItineraryIndex.ToString(CultureInfo.InvariantCulture));
            _rf.Add(rec.pa.ItineraryCount.ToString(CultureInfo.InvariantCulture));
            _rf.Add(S(rec.pa.CurrentTargetName));
            _rf.Add(onMesh ? "1" : "0");
            // ---- appended (schema v3): UncertainBehavior, read from the production component's state machine at this sample ----
            bool uActive = rec.pa.UncertainActive;
            _rf.Add(rec.pa.HasUncertainComponent ? (uActive ? "1" : "0") : "");
            _rf.Add(uActive ? rec.pa.UncertainStateLabel : "");
            _rf.Add(uActive ? rec.pa.UncertainLookPhaseLabel : "");
            _npcLines.Add(_npcState.Join(_rf));

            // Edge detection of NAV destination changes (arrival / dwell edges are now raised directly by PedestrianAgent at the real moment).
            if (!rec.hasLastState) { rec.lastState = state; rec.hasLastState = true; }
            else rec.lastState = state;

            if (!rec.hasLastDest) { rec.lastDest = navDest; rec.hasLastDest = true; }
            else if ((navDest - rec.lastDest).sqrMagnitude > 0.25f) // > ~0.5 m
            {
                // ONE observed-destination-change row (the former second row, npc_leg_start, carried identical data and is retired in schema v3).
                // value_str says why the NavMeshAgent destination moved; the exact-time itinerary events are npc_movement_start / npc_target_set.
                WriteNpcEventRow(CaptureConstants.NpcEvtDestChange, rec, pos, yaw, navDest, "", "",
                    "cause=" + DestChangeCause(rec, navDest) + ";jump_m=" + F((navDest - rec.lastDest).magnitude), tAbs, tUnix);
                rec.lastDest = navDest;
            }
        }

        if (_npcLines.Count > 0) _npcProduced = true;
        long before = _npcState.BytesWritten;
        bool firstWrite = before == 0;
        _npcState.WriteLines(_npcLines);   // one open/write/close per tick for the whole population (always the COMPLETE population)
        if (_npcLines.Count == 0 || _npcState.Failed) return;

        long rowBytes = _npcState.BytesWritten - before - (firstWrite ? _npcState.HeaderBytes : 0);
        _rate.OnSnapshot(tAbs, _npcLines.Count, rowBytes, _npcByAgent.Count);
        UpdateRateReadout();
    }

    void UpdateRateReadout()
    {
        readoutEffectiveIntervalSeconds = _rate.EffectiveInterval;
        readoutObservedMBPerMinute = (float)_rate.ObservedMBPerMinute;
        readoutActiveNpcs = _rate.LastActiveNpcs;
        readoutBudgetStatus = NpcStateBudgetStatus;

        // Honest budget warning: at most once when it first becomes unachievable, then at most once a minute while it stays so.
        if (_rate.BudgetEnabled && _rate.Unachievable && _rate.ObservedMBPerMinute > _rate.BudgetMBPerMinute)
        {
            double now = CaptureClock.TAbs;
            if (!_rateWarnedUnachievable || now - _rateLastWarnT >= 60.0)
            {
                _rateWarnedUnachievable = true; _rateLastWarnT = now;
                Debug.LogWarning("[DataCapture] npc_state data budget " + F(_rate.BudgetMBPerMinute) + " MB/min cannot be met with " + _rate.LastActiveNpcs +
                    " active NPCs even at the maximum interval " + F(_rate.MaxInterval) + " s; observed " + F(_rate.ObservedMBPerMinute) + " MB/min. " +
                    "Raise npcStateMaxIntervalSeconds or the budget. (Recorded in session_manifest.json.)");
            }
        }
        else if (!_rate.Unachievable) _rateWarnedUnachievable = false;
    }

    // Why the NavMeshAgent destination moved: an UncertainBehavior detour, the current itinerary target (leg change / re-issue), or something else
    // (entrance placement, wander re-targeting that is not the current itinerary entry).
    static string DestChangeCause(NpcRec rec, Vector3 navDest)
    {
        PedestrianAgent pa = rec.pa;
        if (pa.UncertainActive && pa.UncertainStateLabel == "detour") return "detour";
        IReadOnlyList<Transform> it = pa.ItineraryTransformsView;
        int i = pa.CurrentItineraryIndex;
        if (it != null && i >= 0 && i < it.Count && it[i] != null)
        {
            Vector3 d = it[i].position - navDest; d.y = 0f;
            if (d.sqrMagnitude <= 2.25f) return "itinerary_target";   // within 1.5 m: NavMeshAgent snaps its destination onto the NavMesh
        }
        return "other";
    }

    void WriteNpcEventRow(string type, NpcRec rec, Vector3 pos, float yaw, Vector3 dest,
        string speedSetting, string cornersPacked, string valueStr, double tAbs, long tUnix)
    {
        AppendNpcEventLine(type, tAbs, tUnix, rec.agentId, rec.lifetimeId, rec.lifetimeIndex, pos, yaw, true, dest, speedSetting, cornersPacked, valueStr,
            rec.pa != null ? rec.pa.CurrentItineraryIndex : -1, rec.pa != null ? rec.pa.CurrentTargetName : "");
    }

    // Formats one npc_events.csv row with an EXPLICIT timestamp (the moment the transition happened) and queues it. FlushNpcEvents() writes the batch.
    void AppendNpcEventLine(string type, double tAbs, long tUnix, string agentId, string lifetimeId, int lifetimeIndex, Vector3 pos, float yaw,
        bool hasDest, Vector3 dest, string speedSetting, string cornersPacked, string valueStr, int itineraryIndex, string targetId)
    {
        if (_npcEvents == null || _npcEvents.Failed || !NpcRecordingEnabled) return;
        var f = new List<string>(21);
        f.Add(F(tAbs));
        f.Add(tUnix.ToString(CultureInfo.InvariantCulture));
        f.Add(S(agentId));
        f.Add(S(lifetimeId));
        f.Add(S(type));
        f.Add(F(pos.x)); f.Add(F(pos.y)); f.Add(F(pos.z));
        f.Add(F(yaw));
        f.Add(hasDest ? F(dest.x) : ""); f.Add(hasDest ? F(dest.y) : ""); f.Add(hasDest ? F(dest.z) : "");
        f.Add(speedSetting ?? "");
        f.Add(cornersPacked ?? "");
        f.Add(S(valueStr));
        // ---- appended (schema v2) ----
        f.Add(S(SessionId)); f.Add(S(CurrentPart)); f.Add(S(CurrentPhase));
        f.Add(lifetimeIndex > 0 ? lifetimeIndex.ToString(CultureInfo.InvariantCulture) : "");
        f.Add(itineraryIndex >= 0 ? itineraryIndex.ToString(CultureInfo.InvariantCulture) : "");
        f.Add(S(targetId));
        _npcEventBuf.Add(_npcEvents.Join(f));
        if (_npcEventBuf.Count >= NpcEventBufMax) FlushNpcEvents();
    }

    // Writes the queued npc_events lines in one open/write/close. Called every frame (LateUpdate) and at every lifecycle checkpoint.
    void FlushNpcEvents()
    {
        if (_npcEventBuf.Count == 0 || _npcEvents == null) return;
        _npcEvents.WriteLines(_npcEventBuf);
        _npcEventBuf.Clear();
    }

    void LateUpdate() { FlushNpcEvents(); }

    // One row per lifetime, written at its spawn boundary. Termination is in npc_events.csv (npc_itinerary_complete / npc_despawn).
    void WriteLifetimeRow(NpcRec rec, double spawnAbs, long spawnUnix, double registeredAbs, string registration)
    {
        if (_npcLifetimes == null || _npcLifetimes.Failed || !NpcRecordingEnabled) return;
        PedestrianAgent pa = rec.pa;
        IReadOnlyList<Transform> it = pa.ItineraryTransformsView;
        int n = it != null ? it.Count : 0;
        string entrance = n > 0 && it[0] != null ? it[0].name : "";
        string first = n > 1 && it[1] != null ? it[1].name : "";
        string final = n > 0 && it[n - 1] != null ? it[n - 1].name : "";
        var ids = new StringBuilder(64);
        for (int i = 0; i < n; i++) { if (i > 0) ids.Append('|'); ids.Append(it[i] != null ? S(it[i].name) : ""); }

        NavMeshAgent nav = rec.agent;
        Vector3 sp = rec.tr.position;
        bool wander = pa.MovementMode == NpcMovementMode.Part2WanderingItinerary;
        string prefab = pa.gameObject.name;
        int br = prefab.IndexOf(" [", StringComparison.Ordinal);
        string prefabName = br > 0 ? prefab.Substring(0, br) : prefab;
        if (prefabName.EndsWith("(Clone)", StringComparison.Ordinal)) prefabName = prefabName.Substring(0, prefabName.Length - 7);

        var f = new List<string>(42);
        f.Add(S(SessionId)); f.Add(rec.lifetimeId); f.Add(S(rec.agentId)); f.Add(rec.lifetimeIndex.ToString(CultureInfo.InvariantCulture));
        f.Add(S(CurrentPart)); f.Add(S(CurrentPhase)); f.Add(CapturePeriod());
        f.Add(S(prefabName)); f.Add(S(pa.gameObject.name));
        f.Add(double.IsNaN(pa.ObjectCreatedTAbs) ? "" : F(pa.ObjectCreatedTAbs)); f.Add(S(pa.ObjectCreatedKind));
        f.Add(double.IsNaN(pa.PoolAcquiredTAbs) ? "" : F(pa.PoolAcquiredTAbs));
        f.Add(F(spawnAbs)); f.Add(spawnUnix.ToString(CultureInfo.InvariantCulture)); f.Add(F(registeredAbs)); f.Add(registration); f.Add(S(pa.SpawnSource));
        f.Add(F(sp.x)); f.Add(F(sp.y)); f.Add(F(sp.z));
        f.Add(pa.MovementMode.ToString()); f.Add(n.ToString(CultureInfo.InvariantCulture));
        f.Add(S(entrance)); f.Add(S(first)); f.Add(S(final)); f.Add(ids.ToString());
        bool profiled = !string.IsNullOrEmpty(pa.ProfileName);   // AgentFactory applied an AgentProfile to this NPC; otherwise these three are unknown, not 0
        f.Add(profiled ? F(pa.ConfiguredSpeed) : ""); f.Add(S(pa.ProfileName)); f.Add(profiled ? F(pa.ProfileBaseSpeed) : ""); f.Add(profiled ? F(pa.SpeedMultiplier) : "");
        f.Add(pa.UncertainBehaviorEnabled ? "1" : "0");
        f.Add(pa.LifetimeProfile != null && pa.LifetimeProfile.appearance != null ? pa.LifetimeProfile.appearance.gender.ToString() : "");   // realised for THIS lifetime
        f.Add(wander ? "" : F(pa.DwellSeconds));
        f.Add(wander ? F(pa.WanderLookAheadDistance) : "");
        f.Add(nav != null ? F(nav.radius) : ""); f.Add(nav != null ? F(nav.height) : "");
        f.Add(nav != null ? F(nav.acceleration) : ""); f.Add(nav != null ? F(nav.angularSpeed) : "");
        f.Add(nav != null ? nav.avoidancePriority.ToString(CultureInfo.InvariantCulture) : "");

        // ---- appended (schema v3): profile content in force + REALISED appearance, frozen when this lifetime began ----
        NpcLifetimeProfile lp = pa.LifetimeProfile ?? NpcLifetimeProfile.None;
        f.Add(lp.profileContentHash);
        f.Add(lp.profileContentHash.Length > 0 ? F(lp.femaleProbability) : "");
        f.Add(lp.profileContentHash.Length > 0 ? (lp.forceUncertain ? "1" : "0") : "");
        f.Add(lp.profileContentHash.Length > 0 ? F(lp.uncertainProbability) : "");
        f.Add(S(lp.uncertainConfig));
        AppliedAppearance ap = lp.appearance;
        f.Add(ap != null ? ap.LookHash() : "");
        f.Add(ap != null ? F(ap.appliedTAbs) : "");
        AddPart(f, ap != null ? ap.body : null);
        AddPart(f, ap != null ? ap.hair : null);
        AddPart(f, ap != null ? ap.top : null);
        AddPart(f, ap != null ? ap.bottom : null);
        AddPart(f, ap != null ? ap.shoes : null);
        AddPart(f, ap != null ? ap.accessories : null);
        _npcLifetimes.WriteLine(_npcLifetimes.Join(f));
        WriteProfileRegistryRow(lp);
    }

    // option / mesh / materials of one body part; all three blank when the part has no renderer on this prefab (nothing to record).
    static void AddPart(List<string> f, AppliedPartRecord p)
    {
        if (p == null || !p.hasRenderer) { f.Add(""); f.Add(""); f.Add(""); return; }
        f.Add(p.OptionText); f.Add(S(p.MeshName)); f.Add(S(p.MaterialNames));
    }

    // First time a profile content hash is seen in this session: store its full description once (npc_profiles.csv).
    void WriteProfileRegistryRow(NpcLifetimeProfile lp)
    {
        if (lp == null || lp.profileContentHash.Length == 0 || _npcProfiles == null || _npcProfiles.Failed || !NpcRecordingEnabled) return;
        if (!_profilesWritten.Add(lp.profileContentHash)) return;
        var f = new List<string>(5)
        {
            S(SessionId), lp.profileContentHash, S(lp.profileName), F(CaptureClock.TAbs), AgentProfileSnapshot.JsonFor(lp.profileContentHash)
        };
        _npcProfiles.WriteLine(_npcProfiles.Join(f));
    }

    // ---------------- events.csv writer ----------------

    void WriteEventRow(string type, string part, string phase, string label,
        string buildingId, string targetId, string npcId, string npcSpawnInstanceId,
        double valueNum, string valueStr, Vector3 fromPos, Vector3 toPos,
        double tAbsOverride = double.NaN, long tUnixOverride = 0)
    {
        try
        {
            if (_events.Failed) return;
            // NPC-specific rows (spawn / despawn / warm-up markers) are never written while the master switch is off, whatever the caller.
            if ((type == CaptureConstants.EvtNpcSpawn || type == CaptureConstants.EvtNpcDespawn || type == CaptureConstants.EvtNpcWarmupStart ||
                 type == CaptureConstants.EvtNpcWarmupEnd) && !NpcRecordingEnabled) return;
            _rf.Clear();
            // Event time: the explicit time of the transition when the caller has one (e.g. an NPC spawn), otherwise now.
            _rf.Add(F(double.IsNaN(tAbsOverride) ? CaptureClock.TAbs : tAbsOverride));
            _rf.Add((tUnixOverride != 0 ? tUnixOverride : CaptureClock.TUnixMs).ToString(CultureInfo.InvariantCulture));
            _rf.Add(S(type)); _rf.Add(S(part)); _rf.Add(S(phase)); _rf.Add(S(label));
            _rf.Add(S(buildingId)); _rf.Add(S(targetId)); _rf.Add(S(npcId)); _rf.Add(S(npcSpawnInstanceId));
            bool hasPos = fromPos != Vector3.zero || toPos != Vector3.zero;
            _rf.Add(hasPos ? F(fromPos.x) : ""); _rf.Add(hasPos ? F(fromPos.y) : ""); _rf.Add(hasPos ? F(fromPos.z) : "");
            _rf.Add(hasPos ? F(toPos.x)   : ""); _rf.Add(hasPos ? F(toPos.y)   : ""); _rf.Add(hasPos ? F(toPos.z)   : "");
            _rf.Add(valueNum != 0.0 ? F(valueNum) : "");
            _rf.Add(S(valueStr));
            _rf.Add(S(SessionId));
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

        // ---- schema v2: identity scheme and NPC capture mode (new; older sessions do not have these keys) ----
        public int schema_version;
        public string run_guid = "";
        public string session_key_scheme = "";
        public string npc_id_scheme = "";
        public string npc_state_capture_mode = "";
        public bool npc_lifetime_records_always_written = true;
        public int npc_lifetimes_registered;
        public int unresolved_npc_gaze_hits;
        public int npc_lifecycle_events_dropped_before_attach;
        // false = that stream hit an I/O error and stopped writing; the data set is then INCOMPLETE for that stream.
        public bool npc_state_ok = true, npc_events_ok = true, npc_lifetimes_ok = true;

        // ---- NPC master switch + state-sampling controls (settings are written at session start, statistics at every manifest update) ----
        public string npc_master_switch_semantics = "";
        public string npc_state_budget_scope = "";
        public float npc_state_base_interval_seconds, npc_state_max_interval_seconds, npc_state_data_budget_mb_per_minute;
        // true when npcCaptureEnabled is true for the Part that is running (or selected, before any Part runs) at the time of the update
        public bool npc_capture_effective_enabled;
        public int npc_events_suppressed_by_master_switch;
        public int npc_state_snapshots;
        public int npc_state_rows;
        public long npc_state_bytes_written;
        public int npc_state_max_active_npcs, npc_state_last_active_npcs;
        public float npc_state_effective_interval_last_seconds, npc_state_effective_interval_min_seconds, npc_state_effective_interval_max_seconds;
        public float npc_state_actual_hz_mean;
        public float npc_state_observed_mb_per_minute_last, npc_state_observed_mb_per_minute_peak;
        public float npc_state_observed_mb_per_minute_session_mean;
        public string npc_state_budget_status = "unlimited";
        public int npc_state_throttled_snapshots, npc_state_budget_exceeded_snapshots;
        public bool npc_state_budget_unachievable_seen;
    }

    void BuildManifestPass1()
    {
        _manifest = new Manifest
        {
            session_id      = SessionId,
            schema_version  = CaptureConstants.SchemaVersion,
            run_guid        = _runGuid,
            session_key_scheme = CaptureConstants.SessionKeyScheme,
            npc_id_scheme   = CaptureConstants.NpcIdScheme,
            npc_state_capture_mode = CaptureConstants.NpcCaptureModeText,
            npc_master_switch_semantics = CaptureConstants.NpcMasterSwitchText,
            npc_state_budget_scope = CaptureConstants.NpcBudgetScopeText,
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
            sample_rate_npc_hz = 1f / _rate.BaseInterval,
            npc_state_base_interval_seconds = _rate.BaseInterval,
            npc_state_max_interval_seconds = _rate.MaxInterval,
            npc_state_data_budget_mb_per_minute = (float)_rate.BudgetMBPerMinute,

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
        _manifest.npc_capture   = _npcState != null && !_npcState.Failed && (_npcProduced || _lifetimesRegistered > 0);
        _manifest.npc_lifetimes_registered = _lifetimesRegistered;
        _manifest.unresolved_npc_gaze_hits = _unresolvedNpcHits;
        _manifest.npc_lifecycle_events_dropped_before_attach = NpcLifecycleBus.DroppedPending;
        FillNpcRateManifest();
        _manifest.npc_state_ok = _npcState != null && !_npcState.Failed;
        _manifest.npc_events_ok = _npcEvents != null && !_npcEvents.Failed;
        _manifest.npc_lifetimes_ok = _npcLifetimes != null && !_npcLifetimes.Failed;
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
        AddIfExists("npc_lifetimes.csv");
        AddIfExists("npc_profiles.csv");
        _manifest.file_list = _fileList.ToArray();

        WriteManifest();
    }

    void FillNpcRateManifest()
    {
        _manifest.npc_capture_effective_enabled = NpcRecordingEnabled;
        _manifest.npc_events_suppressed_by_master_switch = NpcEventsSuppressedByMasterSwitch;
        _manifest.npc_state_base_interval_seconds = _rate.BaseInterval;
        _manifest.npc_state_max_interval_seconds = _rate.MaxInterval;
        _manifest.npc_state_data_budget_mb_per_minute = (float)_rate.BudgetMBPerMinute;
        _manifest.npc_state_snapshots = (int)_rate.Snapshots;
        _manifest.npc_state_rows = (int)_rate.Rows;
        _manifest.npc_state_bytes_written = _rate.Bytes;
        _manifest.npc_state_max_active_npcs = _rate.MaxActiveNpcs;
        _manifest.npc_state_last_active_npcs = _rate.LastActiveNpcs;
        _manifest.npc_state_effective_interval_last_seconds = _rate.Snapshots > 0 ? _rate.EffectiveInterval : 0f;
        _manifest.npc_state_effective_interval_min_seconds = float.IsNaN(_rate.MinEffectiveInterval) ? 0f : _rate.MinEffectiveInterval;
        _manifest.npc_state_effective_interval_max_seconds = float.IsNaN(_rate.MaxEffectiveIntervalSeen) ? 0f : _rate.MaxEffectiveIntervalSeen;
        double hz = _rate.ActualHz;
        _manifest.npc_state_actual_hz_mean = double.IsNaN(hz) ? 0f : (float)hz;
        _manifest.npc_state_observed_mb_per_minute_last = (float)_rate.ObservedMBPerMinute;
        _manifest.npc_state_observed_mb_per_minute_peak = (float)_rate.PeakObservedMBPerMinute;
        double span = _rate.LastSnapshotT - _rate.FirstSnapshotT + _rate.EffectiveInterval;
        _manifest.npc_state_observed_mb_per_minute_session_mean = _rate.Snapshots > 0 && span > 0.0 ? (float)(_rate.Bytes / span * 60.0 / 1e6) : 0f;
        _manifest.npc_state_budget_status = NpcStateBudgetStatus;
        _manifest.npc_state_throttled_snapshots = (int)_rate.ThrottledSnapshots;
        _manifest.npc_state_budget_exceeded_snapshots = (int)_rate.ExceededSnapshots;
        if (_rate.Unachievable) _unachievableSeen = true;
        _manifest.npc_state_budget_unachievable_seen = _unachievableSeen;
    }
    bool _unachievableSeen;

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
