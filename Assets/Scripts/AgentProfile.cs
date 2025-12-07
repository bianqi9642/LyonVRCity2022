using UnityEngine;

/// <summary>
/// Experimenter-facing profile that describes appearance and behavior settings for an agent.
/// </summary>
[CreateAssetMenu(menuName = "VRExp/AgentProfile")]
public class AgentProfile : ScriptableObject
{
    [Header("Movement")]
    public float speed = 1.4f;                 // base navigation speed
    public float acceleration = 8f;
    public float angularSpeed = 120f;
    [Range(0,100)]
    public int avoidancePriority = 50;         // NavMeshAgent.avoidancePriority

    [Header("Behavior")]
    public bool forceUncertain = false;        // force UncertainBehavior on
    [Range(0f,1f)]
    public float uncertainProbability = 0.2f; // probability to turn on uncertain if not forced

    [Header("Appearance")]
    public GameObject[] bodyVariants;          // optional: different body/clothing subprefabs you can enable
    public Material[] clothingMaterials;       // optional materials to swap
    public Color[] clothingColors;

    [Header("Collision / NavMesh")]
    public float agentRadius = 0.3f;
    public float agentHeight = 1.8f;
}
