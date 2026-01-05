using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Responsible for instantiating agents and applying AgentProfile settings.
/// This script calls AppearanceController.ApplyProfile and applies movement/behavior/navmesh settings.
/// Updated to support AgentProfile.speedOptions (multipliers) so final per-agent speed = profile.speed * chosenMultiplier.
/// </summary>
public class AgentFactory : MonoBehaviour
{
    [Tooltip("Default prefab that contains SkinnedMeshRenderers, AppearanceController, NavMeshAgent, UncertainBehavior, etc.")]
    public GameObject agentPrefab; // contains PedestrianAgent, MoveToTarget, UncertainBehavior, AppearanceController, maybe NavMeshAgent

    /// <summary>
    /// Create a new agent instance and apply profile. Returns the GameObject.
    /// </summary>
    public GameObject CreateAgent(AgentProfile profile, Vector3 spawnPosition)
    {
        GameObject go = Instantiate(agentPrefab, spawnPosition, Quaternion.identity);

        // apply appearance
        var app = go.GetComponent<AppearanceController>();
        if (app != null && profile != null) app.ApplyProfile(profile);

        // configure MoveToTarget / PedestrianAgent / NavMeshAgent
        var move = go.GetComponent<MoveToTarget>();
        var ped = go.GetComponent<PedestrianAgent>();
        var uncertain = go.GetComponent<UncertainBehavior>();
        var nav = go.GetComponent<NavMeshAgent>();

        // Compute final speed: base profile.speed possibly multiplied by a chosen multiplier from speedOptions
        float appliedSpeed = profile != null ? profile.speed : 0f;
        if (profile != null && profile.speedOptions != null && profile.speedOptions.Length > 0)
        {
            float mult = profile.ChooseWeightedSpeedMultiplier();
            appliedSpeed *= mult;
        }

        if (nav != null && profile != null)
        {
            // set NavMeshAgent params for avoidance and shape
            nav.radius = profile.agentRadius;
            nav.height = profile.agentHeight;
            nav.speed = appliedSpeed;
            nav.acceleration = profile.acceleration;
            nav.angularSpeed = profile.angularSpeed;
            nav.avoidancePriority = Mathf.Clamp(profile.avoidancePriority, 0, 99);
        }
        else if (move != null && profile != null)
        {
            // if MoveToTarget uses its own speed, set it (MoveToTarget API should expose speed)
            move.SetMoveSpeed(appliedSpeed);
        }

        // configure UncertainBehavior: either force, probabilistic, or off
        if (uncertain != null && profile != null)
        {
            if (profile.forceUncertain)
                uncertain.enabled = true;
            else
            {
                float r = Random.value;
                uncertain.enabled = r < profile.uncertainProbability;
            }
        }

        return go;
    }

    /// <summary>
    /// Apply profile to an already existing pooled agent (called when reusing from pool).
    /// </summary>
    public void ApplyProfileToInstance(GameObject go, AgentProfile profile)
    {
        if (go == null || profile == null) return;
        var app = go.GetComponent<AppearanceController>();
        if (app != null) app.ApplyProfile(profile);

        var nav = go.GetComponent<NavMeshAgent>();
        var move = go.GetComponent<MoveToTarget>();
        var uncertain = go.GetComponent<UncertainBehavior>();

        // Compute final speed: base profile.speed possibly multiplied by a chosen multiplier from speedOptions
        float appliedSpeed = profile.speed;
        if (profile.speedOptions != null && profile.speedOptions.Length > 0)
        {
            float mult = profile.ChooseWeightedSpeedMultiplier();
            appliedSpeed *= mult;
        }

        if (nav != null)
        {
            nav.radius = profile.agentRadius;
            nav.height = profile.agentHeight;
            nav.speed = appliedSpeed;
            nav.acceleration = profile.acceleration;
            nav.angularSpeed = profile.angularSpeed;
            nav.avoidancePriority = Mathf.Clamp(profile.avoidancePriority, 0, 99);
        }
        else if (move != null)
        {
            move.SetMoveSpeed(appliedSpeed);
        }

        if (uncertain != null)
        {
            uncertain.enabled = profile.forceUncertain || Random.value < profile.uncertainProbability;
        }
    }
}
