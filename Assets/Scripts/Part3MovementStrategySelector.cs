// Part3MovementStrategySelector.cs
// Decides, for ONE NPC that is about to be spawned in Part 3, which of the two EXISTING movement strategies it uses for its whole lifetime:
//
//   NpcMovementMode.Part3AttractorItinerary   Entrance -> attractors -> Exit    (attractor dwell + capacity; the Part 3 behaviour)
//   NpcMovementMode.Part2WanderingItinerary   Entrance -> wandering points -> Exit (look-ahead, no dwell; the Part 2 behaviour)
//
// RULE (independent Bernoulli trial per spawn, KEYED BY THE SPAWN INDEX):  with a = the configured attractor probability (SpawnerManager.part3AttractorMovementProbability,
// 0..1), the NPC with spawn index i takes the FIRST value r in [0, 1) of ITS OWN MovementAssignment stream (derived from Master Seed + Part + stream + i, see
// ExperimentRng.cs) and uses the attractor strategy when r < a, the wandering strategy otherwise. So the assignment of spawn i is a pure function of
// (Master Seed, Part, i, a): it does not depend on how many other NPCs were assigned, on spawn timing, or on any other random draw (route, appearance, hesitation),
// and the value r does not depend on a (the attractor sets are nested across probabilities). Nothing else is consulted: not the live population, not the history,
// not other NPCs' strategies. No quota, no deficit correction, no reassignment. a = 1 always gives attractors and a = 0 always wanderers (r &lt; 1 and r &lt; 0 are exact).
//
// RANDOM-NUMBER ISOLATION: the value comes from the NPC's MovementAssignment stream (PCG32); UnityEngine.Random and every other stream are untouched. Part 1, Part 2,
// "no running Part" and Part 3 with npcDirectedMovement = false never call Choose (SpawnerManager.DecideMovementMode), so they take no draw at all.
// The Master Seed is the only root of this stream (derived like every other stream: Master Seed + Part + stream id + spawn index); there is no override.
//
// The class holds no UnityEngine objects, so it can be tested without a scene. All comments in English per project convention.

public sealed class Part3MovementStrategySelector
{
    /// <summary>Default probability: every Part 3 NPC uses the attractor strategy (the behaviour before the mixture existed).</summary>
    public const float DefaultAttractorProbability = 1f;

    /// <summary>Number of decisions taken so far (one stream value each); observation only, never read by Choose.</summary>
    public long Draws { get; private set; }

    /// <summary>Cumulative assignments reported with <see cref="NoteAssigned"/> since the last <see cref="ResetCounts"/> (observation only; never read by Choose).</summary>
    public long AssignedAttractor { get; private set; }
    public long AssignedWandering { get; private set; }
    public long AssignedTotal => AssignedAttractor + AssignedWandering;

    /// <summary>Clamps to [0, 1]; NaN (an unset/corrupt Inspector value) falls back to the default (all attractor), which is the pre-mixture behaviour.</summary>
    public static float SanitizeProbability(float p)
    {
        if (float.IsNaN(p)) return DefaultAttractorProbability;
        if (p < 0f) return 0f;
        if (p > 1f) return 1f;
        return p;
    }

    /// <summary>The pure decision for a given first stream value: attractor when r &lt; probability.</summary>
    public static NpcMovementMode FromValue(float r, float probability)
    {
        return r < SanitizeProbability(probability) ? NpcMovementMode.Part3AttractorItinerary : NpcMovementMode.Part2WanderingItinerary;
    }

    /// <summary>The strategy of an NPC with the given streams: one value of its MovementAssignment stream.</summary>
    public NpcMovementMode Choose(NpcRngSet npc, float probability)
    {
        float r = npc.MovementAssignment.NextFloat();
        Draws++;
        return FromValue(r, probability);
    }

    /// <summary>
    /// What the NPC with spawn index 'spawnIndex' receives, computed without any NPC object: the first value of the MovementAssignment stream derived from rootSeed
    /// (the effective Master Seed). Used by tests and offline tools to verify recorded movement modes.
    /// </summary>
    public static NpcMovementMode Predict(ulong rootSeed, RngPart part, long spawnIndex, float probability)
    {
        return FromValue(new Pcg32(rootSeed, part, RngStream.MovementAssignment, spawnIndex).NextFloat(), probability);
    }

    /// <summary>The decision was applied to a real NPC (cumulative count, for reports and tests only).</summary>
    public void NoteAssigned(NpcMovementMode mode)
    {
        if (mode == NpcMovementMode.Part3AttractorItinerary) AssignedAttractor++; else AssignedWandering++;
    }

    /// <summary>Forgets the cumulative counts (a new Part starts). There is no stream state to reset: every assignment is keyed by the spawn index.</summary>
    public void ResetCounts()
    {
        AssignedAttractor = 0;
        AssignedWandering = 0;
    }
}
