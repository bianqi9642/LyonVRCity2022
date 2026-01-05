using UnityEngine;

/// <summary>
/// Experimenter-facing profile that describes appearance and behavior settings for an agent.
/// Appearance contains per-part options (mesh + candidate materials + weight).
/// This version also supports configurable speed options (multipliers with weights),
/// so you can control both absolute magnitude (speed) and proportional variation across agents.
/// </summary>
[CreateAssetMenu(menuName = "VRExp/AgentProfile")]
public class AgentProfile : ScriptableObject
{
    [Header("Movement")]
    // Base navigation speed (used as the baseline). Final applied speed may be base * chosenMultiplier.
    public float speed = 1.4f;                 // base navigation speed

    // Optional per-agent speed multiplier options with weights (relative probabilities).
    // If empty or null, no multiplier is applied (equivalent to multiplier = 1.0).
    [System.Serializable]
    public class SpeedOption
    {
        [Tooltip("Multiplier applied to the profile.base speed. e.g. 0.8 reduces speed, 1.2 increases.")]
        public float multiplier = 1f;

        [Tooltip("Relative weight (probability) for selecting this speed option.")]
        public float weight = 1f;
    }

    [Header("Movement - Speed Options (optional)")]
    public SpeedOption[] speedOptions;

    public float acceleration = 8f;
    public float angularSpeed = 120f;
    [Range(0,100)]
    public int avoidancePriority = 50;         // NavMeshAgent.avoidancePriority

    [Header("Behavior")]
    public bool forceUncertain = false;        // force UncertainBehavior on
    [Range(0f,1f)]
    public float uncertainProbability = 0.2f; // probability to turn on uncertain if not forced

    // ---------------------------------------
    // Appearance (per-part options)
    // ---------------------------------------

    /// <summary>
    /// One selectable option for a body part: mesh + candidate materials + weight.
    /// </summary>
    [System.Serializable]
    public class PartOption
    {
        // Mesh to assign to the SkinnedMeshRenderer. Can be null to leave mesh unchanged.
        public Mesh mesh;

        // Candidate materials for random selection. If empty or null, materials are not changed.
        public Material[] materials;

        // Relative selection weight for weighted random selection among options.
        public float weight = 1f;
    }

    [Header("Appearance - per part options")]
    public PartOption[] bodyOptions;
    public PartOption[] hairOptions;
    public PartOption[] topOptions;          // upper clothes
    public PartOption[] bottomOptions;       // lower clothes
    public PartOption[] shoesOptions;
    public PartOption[] accessoriesOptions;

    [Header("Collision / NavMesh")]
    public float agentRadius = 0.3f;
    public float agentHeight = 1.8f;

    /// <summary>
    /// Choose one PartOption from an array using weights. Returns null if none available.
    /// </summary>
    public PartOption ChooseWeightedOption(PartOption[] options)
    {
        if (options == null || options.Length == 0) return null;

        float total = 0f;
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (o != null) total += Mathf.Max(0f, o.weight);
        }

        if (total <= 0f) return null;

        float r = Random.value * total;
        float acc = 0f;
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (o == null) continue;
            acc += Mathf.Max(0f, o.weight);
            if (r <= acc) return o;
        }

        // fallback: return last non-null option
        for (int i = options.Length - 1; i >= 0; i--)
            if (options[i] != null) return options[i];

        return null;
    }

    /// <summary>
    /// Choose a speed multiplier from speedOptions using weights.
    /// Returns 1.0 if no options available or total weight <= 0.
    /// </summary>
    public float ChooseWeightedSpeedMultiplier()
    {
        if (speedOptions == null || speedOptions.Length == 0) return 1f;

        float total = 0f;
        for (int i = 0; i < speedOptions.Length; i++)
        {
            var s = speedOptions[i];
            if (s != null) total += Mathf.Max(0f, s.weight);
        }

        if (total <= 0f) return 1f;

        float r = Random.value * total;
        float acc = 0f;
        for (int i = 0; i < speedOptions.Length; i++)
        {
            var s = speedOptions[i];
            if (s == null) continue;
            acc += Mathf.Max(0f, s.weight);
            if (r <= acc)
            {
                // ensure multiplier is a valid finite value
                return float.IsFinite(s.multiplier) ? s.multiplier : 1f;
            }
        }

        // fallback: last valid multiplier
        for (int i = speedOptions.Length - 1; i >= 0; i--)
            if (speedOptions[i] != null && float.IsFinite(speedOptions[i].multiplier)) return speedOptions[i].multiplier;

        return 1f;
    }
}
