using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// TrajectoryRecorder
/// - Attach to XR Rig root (player) or to NPC root to record trajectory data.
/// - If attached to player (detected as a child or same object as ExperimentManager.playerRig),
///   this script will read ExternalSessionController.participantId and selectedPart to set subject/trial metadata.
/// - If attached to NPC, subjectId will default to the GameObject.name and trialId will be set from selectedPart.
/// - Subscribes to ExperimentManager.OnTrialStart / OnTrialEnd to automatically Start/Stop recording each Part.
/// - Writes CSV to Application.persistentDataPath with filename:
///      "{subjectId} - {trialId} - Trajectory - {yyyyMMddTHHmmssZ}.csv"
/// - Attempts to read MoveToTarget component (if present) to record movement style metadata via reflection.
/// - Uses Time.realtimeSinceStartup for monotonic timing (independent of timeScale).
/// </summary>
[DisallowMultipleComponent]
public class TrajectoryRecorder : MonoBehaviour
{
    [Header("Manual metadata (fallbacks)")]
    [Tooltip("Subject ID fallback. If this is a player recorder it will be overwritten from ExternalSessionController if available.")]
    public string subjectId = "sub-001";
    [Tooltip("Trial ID / Selected Part fallback. Will be overwritten from ExternalSessionController.selectedPart when available.")]
    public string trialId = "Part1";

    [Header("Sampling")]
    [Tooltip("Samples per second")]
    public float sampleRate = 30f;
    [Tooltip("If true, will start recording on Start() even without ExperimentManager events.")]
    public bool autoStartIfNoExperimentManager = false;
    [Tooltip("Append to existing file if true (careful when reusing subject/trial names).")]
    public bool appendToFile = false;

    [Header("Integration")]
    [Tooltip("Automatically subscribe to ExperimentManager.OnTrialStart / OnTrialEnd if an ExperimentManager exists.")]
    public bool subscribeToExperimentEvents = true;

    [Header("Options")]
    public bool recordAgentVelocity = true;
    public bool recordRigidbodyVelocity = true;

    // internal runtime state
    private bool isRecording = false;
    private Coroutine samplingCoroutine;
    private string fullPath;
    private Vector3 lastPosition;
    private double lastRealtimeSinceStartup;
    private NavMeshAgent agent;
    private Rigidbody rb;
    private CharacterController charController;

    // references to experiment/session
    private ExperimentManager experimentManager;
    private Component externalSessionController; // typed unknown, use reflection to read fields
    private Component moveToTargetComponent; // replacement for old NPCMoverOffset

    private StringBuilder rowBuilder = new StringBuilder();

