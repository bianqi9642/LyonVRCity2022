using System;
using System.IO;
using UnityEngine;

/// <summary>
/// ExternalSessionController
///
/// Purpose:
/// - Hold session-level configuration (participant ID, chosen Part, Part2 group index, Part3 group index, optional experimental overrides).
/// - Resolve it ONCE per run into an immutable SessionConfigSnapshot (see SessionConfiguration.cs) that every other component reads.
///
/// Configuration source (primary switch: useExternalJson). The two sources are never mixed:
/// - useExternalJson == false: the saved Inspector fields are the source of truth.
/// - useExternalJson == true : the validated session_config.json in Application.persistentDataPath is the source of truth. If it is missing, malformed
///   or invalid NOTHING from it is applied, there is no fall-back to the Inspector values, and recording / Part start stay blocked (ConfigError).
///
/// Lifecycle: this component runs first ([DefaultExecutionOrder(-200)]) and resolves in Awake. Any consumer may also call Initialize() / read Resolved earlier;
/// resolution happens exactly once (memoised) and never repeats, so the order of Awake calls across GameObjects cannot matter. State is Pending until the
/// first resolution, then Valid or Invalid for the rest of the run. An Invalid configuration exposes no values (empty id, no effective settings) and blocks
/// every Part start and all recording.
/// Editing the Inspector or the JSON file during the run does not change the active session; restart to apply a changed configuration.
/// Consumers read the Active* properties (or Resolved / Effective), not the raw Inspector fields. The EFFECTIVE settings (JSON overrides merged with the
/// Scene-owned values, with provenance) are frozen once by ExperimentManager.EnsureSessionOverridesApplied() via FreezeEffective().
///
/// External JSON format (schemaVersion 1; see Documentation/Configuration/SESSION_CONFIGURATION_GUIDE.md):
/// {
///   "schemaVersion": 1,
///   "participantId": "P001",
///   "selectedPart": "Part3",
///   "part2GroupIndex": 0,
///   "part3GroupIndex": 1,
///   "autoStart": true
/// }
///
/// Note: This component only stores &amp; provides the session configuration. The ExperimentManager reads it to choose start positions / targets.
/// </summary>
[DefaultExecutionOrder(-200)]
public class ExternalSessionController : MonoBehaviour
{
    public enum ExperimentPart { Part1 = 1, Part2 = 2, Part3 = 3 }

    /// <summary>Pending = not resolved yet; Valid / Invalid = the final, permanent outcome of the single resolution.</summary>
    public enum ConfigState { Pending, Valid, Invalid }

    [Header("Session settings (Inspector editable)")]
    [Tooltip("Participant ID (string).")]
    public string participantId = "P000";

    [Tooltip("Which experimental part to run for this session.")]
    public ExperimentPart selectedPart = ExperimentPart.Part1;

    [Tooltip("Index of Part2 group (used to select start & teleport positions). Zero-based; must be a valid index of ExperimentManager.part2Groups when Part 2 is selected.")]
    public int part2GroupIndex = 0;

    [Tooltip("Index of Part3 group (used to select start positions & target buildings). Zero-based, Group Number = Group Index (valid 0..11 when Part 3 is selected).")]
    public int part3GroupIndex = 0;

    [Tooltip("If true, on Start() the ExperimentManager will automatically start the chosen part.")]
    public bool autoStart = false;

    [Header("External JSON config")]
    [Tooltip("If true, the session is configured ONLY from session_config.json in Application.persistentDataPath; the Inspector values above are ignored. " +
             "A missing or invalid file blocks the session (no fall-back to the Inspector).")]
    public bool useExternalJson = false;

    [Tooltip("Filename used for external config (inside Application.persistentDataPath).")]
    public string externalFileName = "session_config.json";

    /// <summary>Test / Editor-validation hook: when non-empty, this exact path is read instead of persistentDataPath/externalFileName. Not serialized.</summary>
    public string ExternalFilePathOverride { get; set; }

    // Path computed at runtime
    public string ExternalFilePath => !string.IsNullOrEmpty(ExternalFilePathOverride) ? ExternalFilePathOverride : Path.Combine(Application.persistentDataPath, externalFileName);

    /// <summary>
    /// Structure used when writing a template with SaveToExternalJson (mirror of the required fields). Reading uses the strict parser in SessionConfigCore.
    /// </summary>
    [Serializable]
    public class SessionConfigData
    {
        public int schemaVersion = SessionConfigCore.SchemaVersion;
        public string participantId;
        public string selectedPart; // "Part1"/"Part2"/"Part3"
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

    private SessionConfigSnapshot _resolved;

    /// <summary>The configuration of this run (resolved on first access). Never null.</summary>
    public SessionConfigSnapshot Resolved { get { EnsureResolved(); return _resolved; } }

    /// <summary>Pending until the single resolution has run (reading this never triggers it), then Valid or Invalid.</summary>
    public ConfigState State => _resolved == null ? ConfigState.Pending : _resolved.IsValid ? ConfigState.Valid : ConfigState.Invalid;

    /// <summary>Resolve now if that has not happened yet (idempotent) and report the final state.</summary>
    public ConfigState Initialize() { EnsureResolved(); return State; }

    /// <summary>The frozen effective settings; null until ExperimentManager froze them, and always null for an Invalid configuration.</summary>
    public EffectiveSettings Effective => Resolved.Effective;

    /// <summary>Freeze the effective settings from the given Scene values (write-once, see SessionConfigSnapshot.FreezeEffective). Null if the configuration is Invalid.</summary>
    public EffectiveSettings FreezeEffective(SceneSettings scene)
    {
        SessionConfigSnapshot r = Resolved;
        bool first = r.IsValid && r.Effective == null;
        EffectiveSettings e = r.FreezeEffective(scene);
        if (first && e != null) Debug.Log(SessionConfigCore.FormatEffective(r));
        return e;
    }

