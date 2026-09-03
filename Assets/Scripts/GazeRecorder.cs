// GazeRecorder.cs
//
// Eye-tracking data recorder for Meta Quest Pro.
//
// Revision 2 (data-capture Architecture C) changes - all ADDITIVE, existing columns unchanged:
//  - Writes into the shared session folder as "gaze.csv" ({session_id}/gaze.csv) when a
//    DataCaptureManager is present. File + header are created LAZILY on the first written row.
//  - Per-sample scope gate: rows are written only while DataCaptureManager.ParticipantCaptureActive is
//    true (Part 2 + Part 3). A Part 1 session therefore produces NO gaze.csv.
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
//
// All comments in English per project convention.

using System;
using System.Collections;
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
    bool isRecording = false;
    string fullPath = "";
    bool headerWritten = false;
    Coroutine samplingCoroutine = null;
    double recordingStartRealtime = 0.0;

    // Revision 2: shared capture context (may be null - the recorder still works, fail-open).
    DataCaptureManager dataCapture;

    void Awake()
    {
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
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= OnExperimentTrialStart;
            experimentManager.OnTrialEnd   -= OnExperimentTrialEnd;
        }
        StopRecording();
    }

    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus && isRecording)
        {
            if (enableDebugLogs)
                Debug.LogWarning("[GazeRecorder] Application paused while recording - stopping recording " +
                    "defensively. Data already written is safe (each row is flushed on write).");
            StopRecording();
        }
    }

    void OnApplicationQuit()
    {
        if (isRecording) StopRecording();
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

        if (string.IsNullOrEmpty(subjectId) && sessionController != null) subjectId = sessionController.participantId;
        if (string.IsNullOrEmpty(subjectId)) subjectId = "UNKNOWN_SUBJECT";

        if (string.IsNullOrEmpty(trialId) && sessionController != null) trialId = sessionController.selectedPart.ToString();
        if (string.IsNullOrEmpty(trialId)) trialId = "UNKNOWN_TRIAL";

        fullPath = BuildFullPath();
        headerWritten = false;
        recordingStartRealtime = Time.realtimeSinceStartupAsDouble;
        isRecording = true;

        if (enableDebugLogs)
            Debug.Log($"[GazeRecorder] Recording started. subject={subjectId} trial={trialId} path={fullPath}");

        samplingCoroutine = StartCoroutine(SamplingLoop());
    }

    /// <summary>Stops the current gaze-recording session, if any. Safe to call multiple times.</summary>
    public void StopRecording()
    {
        if (!isRecording && samplingCoroutine == null) return;

        if (samplingCoroutine != null)
        {
            StopCoroutine(samplingCoroutine);
            samplingCoroutine = null;
        }

        if (isRecording && enableDebugLogs)
            Debug.Log($"[GazeRecorder] Recording stopped. path={fullPath}");

        isRecording = false;
    }

    string BuildFullPath()
    {
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
            "hit_is_interactable"
        });
    }

    struct EyeSample
    {
        public bool valid;
        public float confidence;
        public Vector3 origin;
        public Vector3 direction;
    }

    EyeSample ReadEye(OVREyeGaze eyeGaze)
    {
        EyeSample sample = new EyeSample
        {
            valid = false,
            confidence = 0f,
            origin = Vector3.zero,
            direction = Vector3.forward
        };

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
            // Revision 2 scope gate: only Part 2 + Part 3 are captured. Part 1 -> no file, no rows.
            if (dataCapture != null && !dataCapture.ParticipantCaptureActive)
            {
                yield return new WaitForSecondsRealtime(interval);
                continue;
            }

            if (!headerWritten) WriteHeaderIfNeeded();

            EyeSample left = ReadEye(leftEyeGaze);
            EyeSample right = ReadEye(rightEyeGaze);

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
            // ray when eye tracking is invalid. combined_valid is NOT affected by this.
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

            double tRel = Time.realtimeSinceStartupAsDouble - recordingStartRealtime;
            double tAbs = Time.realtimeSinceStartupAsDouble;
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string sessionId = (dataCapture != null && !string.IsNullOrEmpty(dataCapture.SessionId))
                ? dataCapture.SessionId : (SafeCsvField(subjectId) + "_" + SafeCsvField(trialId));

            Vector3 hp = didHit ? hit.point : Vector3.zero;

            string row = string.Join(",", new string[]
            {
                // ----- existing columns -----
                tRel.ToString("F4"),
                unixMs.ToString(),
                SafeCsvField(subjectId),
                SafeCsvField(trialId),
                left.confidence.ToString("F3"),
                left.valid ? "1" : "0",
                left.origin.x.ToString("F4"), left.origin.y.ToString("F4"), left.origin.z.ToString("F4"),
                left.direction.x.ToString("F4"), left.direction.y.ToString("F4"), left.direction.z.ToString("F4"),
                right.confidence.ToString("F3"),
                right.valid ? "1" : "0",
                right.origin.x.ToString("F4"), right.origin.y.ToString("F4"), right.origin.z.ToString("F4"),
                right.direction.x.ToString("F4"), right.direction.y.ToString("F4"), right.direction.z.ToString("F4"),
                combinedValid ? "1" : "0",
                combinedConfidence.ToString("F3"),
                combinedOrigin.x.ToString("F4"), combinedOrigin.y.ToString("F4"), combinedOrigin.z.ToString("F4"),
                combinedDirection.x.ToString("F4"), combinedDirection.y.ToString("F4"), combinedDirection.z.ToString("F4"),
                headPos.x.ToString("F4"), headPos.y.ToString("F4"), headPos.z.ToString("F4"),
                headRot.x.ToString("F4"), headRot.y.ToString("F4"), headRot.z.ToString("F4"), headRot.w.ToString("F4"),
                roiName,
                float.IsNaN(roiDistance) ? "" : roiDistance.ToString("F3"),
                // ----- Revision 2 appended columns -----
                tAbs.ToString("F4"),
                SafeCsvField(sessionId),
                Time.frameCount.ToString(),
                gazeRaySource,
                didHit ? "1" : "0",
                didHit ? hp.x.ToString("F4") : "", didHit ? hp.y.ToString("F4") : "", didHit ? hp.z.ToString("F4") : "",
                SafeCsvField(aoi.colliderName),
                SafeCsvField(aoi.objectName),
                didHit ? aoi.instanceId.ToString() : "",
                aoi.layer >= 0 ? aoi.layer.ToString() : "",
                SafeCsvField(aoi.tag),
                aoi.semanticClass,
                SafeCsvField(aoi.buildingId),
                SafeCsvField(aoi.npcId),
                SafeCsvField(aoi.npcSpawnInstanceId),
                aoi.isInteractable
            });

            WriteRow(row);

            yield return new WaitForSecondsRealtime(interval);
        }
    }

    void WriteHeaderIfNeeded()
    {
        try
        {
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(fullPath))
            {
                using (StreamWriter writer = new StreamWriter(fullPath, append: false))
                    writer.WriteLine(GetCsvHeader());
            }
            headerWritten = true;
        }
        catch (Exception e)
        {
            if (enableDebugLogs)
                Debug.LogError($"[GazeRecorder] Failed to write CSV header to {fullPath}: {e.Message}");
        }
    }

    void WriteRow(string row)
    {
        try
        {
            using (StreamWriter writer = new StreamWriter(fullPath, append: true))
                writer.WriteLine(row);
        }
        catch (Exception e)
        {
            if (enableDebugLogs)
                Debug.LogError($"[GazeRecorder] Failed to write gaze sample to {fullPath}: {e.Message}");
        }
    }

    string SafeCsvField(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
    }
}