using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Responsible for instantiating / reusing agents and applying AgentProfile settings.
/// This keeps SpawnerManager simpler.
/// </summary>
public class AgentFactory : MonoBehaviour
{
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

        if (nav != null && profile != null)
        {
            // set NavMeshAgent params for avoidance and shape
            nav.radius = profile.agentRadius;
            nav.height = profile.agentHeight;
            nav.speed = profile.speed;
            nav.acceleration = profile.acceleration;
            nav.angularSpeed = profile.angularSpeed;
            nav.avoidancePriority = Mathf.Clamp(profile.avoidancePriority, 0, 99);
        }
        else if (move != null && profile != null)
        {
            // if MoveToTarget uses its own speed, set it (MoveToTarget API should expose speed)
            move.SetMoveSpeed(profile.speed);
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

        if (nav != null)
        {
            nav.radius = profile.agentRadius;
            nav.height = profile.agentHeight;
            nav.speed = profile.speed;
            nav.acceleration = profile.acceleration;
            nav.angularSpeed = profile.angularSpeed;
            nav.avoidancePriority = Mathf.Clamp(profile.avoidancePriority, 0, 99);
        }
        else if (move != null)
        {
            move.SetMoveSpeed(profile.speed);
        }

        if (uncertain != null)
        {
            uncertain.enabled = profile.forceUncertain || Random.value < profile.uncertainProbability;
        }
    }
}
