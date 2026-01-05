using System;
using System.IO;
using UnityEngine;

/// <summary>
/// ExternalSessionController
/// 
/// Purpose:
/// - Hold session-level configuration (participant ID, chosen Part, Part2 group index, Part3 group index).
/// - Allow configuration via Inspector for quick Unity testing.
/// - Optionally load configuration from an external JSON file at runtime (for built exe).
/// 
/// Usage:
/// - Add this script to a GameObject in your scene (e.g. "SessionController") and configure inspector fields.
/// - If 'useExternalJson' is true, the script will attempt to load file:
///     Application.persistentDataPath + "/session_config.json"
///   and override Inspector values with the values from JSON (if file exists and is valid).
/// - You can also call SaveToJson() in editor or code to write current inspector settings to that file.
/// 
/// JSON format example:
/// {
///   "participantId":"P001",
///   "selectedPart": "Part2",
///   "part2GroupIndex": 0,
///   "part3GroupIndex": 1,
///   "autoStart": true
/// }
/// 
/// Note: This component only stores & provides the session configuration. The ExperimentManager
/// must be given a reference to this component to read those settings and choose start positions / targets accordingly.
/// </summary>
public class ExternalSessionController : MonoBehaviour
{
    public enum ExperimentPart { Part1 = 1, Part2 = 2, Part3 = 3 }

    [Header("Session settings (Inspector editable)")]
    [Tooltip("Participant ID (string).")]
    public string participantId = "P000";

    [Tooltip("Which experimental part to run for this session.")]
    public ExperimentPart selectedPart = ExperimentPart.Part1;

    [Tooltip("Index of Part2 group (used to select start & teleport positions). Zero-based.")]
    public int part2GroupIndex = 0;

    [Tooltip("Index of Part3 group (used to select start positions & target buildings). Zero-based.")]
    public int part3GroupIndex = 0;

    [Tooltip("If true, on Start() the ExperimentManager will automatically start the chosen part.")]
    public bool autoStart = false;

    [Header("External JSON config")]
    [Tooltip("If true, will attempt to load session_config.json from persistentDataPath at startup and override the Inspector values.")]
    public bool useExternalJson = false;

    [Tooltip("Filename used for external config (inside Application.persistentDataPath).")]
    public string externalFileName = "session_config.json";

    // Path computed at runtime
    public string ExternalFilePath => Path.Combine(Application.persistentDataPath, externalFileName);

    /// <summary>
    /// Structure used for JSON (simple mirror of this component's public fields).
    /// </summary>
    [Serializable]
    public class SessionConfigData
    {
        public string participantId;
        public string selectedPart; // "Part1"/"Part2"/"Part3" (we'll parse)
        public int part2GroupIndex;
        public int part3GroupIndex;
        public bool autoStart;
    }

    private void Awake()
    {
        if (useExternalJson)
        {
            TryLoadFromExternalJson();
        }
    }

    /// <summary>
    /// Attempt to load JSON file and apply values. Returns true if successful.
    /// </summary>
    public bool TryLoadFromExternalJson()
    {
        try
        {
            if (!File.Exists(ExternalFilePath))
            {
                Debug.LogWarning($"ExternalSessionController: config file not found at {ExternalFilePath}");
                return false;
            }

            string json = File.ReadAllText(ExternalFilePath);
            SessionConfigData data = JsonUtility.FromJson<SessionConfigData>(json);
            if (data == null)
            {
                Debug.LogWarning("ExternalSessionController: Failed to parse JSON into SessionConfigData.");
                return false;
            }

            // Apply values
            if (!string.IsNullOrEmpty(data.participantId)) participantId = data.participantId;

            // parse selected part string
            if (!string.IsNullOrEmpty(data.selectedPart))
            {
                if (data.selectedPart.Equals("Part1", StringComparison.OrdinalIgnoreCase)) selectedPart = ExperimentPart.Part1;
                else if (data.selectedPart.Equals("Part2", StringComparison.OrdinalIgnoreCase)) selectedPart = ExperimentPart.Part2;
                else if (data.selectedPart.Equals("Part3", StringComparison.OrdinalIgnoreCase)) selectedPart = ExperimentPart.Part3;
                else Debug.LogWarning("ExternalSessionController: Unknown selectedPart in JSON: " + data.selectedPart);
            }

            part2GroupIndex = Mathf.Max(0, data.part2GroupIndex);
            part3GroupIndex = Mathf.Max(0, data.part3GroupIndex);
            autoStart = data.autoStart;

            Debug.Log($"ExternalSessionController: Loaded config from {ExternalFilePath} (participantId={participantId}, selectedPart={selectedPart}, part2GroupIndex={part2GroupIndex}, part3GroupIndex={part3GroupIndex}, autoStart={autoStart})");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("ExternalSessionController: Exception while loading JSON: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Save current inspector values to the external JSON file (overwrites).
    /// Useful for generating a config file on the lab PC.
    /// Returns true if saved successfully.
    /// </summary>
    public bool SaveToExternalJson()
    {
        try
        {
            SessionConfigData data = new SessionConfigData
            {
                participantId = this.participantId,
                selectedPart = $"Part{(int)this.selectedPart}",
                part2GroupIndex = this.part2GroupIndex,
                part3GroupIndex = this.part3GroupIndex,
                autoStart = this.autoStart
            };

            string json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(ExternalFilePath, json);
            Debug.Log($"ExternalSessionController: Saved config to {ExternalFilePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("ExternalSessionController: Exception while saving JSON: " + ex);
            return false;
        }
    }
}
