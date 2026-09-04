using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Responsible for applying AgentProfile settings (appearance, movement, behavior) to a pooled/instantiated
/// NPC GameObject. This script calls AppearanceController.ApplyProfile and applies movement/behavior/navmesh
/// settings. Supports AgentProfile.speedOptions (multipliers) so final per-agent speed = profile.speed *
/// chosenMultiplier.
/// </summary>
public class AgentFactory : MonoBehaviour
{
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
            // Re-baseline UncertainBehavior's "normal" speed to THIS spawn's applied speed.
            // UncertainBehavior.Start() only runs once per pooled GameObject's lifetime, so on every pool
            // reuse this call is the only thing that keeps the Slow/Detour "restore speed" baseline in
            // sync with the CURRENT AgentProfile - without it, a reused NPC could restore a stale speed
            // left over from a previous lifetime's profile after a Slow state ends.
            uncertain.ResetBaselineSpeed(appliedSpeed);

            // configure UncertainBehavior: either force, probabilistic, or off
            uncertain.enabled = profile.forceUncertain || Random.value < profile.uncertainProbability;
        }
    }
}