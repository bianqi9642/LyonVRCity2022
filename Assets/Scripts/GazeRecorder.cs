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
    [Tooltip("OVREyeGaze component configured with Eye = Left. Typically a child of CenterEyeAnchor.")]
    public OVREyeGaze leftEyeGaze;

    [Tooltip("OVREyeGaze component configured with Eye = Right. Typically a child of CenterEyeAnchor.")]
    public OVREyeGaze rightEyeGaze;

    [Tooltip("Minimum OVREyeGaze.Confidence (0..1) for a single eye's sample to be treated as valid. " +
             "Meta's SDK does not expose a separate boolean validity flag on OVREyeGaze - confidence " +
             "is the documented signal to threshold on.")]
    [Range(0f, 1f)]
    public float minConfidenceForValid = 0.5f;

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
    public class EyeInjection { public bool active = true; public float confidence; public Vector3 origin; public Vector3 direction = Vector3.forward; }
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

        // Android runtime eye-tracking permission. Meta XR SDK 201: OVRPermissionsRequester is a static
        // requester that does NOT require an OVRManager in the scene. No effect outside an Android build.
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.EyeTracking))
            {
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.EyeTracking });
            }
        }
        catch (Exception e)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[GazeRecorder] Eye-tracking permission request failed: {e.Message}. " +
                    "Verify OVRPermissionsRequester exists in the installed Meta XR SDK version.");
        }
#endif

        if (enableDebugLogs && (leftEyeGaze == null || rightEyeGaze == null))
        {
            Debug.LogWarning("[GazeRecorder] leftEyeGaze and/or rightEyeGaze is not assigned. This recorder " +
                "will still run but will record all-invalid samples until both are assigned.");
        }
    }

    void Start()
    {
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
        if (string.IsNullOrEmpty(subjectId) && sessionController != null) subjectId = sessionController.participantId;
        if (string.IsNullOrEmpty(subjectId)) subjectId = "UNKNOWN_SUBJECT";

        if (string.IsNullOrEmpty(trialId) && sessionController != null) trialId = sessionController.selectedPart.ToString();
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
            "hit_assigned_destination"
        });
    }

    struct EyeSample
    {
        public bool valid;
        public float confidence;
        public Vector3 origin;
        public Vector3 direction;
    }

    EyeSample ReadEye(OVREyeGaze eyeGaze, EyeInjection injected)
    {
        EyeSample sample = new EyeSample
        {
            valid = false,
            confidence = 0f,
            origin = Vector3.zero,
            direction = Vector3.forward
        };

        if (injected != null && injected.active)
        {
            sample.confidence = injected.confidence;
            sample.origin = injected.origin;
            sample.direction = injected.direction;
            sample.valid = sample.confidence >= minConfidenceForValid;
            return sample;
        }

        if (eyeGaze == null || !eyeGaze.isActiveAndEnabled) return sample;

        sample.confidence = eyeGaze.Confidence;
        sample.origin = eyeGaze.transform.position;
        sample.direction = eyeGaze.transform.forward;
        sample.valid = sample.confidence >= minConfidenceForValid;
        return sample;
    }

    IEnumerator SamplingLoop()
    {
        float interval = (sampleRate > 0f) ? (1f / sampleRate) : (1f / 30f);

        while (true)
        {
            // Revision 2.3 scope gate: per-Part gazeCaptureEnabled AND an open trial AND no End-Panel hard
            // stop (DataCaptureManager.GazeCaptureActive). With gazeCaptureEnabled=false for a Part -> no
            // file, no rows for that Part (matches the shipped Part1Config default).
            if (dataCapture != null && !dataCapture.GazeCaptureActive)
            {
                Sampling?.BreakEpoch();   // intentional gap: capture window closed (trial not open / End panel / Part has gaze capture off)
                yield return new WaitForSecondsRealtime(interval);
                continue;
            }

            EyeSample left = ReadEye(leftEyeGaze, testLeftEye);
            EyeSample right = ReadEye(rightEyeGaze, testRightEye);

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
                combinedOrigin = headTransform != null ? headTransform.position : transform.position;
                combinedDirection = headTransform != null ? headTransform.forward : transform.forward;
                gazeRaySource = "head_fallback";
            }

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
            double tRel = tAbs - recordingStartRealtime;
            long unixMs = CaptureClock.TUnixMs;
            string sessionId = (dataCapture != null && !string.IsNullOrEmpty(dataCapture.SessionId))
                ? dataCapture.SessionId : (subjectId + "_" + trialId);

            Vector3 hp = didHit ? hit.point : Vector3.zero;

            string row = string.Join(",", new string[]
            {
                // ----- existing columns -----
                F4(tRel),
                Inv(unixMs),
                SafeCsvField(subjectId),
                SafeCsvField(trialId),
                F3(left.confidence),
                left.valid ? "1" : "0",
                V3(left.valid, left.origin), V3(left.valid, left.direction),
                F3(right.confidence),
                right.valid ? "1" : "0",
                V3(right.valid, right.origin), V3(right.valid, right.direction),
                combinedValid ? "1" : "0",
                F3(combinedConfidence),
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
                SafeCsvField(aoi.assignedDestination)
            });

            if (WriteRow(row))
            {
                RowsWritten++;
                if (double.IsNaN(FirstRowT)) FirstRowT = tAbs;
                LastRowT = tAbs;
                if (combinedValid) RowsValidEye++; else RowsFallback++;
                if (left.valid) RowsLeftValid++;
                if (right.valid) RowsRightValid++;
                Sampling?.OnSample(tAbs);   // only a SUCCESSFULLY written row is a sample
            }

            yield return new WaitForSecondsRealtime(interval);
        }
    }

    // Blank when the vector is invalid / unavailable; otherwise three fixed-4 components (zeros are valid values).
    static string V3(bool valid, Vector3 v) => valid ? F4(v.x) + "," + F4(v.y) + "," + F4(v.z) : ",,";

    // Rows go through the shared CaptureCsvWriter: lazy header, failure state + counters + bounded recovery (F-13). Returns true only if the row reached the file.
    bool WriteRow(string row)
    {
        if (gazeWriter == null) return false;
        long before = gazeWriter.RowsWritten;
        gazeWriter.WriteLine(row);
        return gazeWriter.RowsWritten > before;
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