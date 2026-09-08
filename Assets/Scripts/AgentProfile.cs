using UnityEngine;

/// <summary>
/// The gender of a spawned NPC. Rolled once per NPC (per spawn / pool reuse) from
/// AgentProfile.femaleProbability and stored on AppearanceController.CurrentGender.
/// Gender-restricted appearance options (hair / top / bottom) are then all selected
/// within this one gender, so an NPC can never wear a cross-gender combination.
/// </summary>
public enum AgentGender
{
    Male,
    Female
}

/// <summary>
/// Per-PartOption gender applicability. 'Unisex' (default) means the option is valid
/// for both genders and is selected exactly as before. 'MaleOnly' / 'FemaleOnly'
/// restrict the option to that gender's pool.
/// </summary>
public enum GenderRestriction
{
    Unisex,
    MaleOnly,
    FemaleOnly
}

/// <summary>
/// Experimenter-facing profile that describes appearance and behavior settings for an agent.
/// Appearance contains per-part options (mesh + candidate materials + weight).
/// This version also supports configurable speed options (multipliers with weights),
/// so you can control both absolute magnitude (speed) and proportional variation across agents.
///
/// Gender-consistent appearance:
///   - Each spawned NPC is assigned a single AgentGender, rolled once from
///     'femaleProbability' (see ChooseGender). AppearanceController does this once per
///     ApplyProfile call and stores the result on AppearanceController.CurrentGender.
///   - Hair / Top / Bottom PartOptions carry a GenderRestriction. AppearanceController
///     passes the NPC's gender into ChooseWeightedOption(options, gender), which only
///     considers options whose restriction is Unisex or matches that gender.
///   - Shoes / Accessories / Body options stay Unisex and are selected exactly as before.
///   - The weighted-random selection logic (ChooseWeightedOption) is unchanged; the
///     gender overload merely skips non-matching options when summing weights and picking.
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
    /// One selectable option for a body part: mesh + candidate materials + weight (+ gender applicability).
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

        [Tooltip("Which NPC gender this option may be used by. 'Unisex' (default) = both, " +
                 "and behaviour is identical to before this field existed. Set 'MaleOnly' / " +
                 "'FemaleOnly' to make Hair / Top / Bottom gender-specific.")]
        public GenderRestriction genderRestriction = GenderRestriction.Unisex;

        /// <summary>True if this option may be worn by an NPC of the given gender.</summary>
        public bool AvailableTo(AgentGender gender)
        {
            switch (genderRestriction)
            {
                case GenderRestriction.MaleOnly:   return gender == AgentGender.Male;
                case GenderRestriction.FemaleOnly: return gender == AgentGender.Female;
                default:                           return true; // Unisex
            }
        }
    }

    [Header("Appearance - per part options")]
    public PartOption[] bodyOptions;
    public PartOption[] hairOptions;
    public PartOption[] topOptions;          // upper clothes
    public PartOption[] bottomOptions;       // lower clothes
    public PartOption[] shoesOptions;
    public PartOption[] accessoriesOptions;

    [Header("Appearance - Gender")]
    [Range(0f, 1f)]
    [Tooltip("Probability that a spawned NPC is Female. Rolled once per NPC (spawn / pool reuse). " +
             "0 = always Male, 0.5 = even split, 1 = always Female.")]
    public float femaleProbability = 0.5f;

    [Header("Collision / NavMesh")]
    public float agentRadius = 0.3f;
    public float agentHeight = 1.8f;

    /// <summary>
    /// Roll a gender for one NPC. Called once per NPC by AppearanceController.ApplyProfile.
    /// Uses the same (unseeded) UnityEngine.Random source as the rest of the appearance system.
    /// </summary>
    public AgentGender ChooseGender()
    {
        return (Random.value < femaleProbability) ? AgentGender.Female : AgentGender.Male;
    }

    /// <summary>
    /// Choose one PartOption from an array using weights. Returns null if none available.
    /// Unchanged public behaviour: no gender filtering (equivalent to every option being Unisex).
    /// </summary>
    public PartOption ChooseWeightedOption(PartOption[] options)
    {
        return ChooseWeightedOptionInternal(options, null);
    }

    /// <summary>
    /// Choose one PartOption using weights, considering ONLY options that are valid for the
    /// given gender (Unisex options always qualify). Used for Hair / Top / Bottom so a
    /// male NPC can never receive a female option and vice-versa. Returns null if the array
    /// is empty OR if no option matches the requested gender.
    /// </summary>
    public PartOption ChooseWeightedOption(PartOption[] options, AgentGender gender)
    {
        return ChooseWeightedOptionInternal(options, gender);
    }

    /// <summary>
    /// Shared weighted-random implementation. When genderFilter is null, every non-null option
    /// is eligible (original behaviour). When set, only options with AvailableTo(gender) == true
    /// contribute to the weight sum and can be picked.
    /// </summary>
    private PartOption ChooseWeightedOptionInternal(PartOption[] options, AgentGender? genderFilter)
    {
        if (options == null || options.Length == 0) return null;

        float total = 0f;
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (o == null) continue;
            if (genderFilter.HasValue && !o.AvailableTo(genderFilter.Value)) continue;
            total += Mathf.Max(0f, o.weight);
        }

        if (total <= 0f) return null;

        float r = Random.value * total;
        float acc = 0f;
        for (int i = 0; i < options.Length; i++)
        {
            var o = options[i];
            if (o == null) continue;
            if (genderFilter.HasValue && !o.AvailableTo(genderFilter.Value)) continue;
            acc += Mathf.Max(0f, o.weight);
            if (r <= acc) return o;
        }

        // fallback: return last eligible non-null option
        for (int i = options.Length - 1; i >= 0; i--)
        {
            var o = options[i];
            if (o == null) continue;
            if (genderFilter.HasValue && !o.AvailableTo(genderFilter.Value)) continue;
            return o;
        }

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