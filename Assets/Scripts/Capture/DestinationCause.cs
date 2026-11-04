// DestinationCause.cs
// Why an observed NavMesh destination of an NPC moved (npc_dest_change value_str "cause=..."). ONE classifier for every movement mode: it never looks at which Part is running. Pure C# +
// UnityEngine.Vector3, so it is unit-testable without a scene.
//
// Taxonomy (the cause says WHY the destination changed, not what kind of destination it is - the destination's identity is the row's own target_id / dest_* columns):
//   initial_assignment : the lifetime's first valid destination (written elsewhere; never reaches this classifier).
//   detour             : the NPC is IN an UncertainBehavior detour right now (it diverted from its main destination).
//   detour_return      : the NPC COMPLETED a detour and was sent back to the main destination it was following BEFORE that detour. Wandering point (Part 2) or itinerary / attractor stop point
//                        (Part 3) alike. Requires ALL of:
//                          (1) a detour really started in this lifetime and a valid main destination existed when it started (UncertainBehavior, DetoursStarted > 0),
//                          (2) it completed, and that completion has not been consumed by an earlier observed change (DetoursCompleted > DetoursCompletedConsumed),
//                          (3) UncertainBehavior verified that what it restored IS the pre-detour destination (RestoredPreDetourTarget: same saved position, or - when the owner re-issued its own
//                              target, Part 2 - the same Transform; false when the owner picked a different target or issued nothing),
//                          (4) the observed destination is that destination: by Transform identity when the restore preserved one (ResumeTransformIdentity), otherwise horizontally within the
//                              NavMesh-snap tolerance of the recorded resume position (NavMeshAgent.destination is snapped onto the NavMesh).
//   itinerary_target   : a normal new / switched itinerary or wandering target: the observed destination is (within the same snap tolerance) the NPC's current itinerary entry and no verified return explains it.
//   other              : a genuine change whose reason cannot be identified. Never relabelled as an initial assignment or a return.
// ORDER (strongest evidence first): detour -> detour_return -> itinerary_target -> other. A verified return is decided BEFORE the generic geometric itinerary match, so a restored wandering point is
// never consumed by itinerary_target. A completion explains the FIRST observed change after it: a later, unrelated change is never labelled detour_return, whatever it resembles.
// TOLERANCE: DetourReturnToleranceMeters = 1.5 m horizontal, the same snap rule itinerary_target uses (no other threshold exists). It is only a fallback for a restore that has no Transform identity.
// All comments in English per project convention.

using UnityEngine;

public static class DestinationCause
{
    public const float DetourReturnToleranceMeters = 1.5f;

    /// <summary>Does the mover currently follow the Transform UncertainBehavior recorded after an identity-preserving restore?</summary>
    public enum ResumeIdentity { Unknown = 0, Match = 1, Mismatch = 2 }

    public struct Inputs
    {
        public bool DetourActiveNow;            // UncertainBehavior active and in the "detour" state
        public bool ItineraryTargetMatches;     // observed destination within tolerance of the current itinerary entry
        public int DetoursStarted;              // this lifetime
        public int DetoursCompleted;            // this lifetime
        public int DetoursCompletedConsumed;    // completions already accounted for by earlier observed destination changes of this lifetime
        public bool RestoredPreDetourTarget;    // UncertainBehavior verified the restore went back to the pre-detour destination
        public ResumeIdentity ResumeTransformIdentity;   // Unknown = the restore was by position (no Transform to compare)
        public Vector3 ResumeTarget;            // destination recorded by UncertainBehavior when the last detour ended
        public Vector3 ObservedDestination;     // NavMeshAgent.destination at the sampling tick
    }

    /// <summary>Returns the cause; <paramref name="consumedCompletions"/> is the completion count to store back (pending completions are consumed by the first observed change).</summary>
    public static string Classify(Inputs i, out int consumedCompletions)
    {
        bool pending = i.DetoursStarted > 0 && i.DetoursCompleted > i.DetoursCompletedConsumed;
        consumedCompletions = pending ? i.DetoursCompleted : i.DetoursCompletedConsumed;
        if (i.DetourActiveNow) return "detour";
        if (pending && i.RestoredPreDetourTarget && ResumeMatches(i)) return CaptureConstants.DestCauseDetourReturn;
        if (i.ItineraryTargetMatches) return "itinerary_target";
        return CaptureConstants.DestCauseOther;
    }

    static bool ResumeMatches(Inputs i)
    {
        if (i.ResumeTransformIdentity == ResumeIdentity.Match) return true;
        if (i.ResumeTransformIdentity == ResumeIdentity.Mismatch) return false;   // the mover follows something else now: stronger than any geometry
        return HorizontalDistance(i.ObservedDestination, i.ResumeTarget) <= DetourReturnToleranceMeters;
    }

    static float HorizontalDistance(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return (a - b).magnitude; }
}
