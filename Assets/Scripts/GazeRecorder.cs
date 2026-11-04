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
// All comments in English per project convention.

using System;
using System.Collections;
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
    CaptureCsvWriter gazeWriter;
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

    string GetCsvHeader()
    {
        return string.Join(",", new string[]
        {
            // ----- existing columns (DO NOT reorder / rename) -----
            "time_since_start_s",
            "unix_time_ms",
            "subject_id",
            "trial_id",
            "left_confidence",
            "left_valid",
            "left_origin_x", "left_origin_y", "left_origin_z",
            "left_dir_x", "left_dir_y", "left_dir_z",
            "right_confidence",
            "right_valid",
            "right_origin_x", "right_origin_y", "right_origin_z",
            "right_dir_x", "right_dir_y", "right_dir_z",
            "combined_valid",
            "combined_confidence",
            "combined_origin_x", "combined_origin_y", "combined_origin_z",
            "combined_dir_x", "combined_dir_y", "combined_dir_z",
            "head_pos_x", "head_pos_y", "head_pos_z",
            "head_rot_x", "head_rot_y", "head_rot_z", "head_rot_w",
            "roi_hit_name",
            "roi_hit_distance",
            // ----- Revision 2 appended columns -----
            "t_abs",
            "session_id",
            "frame_index",
            "gaze_ray_source",
            "ray_did_hit",
            "hit_point_x", "hit_point_y", "hit_point_z",
            "hit_collider_name",
            "hit_object_name",
            "hit_instance_id",
            "hit_layer",
            "hit_tag",
            "hit_semantic_class",
            "hit_building_id",
            "hit_npc_id",
            "hit_npc_spawn_instance_id",
            "hit_is_interactable",
            // ----- schema v5 appended columns -----
            "part",
            "phase",
            "ray_is_eye_gaze",
            "ray_origin_x", "ray_origin_y", "ray_origin_z",
            "ray_dir_x", "ray_dir_y", "ray_dir_z",
            "hit_is_current_target",
            "hit_assigned_destination",
            // ----- schema v8 appended columns (eye-tracking acquisition) -----
            // eye_tracking_state: lifecycle state at the sample (waiting_for_xr | ... | running | tracking_unavailable ...), blank without a lifecycle controller.
            // left/right_provider_valid: the provider's own per-eye IsValid at the sample (1/0), blank when no provider probe is available (never a guessed 0).
            "eye_tracking_state",
            "left_provider_valid", "right_provider_valid",
            // ----- schema v10 appended column (gaze origin) -----
            // gaze_origin_source: where the recorded eye origin(s) came from - provider | eye_anchor | provider_unchecked | mixed (two valid eyes disagree); blank for the head-fallback ray.
            "gaze_origin_source",
            // ----- schema v11 appended columns (blink signal; ALWAYS blank unless the face-tracking eye-closure signal is available - blank means "not measured", never "eyes open") -----
            // eyes_closed_left / eyes_closed_right: raw FaceExpression.EyesClosedL / R weights 0..1; blink_state: 1 = inside a detected blink, 0 = not, blank = no valid measurement for both eyes.
            "eyes_closed_left", "eyes_closed_right", "blink_state",
            // ----- schema v13 appended columns (view frame; ALL blank when the rendered view / OVR anchors are not resolved, ground columns blank when no ground surface is found) -----
            // hmd_world_*: the RENDERED camera (centre-eye / HMD reference the participant sees from) in Unity world coordinates; hmd_world_rot_*: its rotation.
            // *_eye_head_local_*: the eye anchors in the OVR centre-eye (head) frame - independent of any world height. *_eye_world_*: the eyes in the rendered view frame = view pos + view rot * head-local.
            // ground_y: world Y of the walkable ground surface below the rendered camera; *_height_above_ground = world Y - ground_y (never an assumed eye height).
            "hmd_world_x", "hmd_world_y", "hmd_world_z", "hmd_world_rot_x", "hmd_world_rot_y", "hmd_world_rot_z", "hmd_world_rot_w",
            "left_eye_head_local_x", "left_eye_head_local_y", "left_eye_head_local_z", "right_eye_head_local_x", "right_eye_head_local_y", "right_eye_head_local_z",
            "left_eye_world_x", "left_eye_world_y", "left_eye_world_z", "right_eye_world_x", "right_eye_world_y", "right_eye_world_z",
            "ground_y", "hmd_height_above_ground", "left_eye_height_above_ground", "right_eye_height_above_ground",
            // ----- schema v14 appended columns (per-eye provider pipeline; blank when unavailable, never estimated) -----
            // provider_time_s: OVRPlugin EyeGazesState.Time; provider_time_changed: 1 = a newer provider sample than the previous row, 0 = the previous sample reused.
            // *_raw_tracking_*: the provider eye pose in OVRPlugin tracking space (position, forward direction). *_gaze_head_*: the same pose in the head frame (SDK head-space conversion; independent of the tracking origin,
            // the Camera Offset and the world). The recorded gaze ray (left_/right_/combined_origin, _dir) remains the OVREyeGaze WorldSpace result.
            "provider_time_s", "provider_time_changed",
            "left_raw_tracking_pos_x", "left_raw_tracking_pos_y", "left_raw_tracking_pos_z", "left_raw_tracking_dir_x", "left_raw_tracking_dir_y", "left_raw_tracking_dir_z",
            "right_raw_tracking_pos_x", "right_raw_tracking_pos_y", "right_raw_tracking_pos_z", "right_raw_tracking_dir_x", "right_raw_tracking_dir_y", "right_raw_tracking_dir_z",
            "left_gaze_head_pos_x", "left_gaze_head_pos_y", "left_gaze_head_pos_z", "left_gaze_head_dir_x", "left_gaze_head_dir_y", "left_gaze_head_dir_z",
            "right_gaze_head_pos_x", "right_gaze_head_pos_y", "right_gaze_head_pos_z", "right_gaze_head_dir_x", "right_gaze_head_dir_y", "right_gaze_head_dir_z",
            // provider eye position minus the stereo VIEW anchor, both in head space (metres); WorldSpace result vs (rendered camera * head-space eye): origin metres / direction degrees (should be ~0 within one frame).
            "left_gaze_vs_view_anchor_head_m", "right_gaze_vs_view_anchor_head_m",
            "left_worldspace_vs_composed_origin_m", "right_worldspace_vs_composed_origin_m", "left_worldspace_vs_composed_dir_deg", "right_worldspace_vs_composed_dir_deg",
            // stereo VIEW anchor separation, provider eye separation (head space, both eyes valid), the IPD the runtime holds (OVRPlugin.ipd) - three different quantities, none is a pupil measurement.
            "view_anchor_separation_m", "gaze_head_separation_m", "runtime_user_ipd_m",
            // Unity frame of the last OVRCameraRig.UpdatedAnchors; why the recorded origin is not the provider's (no_valid_eye | provider_exceeds_deviation_gate | provider_origin_non_finite | mixed).
            "anchors_updated_frame", "origin_fallback_reason"
        });
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
        sample.confidenceAvailable = GazeConfidence.AvailableAfterProvider(sample.confidenceAvailable, providerKnown, providerValid);
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
        bool rawOk = ReadRawGaze(out RawGazeState raw);
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
        if (gazeRaySource == "head_fallback") reason = "no_valid_eye";
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
        f.ComponentPresent = faceExpressions != null && faceExpressions.isActiveAndEnabled;
        f.SignalValid = f.ComponentPresent && faceExpressions.ValidExpressions;
        return f;
    }

    void ReadEyeClosure(out float? left, out float? right)
    {
        left = right = null;
        if (testEyeClosure != null) { var t = testEyeClosure(); left = t.left; right = t.right; return; }
        if (faceExpressions == null) return;
        if (faceExpressions.TryGetFaceExpressionWeight(OVRFaceExpressions.FaceExpression.EyesClosedL, out float l)) left = l;
        if (faceExpressions.TryGetFaceExpressionWeight(OVRFaceExpressions.FaceExpression.EyesClosedR, out float r)) right = r;
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
            bool probeKnown = requireProviderValidity && lc != null && lc.ProviderProbeAvailable &&
                              (lc.State == EyeTrackingState.Running || lc.State == EyeTrackingState.TrackingUnavailable);
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
            EyeSample left = ReadEye(leftEyeGaze, testLeftEye, probeKnown, lc != null && lc.LeftProviderValid, hostBlocked, leftEyeAnchor, vfOk ? vf.leftWorld : (Vector3?)null);
            EyeSample right = ReadEye(rightEyeGaze, testRightEye, probeKnown, lc != null && lc.RightProviderValid, hostBlocked, rightEyeAnchor, vfOk ? vf.rightWorld : (Vector3?)null);
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

            Vector3 headPos = headTransform != null ? headTransform.position : Vector3.zero;
            Quaternion headRot = headTransform != null ? headTransform.rotation : Quaternion.identity;

            double tAbs = CaptureClock.TAbs;

            // Blink (schema v11): only from a real eye-closure signal. Gaze validity / confidence / tracking loss are never read as a blink.
            float? cl = null, cr = null; string blinkState = "";
            RefreshBlinkCapability(tAbs);
            RefreshTrackingSpaceProbe(tAbs);
            RefreshXrFacts(tAbs);
            if (BlinkStatus == BlinkCapability.Available)
            {
                ReadEyeClosure(out cl, out cr);
                BlinkDetector.Result br = Blink.Update(tAbs, cl, cr);
                if (dataCapture != null)
                {
                    if (br.Started) dataCapture.RecordBlinkEvent(CaptureConstants.EvtBlinkStart, br.StartT, null, "");
                    if (br.Ended) dataCapture.RecordBlinkEvent(CaptureConstants.EvtBlinkEnd, br.EndT, br.Duration, br.Truncated ? "truncated_by_missing_data" : "");
                }
                blinkState = (cl.HasValue && cr.HasValue) ? (Blink.InBlink ? "1" : "0") : "";
            }
            double tRel = tAbs - recordingStartRealtime;
            long unixMs = CaptureClock.TUnixMs;
            string sessionId = (dataCapture != null && !string.IsNullOrEmpty(dataCapture.SessionId))
                ? dataCapture.SessionId : (subjectId + "_" + trialId);

            Vector3 hp = didHit ? hit.point : Vector3.zero;

            TrySubscribeAnchors();
            string pipelineCols = PipelineColumns(tAbs, vfOk, in vf, in left, in right, gazeRaySource);

            string row = string.Join(",", new string[]
            {
                // ----- existing columns -----
                F4(tRel),
                Inv(unixMs),
                SafeCsvField(subjectId),
                SafeCsvField(trialId),
                GazeConfidence.Format(left.confidence, left.confidenceAvailable),
                left.valid ? "1" : "0",
                V3(left.valid, left.origin), V3(left.valid, left.direction),
                GazeConfidence.Format(right.confidence, right.confidenceAvailable),
                right.valid ? "1" : "0",
                V3(right.valid, right.origin), V3(right.valid, right.direction),
                combinedValid ? "1" : "0",
                GazeConfidence.Format(combinedConfOut, combinedConfAvailable),
                // schema v5 (F-16): an invalid / unavailable vector is BLANK, never a fabricated default. Validity is decided ONLY by the *_valid flags
                // (a valid vector may legitimately contain zeros). The head-forward fallback ray is not written here; see ray_* below.
                V3(combinedValid, combinedOrigin), V3(combinedValid, combinedDirection),
                F4(headPos.x), F4(headPos.y), F4(headPos.z),
                F4(headRot.x), F4(headRot.y), F4(headRot.z), F4(headRot.w),
                roiName,
                float.IsNaN(roiDistance) ? "" : F3(roiDistance),
                // ----- Revision 2 appended columns -----
                F4(tAbs),
                SafeCsvField(sessionId),
                Inv(Time.frameCount),
                gazeRaySource,
                didHit ? "1" : "0",
                didHit ? F4(hp.x) : "", didHit ? F4(hp.y) : "", didHit ? F4(hp.z) : "",
                SafeCsvField(aoi.colliderName),
                SafeCsvField(aoi.objectName),
                didHit ? Inv(aoi.instanceId) : "",
                aoi.layer >= 0 ? Inv(aoi.layer) : "",
                SafeCsvField(aoi.tag),
                aoi.semanticClass,
                SafeCsvField(aoi.buildingId),
                SafeCsvField(aoi.npcId),
                SafeCsvField(aoi.npcSpawnInstanceId),
                aoi.isInteractable,
                // ----- schema v5 appended columns -----
                dataCapture != null ? SafeCsvField(dataCapture.CurrentPart) : "",
                dataCapture != null ? SafeCsvField(dataCapture.CurrentPhase) : "",
                rayIsEye ? "1" : "0",
                F4(combinedOrigin.x), F4(combinedOrigin.y), F4(combinedOrigin.z),
                F4(combinedDirection.x), F4(combinedDirection.y), F4(combinedDirection.z),
                aoi.isCurrentTarget,
                SafeCsvField(aoi.assignedDestination),
                // ----- schema v8 appended columns -----
                lc != null ? EyeTrackingNames.Of(lc.State) : "",
                (probeKnown && !leftInjected) ? (lc.LeftProviderValid ? "1" : "0") : "",
                (probeKnown && !rightInjected) ? (lc.RightProviderValid ? "1" : "0") : "",
                // ----- schema v10 appended column -----
                EyeGazeOrigin.CombinedName(left.valid, left.originSource, right.valid, right.originSource),
                // ----- schema v11 appended columns -----
                cl.HasValue ? F4(cl.Value) : "", cr.HasValue ? F4(cr.Value) : "", blinkState,
                // ----- schema v13 appended columns -----
                V3(vfOk, vf.hmdPos), vfOk ? F4(vf.hmdRot.x) : "", vfOk ? F4(vf.hmdRot.y) : "", vfOk ? F4(vf.hmdRot.z) : "", vfOk ? F4(vf.hmdRot.w) : "",
                V3(vfOk, vf.leftLocal), V3(vfOk, vf.rightLocal),
                V3(vfOk, vf.leftWorld), V3(vfOk, vf.rightWorld),
                (vfOk && vf.groundOk) ? F4(vf.groundY) : "",
                (vfOk && vf.groundOk) ? F4(vf.hmdPos.y - vf.groundY) : "",
                (vfOk && vf.groundOk) ? F4(vf.leftWorld.y - vf.groundY) : "",
                (vfOk && vf.groundOk) ? F4(vf.rightWorld.y - vf.groundY) : "",
                // ----- schema v14 appended columns (37, pre-joined) -----
                pipelineCols
            });

            if (WriteRow(row))
            {
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

    // Blank when the vector is invalid / unavailable; otherwise three fixed-4 components (zeros are valid values).
    static string V3(bool valid, Vector3 v) => valid ? F4(v.x) + "," + F4(v.y) + "," + F4(v.z) : ",,";

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