    /// <summary>"inspector" or "external_json": where the values in use came from.</summary>
    public string ConfigSource => Resolved.Source;
    /// <summary>FNV-1a hash of the external JSON text that was applied ("" for the Inspector source or when none was applied).</summary>
    public string ConfigHash => Resolved.Hash;
    /// <summary>Non-empty when the configured external JSON could not be loaded/validated. There is NO fallback to Inspector values in that case.</summary>
    public string ConfigError { get { var r = Resolved; return r.Source == SessionConfigCore.SourceExternal && !r.IsValid ? r.Identity.message : ""; } }

    // The values the running session actually uses. Defaults while the configuration is invalid (nothing starts in that case).
    public string ActiveParticipantId => Resolved.ParticipantId;
    public ExperimentPart ActivePart => (ExperimentPart)Mathf.Clamp(Resolved.PartNumber, 1, 3);
    public int ActivePart2GroupIndex => Resolved.Part2GroupIndex;
    public int ActivePart3GroupIndex => Resolved.Part3GroupIndex;
    /// <summary>The requested auto-start flag of the resolved configuration (false for an invalid external file). It does NOT bypass validation: ExperimentManager.StartSelectedPart refuses to start unless the configuration is Valid.</summary>
    public bool ActiveAutoStart => Resolved.AutoStart;

    /// <summary>Validates the configuration actually in force: participant ID, source resolution, part and group indices. Cached: the answer never changes during the run.</summary>
    public SessionIdentity.Result ValidateIdentity()
    {
        EnsureResolved();
        identityStatus = _resolved.Identity.status.ToString(); identityMessage = _resolved.Identity.message ?? "";
        return _resolved.Identity;
    }

    private void Awake()
    {
        EnsureResolved();
        if (!_resolved.IsValid)
            Debug.LogError("[ExternalSessionController] Session configuration rejected (" + _resolved.Identity.status + "): " + _resolved.Identity.message + " Research recording and auto-start stay blocked until this is fixed.");
    }

    /// <summary>Resolve exactly once. Called from Awake and lazily by any consumer (so script execution order does not matter).</summary>
    private void EnsureResolved()
    {
        if (_resolved != null) return;

        SessionConfigCore.Limits limits = BuildLimits();
        if (useExternalJson)
        {
            // Strict: when the external file is the configured source it must exist, parse and validate. A failure is a hard configuration error (no
            // silent fall-back to Inspector values, and the unrelated Assets/Scripts/session_config.json is never read).
            Debug.Log("[ExternalSessionController] External configuration file: " + ExternalFilePath + " (exists: " + File.Exists(ExternalFilePath) + ")");
            _resolved = SessionConfigCore.ResolveExternalFromFile(ExternalFilePath, limits, AgentProfileSnapshot.Fnv1a64Hex);
            if (_resolved.IsValid)
            {
                // Mirror into the Inspector fields for display only (runtime, not saved); consumers use the snapshot.
                participantId = _resolved.ParticipantId;
                selectedPart = (ExperimentPart)_resolved.PartNumber;
                part2GroupIndex = _resolved.Part2GroupIndex;
                part3GroupIndex = _resolved.Part3GroupIndex;
                autoStart = _resolved.AutoStart;
            }
        }
        else
        {
            _resolved = SessionConfigCore.ResolveInternal(participantId, (int)selectedPart, part2GroupIndex, part3GroupIndex, autoStart, limits);
        }

        identityStatus = _resolved.Identity.status.ToString(); identityMessage = _resolved.Identity.message ?? "";
        Debug.Log(SessionConfigCore.FormatSummary(_resolved, SummaryExtraLines(_resolved)));
    }

    // What the scene offers (group counts, scene-authored Part 3 maxima). Missing ExperimentManager => counts unknown (0): only the lower bound is checked.
    private static SessionConfigCore.Limits BuildLimits()
    {
        var limits = new SessionConfigCore.Limits { Part3GroupCount = ExperimentManager.Part3GroupCount };
        ExperimentManager em = FindObjectOfType<ExperimentManager>(true);
        if (em != null)
        {
            limits.Part2GroupCount = em.part2Groups != null ? em.part2Groups.Length : 0;
            limits.BaselinePart3Destination1Max = em.part3Destination1MaxSeconds;
            limits.BaselinePart3TotalMax = em.part3TotalMaxSeconds;
        }
        return limits;
    }

    private static string[] SummaryExtraLines(SessionConfigSnapshot s)
    {
        if (s.IsValid && s.PartNumber == 3 && ExperimentManager.TryGetPart3GroupZones(s.Part3GroupIndex, out string st, out string d1, out string d2))
            return new[] { "Start Zone: " + st, "D1 Zone: " + d1, "D2 Zone: " + d2 };
        return null;
    }

    /// <summary>
    /// Kept for source compatibility. The configuration is resolved once automatically; this returns whether the EXTERNAL source is in use and valid and
    /// never re-reads the file (a changed file requires a restart).
    /// </summary>
    public bool TryLoadFromExternalJson()
    {
        EnsureResolved();
        return useExternalJson && _resolved.IsValid;
    }

    /// <summary>
    /// Write the current Inspector values as a template to the external JSON path. Explicit experimenter action only; NEVER overwrites an existing file
    /// (returns false and logs instead) unless overwrite is true. Returns true if saved.
    /// </summary>
    public bool SaveToExternalJson(bool overwrite = false)
    {
        try
        {
            if (File.Exists(ExternalFilePath) && !overwrite)
            {
                Debug.LogWarning("[ExternalSessionController] Not saving: " + ExternalFilePath + " already exists (pass overwrite=true to replace it).");
                return false;
            }
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
