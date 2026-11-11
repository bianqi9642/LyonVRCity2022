// GazeRecorder.cs
//
// Eye-tracking data recorder for Meta Quest Pro.
//
// Revision 2 (data-capture Architecture C) changes - all ADDITIVE, existing columns unchanged:
//  - Writes into the shared session folder as "gaze.csv" ({session_id}/gaze.csv) when a
//    DataCaptureManager is present. File + header are created LAZILY on the first written row.
//  - Per-sample scope gate: rows are written only while DataCaptureManager.GazeCaptureActive is true - which
//    folds together the current Part's PartCaptureConfig.gazeCaptureEnabled (Inspector-configurable per Part
//    on ExperimentManager, Requirement 1B), an open trial (OnTrialStart..OnTrialEnd), and the End-Panel hard
//    stop. Previously gated on ParticipantCaptureActive (a hardcoded Part2/Part3 rule with no per-Part
//    configurability and no End-Panel awareness); GazeCaptureActive supersedes it for this purpose. With the
//    shipped default Part1Config (gazeCaptureEnabled=false), a Part 1 session still produces NO gaze.csv.
//  - The ROI raycast now runs whenever enableROIRaycast is true - it is NO LONGER gated on
//    combined_valid. When eye tracking is invalid the ray is cast from the head-forward fallback and
//    gaze_ray_source is recorded as "head_fallback". combined_valid remains a TRUE eye-tracking
//    validity indicator and is never set to 1 for a fallback ray.
//  - New appended columns (after roi_hit_distance): t_abs, session_id, frame_index, gaze_ray_source,
//    ray_did_hit, hit_point_x/y/z, hit_collider_name, hit_object_name, hit_instance_id, hit_layer,
//    hit_tag, hit_semantic_class, hit_building_id, hit_npc_id, hit_npc_spawn_instance_id,
//    hit_is_interactable  (via AoiClassifier).
//  - Legacy columns roi_hit_name / roi_hit_distance keep their EXACT previous semantics: populated only
//    when combined_valid == 1 and the ray hit something.
//  - Android eye-tracking permission is requested via OVRPermissionsRequester (Meta XR SDK 201).
//  - Timestamps (time_since_start_s / t_abs / unix_time_ms) are now sourced from CaptureClock.TAbs /
//    CaptureClock.TUnixMs, the single authoritative timebase for the data-capture layer, instead of
//    computing Time.realtimeSinceStartupAsDouble / DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
//    independently. Values are numerically identical; only the call site moved.
//
// Revision 3 (eye-tracking acquisition, schema v8) - ADDITIVE, existing columns unchanged:
//  - The Android permission request is NO LONGER made here (it depended on Awake order and was skipped when eyeTrackingSupported was still false). The single owner of
//    start / permission / retry is EyeTrackingLifecycleController (auto-created in Awake when the scene has none); this recorder stays the only reader of gaze data.
//  - Appended gaze.csv columns: eye_tracking_state, left_provider_valid, right_provider_valid. The provider flags come from the SDK's per-eye IsValid and expose the
//    stale OVREyeGaze.Confidence (the SDK leaves Confidence untouched on invalid samples). A per-eye sample is valid only if confidence >= minConfidenceForValid AND (when
//    the provider probe is available and requireProviderValidity) the provider flagged that eye valid. Head-forward fallback rows stay identifiable (ray_is_eye_gaze=0).
//  - Samples are scheduled on an absolute deadline grid (SampleScheduler) instead of "wait interval after each sample", which drifted below the nominal rate.
//
// Revision 4 (gaze origin, schema v10) - ADDITIVE: each eye's world-space origin is validated against the rig's own eye anchor (EyeGazeOrigin). The SDK's WorldSpace conversion mixes
//  two tracking origins when the rig is EyeLevel and the eye pose is not (observed: constant +1.361 m vertical offset); an implausible provider origin is replaced by the rig eye anchor
//  and labelled in the appended column gaze_origin_source. Direction, per-eye validity, confidence and the head-forward fallback are unchanged. See docs/gaze_origin_verification.md.
//
// Revision 5 (schema v17, NOT additive - an explicitly requested reorganisation): gaze.csv is the 61-column ANALYSIS table (PrimaryColumns); everything that exists to QA or reproduce it is written to gaze_diagnostics.csv
//  (DiagnosticColumns, 85 columns, one row per gaze row, keys session_id + frame_index + t_abs). Both rows are built from the SAME values of one sample. Removed / moved / derivable columns and the reasons:
//  docs/gaze_schema_v17_dictionary.md. The face signal is read LIVE per row from one GetFaceState2 state (started, source, validity, provider time, weights); the readiness gate (BeginFaceTrackingStartup /
//  AwaitFaceTrackingReady) starts face tracking before the first row and records nothing. No gaze origin / direction / ViewFrame / sampling-rate logic changed. See docs/schema_v17_changes.md.
//
// Revision 6 (Canvas UI gaze detection, gaze_ui_hits.csv schema v1) - ADDITIVE: gaze.csv (v17, 61 columns) and gaze_diagnostics.csv (85 columns) and the Physics.Raycast above are UNCHANGED. With enableUiGaze the
//  same combined gaze ray is also tested against the registered Canvases (GazeUiRaycaster: World Space and Screen Space - Camera; Overlay is unsupported) and ONE companion row per gaze row is written to
//  gaze_ui_hits.csv (UiHitColumns; keys t_abs + frame_index + session_id): the raw UI hit, the raw world hit facts, and the resolved primary target (GazeTargetResolver). The UI test runs only for rows
//  that carry a real eye-gaze ray (combined_valid = 1) unless uiRaycastOnHeadFallback is set. See docs/gaze_ui_detection.md.
//
// All comments in English per project convention.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public class GazeRecorder : MonoBehaviour
{
    [Header("Eye Tracking Sources (Meta Quest Pro)")]
    [Tooltip("OVREyeGaze component configured with Eye = Left. Must be an independent object (NOT the OVRCameraRig Left/Right/CenterEyeAnchor itself: the rig rewrites that transform every frame), ideally TrackingMode = WorldSpace under an identity-rotation parent.")]
    public OVREyeGaze leftEyeGaze;

    [Tooltip("OVREyeGaze component configured with Eye = Right. Same hosting rule as the left eye.")]
    public OVREyeGaze rightEyeGaze;

    [Tooltip("Minimum OVREyeGaze.Confidence (0..1) for a single eye's sample to be treated as valid. " +
             "Meta's SDK does not expose a separate boolean validity flag on OVREyeGaze - confidence " +
             "is the documented signal to threshold on.")]
    [Range(0f, 1f)]
    public float minConfidenceForValid = 0.5f;

    [Header("Acquisition lifecycle")]
    [Tooltip("Optional: the EyeTrackingLifecycleController that owns start/permission/retry. If empty, the scene's one is found, or one is created on this GameObject in Awake.")]
    public EyeTrackingLifecycleController lifecycleController;

    [Tooltip("If true (default), a per-eye sample also needs the provider's own IsValid flag whenever the lifecycle's provider probe is available. Guards against a stale " +
             "OVREyeGaze.Confidence being read as a live eye. Has no effect when no probe is available (Editor without runtime, tests) - the legacy confidence rule then applies.")]
    public bool requireProviderValidity = true;

    [Tooltip("If true (default), eye samples are recorded as invalid (head_fallback) while an OVREyeGaze sits directly on an OVRCameraRig eye anchor: the rig rewrites that " +
             "transform every frame, so its pose would be the eye-camera pose, not the gaze pose.")]
    public bool blockEyeSamplesOnHostConflict = true;

    [Header("Blink (Quest Pro FACE tracking - not part of eye tracking)")]
    [Tooltip("Optional OVRFaceExpressions (Meta face tracking). The eye-gaze API has NO eye-openness signal; the only blink-capable signal in the installed SDK is FaceExpression.EyesClosedL/R from face tracking, " +
             "which needs OculusProjectConfig.faceTrackingSupport, the FACE_TRACKING permission and this component. Left empty (the shipped state) the blink capability is reported 'not_configured' in the manifest and no " +
             "blink is ever recorded or inferred. Enabling face tracking records facial data and is a data-protection decision for the study.")]
    public OVRFaceExpressions faceExpressions;

    [Tooltip("If true (default), the recorder adds an OVRFaceExpressions component to its own GameObject at run time when none exists and the runtime reports face tracking support. " +
             "Reason: OculusProjectConfig.faceTrackingSupport only writes the manifest / permission; face tracking is started solely by an enabled OVRFaceExpressions. Without one, eyes_closed_* " +
             "can never be measured. No scene is edited. Set false to keep face tracking off (the blink capability then stays not_configured, nothing is recorded). " +
             "Enabling it makes the runtime process facial data about the participant - a data-protection decision for the study.")]
    public bool autoCreateFaceExpressions = true;

    [Header("Diagnostics stream (schema v17)")]
    [Tooltip("Write gaze_diagnostics.csv next to gaze.csv: provider pipeline, validity / confidence, legacy head anchor, view anchors, raw face weights, face source / validity and the uncalibrated blink state, one row per gaze row. Off = the main gaze.csv alone.")]
    public bool recordDiagnostics = true;

    [Tooltip("Both eyes' closure weight (0..1) at or above this starts a blink. UNCALIBRATED default - calibrate on a Quest Pro before analysing blink durations.")]
    [Range(0.05f, 1f)] public float blinkCloseThreshold = 0.7f;
    [Tooltip("Both eyes' closure weight at or below this ends the blink (hysteresis: must be below the close threshold).")]
    [Range(0f, 0.95f)] public float blinkOpenThreshold = 0.5f;

    /// <summary>Blink state machine; counts are reported in the manifest.</summary>
    public BlinkDetector Blink { get; private set; }
    /// <summary>unsupported | unavailable | not_configured | available - decided from observed runtime facts (BlinkCapability), re-evaluated about once per second.</summary>
    public string BlinkStatus { get; private set; } = "unavailable";
    public string BlinkReason { get; private set; } = "not_evaluated_yet";
    public BlinkFacts BlinkFactsLast { get; private set; }
    double _nextBlinkCapabilityT; bool _faceSearched;
    /// <summary>True when this recorder created the OVRFaceExpressions component itself (schema v16).</summary>
    public bool FaceAutoCreated { get; private set; }
    /// <summary>Number of bounded re-start attempts of an auto-created component that disabled itself (permission pending / StartFaceTracking2 failed).</summary>
    public int FaceStartAttempts { get; private set; }
    public bool FacePermissionRequested { get; private set; }
    /// <summary>Current / not-current bookkeeping of the face signal (one observation per gaze row).</summary>
    public FaceContinuity FaceSignal { get; } = new FaceContinuity();
    /// <summary>Unity frame of the most recent gaze row built (diagnostic alignment of face reads and gaze rows).</summary>
    public int LastRowFrame { get; private set; }
    public long RowsFaceEyeFollowing { get; private set; }
    public long RowsFaceAudioOnly { get; private set; }
    /// <summary>Latest row state (FaceStateNames) and the 1 Hz-probed data source; used by the manifest and the diagnostic tool.</summary>
    public string FaceStateLast { get; private set; } = FaceStateNames.NoRuntime;
    const int MaxFaceStartAttempts = 12; const double FaceRetryIntervalS = 5.0;
    double _nextFaceRetryT; OVRPlugin.FaceState _faceProbeState;
    /// <summary>Evidence about OVRPlugin's vs Unity XR's tracking origin (G1): sampled about every 2 s, never per sample. Written to manifest eye_tracking.tracking_space.</summary>
    public EyeGazeOrigin.TrackingSpaceStats TrackingSpace { get; } = new EyeGazeOrigin.TrackingSpaceStats();
    double _nextTrackingProbeT;
    /// <summary>Rendered-view resolution + readings (schema v13). Statistics below are written to manifest eye_tracking.view_frame.</summary>
    public ViewFrameProbe ViewProbe { get; } = new ViewFrameProbe();
    public long ViewSamples { get; private set; }
    public long GroundSamples { get; private set; }
    public long GroundUnavailableSamples { get; private set; }
    public readonly EyeGazeOrigin.Welford3 ViewMinusCentre = new EyeGazeOrigin.Welford3();
    /// <summary>Provider minus the RAW OVR rig anchor (rig frame): the v10-v12 diagnostic, kept so the Camera Floor Offset stays visible in the data.</summary>
    public readonly EyeGazeOrigin.Welford3 ProviderMinusRigAnchor = new EyeGazeOrigin.Welford3();
    public double HeightMin = double.NaN, HeightMax = double.NaN, HeightSum, HeightFirst = double.NaN;   // hmd height above ground (first recorded = the initial standing height)
    [NonSerialized] public Func<XrOriginFacts> testXrFacts;   // test seam (never set in production)
    double _nextXrFactsT; public XrOriginFacts XrFactsLast { get; private set; }

    // ---- per-eye pipeline evidence (schema v14): raw provider pose, head-space pose, coherence with the rendered camera, stereo / IPD facts ----
    [Header("Provider pipeline evidence (schema v14)")]
    [Tooltip("Record the raw OVRPlugin eye-gaze poses (tracking space), their head-space conversion, the provider timestamp, stereo / IPD facts and the composed-vs-WorldSpace coherence check. Reads OVRPlugin.GetEyeGazesState once per row; does not change the recorded gaze ray.")]
    public bool recordProviderPipeline = true;
    public EyePipelineManifest Pipeline { get; } = new EyePipelineManifest();
    /// <summary>Unity frame in which OVRCameraRig last raised UpdatedAnchors (-1 = never seen).</summary>
    public int AnchorsUpdatedFrame { get; private set; } = -1;
    OVRPlugin.EyeGazesState _rawBuf; double _prevProviderTime = double.NaN; OVRCameraRig _subRig; double _nextSubT, _nextIpdT; float _ipdCache; bool _ipdOk;
    [NonSerialized] public Func<RawGazeState> testRawGaze;                          // test seams (never set in production)
    [NonSerialized] public Func<(Vector3 pos, Quaternion rot)> testHeadReference;   // head node pose in tracking space (what the SDK's head-space conversion uses)
    [NonSerialized] public Func<float> testRuntimeIpd;
    public string OvrTrackingOriginType { get; private set; } = "";
    [NonSerialized] public Func<EyeGazeOrigin.TrackingSpaceFacts> testTrackingFacts;   // test seam (never set in production)
    Transform _rigTrackingSpace;
    /// <summary>Test seams (never set in production): replace the runtime facts / the two closure weights.</summary>
    [NonSerialized] public Func<BlinkFacts> testBlinkFacts;
    [NonSerialized] public Func<(float? left, float? right)> testEyeClosure;
    [NonSerialized] public Func<FaceReading> testFaceReading;   // test seam (never set in production): a full live reading incl. validity, look-down and eye-following flags

    [Header("Gaze origin (world space)")]
    [Tooltip("Rig LeftEyeAnchor (world position of the left eye as the OVRCameraRig renders it). Auto-filled from the scene's OVRCameraRig when empty. Used ONLY to validate the provider's left-eye origin and, when that origin is not plausible, as that eye's origin (see EyeGazeOrigin).")]
    public Transform leftEyeAnchor;

    [Tooltip("Rig RightEyeAnchor. Same role as the left anchor.")]
    public Transform rightEyeAnchor;

    [Tooltip("If true (default), an OVREyeGaze origin farther than 'maxProviderOriginDeviationMeters' from its rig eye anchor is replaced by that anchor's world position and the row is labelled origin source 'eye_anchor'. " +
             "Guards against a tracking-origin mismatch between the eye pose and the head pose (observed: a constant +1.361 m vertical offset). No height constant is applied anywhere.")]
    public bool validateProviderOrigin = true;

    [Tooltip("Plausibility bound (metres) between the provider's eye origin and the rig eye anchor. Real pupil-vs-eye-node differences are centimetres; this is a gate, never added to a coordinate.")]
    [Range(0.02f, 0.5f)]
    public float maxProviderOriginDeviationMeters = EyeGazeOrigin.DefaultMaxDeviationMeters;

    [Header("View frame (the camera the participant sees from) - schema v13")]
    [Tooltip("The RENDERED camera (centre-eye / HMD reference). Empty = Camera.main. In this project that is the XRI Main Camera under the XR Origin's Camera Floor Offset, NOT the OVRCameraRig's CenterEyeAnchor: the two follow the same " +
             "tracked head but differ by the Camera Floor Offset (see ViewFrame.cs). Eye / HMD world positions, heights above ground, the head-fallback ray and the anchor validation use this frame.")]
    public Transform viewCamera;

    [Tooltip("OVRCameraRig CenterEyeAnchor (the head frame the eye anchors are defined in). Empty = the scene's OVRCameraRig, then 'headTransform'.")]
    public Transform centerEyeAnchor;

    [Tooltip("Layers that are the walkable ground for height-above-ground. Empty (0) = the ground layer of the scene's AutoGroundAlign. NPC and participant colliders are never ground. No ground found = the height columns stay blank.")]
    public LayerMask groundLayerMask;

    [Tooltip("Record the view-frame columns (hmd_world_*, eye head-local / world positions, heights above ground) and use the rendered view for the head-fallback ray and the anchor validation. Turn off only to reproduce the pre-v13 behaviour.")]
    public bool recordViewFrame = true;

    [Header("Head Reference")]
    [Tooltip("HMD transform (e.g. CenterEyeAnchor) used to log head position/rotation alongside gaze, " +
             "and as a fallback gaze origin if a given eye is invalid. If left empty, Camera.main is used.")]
    public Transform headTransform;

    [Header("Session / Trial linkage")]
    [Tooltip("Optional: assign the scene's ExperimentManager. If left empty, auto-found via " +
             "FindObjectOfType at Start().")]
    public ExperimentManager experimentManager;

    [Tooltip("Optional: assign the scene's ExternalSessionController (source of participantId). " +
             "If left empty, auto-found via FindObjectOfType at Start().")]
    public ExternalSessionController sessionController;

    [Tooltip("If true, StartRecording()/StopRecording() are called automatically from " +
             "ExperimentManager.OnTrialStart/OnTrialEnd.")]
    public bool subscribeToExperimentEvents = true;

    [Tooltip("If true and no ExperimentManager is found in the scene, recording starts immediately on " +
             "Start() instead of waiting for trial events (useful for isolated testing of this script).")]
    public bool autoStartIfNoExperimentManager = false;

    [Header("Identification (used in filename + CSV columns)")]
    [Tooltip("Participant/subject identifier. If empty, resolved from ExternalSessionController.participantId.")]
    public string subjectId = "";

    [Tooltip("Trial/phase identifier. If empty, resolved from ExternalSessionController.selectedPart.")]
    public string trialId = "";

    [Header("Sampling")]
    [Tooltip("Samples per second. 30 Hz is the approved default.")]
    public float sampleRate = 30f;

    [Header("ROI / AOI raycasting")]
    [Tooltip("If true, each sample raycasts from the combined gaze origin (or the head-forward fallback " +
             "when eye tracking is invalid) and records what it hit, plus a light semantic class.")]
    public bool enableROIRaycast = true;

    [Tooltip("Layers considered for ROI/AOI raycasting. Recommended: Default + Ground + Water + UI only.")]
    public LayerMask roiLayerMask = ~0;

    [Tooltip("Maximum raycast distance (metres) for ROI/AOI detection.")]
    public float roiMaxDistance = 50f;

    [Header("UI (Canvas) gaze detection - gaze_ui_hits.csv (schema v1)")]
    [Tooltip("If true, the same gaze ray is also tested against the registered World Space / Screen Space - Camera Canvases (GazeUiRaycaster) and one row per gaze row is written to gaze_ui_hits.csv. " +
             "The physical raycast and gaze.csv are not affected either way. Screen Space - Overlay Canvases are not supported (never rendered into the headset image).")]
    public bool enableUiGaze = true;

    [Tooltip("Maximum distance (metres) of the UI ray. 0 = use roiMaxDistance, so both channels share one ray length.")]
    public float uiMaxDistance = 0f;

    [Tooltip("If false (default), the UI test runs only for rows with a real eye-gaze ray (combined_valid = 1). A head-forward fallback ray is not gaze; its row is still written with ui_ray_evaluated = 0.")]
    public bool uiRaycastOnHeadFallback = false;

    [Tooltip("If true (default), a Canvas needs an enabled GraphicRaycaster to be tested (the project's definition of a UI Canvas that takes part in interaction).")]
    public bool uiRequireGraphicRaycaster = true;

    [Tooltip("If true (default), Screen Space - Camera Canvases with a camera assigned are tested like World Space ones. Screen Space - Overlay is never tested.")]
    public bool uiSupportScreenSpaceCamera = true;

    [Tooltip("A Graphic whose effective alpha (Graphic colour x CanvasRenderer alpha x CanvasGroup alpha) is below this is invisible and never returned as a gaze target (exception: the transparent hit area of a control that has something visible under it).")]
    [Range(0f, 1f)] public float uiMinVisibleAlpha = 0.01f;

    [Tooltip("Optional event camera for World Space Canvases that have no Canvas.worldCamera. Empty = Camera.main (the camera GraphicRaycaster itself falls back to).")]
    public Camera uiEventCamera;

    [Header("Debug")]
    [Tooltip("Gates start/stop/warning logs; per-sample data is never logged regardless.")]
    public bool enableDebugLogs = true;

    // Internal recording state
    // isRecording      : a recording session is open (OnTrialStart..OnTrialEnd, or a manual StartRecording()).
    //                    It stays true while sampling is suspended by an application pause.
    // samplingCoroutine: the single sampling loop; null while suspended or stopped.
    // suspendedByPause : sampling was stopped by OnApplicationPause(true) inside an open session and is
    //                    waiting to be resumed. The session (path, header, relative-time origin) is kept.
    // terminated       : the component was destroyed or the application is quitting; never restart.
    bool isRecording = false;
    string fullPath = "";
    Coroutine samplingCoroutine = null;
    double recordingStartRealtime = 0.0;
    bool suspendedByPause = false;
    bool terminated = false;

    // ---- recording evidence (F-09 / F-18): counted from the code paths that really write rows, read by DataCaptureManager at manifest checkpoints ----
    /// <summary>Rows successfully appended to gaze.csv.</summary>
    public long RowsWritten { get; private set; }
    /// <summary>Rows whose combined ray came from at least one valid eye (combined_valid = 1).</summary>
    public long RowsValidEye { get; private set; }
    /// <summary>Rows with NO valid eye: the ray is the head-forward fallback (combined_valid = 0, gaze_ray_source = head_fallback).</summary>
    public long RowsFallback { get; private set; }
    public long RowsLeftValid { get; private set; }
    public long RowsRightValid { get; private set; }
    // ---- acquisition evidence (schema v8) ----
    /// <summary>Eye samples whose confidence passed the threshold but whose provider flag said invalid (stale confidence) - recorded as invalid.</summary>
    public long RowsStaleConfidenceRejected { get; private set; }
    /// <summary>Rows whose real OVREyeGaze reading was rejected because provider validity was neither readable from the same-row raw provider read nor from the lifecycle probe.</summary>
    public long RowsProviderUnverified { get; private set; }
    /// <summary>Per-eye readings suppressed because the OVREyeGaze is hosted on an OVRCameraRig eye anchor.</summary>
    public long RowsHostConflictBlocked { get; private set; }
    /// <summary>Provider-vs-rig-anchor origin diagnostics (counts by origin source, deviation summary). Written to the manifest eye_tracking block.</summary>
    public EyeGazeOrigin.DeviationStats OriginStats { get; } = new EyeGazeOrigin.DeviationStats();
    /// <summary>Behaviour of the raw provider confidence over the recorded rows (constant / varying / unavailable counts) - the manifest tells a reader whether the number carries information.</summary>
    public ConfidenceManifest ConfidenceInfo { get; } = new ConfidenceManifest();
    /// <summary>Fallback rows written while the lifecycle was NOT in a running state (eye tracking never started / failed / denied / unsupported).</summary>
    public long RowsFallbackNotAcquiring { get; private set; }
    public double FirstValidRowT { get; private set; } = double.NaN;
    public double LastValidRowT { get; private set; } = double.NaN;
    /// <summary>Longest stretch (seconds) between two rows with a valid eye, or from the first row to the first valid row, while sampling.</summary>
    public double LongestNoValidEyeSeconds { get; private set; }
    /// <summary>The acquisition state machine (null until a controller exists).</summary>
    public EyeTrackingLifecycle Lifecycle => lifecycleController != null ? lifecycleController.Lifecycle : null;
    /// <summary>Deadline-grid scheduler of the sampling loop (statistics: ticks, skipped ticks, max lateness).</summary>
    public SampleScheduler Scheduler { get; private set; }
    double _noValidSince = double.NaN;
    /// <summary>The CSV writer (shared failure/recovery policy and counters); null until the first StartRecording.</summary>
    public CaptureCsvWriter Writer => gazeWriter;
    CaptureCsvWriter gazeWriter, diagWriter;
    /// <summary>The gaze_diagnostics.csv writer (schema v17); null until the first StartRecording or when recordDiagnostics is off.</summary>
    public CaptureCsvWriter DiagnosticsWriter => diagWriter;
    CaptureCsvWriter uiWriter;
    /// <summary>The gaze_ui_hits.csv writer (companion file, schema v1); null until the first StartRecording or when enableUiGaze is off.</summary>
    public CaptureCsvWriter UiHitsWriter => uiWriter;
    /// <summary>The UI raycaster (counters of tested / skipped canvases go to the manifest).</summary>
    public GazeUiRaycaster UiRaycaster { get; } = new GazeUiRaycaster();
    /// <summary>Companion-file evidence: rows with a UI hit, rows where the UI path was not evaluated, primary-target class counts.</summary>
    public long UiRowsWritten { get; private set; }
    public long UiRowsWithHit { get; private set; }
    public long UiRowsNotEvaluated { get; private set; }
    public long UiPrimaryCanvasUi { get; private set; }
    public long UiPrimaryWorldObject { get; private set; }
    public long UiPrimaryUncertain { get; private set; }
    public long UiPrimaryNoHit { get; private set; }
    /// <summary>Rows where BOTH channels hit and the UI lay behind the physical hit (occlusion policy applied).</summary>
    public long UiRowsBehindWorldHit { get; private set; }
    public long WriteErrors => gazeWriter != null ? gazeWriter.WriteErrors : 0;
    public string LastWriteError => gazeWriter != null ? gazeWriter.LastError : "";
    public double FirstRowT { get; private set; } = double.NaN;
    public double LastRowT { get; private set; } = double.NaN;
    /// <summary>True once the CSV header was written successfully (the file is usable).</summary>
    public bool HeaderWriteOk => gazeWriter != null && gazeWriter.HeaderWritten;
    public int RecordingStarts { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    /// <summary>Achieved timing of the SUCCESSFULLY written rows (see SamplingStats); epochs break at pauses and while the capture gate is closed.</summary>
    public SamplingStats Sampling { get; private set; }

    /// <summary>Test seam: replaces the OVREyeGaze reading of one eye with a fixed sample (null/inactive = real reading). Never set in production.</summary>
    [Serializable]
    public class EyeInjection { public bool active = true; public float confidence; public Vector3 origin; public Vector3 direction = Vector3.forward; public bool applyProviderGate; }
    [NonSerialized] public EyeInjection testLeftEye;
    [NonSerialized] public EyeInjection testRightEye;

    /// <summary>True while the sampling loop is running (false while suspended by a pause or stopped).</summary>
    public bool IsSampling => samplingCoroutine != null;

    /// <summary>True while an open recording session is suspended by an application pause.</summary>
    public bool IsSuspendedByPause => suspendedByPause;

    /// <summary>Test hook: when non-empty, gaze rows are written to this exact file instead of the session folder / persistentDataPath.</summary>
    [NonSerialized] public string outputPathOverride = "";

    // Revision 2: shared capture context (may be null - the recorder still works, fail-open).
    DataCaptureManager dataCapture;

    void Awake()
    {
        Sampling = new SamplingStats(sampleRate);
        if (headTransform == null && Camera.main != null)
        {
            headTransform = Camera.main.transform;
        }

        ResolveEyeAnchors();
        EnsureLifecycleController();

        if (enableDebugLogs && (leftEyeGaze == null || rightEyeGaze == null))
        {
            Debug.LogWarning("[GazeRecorder] leftEyeGaze and/or rightEyeGaze is not assigned. This recorder " +
                "will still run but will record all-invalid samples until both are assigned.");
        }
    }

    // The rig's own per-eye positions: OVRCameraRig.leftEyeAnchor / rightEyeAnchor. Explicit inspector slots win; otherwise the scene's OVRCameraRig. Missing anchors are
    // not an error: the provider origin is then recorded unchecked (origin source 'provider_unchecked').
    int _anchorSearches;
    void ResolveEyeAnchors()
    {
        if (leftEyeAnchor != null && rightEyeAnchor != null && _rigTrackingSpace != null) return;
        if (leftEyeAnchor != null && rightEyeAnchor != null && _anchorSearches >= 8) return;
        _anchorSearches++;   // bounded: the rig creates its anchors in its own Awake/Update, which may run after this component's Awake, so the search is repeated a few times (Awake, Start, first samples)
        OVRCameraRig rig = FindObjectOfType<OVRCameraRig>();
        if (rig == null) return;
        if (leftEyeAnchor == null) leftEyeAnchor = rig.leftEyeAnchor;
        if (rightEyeAnchor == null) rightEyeAnchor = rig.rightEyeAnchor;
        if (_rigTrackingSpace == null) _rigTrackingSpace = rig.trackingSpace != null ? rig.trackingSpace : rig.transform;
    }

    // The permission request, the start of OVREyeGaze and its retries belong to ONE owner (EyeTrackingLifecycleController); this recorder only makes sure it exists.
    void EnsureLifecycleController()
    {
        if (lifecycleController == null) lifecycleController = FindObjectOfType<EyeTrackingLifecycleController>();
        if (lifecycleController == null)
        {
            lifecycleController = gameObject.AddComponent<EyeTrackingLifecycleController>();
            lifecycleController.leftEyeGaze = leftEyeGaze;
            lifecycleController.rightEyeGaze = rightEyeGaze;
        }
    }

    void Start()
    {
        ResolveEyeAnchors();
        if (experimentManager == null) experimentManager = FindObjectOfType<ExperimentManager>();
        if (sessionController == null)  sessionController  = FindObjectOfType<ExternalSessionController>();
        if (dataCapture == null)        dataCapture        = FindObjectOfType<DataCaptureManager>();

        if (subscribeToExperimentEvents && experimentManager != null)
        {
            experimentManager.OnTrialStart += OnExperimentTrialStart;
            experimentManager.OnTrialEnd   += OnExperimentTrialEnd;
        }
        else if (subscribeToExperimentEvents && experimentManager == null && autoStartIfNoExperimentManager)
        {
            if (enableDebugLogs)
                Debug.LogWarning("[GazeRecorder] No ExperimentManager found; autoStartIfNoExperimentManager " +
                    "is true, so starting recording immediately (standalone/testing mode).");
            StartRecording();
        }
    }

    void OnDestroy()
    {
        terminated = true;
        if (_subRig != null) { _subRig.UpdatedAnchors -= OnAnchorsUpdated; _subRig = null; }
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= OnExperimentTrialStart;
            experimentManager.OnTrialEnd   -= OnExperimentTrialEnd;
        }
        StopRecording();
    }

    // F-01: a pause only SUSPENDS sampling (the open recording session, file path and relative-time origin are kept), and a resume
    // restarts the loop if - and only if - the session is still open. Previously a pause called StopRecording(), which discarded the
    // session, so nothing ever restarted gaze sampling while trajectory.csv kept recording.
    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus) SuspendSampling();
        else ResumeSampling();
    }

    // Some devices deliver focus gain without a matching pause(false) (or in the opposite order). Resuming is idempotent and only
    // acts on a session suspended by a pause, so handling it here cannot create a second loop or start a recording on its own.
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus) ResumeSampling();
    }

    void OnApplicationQuit()
    {
        terminated = true;
        if (isRecording) StopRecording();
    }

    void SuspendSampling()
    {
        if (!isRecording || suspendedByPause) return;

        StopSamplingLoop();
        suspendedByPause = true;
        PauseCount++;
        Sampling?.BreakEpoch();   // intentional gap: the wait across a pause must never be read as a low sensor rate

        if (enableDebugLogs)
            Debug.LogWarning("[GazeRecorder] Application paused while recording - sampling suspended; it resumes with the application " +
                "if the trial is still open. Data already written is safe (each row is flushed on write).");
    }

    void ResumeSampling()
    {
        if (!suspendedByPause) return;

        // The trial ended (StopRecording clears the flag), the recorder was destroyed, or the session ended: never restart.
        if (!isRecording || terminated || (experimentManager != null && experimentManager.SessionEnded))
        {
            suspendedByPause = false;
            return;
        }

        if (samplingCoroutine != null)
        {
            suspendedByPause = false;
            return;
        }

        if (!isActiveAndEnabled || string.IsNullOrEmpty(fullPath))
        {
            // Stay suspended so a later resume callback can retry. Always logged: gaze rows are missing until it succeeds.
            Debug.LogWarning("[GazeRecorder] Could not resume gaze sampling after pause (component inactive or no output path); " +
                "gaze.csv will have a gap until sampling restarts.");
            return;
        }

        suspendedByPause = false;
        ResumeCount++;
        // The per-sample GazeCaptureActive gate in SamplingLoop still applies. Header, file and recordingStartRealtime are untouched.
        samplingCoroutine = StartCoroutine(SamplingLoop());

        if (enableDebugLogs)
            Debug.Log($"[GazeRecorder] Sampling resumed after pause. path={fullPath}");
    }

    void StopSamplingLoop()
    {
        if (samplingCoroutine != null)
        {
            StopCoroutine(samplingCoroutine);
            samplingCoroutine = null;
        }
    }

    void OnExperimentTrialStart() { StartRecording(); }
    void OnExperimentTrialEnd()   { StopRecording(); }

    /// <summary>
    /// Begins a new gaze-recording session. Whether any row is actually WRITTEN is decided per-sample by
    /// the capture scope (Part 2 + Part 3 only).
    /// </summary>
    public void StartRecording()
    {
        if (_closedForSession) { LateRowsRejected++; return; }
        if (isRecording)
        {
            if (enableDebugLogs) Debug.LogWarning("[GazeRecorder] StartRecording() called while already recording; ignoring.");
            return;
        }

        // Identity/configuration rejected (F-14): the capture manager blocks research recording, so does the gaze recorder (no legacy fall-back file either).
        if (dataCapture == null) dataCapture = FindObjectOfType<DataCaptureManager>();
        if (dataCapture != null && dataCapture.RecordingBlocked)
        {
            Debug.LogError("[GazeRecorder] StartRecording refused: recording is blocked (" + dataCapture.BlockReason + ")");
            return;
        }

        // A destroyed / quitting recorder or an ended session can never start a new recording.
        if (terminated || (experimentManager != null && experimentManager.SessionEnded))
        {
            if (enableDebugLogs) Debug.LogWarning("[GazeRecorder] StartRecording() ignored: the recorder was terminated or the session has ended.");
            return;
        }

        if (string.IsNullOrEmpty(subjectId) && dataCapture != null && !string.IsNullOrEmpty(dataCapture.ParticipantId)) subjectId = dataCapture.ParticipantId;
        if (string.IsNullOrEmpty(subjectId) && sessionController != null)
        {
            // A session controller exists, so this is an experiment session: the participant must be the VALIDATED one. Never record under a placeholder.
            if (!sessionController.ValidateIdentity().IsValid)
            {
                Debug.LogError("[GazeRecorder] StartRecording refused: the session configuration/participant identity is not valid (" + sessionController.Resolved.Identity.message + ")");
                return;
            }
            subjectId = sessionController.ActiveParticipantId;
        }
        // Legacy standalone/development fallback ONLY: no session controller exists in the scene at all (never reached in an experiment session).
        if (string.IsNullOrEmpty(subjectId)) subjectId = "UNKNOWN_SUBJECT";

        if (string.IsNullOrEmpty(trialId) && sessionController != null) trialId = sessionController.ActivePart.ToString();
        if (string.IsNullOrEmpty(trialId)) trialId = "UNKNOWN_TRIAL";

        fullPath = BuildFullPath();
        if (gazeWriter == null || gazeWriter.Path != fullPath)
        {
            gazeWriter = new CaptureCsvWriter(fullPath, GetCsvHeader(), "gaze");
            if (dataCapture != null) gazeWriter.Transition += dataCapture.OnWriterTransition;
        }
        if (recordDiagnostics)
        {
            string dpath = DiagnosticsPathFor(fullPath);
            if (diagWriter == null || diagWriter.Path != dpath)
            {
                diagWriter = new CaptureCsvWriter(dpath, GetDiagnosticsHeader(), "gaze_diagnostics");
                if (dataCapture != null) diagWriter.Transition += dataCapture.OnWriterTransition;
            }
        }
        if (enableUiGaze)
        {
            string upath = UiHitsPathFor(fullPath);
            if (uiWriter == null || uiWriter.Path != upath)
            {
                uiWriter = new CaptureCsvWriter(upath, GetUiHitsHeader(), "gaze_ui_hits");
                if (dataCapture != null) uiWriter.Transition += dataCapture.OnWriterTransition;
            }
            GazeUiRegistry.RescanScene();   // ONE scene scan per recording start (a safety net for Canvases created outside UIManager); never per frame
        }
        recordingStartRealtime = CaptureClock.TAbs;
        isRecording = true;
        RecordingStarts++;
        if (Sampling == null) Sampling = new SamplingStats(sampleRate);
        Sampling.SetNominalHz(sampleRate);

        if (enableDebugLogs)
            Debug.Log($"[GazeRecorder] Recording started. subject={subjectId} trial={trialId} path={fullPath}");

        samplingCoroutine = StartCoroutine(SamplingLoop());
    }

    /// <summary>Stops the current gaze-recording session, if any. Safe to call multiple times.</summary>
    public void StopRecording()
    {
        if (!isRecording && samplingCoroutine == null) return;

        StopSamplingLoop();
        Sampling?.BreakEpoch();
        suspendedByPause = false;   // a stopped session can no longer be resumed by a later pause(false) / focus callback

        if (isRecording && enableDebugLogs)
            Debug.Log($"[GazeRecorder] Recording stopped. path={fullPath}");

        isRecording = false;
    }

    /// <summary>gaze.csv -> gaze_diagnostics.csv (same folder); any other name gets "_diagnostics" before the extension.</summary>
    public static string DiagnosticsPathFor(string gazePath)
    {
        string dir = Path.GetDirectoryName(gazePath) ?? "", name = Path.GetFileNameWithoutExtension(gazePath);
        return Path.Combine(dir, (name == "gaze" ? "gaze" : name) + "_diagnostics.csv");
    }

    string BuildFullPath()
    {
        if (!string.IsNullOrEmpty(outputPathOverride)) return outputPathOverride;

        if (dataCapture == null) dataCapture = FindObjectOfType<DataCaptureManager>();

        if (dataCapture != null && !string.IsNullOrEmpty(dataCapture.SessionFolder))
            return Path.Combine(dataCapture.SessionFolder, "gaze.csv");

        // Fail-open fallback (no DataCaptureManager): keep the legacy standalone naming so gaze data is
        // never silently lost if the capture layer failed to initialise.
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        string fileName = $"{SanitizeForFilename(subjectId)}_{SanitizeForFilename(trialId)}_Gaze_{timestamp}.csv";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    // =================================================================================================================================
    // SCHEMA v17: the main gaze.csv is an ANALYSIS table (61 columns); everything that exists to QA / reproduce it lives in gaze_diagnostics.csv (one row per gaze row, joinable by session_id + frame_index + t_abs).
    // The columns below are the single source of truth for both files (tests, docs and the offline reader use these arrays).
    // =================================================================================================================================
    static string[] Xyz(string p) => new[] { p + "_x", p + "_y", p + "_z" };
    static string[] Quat(string p) => new[] { p + "_x", p + "_y", p + "_z", p + "_w" };
    static string[] Cat(params object[] parts) { var l = new List<string>(); foreach (var o in parts) { if (o is string str) l.Add(str); else l.AddRange((string[])o); } return l.ToArray(); }

    /// <summary>The v17 main gaze.csv header, in file order.</summary>
    public static readonly string[] PrimaryColumns = Cat(
        // identification and time (t_abs is the canonical CaptureClock time; time_since_start_s is t_abs minus the recording start)
        "t_abs", "time_since_start_s", "frame_index", "session_id", "subject_id", "trial_id", "part", "phase",
        // validity / provenance of the gaze ray (compact): combined_valid 0 = no valid eye (the ray_* columns of v16 are derivable: see docs), origin source, lifecycle state
        "combined_valid", "gaze_ray_source", "gaze_origin_source", "eye_tracking_state",
        // per-eye gaze rays (WorldSpace; blank when that eye is not valid) and the combined binocular ray
        Xyz("left_origin"), Xyz("left_dir"), Xyz("right_origin"), Xyz("right_dir"), Xyz("combined_origin"), Xyz("combined_dir"),
        // the rendered camera (view reference)
        Xyz("hmd_world"), Quat("hmd_world_rot"),
        // the physical gaze-ray interaction (Physics.Raycast along the combined ray, or the head-forward fallback ray when combined_valid = 0)
        "ray_did_hit", Xyz("hit_point"), "roi_hit_distance",
        "hit_collider_name", "hit_object_name", "hit_instance_id", "hit_layer", "hit_tag", "hit_semantic_class", "hit_building_id", "hit_npc_id", "hit_npc_spawn_instance_id", "hit_is_interactable", "hit_is_current_target", "hit_assigned_destination",
        // ground reference (blank when no Ground-layer surface lies within the ground ray)
        "ground_y", "hmd_height_above_ground", "left_eye_height_above_ground", "right_eye_height_above_ground",
        // eyelid closure: the RECOVERED weights (EyesClosed + min(EyesLookDown) when eye-following is valid, else the raw weight) and a compact validity flag (1 = measured this row from a valid Visual frame)
        "eyes_closed_left_recovered", "eyes_closed_right_recovered", "face_closure_valid");

    /// <summary>The v17 gaze_diagnostics.csv header, in file order. Keys first, then the groups documented in docs/schema_v17_changes.md.</summary>
    public static readonly string[] DiagnosticColumns = Cat(
        "t_abs", "frame_index", "session_id",
        "unix_time_ms",
        // v14 provider pipeline (37)
        "provider_time_s", "provider_time_changed",
        Xyz("left_raw_tracking_pos"), Xyz("left_raw_tracking_dir"), Xyz("right_raw_tracking_pos"), Xyz("right_raw_tracking_dir"),
        Xyz("left_gaze_head_pos"), Xyz("left_gaze_head_dir"), Xyz("right_gaze_head_pos"), Xyz("right_gaze_head_dir"),
        "left_gaze_vs_view_anchor_head_m", "right_gaze_vs_view_anchor_head_m",
        "left_worldspace_vs_composed_origin_m", "right_worldspace_vs_composed_origin_m", "left_worldspace_vs_composed_dir_deg", "right_worldspace_vs_composed_dir_deg",
        "view_anchor_separation_m", "gaze_head_separation_m", "runtime_user_ipd_m", "anchors_updated_frame", "origin_fallback_reason",
        // per-eye validity and provider confidence
        "left_confidence", "left_valid", "left_provider_valid", "right_confidence", "right_valid", "right_provider_valid", "combined_confidence",
        // legacy OVR-side head anchor (rig frame, WITHOUT the Camera Floor Offset)
        Xyz("head_pos"), Quat("head_rot"),
        // stereo view anchors (head frame / rendered view frame): NOT gaze origins
        Xyz("left_eye_head_local"), Xyz("right_eye_head_local"), Xyz("left_eye_world"), Xyz("right_eye_world"),
        // face tracking: state, live per-row source / validity, raw weights and correction inputs, uncalibrated blink state
        "face_state", "face_tracking_started", "face_expressions_valid", "eye_following_valid", "eyes_look_down_left", "eyes_look_down_right", "eyes_closed_left", "eyes_closed_right",
        "face_sample_frame", "face_data_source", "face_provider_time_s", "blink_state",
        // the ray that was actually cast when combined_valid = 0 (head-forward fallback); blank on eye-gaze rows (there the cast ray is combined_origin / combined_dir). Keeps every hit reconstructible even without a resolved view frame.
        Xyz("fallback_ray_origin"), Xyz("fallback_ray_dir"));

    /// <summary>Schema version of the OPTIONAL companion file gaze_ui_hits.csv (independent of the session schema_version, which stays 17). Bumped only when UiHitColumns changes.</summary>
    public const int GazeUiHitsSchemaVersion = 1;
    public const string GazeUiHitsFileName = "gaze_ui_hits.csv";

    /// <summary>The gaze_ui_hits.csv v1 header, in file order. One row per gaze row; the keys (t_abs, frame_index, session_id) are the same values as in gaze.csv.</summary>
    public static readonly string[] UiHitColumns = Cat(
        "t_abs", "frame_index", "session_id",
        // was the UI path run for this row (blank ui_hit = not evaluated, never "looked and found nothing")
        "ui_ray_evaluated", "ui_ray_skip_reason", "ui_hit",
        // the Canvas and the specific Graphic that was hit
        "ui_canvas_id", "ui_canvas_name", "ui_canvas_render_mode", "ui_canvas_sorting_order",
        "ui_element_id", "ui_element_name", "ui_element_category", "ui_roi_label", "ui_element_has_gaze_component", "ui_interactable",
        Xyz("ui_hit_point"), "ui_hit_distance", "ui_graphics_under_ray",
        // the raw physical result of the same ray (independent of the UI result)
        "world_hit", "world_hit_distance", "world_hit_instance_id", "world_hit_semantic_class", "world_occluder_established",
        // the resolved primary target
        "primary_target_class", "primary_target_basis", "primary_target_id", "primary_target_roi");

    string GetCsvHeader() => string.Join(",", PrimaryColumns);
    string GetDiagnosticsHeader() => string.Join(",", DiagnosticColumns);
    string GetUiHitsHeader() => string.Join(",", UiHitColumns);

    /// <summary>gaze.csv -> gaze_ui_hits.csv (same folder); any other name gets "_ui_hits" before the extension.</summary>
    public static string UiHitsPathFor(string gazePath)
    {
        string dir = Path.GetDirectoryName(gazePath) ?? "", name = Path.GetFileNameWithoutExtension(gazePath);
        return Path.Combine(dir, (name == "gaze" ? "gaze" : name) + "_ui_hits.csv");
    }

    struct EyeSample
    {
        public bool valid;
        public float confidence;
        public Vector3 origin;
        public Vector3 direction;
        public EyeOriginSource originSource;
        public bool confidenceAvailable;   // a usable RAW provider confidence exists for this sample (see GazeConfidence); false = the confidence columns stay blank
        public bool hasProviderWorld;      // a real OVREyeGaze reading (not injected): providerWorldOrigin / Dir are its WorldSpace transform BEFORE any fallback replaced the origin
        public Vector3 providerWorldOrigin, providerWorldDir;
        public bool providerUnverified;    // a real OVREyeGaze reading whose provider validity could not be established for this row (not accepted as an eye sample)
        public string fallbackReason;      // (null = none) why the recorded origin is not the provider's: provider_exceeds_deviation_gate | provider_origin_non_finite ("" = provider origin used / not applicable)
    }

    EyeSample ReadEye(OVREyeGaze eyeGaze, EyeInjection injected, bool providerKnown, bool providerValid, bool hostBlocked, Transform eyeAnchor = null, Vector3? viewFrameAnchor = null)
    {
        EyeSample sample = new EyeSample
        {
            valid = false,
            confidence = 0f,
            origin = Vector3.zero,
            direction = Vector3.forward,
            originSource = EyeOriginSource.ProviderUnchecked
        };

        if (injected != null && injected.active)
        {
            sample.confidence = injected.confidence;
            sample.confidenceAvailable = GazeConfidence.Usable(sample.confidence);
            sample.origin = injected.origin;
            sample.direction = injected.direction;
            sample.valid = sample.confidenceAvailable && sample.confidence >= minConfidenceForValid;
            if (!injected.applyProviderGate) return sample;
        }
        else
        {
            if (eyeGaze == null || !eyeGaze.isActiveAndEnabled) return sample;

            sample.confidence = eyeGaze.Confidence;   // RAW provider value (GazeConfidence documents the path); stale when the provider flags the eye invalid
            sample.confidenceAvailable = GazeConfidence.Usable(sample.confidence);
            sample.origin = eyeGaze.transform.position;
            sample.direction = eyeGaze.transform.forward;
            sample.hasProviderWorld = true; sample.providerWorldOrigin = sample.origin; sample.providerWorldDir = sample.direction;
            // The threshold only ACCEPTS or REJECTS a measured value. A provider-valid eye with no usable confidence is not rejected by it (nothing was measured to compare), and a missing
            // confidence never turns a valid ray into an invalid one.
            sample.valid = GazeConfidence.IsValid(sample.confidence, sample.confidenceAvailable, minConfidenceForValid, providerKnown, providerValid);
            // A real OVREyeGaze reading whose provider validity cannot be established (neither the same-row raw read nor the lifecycle probe answered) is UNVERIFIED: OVREyeGaze keeps its last Confidence and
            // transform when the provider flags a sample invalid, so a non-null transform and a confidence above the threshold prove nothing about THIS sample. It is not accepted as an eye sample.
            if (!GazeConfidence.ProviderEstablished(requireProviderValidity, providerKnown)) { sample.providerUnverified = true; if (sample.valid) { sample.valid = false; RowsProviderUnverified++; } }
            if (validateProviderOrigin)
            {
                // Origin and direction must live in the same world frame. The direction is a rotation (unaffected by a translation between tracking origins); the origin is
                // validated against the rig's own eye position (see EyeGazeOrigin for the observed mismatch). Nothing is added to or subtracted from a coordinate.
                Vector3 providerWorld = sample.origin;
                // The anchor the provider is compared with / replaced by is the eye as seen from the RENDERED view (viewFrameAnchor) when the view frame is available: the provider (OVREyeGaze converts with
                // Camera.main) lives in that frame, the raw OVR anchor does not (it lacks the XR Origin's Camera Floor Offset). Without a view frame the raw anchor is used, as before.
                Vector3? rigAnchor = eyeAnchor != null ? eyeAnchor.position : (Vector3?)null;
                Vector3? anchorWorld = viewFrameAnchor ?? rigAnchor;
                sample.originSource = EyeGazeOrigin.Resolve(providerWorld, anchorWorld, maxProviderOriginDeviationMeters, out sample.origin, out float deviation);
                if (viewFrameAnchor.HasValue && sample.originSource == EyeOriginSource.EyeAnchor) sample.originSource = EyeOriginSource.EyeAnchorView;
                if (sample.originSource == EyeOriginSource.EyeAnchor || sample.originSource == EyeOriginSource.EyeAnchorView) sample.fallbackReason = float.IsInfinity(deviation) ? "provider_origin_non_finite" : "provider_exceeds_deviation_gate";
                if (sample.valid && rigAnchor.HasValue && EyeGazeOrigin.IsFinite(providerWorld) && EyeGazeOrigin.IsFinite(rigAnchor.Value)) ProviderMinusRigAnchor.Add(providerWorld - rigAnchor.Value);
                if (sample.valid) OriginStats.Note(sample.originSource, providerWorld, anchorWorld, deviation);
                if (sample.valid && anchorWorld.HasValue && headTransform != null)
                    OriginStats.NoteInFrames(providerWorld, anchorWorld.Value, headTransform.rotation, _rigTrackingSpace != null ? _rigTrackingSpace.rotation : Quaternion.identity);
            }
        }
        // Provider gating (same rules for a real OVREyeGaze reading and for an injected one that opts in): host conflict and stale confidence.
        if (sample.valid && hostBlocked) { sample.valid = false; RowsHostConflictBlocked++; }
        else if (sample.valid && providerKnown && !providerValid) { sample.valid = false; RowsStaleConfidenceRejected++; }
        // The SDK keeps its previous Confidence when the provider flags the eye invalid: that number is not an observation of THIS sample.
        sample.confidenceAvailable = GazeConfidence.AvailableAfterProvider(sample.confidenceAvailable, providerKnown, providerValid) && !sample.providerUnverified;   // an unverified sample's Confidence may be stale: never written as a measurement
        return sample;
    }

    // ---- per-eye pipeline helpers (schema v14) ----
    void OnAnchorsUpdated(OVRCameraRig rig) { AnchorsUpdatedFrame = Time.frameCount; Pipeline.anchors_updated_events++; }

    void TrySubscribeAnchors()
    {
        if (_subRig != null || Time.unscaledTime < _nextSubT) return;
        _nextSubT = Time.unscaledTime + 1f;
        OVRCameraRig rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null) { _subRig = rig; rig.UpdatedAnchors += OnAnchorsUpdated; }
    }

    // One OVRPlugin read per row (the public call OVREyeGaze.Update itself makes). Unavailable = every raw column stays blank.
    RawGazeState _rowRaw; bool _rowRawOk;   // the raw provider read of the row being built (one OVRPlugin read per row)

    bool ReadRawGaze(out RawGazeState r)
    {
        r = default;
        if (testRawGaze != null) { r = testRawGaze(); return r.available; }
        try
        {
            if (!OVRPlugin.initialized || !OVRPlugin.eyeTrackingEnabled) return false;
            if (!OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref _rawBuf) || _rawBuf.EyeGazes == null || _rawBuf.EyeGazes.Length < 2) return false;
            r.available = true; r.time = _rawBuf.Time;
            r.left = ToRawEye(_rawBuf.EyeGazes[(int)OVRPlugin.Eye.Left]); r.right = ToRawEye(_rawBuf.EyeGazes[(int)OVRPlugin.Eye.Right]);
            return true;
        }
        catch (Exception) { return false; }
    }

    static RawGazeEye ToRawEye(OVRPlugin.EyeGazeState g)
    {
        OVRPose p = g.Pose.ToOVRPose();   // the SDK's own handedness conversion
        return new RawGazeEye { valid = g.IsValid, confidence = g.Confidence, rawPos = p.position, rawDir = p.orientation * Vector3.forward };
    }

    // The SDK's head-space conversion (OVRCommon.ToHeadSpacePose = head node^-1 * pose, head node from the same OVRPlugin source the anchors use) - or a test head reference.
    bool ToHeadSpace(Vector3 pos, Quaternion rot, out Vector3 hp, out Quaternion hr)
    {
        hp = default; hr = Quaternion.identity;
        try
        {
            if (testHeadReference != null) { var h = testHeadReference(); EyePipelineMath.ToHeadSpace(h.pos, h.rot, pos, rot, out hp, out hr); return true; }
            OVRPose p = new OVRPose { position = pos, orientation = rot }.ToHeadSpacePose();
            hp = p.position; hr = p.orientation; return EyeGazeOrigin.IsFinite(hp);
        }
        catch (Exception) { return false; }
    }

    static Quaternion LookOf(Vector3 dir) => dir.sqrMagnitude > 1e-12f ? Quaternion.LookRotation(dir, Vector3.up) : Quaternion.identity;

    float ReadRuntimeIpd(double now, out bool ok)
    {
        if (testRuntimeIpd != null) { float t = testRuntimeIpd(); ok = EyePipelineMath.IsUsableIpd(t); return t; }
        if (now >= _nextIpdT)
        {
            _nextIpdT = now + 1.0;
            try { _ipdCache = OVRPlugin.initialized ? OVRPlugin.ipd : 0f; } catch (Exception) { _ipdCache = 0f; }
            _ipdOk = EyePipelineMath.IsUsableIpd(_ipdCache);
        }
        ok = _ipdOk; return _ipdCache;
    }

    // The 37 schema-v14 columns of one row (see GetCsvHeader). Everything unavailable is blank; nothing is estimated.
    string PipelineColumns(double tAbs, bool vfOk, in ViewFrameSample vf, in EyeSample left, in EyeSample right, string gazeRaySource)
    {
        const int N = 37;
        string[] c = new string[N]; for (int i = 0; i < N; i++) c[i] = "";
        if (!recordProviderPipeline) return string.Join(",", c);
        bool rawOk = _rowRawOk; RawGazeState raw = _rowRaw;   // the read taken at the top of this row (SamplingLoop)
        Vector3 lHeadPos = default, lHeadDir = default, rHeadPos = default, rHeadDir = default; bool lHead = false, rHead = false;
        if (rawOk)
        {
            Pipeline.raw_provider_read_available = true; Pipeline.rows_with_raw_provider++;
            c[0] = raw.time.ToString("F6", CultureInfo.InvariantCulture);
            if (!double.IsNaN(_prevProviderTime))
            {
                bool adv = raw.time != _prevProviderTime; c[1] = adv ? "1" : "0";
                if (adv) Pipeline.rows_provider_time_advanced++; else Pipeline.rows_provider_time_repeated++;
            }
            _prevProviderTime = raw.time;
            if (raw.left.valid)
            {
                SetV3(c, 2, raw.left.rawPos); SetV3(c, 5, raw.left.rawDir);
                if (ToHeadSpace(raw.left.rawPos, LookOf(raw.left.rawDir), out Vector3 hp, out Quaternion hr)) { lHead = true; lHeadPos = hp; lHeadDir = hr * Vector3.forward; SetV3(c, 14, lHeadPos); SetV3(c, 17, lHeadDir); }
            }
            if (raw.right.valid)
            {
                SetV3(c, 8, raw.right.rawPos); SetV3(c, 11, raw.right.rawDir);
                if (ToHeadSpace(raw.right.rawPos, LookOf(raw.right.rawDir), out Vector3 hp, out Quaternion hr)) { rHead = true; rHeadPos = hp; rHeadDir = hr * Vector3.forward; SetV3(c, 20, rHeadPos); SetV3(c, 23, rHeadDir); }
            }
        }
        // view anchors in the SAME head frame the provider is converted into (so the two are comparable)
        Vector3 laHead = default, raHead = default; bool laOk = false, raOk = false;
        if (vfOk && ViewProbe.LeftEye != null && ViewProbe.RightEye != null)
        {
            laOk = ToHeadSpace(ViewProbe.LeftEye.localPosition, ViewProbe.LeftEye.localRotation, out laHead, out _);
            raOk = ToHeadSpace(ViewProbe.RightEye.localPosition, ViewProbe.RightEye.localRotation, out raHead, out _);
        }
        if (lHead && laOk) { float d = (lHeadPos - laHead).magnitude; c[26] = F5(d); Pipeline.left_gaze_vs_view_anchor_head_m.Add(d); }
        if (rHead && raOk) { float d = (rHeadPos - raHead).magnitude; c[27] = F5(d); Pipeline.right_gaze_vs_view_anchor_head_m.Add(d); }
        // coherence: the OVREyeGaze WorldSpace result vs (rendered camera * head-space eye). ~0 when the camera and the provider sample share a frame.
        if (vfOk && lHead && left.hasProviderWorld)
        {
            float d = (EyePipelineMath.ComposeOrigin(vf.hmdPos, vf.hmdRot, lHeadPos) - left.providerWorldOrigin).magnitude;
            float a = EyePipelineMath.AngleDeg(EyePipelineMath.ComposeDirection(vf.hmdRot, lHeadDir), left.providerWorldDir);
            c[28] = F5(d); c[30] = F3(a); Pipeline.left_worldspace_vs_composed_origin_m.Add(d); Pipeline.left_worldspace_vs_composed_dir_deg.Add(a);
        }
        if (vfOk && rHead && right.hasProviderWorld)
        {
            float d = (EyePipelineMath.ComposeOrigin(vf.hmdPos, vf.hmdRot, rHeadPos) - right.providerWorldOrigin).magnitude;
            float a = EyePipelineMath.AngleDeg(EyePipelineMath.ComposeDirection(vf.hmdRot, rHeadDir), right.providerWorldDir);
            c[29] = F5(d); c[31] = F3(a); Pipeline.right_worldspace_vs_composed_origin_m.Add(d); Pipeline.right_worldspace_vs_composed_dir_deg.Add(a);
        }
        if (vfOk) { float s = (vf.leftLocal - vf.rightLocal).magnitude; c[32] = F5(s); Pipeline.view_anchor_separation_m.Add(s); }
        if (lHead && rHead) { float s = (lHeadPos - rHeadPos).magnitude; c[33] = F5(s); Pipeline.gaze_head_separation_m.Add(s); }
        float ipd = ReadRuntimeIpd(tAbs, out bool ipdOk);
        if (ipdOk) { c[34] = F5(ipd); Pipeline.runtime_user_ipd_available = true; Pipeline.runtime_user_ipd_m.Add(ipd); }
        if (AnchorsUpdatedFrame >= 0)
        {
            c[35] = Inv(AnchorsUpdatedFrame);
            long lag = Time.frameCount - AnchorsUpdatedFrame; if (lag > Pipeline.anchors_frame_lag_max) Pipeline.anchors_frame_lag_max = lag;
        }
        // why the recorded origin is not (all) the provider's
        string reason = "";
        if (gazeRaySource == "head_fallback") reason = (left.providerUnverified || right.providerUnverified) ? "provider_validity_unverified" : "no_valid_eye";
        else
        {
            string lr = (left.valid ? left.fallbackReason : null) ?? "", rr = (right.valid ? right.fallbackReason : null) ?? "";
            reason = (lr.Length > 0 && rr.Length > 0 && lr != rr) ? "mixed" : (lr.Length > 0 ? lr : rr);
        }
        c[36] = reason;
        return string.Join(",", c);
    }

    static void SetV3(string[] c, int at, Vector3 v) { c[at] = F4(v.x); c[at + 1] = F4(v.y); c[at + 2] = F4(v.z); }
    static string F5(float v) => v.ToString("F5", CultureInfo.InvariantCulture);

    // ---- XR Origin facts (schema v13): about every 2 s, guarded, observation only ----
    void RefreshXrFacts(double now)
    {
        if (now < _nextXrFactsT) return;
        _nextXrFactsT = now + 2.0;
        XrFactsLast = testXrFacts != null ? testXrFacts() : ViewProbe.ReadXrFacts();
        if (testXrFacts == null)
        {
            try { OvrTrackingOriginType = OVRPlugin.initialized && OVRManager.instance != null ? OVRManager.instance.trackingOriginType.ToString() : ""; }
            catch (Exception) { OvrTrackingOriginType = ""; }
        }
    }

    // ---- tracking-origin evidence (G1): read at about 0.5 Hz, guarded, observation only ----
    void RefreshTrackingSpaceProbe(double now)
    {
        if (now < _nextTrackingProbeT) return;
        _nextTrackingProbeT = now + 2.0;
        EyeGazeOrigin.TrackingSpaceFacts f = testTrackingFacts != null ? testTrackingFacts() : ReadRuntimeTrackingFacts();
        TrackingSpace.Note(f);
    }

    static EyeGazeOrigin.TrackingSpaceFacts ReadRuntimeTrackingFacts()
    {
        var f = new EyeGazeOrigin.TrackingSpaceFacts { OvrPluginOrigin = "unavailable", UnityXrMode = "unavailable", LoadedXrDevice = "", NativeXrApi = "" };
        try
        {
            f.OvrPluginInitialized = OVRPlugin.initialized;
            if (f.OvrPluginInitialized)
            {
                f.OvrPluginOrigin = OVRPlugin.GetTrackingOriginType().ToString();   // OVRPlugin's OWN tracking origin (the space OVRPlugin node / eye-gaze poses are reported in)
                f.OpenXrActive = OVRPlugin.UnityOpenXR.Enabled;
                f.NativeXrApi = OVRPlugin.nativeXrApi.ToString();
                f.OvrPluginHeadY = OVRPlugin.GetNodePose(OVRPlugin.Node.Head, OVRPlugin.Step.Render).ToOVRPose().position.y;
            }
            f.LoadedXrDevice = OVRManager.loadedXRDevice.ToString();
        }
        catch (Exception) { /* not available on this runtime: the manifest says so */ }
        try
        {
            var subs = new System.Collections.Generic.List<UnityEngine.XR.XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            if (subs.Count > 0) f.UnityXrMode = subs[0].GetTrackingOriginMode().ToString();   // Device = eye-level (what the rig, camera and eye anchors use); Floor = floor-referenced
            var head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
            if (head.isValid && head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 hp)) f.UnityHeadY = hp.y;
        }
        catch (Exception) { }
        return f;
    }

    // ---- blink signal plumbing (decisions live in BlinkCapability / BlinkDetector, both unit-tested) ----
    void RefreshBlinkCapability(double now)
    {
        if (Blink == null) Blink = new BlinkDetector(blinkCloseThreshold, Mathf.Min(blinkOpenThreshold, blinkCloseThreshold - 0.01f));
        if (now < _nextBlinkCapabilityT) return;
        _nextBlinkCapabilityT = now + 1.0;
        BlinkFacts f = testBlinkFacts != null ? testBlinkFacts() : ReadRuntimeBlinkFacts();
        BlinkFactsLast = f;
        BlinkStatus = BlinkCapability.Decide(f, out string reason);
        BlinkReason = reason;
    }

    BlinkFacts ReadRuntimeBlinkFacts()
    {
        var f = new BlinkFacts();
        try
        {
            f.OvrPluginInitialized = OVRPlugin.initialized;
            if (f.OvrPluginInitialized) { f.FaceTrackingSupported = OVRPlugin.faceTracking2Supported; f.FaceTrackingEnabled = OVRPlugin.faceTracking2Enabled; }
            f.PermissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.FaceTracking);
        }
        catch (Exception) { }
        if (faceExpressions == null && !_faceSearched) { _faceSearched = true; faceExpressions = FindObjectOfType<OVRFaceExpressions>(); }   // searched once: a scene-wide search every second would cost frames
        EnsureFaceExpressions(ref f, allowCreate: !FaceStartup.attempted);   // schema v17: lazy creation only when NO start-up gate ran (standalone use); otherwise the gate created it before the first row
        f.ComponentPresent = faceExpressions != null && faceExpressions.isActiveAndEnabled;
        f.SignalValid = f.ComponentPresent && faceExpressions.ValidExpressions;
        ProbeFaceState(ref f);
        return f;
    }

    // Creates the face-tracking component when the project asked for none but the runtime supports it, and re-starts an auto-created one a bounded number of times. About once per second (RefreshBlinkCapability).
    void EnsureFaceExpressions(ref BlinkFacts f, bool allowCreate = true)
    {
        if (!autoCreateFaceExpressions || !f.OvrPluginInitialized || !f.FaceTrackingSupported) return;
        double now = CaptureClock.TAbs;
        try
        {
            if (faceExpressions == null)
            {
                if (!allowCreate) return;   // a start-up gate ran: creation belongs to it, never to the data path of the first gaze row
                if (!f.PermissionGranted && !FacePermissionRequested)
                {
                    FacePermissionRequested = true;
                    OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.FaceTracking });   // no-op off Android (Link / Editor)
                }
                faceExpressions = gameObject.AddComponent<OVRFaceExpressions>();   // OnEnable starts face tracking, or waits for the permission callback
                FaceAutoCreated = true;
                if (enableDebugLogs) Debug.Log("[GazeRecorder] OVRFaceExpressions created at run time (autoCreateFaceExpressions); permission granted=" + f.PermissionGranted);
            }
            else if (FaceAutoCreated && !faceExpressions.enabled && f.PermissionGranted && !f.FaceTrackingEnabled && FaceStartAttempts < MaxFaceStartAttempts && now >= _nextFaceRetryT)
            {
                FaceStartAttempts++; _nextFaceRetryT = now + FaceRetryIntervalS;
                faceExpressions.enabled = true;   // OnEnable runs StartFaceTracking2 again; on failure the component disables itself again and the SDK logs one warning
            }
        }
        catch (Exception e) { Debug.LogWarning("[GazeRecorder] face-tracking component setup failed: " + e.Message); }
    }

    // A direct, independent OVRPlugin.GetFaceState2 read (1 Hz, never per row): the API result, Status.IsValid, the data source (Visual / Audio) and the provider timestamp. OVRFaceExpressions keeps these private.
    void ProbeFaceState(ref BlinkFacts f)
    {
        f.DataSource = "";
        if (!f.OvrPluginInitialized || !f.FaceTrackingEnabled) return;
        try
        {
            f.StateApiProbed = true;
            f.StateApiOk = OVRPlugin.GetFaceState2(OVRPlugin.Step.Render, -1, ref _faceProbeState);
            if (!f.StateApiOk) return;
            f.StateStatusValid = _faceProbeState.Status.IsValid;
            f.ProviderTime = _faceProbeState.Time;
            if (f.StateStatusValid) f.DataSource = _faceProbeState.DataSource == OVRPlugin.FaceTrackingDataSource.Audio ? "audio" : "visual";
        }
        catch (Exception) { f.StateApiProbed = false; }
    }

    // One live face reading for the current Unity frame (schema v17): ONE OVRPlugin.GetFaceState2 call per row supplies, from the same state, the API result, Status.IsValid, the data source, the provider
    // time and every weight - so the data source recorded with a closure value is the source of exactly that sample, and nothing is cached across rows. OVRFaceExpressions still owns STARTING face
    // tracking (OnEnable) and the permission flow; this read does not depend on its per-frame Update having run.
    FaceReading ReadFace()
    {
        if (testFaceReading != null) { var t = testFaceReading(); t.Queried = true; return t; }
        var r = new FaceReading { Frame = Time.frameCount, DataSource = "" };
        if (testEyeClosure != null)   // legacy seam: closure pair only
        {
            var c = testEyeClosure(); r.Queried = true;
            if (c.left.HasValue && c.right.HasValue) { r.ExpressionsValid = true; r.ClosedL = c.left.Value; r.ClosedR = c.right.Value; }
            return r;
        }
        if (faceExpressions == null || !faceExpressions.isActiveAndEnabled) return r;
        r.Queried = true;
        try
        {
            if (!OVRPlugin.initialized || !OVRPlugin.faceTracking2Enabled) return r;   // Started stays false: face_state = not_started
            r.Started = true;
            r.ApiOk = OVRPlugin.GetFaceState2(OVRPlugin.Step.Render, -1, ref _faceRowState);
            if (!r.ApiOk) return r;
            r.ProviderTime = _faceRowState.Time;
            if (!_faceRowState.Status.IsValid) return r;
            r.DataSource = _faceRowState.DataSource == OVRPlugin.FaceTrackingDataSource.Audio ? "audio" : "visual";
            float[] w = _faceRowState.ExpressionWeights;
            int iCL = (int)OVRFaceExpressions.FaceExpression.EyesClosedL, iCR = (int)OVRFaceExpressions.FaceExpression.EyesClosedR, iDL = (int)OVRFaceExpressions.FaceExpression.EyesLookDownL, iDR = (int)OVRFaceExpressions.FaceExpression.EyesLookDownR;
            if (w == null || w.Length <= Math.Max(Math.Max(iCL, iCR), Math.Max(iDL, iDR))) return r;   // no eyelid weights in this state: not a measurement
            r.ExpressionsValid = true; r.EyeFollowingValid = _faceRowState.Status.IsEyeFollowingBlendshapesValid;
            r.ClosedL = w[iCL]; r.ClosedR = w[iCR];
            if (r.EyeFollowingValid) { r.LookDownL = w[iDL]; r.LookDownR = w[iDR]; }
        }
        catch (Exception) { r.ExpressionsValid = false; }
        return r;
    }

    // ---- face-tracking start-up (schema v17): the component exists and the first valid Visual frame has been seen BEFORE the first formal gaze row ----
    /// <summary>Outcome of the bounded start-up wait; written to manifest eye_tracking.blink.startup_*.</summary>
    public FaceStartupInfo FaceStartup { get; } = new FaceStartupInfo();
    /// <summary>Test seam (never set in production): replaces the live readiness facts.</summary>
    [NonSerialized] public Func<FaceReadinessFacts> testFaceReadiness;
    OVRPlugin.FaceState _faceRowState, _faceReadyState;

    /// <summary>Creates / starts the face-tracking component without waiting (idempotent). Called at the start of a Part so the runtime has the whole warm-up to start tracking.</summary>
    public void BeginFaceTrackingStartup(string startPoint = "preflight")
    {
        if (!FaceStartup.attempted) { FaceStartup.attempted = true; FaceStartup.requested_t_abs = CaptureClock.TAbs; FaceStartup.start_point = startPoint; }
        if (testFaceReadiness != null) return;
        bool had = faceExpressions != null;
        EnsureFaceExpressionsLive();
        if (!had && faceExpressions != null && FaceAutoCreated) FaceStartup.component_created_by_gate = true;
    }

    void EnsureFaceExpressionsLive()
    {
        var f = new BlinkFacts();
        try
        {
            f.OvrPluginInitialized = OVRPlugin.initialized;
            if (f.OvrPluginInitialized) { f.FaceTrackingSupported = OVRPlugin.faceTracking2Supported; f.FaceTrackingEnabled = OVRPlugin.faceTracking2Enabled; }
            f.PermissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.FaceTracking);
        }
        catch (Exception) { }
        if (faceExpressions == null && !_faceSearched) { _faceSearched = true; faceExpressions = FindObjectOfType<OVRFaceExpressions>(); }
        EnsureFaceExpressions(ref f);
    }

    /// <summary>All readiness facts, read live (no 1 Hz cache).</summary>
    FaceReadinessFacts ReadFaceReadiness()
    {
        if (testFaceReadiness != null) return testFaceReadiness();
        var f = new FaceReadinessFacts { DataSource = "" };
        try
        {
            f.OvrInit = OVRPlugin.initialized;
            if (f.OvrInit) { f.Supported = OVRPlugin.faceTracking2Supported; f.Started = OVRPlugin.faceTracking2Enabled; }
            f.PermissionGranted = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.FaceTracking);
        }
        catch (Exception) { }
        f.ComponentCreated = faceExpressions != null; f.ComponentEnabled = f.ComponentCreated && faceExpressions.isActiveAndEnabled;
        if (f.OvrInit && f.Started)
        {
            try
            {
                f.ApiOk = OVRPlugin.GetFaceState2(OVRPlugin.Step.Render, -1, ref _faceReadyState);
                if (f.ApiOk)
                {
                    f.StatusValid = _faceReadyState.Status.IsValid;
                    if (f.StatusValid)
                    {
                        f.DataSource = _faceReadyState.DataSource == OVRPlugin.FaceTrackingDataSource.Audio ? "audio" : "visual";
                        float[] w = _faceReadyState.ExpressionWeights; int i1 = (int)OVRFaceExpressions.FaceExpression.EyesClosedL, i2 = (int)OVRFaceExpressions.FaceExpression.EyesClosedR;
                        f.EyeClosureAvailable = w != null && w.Length > Math.Max(i1, i2);
                    }
                }
            }
            catch (Exception) { f.ApiOk = false; }
        }
        return f;
    }

    /// <summary>
    /// Bounded readiness wait: ends when a valid Visual frame with eyelid weights is observed, when the runtime is permanently unable (no runtime / unsupported), or after maxWaitSeconds - never unbounded.
    /// Writes NOTHING (no gaze row, no face sample): it only observes. The outcome (including the precise unmet requirement) is in FaceStartup; the policy for a failure (continue / block) is the caller's.
    /// </summary>
    public IEnumerator AwaitFaceTrackingReady(float maxWaitSeconds, string startPoint = "preflight")
    {
        BeginFaceTrackingStartup(startPoint);
        FaceStartup.max_wait_s = maxWaitSeconds; FaceStartup.ready = false; FaceStartup.failure_reason = "";
        double t0 = CaptureClock.TAbs, nextEnsure = t0 + 0.5; FaceReadinessStage stage;
        while (true)
        {
            FaceReadinessFacts f = ReadFaceReadiness(); stage = FaceReadiness.Evaluate(in f); FaceStartup.frames_polled++;
            double now = CaptureClock.TAbs;
            if (stage == FaceReadinessStage.Ready) { FaceStartup.ready = true; FaceStartup.ready_t_abs = now; FaceStartup.data_source_at_ready = f.DataSource ?? ""; break; }
            if (FaceReadiness.IsPermanent(stage)) { FaceStartup.failure_reason = FaceReadiness.Name(stage) + ":permanent"; break; }
            if (now - t0 >= maxWaitSeconds) { FaceStartup.failure_reason = FaceReadiness.Name(stage) + ":timeout_after_" + (now - t0).ToString("F2", CultureInfo.InvariantCulture) + "s"; break; }
            if (now >= nextEnsure) { nextEnsure = now + 0.5; if (testFaceReadiness == null) EnsureFaceExpressionsLive(); }   // bounded re-start of a component that disabled itself (permission pending)
            yield return null;
        }
        FaceStartup.stage = FaceReadiness.Name(stage); FaceStartup.wait_s = CaptureClock.TAbs - t0;
        if (!FaceStartup.ready) Debug.LogWarning("[GazeRecorder] face tracking is NOT ready before the first gaze row: " + FaceStartup.failure_reason + " (no eyelid values will be recorded until it becomes available; blanks, never zeros)");
        else if (enableDebugLogs) Debug.Log("[GazeRecorder] face tracking ready after " + FaceStartup.wait_s.ToString("F2", CultureInfo.InvariantCulture) + " s (" + FaceStartup.data_source_at_ready + ")");
    }

    IEnumerator SamplingLoop()
    {
        float interval = (sampleRate > 0f) ? (1f / sampleRate) : (1f / 30f);
        if (Scheduler == null) Scheduler = new SampleScheduler(interval); else Scheduler.SetInterval(interval);
        Scheduler.Reset();
        _noValidSince = double.NaN;

        while (true)
        {
            // Revision 2.3 scope gate: per-Part gazeCaptureEnabled AND an open trial AND no End-Panel hard
            // stop (DataCaptureManager.GazeCaptureActive). With gazeCaptureEnabled=false for a Part -> no
            // file, no rows for that Part (matches the shipped Part1Config default).
            if (dataCapture != null && !dataCapture.GazeCaptureActive)
            {
                Sampling?.BreakEpoch();   // intentional gap: capture window closed (trial not open / End panel / Part has gaze capture off)
                Scheduler.Reset();
                _noValidSince = double.NaN;
                yield return new WaitForSecondsRealtime(interval);
                continue;
            }

            if ((leftEyeAnchor == null || rightEyeAnchor == null) && validateProviderOrigin && _anchorSearches < 8) ResolveEyeAnchors();

            // Acquisition context (cheap, no allocation): the provider probe is only trusted while the lifecycle says it is running and the last query succeeded.
            EyeTrackingLifecycle lc = Lifecycle;
            // Provider validity of THIS row. Source 1 (preferred): the provider's own per-eye IsValid from ONE OVRPlugin.GetEyeGazesState read taken now (the same call OVREyeGaze.Update makes; the result is reused by
            // PipelineColumns, so there is still one read per row). It does not depend on the lifecycle state: in a session whose lifecycle stayed 'start_failed' while the provider was in fact enabled and delivering,
            // the lifecycle probe was never available, the flags were blank and validity fell back to a possibly stale OVREyeGaze.Confidence. Source 2: the lifecycle probe (Running / TrackingUnavailable only).
            // Neither available = provider validity UNKNOWN (blank flags; the sample is not accepted as an eye sample, see ReadEye).
            _rowRawOk = ReadRawGaze(out _rowRaw);
            bool rawKnown = requireProviderValidity && _rowRawOk;
            bool lifecycleKnown = requireProviderValidity && lc != null && lc.ProviderProbeAvailable &&
                                  (lc.State == EyeTrackingState.Running || lc.State == EyeTrackingState.TrackingUnavailable);
            bool probeKnown = rawKnown || lifecycleKnown;
            bool leftProviderValid = rawKnown ? _rowRaw.left.valid : (lc != null && lc.LeftProviderValid);
            bool rightProviderValid = rawKnown ? _rowRaw.right.valid : (lc != null && lc.RightProviderValid);
            bool hostBlocked = blockEyeSamplesOnHostConflict && lifecycleController != null && lifecycleController.HostConflict.Length > 0;
            ViewFrameSample vf = default; bool vfOk = false;
            if (recordViewFrame)
            {
                ViewProbe.Resolve(viewCamera, centerEyeAnchor, leftEyeAnchor, rightEyeAnchor, groundLayerMask, headTransform);
                vfOk = ViewProbe.Read(out vf);
                if (vfOk)
                {
                    ViewSamples++; ViewMinusCentre.Add(vf.viewMinusCentre);
                    if (vf.groundOk)
                    {
                        GroundSamples++; double h = vf.hmdPos.y - vf.groundY; if (double.IsNaN(HeightFirst)) HeightFirst = h;
                        if (double.IsNaN(HeightMin) || h < HeightMin) HeightMin = h; if (double.IsNaN(HeightMax) || h > HeightMax) HeightMax = h; HeightSum += h;
                    }
                    else GroundUnavailableSamples++;
                }
            }
            EyeSample left = ReadEye(leftEyeGaze, testLeftEye, probeKnown, leftProviderValid, hostBlocked, leftEyeAnchor, vfOk ? vf.leftWorld : (Vector3?)null);
            EyeSample right = ReadEye(rightEyeGaze, testRightEye, probeKnown, rightProviderValid, hostBlocked, rightEyeAnchor, vfOk ? vf.rightWorld : (Vector3?)null);
            bool leftInjected = testLeftEye != null && testLeftEye.active && !testLeftEye.applyProviderGate, rightInjected = testRightEye != null && testRightEye.active && !testRightEye.applyProviderGate;

            bool combinedValid;
            float combinedConfidence;
            Vector3 combinedOrigin;
            Vector3 combinedDirection;
            string gazeRaySource;

            if (left.valid && right.valid)
            {
                combinedValid = true;
                combinedConfidence = (left.confidence + right.confidence) * 0.5f;
                combinedOrigin = (left.origin + right.origin) * 0.5f;
                Vector3 dirSum = left.direction + right.direction;
                combinedDirection = (dirSum.sqrMagnitude > 1e-6f) ? dirSum.normalized : left.direction;
                gazeRaySource = "both";
            }
            else if (left.valid)
            {
                combinedValid = true;
                combinedConfidence = left.confidence;
                combinedOrigin = left.origin;
                combinedDirection = left.direction;
                gazeRaySource = "left";
            }
            else if (right.valid)
            {
                combinedValid = true;
                combinedConfidence = right.confidence;
                combinedOrigin = right.origin;
                combinedDirection = right.direction;
                gazeRaySource = "right";
            }
            else
            {
                combinedValid = false;
                combinedConfidence = 0f;
                // The head-forward fallback ray starts at the RENDERED view (what the participant sees from) when it is available, else at the head transform as before.
                combinedOrigin = vfOk ? vf.hmdPos : (headTransform != null ? headTransform.position : transform.position);
                combinedDirection = vfOk ? vf.hmdRot * Vector3.forward : (headTransform != null ? headTransform.forward : transform.forward);
                gazeRaySource = "head_fallback";
            }

            // Combined confidence (schema v11): the mean of the contributing valid eyes' RAW confidences, defined only when each contributing eye has one; blank for the head-fallback ray.
            bool combinedConfAvailable = GazeConfidence.TryCombine(left.valid, left.confidenceAvailable, left.confidence, right.valid, right.confidenceAvailable, right.confidence, out float combinedConfOut);
            if (left.confidenceAvailable) { ConfidenceInfo.left.Observe(left.confidence); } else ConfidenceInfo.rows_confidence_unavailable_left++;
            if (right.confidenceAvailable) { ConfidenceInfo.right.Observe(right.confidence); } else ConfidenceInfo.rows_confidence_unavailable_right++;

            // ROI raycast: ALWAYS performed (subject to enableROIRaycast). Uses the head-forward fallback
            // ray when eye tracking is invalid. combined_valid is NOT affected by this. The ray that was actually cast is recorded in the
            // ray_* columns together with ray_is_eye_gaze, so a hit produced by the fallback can never be mistaken for an eye-gaze fixation.
            bool rayIsEye = combinedValid;
            bool didHit = false;
            RaycastHit hit = default;
            if (enableROIRaycast)
            {
                didHit = Physics.Raycast(combinedOrigin, combinedDirection, out hit, roiMaxDistance, roiLayerMask);
            }

            // Legacy columns keep their EXACT previous meaning: only filled when eye gaze was valid.
            string roiName = "";
            float roiDistance = float.NaN;
            if (didHit && combinedValid)
            {
                roiName = SafeCsvField(hit.collider != null ? hit.collider.name : hit.transform.name);
                roiDistance = hit.distance;
            }

            AoiClassifier.HitInfo aoi = AoiClassifier.Classify(
                didHit, didHit ? hit.collider : null, experimentManager, dataCapture);

            // UI (Canvas) gaze channel: a SEPARATE test of the same ray against the registered Canvases. The physical result above is not touched by it. Runs only for eye-gaze rows (see uiRaycastOnHeadFallback);
            // a failure here can never stop the primary stream.
            GazeUiHit uiHit = default; bool uiEvaluated = false;
            if (enableUiGaze && (combinedValid || uiRaycastOnHeadFallback))
            {
                uiEvaluated = true;
                try
                {
                    var uo = GazeUiRaycaster.Options.Default;
                    uo.maxDistance = uiMaxDistance > 0f ? uiMaxDistance : roiMaxDistance; uo.minVisibleAlpha = uiMinVisibleAlpha;
                    uo.requireGraphicRaycaster = uiRequireGraphicRaycaster; uo.supportScreenSpaceCamera = uiSupportScreenSpaceCamera; uo.fallbackCamera = uiEventCamera;
                    UiRaycaster.Raycast(combinedOrigin, combinedDirection, in uo, out uiHit);
                }
                catch (Exception e)
                {
                    uiHit = default;
                    if (!_uiErrorLogged) { _uiErrorLogged = true; Debug.LogWarning("[GazeRecorder] UI gaze raycast failed (no UI hit recorded; physical gaze is unaffected): " + e.Message); }
                }
            }

            Vector3 headPos = headTransform != null ? headTransform.position : Vector3.zero;
            Quaternion headRot = headTransform != null ? headTransform.rotation : Quaternion.identity;

            double tAbs = CaptureClock.TAbs;

            // Blink (schema v11): only from a real eye-closure signal. Gaze validity / confidence / tracking loss are never read as a blink.
            float? cl = null, cr = null; string blinkState = "";
            RefreshBlinkCapability(tAbs);
            RefreshTrackingSpaceProbe(tAbs);
            RefreshXrFacts(tAbs);
            // The face signal is read LIVE for every row (a 1 Hz cached status must not blank the first second after the signal appears, nor keep a value after it vanishes).
            FaceReading fr = ReadFace();
            BlinkFacts bfNow = BlinkFactsLast;
            bool seam = testFaceReading != null || testEyeClosure != null;   // tests inject a live reading: the runtime facts do not apply to it
            // Schema v17: "started" and the data source are read LIVE in this row's frame (ReadFace), not taken from the <=1 s old probe: a source switch Visual -> Audio is seen on the row it happens.
            bool fInit = seam || bfNow.OvrPluginInitialized, fSupported = seam || bfNow.FaceTrackingSupported, fStarted = seam || fr.Started;
            string fSource = !string.IsNullOrEmpty(fr.DataSource) ? fr.DataSource : (seam ? (bfNow.DataSource ?? "") : "");
            string faceState = FaceClosure.Classify(fInit, fSupported, fStarted, fSource, in fr);
            FaceStateLast = faceState;
            LastRowFrame = Time.frameCount;
            bool faceCurrent = FaceClosure.IsCurrent(faceState);
            FaceSignal.Observe(faceCurrent, tAbs);
            if (faceCurrent && fr.EyeFollowingValid) RowsFaceEyeFollowing++;
            if (faceState == FaceStateNames.AudioOnly) RowsFaceAudioOnly++;
            if (BlinkStatus == BlinkCapability.Available || fr.Queried)
            {
                float? rl = null, rr = null;
                if (faceCurrent) { FaceClosure.Recover(in fr, out float xl, out float xr); rl = xl; rr = xr; }
                cl = faceCurrent ? (float?)fr.ClosedL : null; cr = faceCurrent ? (float?)fr.ClosedR : null;
                BlinkDetector.Result br = Blink.Update(tAbs, rl, rr);
                if (dataCapture != null)
                {
                    if (br.Started) dataCapture.RecordBlinkEvent(CaptureConstants.EvtBlinkStart, br.StartT, null, "");
                    if (br.Ended) dataCapture.RecordBlinkEvent(CaptureConstants.EvtBlinkEnd, br.EndT, br.Duration, br.Truncated ? "truncated_by_missing_data" : "");
                }
                blinkState = (rl.HasValue && rr.HasValue) ? (Blink.InBlink ? "1" : "0") : "";
            }
            string[] faceCols = FaceColumns.Values(faceState, fInit, fStarted, fSource, in fr);
            double tRel = tAbs - recordingStartRealtime;
            long unixMs = CaptureClock.TUnixMs;
            string sessionId = (dataCapture != null && !string.IsNullOrEmpty(dataCapture.SessionId))
                ? dataCapture.SessionId : (subjectId + "_" + trialId);

            Vector3 hp = didHit ? hit.point : Vector3.zero;

            TrySubscribeAnchors();
            string pipelineCols = PipelineColumns(tAbs, vfOk, in vf, in left, in right, gazeRaySource);

            // ---------------- schema v17: the primary row (PrimaryColumns, 61) and the diagnostics row (DiagnosticColumns, 85) are built from the SAME values of this sample ----------------
            string rayState = lc != null ? EyeTrackingNames.Of(lc.State) : "";
            string originSource = EyeGazeOrigin.CombinedName(left.valid, left.originSource, right.valid, right.originSource);
            string row = string.Join(",", new string[]
            {
                // identification and time
                F4(tAbs), F4(tRel), Inv(Time.frameCount), SafeCsvField(sessionId), SafeCsvField(subjectId), SafeCsvField(trialId),
                dataCapture != null ? SafeCsvField(dataCapture.CurrentPart) : "", dataCapture != null ? SafeCsvField(dataCapture.CurrentPhase) : "",
                // validity / provenance (an invalid / unavailable vector is BLANK, never a fabricated default; validity is decided ONLY by combined_valid / the blanks)
                combinedValid ? "1" : "0", gazeRaySource, originSource, rayState,
                V3(left.valid, left.origin), V3(left.valid, left.direction), V3(right.valid, right.origin), V3(right.valid, right.direction),
                V3(combinedValid, combinedOrigin), V3(combinedValid, combinedDirection),
                // rendered camera
                V3(vfOk, vf.hmdPos), vfOk ? F4(vf.hmdRot.x) : "", vfOk ? F4(vf.hmdRot.y) : "", vfOk ? F4(vf.hmdRot.z) : "", vfOk ? F4(vf.hmdRot.w) : "",
                // gaze-ray interaction
                didHit ? "1" : "0", didHit ? F4(hp.x) : "", didHit ? F4(hp.y) : "", didHit ? F4(hp.z) : "", float.IsNaN(roiDistance) ? "" : F3(roiDistance),
                SafeCsvField(aoi.colliderName), SafeCsvField(aoi.objectName), didHit ? Inv(aoi.instanceId) : "", aoi.layer >= 0 ? Inv(aoi.layer) : "", SafeCsvField(aoi.tag), aoi.semanticClass,
                SafeCsvField(aoi.buildingId), SafeCsvField(aoi.npcId), SafeCsvField(aoi.npcSpawnInstanceId), aoi.isInteractable, aoi.isCurrentTarget, SafeCsvField(aoi.assignedDestination),
                // ground reference
                (vfOk && vf.groundOk) ? F4(vf.groundY) : "", (vfOk && vf.groundOk) ? F4(vf.hmdPos.y - vf.groundY) : "",
                (vfOk && vf.groundOk) ? F4(vf.leftWorld.y - vf.groundY) : "", (vfOk && vf.groundOk) ? F4(vf.rightWorld.y - vf.groundY) : "",
                // eyelid closure (recovered) + compact validity
                faceCols[6], faceCols[7], FaceClosure.IsCurrent(faceState) ? "1" : "0"
            });

            string diagRow = recordDiagnostics ? string.Join(",", new string[]
            {
                F4(tAbs), Inv(Time.frameCount), SafeCsvField(sessionId),
                Inv(unixMs),
                pipelineCols,   // the 37 v14 provider-pipeline columns, pre-joined
                GazeConfidence.Format(left.confidence, left.confidenceAvailable), left.valid ? "1" : "0", (probeKnown && !leftInjected) ? (leftProviderValid ? "1" : "0") : "",
                GazeConfidence.Format(right.confidence, right.confidenceAvailable), right.valid ? "1" : "0", (probeKnown && !rightInjected) ? (rightProviderValid ? "1" : "0") : "",
                GazeConfidence.Format(combinedConfOut, combinedConfAvailable),
                F4(headPos.x), F4(headPos.y), F4(headPos.z), F4(headRot.x), F4(headRot.y), F4(headRot.z), F4(headRot.w),
                V3(vfOk, vf.leftLocal), V3(vfOk, vf.rightLocal), V3(vfOk, vf.leftWorld), V3(vfOk, vf.rightWorld),
                faceCols[0], faceCols[1], faceCols[2], faceCols[3], faceCols[4], faceCols[5],
                cl.HasValue ? F4(cl.Value) : "", cr.HasValue ? F4(cr.Value) : "",   // RAW SDK EyesClosedL / R (not recovered)
                faceCols[8], faceCols[9], (FaceClosure.IsCurrent(faceState) && fr.ProviderTime > 0) ? fr.ProviderTime.ToString("F6", CultureInfo.InvariantCulture) : "",
                blinkState,
                combinedValid ? ",," : V3(true, combinedOrigin), combinedValid ? ",," : V3(true, combinedDirection)
            }) : null;

            string uiRow = null; string uiPrimaryClass = null; bool uiBehindWorld = false;
            if (enableUiGaze && uiWriter != null)
                uiRow = BuildUiRow(tAbs, sessionId, uiEvaluated, in uiHit, didHit, didHit ? hit.distance : 0f, didHit ? hit.collider : null, in aoi, out uiPrimaryClass, out uiBehindWorld);

            if (WriteRow(row))
            {
                if (diagRow != null) WriteDiagRow(diagRow);
                if (uiRow != null && WriteUiRow(uiRow))
                {
                    UiRowsWritten++;
                    if (!uiEvaluated) UiRowsNotEvaluated++; else if (uiHit.hit) UiRowsWithHit++;
                    if (uiBehindWorld) UiRowsBehindWorldHit++;
                    switch (uiPrimaryClass)
                    {
                        case GazeTargetResolver.ClsUi: UiPrimaryCanvasUi++; break;
                        case GazeTargetResolver.ClsWorld: UiPrimaryWorldObject++; break;
                        case GazeTargetResolver.ClsUncertain: UiPrimaryUncertain++; break;
                        default: UiPrimaryNoHit++; break;
                    }
                }
                RowsWritten++;
                if (double.IsNaN(FirstRowT)) FirstRowT = tAbs;
                LastRowT = tAbs;
                if (combinedValid) RowsValidEye++; else { RowsFallback++; if (lc == null || lc.State != EyeTrackingState.Running) RowsFallbackNotAcquiring++; }
                if (left.valid) RowsLeftValid++;
                if (right.valid) RowsRightValid++;
                // Longest stretch without a valid eye INSIDE one capture epoch (the reference restarts after a pause / closed capture window).
                if (double.IsNaN(_noValidSince)) _noValidSince = tAbs;
                if (combinedValid)
                {
                    LongestNoValidEyeSeconds = Math.Max(LongestNoValidEyeSeconds, tAbs - _noValidSince);
                    _noValidSince = tAbs;
                    if (double.IsNaN(FirstValidRowT)) FirstValidRowT = tAbs;
                    LastValidRowT = tAbs;
                }
                if (dataCapture != null)
                {
                    dataCapture.FrameStats.TakeWindow(0, out double fMax, out int fCount);   // frames seen since the previous gaze sample (the evidence that separates frame / scheduler / writer gaps)
                    Sampling?.OnSampleWithEvidence(tAbs, fMax, fCount, gazeWriter != null ? gazeWriter.LastCommitSeconds : double.NaN);
                }
                else Sampling?.OnSampleWithFrame(tAbs, Time.unscaledDeltaTime);   // standalone recorder (no capture manager): frame duration only
                // only a SUCCESSFULLY written row is a sample
            }

            // Absolute deadline grid: the overshoot of one sample shortens the next wait, so the long-run rate converges to the nominal one when the frame rate allows it.
            yield return new WaitForSecondsRealtime((float)Scheduler.WaitAfterSample(CaptureClock.TAbs));
        }
    }

    bool _uiErrorLogged;

    // The gaze_ui_hits.csv row of one gaze row (UiHitColumns). Raw UI hit and raw world-hit facts are recorded independently; the primary target is the GazeTargetResolver policy applied to them.
    string BuildUiRow(double tAbs, string sessionId, bool uiEvaluated, in GazeUiHit ui, bool worldHit, float worldDistance, Collider worldCollider, in AoiClassifier.HitInfo aoi, out string primaryClass, out bool uiBehindWorld)
    {
        bool uiHit = uiEvaluated && ui.hit;
        // the occlusion question only exists when both channels hit and the physical surface lies in FRONT of the UI element (nearer to the eye by more than the coplanar tolerance)
        bool occluderChecked = uiHit && worldHit && ui.distance > worldDistance + GazeTargetResolver.CoplanarEpsilon;
        bool occluder = occluderChecked && GazeTargetResolver.IsEstablishedOccluder(worldCollider);
        GazeTargetResolver.Primary pr = GazeTargetResolver.Resolve(uiEvaluated, uiHit, ui.distance, worldHit, worldDistance, occluder);
        primaryClass = pr.cls; uiBehindWorld = occluderChecked;
        string primaryId = pr.cls == GazeTargetResolver.ClsUi ? ui.elementId
            : (pr.cls == GazeTargetResolver.ClsWorld ? (!string.IsNullOrEmpty(aoi.buildingId) ? aoi.buildingId : (!string.IsNullOrEmpty(aoi.npcId) ? aoi.npcId : aoi.objectName)) : "");
        return string.Join(",", new string[]
        {
            F4(tAbs), Inv(Time.frameCount), SafeCsvField(sessionId),
            uiEvaluated ? "1" : "0", uiEvaluated ? "" : "head_fallback_ray", uiEvaluated ? (ui.hit ? "1" : "0") : "",
            uiHit ? SafeCsvField(ui.canvasId) : "", uiHit ? SafeCsvField(ui.canvasName) : "", uiHit ? ui.renderMode : "", uiHit ? Inv(ui.canvasSortingOrder) : "",
            uiHit ? SafeCsvField(ui.elementId) : "", uiHit ? SafeCsvField(ui.elementName) : "", uiHit ? ui.category : "", uiHit ? SafeCsvField(ui.roiLabel) : "", uiHit ? (ui.hasGazeElement ? "1" : "0") : "", uiHit ? ui.interactable : "",
            V3(uiHit, ui.point), uiHit ? F3(ui.distance) : "", uiHit ? Inv(ui.graphicsUnderRay) : "",
            worldHit ? "1" : "0", worldHit ? F3(worldDistance) : "", worldHit ? Inv(aoi.instanceId) : "", worldHit ? aoi.semanticClass : "", occluderChecked ? (occluder ? "1" : "0") : "",
            pr.cls, pr.basis, SafeCsvField(primaryId), AoiClassifier.PrimaryRoi(pr.cls, ui.roiLabel, aoi.semanticClass)
        });
    }

    // The companion row of an accepted gaze row (one-to-one). A failure here never stops the primary stream; it is counted by the writer and reported in the manifest.
    bool WriteUiRow(string row)
    {
        if (uiWriter == null || _closedForSession) return false;
        long before = uiWriter.RowsWritten;
        uiWriter.WriteLine(row);
        return uiWriter.RowsWritten > before;
    }

    // Blank when the vector is invalid / unavailable; otherwise three fixed-4 components (zeros are valid values).
    static string V3(bool valid, Vector3 v) => valid ? F4(v.x) + "," + F4(v.y) + "," + F4(v.z) : ",,";

    // The diagnostics row of an accepted gaze row (one-to-one). A failure here never stops the primary stream; it is counted by the writer and reported in the manifest.
    void WriteDiagRow(string row)
    {
        if (diagWriter == null || _closedForSession) return;
        diagWriter.WriteLine(row);
    }

    // Rows go through the shared CaptureCsvWriter: lazy header, failure state + counters + bounded recovery (F-13). Returns true only if the row reached the file.
    bool WriteRow(string row)
    {
        if (gazeWriter == null) return false;
        if (_closedForSession) { LateRowsRejected++; return false; }
        long before = gazeWriter.RowsWritten;
        gazeWriter.WriteLine(row);
        return gazeWriter.RowsWritten > before;
    }

    // M2: the session's capture ended (DataCaptureManager.FinalizeCapture). Rows accepted so far are already on disk (every row is its own append); sampling stops for good, the stream is
    // closed and any later row - or a later StartRecording - is refused and only counted.
    bool _closedForSession;
    public long LateRowsRejected { get; private set; }
    public bool ClosedForSession => _closedForSession;
    public void CloseForSession()
    {
        if (_closedForSession) return;
        StopRecording();
        _closedForSession = true;
        if (gazeWriter != null) gazeWriter.Close();
        if (diagWriter != null) diagWriter.Close();
        if (uiWriter != null) uiWriter.Close();
    }

    // F-02: every number in gaze.csv is formatted with the invariant culture. The row is comma-joined without quoting, so a
    // culture-specific decimal comma (e.g. fr-FR) or minus sign would add columns / corrupt values. Precision is unchanged.
    static string F3(float v)   => v.ToString("F3", CultureInfo.InvariantCulture);
    static string F4(float v)   => v.ToString("F4", CultureInfo.InvariantCulture);
    static string F4(double v)  => v.ToString("F4", CultureInfo.InvariantCulture);
    static string Inv(long v)   => v.ToString(CultureInfo.InvariantCulture);

    // Existing convention: commas / line breaks become spaces. A field containing a double quote is additionally wrapped in quotes
    // with embedded quotes doubled (RFC 4180), so a name that starts with or contains '"' can no longer merge columns in a CSV parser.
    string SafeCsvField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
        if (s.IndexOf('"') >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}