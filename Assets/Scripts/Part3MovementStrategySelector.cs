// Part3MovementStrategySelector.cs
// Decides, for ONE NPC that is about to be spawned in Part 3, which of the two EXISTING movement strategies it uses for its whole lifetime:
//
//   NpcMovementMode.Part3AttractorItinerary   Entrance -> attractors -> Exit    (attractor dwell + capacity; the Part 3 behaviour)
//   NpcMovementMode.Part2WanderingItinerary   Entrance -> wandering points -> Exit (look-ahead, no dwell; the Part 2 behaviour)
//
// RULE (independent Bernoulli trial per spawn):  with a = the configured attractor probability (SpawnerManager.part3AttractorMovementProbability, 0..1),
// every new NPC draws ONE value r in [0, 1) and uses the attractor strategy when r < a, the wandering strategy otherwise. Nothing else is consulted:
// not the live population, not the history, not the other NPCs' strategies. There is no quota, no deficit correction and no reassignment, so the realised
// proportion fluctuates around a like any random sample (a = 1 always gives attractors, a = 0 always wandering, because r < 1 and r < 0 are exact).
// One value is drawn for EVERY decision, whatever a is, so the position in the random stream does not depend on the configured probability.
//
// RANDOM-NUMBER ISOLATION: the draw comes from a PRIVATE System.Random owned by this object. It never touches UnityEngine.Random (route generation, wandering
// point / attractor choice, dwell sampling, appearance and the Agent Profile rolls keep their sequences), and Part 2 never calls Choose(), so Part 2 never
// consumes a draw. The seed is explicit (Inspector override) or TickCount-derived, and the effective seed is recorded in the session manifest.
//
// The class holds no UnityEngine objects, so it can be tested without a scene. All comments in English per project convention.

using System;

public sealed class Part3MovementStrategySelector
{
    /// <summary>Default probability: every Part 3 NPC uses the attractor strategy (the behaviour before the mixture existed).</summary>
    public const float DefaultAttractorProbability = 1f;

    /// <summary>The seed of the private stream (explicit or derived); recorded in the manifest.</summary>
    public int Seed { get; }

    /// <summary>Number of draws taken so far (one per Choose call).</summary>
    public long Draws { get; private set; }

    /// <summary>Cumulative assignments reported with <see cref="NoteAssigned"/> since the last <see cref="ResetCounts"/> (observation only; never read by Choose).</summary>
    public long AssignedAttractor { get; private set; }
    public long AssignedWandering { get; private set; }
    public long AssignedTotal => AssignedAttractor + AssignedWandering;

    readonly Random rng;

    public Part3MovementStrategySelector(int seed)
    {
        Seed = seed;
        rng = new Random(seed);
    }

    /// <summary>Clamps to [0, 1]; NaN (an unset/corrupt Inspector value) falls back to the default (all attractor), which is the pre-mixture behaviour.</summary>
    public static float SanitizeProbability(float p)
    {
        if (float.IsNaN(p)) return DefaultAttractorProbability;
        if (p < 0f) return 0f;
        if (p > 1f) return 1f;
        return p;
    }

    /// <summary>An explicit non-zero seed is used as is; 0 (the Inspector default) derives a seed from the clock and a salt so two sessions differ.</summary>
    public static int ResolveSeed(int configuredSeed, int salt)
    {
        return configuredSeed != 0 ? configuredSeed : unchecked(Environment.TickCount * 397) ^ salt;
    }

    /// <summary>The strategy of the next NPC: one independent draw r in [0, 1); attractor when r &lt; probability.</summary>
    public NpcMovementMode Choose(float probability)
    {
        double a = SanitizeProbability(probability);
        double r = rng.NextDouble();
        Draws++;
        return r < a ? NpcMovementMode.Part3AttractorItinerary : NpcMovementMode.Part2WanderingItinerary;
    }

    /// <summary>The decision was applied to a real NPC (cumulative count, for reports and tests only).</summary>
    public void NoteAssigned(NpcMovementMode mode)
    {
        if (mode == NpcMovementMode.Part3AttractorItinerary) AssignedAttractor++; else AssignedWandering++;
    }

    /// <summary>Forgets the cumulative counts (a new Part starts). The random stream is NOT reset: draws continue.</summary>
    public void ResetCounts()
    {
        AssignedAttractor = 0;
        AssignedWandering = 0;
    }
}
