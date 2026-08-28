// GazeRecorder.cs
//
// Eye-tracking data recorder for Meta Quest Pro, built to plug into the existing experiment
// architecture the same way TrajectoryRecorder.cs already does: it subscribes to
// ExperimentManager.OnTrialStart/OnTrialEnd, writes one CSV row per sample to
// Application.persistentDataPath, and uses the same crash-safe "open/write/close per row"
// pattern already established by TrajectoryRecorder, so eye-tracking files use the same
// subject/trial naming convention and the same Time.realtimeSinceStartupAsDouble clock basis,
// making post-hoc alignment between behavioural trajectories and gaze data straightforward.
//
// REQUIREMENTS (see the accompanying setup instructions for full detail):
// - Meta XR Core SDK (or Meta XR All-in-One SDK) imported via Package Manager - this is what
//   provides the 'OVREyeGaze' and 'OVRManager' components referenced below. If your project only
//   has the older "Oculus Integration" asset, OVREyeGaze may not exist yet; update to a current
//   Meta XR Core SDK package first.
// - An OVRManager component in the scene with "Eye Tracking Support" set to Supported/Required
//   (Quest Features > General), which is what causes Unity to inject the required Android
//   manifest entries (com.oculus.permission.EYE_TRACKING permission + oculus.software.eye_tracking
//   feature) at build time.
// - Two OVREyeGaze components in the scene (one set to Eye = Left, one set to Eye = Right),
//   typically placed as children of the HMD camera transform (CenterEyeAnchor). See setup notes.
// - Only Meta Quest Pro currently exposes hardware eye tracking via this API; on Quest 2/3 or in
//   the Editor without a connected Quest Pro (or the Meta XR Simulator, if your SDK version
//   supports simulated eye tracking), gaze samples will simply report as not valid.
//
// IMPORTANT NOTE ON API SURFACE: Meta has renamed and reorganized its XR packages several times
// (Oculus Integration -> Meta XR Core SDK / Movement SDK). The properties used below
// (OVREyeGaze.Eye, OVREyeGaze.Confidence, OVRManager.instance.requestEyeTrackingPermission()) are
// the stable, documented surface as of recent Meta XR Core SDK releases, but please verify these
// exact names against the specific SDK version installed in this project before relying on this
// in a live study - if a name has changed, the compiler will point you directly at the line to fix.
//
// All comments in English.

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
             "FindObjectOfType at Start() (same pattern used by TrajectoryRecorder/TargetSelectable).")]
    public ExperimentManager experimentManager;

    [Tooltip("Optional: assign the scene's ExternalSessionController (source of participantId). " +
             "If left empty, auto-found via FindObjectOfType at Start().")]
    public ExternalSessionController sessionController;

    [Tooltip("If true, StartRecording()/StopRecording() are called automatically from " +
             "ExperimentManager.OnTrialStart/OnTrialEnd, exactly like TrajectoryRecorder. This keeps the " +
             "gaze-recording window aligned with the same trial boundaries as the trajectory data.")]
    public bool subscribeToExperimentEvents = true;

    [Tooltip("If true and no ExperimentManager is found in the scene, recording starts immediately on " +
             "Start() instead of waiting for trial events (useful for isolated testing of this script).")]
    public bool autoStartIfNoExperimentManager = false;

    [Header("Identification (used in filename + CSV columns)")]
    [Tooltip("Participant/subject identifier. If empty, resolved from ExternalSessionController.participantId " +
             "at recording start.")]
    public string subjectId = "";

    [Tooltip("Trial/phase identifier. If empty, resolved from ExternalSessionController.selectedPart " +
             "at recording start (matches TrajectoryRecorder's convention, e.g. 'Part2').")]
    public string trialId = "";

    [Header("Sampling")]
    [Tooltip("Samples per second. Meta Quest Pro eye tracking updates well above typical VR frame rates, " +
             "but recording faster than your analysis needs just inflates file size - 30-60Hz is a " +
             "reasonable default; match TrajectoryRecorder's sampleRate if you want 1:1 aligned rows.")]
    public float sampleRate = 30f;

    [Header("ROI / AOI raycasting")]
    [Tooltip("If true, each sample also raycasts from the combined gaze origin along the combined gaze " +
             "direction and records what it hit (e.g. a target building or an NPC), enabling ROI/AOI " +
             "analysis without needing a separate offline gaze-to-scene reprojection step.")]
    public bool enableROIRaycast = true;

    [Tooltip("Layers considered for ROI/AOI raycasting. Set this to only the layers your target " +
             "buildings/NPCs/points-of-interest are on, to avoid the raycast hitting incidental geometry.")]
    public LayerMask roiLayerMask = ~0;

    [Tooltip("Maximum raycast distance (metres) for ROI/AOI detection.")]
    public float roiMaxDistance = 50f;

    [Header("Debug")]
    [Tooltip("Mirrors the enableDebugLogs convention used elsewhere in this project (see SpawnerManager, " +
             "MoveToTarget). Gates start/stop/warning logs; per-sample data is never logged regardless, " +
             "since that alone would be a bigger performance cost than anything this flag guards.")]
    public bool enableDebugLogs = true;

    // Internal recording state
    bool isRecording = false;
    string fullPath = "";
    bool headerWritten = false;
    Coroutine samplingCoroutine = null;
    double recordingStartRealtime = 0.0;

    void Awake()
    {
        if (headTransform == null && Camera.main != null)
        {
            headTransform = Camera.main.transform;
        }

        // Best-effort runtime permission request. On Quest, eye tracking requires the Android runtime
        // permission 'com.oculus.permission.EYE_TRACKING' to be granted in addition to the manifest
        // entries injected by OVRManager's "Eye Tracking Support" build setting. This has no effect
        // (and is skipped) outside of an on-device Android build.
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (OVRManager.instance != null)
            {
                OVRManager.instance.requestEyeTrackingPermission();
            }
            else if (enableDebugLogs)
            {
                Debug.LogWarning("[GazeRecorder] OVRManager.instance is null - cannot request eye tracking " +
                    "permission. Ensure an OVRManager component exists in the scene with Eye Tracking " +
                    "Support enabled.");
            }
        }
        catch (Exception e)
        {
            if (enableDebugLogs)
                Debug.LogWarning($"[GazeRecorder] requestEyeTrackingPermission() threw: {e.Message}. " +
                    "Verify this method exists on your installed Meta XR Core SDK version.");
        }
