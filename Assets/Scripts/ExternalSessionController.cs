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

    // ---- identity / configuration validation (F-14) ----
    [Header("Identity status (read-only, set at runtime)")]
    [SerializeField, Tooltip("Valid | Missing | Invalid | Placeholder | UnresolvedConfig. Research recording and auto-start are blocked unless this is Valid.")]
    private string identityStatus = "NotChecked";
    [SerializeField, Tooltip("Why the identity/configuration was rejected (empty when valid).")]
    private string identityMessage = "";

    /// <summary>"inspector" or "external_json": where the values in use came from.</summary>
    public string ConfigSource => useExternalJson ? "external_json" : "inspector";
    /// <summary>FNV-1a hash of the external JSON text that was applied ("" for the Inspector source or when none was applied).</summary>
    public string ConfigHash { get; private set; } = "";
    /// <summary>Non-empty when the configured external JSON could not be loaded/validated. There is NO fallback to Inspector values in that case.</summary>
    public string ConfigError { get; private set; } = "";

    /// <summary>Validates the configuration actually in force: participant ID, source resolution, part and group indices.</summary>
    public SessionIdentity.Result ValidateIdentity()
    {
        SessionIdentity.Result r;
        if (!string.IsNullOrEmpty(ConfigError))
            r = new SessionIdentity.Result { status = SessionIdentity.Status.UnresolvedConfig, message = ConfigError };
        else
        {
            r = SessionIdentity.ValidateParticipantId(participantId);
            if (r.IsValid)
            {
                string bad = ValidateIndices(selectedPart, part2GroupIndex, part3GroupIndex);
                if (bad != null) r = new SessionIdentity.Result { status = SessionIdentity.Status.Invalid, message = bad };
            }
        }
        identityStatus = r.status.ToString(); identityMessage = r.message ?? "";
        return r;
    }

    private static string ValidateIndices(ExperimentPart part, int g2, int g3)
    {
        if (g2 < 0) return "part2GroupIndex must be >= 0.";
        if (g3 < 0 || g3 > 11) return "part3GroupIndex must be 0..11.";
        return null;
    }

    private void Awake()
    {
        if (useExternalJson)
        {
            // Strict: when the external file is the configured source it must exist, parse and validate. A failure is a hard configuration error (no
            // silent fall-back to Inspector values, and the unrelated Assets/Scripts/session_config.json is never read).
            TryLoadFromExternalJson();
        }
        SessionIdentity.Result r = ValidateIdentity();
        if (!r.IsValid)
            Debug.LogError("[ExternalSessionController] Session configuration rejected (" + r.status + "): " + r.message + " Research recording and auto-start stay blocked until this is fixed.");
    }

    /// <summary>
    /// Attempt to load JSON file and apply values. Returns true if successful.
    /// </summary>
    public bool TryLoadFromExternalJson()
    {
        try
        {
            if (!File.Exists(ExternalFilePath)) return Fail("External session configuration not found (expected '" + externalFileName + "' in the application data folder).");

            string json = File.ReadAllText(ExternalFilePath);
            // required keys must really be present: JsonUtility cannot tell a missing int from 0
            foreach (string key in new[] { "participantId", "selectedPart", "part2GroupIndex", "part3GroupIndex" })
                if (json.IndexOf("\"" + key + "\"", StringComparison.Ordinal) < 0) return Fail("External session configuration is missing required field '" + key + "'.");

            SessionConfigData data;
            try { data = JsonUtility.FromJson<SessionConfigData>(json); }
            catch (Exception) { return Fail("External session configuration is not valid JSON."); }
            if (data == null) return Fail("External session configuration could not be parsed.");

            ExperimentPart part;
            if (data.selectedPart != null && data.selectedPart.Equals("Part1", StringComparison.OrdinalIgnoreCase)) part = ExperimentPart.Part1;
            else if (data.selectedPart != null && data.selectedPart.Equals("Part2", StringComparison.OrdinalIgnoreCase)) part = ExperimentPart.Part2;
            else if (data.selectedPart != null && data.selectedPart.Equals("Part3", StringComparison.OrdinalIgnoreCase)) part = ExperimentPart.Part3;
            else return Fail("External session configuration has an unknown 'selectedPart' (expected Part1, Part2 or Part3).");

            SessionIdentity.Result idr = SessionIdentity.ValidateParticipantId(data.participantId);
            if (!idr.IsValid) return Fail("External session configuration: " + idr.message);
            string bad = ValidateIndices(part, data.part2GroupIndex, data.part3GroupIndex);
            if (bad != null) return Fail("External session configuration: " + bad);

            // all fields validated: apply atomically
            participantId = idr.participantId;
            selectedPart = part;
            part2GroupIndex = data.part2GroupIndex;
            part3GroupIndex = data.part3GroupIndex;
            autoStart = data.autoStart;
            ConfigHash = AgentProfileSnapshot.Fnv1a64Hex(json);
            ConfigError = "";
            Debug.Log($"ExternalSessionController: Loaded config '{externalFileName}' (selectedPart={selectedPart}, part2GroupIndex={part2GroupIndex}, part3GroupIndex={part3GroupIndex}, autoStart={autoStart}, hash={ConfigHash})");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("External session configuration could not be read: " + ex.GetType().Name);
        }
    }

    private bool Fail(string message)
    {
        ConfigError = message;
        ConfigHash = "";
        return false;
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
