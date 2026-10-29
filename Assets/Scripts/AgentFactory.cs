using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Responsible for applying AgentProfile settings (appearance, movement, behavior) to a pooled/instantiated
/// NPC GameObject. This script calls AppearanceController.ApplyProfile and applies movement/behavior/navmesh
/// settings. Supports AgentProfile.speedOptions (multipliers) so final per-agent speed = profile.speed *
/// chosenMultiplier.
/// Stage 4 (Option A movement-architecture migration): the applied speed is now handed to
/// MoveToTarget.SetNormalSpeed() instead of being written to NavMeshAgent.speed directly - MoveToTarget is
/// the single component allowed to control the NavMeshAgent.
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
        float speedMultiplier = 1f;   // recorded for data capture only
        if (profile.speedOptions != null && profile.speedOptions.Length > 0)
        {
            float mult = profile.ChooseWeightedSpeedMultiplier();
            speedMultiplier = mult;
            appliedSpeed *= mult;
        }

        if (nav != null)
        {
            // Shape/avoidance configuration - unrelated to movement authority, stays a direct NavMeshAgent write.
            nav.radius = profile.agentRadius;
            nav.height = profile.agentHeight;
            nav.acceleration = profile.acceleration;
            nav.angularSpeed = profile.angularSpeed;
            nav.avoidancePriority = Mathf.Clamp(profile.avoidancePriority, 0, 99);
        }

        // Stage 4: the normal-walking speed baseline is now owned exclusively by MoveToTarget. AgentFactory
        // no longer writes NavMeshAgent.speed directly - it hands the applied profile speed to
        // MoveToTarget.SetNormalSpeed(), which both stores it as the baseline (for
        // UncertainBehavior.RestoreNormalSpeed()/EnterSlow() to read/restore) and writes it to the live
        // NavMeshAgent immediately.
        if (move != null)
        {
            move.SetNormalSpeed(appliedSpeed);
        }
        else if (nav != null)
        {
            // Defensive fallback for a prefab with a NavMeshAgent but no MoveToTarget (should not occur on
            // the real NPC prefab, which always carries both).
            nav.speed = appliedSpeed;
        }

        if (uncertain != null)
        {
            // configure UncertainBehavior: either force, probabilistic, or off
            uncertain.enabled = profile.forceUncertain || Random.value < profile.uncertainProbability;
        }

        // Data capture (observation only): remember what was actually applied to this NPC so the recorder can write it once per lifetime.
        PedestrianAgent pedestrian = go.GetComponent<PedestrianAgent>();
        if (pedestrian != null)
        {
            pedestrian.SetProfileAttributes(profile.name, profile.speed, speedMultiplier, appliedSpeed, uncertain != null && uncertain.enabled);
            pedestrian.SetAppliedProfile(profile, app != null ? app.LastApplied : null);
        }
    }
}