#endif

        if (enableDebugLogs && (leftEyeGaze == null || rightEyeGaze == null))
        {
            Debug.LogWarning("[GazeRecorder] leftEyeGaze and/or rightEyeGaze is not assigned. This recorder " +
                "will still run but will record all-invalid samples until both are assigned. See setup notes " +
                "for how to add OVREyeGaze components to the XR rig.");
        }
    }

    void Start()
    {
        if (experimentManager == null)
        {
            experimentManager = FindObjectOfType<ExperimentManager>();
        }

        if (sessionController == null)
        {
            sessionController = FindObjectOfType<ExternalSessionController>();
        }

        if (subscribeToExperimentEvents && experimentManager != null)
        {
            experimentManager.OnTrialStart += OnExperimentTrialStart;
            experimentManager.OnTrialEnd += OnExperimentTrialEnd;
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
            experimentManager.OnTrialEnd -= OnExperimentTrialEnd;
        }

        // Defensive: make sure we don't leave a dangling coroutine reference or an unflushed state.
        StopRecording();
    }

    // Crash-safety net: if the app is paused (e.g. the user removes the headset, or the OS backgrounds
    // the app) or is quitting, make sure recording is cleanly stopped rather than relying only on
    // OnTrialEnd firing. Each row is already flushed-and-closed individually (see SamplingLoop), so this
    // mainly ensures the sampling coroutine itself doesn't keep running in the background.
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
        if (isRecording)
        {
            StopRecording();
        }
    }

    void OnExperimentTrialStart()
    {
        StartRecording();
    }

    void OnExperimentTrialEnd()
    {
        StopRecording();
    }

    /// <summary>
    /// Begins a new gaze-recording session: resolves subject/trial IDs if not explicitly set, builds a
    /// fresh timestamped CSV path, and starts the per-sample coroutine.
    /// </summary>
    public void StartRecording()
    {
        if (isRecording)
        {
            if (enableDebugLogs) Debug.LogWarning("[GazeRecorder] StartRecording() called while already recording; ignoring.");
            return;
        }

        if (string.IsNullOrEmpty(subjectId) && sessionController != null)
        {
            subjectId = sessionController.participantId;
        }
        if (string.IsNullOrEmpty(subjectId))
        {
            subjectId = "UNKNOWN_SUBJECT";
        }

        if (string.IsNullOrEmpty(trialId) && sessionController != null)
        {
            trialId = sessionController.selectedPart.ToString();
        }
        if (string.IsNullOrEmpty(trialId))
        {
            trialId = "UNKNOWN_TRIAL";
        }

        fullPath = BuildFullPath();
        headerWritten = false;
        recordingStartRealtime = Time.realtimeSinceStartupAsDouble;
        isRecording = true;

        if (enableDebugLogs)
            Debug.Log($"[GazeRecorder] Recording started. subject={subjectId} trial={trialId} path={fullPath}");

        samplingCoroutine = StartCoroutine(SamplingLoop());
    }

    /// <summary>
    /// Stops the current gaze-recording session, if any. Safe to call multiple times.
    /// </summary>
    public void StopRecording()
    {
        if (!isRecording && samplingCoroutine == null)
            return;

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
        // Filename convention mirrors TrajectoryRecorder.cs so files from the same trial can be matched
        // by their {subjectId}_{trialId}_... prefix during offline analysis.
        string timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        string safeSubject = SanitizeForFilename(subjectId);
        string safeTrial = SanitizeForFilename(trialId);
        string fileName = $"{safeSubject}_{safeTrial}_Gaze_{timestamp}.csv";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    string SanitizeForFilename(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s;
    }

    string GetCsvHeader()
    {
        return string.Join(",", new string[]
        {
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
            "roi_hit_distance"
        });
    }

    /// <summary>
    /// Per-eye sample snapshot, read directly from an OVREyeGaze component. Kept as a small local struct
    /// (rather than reading fields inline everywhere) so the "is this eye valid" logic lives in one place.
    /// </summary>
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

        if (eyeGaze == null || !eyeGaze.isActiveAndEnabled)
            return sample;

        // OVREyeGaze drives its own transform to represent the eye's current gaze pose; position is the
        // gaze origin (approx. eye location) and forward is the gaze direction. Confidence (0..1) is the
        // documented signal for sample quality - there is no separate boolean validity flag in the
        // Meta XR Core SDK's OVREyeGaze API, so we derive 'valid' from a configurable confidence threshold.
        sample.confidence = eyeGaze.Confidence;
        sample.origin = eyeGaze.transform.position;
        sample.direction = eyeGaze.transform.forward;
        sample.valid = sample.confidence >= minConfidenceForValid;

        return sample;
    }

    IEnumerator SamplingLoop()
    {
        // Write the header once per file, the first time we're about to write a row.
        if (!headerWritten)
        {
            WriteHeaderIfNeeded();
        }

        float interval = (sampleRate > 0f) ? (1f / sampleRate) : (1f / 30f);

        while (true)
        {
            EyeSample left = ReadEye(leftEyeGaze);
            EyeSample right = ReadEye(rightEyeGaze);

            // Combined gaze: average the two eyes when both are valid, fall back to whichever single eye
            // is valid, or mark combined invalid if neither eye currently meets the confidence threshold.
            bool combinedValid;
            float combinedConfidence;
            Vector3 combinedOrigin;
            Vector3 combinedDirection;

            if (left.valid && right.valid)
            {
                combinedValid = true;
                combinedConfidence = (left.confidence + right.confidence) * 0.5f;
                combinedOrigin = (left.origin + right.origin) * 0.5f;
                Vector3 dirSum = left.direction + right.direction;
                combinedDirection = (dirSum.sqrMagnitude > 1e-6f) ? dirSum.normalized : left.direction;
            }
            else if (left.valid)
            {
                combinedValid = true;
                combinedConfidence = left.confidence;
                combinedOrigin = left.origin;
                combinedDirection = left.direction;
            }
            else if (right.valid)
            {
                combinedValid = true;
                combinedConfidence = right.confidence;
                combinedOrigin = right.origin;
                combinedDirection = right.direction;
            }
            else
            {
                combinedValid = false;
                combinedConfidence = 0f;
                // Fall back to head-forward so downstream analysis at least has a plausible ray to work
                // with for this sample, rather than a zero vector; 'combined_valid' still correctly flags
                // it as not real eye-tracking data.
                combinedOrigin = headTransform != null ? headTransform.position : transform.position;
                combinedDirection = headTransform != null ? headTransform.forward : transform.forward;
            }

            string roiName = "";
            float roiDistance = float.NaN;

            if (enableROIRaycast && combinedValid)
            {
                RaycastHit hit;
                if (Physics.Raycast(combinedOrigin, combinedDirection, out hit, roiMaxDistance, roiLayerMask))
                {
                    roiName = SafeCsvField(hit.collider != null ? hit.collider.name : hit.transform.name);
                    roiDistance = hit.distance;
                }
            }

            Vector3 headPos = headTransform != null ? headTransform.position : Vector3.zero;
            Quaternion headRot = headTransform != null ? headTransform.rotation : Quaternion.identity;

            double t = Time.realtimeSinceStartupAsDouble - recordingStartRealtime;
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string row = string.Join(",", new string[]
            {
                t.ToString("F4"),
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
                float.IsNaN(roiDistance) ? "" : roiDistance.ToString("F3")
            });

            WriteRow(row);

            yield return new WaitForSecondsRealtime(interval);
        }
    }

    void WriteHeaderIfNeeded()
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                using (StreamWriter writer = new StreamWriter(fullPath, append: false))
                {
                    writer.WriteLine(GetCsvHeader());
                }
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
        // Same crash-safety pattern as TrajectoryRecorder: open, write, and close (flush) on every single
        // row, so at most one in-flight sample is ever at risk if the app terminates unexpectedly.
        try
        {
            using (StreamWriter writer = new StreamWriter(fullPath, append: true))
            {
                writer.WriteLine(row);
            }
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
        // Strip characters that would break the simple comma-separated format used here (no quoting/escaping),
        // consistent with the lightweight CSV approach already used by TrajectoryRecorder.
        return s.Replace(",", " ").Replace("\n", " ").Replace("\r", " ");
    }
}