    // ---- lifecycle ----
    private void Awake()
    {
        // locate common components to speed up sampling
        agent = GetComponent<NavMeshAgent>() ?? GetComponentInParent<NavMeshAgent>();
        rb = GetComponent<Rigidbody>() ?? GetComponentInParent<Rigidbody>();
        charController = GetComponent<CharacterController>() ?? GetComponentInParent<CharacterController>();

        // try to find MoveToTarget component (name-based since type might be in another namespace)
        moveToTargetComponent = TryGetComponentByName("MoveToTarget") ?? GetComponentInParent(Type.GetType("MoveToTarget"));

        // attempt to find ExperimentManager and ExternalSessionController
        experimentManager = FindObjectOfType<ExperimentManager>();
        if (experimentManager != null)
        {
            // prefer the sessionController referenced by ExperimentManager if present
            try
            {
                FieldInfo scField = typeof(ExperimentManager).GetField("sessionController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (scField != null)
                    externalSessionController = scField.GetValue(experimentManager) as Component;

                // if not found above, fallback to searching the scene for an ExternalSessionController
                if (externalSessionController == null)
                {
                    externalSessionController = FindObjectOfTypeByName("ExternalSessionController");
                }
            }
            catch
            {
                externalSessionController = FindObjectOfTypeByName("ExternalSessionController");
            }
        }
        else
        {
            // no ExperimentManager in scene; still try to find ExternalSessionController standalone
            externalSessionController = FindObjectOfTypeByName("ExternalSessionController");
        }
    }

    private void Start()
    {
        lastPosition = transform.position;
        lastRealtimeSinceStartup = Time.realtimeSinceStartupAsDouble;

        // If we detected an ExperimentManager and the subscribe flag is true, register to its events
        if (subscribeToExperimentEvents && experimentManager != null)
        {
            experimentManager.OnTrialStart += OnExperimentTrialStart;
            experimentManager.OnTrialEnd += OnExperimentTrialEnd;
        }

        // If we didn't subscribe (no ExperimentManager) but autoStart is requested, start recording now
        if (!subscribeToExperimentEvents && autoStartIfNoExperimentManager)
        {
            PrepareMetadataFromSessionOrGameObject();
            StartRecording();
        }
    }

    private void OnDestroy()
    {
        // cleanup subscription safely
        if (experimentManager != null)
        {
            experimentManager.OnTrialStart -= OnExperimentTrialStart;
            experimentManager.OnTrialEnd -= OnExperimentTrialEnd;
        }
    }

    // Called when ExperimentManager signals a trial (part) start
    private void OnExperimentTrialStart()
    {
        // update subject/trial metadata from ExternalSessionController or from GameObject name
        PrepareMetadataFromSessionOrGameObject();

        // Build file path (timestamped)
        BuildFullPath();

        StartRecording();
    }

    // Called when ExperimentManager signals a trial (part) end
    private void OnExperimentTrialEnd()
    {
        StopRecording();
    }

    /// <summary>
    /// Prepare subjectId and trialId based on where this recorder is attached:
    /// - if this object is the ExperimentManager.playerRig or a child of it -> treat as participant recorder:
    ///     subjectId <- ExternalSessionController.participantId (fallback to existing subjectId)
    ///     trialId <- ExternalSessionController.selectedPart (fallback to existing trialId)
    /// - else treat as NPC:
    ///     subjectId <- this.gameObject.name
    ///     trialId <- ExternalSessionController.selectedPart (fallback to existing trialId)
    /// </summary>
    private void PrepareMetadataFromSessionOrGameObject()
    {
        // Determine whether recorder is attached to the player rig from ExperimentManager
        bool isPlayerRecorder = false;
        try
        {
            if (experimentManager != null && experimentManager.playerRig != null)
            {
                Transform playerRig = experimentManager.playerRig;
                if (playerRig == transform || transform.IsChildOf(playerRig))
                {
                    isPlayerRecorder = true;
                }
            }
        }
        catch
        {
            isPlayerRecorder = false;
        }

        // Try to read selectedPart and participantId from externalSessionController (reflection, safe)
        string selectedPartStr = null;
        string participantIdStr = null;
        if (externalSessionController != null)
        {
            var scType = externalSessionController.GetType();

            // selectedPart (likely enum) - prefer property then field
            object selectedPartVal = GetMemberValueSafe(externalSessionController, "selectedPart");
            if (selectedPartVal != null) selectedPartStr = selectedPartVal.ToString();

            // participant id (common variations)
            object pidVal = GetMemberValueSafe(externalSessionController, "participantId")
                          ?? GetMemberValueSafe(externalSessionController, "participantID")
                          ?? GetMemberValueSafe(externalSessionController, "participant")
                          ?? GetMemberValueSafe(externalSessionController, "participantName")
                          ?? GetMemberValueSafe(externalSessionController, "participant_id");
            if (pidVal != null) participantIdStr = pidVal.ToString();
        }

        if (isPlayerRecorder)
        {
            // set subjectId from session controller if available, else keep inspector value
            if (!string.IsNullOrEmpty(participantIdStr)) subjectId = participantIdStr;
            if (!string.IsNullOrEmpty(selectedPartStr)) trialId = selectedPartStr;
        }
        else
        {
            // NPC: subject is gameobject name, trial from session controller if available
            subjectId = gameObject.name;
            if (!string.IsNullOrEmpty(selectedPartStr)) trialId = selectedPartStr;
        }
    }

    /// <summary>
    /// Build/assign the full path for the current subject/trial. Timestamp is UTC.
    /// Format: "{Subject ID} - {Trial ID} - Trajectory - {yyyyMMddTHHmmssZ}.csv"
    /// </summary>
    private void BuildFullPath()
    {
        string safeSub = MakeSafeFileName(subjectId ?? "unknown");
        string safeTrial = MakeSafeFileName(trialId ?? "Part");
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        string filename = $"{safeSub}_{safeTrial}_Trajectory_{ts}.csv";
        fullPath = Path.Combine(Application.persistentDataPath, filename);

        // If not appending, write header (overwrite)
        if (!appendToFile || !File.Exists(fullPath))
        {
            try
            {
                using (var sw = new StreamWriter(fullPath, false, Encoding.UTF8))
                {
                    sw.WriteLine(GetCsvHeader());
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"TrajectoryRecorder: Failed to write header to {fullPath}: {e}");
            }
        }
    }

    /// <summary>
    /// Public API to start recording immediately (useful if not using ExperimentManager events).
    /// </summary>
    public void StartRecording()
    {
        if (isRecording) return;

        // ensure metadata and path exist
        if (string.IsNullOrEmpty(fullPath))
        {
            PrepareMetadataFromSessionOrGameObject();
            BuildFullPath();
        }

        isRecording = true;
        samplingCoroutine = StartCoroutine(SamplingLoop());
        Debug.Log($"TrajectoryRecorder: Started recording to {fullPath}");
    }

    /// <summary>
    /// Public API to stop recording.
    /// </summary>
    public void StopRecording()
    {
        if (!isRecording) return;

        isRecording = false;
        if (samplingCoroutine != null) StopCoroutine(samplingCoroutine);
        samplingCoroutine = null;
        Debug.Log($"TrajectoryRecorder: Stopped recording to {fullPath}");
    }

    // ---- Sampling loop ----
    private IEnumerator SamplingLoop()
    {
        float interval = Mathf.Max(0.0001f, 1f / Mathf.Max(0.1f, sampleRate));
        double trialStartRealtime = Time.realtimeSinceStartupAsDouble;

        // last position/time already initialized in Start/Awake, but refresh here for safety
        lastPosition = transform.position;
        lastRealtimeSinceStartup = Time.realtimeSinceStartupAsDouble;

        while (isRecording)
        {
            double nowRealtime = Time.realtimeSinceStartupAsDouble;
            double timeSinceStart = nowRealtime - trialStartRealtime;
            DateTime utcNow = DateTime.UtcNow;

            Vector3 pos = transform.position;
            Vector3 forward = transform.forward;

            // determine velocity using available components
            Vector3 velocity = Vector3.zero;
            if (recordRigidbodyVelocity && rb != null)
            {
                velocity = rb.velocity;
            }
            else if (recordAgentVelocity && agent != null)
            {
                velocity = agent.velocity;
            }
            else
            {
                double dt = nowRealtime - lastRealtimeSinceStartup;
                if (dt > 0) velocity = (pos - lastPosition) / (float)dt;
                else velocity = Vector3.zero;
            }

            float speed = velocity.magnitude;

            // heading: from movement if moving, else from forward
            float heading = 0f;
            if (speed > 0.01f)
            {
                Vector3 horiz = new Vector3(velocity.x, 0f, velocity.z);
                heading = Mathf.Atan2(horiz.x, horiz.z) * Mathf.Rad2Deg;
            }
            else
            {
                Vector3 f = new Vector3(forward.x, 0f, forward.z);
                heading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
            if (heading < 0f) heading += 360f;

            // movement style / metadata from MoveToTarget if present
            string movementStyle = GetMovementStyleString();

            // Build CSV row (matching header)
            string row = BuildCsvRow(
                subjectId,
                trialId,
                gameObject.name,
                utcNow,
                (float)timeSinceStart,
                pos,
                velocity,
                speed,
                forward,
                heading,
                agent,
                rb,
                movementStyle
            );

            // append row to file
            try
            {
                using (var sw = new StreamWriter(fullPath, true, Encoding.UTF8))
                {
                    sw.WriteLine(row);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"TrajectoryRecorder: Failed to append row to {fullPath}: {e}");
            }

            // update last state
            lastPosition = pos;
            lastRealtimeSinceStartup = nowRealtime;

            yield return new WaitForSecondsRealtime(interval);
        }
    }

    // ---- CSV building helpers ----
    private string BuildCsvRow(
        string subject,
        string trial,
        string source,
        DateTime utcNow,
        float timeSinceStart,
        Vector3 pos,
        Vector3 velocity,
        float speed,
        Vector3 forward,
        float heading,
        NavMeshAgent agentRef,
        Rigidbody rbRef,
        string movementStyle)
    {
        rowBuilder.Clear();

        rowBuilder.Append(EscapeCsv(subject)).Append(",");    // subject_id
        rowBuilder.Append(EscapeCsv(trial)).Append(",");      // trial_id
        rowBuilder.Append(EscapeCsv(source)).Append(",");     // source (gameobject name)
        rowBuilder.Append(utcNow.ToString("o")).Append(",");  // timestamp_utc ISO8601
        rowBuilder.Append(timeSinceStart.ToString("F4")).Append(","); // time_since_start_s

        // position
        rowBuilder.Append(pos.x.ToString("F4")).Append(",");
        rowBuilder.Append(pos.y.ToString("F4")).Append(",");
        rowBuilder.Append(pos.z.ToString("F4")).Append(",");

        // velocity vector
        rowBuilder.Append(velocity.x.ToString("F4")).Append(",");
        rowBuilder.Append(velocity.y.ToString("F4")).Append(",");
        rowBuilder.Append(velocity.z.ToString("F4")).Append(",");

        // speed
        rowBuilder.Append(speed.ToString("F4")).Append(",");

        // forward vector
        rowBuilder.Append(forward.x.ToString("F4")).Append(",");
        rowBuilder.Append(forward.y.ToString("F4")).Append(",");
        rowBuilder.Append(forward.z.ToString("F4")).Append(",");

        // heading
        rowBuilder.Append(heading.ToString("F2")).Append(",");

        // NavMeshAgent velocity & speed & desiredVelocity if present
        if (agentRef != null)
        {
            Vector3 agentVel = agentRef.velocity;
            rowBuilder.Append(agentVel.x.ToString("F4")).Append(",");
            rowBuilder.Append(agentVel.y.ToString("F4")).Append(",");
            rowBuilder.Append(agentVel.z.ToString("F4")).Append(",");
            rowBuilder.Append(agentVel.magnitude.ToString("F4")).Append(",");
            // desiredVelocity may exist
            try
            {
                Vector3 desired = agentRef.desiredVelocity;
                rowBuilder.Append(desired.x.ToString("F4")).Append(",");
                rowBuilder.Append(desired.y.ToString("F4")).Append(",");
                rowBuilder.Append(desired.z.ToString("F4")).Append(",");
            }
            catch
            {
                rowBuilder.Append(",, ,");
            }
        }
        else
        {
            // placeholders for agent columns
            rowBuilder.Append(",,,,,, ,");
        }

        // Rigidbody velocity if present
        if (rbRef != null)
        {
            rowBuilder.Append(rbRef.velocity.x.ToString("F4")).Append(",");
            rowBuilder.Append(rbRef.velocity.y.ToString("F4")).Append(",");
            rowBuilder.Append(rbRef.velocity.z.ToString("F4")).Append(",");
        }
        else
        {
            rowBuilder.Append(",,,");
        }

        // movement style metadata
        rowBuilder.Append(EscapeCsv(movementStyle));

        return rowBuilder.ToString();
    }

    private string GetCsvHeader()
    {
        // Must match BuildCsvRow order
        var header = new StringBuilder();
        header.Append("subject_id,trial_id,source,timestamp_utc,time_since_start_s,");
        header.Append("pos_x_m,pos_y_m,pos_z_m,");
        header.Append("vel_x_mps,vel_y_mps,vel_z_mps,");
        header.Append("speed_mps,");
        header.Append("forward_x,forward_y,forward_z,");
        header.Append("heading_deg,");
        header.Append("agent_vel_x_mps,agent_vel_y_mps,agent_vel_z_mps,agent_speed_mps,agent_desired_x,agent_desired_y,agent_desired_z,");
        header.Append("rigidbody_vel_x,rigidbody_vel_y,rigidbody_vel_z,");
        header.Append("movement_style");
        return header.ToString();
    }

    private string EscapeCsv(string s)
    {
        if (s == null) return "";
        if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
        {
            return $"\"{s.Replace("\"", "\"\"")}\"";
        }
        return s;
    }

