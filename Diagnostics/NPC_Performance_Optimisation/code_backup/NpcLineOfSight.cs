// NpcLineOfSight.cs
// NPC -> participant line-of-sight query for npc_state.csv (F-10). Observation only: it never changes an NPC, a collider or any simulation state.
//
// Meaning of the result
//   Clear    (los_clear_to_participant = 1): the segment from the NPC's eye point to the participant's head target meets no genuine obstacle.
//   Blocked  (= 0): at least one real obstacle collider lies between them.
//   anything else (blank): the question could not be answered reliably - never reported as blocked.
//
// What counts as an obstacle: every non-trigger collider in the LOS layer mask (Default | Ground) that is NOT
//   * part of the querying NPC (its whole transform hierarchy - the old query ran from the participant to a point INSIDE the NPC's own capsule and therefore
//     almost always hit the NPC's own colliders), or
//   * part of the participant (the rig hierarchy is the intended endpoint, not an obstacle).
// Other NPCs, buildings, terrain and props are obstacles (another NPC in the way does occlude). Trigger colliders are ignored. Hits are NOT assumed sorted:
// every hit within the segment is examined. The query uses a reusable RaycastHit buffer (no allocation); if the buffer fills up it is retried once with a
// larger buffer and, if still full, the result is indeterminate (HitBufferOverflow) rather than a guess.
//
// Geometry: origin = the NPC's head bone (humanoid Animator) when available, else its eye height (90 % of the NavMeshAgent height, or of its collider bounds)
// above the root; target = the participant's CenterEye (HMD) position. All comments in English per project convention.

using UnityEngine;
using UnityEngine.AI;

public enum LosStatus { Clear, Blocked, MissingParticipantTarget, MissingNpcOrigin, InvalidGeometry, HitBufferOverflow, QueryError }

public static class NpcLineOfSight
{
    public const int SmallBuffer = 32, LargeBuffer = 256;
    static readonly RaycastHit[] s_small = new RaycastHit[SmallBuffer];
    static readonly RaycastHit[] s_large = new RaycastHit[LargeBuffer];

    /// <summary>Stable text for npc_state.csv's los_status column.</summary>
    public static string Name(LosStatus s)
    {
        switch (s)
        {
            case LosStatus.Clear: return "clear";
            case LosStatus.Blocked: return "blocked";
            case LosStatus.MissingParticipantTarget: return "missing_participant_target";
            case LosStatus.MissingNpcOrigin: return "missing_npc_origin";
            case LosStatus.InvalidGeometry: return "invalid_geometry";
            case LosStatus.HitBufferOverflow: return "hit_buffer_overflow";
            default: return "query_error";
        }
    }

    /// <summary>The NPC's observation (eye) point: humanoid head bone, else 90 % of agent/collider height above the root. False if no transform exists.</summary>
    public static bool TryGetNpcEyePoint(Transform npcRoot, NavMeshAgent agent, Animator animator, out Vector3 eye)
    {
        eye = default;
        if (npcRoot == null) return false;
        if (animator != null && animator.isHuman)
        {
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) { eye = head.position; return true; }
        }
        float h = 0f;
        if (agent != null && agent.height > 0.1f) h = agent.height;
        else
        {
            var cols = npcRoot.GetComponentsInChildren<Collider>(false);
            float top = float.NegativeInfinity;
            for (int i = 0; i < cols.Length; i++) if (!cols[i].isTrigger) top = Mathf.Max(top, cols[i].bounds.max.y);
            if (!float.IsNegativeInfinity(top)) h = Mathf.Max(0f, top - npcRoot.position.y);
        }
        if (h <= 0.1f) return false;
        eye = npcRoot.position + Vector3.up * (0.9f * h);
        return true;
    }

    /// <param name="npcEye">Ray origin (use TryGetNpcEyePoint); null = unavailable.</param>
    /// <param name="participantTarget">The participant's head position target; null transform = unavailable.</param>
    public static LosStatus Query(Vector3? npcEye, Transform npcRoot, Transform participantTarget, Transform participantRoot, int layerMask)
    {
        try
        {
            if (participantTarget == null) return LosStatus.MissingParticipantTarget;
            if (npcEye == null || npcRoot == null) return LosStatus.MissingNpcOrigin;

            Vector3 origin = npcEye.Value, target = participantTarget.position;
            Vector3 delta = target - origin;
            float dist = delta.magnitude;
            if (float.IsNaN(dist) || float.IsInfinity(dist) || dist < 1e-3f) return LosStatus.InvalidGeometry;
            Vector3 dir = delta / dist;

            int n = Physics.RaycastNonAlloc(origin, dir, s_small, dist, layerMask, QueryTriggerInteraction.Ignore);
            RaycastHit[] buf = s_small; int cap = SmallBuffer;
            if (n >= SmallBuffer)
            {
                n = Physics.RaycastNonAlloc(origin, dir, s_large, dist, layerMask, QueryTriggerInteraction.Ignore);
                buf = s_large; cap = LargeBuffer;
            }
            for (int i = 0; i < n; i++)
            {
                Transform ct = buf[i].collider != null ? buf[i].collider.transform : null;
                if (ct == null) continue;
                if (ct.IsChildOf(npcRoot)) continue;                                   // the querying NPC's own colliders (any depth)
                if (participantRoot != null && ct.IsChildOf(participantRoot)) continue; // the participant is the endpoint, not an obstacle
                if (ct == participantTarget || ct.IsChildOf(participantTarget)) continue;
                return LosStatus.Blocked;   // one genuine obstacle among the returned hits is conclusive even if the buffer was full
            }
            // No obstacle among the hits we got. If the buffer was full the list may be incomplete: do not claim "clear".
            return n >= cap ? LosStatus.HitBufferOverflow : LosStatus.Clear;
        }
        catch (System.Exception) { return LosStatus.QueryError; }
    }
}