    // ---- movement style reflection for MoveToTarget ----
    private string GetMovementStyleString()
    {
        if (moveToTargetComponent == null) return "";

        var type = moveToTargetComponent.GetType();
        string[] candidates = { "movementStyle", "MovementStyle", "style", "movement_type", "movementType", "agentStyle" };
        foreach (var name in candidates)
        {
            var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                var val = f.GetValue(moveToTargetComponent);
                if (val != null) return val.ToString();
            }
            var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                var val = p.GetValue(moveToTargetComponent);
                if (val != null) return val.ToString();
            }
        }

        // last resort: call ToString()
        try
        {
            return moveToTargetComponent.ToString();
        }
        catch
        {
            return "";
        }
    }

    // ---- small reflection & utilities ----
    private object GetMemberValueSafe(object obj, string memberName)
    {
        if (obj == null || string.IsNullOrEmpty(memberName)) return null;
        var t = obj.GetType();

        // try property
        var p = t.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null)
        {
            try { return p.GetValue(obj); } catch { }
        }

        // try field
        var f = t.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null)
        {
            try { return f.GetValue(obj); } catch { }
        }

        return null;
    }

    private Component TryGetComponentByName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        // try on this object
        var comp = GetComponent(typeName);
        if (comp != null) return comp;

        // try in parents
        Transform p = transform.parent;
        while (p != null)
        {
            comp = p.GetComponent(typeName);
            if (comp != null) return comp;
            p = p.parent;
        }

        // try children
        foreach (Transform child in transform)
        {
            comp = child.GetComponent(typeName);
            if (comp != null) return comp;
        }

        return null;
    }

    private Component FindObjectOfTypeByName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        // brute-force search through all MonoBehaviours
        var all = GameObject.FindObjectsOfType<MonoBehaviour>();
        foreach (var m in all)
        {
            if (m == null) continue;
            if (m.GetType().Name == typeName) return m;
        }
        return null;
    }

    private string MakeSafeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s;
    }
}
